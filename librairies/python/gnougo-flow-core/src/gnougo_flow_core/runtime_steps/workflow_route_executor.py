from __future__ import annotations

import asyncio
import copy
import json
import re
import uuid
from dataclasses import dataclass
from typing import Any

from gnougo_flow_core.json_schema import inputs_to_json_schema
from gnougo_flow_core.models import (
    CompiledWorkflow,
    ExecutionLimits,
    LLMRequest,
    WorkflowRouteCandidate,
    WorkflowRouteCandidateQuery,
)
from gnougo_flow_core.runtime import *  # noqa: F401,F403
from gnougo_flow_core.runtime import apply_workflow_input_defaults, validate_input_types
from gnougo_flow_core.workflow_call_resolver import (
    DefaultWorkflowCallResolver,
    WorkflowCallResolution,
    WorkflowCallResolutionContext,
)


@dataclass(slots=True)
class _RouteCandidate:
    id: str
    name: str
    ref: dict[str, Any]
    description: str | None
    tags: list[str]
    inputs: Any = None
    outputs: Any = None
    reason: str | None = None
    confidence: float | None = None

    @classmethod
    def from_provider(cls, candidate: WorkflowRouteCandidate) -> "_RouteCandidate":
        candidate_id = candidate.id.strip() if candidate.id else ""
        if not candidate_id:
            candidate_id = f"{candidate.ref.get('kind', 'candidate')}:{candidate.name}"
        return cls(
            id=candidate_id,
            name=candidate.name,
            ref=copy.deepcopy(candidate.ref),
            description=candidate.description,
            tags=list(candidate.tags or []),
            inputs=copy.deepcopy(candidate.inputs),
            outputs=copy.deepcopy(candidate.outputs),
        )


@dataclass(slots=True)
class _RouteExecutionResult:
    candidate: _RouteCandidate
    workflow_name: str
    success: bool
    outputs: Any
    error: str | None
    steps_executed: int


@dataclass(slots=True)
class _AutoExtractConfig:
    enabled: bool
    provider: str | None = None
    model: str | None = None
    temperature: float | None = None


class WorkflowRouteExecutor:
    step_type = "workflow.route"
    step_description = "Route a prompt to one or more workflow candidates and execute the selected workflows."
    dsl_snippet = """
### workflow.route - Route to one or more workflow candidates
```yaml
- id: route
  type: workflow.route
  input:
    prompt: "${data.inputs.prompt}"
    candidates:
      - ref: { kind: database }
        tags_any: [git, documents]
        limit: 20
      - ref: { kind: local, name: fallback }
        description: General fallback.
    selection: { mode: multiple, min: 1, max: 3 }
    args:
      passthrough: true
      auto_extract: true
    execution: { parallel: true, max_concurrency: 3 }
    combine: { strategy: synthesize }
```
Output: `{ selected: [...], results: [...], answer?, text? }`.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "workflow.route input, candidates, selection, args, execution, or combine sections are malformed."),
        (ErrorCodes.TEMPLATE_PLAN, False, "The routing LLM is unavailable or did not return a valid selection."),
        (ErrorCodes.WORKFLOW_FETCH_NETWORK, False, "A dynamic candidate source or selected workflow could not be resolved."),
        (ErrorCodes.WORKFLOW_CYCLE_DETECTED, False, "A selected workflow call would exceed route call-depth limits or create a call cycle."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.route input must be object")

        prompt = str(input_obj.get("prompt") or input_obj.get("task") or input_obj.get("query") or "")
        candidates_input = input_obj.get("candidates")
        if not isinstance(candidates_input, list):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.route requires 'candidates' array")

        if ctx.call_depth >= ctx.limits.max_call_depth:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_CYCLE_DETECTED,
                f"Max call depth ({ctx.limits.max_call_depth}) exceeded",
            )

        candidates = await self._normalize_candidates_async(ctx, candidates_input)
        if not candidates:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.route found no candidates")

        selection_input = input_obj.get("selection") if isinstance(input_obj.get("selection"), dict) else {}
        mode = str(selection_input.get("mode", "multiple"))
        min_selected = max(0, int(selection_input.get("min", 1)))
        default_max = 1 if mode.lower() == "single" else len(candidates)
        max_selected = int(selection_input.get("max", default_max))
        if mode.lower() == "single":
            max_selected = 1
        max_selected = min(max(max_selected, 1), len(candidates))
        min_selected = min(max(min_selected, 0), max_selected)

        selected = await self._select_candidates_async(ctx, input_obj, prompt, candidates, min_selected, max_selected)
        if len(selected) < min_selected:
            raise WorkflowRuntimeException(
                ErrorCodes.TEMPLATE_PLAN,
                f"workflow.route selected {len(selected)} candidate(s), below required minimum {min_selected}",
            )

        args_input = input_obj.get("args") if isinstance(input_obj.get("args"), dict) else None
        args = self._build_workflow_args(ctx, args_input)
        execution_input = input_obj.get("execution") if isinstance(input_obj.get("execution"), dict) else {}
        execute_in_parallel = bool(execution_input.get("parallel", True))
        max_concurrency = min(max(int(execution_input.get("max_concurrency", len(selected))), 1), max(1, len(selected)))

        if execute_in_parallel:
            route_results = await self._execute_selected_parallel_async(ctx, input_obj, selected, args, args_input, max_concurrency)
        else:
            route_results = await self._execute_selected_sequential_async(ctx, input_obj, selected, args, args_input)

        output: dict[str, Any] = {
            "selected": self._build_selected_array(selected),
            "results": self._build_results_array(route_results),
        }

        combine = input_obj.get("combine") if isinstance(input_obj.get("combine"), dict) else {}
        strategy = str(combine.get("strategy", "first" if len(route_results) == 1 else "synthesize"))
        answer = await self._combine_async(ctx, input_obj, prompt, route_results, strategy)
        if answer is not None:
            output["answer"] = answer
            output["text"] = answer
        return output

    async def _normalize_candidates_async(self, ctx: StepExecutionContext, candidates_input: list[Any]) -> list[_RouteCandidate]:
        candidates: list[_RouteCandidate] = []
        for candidate_obj in candidates_input:
            if not isinstance(candidate_obj, dict):
                raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.route candidates must be objects")
            ref = candidate_obj.get("ref")
            if not isinstance(ref, dict):
                raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.route candidate requires 'ref'")

            kind = str(ref.get("kind", "local"))
            explicit_agent = ref.get("agent")
            explicit_name = ref.get("name")
            is_dynamic_database = (
                kind.lower() == "database"
                and not str(explicit_agent or "").strip()
                and not str(explicit_name or "").strip()
            )
            if is_dynamic_database:
                provider = ctx.engine.workflow_candidate_provider
                if provider is None:
                    raise WorkflowRuntimeException(
                        ErrorCodes.WORKFLOW_FETCH_NETWORK,
                        "workflow.route dynamic database candidates require a WorkflowCandidateProvider",
                    )
                dynamic_candidates = await provider.get_candidates_async(
                    WorkflowRouteCandidateQuery(
                        ref=copy.deepcopy(ref),
                        kind=kind,
                        tags_any=self._read_string_array(candidate_obj.get("tags_any")),
                        tags_all=self._read_string_array(candidate_obj.get("tags_all")),
                        exclude_tags=self._read_string_array(candidate_obj.get("exclude_tags")),
                        limit=candidate_obj.get("limit") if isinstance(candidate_obj.get("limit"), int) else None,
                    )
                )
                candidates.extend(_RouteCandidate.from_provider(candidate) for candidate in dynamic_candidates)
                continue

            name = str(explicit_agent or explicit_name or ref.get("url") or f"candidate-{len(candidates) + 1}")
            candidates.append(
                _RouteCandidate(
                    id=f"{kind}:{name}",
                    name=name,
                    ref=copy.deepcopy(ref),
                    description=candidate_obj.get("description"),
                    tags=self._read_string_array(candidate_obj.get("tags")),
                    inputs=copy.deepcopy(candidate_obj.get("inputs")),
                    outputs=copy.deepcopy(candidate_obj.get("outputs")),
                )
            )

        deduped: dict[str, _RouteCandidate] = {}
        for candidate in candidates:
            key = candidate.id.lower()
            if key not in deduped:
                deduped[key] = candidate
        return list(deduped.values())

    async def _select_candidates_async(
        self,
        ctx: StepExecutionContext,
        input_obj: dict[str, Any],
        prompt: str,
        candidates: list[_RouteCandidate],
        min_selected: int,
        max_selected: int,
    ) -> list[_RouteCandidate]:
        if len(candidates) == 1:
            return candidates
        if ctx.engine.llm_client is None:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "workflow.route requires an LLM client for multi-candidate selection")

        selection_input = input_obj.get("selection") if isinstance(input_obj.get("selection"), dict) else {}
        provider, model = ctx.engine.resolve_llm_target(selection_input.get("provider"), selection_input.get("model"))
        response = await ctx.engine.call_llm_async(
            LLMRequest(
                provider=provider,
                model=model or "",
                prompt=self._build_selection_prompt(prompt, input_obj.get("history"), candidates, min_selected, max_selected),
                temperature=selection_input.get("temperature", 0),
                structured_output_strict=False,
                structured_output_schema=self._build_selection_schema(),
            )
        )
        payload = response.json_payload or self._try_parse_json_object(response.text)
        if not isinstance(payload, dict):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "workflow.route selection did not return JSON")
        selected_ids = payload.get("selected")
        if not isinstance(selected_ids, list):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "workflow.route selection JSON requires 'selected' array")

        by_id = {candidate.id.lower(): candidate for candidate in candidates}
        by_name = {candidate.name.lower(): candidate for candidate in candidates}
        selected: list[_RouteCandidate] = []
        for selected_node in selected_ids:
            selected_id = None
            reason = None
            confidence = None
            if isinstance(selected_node, str):
                selected_id = selected_node
            elif isinstance(selected_node, dict):
                selected_id = selected_node.get("id") or selected_node.get("name") or selected_node.get("workflow")
                reason = selected_node.get("reason")
                confidence = selected_node.get("confidence")
            if not str(selected_id or "").strip():
                continue
            match = by_id.get(str(selected_id).lower()) or by_name.get(str(selected_id).lower())
            if match is None or any(item.id.lower() == match.id.lower() for item in selected):
                continue
            selected.append(
                _RouteCandidate(
                    id=match.id,
                    name=match.name,
                    ref=copy.deepcopy(match.ref),
                    description=match.description,
                    tags=list(match.tags),
                    inputs=copy.deepcopy(match.inputs),
                    outputs=copy.deepcopy(match.outputs),
                    reason=reason,
                    confidence=float(confidence) if isinstance(confidence, (int, float)) else None,
                )
            )
            if len(selected) >= max_selected:
                break

        if not selected and min_selected > 0:
            first = candidates[0]
            selected.append(
                _RouteCandidate(
                    id=first.id,
                    name=first.name,
                    ref=copy.deepcopy(first.ref),
                    description=first.description,
                    tags=list(first.tags),
                    inputs=copy.deepcopy(first.inputs),
                    outputs=copy.deepcopy(first.outputs),
                    reason="Fallback selection because the router returned no known candidate.",
                    confidence=0,
                )
            )
        return selected

    @staticmethod
    def _build_workflow_args(ctx: StepExecutionContext, args_input: dict[str, Any] | None) -> dict[str, Any]:
        passthrough = True if args_input is None else bool(args_input.get("passthrough", True))
        args = copy.deepcopy(ctx.data.get("inputs", {})) if passthrough else {}
        if not isinstance(args, dict):
            args = {}
        add = args_input.get("add") if isinstance(args_input, dict) else None
        if isinstance(add, dict):
            for key, value in add.items():
                if str(key).strip():
                    args[str(key)] = copy.deepcopy(value)
        return args

    async def _apply_auto_extract_args_async(
        self,
        ctx: StepExecutionContext,
        route_input: dict[str, Any],
        args_input: dict[str, Any] | None,
        candidate: _RouteCandidate,
        workflow: CompiledWorkflow,
        args: dict[str, Any],
    ) -> dict[str, Any]:
        config = self._parse_auto_extract_config(args_input)
        if not config.enabled:
            return args

        schema = None
        if workflow.source.inputs:
            schema = inputs_to_json_schema(workflow.source.inputs)
        elif candidate.inputs is not None:
            schema = copy.deepcopy(candidate.inputs)
        if schema is None:
            return args
        allowed_keys = self._extract_argument_keys(schema)
        mapped_args = self._filter_args_to_allowed_keys(args, allowed_keys)
        if ctx.engine.llm_client is None:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "workflow.route args.auto_extract requires an LLM client")

        provider, model = ctx.engine.resolve_llm_target(config.provider, config.model)
        response = await ctx.engine.call_llm_async(
            LLMRequest(
                provider=provider,
                model=model or "",
                temperature=0 if config.temperature is None else config.temperature,
                prompt=self._build_argument_extraction_prompt(route_input, candidate, workflow, mapped_args, schema),
                structured_output_strict=False,
                structured_output_schema=self._build_argument_extraction_schema(schema),
            )
        )
        payload = response.json_payload or self._try_parse_json_object(response.text)
        if not isinstance(payload, dict):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "workflow.route auto_extract did not return JSON")
        extracted = payload.get("arguments", payload)
        if not isinstance(extracted, dict):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "workflow.route auto_extract JSON must be an object")
        for key, value in extracted.items():
            key_text = str(key)
            if key_text.strip() and value is not None and key_text in allowed_keys:
                mapped_args[key_text] = copy.deepcopy(value)
        return mapped_args

    @staticmethod
    def _filter_args_to_allowed_keys(args: dict[str, Any], allowed_keys: set[str]) -> dict[str, Any]:
        filtered: dict[str, Any] = {}
        for key, value in args.items():
            key_text = str(key)
            if key_text.strip() and value is not None and key_text in allowed_keys:
                filtered[key_text] = copy.deepcopy(value)
        return filtered

    @staticmethod
    def _extract_argument_keys(schema: Any) -> set[str]:
        keys: set[str] = set()
        WorkflowRouteExecutor._add_argument_keys(schema, keys)
        return keys

    @staticmethod
    def _add_argument_keys(schema: Any, keys: set[str]) -> None:
        if not isinstance(schema, dict):
            return
        properties = schema.get("properties")
        if isinstance(properties, dict):
            for key in properties.keys():
                key_text = str(key)
                if key_text.strip():
                    keys.add(key_text)
        for union_key in ("allOf", "anyOf", "oneOf"):
            variants = schema.get(union_key)
            if not isinstance(variants, list):
                continue
            for variant in variants:
                WorkflowRouteExecutor._add_argument_keys(variant, keys)

    @staticmethod
    def _parse_auto_extract_config(args_input: dict[str, Any] | None) -> _AutoExtractConfig:
        node = args_input.get("auto_extract") if isinstance(args_input, dict) else None
        if node is None:
            return _AutoExtractConfig(False)
        if isinstance(node, bool):
            return _AutoExtractConfig(node)
        if isinstance(node, dict):
            return _AutoExtractConfig(
                bool(node.get("enabled", True)),
                node.get("provider"),
                node.get("model"),
                node.get("temperature") if isinstance(node.get("temperature"), (int, float)) else None,
            )
        raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.route args.auto_extract must be boolean or object")

    async def _execute_selected_sequential_async(
        self,
        ctx: StepExecutionContext,
        route_input: dict[str, Any],
        selected: list[_RouteCandidate],
        args: dict[str, Any],
        args_input: dict[str, Any] | None,
    ) -> list[_RouteExecutionResult]:
        results: list[_RouteExecutionResult] = []
        for candidate in selected:
            results.append(await self._execute_candidate_async(ctx, route_input, candidate, args, args_input))
        return results

    async def _execute_selected_parallel_async(
        self,
        ctx: StepExecutionContext,
        route_input: dict[str, Any],
        selected: list[_RouteCandidate],
        args: dict[str, Any],
        args_input: dict[str, Any] | None,
        max_concurrency: int,
    ) -> list[_RouteExecutionResult]:
        semaphore = asyncio.Semaphore(max_concurrency)

        async def run(candidate: _RouteCandidate) -> _RouteExecutionResult:
            async with semaphore:
                return await self._execute_candidate_async(ctx, route_input, candidate, args, args_input)

        return list(await asyncio.gather(*(run(candidate) for candidate in selected)))

    async def _execute_candidate_async(
        self,
        ctx: StepExecutionContext,
        route_input: dict[str, Any],
        candidate: _RouteCandidate,
        args: dict[str, Any],
        args_input: dict[str, Any] | None,
    ) -> _RouteExecutionResult:
        kind = str(candidate.ref.get("kind", "local"))
        resolver = ctx.engine.workflow_call_resolver or DefaultWorkflowCallResolver()
        resolution: WorkflowCallResolution = await resolver.resolve_async(
            WorkflowCallResolutionContext(
                engine=ctx.engine,
                ref=candidate.ref,
                kind=kind,
                call_depth=ctx.call_depth,
                call_stack=set(ctx.call_stack),
            )
        )
        if resolution.call_stack_key and resolution.call_stack_key in ctx.call_stack:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_CYCLE_DETECTED,
                f"Cycle detected: workflow '{resolution.workflow_name}' already in call stack",
            )

        child_engine = self._create_child_engine(ctx, candidate)
        candidate_args = copy.deepcopy(args)
        candidate_args = await self._apply_auto_extract_args_async(
            ctx,
            route_input,
            args_input,
            candidate,
            resolution.workflow,
            candidate_args,
        )
        resolved_args = apply_workflow_input_defaults(resolution.workflow.source, candidate_args)
        self._validate_routed_inputs(resolution.workflow_name, resolution.workflow.source, resolved_args)
        self._emit_routed_inputs_telemetry(
            ctx,
            candidate,
            resolution.workflow_name,
            candidate_args,
            resolved_args,
            args_input,
        )
        result = await child_engine.execute_async(resolution.workflow, resolved_args, ctx.ct)
        return _RouteExecutionResult(
            candidate=candidate,
            workflow_name=resolution.workflow_name,
            success=result.success,
            outputs=copy.deepcopy(result.outputs),
            error=result.error.message if result.error else None,
            steps_executed=len(result.step_results),
        )

    @staticmethod
    def _create_child_engine(ctx: StepExecutionContext, candidate: _RouteCandidate) -> WorkflowEngine:
        child = WorkflowEngine(ctx.engine.registry)
        child.llm_client = ctx.engine.llm_client
        child.workflow_fetcher = ctx.engine.workflow_fetcher
        child.workflow_call_resolver = ctx.engine.workflow_call_resolver
        child.workflow_candidate_provider = ctx.engine.workflow_candidate_provider
        child.template_engine = ctx.engine.template_engine
        child.mcp_client_factory = ctx.engine.mcp_client_factory
        child.human_input_provider = ctx.engine.human_input_provider
        child.checkpointer = None
        child.telemetry = ctx.engine.telemetry
        child.lm_defaults = copy.deepcopy(ctx.engine.lm_defaults)
        child.llm_options = ctx.engine.llm_options
        child.fetch_policy = ctx.engine.fetch_policy
        child.limits = WorkflowRouteExecutor._create_child_limits(ctx.limits, candidate)
        child.mcp_cache = ctx.engine.mcp_cache
        child.logger = ctx.engine.logger
        return child

    @staticmethod
    def _emit_routed_inputs_telemetry(
        ctx: StepExecutionContext,
        candidate: _RouteCandidate,
        workflow_name: str,
        candidate_args: dict[str, Any],
        resolved_args: dict[str, Any],
        args_input: dict[str, Any] | None,
    ) -> None:
        auto_extract_enabled = WorkflowRouteExecutor._parse_auto_extract_config(args_input).enabled
        argument_keys = ",".join(candidate_args.keys())
        resolved_input_keys = ",".join(resolved_args.keys())
        attributes: list[tuple[str, Any]] = [
            ("gnougo-flow.step.id", ctx.step.id),
            ("gnougo-flow.step.type", ctx.step.type),
            ("gnougo-flow.step.call_depth", ctx.call_depth),
            ("gnougo-flow.workflow_route.candidate.id", candidate.id),
            ("gnougo-flow.workflow_route.candidate.name", candidate.name),
            ("gnougo-flow.workflow_route.workflow.name", workflow_name),
            ("gnougo-flow.workflow_route.auto_extract.enabled", auto_extract_enabled),
            ("gnougo-flow.workflow_route.arguments.keys", argument_keys),
            ("gnougo-flow.workflow_route.resolved_inputs.keys", resolved_input_keys),
        ]
        if ctx.limits.log_step_content:
            attributes.extend(
                [
                    ("gnougo-flow.workflow_route.arguments", _format_inputs_for_telemetry(candidate_args)),
                    ("gnougo-flow.workflow_route.resolved_inputs", _format_inputs_for_telemetry(resolved_args)),
                ]
            )
        ctx.add_telemetry_event(
            "gnougo-flow.workflow_route.inputs_extracted",
            attributes,
        )

        message = (
            f"Triggering workflow '{workflow_name}' with inputs {_format_inputs_for_telemetry(resolved_args)}"
            if ctx.limits.log_step_content
            else f"Triggering workflow '{workflow_name}' with input keys: {resolved_input_keys}"
        )
        thinking_attributes: list[tuple[str, Any]] = [
            ("gnougo-flow.thinking.message", message),
            ("gnougo-flow.thinking.level", "progress"),
            ("gnougo-flow.thinking.source", "workflow.route"),
            ("gnougo-flow.workflow_route.candidate.id", candidate.id),
            ("gnougo-flow.workflow_route.candidate.name", candidate.name),
            ("gnougo-flow.workflow_route.workflow.name", workflow_name),
            ("gnougo-flow.workflow_route.arguments.keys", argument_keys),
            ("gnougo-flow.workflow_route.resolved_inputs.keys", resolved_input_keys),
        ]
        if ctx.limits.log_step_content:
            thinking_attributes.append(
                ("gnougo-flow.workflow_route.resolved_inputs", _format_inputs_for_telemetry(resolved_args))
            )
        ctx.add_telemetry_event(
            "gnougo-flow.step.thinking",
            thinking_attributes,
        )

    @staticmethod
    def _validate_routed_inputs(workflow_name: str, workflow: WorkflowDef | None, resolved_args: dict[str, Any]) -> None:
        input_errors = validate_input_types(workflow, resolved_args)
        if not input_errors:
            return
        raise WorkflowRuntimeException(
            ErrorCodes.INPUT_VALIDATION,
            f"Input validation failed for routed workflow '{workflow_name}': {'; '.join(input_errors)}",
            details={"workflow": workflow_name, "validation_errors": input_errors},
        )

    async def _combine_async(
        self,
        ctx: StepExecutionContext,
        input_obj: dict[str, Any],
        prompt: str,
        results: list[_RouteExecutionResult],
        strategy: str,
    ) -> str | None:
        if strategy.lower() == "raw":
            return None
        if strategy.lower() == "first" or len(results) == 1:
            first = results[0] if results else None
            if first is None:
                return None
            return self._extract_answer(first.outputs)
        if ctx.engine.llm_client is None:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "workflow.route combine strategy 'synthesize' requires an LLM client")

        combine = input_obj.get("combine") if isinstance(input_obj.get("combine"), dict) else {}
        provider, model = ctx.engine.resolve_llm_target(combine.get("provider"), combine.get("model"))
        response = await ctx.engine.call_llm_async(
            LLMRequest(
                provider=provider,
                model=model or "",
                temperature=combine.get("temperature", 0.2),
                prompt=self._build_synthesis_prompt(prompt, input_obj.get("history"), results),
            )
        )
        return response.text.strip()

    @staticmethod
    def _build_selection_prompt(
        prompt: str,
        history: Any,
        candidates: list[_RouteCandidate],
        min_selected: int,
        max_selected: int,
    ) -> str:
        lines = [
            "You are a workflow router. Select the best workflow candidates for the user prompt.",
            'Return JSON only: {"selected":[{"id":"candidate-id","reason":"short reason","confidence":0.0}]}.',
            f"Select at least {min_selected} and at most {max_selected} candidate(s).",
            "",
            "[USER PROMPT]",
            prompt,
        ]
        if history is not None:
            lines.extend(["", "[RECENT HISTORY]", json.dumps(history, ensure_ascii=False)])
        lines.extend(["", "[CANDIDATES]"])
        for candidate in candidates:
            lines.append(f"- id: {candidate.id}")
            lines.append(f"  name: {candidate.name}")
            if candidate.description:
                lines.append(f"  description: {candidate.description}")
            if candidate.tags:
                lines.append(f"  tags: {', '.join(candidate.tags)}")
            if candidate.inputs is not None:
                lines.append(f"  inputs: {json.dumps(candidate.inputs, ensure_ascii=False)}")
            if candidate.outputs is not None:
                lines.append(f"  outputs: {json.dumps(candidate.outputs, ensure_ascii=False)}")
        return "\n".join(lines)

    @staticmethod
    def _build_synthesis_prompt(prompt: str, history: Any, results: list[_RouteExecutionResult]) -> str:
        lines = [
            "Synthesize a concise final answer for the user from the routed workflow results.",
            "Start directly with the answer. Do not mention routing unless it is necessary for clarity.",
            "",
            "[USER PROMPT]",
            prompt,
        ]
        if history is not None:
            lines.extend(["", "[RECENT HISTORY]", json.dumps(history, ensure_ascii=False)])
        lines.extend(["", "[WORKFLOW RESULTS]", json.dumps(WorkflowRouteExecutor._build_results_array(results), ensure_ascii=False)])
        return "\n".join(lines)

    @staticmethod
    def _build_argument_extraction_prompt(
        route_input: dict[str, Any],
        candidate: _RouteCandidate,
        workflow: CompiledWorkflow,
        current_args: dict[str, Any],
        schema: dict[str, Any],
    ) -> str:
        lines = [
            "You extract workflow input arguments from a user prompt and recent history.",
            'Return JSON only in this shape: {"arguments":{...}}.',
            "The selected workflow's declared YAML inputs are authoritative.",
            "Return only keys declared in [EXPECTED WORKFLOW INPUT JSON SCHEMA].",
            "Map natural-language data into those exact input names; do not copy parent routing aliases unless the alias is itself a declared input.",
            "Only include fields you can infer confidently or that already exist in current arguments.",
            "Use defaults from the schema or current arguments when present. Do not invent values for unknown required fields.",
            "",
            "[SELECTED WORKFLOW]",
            f"id: {candidate.id}",
            f"name: {candidate.name}",
        ]
        if candidate.description:
            lines.append(f"description: {candidate.description}")
        if candidate.tags:
            lines.append(f"tags: {', '.join(candidate.tags)}")
        lines.append(f"workflow_name: {workflow.name}")
        if candidate.inputs is not None:
            lines.extend(["", "[CANDIDATE SKILL INPUT HINTS]", json.dumps(candidate.inputs, ensure_ascii=False)])
        lines.extend(
            [
                "",
                "[EXPECTED WORKFLOW INPUT JSON SCHEMA]",
                json.dumps(schema, ensure_ascii=False),
                "",
                "[CURRENT ARGUMENTS]",
                json.dumps(current_args, ensure_ascii=False),
                "",
                "[USER PROMPT]",
                str(route_input.get("prompt") or route_input.get("task") or route_input.get("query") or ""),
            ]
        )
        if route_input.get("history") is not None:
            lines.extend(["", "[RECENT HISTORY]", json.dumps(route_input["history"], ensure_ascii=False)])
        return "\n".join(lines)

    @staticmethod
    def _build_selection_schema() -> dict[str, Any]:
        return {
            "type": "object",
            "additionalProperties": False,
            "required": ["selected"],
            "properties": {
                "selected": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "additionalProperties": False,
                        "required": ["id"],
                        "properties": {
                            "id": {"type": "string"},
                            "reason": {"type": "string"},
                            "confidence": {"type": "number"},
                        },
                    },
                }
            },
        }

    @staticmethod
    def _build_argument_extraction_schema(argument_schema: Any) -> dict[str, Any]:
        return {
            "type": "object",
            "additionalProperties": False,
            "required": ["arguments"],
            "properties": {
                "arguments": WorkflowRouteExecutor._build_loose_extraction_argument_schema(argument_schema)
            },
        }

    @staticmethod
    def _build_loose_extraction_argument_schema(argument_schema: Any) -> dict[str, Any]:
        schema = copy.deepcopy(argument_schema)
        if not isinstance(schema, dict):
            schema = {"type": "object"}
        WorkflowRouteExecutor._remove_required_and_close_objects(schema)
        return schema

    @staticmethod
    def _remove_required_and_close_objects(schema: Any) -> None:
        if isinstance(schema, dict):
            schema.pop("required", None)
            schema_type = schema.get("type")
            if (isinstance(schema_type, str) and schema_type.lower() == "object") or isinstance(schema.get("properties"), dict):
                schema["additionalProperties"] = False

            properties = schema.get("properties")
            if isinstance(properties, dict):
                for property_schema in properties.values():
                    WorkflowRouteExecutor._remove_required_and_close_objects(property_schema)

            for union_key in ("allOf", "anyOf", "oneOf"):
                variants = schema.get(union_key)
                if isinstance(variants, list):
                    for variant in variants:
                        WorkflowRouteExecutor._remove_required_and_close_objects(variant)

            WorkflowRouteExecutor._remove_required_and_close_objects(schema.get("items"))
            WorkflowRouteExecutor._remove_required_and_close_objects(schema.get("additionalProperties"))
            return

        if isinstance(schema, list):
            for item in schema:
                WorkflowRouteExecutor._remove_required_and_close_objects(item)

    @staticmethod
    def _build_selected_array(selected: list[_RouteCandidate]) -> list[dict[str, Any]]:
        return [
            {
                "id": candidate.id,
                "name": candidate.name,
                "ref": copy.deepcopy(candidate.ref),
                "description": candidate.description,
                "reason": candidate.reason,
                "confidence": candidate.confidence,
                "tags": list(candidate.tags),
            }
            for candidate in selected
        ]

    @staticmethod
    def _build_results_array(results: list[_RouteExecutionResult]) -> list[dict[str, Any]]:
        return [
            {
                "id": result.candidate.id,
                "name": result.candidate.name,
                "workflow": result.workflow_name,
                "success": result.success,
                "outputs": copy.deepcopy(result.outputs),
                "error": result.error,
                "run": {"steps_executed": result.steps_executed},
            }
            for result in results
        ]

    @staticmethod
    def _read_string_array(node: Any) -> list[str]:
        if not isinstance(node, list):
            return []
        return [str(item).strip() for item in node if str(item).strip()]

    @staticmethod
    def _try_parse_json_object(text: str) -> dict[str, Any] | None:
        try:
            payload = json.loads(text)
        except Exception:
            return None
        return payload if isinstance(payload, dict) else None

    @staticmethod
    def _extract_answer(outputs: Any) -> str | None:
        if not isinstance(outputs, dict):
            return json.dumps(outputs, ensure_ascii=False) if outputs is not None else None
        for key in ("answer", "text", "result", "response"):
            value = outputs.get(key)
            if isinstance(value, str):
                return value
        return json.dumps(outputs, ensure_ascii=False)

    @staticmethod
    def _create_child_limits(parent: ExecutionLimits, candidate: _RouteCandidate) -> ExecutionLimits:
        parent_run_id = parent.run_id.strip() if parent.run_id else uuid.uuid4().hex
        return ExecutionLimits(
            max_total_steps_executed=parent.max_total_steps_executed,
            max_call_depth=parent.max_call_depth,
            max_parallel_branches=parent.max_parallel_branches,
            max_loop_iterations=parent.max_loop_iterations,
            max_expression_ast_nodes=parent.max_expression_ast_nodes,
            max_expression_statements=parent.max_expression_statements,
            expression_timeout_seconds=parent.expression_timeout_seconds,
            expression_memory_limit_bytes=parent.expression_memory_limit_bytes,
            max_switch_cases=parent.max_switch_cases,
            max_function_call_depth=parent.max_function_call_depth,
            log_step_content=parent.log_step_content,
            run_id=f"{parent_run_id}:route:{WorkflowRouteExecutor._sanitize_run_id_part(candidate.id)}:{uuid.uuid4().hex}",
        )

    @staticmethod
    def _sanitize_run_id_part(value: str) -> str:
        cleaned = re.sub(r"[^A-Za-z0-9_-]", "_", value.strip())[:64]
        return cleaned or "candidate"


_SENSITIVE_KEY_FRAGMENTS = (
    "api_key",
    "apikey",
    "authorization",
    "bearer",
    "credential",
    "password",
    "secret",
    "token",
)
_INPUT_ATTRIBUTE_LIMIT = 4 * 1024


def _format_inputs_for_telemetry(value: Any) -> str:
    redacted = _redact_sensitive_values(value)
    text = json.dumps(redacted if redacted is not None else {}, ensure_ascii=False, default=str)
    if len(text) <= _INPUT_ATTRIBUTE_LIMIT:
        return text
    return text[:_INPUT_ATTRIBUTE_LIMIT] + "...<truncated>"


def _redact_sensitive_values(value: Any) -> Any:
    if isinstance(value, dict):
        return {
            str(key): "<redacted>" if _is_sensitive_key(str(key)) else _redact_sensitive_values(item)
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [_redact_sensitive_values(item) for item in value]
    return copy.deepcopy(value)


def _is_sensitive_key(key: str) -> bool:
    lower = key.lower()
    return any(fragment in lower for fragment in _SENSITIVE_KEY_FRAGMENTS)
