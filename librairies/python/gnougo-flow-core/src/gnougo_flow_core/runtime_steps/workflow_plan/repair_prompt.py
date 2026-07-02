from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanRepairPromptMixin:
    async def _build_repair_context_with_mcp_docs(
        self,
        ctx: StepExecutionContext,
        policy: dict[str, Any],
        invalid_yaml: str | None,
        exc: Exception,
        forced_mcp_server_names: set[str],
        mcp_tool_contracts: list[McpToolOutputContract] | None = None,
    ) -> str:
        context = self._build_minimal_repair_context(ctx, policy, invalid_yaml, exc, mcp_tool_contracts)
        missing_server_name = self._try_extract_missing_mcp_server_name(str(exc))
        if not missing_server_name:
            return context

        forced_mcp_server_names.add(missing_server_name)
        configured_servers = self._get_configured_mcp_server_metadata(ctx)
        missing_server = self._find_mcp_server_metadata(configured_servers, missing_server_name)
        if missing_server is None:
            available = ", ".join(meta.name for meta in configured_servers if meta.name) or "(none)"
            return f"{context}\n\nMCP server '{missing_server_name}' is not configured. Available MCP servers: {available}".rstrip()

        mcp_doc = await self._build_mcp_documentation(ctx, [missing_server], [])
        return f"{context}\n\nMCP server context required by the failed workflow:\n{mcp_doc}".rstrip()


    @classmethod
    def _build_minimal_repair_context(
        cls,
        ctx: StepExecutionContext,
        policy: dict[str, Any],
        invalid_yaml: str | None,
        exc: Exception,
        mcp_tool_contracts: list[McpToolOutputContract] | None = None,
    ) -> str:
        selected_types = cls._extract_repair_step_types(ctx, invalid_yaml, str(exc))
        if not selected_types:
            selected_types = cls._extract_known_step_types_from_yaml(ctx, invalid_yaml)

        allowed_raw = policy.get("allowed_step_types")
        allowed_types = {str(x) for x in allowed_raw} if isinstance(allowed_raw, list) else None
        if allowed_types is not None:
            selected_types &= allowed_types

        snippet_map = ctx.engine.registry.get_dsl_snippet_map(None)
        available_types = sorted(allowed_types or set(snippet_map.keys()))
        parts = ["", "Available step type names:", ", ".join(available_types)]

        snippets = ctx.engine.registry.get_dsl_snippets(selected_types) if selected_types else []
        if snippets:
            parts.extend(["", "DSL snippets for failed/referenced step types:", cls._remove_markdown_fence_lines("\n".join(snippets))])

        mcp_repair_context = cls._build_minimal_mcp_repair_context(
            ctx,
            invalid_yaml,
            selected_types,
            mcp_tool_contracts,
        )
        if mcp_repair_context:
            parts.extend(["", "MCP docs for failed/referenced calls:", mcp_repair_context])

        return "\n".join(parts).rstrip()


    @classmethod
    def _build_minimal_mcp_repair_context(
        cls,
        ctx: StepExecutionContext,
        invalid_yaml: str | None,
        selected_types: set[str],
        mcp_tool_contracts: list[McpToolOutputContract] | None,
    ) -> str | None:
        if "mcp.call" not in selected_types or not invalid_yaml or not invalid_yaml.strip():
            return None

        contracts = list(mcp_tool_contracts or [])
        if not contracts:
            return None

        try:
            doc = WorkflowParser.parse(invalid_yaml)
        except Exception:
            return None

        calls: list[tuple[str, str, list[str], Any]] = []
        for workflow in doc.workflows.values():
            for step in cls._enumerate_steps(workflow.steps):
                if step.type != "mcp.call" or not isinstance(step.input, dict):
                    continue
                server = step.input.get("server")
                if not isinstance(server, str) or not server.strip() or "${" in server:
                    continue
                methods: list[str] = []
                method = step.input.get("method")
                if isinstance(method, str) and method.strip() and "${" not in method:
                    methods.append(method.strip())
                raw_methods = step.input.get("methods")
                if isinstance(raw_methods, list):
                    for item in raw_methods:
                        if isinstance(item, str) and item.strip() and "${" not in item:
                            methods.append(item.strip())
                calls.append((step.id, server.strip(), methods, step.input.get("request")))

        if not calls:
            return None

        configured_metadata = {
            str(meta.name): meta
            for meta in cls._get_configured_mcp_server_metadata(ctx)
            if getattr(meta, "name", None)
        }
        contracts_by_server: dict[str, list[McpToolOutputContract]] = {}
        for contract in contracts:
            contracts_by_server.setdefault(contract.server_name, []).append(contract)

        available_servers = sorted(set(configured_metadata) | set(contracts_by_server))
        lines = [
            "Use exact MCP server and method names. Tool arguments must be nested under `input.request`.",
            "Correct direct tool call shape:",
            "  type: mcp.call",
            "  input: { server: <exact-server>, kind: tool, method: <exact-tool>, request: { ... } }",
            "For every listed input_schema_json, copy all required request properties into input.request with the exact schema name and scalar type.",
            "If a required numeric/integer/boolean MCP property is missing, add an explicit YAML scalar value; do not use an expression string, cast, empty value, fake value, or data.env fallback.",
            "When repairing one MCP call, re-check every MCP call in the YAML so earlier schema fixes are preserved.",
            f"Available MCP servers: {', '.join(available_servers) if available_servers else '(none)'}",
        ]

        seen: set[tuple[str, str, tuple[str, ...]]] = set()
        for step_id, server_name, methods, request in calls:
            key = (step_id, server_name, tuple(methods))
            if key in seen:
                continue
            seen.add(key)

            lines.append("")
            lines.append(f"- Step `{step_id}` references MCP server `{server_name}`.")
            server_description = getattr(configured_metadata.get(server_name), "description", None)
            if server_description:
                lines.append(f"  Server description: {server_description}")

            server_contracts = contracts_by_server.get(server_name, [])
            if not server_contracts:
                lines.append("  This server was not found in the discovered MCP tool catalog.")
                continue

            available_tools = [contract.tool_name for contract in server_contracts]
            lines.append(f"  Available tools on `{server_name}`: {', '.join(available_tools)}")

            unknown_methods = [method for method in methods if method not in available_tools]
            if unknown_methods:
                lines.append(f"  Unknown requested method(s): {', '.join(unknown_methods)}")
                selected_contracts = server_contracts
            elif methods:
                selected_contracts = [contract for contract in server_contracts if contract.tool_name in methods]
            else:
                lines.append("  No literal method was selected; use one exact tool name when the request schema is known.")
                selected_contracts = server_contracts

            if request is not None:
                lines.append("  invalid_request_yaml:")
                lines.extend("    " + line for line in cls._serialize_yaml_value(request).splitlines())

            for contract in selected_contracts[:12]:
                description = f": {contract.description}" if contract.description else ""
                lines.append(f"  - {contract.tool_name}{description}")
                if contract.input_schema is not None:
                    lines.append("    input_schema_json: " + cls._dump_json(contract.input_schema))
                if contract.output_schema is not None:
                    lines.append("    output_schema_json: " + cls._dump_json(contract.output_schema))
                if contract.example_response is not None:
                    lines.append("    example_response_json: " + cls._dump_json(contract.example_response))
            if len(selected_contracts) > 12:
                lines.append(f"  ... {len(selected_contracts) - 12} additional tool(s) omitted from repair context.")

        return "\n".join(lines).rstrip()


    @classmethod
    def _extract_repair_step_types(cls, ctx: StepExecutionContext, invalid_yaml: str | None, error_message: str) -> set[str]:
        lookup = cls._build_step_repair_lookup(invalid_yaml)
        selected: set[str] = set()

        for step_id in cls._extract_error_step_ids(error_message):
            info = lookup.get(step_id)
            if not info:
                continue
            step_type, ancestors = info
            if ctx.engine.registry.get(step_type) is not None:
                selected.add(step_type)
            for ancestor_type in ancestors:
                if ctx.engine.registry.get(ancestor_type) is not None:
                    selected.add(ancestor_type)

        for step_type in cls._extract_quoted_step_types(error_message):
            if ctx.engine.registry.get(step_type) is not None:
                selected.add(step_type)

        return selected


    @classmethod
    def _extract_known_step_types_from_yaml(cls, ctx: StepExecutionContext, invalid_yaml: str | None) -> set[str]:
        selected: set[str] = set()
        for step_type, _ancestors in cls._build_step_repair_lookup(invalid_yaml).values():
            if ctx.engine.registry.get(step_type) is not None:
                selected.add(step_type)
        return selected


    @staticmethod
    def _build_step_repair_lookup(invalid_yaml: str | None) -> dict[str, tuple[str, tuple[str, ...]]]:
        lookup: dict[str, tuple[str, tuple[str, ...]]] = {}
        if not invalid_yaml or not invalid_yaml.strip():
            return lookup
        try:
            doc = WorkflowParser.parse(invalid_yaml)
        except Exception:
            return lookup

        def visit(steps: list[StepDef] | None, ancestors: tuple[str, ...]) -> None:
            for step in steps or []:
                lookup[step.id] = (step.type, ancestors)
                child_ancestors = ancestors + (step.type,)
                visit(step.steps, child_ancestors)
                for branch in step.branches or []:
                    visit(branch.steps, child_ancestors)
                for case in step.cases or []:
                    visit(case.steps, child_ancestors)
                visit(step.default, child_ancestors)

        for workflow in doc.workflows.values():
            visit(workflow.steps, ())
        return lookup


    @staticmethod
    def _extract_error_step_ids(error_message: str) -> set[str]:
        ids: set[str] = set()
        ids.update(re.findall(r"step '([^']+)'", error_message, flags=re.IGNORECASE))
        ids.update(re.findall(r'"step":"([^"]+)"', error_message, flags=re.IGNORECASE))
        ids.update(re.findall(r"\bdata\.steps\.([A-Za-z_][A-Za-z0-9_-]*)", error_message, flags=re.IGNORECASE))
        return ids


    @staticmethod
    def _extract_quoted_step_types(error_message: str) -> set[str]:
        step_types: set[str] = set()
        step_types.update(re.findall(r"Step type '([^']+)'", error_message, flags=re.IGNORECASE))
        step_types.update(re.findall(r"type '([^']+)'", error_message, flags=re.IGNORECASE))
        return step_types


    @staticmethod
    def _build_structured_plan_error(exc: Exception) -> str:
        return build_structured_plan_error(exc)
