from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanMcpPrefilterMixin:
    @staticmethod
    def _should_prefilter(generator: dict[str, Any]) -> bool:
        prefilter = generator.get("prefilter")
        return prefilter is None or isinstance(prefilter, dict) or bool(prefilter)


    @staticmethod
    def _prefilter_target(generator: dict[str, Any]) -> tuple[str | None, str | None, float | None]:
        prefilter = generator.get("prefilter")
        provider = None
        model = None
        temperature = None
        if isinstance(prefilter, dict):
            provider = prefilter.get("provider")
            model = prefilter.get("model")
            if prefilter.get("temperature") is not None:
                temperature = float(prefilter["temperature"])
        model = model or generator.get("model")
        provider = provider or generator.get("provider")
        return provider, model, temperature


    async def _maybe_prefilter_mcp_server_metadata(
        self,
        ctx: StepExecutionContext,
        generator: dict[str, Any],
        instruction: str,
        context_text: str,
        plan_reasoning: str | None = None,
    ) -> list[McpServerMetadata] | None:
        if not self._should_prefilter(generator):
            return None

        factory = ctx.engine.mcp_client_factory
        llm_client = ctx.engine.llm_client
        if factory is None or llm_client is None:
            return None

        server_meta = list(getattr(factory, "server_metadata", []) or [])
        if not server_meta:
            return None

        provider, model, temperature = self._prefilter_target(generator)
        if not model:
            return None

        catalog = "\n".join(f"- {getattr(meta, 'name', str(meta))}: {getattr(meta, 'description', None) or '(no description)'}" for meta in server_meta)
        prompt = (
            "You are an MCP server-selection assistant. Given a task description and a catalog of MCP server "
            "descriptions, select only the servers likely to contain relevant capabilities.\n\n"
            "Return ONLY a JSON object matching this shape: "
            '{"servers":[{"name":"server-name","reason":"short relevance reason"}]}.\n\n'
            "Rules:\n"
            "- Use only exact server names from the catalog.\n"
            "- Base the decision only on server descriptions, not on imagined tools.\n"
            "- Include every plausibly relevant server; exclude clearly unrelated servers.\n"
            '- If no server is relevant, return {"servers": []}.\n\n'
            f"<server_catalog>\n{catalog}\n</server_catalog>\n\n"
            f"{self._build_user_task_block(instruction, context_text)}"
        )

        prefilter_span = ctx.begin_telemetry_span(
            "workflow.plan.mcp_server_prefilter",
            "mcp_server_prefilter",
            [
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.system", provider or "unknown"),
                ("gen_ai.request.model", model),
                ("mcp.servers_total", len(server_meta)),
            ],
        )
        try:
            prefilter_span.__enter__()
            ctx.add_telemetry_event(
                "gnougo-flow.step.thinking",
                [
                    (
                        "gnougo-flow.thinking.message",
                        f"Pre-filtering MCP server descriptions with {model} ({len(server_meta)} server(s))…",
                    ),
                    ("gnougo-flow.thinking.level", "thinking"),
                ],
            )
            ctx.add_telemetry_event(
                "gnougo-flow.plan.prefilter.servers.start",
                [
                    ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.system", provider or "unknown"),
                    ("gen_ai.request.model", model),
                    ("mcp.servers_total", len(server_meta)),
                ],
            )
            if ctx.limits.log_step_content:
                prefilter_span.add_event(
                    "gen_ai.content.prompt",
                    [
                        ("gen_ai.prompt", prompt),
                        ("prompt.role", "user"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ],
                )
                ctx.add_telemetry_event(
                    "gen_ai.content.prompt",
                    [
                        ("gen_ai.prompt", prompt),
                        ("prompt.role", "user"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ],
                )

            response = await ctx.engine.call_llm_async(
                LLMRequest(
                    provider=provider,
                    model=str(model),
                    prompt=prompt,
                    temperature=temperature,
                    structured_output_schema={
                        "type": "object",
                        "properties": {
                            "servers": {
                                "type": "array",
                                "items": {
                                    "type": "object",
                                    "properties": {
                                        "name": {"type": "string"},
                                        "reason": {"type": "string"},
                                    },
                                    "required": ["name", "reason"],
                                    "additionalProperties": False,
                                },
                            }
                        },
                        "required": ["servers"],
                        "additionalProperties": False,
                    },
                    structured_output_strict=True,
                    reasoning=plan_reasoning,
                )
            )

            if ctx.limits.log_step_content and response.text:
                prefilter_span.add_event(
                    "gen_ai.content.completion",
                    [
                        ("gen_ai.completion", response.text),
                        ("completion.role", "assistant"),
                        ("completion.finish_reason", "stop"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ],
                )
                ctx.add_telemetry_event(
                    "gen_ai.content.completion",
                    [
                        ("gen_ai.completion", response.text),
                        ("completion.role", "assistant"),
                        ("completion.finish_reason", "stop"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ],
                )
            self._add_usage_attributes(prefilter_span, response.usage)
            self._add_prefilter_usage_event(ctx, response.usage, str(model), provider, "mcp_server_prefilter", "gnougo-flow.plan.prefilter.servers.usage")

            payload = response.json_payload
            if not isinstance(payload, dict) and response.text:
                payload = json.loads(self._strip_markdown_code_fence(response.text))
            if not isinstance(payload, dict) or not isinstance(payload.get("servers"), list):
                raise ValueError("MCP server prefilter response did not contain a servers array")

            by_name = {str(getattr(meta, "name", "")).lower(): meta for meta in server_meta}
            selected: list[McpServerMetadata] = []
            seen: set[str] = set()
            for item in payload["servers"]:
                name = item.get("name") if isinstance(item, dict) else item
                if not isinstance(name, str):
                    continue
                key = name.lower()
                if key in by_name and key not in seen:
                    selected.append(by_name[key])
                    seen.add(key)

            ctx.add_telemetry_event(
                "gnougo-flow.plan.prefilter.servers.result",
                [
                    ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.request.model", model),
                    ("mcp.servers_total", len(server_meta)),
                    ("mcp.servers_selected", len(selected)),
                    ("mcp.server.names", ",".join(str(meta.name) for meta in selected)),
                ],
            )
            prefilter_span.set_attribute("mcp.servers_selected", len(selected))
            prefilter_span.set_attribute("mcp.server.names", ",".join(str(meta.name) for meta in selected))
            ctx.add_telemetry_event(
                "gnougo-flow.step.thinking",
                [
                    (
                        "gnougo-flow.thinking.message",
                        f"MCP server pre-filter: {len(selected)}/{len(server_meta)} server(s) selected before discovery.",
                    ),
                    ("gnougo-flow.thinking.level", "info"),
                ],
            )
            prefilter_span.end()
            return selected
        except Exception as exc:
            prefilter_span.fail(exc)
            ctx.add_telemetry_event(
                "gnougo-flow.plan.prefilter.servers.fallback",
                [
                    ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.request.model", model),
                    ("error.type", type(exc).__name__),
                    ("mcp.servers_total", len(server_meta)),
                ],
            )
            ctx.add_telemetry_event(
                "gnougo-flow.step.thinking",
                [
                    (
                        "gnougo-flow.thinking.message",
                        f"MCP server pre-filter failed ({exc}), discovering all {len(server_meta)} server(s).",
                    ),
                    ("gnougo-flow.thinking.level", "info"),
                ],
            )
            prefilter_span.end()
            return server_meta
