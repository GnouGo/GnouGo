from __future__ import annotations

import gnougo_flow_core.runtime as _runtime
from gnougo_flow_core.runtime import *  # noqa: F401,F403

class McpCallExecutor:
    step_type = "mcp.call"
    step_description = "Call MCP tool/prompt directly or via LLM-assisted selection."
    dsl_snippet = """
### mcp.call - Execute MCP tool or prompt
```yaml
- id: browse
  type: mcp.call
  input:
    server: GnOuGo.Browser.Mcp
    kind: tool
    method: navigate
    request:
      url: "https://slimfaas.dev"

- id: smart_call
  type: mcp.call
  input:
    server: GnOuGo.Browser.Mcp
    model: gpt-4o-mini
    prompt: "Pick and call the best MCP capability for this task"
    tools: "${data.steps.discover_mcp.tools}"
```
Output: single `{ status, response }`, batch `{ status, results }`, or assisted `{ status, selection_mode, text, tool_calls, results, json? }`.
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

        method = input_obj.get("method")
        methods = input_obj.get("methods") if isinstance(input_obj.get("methods"), list) else None
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

        session = await factory.get_client_async(server)

        if has_prompt_selection:
            return await self._execute_llm_assisted(ctx, session, input_obj, kind, method if isinstance(method, str) else None, methods, timeout)

        async def single(call_method: str) -> dict[str, Any]:
            return await self._call_single(ctx, session, kind, call_method, request, timeout)

        if isinstance(methods, list) and methods:
            results = []
            has_error = False
            for m in methods:
                item = await single(str(m))
                item["method"] = str(m)
                has_error = has_error or item.get("status") == "error"
                results.append(item)
            ctx.set_telemetry_attribute("mcp.methods_count", len(methods))
            ctx.set_telemetry_attribute("gen_ai.response.finish_reason", "error" if has_error else "stop")
            return {"status": "error" if has_error else "ok", "results": results}

        if not isinstance(method, str) or not method:
            if kind == "prompt":
                methods = [p.name for p in await session.list_prompts_async()]
            else:
                methods = [t.name for t in await session.list_tools_async()]
            batch = []
            has_error = False
            for m in methods:
                item = await single(m)
                item["method"] = m
                has_error = has_error or item.get("status") == "error"
                batch.append(item)
            ctx.set_telemetry_attribute("mcp.auto_discover", True)
            ctx.set_telemetry_attribute("mcp.methods_count", len(methods))
            ctx.set_telemetry_attribute("gen_ai.response.finish_reason", "error" if has_error else "stop")
            return {"status": "error" if has_error else "ok", "results": batch}

        ctx.set_telemetry_attribute("mcp.method.name", method)
        single_result = await single(method)
        ctx.set_telemetry_attribute("gen_ai.response.finish_reason", "error" if single_result.get("status") == "error" else "stop")
        return single_result

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

        op = session.call_tool_async(method, request_args)
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
            internal = name if name not in used_names else f"tool:{name}"
            used_names.add(internal)
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
            internal = name if name not in used_names else f"prompt:{name}"
            used_names.add(internal)
            capabilities.append(
                {
                    "internal": internal,
                    "method": name,
                    "kind": "prompt",
                    "tool": LLMTool(name=internal, description=node.get("description"), input_schema={"type": "object"}),
                }
            )

        if not capabilities:
            if default_kind == "prompt":
                prompts = await session.list_prompts_async()
                for p in prompts:
                    if allowed is not None and p.name not in allowed:
                        continue
                    internal = p.name if p.name not in used_names else f"prompt:{p.name}"
                    used_names.add(internal)
                    capabilities.append(
                        {
                            "internal": internal,
                            "method": p.name,
                            "kind": "prompt",
                            "tool": LLMTool(name=internal, description=p.description, input_schema={"type": "object"}),
                        }
                    )
            else:
                tools = await session.list_tools_async()
                for t in tools:
                    if allowed is not None and t.name not in allowed:
                        continue
                    internal = t.name if t.name not in used_names else f"tool:{t.name}"
                    used_names.add(internal)
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
        if temperature is not None:
            ctx.set_telemetry_attribute("gen_ai.request.temperature", temperature)
        ctx.set_telemetry_attribute("mcp.capabilities_count", len(capabilities))

        selection_prompt = _runtime._build_llm_selection_prompt(prompt)
        if ctx.limits.log_step_content:
            ctx.add_telemetry_event(
                "gen_ai.content.prompt",
                [("gen_ai.prompt", selection_prompt), ("prompt.role", "user"), ("gnougo-flow.mcp.phase", "selection")],
            )

        llm_response = await llm_client.call_async(
            LLMRequest(
                provider=provider,
                model=model,
                prompt=selection_prompt,
                temperature=temperature,
                tools=[c["tool"] for c in capabilities],
            )
        )

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

            finalize_response = await llm_client.call_async(
                LLMRequest(
                    provider=provider,
                    model=model,
                    prompt=finalize_prompt,
                    temperature=temperature,
                    structured_output_schema=schema,
                    structured_output_strict=strict,
                )
            )
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
                    "mcp.call structured_output expected valid JSON but the LLM returned an incompatible response",
                )

            response["selection_text"] = llm_response.text
            response["json"] = structured_json
            if finalize_response.text:
                response["text"] = finalize_response.text

        return response
