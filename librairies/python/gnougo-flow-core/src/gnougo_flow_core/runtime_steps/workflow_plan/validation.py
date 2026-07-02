from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanValidationMixin:
    @staticmethod
    def _parse_and_validate_generated_workflow(yaml_text: str) -> WorkflowDocument:
        try:
            doc = WorkflowParser.parse(yaml_text)
        except Exception as exc:
            details = build_exception_details(
                infer_plan_error_code(str(exc)),
                "parse",
                str(exc),
                exc,
            )
            raise WorkflowRuntimeException(
                ErrorCodes.TEMPLATE_PLAN,
                f"Generated workflow parse failed: {exc} | repair diagnostics: {to_prompt_json(details)}",
                details=details,
            ) from exc

        if not doc.workflows:
            details = build_exception_details(
                "MISSING_ROOT_KEY_WORKFLOWS",
                "validation",
                "required root key 'workflows' must be a non-empty object.",
            )
            raise WorkflowRuntimeException(
                ErrorCodes.TEMPLATE_PLAN,
                "Validation failed: required root key 'workflows' must be a non-empty object. | repair diagnostics: "
                + to_prompt_json(details),
                details=details,
            )

        return doc


    @classmethod
    def _validate_generated_workflow(cls, doc: WorkflowDocument) -> None:
        errors = WorkflowValidator().validate(doc)
        if not errors:
            return
        details = build_validation_failure_details(errors, None, None)
        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            "Generated workflow validation failed: "
            + cls._format_validation_errors(errors)
            + " | repair diagnostics: "
            + to_prompt_json(details),
            details=details,
        )


    @classmethod
    def _validate_generated_workflow_for_plan(
        cls,
        doc: WorkflowDocument,
        mcp_tool_contracts: list[McpToolOutputContract],
        mcp_server_metadata: list[McpServerMetadata] | None = None,
    ) -> int:
        errors = WorkflowValidator().validate(doc)
        semantic_exception: WorkflowSemanticValidationException | None = None
        compilation_exception: Exception | None = None
        normalization_count = 0
        mcp_coverage_exception: Exception | None = None

        try:
            normalization_count = normalize_mcp_call_input_requests(doc, mcp_tool_contracts)
            validate_workflow_semantics(doc, mcp_tool_contracts)
        except WorkflowSemanticValidationException as exc:
            semantic_exception = exc

        try:
            cls._validate_mcp_discovery_coverage(doc, mcp_tool_contracts, mcp_server_metadata or [])
        except Exception as exc:
            mcp_coverage_exception = exc

        if not any(cls._is_fatal_compiler_validation_error(error) for error in errors):
            try:
                WorkflowCompiler().compile(doc)
            except Exception as exc:
                compilation_exception = exc

        if not errors and semantic_exception is None and compilation_exception is None and mcp_coverage_exception is None:
            return normalization_count

        if not errors and semantic_exception is None and compilation_exception is None and isinstance(mcp_coverage_exception, WorkflowRuntimeException):
            raise mcp_coverage_exception

        diagnostics: list[str] = []
        if errors:
            diagnostics.append("workflow validation: " + cls._format_validation_errors(errors))
        if semantic_exception is not None:
            diagnostics.append("semantic validation: " + str(semantic_exception))
        if mcp_coverage_exception is not None:
            diagnostics.append("mcp discovery: " + str(mcp_coverage_exception))
        if compilation_exception is not None:
            diagnostics.append("compilation: " + str(compilation_exception))

        details = build_validation_failure_details(errors, semantic_exception, compilation_exception)
        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            "Generated workflow validation failed: "
            + " | ".join(diagnostics)
            + " | repair diagnostics: "
            + to_prompt_json(details),
            details=details,
        )


    @staticmethod
    def _dump_workflow_yaml(doc: WorkflowDocument) -> str:
        data = doc.model_dump(by_alias=True, exclude_none=True, exclude={"raw_yaml"})
        return yaml.dump(data, Dumper=_NoAliasDumper, sort_keys=False, allow_unicode=False).strip()


    @classmethod
    def _validate_mcp_discovery_coverage(
        cls,
        doc: WorkflowDocument,
        mcp_tool_contracts: list[McpToolOutputContract],
        mcp_server_metadata: list[McpServerMetadata],
    ) -> None:
        tool_calls = [
            step
            for workflow in doc.workflows.values()
            for step in cls._enumerate_steps(workflow.steps)
            if step.type == "mcp.call"
            and isinstance(step.input, dict)
            and str(step.input.get("kind", "tool")).lower() != "prompt"
        ]
        if not tool_calls:
            return

        if not mcp_tool_contracts:
            cls._raise_mcp_discovery_coverage_error(
                "MCP_DISCOVERY_REQUIRED",
                "generated workflow contains mcp.call tool steps, but no MCP tool catalog was discovered. Validation is fail-closed.",
                "Run MCP discovery for this plan, remove mcp.call steps, or add an mcp.list discovery step before tool execution.",
            )

        known_servers = {contract.server_name for contract in mcp_tool_contracts}
        configured_servers = {meta.name for meta in mcp_server_metadata if getattr(meta, "name", None)}
        for step in tool_calls:
            server_name = step.input.get("server")
            if not isinstance(server_name, str) or not server_name.strip() or "${" in server_name:
                cls._raise_mcp_discovery_coverage_error(
                    "MCP_SERVER_DYNAMIC_UNVERIFIABLE",
                    f"mcp.call step '{step.id}' must use a literal discovered server name during workflow.plan validation.",
                    "Use an exact server name from the discovered MCP catalog in input.server.",
                )
            if server_name not in known_servers:
                if configured_servers and server_name in configured_servers:
                    cls._raise_mcp_discovery_coverage_error(
                        "MCP_TOOL_CATALOG_EMPTY",
                        f"MCP server '{server_name}' is referenced by step '{step.id}', but its discovered tool catalog is empty.",
                        "Remove the mcp.call or select a discovered server with tools.",
                    )
                cls._raise_mcp_discovery_coverage_error(
                    "MCP_SERVER_UNKNOWN",
                    f"mcp.call step '{step.id}' references server '{server_name}', which is absent from the discovered MCP catalog.",
                    "Change input.server to one of the discovered server names, or do not generate this mcp.call.",
                )


    @staticmethod
    def _raise_mcp_discovery_coverage_error(code: str, message: str, hint: str) -> None:
        details = build_mcp_discovery_coverage_details(code, message, hint)
        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            f"{code}: {message} | repair diagnostics: {to_prompt_json(details)}",
            details=details,
        )


    @classmethod
    def _enumerate_steps(cls, steps: list[StepDef] | None) -> list[StepDef]:
        found: list[StepDef] = []
        for step in steps or []:
            found.append(step)
            found.extend(cls._enumerate_steps(step.steps))
            if step.branches:
                for branch in step.branches:
                    found.extend(cls._enumerate_steps(branch.steps))
            if step.cases:
                for case in step.cases:
                    found.extend(cls._enumerate_steps(case.steps))
            found.extend(cls._enumerate_steps(step.default))
        return found


    @staticmethod
    def _is_fatal_compiler_validation_error(error: ValidationError) -> bool:
        return error.code in {
            ErrorCodes.EXPR_PARSE,
            "DSL_VERSION",
            "NO_WORKFLOWS",
            ErrorCodes.WORKFLOW_CYCLE_DETECTED,
            "INVALID_ENTRYPOINT",
        }


    @staticmethod
    def _format_validation_errors(errors: list[ValidationError]) -> str:
        return format_validation_errors(errors)


    @staticmethod
    def _validate_generated_workflow_semantics(
        doc: WorkflowDocument,
        mcp_tool_contracts: list[McpToolOutputContract],
    ) -> None:
        try:
            validate_workflow_semantics(doc, mcp_tool_contracts)
        except WorkflowSemanticValidationException as exc:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, str(exc)) from exc
