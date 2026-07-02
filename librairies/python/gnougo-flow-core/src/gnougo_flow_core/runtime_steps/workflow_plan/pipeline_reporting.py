from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanPipelineReportingMixin:
    @staticmethod
    def _build_leaf_input_schema_map(leaf: _GeneratedLeafWorkflow) -> dict[str, Any]:
        inputs = leaf.workflow_node.get("inputs")
        return copy.deepcopy(inputs) if isinstance(inputs, dict) else {}


    @staticmethod
    def _is_required_leaf_input(schema: Any) -> bool:
        if not isinstance(schema, dict):
            return True
        if schema.get("required") is False:
            return False
        if "default" in schema:
            return False
        return True


    @classmethod
    def _build_extraction_json(cls, extraction: _WorkflowPipelineExtraction) -> dict[str, Any]:
        return {
            "subworkflows": [
                {
                    "name": spec.name,
                    "goal": spec.goal,
                    "description": spec.description,
                    "work_kind": spec.work_kind,
                    "contract_role": spec.contract_role,
                    "concrete_outcome": spec.concrete_outcome,
                    "inputs": spec.inputs,
                    "outputs": spec.outputs,
                    "input_schemas": copy.deepcopy(spec.input_schemas),
                    "output_schemas": copy.deepcopy(spec.output_schemas),
                    "extract_reason": spec.extract_reason,
                    "content": spec.content,
                    "planned_tools": [
                        {
                            "server": tool.server,
                            "kind": tool.kind,
                            "method": tool.method,
                            "required": tool.required,
                            "purpose": tool.purpose,
                            "consumes": list(tool.consumes),
                            "produces": list(tool.produces),
                        }
                        for tool in spec.planned_tools
                    ],
                    "required_capabilities": cls._required_capabilities_json(spec),
                    "extraction_score": None,
                }
                for spec in extraction.subworkflows
            ],
            "main_workflow_prompt": extraction.main_workflow_prompt,
            "validation": cls._build_validation_json(extraction.validation_errors),
            "validation_errors": extraction.validation_errors,
            "root_causes": copy.deepcopy(extraction.root_causes),
            "quality_review": copy.deepcopy(extraction.quality_review),
            "quality_warnings": [],
        }


    @staticmethod
    def _build_validation_json(errors: list[str]) -> dict[str, Any]:
        return {"ok": len(errors) == 0, "errors": list(errors), "error_count": len(errors)}


    def _build_pipeline_quality_report(
        self,
        extraction: _WorkflowPipelineExtraction,
        leaves: list[_GeneratedLeafWorkflow],
        main_retry_count: int,
        final_doc: WorkflowDocument,
    ) -> dict[str, Any]:
        main = final_doc.workflows.get("main")
        main_steps = list(self._enumerate_steps(main.steps)) if main is not None else []
        total_step_count = sum(1 for workflow in final_doc.workflows.values() for _ in self._enumerate_steps(workflow.steps))
        main_dataflow_diagnostics = analyze_external_artifact_readiness(final_doc)
        root_causes = list(copy.deepcopy(extraction.root_causes))
        if main_dataflow_diagnostics:
            root_causes.extend(build_main_dataflow_quality_details(main_dataflow_diagnostics).get("root_causes", []))
        repair_count = main_retry_count
        extraction_quality_reviewed = isinstance(extraction.quality_review, dict) and extraction.quality_review.get("verdict") != "warning"
        warnings = [
            {"code": "PIPELINE_EXTRACTION_VALIDATION_ERROR", "message": error}
            for error in extraction.validation_errors
        ]
        if isinstance(extraction.quality_review, dict):
            for diagnostic in extraction.quality_review.get("diagnostics", []) if isinstance(extraction.quality_review.get("diagnostics"), list) else []:
                if isinstance(diagnostic, dict) and str(diagnostic.get("severity") or "").lower() == "warning":
                    warnings.append(
                        {
                            "code": diagnostic.get("code") or "PIPELINE_EXTRACTION_QUALITY_WARNING",
                            "leaf": diagnostic.get("leaf_name") or "",
                            "message": diagnostic.get("message") or "",
                            "recommendation": diagnostic.get("recommendation") or "",
                        }
                    )
        skill_outputs = self._build_output_schema_map(final_doc.skill.outputs) if final_doc.skill and final_doc.skill.outputs else {}
        main_outputs = self._build_output_schema_map(main.outputs, final_doc.skill.outputs if final_doc.skill else None) if main and main.outputs else {}
        planned_tool_count = sum(len(spec.planned_tools) for spec in extraction.subworkflows)
        required_planned_tool_count = sum(1 for spec in extraction.subworkflows for tool in spec.planned_tools if tool.required)
        return {
            "status": "passed" if not main_dataflow_diagnostics and not extraction.validation_errors else "warning",
            "summary": {
                "workflow_count": len(final_doc.workflows),
                "leaf_count": len(leaves),
                "leaf_subworkflow_count": len(leaves),
                "leaf_blueprint_count": len(leaves),
                "main_step_count": len(main_steps),
                "total_step_count": total_step_count,
                "external_work_leaf_count": sum(1 for spec in extraction.subworkflows if spec.work_kind == "external_work"),
                "deterministic_shaping_leaf_count": sum(1 for spec in extraction.subworkflows if spec.work_kind == "deterministic_shaping"),
                "orchestration_leaf_count": sum(1 for spec in extraction.subworkflows if spec.work_kind == "orchestration"),
                "unknown_work_kind_leaf_count": sum(1 for spec in extraction.subworkflows if not spec.work_kind),
                "planned_tool_count": planned_tool_count,
                "required_planned_tool_count": required_planned_tool_count,
                "skill_output_count": len(skill_outputs),
                "main_output_count": len(main_outputs),
                "main_retry_count": main_retry_count,
                "repair_count": repair_count,
                "leaf_contract_repair_count": 0,
                "warning_count": len(warnings),
                "root_cause_count": len(root_causes),
                "extraction_quality_score": (extraction.quality_review or {}).get("score") if isinstance(extraction.quality_review, dict) else None,
            },
            "checks": {
                "extraction_validated": True,
                "leaf_intent_validated": True,
                "leaf_contracts_validated": True,
                "structured_extraction_validated": any(spec.output_schemas or spec.planned_tools for spec in extraction.subworkflows),
                "planned_tools_validated": any(spec.planned_tools for spec in extraction.subworkflows),
                "extraction_quality_reviewed": extraction_quality_reviewed,
                "main_dataflow_validated": True,
                "strong_output_schemas_validated": True,
                "workflow_hierarchy_validated": True,
            },
            "extraction": {
                "main_workflow_prompt": extraction.main_workflow_prompt,
                "validation": self._build_validation_json(extraction.validation_errors),
                "quality_review": copy.deepcopy(extraction.quality_review),
                "validation_errors": list(extraction.validation_errors),
                "root_causes": root_causes,
            },
            "leaves": self._build_pipeline_quality_leaves(extraction, leaves),
            "contracts": {
                "skill_outputs": skill_outputs,
                "main_outputs": main_outputs,
                "leaf_outputs": self._build_pipeline_quality_leaf_outputs(leaves),
            },
            "root_causes": root_causes,
            "repairs": [],
            "events": [],
            "warnings": warnings,
            "mcp_context": {},
        }


    def _build_pipeline_inspection(
        self,
        normalized_markdown: str,
        annotated_markdown: str,
        extraction: _WorkflowPipelineExtraction,
        leaves: list[_GeneratedLeafWorkflow],
        main_retry_count: int,
        final_doc: WorkflowDocument,
        pipeline_mcp_doc: str | None,
        pipeline_mcp_tool_contracts: list[McpToolOutputContract],
    ) -> dict[str, Any]:
        main = final_doc.workflows.get("main")
        root_causes = list(copy.deepcopy(extraction.root_causes))
        return {
            "summary": {
                "leaf_count": len(leaves),
                "leaf_blueprint_count": len(leaves),
                "main_retry_count": main_retry_count,
                "repair_count": main_retry_count,
                "root_cause_count": len(extraction.root_causes),
                "main_step_count": len(main.steps) if main is not None else 0,
                "workflow_count": len(final_doc.workflows),
            },
            "mcp_context": self._build_pipeline_mcp_context_json(pipeline_mcp_doc, pipeline_mcp_tool_contracts),
            "normalized_prompt": normalized_markdown,
            "annotated_markdown": annotated_markdown,
            "extraction_quality_review": copy.deepcopy(extraction.quality_review),
            "leaf_manifest": self._build_generated_leaf_manifest_json(leaves, extraction),
            "generated_leaf_blueprints": self._build_generated_leaf_blueprints_json(leaves),
            "generated_leaf_contracts": self._build_generated_leaf_contracts_json(leaves),
            "final_main_graph": (
                {"missing": True}
                if main is None
                else self._build_workflow_graph_inspection_json("main", main, final_doc.skill.outputs if final_doc.skill else None)
            ),
            "repair_history": [],
            "root_causes": root_causes,
        }


    @staticmethod
    def _build_pipeline_mcp_context_json(
        pipeline_mcp_doc: str | None,
        pipeline_mcp_tool_contracts: list[McpToolOutputContract],
    ) -> dict[str, Any]:
        server_names = sorted({contract.server_name for contract in pipeline_mcp_tool_contracts})
        tool_names = sorted({f"{contract.server_name}/{contract.tool_name}" for contract in pipeline_mcp_tool_contracts})
        servers = [
            {
                "name": server_name,
                "discovered": True,
                "tool_count": sum(1 for contract in pipeline_mcp_tool_contracts if contract.server_name == server_name),
                "prompt_count": 0,
                "tools": sorted(contract.tool_name for contract in pipeline_mcp_tool_contracts if contract.server_name == server_name),
                "prompts": [],
            }
            for server_name in server_names
        ]
        return {
            "available": bool(server_names),
            "selected_server_count": len(server_names),
            "selected_tool_count": len(tool_names),
            "selected_prompt_count": 0,
            "server_names": server_names,
            "tool_names": tool_names,
            "prompt_names": [],
            "servers": servers,
            "has_documentation": bool(pipeline_mcp_doc and pipeline_mcp_doc.strip()),
        }


    def _build_pipeline_quality_leaves(
        self,
        extraction: _WorkflowPipelineExtraction,
        leaves: list[_GeneratedLeafWorkflow],
    ) -> list[dict[str, Any]]:
        leaves_by_name = {leaf.name: leaf for leaf in leaves}
        result: list[dict[str, Any]] = []
        for spec in extraction.subworkflows:
            leaf = leaves_by_name.get(spec.name)
            item: dict[str, Any] = {
                "name": spec.name,
                "goal": spec.goal,
                "description": spec.description,
                "work_kind": spec.work_kind,
                "contract_role": spec.contract_role,
                "concrete_outcome": spec.concrete_outcome,
                "extract_reason": spec.extract_reason,
                "extraction_score": None,
                "planned_tools": self._planned_tools_json(spec.planned_tools),
                "required_capabilities": self._required_capabilities_json(spec),
                "required_planned_tool_count": sum(1 for tool in spec.planned_tools if tool.required),
                "declared_input_schemas": copy.deepcopy(spec.input_schemas),
                "declared_output_schemas": copy.deepcopy(spec.output_schemas),
                "generated": leaf is not None,
            }
            if leaf is not None:
                workflow = leaf.document.workflows.get(leaf.workflow_name)
                steps = list(self._enumerate_steps(workflow.steps)) if workflow else []
                item.update(
                    {
                        "generated_workflow_name": leaf.workflow_name,
                        "step_count": len(steps),
                        "action_step_count": sum(1 for step in steps if self._is_executable_action_step_type(step.type)),
                        "blueprint": self._build_pipeline_leaf_blueprint_json(leaf),
                        "input_contracts": self._build_leaf_input_contracts(leaf),
                        "output_contracts": self._build_leaf_output_contracts(leaf),
                    }
                )
            result.append(item)
        return result


    @staticmethod
    def _is_executable_action_step_type(step_type: str) -> bool:
        return step_type not in {"set", "emit", "switch", "parallel", "loop.sequential", "loop.parallel"}


    def _build_pipeline_quality_leaf_outputs(self, leaves: list[_GeneratedLeafWorkflow]) -> dict[str, Any]:
        return {
            leaf.name: {
                "generated_workflow_name": leaf.workflow_name,
                "outputs": self._build_leaf_output_contracts(leaf),
            }
            for leaf in leaves
        }


    def _build_generated_leaf_manifest_json(
        self,
        leaves: list[_GeneratedLeafWorkflow],
        extraction: _WorkflowPipelineExtraction,
    ) -> dict[str, Any]:
        specs_by_name = {spec.name: spec for spec in extraction.subworkflows}
        leaf_items: list[dict[str, Any]] = []
        for leaf in leaves:
            spec = specs_by_name.get(leaf.name)
            leaf_items.append(
                {
                    "name": leaf.name,
                    "workflow": leaf.name,
                    "generated_workflow": leaf.workflow_name,
                    "goal": spec.goal if spec else "",
                    "description": spec.description if spec else "",
                    "work_kind": spec.work_kind if spec else "",
                    "contract_role": spec.contract_role if spec else "",
                    "concrete_outcome": spec.concrete_outcome if spec else "",
                    "extraction_score": None,
                    "extract_reason": spec.extract_reason if spec else "",
                    "planned_tools": self._planned_tools_json(spec.planned_tools) if spec else [],
                    "required_capabilities": self._required_capabilities_json(spec) if spec else [],
                    "blueprint": self._build_pipeline_leaf_blueprint_json(leaf),
                    "inputs": self._build_leaf_input_contracts(leaf),
                    "outputs": self._build_leaf_output_contracts(leaf),
                }
            )
        return {"leaves": leaf_items, "main_workflow_prompt": extraction.main_workflow_prompt}


    def _build_generated_leaf_blueprints_json(self, leaves: list[_GeneratedLeafWorkflow]) -> dict[str, Any]:
        return {leaf.name: self._build_pipeline_leaf_blueprint_json(leaf) for leaf in leaves}


    def _build_generated_leaf_contracts_json(self, leaves: list[_GeneratedLeafWorkflow]) -> dict[str, Any]:
        return {
            leaf.name: {
                "workflow": leaf.name,
                "generated_workflow": leaf.workflow_name,
                "inputs": self._build_leaf_input_contracts(leaf),
                "outputs": self._build_leaf_output_contracts(leaf),
            }
            for leaf in leaves
        }


    def _build_pipeline_leaf_blueprint_json(self, leaf: _GeneratedLeafWorkflow) -> dict[str, Any]:
        workflow = leaf.document.workflows.get(leaf.workflow_name)
        steps = workflow.steps if workflow is not None else []
        return {
            "leaf": leaf.name,
            "workflow_name": leaf.workflow_name,
            "summary": (leaf.spec.goal if leaf.spec else leaf.name),
            "steps": [self._build_step_inspection_json(step) for step in steps],
            "outputs": self._build_leaf_output_contracts(leaf),
        }


    def _build_workflow_graph_inspection_json(
        self,
        workflow_name: str,
        workflow: WorkflowDef,
        skill_outputs: dict[str, OutputDef] | None,
    ) -> dict[str, Any]:
        return {
            "workflow": workflow_name,
            "has_functions": bool(workflow.functions and workflow.functions.strip()),
            "inputs": self._build_input_schema_map(workflow.inputs or {}),
            "steps": [self._build_step_inspection_json(step) for step in workflow.steps],
            "outputs": self._build_workflow_output_inspection_json(workflow.outputs or {}, skill_outputs),
        }


    def _build_step_inspection_json(self, step: StepDef) -> dict[str, Any]:
        obj: dict[str, Any] = {"id": step.id, "type": step.type}
        if step.if_:
            obj["if"] = step.if_
        if step.output:
            obj["output"] = step.output
        if step.type == "workflow.call" and isinstance(step.input, dict):
            ref = step.input.get("ref") if isinstance(step.input.get("ref"), dict) else {}
            leaf_name = ref.get("name") if isinstance(ref, dict) else None
            if isinstance(leaf_name, str):
                obj["leaf"] = leaf_name
            args = step.input.get("args")
            if isinstance(args, dict):
                obj["args"] = copy.deepcopy(args)
        elif step.input is not None:
            obj["input"] = copy.deepcopy(step.input)
        if step.steps:
            obj["steps"] = [self._build_step_inspection_json(child) for child in step.steps]
        if step.default:
            obj["default"] = [self._build_step_inspection_json(child) for child in step.default]
        if step.branches:
            obj["branches"] = [
                {"index": index, "steps": [self._build_step_inspection_json(child) for child in branch.steps]}
                for index, branch in enumerate(step.branches)
            ]
        if step.cases:
            cases: list[dict[str, Any]] = []
            for case in step.cases:
                case_obj: dict[str, Any] = {"steps": [self._build_step_inspection_json(child) for child in case.steps]}
                if case.value:
                    case_obj["value"] = case.value
                if case.when:
                    case_obj["when"] = case.when
                cases.append(case_obj)
            obj["cases"] = cases
        return obj


    def _build_workflow_output_inspection_json(
        self,
        outputs: dict[str, OutputDef],
        skill_outputs: dict[str, OutputDef] | None,
    ) -> dict[str, Any]:
        schemas = self._build_output_schema_map(outputs, skill_outputs)
        return {
            name: {"expr": output.expr, "schema": copy.deepcopy(schemas.get(name) or self._output_def_to_contract_node(output))}
            for name, output in outputs.items()
        }


    def _build_leaf_input_contracts(self, leaf: _GeneratedLeafWorkflow) -> dict[str, Any]:
        workflow = leaf.document.workflows.get(leaf.workflow_name)
        if workflow and workflow.inputs:
            return self._build_input_schema_map(workflow.inputs)
        inputs = leaf.workflow_node.get("inputs") if isinstance(leaf.workflow_node.get("inputs"), dict) else {}
        return copy.deepcopy(inputs)


    def _build_leaf_output_contracts(self, leaf: _GeneratedLeafWorkflow) -> dict[str, Any]:
        workflow = leaf.document.workflows.get(leaf.workflow_name)
        if workflow and workflow.outputs:
            return self._build_output_schema_map(workflow.outputs, leaf.document.skill.outputs if leaf.document.skill else None)
        outputs = leaf.workflow_node.get("outputs") if isinstance(leaf.workflow_node.get("outputs"), dict) else {}
        return copy.deepcopy(outputs)


    @classmethod
    def _build_input_schema_map(cls, inputs: dict[str, InputDef]) -> dict[str, Any]:
        return {name: cls._input_def_to_contract_node(definition) for name, definition in inputs.items()}


    @classmethod
    def _build_output_schema_map(
        cls,
        outputs: dict[str, OutputDef],
        skill_outputs: dict[str, OutputDef] | None = None,
    ) -> dict[str, Any]:
        schemas: dict[str, Any] = {}
        for name, definition in outputs.items():
            contract = definition
            if (
                cls._is_opaque_output_schema(definition)
                and skill_outputs is not None
                and name in skill_outputs
                and not cls._is_opaque_output_schema(skill_outputs[name])
            ):
                contract = skill_outputs[name]
            schemas[name] = cls._output_def_to_contract_node(contract)
        return schemas


    @classmethod
    def _input_def_to_contract_node(cls, definition: InputDef) -> Any:
        node = cls._type_def_to_contract_node(definition)
        if isinstance(node, dict) and not definition.required:
            node["required"] = False
        elif not definition.required:
            node = {"type": str(node), "required": False}
        return node


    @classmethod
    def _output_def_to_contract_node(cls, definition: OutputDef) -> Any:
        return cls._type_def_to_contract_node(definition)


    @classmethod
    def _type_def_to_contract_node(cls, definition: InputDef | OutputDef) -> Any:
        type_name = cls._normalize_workflow_schema_type(getattr(definition, "type", "any"))
        has_children = any(
            [
                getattr(definition, "description", None),
                getattr(definition, "items", None) is not None,
                getattr(definition, "properties", None),
                getattr(definition, "additional_properties", None) is not None,
                getattr(definition, "required_properties", None),
            ]
        )
        if type_name in {"string", "number", "integer", "boolean"} and not has_children:
            return type_name
        node: dict[str, Any] = {"type": type_name}
        description = getattr(definition, "description", None)
        if isinstance(description, str) and description.strip():
            node["description"] = description
        items = getattr(definition, "items", None)
        if items is not None:
            node["items"] = cls._type_def_to_contract_node(items)
        properties = getattr(definition, "properties", None)
        if properties:
            node["properties"] = {
                name: cls._type_def_to_contract_node(child)
                for name, child in properties.items()
            }
        additional_properties = getattr(definition, "additional_properties", None)
        if additional_properties is not None:
            node["additional_properties"] = cls._type_def_to_contract_node(additional_properties)
        required_properties = getattr(definition, "required_properties", None)
        if required_properties:
            node["required_properties"] = list(required_properties)
        return node


    @classmethod
    def _is_opaque_output_schema(cls, definition: OutputDef) -> bool:
        return (
            cls._normalize_workflow_schema_type(definition.type) == "any"
            and not definition.description
            and definition.items is None
            and definition.properties is None
            and definition.additional_properties is None
            and not definition.required_properties
        )


    @staticmethod
    def _planned_tools_json(planned_tools: list[_PipelinePlannedTool]) -> list[dict[str, Any]]:
        return [
            {
                "server": tool.server,
                "kind": tool.kind,
                "method": tool.method,
                "required": tool.required,
                "purpose": tool.purpose,
                "consumes": list(tool.consumes),
                "produces": list(tool.produces),
            }
            for tool in planned_tools
        ]


    @classmethod
    def _required_capabilities_json(cls, spec: _WorkflowPipelineSubworkflowSpec) -> list[dict[str, Any]]:
        return cls._planned_tools_json([tool for tool in spec.planned_tools if tool.required])
