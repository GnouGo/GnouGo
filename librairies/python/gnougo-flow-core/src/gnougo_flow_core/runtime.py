from __future__ import annotations

import asyncio
import copy
import json
import logging
import random
import time
import textwrap
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Protocol


_LOG = logging.getLogger("gnougo_flow_core")

import yaml

from .compilation import WorkflowCompiler
from .errors import ErrorCodes, WorkflowRuntimeException
from .expressions import ExpressionEvaluator, StringInterpolator
from .mcp_cache import McpCacheHelper
from .model_metadata import LLMModelMetadataResolver, sanitize_llm_request
from .models import (
    CompiledDocument,
    CompiledStep,
    CompiledWorkflow,
    ExecutionLimits,
    FetchPolicy,
    HumanInputRequest,
    LLMRequest,
    LLMResponse,
    LLMTool,
    LLMOptions,
    LLMModelMetadata,
    LlmRuntimeDefaults,
    McpCallResult,
    McpGetPromptResult,
    McpPromptInfo,
    McpResourceInfo,
    McpServerMetadata,
    McpToolInfo,
    OnErrorDef,
    RunResult,
    StepResult,
    StepStatus,
    TemplateResult,
    WorkflowCheckpoint,
    WorkflowDef,
)
from .parsing import WorkflowParser
from .runtime_contracts import (
    IHumanInputProvider,
    ILLMCapabilityResolver,
    ILLMClient,
    IMcpClientFactory,
    IMcpSession,
    ITemplateEngine,
    ITelemetrySpan,
    IWorkflowCandidateProvider,
    IWorkflowFetcher,
    IWorkflowCheckpointer,
    IWorkflowTelemetry,
    NullWorkflowTelemetry,
)
from .scripting import ScriptSandbox
from .templating import MustacheEngine, MustacheRenderException



@dataclass(slots=True)
class StepExecutionContext:
    step: CompiledStep
    data: dict[str, Any]
    engine: "WorkflowEngine"
    limits: ExecutionLimits
    call_depth: int
    call_stack: set[str]
    telemetry_span: ITelemetrySpan | None = None
    telemetry_attributes: dict[str, Any] = field(default_factory=dict)
    ct: asyncio.Event | None = None

    def set_telemetry_attribute(self, key: str, value: Any) -> None:
        self.telemetry_attributes[key] = value
        set_attribute = getattr(self.telemetry_span, "set_attribute", None)
        if callable(set_attribute):
            set_attribute(key, value)

    def add_telemetry_event(self, name: str, attributes: list[tuple[str, Any]] | None = None) -> None:
        add_event = getattr(self.telemetry_span, "add_event", None)
        if callable(add_event):
            add_event(name, attributes)

    def begin_telemetry_span(
        self,
        name: str,
        phase: str | None = None,
        attributes: list[tuple[str, Any]] | None = None,
    ) -> "TelemetrySpanScope":
        all_attributes: list[tuple[str, Any]] = [
            ("gnougo-flow.step.id", self.step.id),
            ("gnougo-flow.step.type", self.step.type),
            ("gnougo-flow.step.call_depth", self.call_depth),
        ]
        if phase:
            all_attributes.append(("gnougo-flow.plan.phase", phase))
        if attributes:
            all_attributes.extend(attributes)

        parent = self.telemetry_span or ITelemetrySpan()
        span_start = getattr(self.engine.telemetry, "span_start", None)
        span = (
            span_start(
                parent,
                {
                    "name": name,
                    "phase": phase,
                    "step_id": self.step.id,
                    "step_type": self.step.type,
                    "call_depth": self.call_depth,
                    "attributes": all_attributes,
                },
            )
            if callable(span_start)
            else ITelemetrySpan()
        )
        return TelemetrySpanScope(self.engine.telemetry, span)


class TelemetrySpanScope:
    def __init__(self, telemetry: IWorkflowTelemetry, span: ITelemetrySpan) -> None:
        self._telemetry = telemetry
        self._span = span
        self._started = time.perf_counter()
        self._success = True
        self._error_type: str | None = None
        self._error_message: str | None = None
        self._ended = False

    def __enter__(self) -> "TelemetrySpanScope":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        if exc is not None:
            self.fail(exc)
        self.end()

    def set_attribute(self, key: str, value: Any) -> None:
        set_attribute = getattr(self._span, "set_attribute", None)
        if callable(set_attribute):
            set_attribute(key, value)

    def add_event(self, name: str, attributes: list[tuple[str, Any]] | None = None) -> None:
        add_event = getattr(self._span, "add_event", None)
        if callable(add_event):
            add_event(name, attributes)

    def fail(self, exc: BaseException | str, message: str | None = None) -> None:
        self._success = False
        if isinstance(exc, str):
            self._error_type = exc
            self._error_message = message
        else:
            self._error_type = type(exc).__name__
            self._error_message = str(exc)

    def end(self) -> None:
        if self._ended:
            return
        self._ended = True
        duration = time.perf_counter() - self._started
        span_end = getattr(self._telemetry, "span_end", None)
        if callable(span_end):
            span_end(
                self._span,
                {
                    "success": self._success,
                    "duration_seconds": duration,
                    "error_type": self._error_type,
                    "error_message": self._error_message,
                },
            )
        end = getattr(self._span, "end", None)
        if callable(end):
            end()


def _coerce_long(value: Any) -> int | None:
    if value is None:
        return None
    if isinstance(value, bool):
        return int(value)
    if isinstance(value, (int, float)):
        return int(value)
    try:
        return int(str(value).strip().strip('"'))
    except Exception:
        return None


def _extract_usage_telemetry(ctx: StepExecutionContext, usage: Any, model: str | None) -> None:
    if model and "gen_ai.request.model" not in ctx.telemetry_attributes:
        ctx.set_telemetry_attribute("gen_ai.request.model", model)

    if not isinstance(usage, dict):
        return

    input_tokens = _coerce_long(usage.get("prompt_tokens"))
    if input_tokens is None:
        input_tokens = _coerce_long(usage.get("input_tokens"))

    output_tokens = _coerce_long(usage.get("completion_tokens"))
    if output_tokens is None:
        output_tokens = _coerce_long(usage.get("output_tokens"))

    total_tokens = _coerce_long(usage.get("total_tokens"))

    if input_tokens is not None:
        current = _coerce_long(ctx.telemetry_attributes.get("gen_ai.usage.input_tokens")) or 0
        ctx.set_telemetry_attribute("gen_ai.usage.input_tokens", current + input_tokens)
    if output_tokens is not None:
        current = _coerce_long(ctx.telemetry_attributes.get("gen_ai.usage.output_tokens")) or 0
        ctx.set_telemetry_attribute("gen_ai.usage.output_tokens", current + output_tokens)
    if total_tokens is not None:
        current = _coerce_long(ctx.telemetry_attributes.get("gen_ai.usage.total_tokens")) or 0
        ctx.set_telemetry_attribute("gen_ai.usage.total_tokens", current + total_tokens)
    elif input_tokens is not None or output_tokens is not None:
        ctx.set_telemetry_attribute(
            "gen_ai.usage.total_tokens",
            (_coerce_long(ctx.telemetry_attributes.get("gen_ai.usage.input_tokens")) or 0)
            + (_coerce_long(ctx.telemetry_attributes.get("gen_ai.usage.output_tokens")) or 0),
        )


def _build_llm_selection_prompt(user_prompt: str) -> str:
    return (
        "You are selecting and parameterizing MCP capabilities for the user's request.\n\n"
        "Important rules:\n"
        "- Preserve every explicit user constraint in the tool-call arguments.\n"
        "- Choose the smallest set of MCP calls needed to satisfy the request.\n"
        "- When a tool already exposes a parameter that matches the user's request, set it explicitly.\n\n"
        f"User request:\n{user_prompt}\n"
    )


def _build_structured_post_process_prompt(original_prompt: str, tool_calls: list[dict[str, Any]], results: list[dict[str, Any]]) -> str:
    return (
        "You have already executed the MCP capabilities needed for the user's request.\n\n"
        f"Original user request:\n{original_prompt}\n\n"
        f"Executed MCP tool calls:\n{json.dumps(tool_calls, ensure_ascii=False)}\n\n"
        f"Executed MCP results:\n{json.dumps(results, ensure_ascii=False)}\n\n"
        "Produce the final answer strictly from the executed MCP results.\n"
        "- Do not invent facts or links not present in MCP results.\n"
        "- Preserve explicit user constraints.\n"
        "- Return only the final answer matching the required JSON schema.\n"
    )


class IStepExecutor(Protocol):
    step_type: str

    async def execute_async(self, ctx: StepExecutionContext) -> Any: ...


@dataclass(slots=True)
class StepExceptionDoc:
    code: str
    retryable: bool
    description: str


@dataclass(slots=True)
class StepExceptionCatalog:
    step_type: str
    exceptions: list[StepExceptionDoc]


class StepExecutorRegistry:
    def __init__(self) -> None:
        self._executors: dict[str, IStepExecutor] = {}

    def register(self, executor: IStepExecutor) -> None:
        self._executors[executor.step_type] = executor

    def get(self, step_type: str) -> IStepExecutor | None:
        return self._executors.get(step_type)

    def get_dsl_snippets(self, allowed_types: set[str] | None = None) -> list[str]:
        return [snippet for _, snippet in self.get_dsl_snippet_map(allowed_types).items()]

    def get_dsl_snippet_map(self, allowed_types: set[str] | None = None) -> dict[str, str]:
        snippets: dict[str, str] = {}
        for step_type, executor in sorted(self._executors.items(), key=lambda x: x[0]):
            if allowed_types is not None and step_type not in allowed_types:
                continue
            snippet = getattr(executor, "dsl_snippet", None)
            if isinstance(snippet, str) and snippet.strip():
                snippets[step_type] = snippet.strip()
                continue
            desc = getattr(executor, "step_description", None)
            if isinstance(desc, str) and desc.strip():
                snippets[step_type] = f"- {step_type}: {desc.strip()}"
        return snippets

    def get_step_exception_catalogs(self, allowed_types: set[str] | None = None) -> list[StepExceptionCatalog]:
        catalogs: list[StepExceptionCatalog] = []
        for step_type, executor in sorted(self._executors.items(), key=lambda x: x[0]):
            if allowed_types is not None and step_type not in allowed_types:
                continue
            raw = getattr(executor, "documented_exceptions", None)
            if not isinstance(raw, list) or not raw:
                continue
            docs: list[StepExceptionDoc] = []
            for item in raw:
                if isinstance(item, StepExceptionDoc):
                    docs.append(item)
                elif isinstance(item, tuple) and len(item) == 3:
                    code, retryable, description = item
                    docs.append(StepExceptionDoc(code=str(code), retryable=bool(retryable), description=str(description)))
                elif isinstance(item, dict) and {"code", "retryable", "description"}.issubset(item):
                    docs.append(
                        StepExceptionDoc(
                            code=str(item["code"]),
                            retryable=bool(item["retryable"]),
                            description=str(item["description"]),
                        )
                    )
            if docs:
                catalogs.append(StepExceptionCatalog(step_type=step_type, exceptions=docs))
        return catalogs


def apply_workflow_input_defaults(workflow: WorkflowDef | None, inputs: Any) -> dict[str, Any]:
    merged = dict(inputs or {})
    if not workflow or not workflow.inputs:
        return merged
    for name, definition in workflow.inputs.items():
        if name not in merged and definition.default is not None:
            merged[name] = definition.default
    return merged


def validate_input_types(workflow: WorkflowDef | None, inputs: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    if not workflow or not workflow.inputs:
        return errors

    def describe(value: Any) -> str:
        if value is None:
            return "null"
        if isinstance(value, bool):
            return "boolean"
        if isinstance(value, (int, float)):
            return "number"
        if isinstance(value, str):
            return "string"
        if isinstance(value, list):
            return "array"
        if isinstance(value, dict):
            return "object"
        return type(value).__name__

    def check(node: Any, definition: Any, path: str, depth: int = 0) -> None:
        if depth > 16:
            errors.append(f"'{path}': validation exceeded maximum depth (16).")
            return
        typ = definition.type.lower()
        if typ == "any":
            return
        if typ == "string" and not isinstance(node, str):
            errors.append(f"'{path}': expected string, got {describe(node)}.")
            return
        if typ == "number" and not isinstance(node, (int, float)):
            errors.append(f"'{path}': expected number, got {describe(node)}.")
            return
        if typ == "boolean" and not isinstance(node, bool):
            errors.append(f"'{path}': expected boolean, got {describe(node)}.")
            return
        if typ == "array":
            if not isinstance(node, list):
                errors.append(f"'{path}': expected array, got {describe(node)}.")
                return
            if definition.items:
                for i, item in enumerate(node):
                    check(item, definition.items, f"{path}[{i}]", depth + 1)
            return
        if typ in {"object", "dictionary"}:
            if not isinstance(node, dict):
                errors.append(f"'{path}': expected object, got {describe(node)}.")
                return
            if typ == "object":
                for req in definition.required_properties or []:
                    if req not in node:
                        errors.append(f"'{path}': missing required property '{req}'.")
                for prop, prop_def in (definition.properties or {}).items():
                    if prop in node:
                        check(node[prop], prop_def, f"{path}.{prop}", depth + 1)
            if definition.additional_properties:
                known = set((definition.properties or {}).keys())
                for key, val in node.items():
                    if typ == "dictionary" or key not in known:
                        check(val, definition.additional_properties, f"{path}.{key}", depth + 1)

    for name, definition in workflow.inputs.items():
        value = inputs.get(name)
        if definition.required and value is None:
            errors.append(f"Input '{name}' is required but was not provided.")
            continue
        if value is not None:
            check(value, definition, name)
    return errors


def build_workflow_source_telemetry_info(workflow: CompiledWorkflow, fallback_source_text: str | None = None) -> dict[str, Any]:
    document = workflow.document.source if workflow.document else None
    source_text = document.raw_yaml if document and document.raw_yaml else fallback_source_text
    return {
        "document_name": document.name if document and document.name else None,
        "source_text": source_text,
        "source_format": "yaml" if source_text else None,
    }


class WorkflowEngine:
    def __init__(self, registry: StepExecutorRegistry | None = None) -> None:
        self._registry = registry or self._create_default_registry()
        self._evaluator = ExpressionEvaluator()
        self._interpolator = StringInterpolator(self._evaluator)
        self._total_steps_executed = 0

        self.llm_client: ILLMClient | None = None
        self.llm_capabilities: ILLMCapabilityResolver | None = None
        self.workflow_fetcher: IWorkflowFetcher | None = None
        self.workflow_call_resolver: Any = None
        self.workflow_candidate_provider: IWorkflowCandidateProvider | None = None
        self.template_engine: ITemplateEngine | None = None
        self.mcp_client_factory: IMcpClientFactory | None = None
        self.human_input_provider: IHumanInputProvider | None = None
        self.checkpointer: IWorkflowCheckpointer | None = None
        self.mcp_cache: McpCacheHelper = McpCacheHelper()

        self.telemetry: IWorkflowTelemetry = NullWorkflowTelemetry()
        self.lm_defaults = LlmRuntimeDefaults()
        self.llm_options = LLMOptions()
        self.fetch_policy: FetchPolicy | None = None
        self.limits = ExecutionLimits()
        self.compiled_document: CompiledDocument | None = None
        self.script_functions: dict[str, Any] = {}
        self.logger: logging.Logger = _LOG

    @property
    def evaluator(self) -> ExpressionEvaluator:
        return self._evaluator

    @property
    def interpolator(self) -> StringInterpolator:
        return self._interpolator

    @property
    def registry(self) -> StepExecutorRegistry:
        return self._registry

    def resolve_llm_target(self, provider: str | None, model: str | None) -> tuple[str | None, str | None]:
        rp = provider if provider and provider.strip() else self.lm_defaults.provider
        rm = model if model and model.strip() else self.lm_defaults.model
        return rp or None, rm or None

    def resolve_model_metadata(self, provider_type: str | None, model: str) -> LLMModelMetadata:
        return LLMModelMetadataResolver(self.llm_options).resolve(provider_type, model)

    def sanitize_llm_request(self, request: LLMRequest) -> LLMRequest:
        metadata = self.resolve_model_metadata(request.provider, request.model)
        return sanitize_llm_request(request, metadata)

    async def call_llm_async(self, request: LLMRequest) -> LLMResponse:
        if self.llm_client is None:
            raise WorkflowRuntimeException(ErrorCodes.LLM_NETWORK, "No LLM client configured")
        return await self.llm_client.call_async(self.sanitize_llm_request(request))

    def _prepare_workflow_execution(self, workflow: CompiledWorkflow) -> None:
        self.compiled_document = workflow.document

        script_functions: dict[str, Any] = {}
        sandbox = ScriptSandbox()
        if workflow.document and workflow.document.source.functions:
            script_functions.update(sandbox.load_functions(workflow.document.source.functions))
        if workflow.source.functions:
            script_functions.update(sandbox.load_functions(workflow.source.functions))
        script_functions.update(self.script_functions)

        self._evaluator = ExpressionEvaluator(script_functions, self.limits)
        self._interpolator = StringInterpolator(self._evaluator)

    async def _save_checkpoint_async(
        self,
        workflow: CompiledWorkflow,
        data: dict[str, Any],
        next_step_index: int,
    ) -> None:
        if self.checkpointer is None or self.limits.run_id is None:
            return

        document = workflow.document.source if workflow.document else None
        checkpoint = WorkflowCheckpoint(
            run_id=self.limits.run_id,
            workflow_name=(document.name if document and document.name else workflow.name),
            next_step_index=next_step_index,
            step_outputs=copy.deepcopy(data.get("steps", {})),
            inputs=copy.deepcopy(data.get("inputs")),
            workflow_yaml=document.raw_yaml if document and document.raw_yaml else "",
            status="running",
            timestamp=datetime.now(timezone.utc).isoformat(),
        )
        await self.checkpointer.save_async(checkpoint)

    async def execute_async(
        self,
        workflow: CompiledWorkflow,
        inputs: Any,
        ct: asyncio.Event | None = None,
    ) -> RunResult:
        self._total_steps_executed = 0
        self._prepare_workflow_execution(workflow)

        merged_inputs = apply_workflow_input_defaults(workflow.source, inputs)
        data = {"inputs": merged_inputs, "steps": {}, "env": {}}

        type_errors = validate_input_types(workflow.source, merged_inputs)
        if type_errors:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"Input validation failed: {'; '.join(type_errors)}")

        result = RunResult(success=True)
        span = self.telemetry.workflow_start(
            {
                "workflow_name": workflow.name,
                "inputs": merged_inputs,
                **build_workflow_source_telemetry_info(workflow),
            }
        )
        started = time.perf_counter()
        document_name = (
            workflow.document.source.name
            if workflow.document and workflow.document.source and workflow.document.source.name
            else "(inline)"
        )
        self.logger.info(
            "Workflow '%s' starting (document: %s)", workflow.name, document_name
        )
        try:
            await self.execute_steps_async(
                workflow.steps,
                data,
                result,
                self.limits,
                0,
                set(),
                span,
                ct=ct,
                checkpoint_workflow=workflow,
            )
            if workflow.outputs:
                outputs: dict[str, Any] = {}
                for key, out in workflow.outputs.items():
                    outputs[key] = self.evaluate_output_def(out, data)
                result.outputs = outputs
            else:
                result.outputs = data.get("steps")
            self.logger.info(
                "Workflow '%s' completed successfully in %.1fms (%d steps)",
                workflow.name,
                (time.perf_counter() - started) * 1000.0,
                self._total_steps_executed,
            )
        except WorkflowRuntimeException as exc:
            result.success = False
            result.error = exc.to_workflow_error()
            self.logger.error(
                "Workflow '%s' failed: [%s] %s",
                workflow.name, exc.code, str(exc),
                exc_info=True,
            )
        except asyncio.CancelledError:
            result.success = False
            result.error = WorkflowRuntimeException("CANCELLED", "Workflow execution cancelled", True).to_workflow_error()
            self.logger.warning(
                "Workflow '%s' was cancelled after %.1fms",
                workflow.name,
                (time.perf_counter() - started) * 1000.0,
            )
        except Exception as exc:
            result.success = False
            result.error = WorkflowRuntimeException("INTERNAL_ERROR", str(exc), False).to_workflow_error()
            self.logger.error(
                "Workflow '%s' internal error: %s",
                workflow.name, str(exc),
                exc_info=True,
            )
        finally:
            self.telemetry.workflow_end(
                span,
                {
                    "success": result.success,
                    "steps_executed": self._total_steps_executed,
                    "duration": time.perf_counter() - started,
                    "error_code": result.error.code if result.error else None,
                },
            )
        return result

    async def resume_async(
        self,
        run_id: str,
        workflow: CompiledWorkflow,
        ct: asyncio.Event | None = None,
    ) -> RunResult:
        if self.checkpointer is None:
            raise RuntimeError("resume_async requires a configured IWorkflowCheckpointer.")

        checkpoint = await self.checkpointer.load_async(run_id)
        if checkpoint is None:
            raise WorkflowRuntimeException("CHECKPOINT_NOT_FOUND", f"No checkpoint found for run '{run_id}'.")

        self._total_steps_executed = 0
        self.limits.run_id = run_id
        self._prepare_workflow_execution(workflow)

        data = {
            "inputs": copy.deepcopy(checkpoint.inputs or {}),
            "steps": copy.deepcopy(checkpoint.step_outputs or {}),
            "env": {},
        }
        result = RunResult(success=True)
        span = self.telemetry.workflow_start(
            {
                "workflow_name": workflow.name,
                "inputs": copy.deepcopy(checkpoint.inputs),
                **build_workflow_source_telemetry_info(workflow, checkpoint.workflow_yaml or None),
            }
        )
        started = time.perf_counter()

        self.logger.info(
            "Workflow '%s' resuming from step index %d (runId: %s)",
            workflow.name,
            checkpoint.next_step_index,
            run_id,
        )
        try:
            await self.execute_steps_async(
                workflow.steps,
                data,
                result,
                self.limits,
                0,
                set(),
                span,
                start_from_index=checkpoint.next_step_index,
                ct=ct,
                checkpoint_workflow=workflow,
            )
            if workflow.outputs:
                result.outputs = {k: self.evaluate_output_def(v, data) for k, v in workflow.outputs.items()}
            else:
                result.outputs = data.get("steps")

            checkpoint.status = "completed"
            checkpoint.timestamp = datetime.now(timezone.utc).isoformat()
            checkpoint.step_outputs = copy.deepcopy(data.get("steps", {}))
            await self.checkpointer.save_async(checkpoint)
        except WorkflowRuntimeException as exc:
            result.success = False
            result.error = exc.to_workflow_error()
            checkpoint.status = "failed"
            checkpoint.timestamp = datetime.now(timezone.utc).isoformat()
            await self.checkpointer.save_async(checkpoint)
        except asyncio.CancelledError:
            result.success = False
            result.error = WorkflowRuntimeException("CANCELLED", "Workflow execution cancelled", True).to_workflow_error()
            checkpoint.status = "paused"
            checkpoint.timestamp = datetime.now(timezone.utc).isoformat()
            await self.checkpointer.save_async(checkpoint)
        except Exception as exc:
            result.success = False
            result.error = WorkflowRuntimeException("INTERNAL_ERROR", str(exc), False).to_workflow_error()
            checkpoint.status = "failed"
            checkpoint.timestamp = datetime.now(timezone.utc).isoformat()
            await self.checkpointer.save_async(checkpoint)
        finally:
            self.telemetry.workflow_end(
                span,
                {
                    "success": result.success,
                    "steps_executed": self._total_steps_executed,
                    "duration": time.perf_counter() - started,
                    "error_code": result.error.code if result.error else None,
                },
            )
        return result

    async def execute_steps_async(
        self,
        steps: list[CompiledStep],
        data: dict[str, Any],
        result: RunResult,
        limits: ExecutionLimits,
        call_depth: int,
        call_stack: set[str],
        parent_span: ITelemetrySpan | None = None,
        start_from_index: int = 0,
        ct: asyncio.Event | None = None,
        checkpoint_workflow: CompiledWorkflow | None = None,
    ) -> None:
        parent_span = parent_span or self.telemetry.workflow_start({})
        for index in range(start_from_index, len(steps)):
            step = steps[index]
            if ct is not None and ct.is_set():
                raise asyncio.CancelledError()
            self._total_steps_executed += 1
            if self._total_steps_executed > limits.max_total_steps_executed:
                raise WorkflowRuntimeException(
                    ErrorCodes.LOOP_LIMIT,
                    f"Total steps executed ({self._total_steps_executed}) exceeds limit ({limits.max_total_steps_executed})",
                )

            step_result = StepResult(step_id=step.id, step_type=step.type, status=StepStatus.RUNNING)
            result.step_results.append(step_result)
            started = time.perf_counter()
            step_span: ITelemetrySpan | None = None
            resolved_input = None

            try:
                if step.source.if_ is not None:
                    guard = self._interpolator.interpolate(step.source.if_, data)
                    if not ExpressionEvaluator.get_bool(guard):
                        step_result.status = StepStatus.SKIPPED
                        step_result.duration = time.perf_counter() - started
                        continue

                if step.source.input is not None:
                    if (
                        step.type == "loop.sequential"
                        and isinstance(step.source.input, dict)
                        and "while" in step.source.input
                    ):
                        temp = dict(step.source.input)
                        while_expr = temp.pop("while")
                        resolved = self._interpolator.resolve_deep(temp, data)
                        resolved["while"] = while_expr
                        resolved_input = resolved
                    else:
                        resolved_input = self._interpolator.resolve_deep(step.source.input, data)

                step_span = self.telemetry.step_start(
                    parent_span,
                    {
                        "step_id": step.id,
                        "step_type": step.type,
                        "input": resolved_input,
                        "call_depth": call_depth,
                    },
                )

                self.logger.debug(
                    "Step '%s' (%s) starting at depth %d", step.id, step.type, call_depth
                )

                if limits.log_step_content and resolved_input is not None and step_span is not None:
                    try:
                        input_json = json.dumps(resolved_input, default=str, ensure_ascii=False)
                    except Exception:
                        input_json = str(resolved_input)
                    step_span.add_event(
                        "gnougo-flow.step.input",
                        [
                            ("gnougo-flow.step.id", step.id),
                            ("gnougo-flow.step.type", step.type),
                            ("gnougo-flow.step.call_depth", call_depth),
                            ("gnougo-flow.content.input", input_json),
                        ],
                    )

                output = await self._execute_with_retry_async(
                    step,
                    data,
                    resolved_input,
                    limits,
                    call_depth,
                    call_stack,
                    step_span,
                    ct=ct,
                )

                data.setdefault("steps", {})[step.id] = output
                if step.source.output:
                    data[step.source.output] = output

                if limits.log_step_content and output is not None and step_span is not None:
                    try:
                        output_json = json.dumps(output, default=str, ensure_ascii=False)
                    except Exception:
                        output_json = str(output)
                    step_span.add_event(
                        "gnougo-flow.step.output",
                        [
                            ("gnougo-flow.step.id", step.id),
                            ("gnougo-flow.step.type", step.type),
                            ("gnougo-flow.step.call_depth", call_depth),
                            ("gnougo-flow.content.output", output_json),
                        ],
                    )

                step_result.output = output
                step_result.status = StepStatus.SUCCEEDED
                self.logger.info(
                    "Step '%s' (%s) succeeded in %.1fms",
                    step.id, step.type,
                    (time.perf_counter() - started) * 1000.0,
                )
                self.telemetry.step_end(step_span, {"status": StepStatus.SUCCEEDED, "output": output})
                if call_depth == 0 and checkpoint_workflow is not None:
                    await self._save_checkpoint_async(checkpoint_workflow, data, index + 1)
            except WorkflowRuntimeException as exc:
                step_result.error = exc.to_workflow_error()
                self.logger.error(
                    "Step '%s' (%s) failed: [%s] %s",
                    step.id, step.type, exc.code, str(exc),
                    exc_info=True,
                )
                if step.source.on_error is not None:
                    action, handled_output = self._handle_on_error(step.source.on_error, exc, step, data)
                    if action == "continue":
                        step_result.status = StepStatus.SUCCEEDED
                        if handled_output is not None:
                            data.setdefault("steps", {})[step.id] = handled_output
                            if step.source.output:
                                data[step.source.output] = handled_output
                            step_result.output = handled_output
                        step_result.duration = time.perf_counter() - started
                        self.telemetry.step_end(step_span or ITelemetrySpan(), {"status": StepStatus.SUCCEEDED})
                        continue
                step_result.status = StepStatus.FAILED
                self.telemetry.step_end(step_span or ITelemetrySpan(), {"status": StepStatus.FAILED, "error_code": exc.code})
                raise
            finally:
                step_result.duration = time.perf_counter() - started

    async def _execute_with_retry_async(
        self,
        step: CompiledStep,
        data: dict[str, Any],
        resolved_input: Any,
        limits: ExecutionLimits,
        call_depth: int,
        call_stack: set[str],
        step_span: ITelemetrySpan | None,
        ct: asyncio.Event | None = None,
    ) -> Any:
        retry = step.source.retry
        max_attempts = retry.max if retry else 1
        backoff_ms = retry.backoff_ms if retry else 1000
        backoff_mult = retry.backoff_mult if retry else 2.0
        jitter_ms = retry.jitter_ms if retry else 0

        last_exc: WorkflowRuntimeException | None = None
        for attempt in range(max_attempts):
            try:
                executor = self._registry.get(step.type)
                if not executor:
                    raise WorkflowRuntimeException(ErrorCodes.STEP_TYPE_UNKNOWN, f"Unknown step type: {step.type}")

                ctx = StepExecutionContext(
                    step=step,
                    data=data,
                    engine=self,
                    limits=limits,
                    call_depth=call_depth,
                    call_stack=call_stack,
                    telemetry_span=step_span,
                    ct=ct,
                )

                if resolved_input is not None:
                    data.setdefault("steps", {})[f"__{step.id}_input__"] = resolved_input

                output = await executor.execute_async(ctx)
                data.setdefault("steps", {}).pop(f"__{step.id}_input__", None)
                return output
            except WorkflowRuntimeException as exc:
                last_exc = exc
                if attempt < max_attempts - 1 and exc.retryable:
                    delay = backoff_ms + (random.randint(0, jitter_ms) if jitter_ms > 0 else 0)
                    delay_s = delay / 1000.0
                    if ct is not None:
                        try:
                            await asyncio.wait_for(ct.wait(), timeout=delay_s)
                            # Event got set during sleep → propagate cancellation
                            raise asyncio.CancelledError()
                        except asyncio.TimeoutError:
                            pass
                    else:
                        await asyncio.sleep(delay_s)
                    backoff_ms = int(backoff_ms * backoff_mult)
                    continue
                raise

        raise last_exc or WorkflowRuntimeException(ErrorCodes.EVAL_ERROR, "Execution failed after retries")

    def _handle_on_error(self, on_error: OnErrorDef, exc: WorkflowRuntimeException, step: CompiledStep, data: dict[str, Any]) -> tuple[str, Any]:
        error_ctx = {
            **data,
            "error": {
                "code": exc.code,
                "type": exc.code,
                "message": str(exc),
                "retryable": exc.retryable,
                "details": exc.details,
            },
            "step": {"id": step.id, "type": step.type},
        }
        for case in on_error.cases:
            if case.if_:
                if not ExpressionEvaluator.get_bool(self._interpolator.interpolate(case.if_, error_ctx)):
                    continue
            out = self._interpolator.resolve_deep(copy.deepcopy(case.set_output), error_ctx) if case.set_output is not None else None
            return case.action, out
        return "stop", None

    def get_resolved_input(self, ctx: StepExecutionContext) -> Any:
        key = f"__{ctx.step.id}_input__"
        if key in ctx.data.get("steps", {}):
            return ctx.data["steps"][key]
        return ctx.step.source.input

    def evaluate_output_def(self, definition: Any, data: dict[str, Any]) -> Any:
        if definition.expr:
            return self._interpolator.interpolate(definition.expr, data)
        if definition.properties:
            return {k: self.evaluate_output_def(v, data) for k, v in definition.properties.items()}
        return None

    @staticmethod
    def _create_default_registry() -> StepExecutorRegistry:
        from .runtime_steps import (
            AssertNonNullExecutor,
            EmitExecutor,
            HumanInputExecutor,
            LlmCallExecutor,
            LoopParallelExecutor,
            LoopSequentialExecutor,
            McpCallExecutor,
            McpListExecutor,
            ParallelExecutor,
            SequenceExecutor,
            SetExecutor,
            SwitchExecutor,
            TemplateRenderExecutor,
            WorkflowCallExecutor,
            WorkflowExecuteExecutor,
            WorkflowPlanExecutor,
            WorkflowRouteExecutor,
        )

        registry = StepExecutorRegistry()
        registry.register(AssertNonNullExecutor())
        registry.register(SequenceExecutor())
        registry.register(ParallelExecutor())
        registry.register(LoopSequentialExecutor())
        registry.register(LoopParallelExecutor())
        registry.register(SwitchExecutor())
        registry.register(SetExecutor())
        registry.register(TemplateRenderExecutor())
        registry.register(LlmCallExecutor())
        registry.register(WorkflowCallExecutor())
        registry.register(WorkflowPlanExecutor())
        registry.register(WorkflowExecuteExecutor())
        registry.register(WorkflowRouteExecutor())
        registry.register(McpCallExecutor())
        registry.register(McpListExecutor())
        registry.register(EmitExecutor())
        registry.register(HumanInputExecutor())
        return registry
