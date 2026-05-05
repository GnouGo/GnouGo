from __future__ import annotations

import asyncio
import copy
import inspect
import json
import os
import uuid
from dataclasses import dataclass
from typing import Any

import gnougo_flow_core.runtime as _runtime
from gnougo_flow_core.errors import ErrorCodes, WorkflowRuntimeException
from gnougo_flow_core.mcp_cache import (
    cache_prompts,
    cache_tools,
    get_cached_prompts,
    get_cached_tools,
)
from gnougo_flow_core.models import LLMRequest, LLMTool, McpPromptInfo
from gnougo_flow_core.runtime import StepExecutionContext
from gnougo_flow_core.runtime_contracts import IMcpSession
from gnougo_flow_core.templating import MustacheEngine


@dataclass(slots=True)
class McpCorrelationContext:
    traceparent: str | None = None
    tracestate: str | None = None
    trace_id: str | None = None
    span_id: str | None = None
    parent_span_id: str | None = None
    correlation_id: str | None = None
    run_id: str | None = None
    step_id: str | None = None
    step_type: str | None = None
    mcp_server: str | None = None
    mcp_method: str | None = None
    mcp_kind: str | None = None

    def to_mcp_meta(self) -> dict[str, Any]:
        gnougo: dict[str, Any] = {}
        _add_if_present(gnougo, "traceparent", self.traceparent)
        _add_if_present(gnougo, "tracestate", self.tracestate)
        _add_if_present(gnougo, "traceId", self.trace_id)
        _add_if_present(gnougo, "spanId", self.span_id)
        _add_if_present(gnougo, "parentSpanId", self.parent_span_id)
        _add_if_present(gnougo, "correlationId", self.correlation_id)
        _add_if_present(gnougo, "runId", self.run_id)
        _add_if_present(gnougo, "stepId", self.step_id)
        _add_if_present(gnougo, "stepType", self.step_type)
        _add_if_present(gnougo, "mcpServer", self.mcp_server)
        _add_if_present(gnougo, "mcpMethod", self.mcp_method)
        _add_if_present(gnougo, "mcpKind", self.mcp_kind)

        meta: dict[str, Any] = {"gnougo": gnougo}
        _add_if_present(meta, "traceparent", self.traceparent)
        _add_if_present(meta, "tracestate", self.tracestate)
        return meta


def _is_unsupported_capability(exc: Exception, method_name: str) -> bool:
    current: BaseException | None = exc
    while current is not None:
        msg = str(current).lower()
        if method_name.lower() in msg and any(
            phrase in msg
            for phrase in ("not available", "not implemented", "method not found", "no handler")
        ):
            return True
        current = current.__cause__ or current.__context__
    return False


async def _maybe_timeout(coro: Any, timeout: float | None) -> Any:
    return await (asyncio.wait_for(coro, timeout=timeout) if timeout is not None else coro)


async def _try_list_prompts(session: IMcpSession, server_name: str, ctx: StepExecutionContext, timeout: float | None) -> list[McpPromptInfo]:
    try:
        return await _maybe_timeout(session.list_prompts_async(), timeout)
    except Exception as exc:
        if not _is_unsupported_capability(exc, "prompts/list"):
            raise
        ctx.engine.logger.warning("mcp.call: prompts/list not supported on '%s': %s", server_name, exc)
        ctx.set_telemetry_attribute("mcp.prompts_unsupported", True)
        ctx.add_telemetry_event(
            "mcp.capability.unsupported",
            [("mcp.server.name", server_name), ("mcp.method", "prompts/list"), ("mcp.reason", str(exc))],
        )
        return []


def _build_unique_internal_name(method_name: str, kind: str, used_names: set[str]) -> str:
    if method_name not in used_names:
        used_names.add(method_name)
        return method_name
    prefixed = f"{kind}:{method_name}"
    if prefixed not in used_names:
        used_names.add(prefixed)
        return prefixed
    index = 2
    while f"{prefixed}:{index}" in used_names:
        index += 1
    internal = f"{prefixed}:{index}"
    used_names.add(internal)
    return internal


def _prompt_argument_schema(arguments: Any) -> dict[str, Any]:
    if not arguments:
        return {"type": "object"}
    properties: dict[str, Any] = {}
    required: list[str] = []
    for arg in arguments:
        name = arg.get("name") if isinstance(arg, dict) else getattr(arg, "name", None)
        if not name:
            continue
        description = arg.get("description") if isinstance(arg, dict) else getattr(arg, "description", None)
        is_required = arg.get("required", False) if isinstance(arg, dict) else getattr(arg, "required", False)
        properties[str(name)] = {"type": "string", "description": description}
        if bool(is_required):
            required.append(str(name))
    schema: dict[str, Any] = {"type": "object", "properties": properties}
    if required:
        schema["required"] = required
    return schema

class McpCallExecutor:
    step_type = "mcp.call"
    step_description = "Call MCP tool/prompt directly or via LLM-assisted selection."
    dsl_snippet = """
### mcp.call - Execute MCP tool or prompt
Direct MCP call pattern (preferred when tool names are known): use `mcp.call` directly with explicit `method` and `request`.
```yaml
- id: browse
  type: mcp.call
  input:
    server: GnOuGo.Browser.Mcp
    kind: tool
    method: navigate
    request:
      url: "https://slimfaas.dev"

LLM-assisted MCP call pattern: provide a natural-language `prompt` + `model` (+ optional `temperature`) and discovered tools/prompts.
- id: smart_call
  type: mcp.call
  input:
    server: GnOuGo.Browser.Mcp
    model: gpt-4o-mini
    prompt: "Pick and call the best MCP capability for this task"
    tools: "${data.steps.discover_mcp.tools}"
```
Output: single `{ status, response }`, batch `{ status, results }`, or
assisted `{ status, selection_mode, text, tool_calls, results, json? }`.
Output (LLM-assisted): inspect `results[]` and optional `json` when `structured_output` is configured.
Output access patterns: `data.steps.<id>.status`, `data.steps.<id>.response`, `data.steps.<id>.results`, `data.steps.<id>.json`.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "mcp.call input/server/method is malformed."),
        (ErrorCodes.TEMPLATE_SYNTAX, False, "mcp.call request_template rendering failed."),
        (ErrorCodes.JSON_PARSE, False, "mcp.call request_template rendered invalid JSON."),
        (ErrorCodes.MCP_CALL_ERROR, False, "MCP tool execution failed."),
        (ErrorCodes.MCP_PROMPT_ERROR, False, "MCP prompt or selection failed."),
        (ErrorCodes.MCP_TIMEOUT, True, "mcp.call timed out."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        factory = ctx.engine.mcp_client_factory
        if factory is None:
            raise WorkflowRuntimeException(ErrorCodes.MCP_CONNECTION_ERROR, "No IMcpClientFactory configured")
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.call input must be object")

        server = input_obj.get("server")
        if not isinstance(server, str) or not server:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.call requires 'server'")

        kind = str(input_obj.get("kind", "tool")).lower()
        if kind not in {"tool", "prompt"}:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"mcp.call 'kind' must be 'tool' or 'prompt', got '{kind}'")

        has_method = "method" in input_obj and input_obj.get("method") is not None
        has_methods = "methods" in input_obj
        method = input_obj.get("method")
        methods = None
        if has_methods:
            raw_methods = input_obj.get("methods")
            if not isinstance(raw_methods, list) or not raw_methods:
                raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.call 'methods' must be a non-empty array of strings")
            methods = []
            for item in raw_methods:
                name = "" if item is None else str(item)
                if not name:
                    raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.call 'methods' contains an empty or null entry")
                methods.append(name)
        elif has_method:
            method = str(method)
            if not method:
                raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.call 'method' must be a non-empty string")
        else:
            method = None
        has_prompt_selection = input_obj.get("prompt") is not None

        ctx.set_telemetry_attribute("gen_ai.operation.name", "chat" if has_prompt_selection else ("prompt_get" if kind == "prompt" else "tool_call"))
        ctx.set_telemetry_attribute("mcp.server.name", server)
        ctx.set_telemetry_attribute("mcp.kind", kind)
        ctx.add_telemetry_event(
            "gnougo-flow.step.thinking",
            [
                ("gnougo-flow.thinking.message", f"Calling MCP server '{server}' ({kind})..."),
                ("gnougo-flow.thinking.level", "thinking"),
            ],
        )

        request = self._build_request_args(input_obj, ctx) if not has_prompt_selection else None
        timeout_ms = input_obj.get("timeout_ms")
        timeout = float(timeout_ms) / 1000.0 if isinstance(timeout_ms, (int, float)) else None
        target = "(llm-selection)" if has_prompt_selection else "(auto)"
        try:
            session = await _maybe_timeout(factory.get_client_async(server), timeout)

            if has_prompt_selection:
                return await self._execute_llm_assisted(ctx, session, input_obj, kind, method if isinstance(method, str) else None, methods, timeout)

            async def single(call_method: str, req: Any) -> dict[str, Any]:
                return await self._call_single(ctx, session, kind, call_method, req, timeout)

            if methods is not None:
                target = ", ".join(methods)
                results = []
                has_error = False
                for m in methods:
                    item = await single(m, copy.deepcopy(request))
                    item["method"] = m
                    has_error = has_error or item.get("status") == "error"
                    results.append(item)
                ctx.set_telemetry_attribute("mcp.methods_count", len(methods))
                ctx.set_telemetry_attribute("gen_ai.response.finish_reason", "error" if has_error else "stop")
                return {"status": "error" if has_error else "ok", "results": results}

            if not method:
                cache = ctx.engine.mcp_cache
                if kind == "prompt":
                    prompts = get_cached_prompts(cache, server)
                    if prompts is None:
                        prompts = await _try_list_prompts(session, server, ctx, timeout)
                        cache_prompts(cache, server, prompts)
                    methods = [p.name for p in prompts]
                else:
                    tools = get_cached_tools(cache, server)
                    if tools is None:
                        tools = await _maybe_timeout(session.list_tools_async(), timeout)
                        cache_tools(cache, server, tools)
                    methods = [t.name for t in tools]
                target = ", ".join(methods) if methods else "(auto)"
                ctx.set_telemetry_attribute("mcp.auto_discover", True)
                ctx.set_telemetry_attribute("mcp.methods_count", len(methods))
                if not methods:
                    ctx.set_telemetry_attribute("gen_ai.response.finish_reason", "stop")
                    return {"status": "ok", "results": []}
                batch = []
                has_error = False
                for m in methods:
                    item = await single(m, copy.deepcopy(request))
                    item["method"] = m
                    has_error = has_error or item.get("status") == "error"
                    batch.append(item)
                ctx.set_telemetry_attribute("gen_ai.response.finish_reason", "error" if has_error else "stop")
                return {"status": "error" if has_error else "ok", "results": batch}

            target = method
            ctx.set_telemetry_attribute("mcp.method.name", method)
            single_result = await single(method, request)
            ctx.set_telemetry_attribute("gen_ai.response.finish_reason", "error" if single_result.get("status") == "error" else "stop")
            return single_result
        except WorkflowRuntimeException:
            raise
        except TimeoutError as exc:
            raise WorkflowRuntimeException(ErrorCodes.MCP_TIMEOUT, f"mcp.call to '{server}/{target}' timed out after {timeout_ms}ms", retryable=True) from exc
        except Exception as exc:
            error_code = ErrorCodes.MCP_PROMPT_ERROR if kind == "prompt" else ErrorCodes.MCP_CALL_ERROR
            raise WorkflowRuntimeException(error_code, f"mcp.call ({kind}) to '{server}/{target}' failed: {exc}", retryable=False) from exc

    async def _call_single(
        self,
        ctx: StepExecutionContext,
        session: IMcpSession,
        kind: str,
        method: str,
        request_args: Any,
        timeout: float | None,
    ) -> dict[str, Any]:
        if kind == "prompt":
            op = session.get_prompt_async(method, request_args)
            result = await (asyncio.wait_for(op, timeout=timeout) if timeout is not None else op)
            _runtime._extract_usage_telemetry(ctx, getattr(result, "usage", None), getattr(result, "model", None))
            messages = [m.model_dump() for m in result.messages]
            text = "\n".join(f"[{m.role}] {m.content}" for m in result.messages)
            return {"status": "ok", "description": result.description, "messages": messages, "text": text}

        correlation = _build_mcp_correlation_context(ctx, session, kind, method)
        op = _call_tool_with_optional_meta(session, method, request_args, correlation.to_mcp_meta())
        result = await (asyncio.wait_for(op, timeout=timeout) if timeout is not None else op)
        _runtime._extract_usage_telemetry(ctx, result.usage, result.model)
        return {"status": "error" if result.is_error else "ok", "response": result.content}

    def _build_request_args(self, input_obj: dict[str, Any], ctx: StepExecutionContext) -> Any:
        if input_obj.get("request") is not None:
            return input_obj.get("request")

        request_template = input_obj.get("request_template")
        if request_template is None:
            return None

        template_data = input_obj.get("template_data")
        if not isinstance(template_data, dict):
            template_data = ctx.data

        try:
            rendered = MustacheEngine.render(str(request_template), template_data)
        except Exception as exc:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_SYNTAX, f"mcp.call request_template rendering failed: {exc}") from exc

        try:
            return json.loads(rendered)
        except Exception as exc:
            raise WorkflowRuntimeException(ErrorCodes.JSON_PARSE, f"mcp.call request_template rendered invalid JSON: {exc}") from exc


    async def _execute_llm_assisted(
        self,
        ctx: StepExecutionContext,
        session: IMcpSession,
        input_obj: dict[str, Any],
        default_kind: str,
        single_method: str | None,
        batch_methods: list[Any] | None,
        timeout: float | None,
    ) -> dict[str, Any]:
        llm_client = ctx.engine.llm_client
        if llm_client is None:
            raise WorkflowRuntimeException(ErrorCodes.LLM_NETWORK, "mcp.call prompt mode requires an LLM client")

        provider, model = ctx.engine.resolve_llm_target(input_obj.get("provider"), input_obj.get("model"))
        if not model:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.call prompt mode requires 'model' unless runtime default is configured")

        prompt = input_obj.get("prompt")
        if not isinstance(prompt, str) or not prompt.strip():
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.call prompt mode requires a non-empty 'prompt'")

        temperature = None
        if input_obj.get("temperature") is not None:
            temperature = float(input_obj["temperature"])

        allowed = None
        if batch_methods is not None:
            allowed = {str(m) for m in batch_methods}
        elif single_method:
            allowed = {single_method}

        capabilities: list[dict[str, Any]] = []
        used_names: set[str] = set()

        for node in input_obj.get("tools", []) if isinstance(input_obj.get("tools"), list) else []:
            if not isinstance(node, dict):
                continue
            name = node.get("name")
            if not isinstance(name, str) or (allowed is not None and name not in allowed):
                continue
            internal = _build_unique_internal_name(name, "tool", used_names)
            capabilities.append(
                {
                    "internal": internal,
                    "method": name,
                    "kind": "tool",
                    "tool": LLMTool(name=internal, description=node.get("description"), input_schema=node.get("input_schema") or node.get("inputSchema")),
                }
            )

        for node in input_obj.get("prompts", []) if isinstance(input_obj.get("prompts"), list) else []:
            if not isinstance(node, dict):
                continue
            name = node.get("name")
            if not isinstance(name, str) or (allowed is not None and name not in allowed):
                continue
            internal = _build_unique_internal_name(name, "prompt", used_names)
            capabilities.append(
                {
                    "internal": internal,
                    "method": name,
                    "kind": "prompt",
                    "tool": LLMTool(name=internal, description=node.get("description"), input_schema=_prompt_argument_schema(node.get("arguments"))),
                }
            )

        if not capabilities:
            cache = ctx.engine.mcp_cache
            server_name = getattr(session, "server_name", input_obj.get("server", ""))
            if default_kind == "prompt":
                prompts = get_cached_prompts(cache, server_name)
                if prompts is None:
                    prompts = await _try_list_prompts(session, server_name, ctx, timeout)
                    cache_prompts(cache, server_name, prompts)
                for p in prompts:
                    if allowed is not None and p.name not in allowed:
                        continue
                    internal = _build_unique_internal_name(p.name, "prompt", used_names)
                    capabilities.append(
                        {
                            "internal": internal,
                            "method": p.name,
                            "kind": "prompt",
                            "tool": LLMTool(name=internal, description=p.description, input_schema=_prompt_argument_schema(p.arguments)),
                        }
                    )
            else:
                tools = get_cached_tools(cache, server_name)
                if tools is None:
                    tools = await _maybe_timeout(session.list_tools_async(), timeout)
                    cache_tools(cache, server_name, tools)
                for t in tools:
                    if allowed is not None and t.name not in allowed:
                        continue
                    internal = _build_unique_internal_name(t.name, "tool", used_names)
                    capabilities.append(
                        {
                            "internal": internal,
                            "method": t.name,
                            "kind": "tool",
                            "tool": LLMTool(name=internal, description=t.description, input_schema=t.input_schema),
                        }
                    )

        if not capabilities:
            ctx.set_telemetry_attribute("gen_ai.system", provider or "default")
            ctx.set_telemetry_attribute("gen_ai.request.model", model)
            ctx.set_telemetry_attribute("gen_ai.response.finish_reason", "stop")
            return {"status": "ok", "selection_mode": "llm", "tool_calls": [], "results": [], "text": "No MCP capabilities available for selection."}

        ctx.set_telemetry_attribute("gen_ai.system", provider or "default")
        ctx.set_telemetry_attribute("gen_ai.request.model", model)
        ctx.set_telemetry_attribute("mcp.capabilities_count", len(capabilities))

        selection_prompt = _runtime._build_llm_selection_prompt(prompt)
        selection_request = ctx.engine.sanitize_llm_request(
            LLMRequest(
                provider=provider,
                model=model,
                prompt=selection_prompt,
                temperature=temperature,
                tools=[c["tool"] for c in capabilities],
            )
        )
        if selection_request.temperature is not None:
            ctx.set_telemetry_attribute("gen_ai.request.temperature", selection_request.temperature)
        if ctx.limits.log_step_content:
            ctx.add_telemetry_event(
                "gen_ai.content.prompt",
                [("gen_ai.prompt", selection_prompt), ("prompt.role", "user"), ("gnougo-flow.mcp.phase", "selection")],
            )

        llm_response = await ctx.engine.call_llm_async(selection_request)

        finish_reason = "tool_calls" if llm_response.tool_calls else "stop"
        ctx.set_telemetry_attribute("gen_ai.response.model", model)
        ctx.set_telemetry_attribute("gen_ai.response.finish_reason", finish_reason)
        _runtime._extract_usage_telemetry(ctx, llm_response.usage, model)

        if ctx.limits.log_step_content and (llm_response.text or llm_response.json_payload is not None):
            completion_payload = llm_response.text if llm_response.text else json.dumps(llm_response.json_payload, ensure_ascii=False)
            ctx.add_telemetry_event(
                "gen_ai.content.completion",
                [
                    ("gen_ai.completion", completion_payload),
                    ("completion.role", "assistant"),
                    ("completion.finish_reason", finish_reason),
                    ("gnougo-flow.mcp.phase", "selection"),
                ],
            )

        if not llm_response.tool_calls:
            err = ErrorCodes.MCP_PROMPT_ERROR if default_kind == "prompt" else ErrorCodes.MCP_CALL_ERROR
            raise WorkflowRuntimeException(err, "mcp.call prompt mode did not select any MCP tool or prompt")

        cap_map = {c["internal"]: c for c in capabilities}
        tool_calls_out: list[dict[str, Any]] = []
        results_out: list[dict[str, Any]] = []
        has_error = False

        for tc in llm_response.tool_calls:
            cap = cap_map.get(tc.name)
            if not cap:
                err = ErrorCodes.MCP_PROMPT_ERROR if default_kind == "prompt" else ErrorCodes.MCP_CALL_ERROR
                raise WorkflowRuntimeException(err, f"mcp.call prompt mode selected unknown MCP capability '{tc.name}'")

            result_item = await self._call_single(ctx, session, cap["kind"], cap["method"], tc.arguments, timeout)
            result_item["method"] = cap["method"]
            result_item["kind"] = cap["kind"]
            if tc.id:
                result_item["call_id"] = tc.id
            has_error = has_error or result_item.get("status") == "error"
            results_out.append(result_item)

            call_obj = {"name": cap["method"], "kind": cap["kind"], "arguments": tc.arguments}
            if tc.id:
                call_obj["id"] = tc.id
            tool_calls_out.append(call_obj)

        if len(llm_response.tool_calls) == 1:
            only = cap_map[llm_response.tool_calls[0].name]
            ctx.set_telemetry_attribute("mcp.method.name", only["method"])
            ctx.set_telemetry_attribute("mcp.kind", only["kind"])
        ctx.set_telemetry_attribute("mcp.methods_count", len(llm_response.tool_calls))

        response: dict[str, Any] = {
            "status": "error" if has_error else "ok",
            "selection_mode": "llm",
            "text": llm_response.text,
            "tool_calls": tool_calls_out,
            "results": results_out,
        }

        structured = input_obj.get("structured_output") if isinstance(input_obj.get("structured_output"), dict) else None
        schema = structured.get("schema_inline") or structured.get("schema_ref") if structured else None
        strict = bool(structured.get("strict")) if structured and "strict" in structured else None
        if schema is not None:
            finalize_prompt = _runtime._build_structured_post_process_prompt(prompt, tool_calls_out, results_out)
            if ctx.limits.log_step_content:
                ctx.add_telemetry_event(
                    "gen_ai.content.prompt",
                    [("gen_ai.prompt", finalize_prompt), ("prompt.role", "user"), ("gnougo-flow.mcp.phase", "finalize")],
                )

            finalize_request = ctx.engine.sanitize_llm_request(
                LLMRequest(
                    provider=provider,
                    model=model,
                    prompt=finalize_prompt,
                    temperature=temperature,
                    structured_output_schema=schema,
                    structured_output_strict=strict,
                )
            )
            finalize_response = await ctx.engine.call_llm_async(finalize_request)
            _runtime._extract_usage_telemetry(ctx, finalize_response.usage, model)

            structured_json = finalize_response.json_payload
            if structured_json is None and finalize_response.text:
                try:
                    structured_json = json.loads(finalize_response.text)
                except Exception:
                    structured_json = None

            if structured_json is None:
                raise WorkflowRuntimeException(
                    ErrorCodes.LLM_SCHEMA,
                    "mcp.call structured_output expected valid JSON but the LLM "
                    "returned an incompatible response",
                )

            response["selection_text"] = llm_response.text
            response["json"] = structured_json
            if finalize_response.text:
                response["text"] = finalize_response.text

        return response

def _build_mcp_correlation_context(
    ctx: StepExecutionContext,
    session: IMcpSession,
    kind: str,
    method: str,
) -> McpCorrelationContext:
    trace_context = _capture_current_trace_context(ctx)
    traceparent = trace_context.get("traceparent")
    parsed_trace_id, parsed_parent_span_id = _parse_traceparent(traceparent)
    trace_id = trace_context.get("trace_id") or parsed_trace_id
    span_id = trace_context.get("span_id") or parsed_parent_span_id

    return McpCorrelationContext(
        traceparent=traceparent,
        tracestate=trace_context.get("tracestate"),
        trace_id=trace_id,
        span_id=span_id,
        parent_span_id=trace_context.get("parent_span_id") or parsed_parent_span_id or span_id,
        correlation_id=ctx.limits.run_id or trace_id or uuid.uuid4().hex,
        run_id=ctx.limits.run_id,
        step_id=ctx.step.id,
        step_type=ctx.step.type,
        mcp_server=getattr(session, "server_name", None),
        mcp_method=method,
        mcp_kind=kind,
    )


def _capture_current_trace_context(ctx: StepExecutionContext) -> dict[str, str]:
    from_otel = _capture_opentelemetry_context()
    if from_otel:
        return from_otel

    span = ctx.telemetry_span
    values: dict[str, str] = {}
    for source_name, target_name in (
        ("traceparent", "traceparent"),
        ("tracestate", "tracestate"),
        ("trace_state", "tracestate"),
        ("trace_id", "trace_id"),
        ("span_id", "span_id"),
        ("parent_span_id", "parent_span_id"),
    ):
        value = getattr(span, source_name, None)
        if isinstance(value, str) and value.strip():
            values[target_name] = value

    attributes = getattr(span, "attributes", None)
    if isinstance(attributes, dict):
        for source_name, target_name in (
            ("traceparent", "traceparent"),
            ("tracestate", "tracestate"),
            ("trace_id", "trace_id"),
            ("span_id", "span_id"),
            ("parent_span_id", "parent_span_id"),
        ):
            value = attributes.get(source_name)
            if isinstance(value, str) and value.strip():
                values.setdefault(target_name, value)

    values.setdefault("traceparent", os.environ.get("TRACEPARENT") or os.environ.get("GNouGo__TraceParent") or "")
    values.setdefault("tracestate", os.environ.get("TRACESTATE") or os.environ.get("GNouGo__TraceState") or "")
    values.setdefault("trace_id", os.environ.get("GNouGo__TraceId") or "")
    values.setdefault("span_id", os.environ.get("GNouGo__SpanId") or "")
    return {key: value for key, value in values.items() if value}


def _capture_opentelemetry_context() -> dict[str, str]:
    try:
        from opentelemetry import trace  # type: ignore[import-not-found]
        from opentelemetry.trace import INVALID_SPAN_CONTEXT  # type: ignore[import-not-found]
    except Exception:
        return {}

    try:
        span = trace.get_current_span()
        span_context = span.get_span_context()
        if span_context is INVALID_SPAN_CONTEXT or not getattr(span_context, "is_valid", False):
            return {}

        trace_id = f"{span_context.trace_id:032x}"
        span_id = f"{span_context.span_id:016x}"
        flags = int(getattr(span_context, "trace_flags", 0)) & 0xFF
        values = {
            "traceparent": f"00-{trace_id}-{span_id}-{flags:02x}",
            "trace_id": trace_id,
            "span_id": span_id,
        }
        trace_state = str(getattr(span_context, "trace_state", "") or "")
        if trace_state:
            values["tracestate"] = trace_state
        parent = getattr(span, "parent", None)
        parent_span_id = getattr(parent, "span_id", None)
        if isinstance(parent_span_id, int) and parent_span_id:
            values["parent_span_id"] = f"{parent_span_id:016x}"
        return values
    except Exception:
        return {}


def _parse_traceparent(traceparent: str | None) -> tuple[str | None, str | None]:
    if not traceparent:
        return None, None
    parts = traceparent.split("-")
    if len(parts) < 4:
        return None, None
    return parts[1], parts[2]


async def _call_tool_with_optional_meta(
    session: IMcpSession,
    method: str,
    request_args: Any,
    mcp_meta: dict[str, Any],
) -> Any:
    call_tool = session.call_tool_async
    try:
        parameters = inspect.signature(call_tool).parameters
        if any(p.kind == inspect.Parameter.VAR_KEYWORD for p in parameters.values()) or "mcp_meta" in parameters:
            return await call_tool(method, request_args, mcp_meta=mcp_meta)
    except (TypeError, ValueError):
        pass

    try:
        return await call_tool(method, request_args, mcp_meta)
    except TypeError:
        return await call_tool(method, request_args)


def _add_if_present(values: dict[str, Any], key: str, value: Any) -> None:
    if value is not None and str(value).strip():
        values[key] = value

