from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanMcpDiscoveryMixin:
    @staticmethod
    def _get_configured_mcp_server_metadata(ctx: StepExecutionContext) -> list[McpServerMetadata]:
        factory = ctx.engine.mcp_client_factory
        if factory is None:
            return []
        return list(getattr(factory, "server_metadata", []) or [])


    @staticmethod
    def _extract_required_mcp_server_names(
        instruction: str,
        context_text: str | None,
        configured_servers: list[McpServerMetadata],
    ) -> set[str]:
        by_lower = {str(meta.name).lower(): str(meta.name) for meta in configured_servers if meta.name}
        if not by_lower:
            return set()

        candidates: set[str] = set()
        combined = "\n".join(part for part in (instruction, context_text or "") if part)
        for match in re.findall(r"(?im)^\s*server\s*:\s*['\"]?([^'\"\r\n#]+)", combined):
            candidates.add(match.strip().strip(",] "))
        for match in re.findall(r"(?im)^\s*servers\s*:\s*\[([^\]\r\n]+)\]", combined):
            for item in match.split(","):
                candidates.add(item.strip().strip("'\" "))

        required: set[str] = set()
        for candidate in candidates:
            configured_name = by_lower.get(candidate.lower())
            if configured_name:
                required.add(configured_name)
        return required


    @staticmethod
    def _include_required_mcp_servers(
        selected_servers: list[McpServerMetadata] | None,
        configured_servers: list[McpServerMetadata],
        required_server_names: set[str],
    ) -> list[McpServerMetadata] | None:
        if selected_servers is None or not required_server_names:
            return selected_servers

        selected = list(selected_servers)
        selected_names = {str(meta.name).lower() for meta in selected if meta.name}
        configured_by_lower = {str(meta.name).lower(): meta for meta in configured_servers if meta.name}
        for server_name in sorted(required_server_names):
            key = server_name.lower()
            if key not in selected_names and key in configured_by_lower:
                selected.append(configured_by_lower[key])
                selected_names.add(key)
        return selected


    async def _collect_mcp_tool_contracts(
        self,
        ctx: StepExecutionContext,
        server_metadata: list[McpServerMetadata],
    ) -> list[McpToolOutputContract]:
        factory = ctx.engine.mcp_client_factory
        if factory is None or not server_metadata:
            return []

        contracts: list[McpToolOutputContract] = []
        for meta in server_metadata:
            name = getattr(meta, "name", None) or str(meta)
            try:
                _session, tools, _prompts, _resources = await self._discover_mcp_capabilities_with_retry(factory, str(name), ctx.engine.mcp_cache)
            except Exception:
                continue

            for tool in tools:
                tool_name = self._get_mcp_field(tool, "name")
                if not tool_name:
                    continue
                contracts.append(
                    McpToolOutputContract(
                        server_name=str(name),
                        tool_name=str(tool_name),
                        description=self._get_mcp_field(tool, "description"),
                        input_schema=copy.deepcopy(self._get_mcp_field(tool, "input_schema") or self._get_mcp_field(tool, "inputSchema")),
                        output_schema=copy.deepcopy(self._get_mcp_field(tool, "output_schema") or self._get_mcp_field(tool, "outputSchema")),
                        example_response=copy.deepcopy(self._get_mcp_field(tool, "example_response") or self._get_mcp_field(tool, "exampleResponse")),
                    )
                )
        return contracts


    @staticmethod
    async def _discover_mcp_capabilities_with_retry(factory: Any, server_name: str, cache: Any = None) -> tuple[Any, list[Any], list[Any], list[Any]]:
        cached_tools = get_cached_tools(cache, server_name)
        cached_prompts = get_cached_prompts(cache, server_name)
        cached_resources = get_cached_resources(cache, server_name)
        if cached_tools is not None and cached_prompts is not None and cached_resources is not None:
            return None, list(cached_tools), list(cached_prompts), list(cached_resources)

        last_error: Exception | None = None
        for attempt in range(1, _MCP_DISCOVERY_MAX_ATTEMPTS + 1):
            try:
                session = await factory.get_client_async(server_name)
                if cached_tools is None:
                    tools = list(await session.list_tools_async())
                    cache_tools(cache, server_name, tools)
                else:
                    tools = list(cached_tools)

                if cached_prompts is None:
                    prompts = list(await session.list_prompts_async())
                    cache_prompts(cache, server_name, prompts)
                else:
                    prompts = list(cached_prompts)

                if cached_resources is None:
                    try:
                        resources = list(await session.list_resources_async())
                    except Exception:
                        resources = []
                    cache_resources(cache, server_name, resources)
                else:
                    resources = list(cached_resources)
                return session, tools, prompts, resources
            except Exception as exc:
                last_error = exc
                if attempt >= _MCP_DISCOVERY_MAX_ATTEMPTS:
                    break
                await asyncio.sleep(_MCP_DISCOVERY_RETRY_BASE_DELAY_SECONDS * attempt)
        raise last_error or RuntimeError(f"MCP discovery failed for server '{server_name}'")


    @staticmethod
    def _find_mcp_server_metadata(
        configured_servers: list[McpServerMetadata],
        server_name: str,
    ) -> McpServerMetadata | None:
        for meta in configured_servers:
            if meta.name and meta.name.lower() == server_name.lower():
                return meta
        return None


    @staticmethod
    def _try_extract_missing_mcp_server_name(error_message: str) -> str | None:
        match = re.search(r"MCP server '([^']+)' not found", error_message, flags=re.IGNORECASE)
        return match.group(1) if match else None


    async def _maybe_prefilter_mcp_documentation(
        self,
        ctx: StepExecutionContext,
        generator: dict[str, Any],
        instruction: str,
        context_text: str,
        mcp_doc: str,
        plan_reasoning: str | None = None,
    ) -> str:
        if not self._should_prefilter(generator):
            return mcp_doc

        llm_client = ctx.engine.llm_client
        if llm_client is None:
            return mcp_doc

        provider, model, temperature = self._prefilter_target(generator)
        if not model:
            return mcp_doc

        prompt = (
            "Select only MCP servers/capabilities relevant to the task.\n"
            'Return JSON object {"filtered":"..."} where filtered is a plain-text subset.\n\n'
            "When keeping a capability with input_schema, output_schema, or example_response, copy the complete "
            "`*_json` line and any continuation lines verbatim. Do not summarize, rewrite, or truncate schema blocks/descriptions.\n"
            "If preserving the selected schema lines exactly is uncertain, return the full relevant server section.\n\n"
            f"{self._build_user_task_block(instruction, context_text)}\n\n"
            f"<available_mcp>\n{mcp_doc}\n</available_mcp>\n"
        )

        prefilter_span = ctx.begin_telemetry_span(
            "workflow.plan.mcp_capability_prefilter",
            "mcp_capability_prefilter",
            [
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.system", provider or "unknown"),
                ("gen_ai.request.model", model),
                ("mcp.documentation.input_length", len(mcp_doc)),
            ],
        )
        try:
            prefilter_span.__enter__()
            ctx.add_telemetry_event(
                "gnougo-flow.plan.prefilter.capabilities.start",
                [
                    ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.system", provider or "unknown"),
                    ("gen_ai.request.model", model),
                ],
            )
            if ctx.limits.log_step_content:
                prefilter_span.add_event(
                    "gen_ai.content.prompt",
                    [
                        ("gen_ai.prompt", prompt),
                        ("prompt.role", "user"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                    ],
                )
                ctx.add_telemetry_event(
                    "gen_ai.content.prompt",
                    [
                        ("gen_ai.prompt", prompt),
                        ("prompt.role", "user"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                    ],
                )
            response = await ctx.engine.call_llm_async(
                LLMRequest(
                    provider=provider,
                    model=model,
                    prompt=prompt,
                    temperature=temperature,
                    structured_output_schema={
                        "type": "object",
                        "properties": {"filtered": {"type": "string"}},
                        "required": ["filtered"],
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
                        ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                    ],
                )
                ctx.add_telemetry_event(
                    "gen_ai.content.completion",
                    [
                        ("gen_ai.completion", response.text),
                        ("completion.role", "assistant"),
                        ("completion.finish_reason", "stop"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                    ],
                )
            self._add_usage_attributes(prefilter_span, response.usage)
            self._add_prefilter_usage_event(
                ctx, response.usage, str(model), provider, "mcp_capability_prefilter", "gnougo-flow.plan.prefilter.capabilities.usage"
            )
            if isinstance(response.json_payload, dict) and isinstance(response.json_payload.get("filtered"), str):
                filtered = response.json_payload["filtered"]
                if not self._filtered_mcp_doc_preserves_schema_blocks(mcp_doc, filtered):
                    filtered = mcp_doc
                prefilter_span.set_attribute("mcp.documentation.filtered_length", len(filtered))
                ctx.add_telemetry_event(
                    "gnougo-flow.plan.prefilter.capabilities.result",
                    [
                        ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                        ("gen_ai.operation.name", "chat"),
                        ("gen_ai.request.model", model),
                        ("mcp.documentation.filtered_length", len(filtered)),
                    ],
                )
                prefilter_span.end()
                return filtered
            if response.text:
                parsed = json.loads(response.text)
                if isinstance(parsed, dict) and isinstance(parsed.get("filtered"), str):
                    filtered = parsed["filtered"]
                    if not self._filtered_mcp_doc_preserves_schema_blocks(mcp_doc, filtered):
                        filtered = mcp_doc
                    prefilter_span.set_attribute("mcp.documentation.filtered_length", len(filtered))
                    ctx.add_telemetry_event(
                        "gnougo-flow.plan.prefilter.capabilities.result",
                        [
                            ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                            ("gen_ai.operation.name", "chat"),
                            ("gen_ai.request.model", model),
                            ("mcp.documentation.filtered_length", len(filtered)),
                        ],
                    )
                    prefilter_span.end()
                    return filtered
        except Exception as exc:
            prefilter_span.fail(exc)
            ctx.add_telemetry_event(
                "gnougo-flow.plan.prefilter.capabilities.fallback",
                [
                    ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.request.model", model),
                    ("error.type", type(exc).__name__),
                ],
            )
            prefilter_span.end()
            return mcp_doc

        prefilter_span.end()
        return mcp_doc


    @staticmethod
    def _filtered_mcp_doc_preserves_schema_blocks(original: str, filtered: str) -> bool:
        if "_json:" not in original:
            return True
        if "_json:" not in filtered:
            return False
        return "…" not in filtered and "... schema truncated" not in filtered.lower()


    async def _build_mcp_documentation(
        self,
        ctx: StepExecutionContext,
        server_meta_override: list[McpServerMetadata] | None = None,
        mcp_tool_contracts: list[McpToolOutputContract] | None = None,
    ) -> str:
        factory = ctx.engine.mcp_client_factory
        if factory is None:
            return "No MCP client factory configured."

        server_meta = server_meta_override if server_meta_override is not None else (getattr(factory, "server_metadata", []) or [])
        if not server_meta:
            return "No MCP servers configured."

        with ctx.begin_telemetry_span("workflow.plan.mcp_discovery", "mcp_discovery", [("mcp.servers_total", len(server_meta))]) as discovery_span:
            sections: list[str] = []
            discovered_count = 0
            tools_total = 0
            prompts_total = 0
            resources_total = 0
            for meta in server_meta:
                name = getattr(meta, "name", None) or str(meta)
                desc = getattr(meta, "description", None) or ""
                section = [f"## Server: {name}", f"Description: {desc or '(none)'}"]
                try:
                    _session, tools, prompts, resources = await self._discover_mcp_capabilities_with_retry(factory, str(name), ctx.engine.mcp_cache)

                    discovered_count += 1
                    tools_total += len(tools)
                    prompts_total += len(prompts)
                    resources_total += len(resources)
                    section.append(f"Tools ({len(tools)}):")
                    for t in tools:
                        tool_name = self._get_mcp_field(t, "name")
                        tool_description = self._get_mcp_field(t, "description")
                        input_schema = self._get_mcp_field(t, "input_schema") or self._get_mcp_field(t, "inputSchema")
                        output_schema = self._get_mcp_field(t, "output_schema") or self._get_mcp_field(t, "outputSchema")
                        example_response = self._get_mcp_field(t, "example_response") or self._get_mcp_field(t, "exampleResponse")

                        section.append(f"- {tool_name}: {tool_description or '(no description)'}")
                        if input_schema is not None:
                            self._append_json_block(section, "  ", "input_schema", input_schema)
                        if output_schema is not None:
                            self._append_json_block(section, "  ", "output_schema", output_schema)
                        if example_response is not None:
                            self._append_json_block(section, "  ", "example_response", example_response)
                        if mcp_tool_contracts is not None and tool_name:
                            mcp_tool_contracts.append(
                                McpToolOutputContract(
                                    server_name=str(name),
                                    tool_name=str(tool_name),
                                    description=tool_description,
                                    input_schema=copy.deepcopy(input_schema),
                                    output_schema=copy.deepcopy(output_schema),
                                    example_response=copy.deepcopy(example_response),
                                )
                            )
                    section.append(f"Prompts ({len(prompts)}):")
                    for p in prompts:
                        section.append(f"- {p.name}: {p.description or '(no description)'}")
                    section.append(f"Resources ({len(resources)}):")
                    for r in resources:
                        section.append(f"- {r.name} ({r.uri}): {r.description or '(no description)'}")
                except Exception as exc:
                    section.append(f"- {name}: {desc or '(none)'}")
                    section.append("(tool discovery unavailable)")
                    section.append(f"Error while reading capabilities: {exc}")
                sections.append("\n".join(section))
            discovery_span.set_attribute("mcp.servers_discovered", discovered_count)
            discovery_span.set_attribute("mcp.tools_total", tools_total)
            discovery_span.set_attribute("mcp.prompts_total", prompts_total)
            discovery_span.set_attribute("mcp.resources_total", resources_total)
            return "\n\n".join(sections)


    @staticmethod
    def _get_mcp_field(value: Any, name: str) -> Any:
        if isinstance(value, dict):
            return value.get(name)
        return getattr(value, name, None)
