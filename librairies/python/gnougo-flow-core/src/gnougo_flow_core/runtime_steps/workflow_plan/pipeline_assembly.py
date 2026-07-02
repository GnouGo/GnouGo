from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanPipelineAssemblyMixin:
    def _build_main_assembly_prompt(
        self,
        pipeline_input: dict[str, Any],
        generator: dict[str, Any],
        normalized_markdown: str,
        extraction: _WorkflowPipelineExtraction,
        leaves: list[_GeneratedLeafWorkflow],
        configured_main_inputs: dict[str, Any],
        generated_leaf_inputs: dict[str, Any],
    ) -> str:
        parts = [
            "You are assembling the parent `main` workflow graph for a GnOuGo.Flow pipeline.",
            "Return ONLY one YAML mapping with `document` and `graph` keys. Do not return version, entrypoint, workflows, a full `main` workflow, or leaf workflow definitions.",
            "",
            "Hard rules:",
            "- Return a compact orchestration graph. The runtime will render the real `main` workflow and graft validated leaf workflows before final validation.",
            "- Graph call nodes must use `leaf: <leaf_name>` and `args`; do not write raw workflow.call refs.",
            "- Non-call support nodes may use normal step `type` and `input` when the main orchestration needs derived values, guards, switches, loops, or parallel branches.",
            "- The main workflow must never use `workflow.plan`, and graph nodes must not inline leaf logic.",
            "- Leaf workflows must never call other workflows.",
            "- Preserve the orchestration algorithm from the normalized prompt and the Main workflow orchestration section.",
            "- Use conditionals, switches, loops, or parallel branches when the orchestration requires them.",
            "- For container support nodes (`sequence`, `switch`, `parallel`, loops), nested graph nodes are allowed in `steps`, `branches[].steps`, `cases[].steps`, and `default`.",
            "- Pass leaf arguments from declared `data.inputs.<name>`, earlier step outputs, loop variables, derived values, or constants.",
            "- Every `data.inputs.<name>` reference MUST have an identically named declaration in `graph.inputs` or `document.skill.inputs`.",
            "- Leaf input names are call arguments, not automatically public main inputs.",
            "- `generated_leaf_contracts_yaml` is authoritative for leaf workflow names, call arguments, and available outputs.",
            "- If `leaf_input_candidates_yaml` or `leaf_subworkflow_specs_json` disagree with `generated_leaf_contracts_yaml`, follow `generated_leaf_contracts_yaml`.",
            "- Map public user input names to differently named leaf arguments when their meanings match.",
            "- Do not expose loop variables, intermediate values, paths, identifiers, flags, or leaf-only implementation details as public inputs unless the user explicitly requested them.",
            "- Use `set` support nodes for data shaping in the main graph: renaming fields, building objects/arrays, constants, and safe type conversions.",
            "- Keep exact JSON values intact when passing arrays, objects, numbers, or booleans. Do not stringify a structured leaf output unless a downstream leaf explicitly wants a string.",
            "- If a leaf call is inside a switch, loop, parallel branch, or conditional path, do not reference that leaf call step from outside that container/path. Put dependent work in the same path, or expose the container step itself as the output.",
        ]
        if configured_main_inputs:
            parts.append("- `authoritative_main_inputs_yaml` is exact: preserve every name and schema and do not add or remove inputs.")
        else:
            parts.append("- Infer public main inputs from the user's normalized request; do not expose leaf-only implementation details.")
        parts.extend(
            [
                "",
                self._prompt_section("configured_document_name", self._resolve_configured_pipeline_document_name(pipeline_input, generator) or ""),
                self._prompt_section("configured_skill_yaml", self._serialize_yaml_value(self._resolve_configured_skill(pipeline_input, generator) or {})),
                self._prompt_section("normalized_markdown", normalized_markdown),
                self._prompt_section("main_workflow_orchestration", extraction.main_workflow_prompt),
                self._prompt_section("authoritative_main_inputs_yaml", self._serialize_yaml_value(configured_main_inputs)),
                self._prompt_section("leaf_input_candidates_yaml", self._serialize_yaml_value(generated_leaf_inputs)),
                self._prompt_section("generated_leaf_contracts_yaml", self._serialize_yaml_value(self._build_generated_leaf_contracts(leaves))),
                self._prompt_section("leaf_subworkflow_specs_json", json.dumps(self._build_extraction_json(extraction), ensure_ascii=False, indent=2)),
                self._prompt_section("generated_leaf_workflows_yaml", "\n---\n".join(leaf.yaml_text for leaf in leaves)),
                "",
                "Output shape example:",
                "document:",
                "  name: example_pipeline",
                "  skill:",
                "    description: Process the user's query.",
                "    tags: [example, pipeline]",
                "    inputs:",
                "      user_query: string",
                "    outputs:",
                "      result: string",
                "graph:",
                "  inputs:",
                "    user_query: string",
                "  steps:",
                "    - id: call_example_leaf",
                "      leaf: example_leaf",
                "      args:",
                "        query: ${data.inputs.user_query}",
                "  outputs:",
                "    result: ${data.steps.call_example_leaf.outputs.result}",
                "",
                "Main graph boundaries:",
                "- Keep business/tool/LLM work inside leaf workflows. The main graph should only orchestrate, derive values, branch, loop, and call leaves.",
                "- If a value is required by a generated leaf input contract, pass it in the leaf args or derive it in an earlier support step.",
                "- Do not add MCP, LLM, template, human-input, workflow.plan, or raw workflow.call support nodes to the main graph.",
            ]
        )
        return "\n".join(parts)


    @classmethod
    def _build_main_assembly_repair_prompt(cls, base_prompt: str, previous_response: str | None, structured_error: str) -> str:
        parts = [
            base_prompt.rstrip(),
            "",
            "The previous main workflow assembly failed final validation.",
            "Return a complete corrected `document` and `graph` YAML mapping that still follows every rule above.",
            "Fix the reported error without changing the user's public contract or orchestration intent.",
        ]
        if previous_response:
            parts.extend(["<invalid_main_assembly_yaml>", cls._strip_markdown_code_fence(previous_response), "</invalid_main_assembly_yaml>"])
        parts.extend(["<main_assembly_validation_error>", structured_error, "</main_assembly_validation_error>"])
        return "\n".join(parts)


    def _parse_generated_main_assembly(self, text: str) -> _GeneratedMainAssembly:
        parsed = yaml.safe_load(self._strip_markdown_code_fence(textwrap.dedent(text))) or {}
        if not isinstance(parsed, dict):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline main assembly response must be a YAML mapping.")
        document = parsed.get("document") if isinstance(parsed.get("document"), dict) else {}
        if isinstance(parsed.get("graph"), dict):
            skill = copy.deepcopy(document.get("skill")) if isinstance(document.get("skill"), dict) else None
            return _GeneratedMainAssembly(self._build_main_workflow_node_from_graph(parsed["graph"]), document.get("name"), skill)
        if isinstance(parsed.get("main"), dict):
            skill = copy.deepcopy(document.get("skill")) if isinstance(document.get("skill"), dict) else None
            return _GeneratedMainAssembly(copy.deepcopy(parsed["main"]), document.get("name"), skill)
        workflows = parsed.get("workflows")
        if isinstance(workflows, dict) and isinstance(workflows.get("main"), dict):
            skill = copy.deepcopy(parsed.get("skill")) if isinstance(parsed.get("skill"), dict) else None
            return _GeneratedMainAssembly(copy.deepcopy(workflows["main"]), parsed.get("name"), skill)
        skill = copy.deepcopy(parsed.get("skill")) if isinstance(parsed.get("skill"), dict) else None
        return _GeneratedMainAssembly(copy.deepcopy(parsed), parsed.get("name"), skill)


    def _build_main_workflow_node_from_graph(self, graph: dict[str, Any]) -> dict[str, Any]:
        main: dict[str, Any] = {}
        if isinstance(graph.get("inputs"), dict):
            main["inputs"] = copy.deepcopy(graph["inputs"])
        source_steps = graph.get("steps") if isinstance(graph.get("steps"), list) else graph.get("nodes")
        if not isinstance(source_steps, list):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline orchestration graph must include steps or nodes.")
        main["steps"] = self._render_graph_step_sequence(source_steps)
        if isinstance(graph.get("outputs"), dict):
            main["outputs"] = copy.deepcopy(graph["outputs"])
        return main


    def _render_graph_step_sequence(self, source_steps: list[Any]) -> list[dict[str, Any]]:
        rendered: list[dict[str, Any]] = []
        for source_step in source_steps:
            if not isinstance(source_step, dict):
                raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline orchestration graph steps must be mappings.")
            rendered.append(self._render_graph_step(source_step))
        return rendered


    def _render_graph_step(self, graph_step: dict[str, Any]) -> dict[str, Any]:
        leaf_name = graph_step.get("leaf") or graph_step.get("workflow")
        if isinstance(leaf_name, str) and leaf_name.strip():
            return self._render_graph_leaf_call_step(graph_step, leaf_name.strip())

        step_type = graph_step.get("type")
        if not isinstance(step_type, str) or not step_type.strip():
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline orchestration graph step must include either leaf or type.")
        if step_type == "workflow.plan":
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_POLICY, "Pipeline orchestration graph must not contain workflow.plan.")
        if step_type == "workflow.call":
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_POLICY, "Pipeline orchestration graph call nodes must use leaf and args, not raw workflow.call.")

        rendered = copy.deepcopy(graph_step)
        if isinstance(rendered.get("steps"), list):
            rendered["steps"] = self._render_graph_step_sequence(rendered["steps"])
        if isinstance(rendered.get("branches"), list):
            rendered["branches"] = self._render_graph_branch_sequence(rendered["branches"])
        if isinstance(rendered.get("cases"), list):
            rendered["cases"] = self._render_graph_case_sequence(rendered["cases"])
        if isinstance(rendered.get("default"), list):
            rendered["default"] = self._render_graph_step_sequence(rendered["default"])
        return rendered


    @staticmethod
    def _render_graph_leaf_call_step(graph_step: dict[str, Any], leaf_name: str) -> dict[str, Any]:
        step: dict[str, Any] = {
            "id": graph_step.get("id") if isinstance(graph_step.get("id"), str) and graph_step.get("id").strip() else f"call_{leaf_name}",
            "type": "workflow.call",
        }
        for common_field in ("if", "retry", "on_error", "output"):
            if common_field in graph_step:
                step[common_field] = copy.deepcopy(graph_step[common_field])
        step["input"] = {
            "ref": {"kind": "local", "name": leaf_name},
            "args": copy.deepcopy(graph_step.get("args") if isinstance(graph_step.get("args"), dict) else {}),
        }
        return step


    def _render_graph_branch_sequence(self, source_branches: list[Any]) -> list[dict[str, Any]]:
        rendered: list[dict[str, Any]] = []
        for source_branch in source_branches:
            if not isinstance(source_branch, dict):
                raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline orchestration graph branches must be mappings.")
            branch = copy.deepcopy(source_branch)
            if isinstance(branch.get("steps"), list):
                branch["steps"] = self._render_graph_step_sequence(branch["steps"])
            rendered.append(branch)
        return rendered


    def _render_graph_case_sequence(self, source_cases: list[Any]) -> list[dict[str, Any]]:
        rendered: list[dict[str, Any]] = []
        for source_case in source_cases:
            if not isinstance(source_case, dict):
                raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline orchestration graph cases must be mappings.")
            case = copy.deepcopy(source_case)
            if isinstance(case.get("steps"), list):
                case["steps"] = self._render_graph_step_sequence(case["steps"])
            rendered.append(case)
        return rendered


    def _compose_pipeline_workflow_yaml(
        self,
        pipeline_input: dict[str, Any],
        generator: dict[str, Any],
        extraction: _WorkflowPipelineExtraction,
        leaves: list[_GeneratedLeafWorkflow],
        assembly: _GeneratedMainAssembly,
        main_inputs: dict[str, Any],
    ) -> str:
        document_name = self._resolve_configured_pipeline_document_name(pipeline_input, generator) or assembly.document_name or "generated-pipeline-workflow"
        main_node = copy.deepcopy(assembly.main_workflow_node)
        main_node["inputs"] = copy.deepcopy(main_inputs)
        root = {
            "version": 1,
            "name": document_name,
            "skill": self._build_pipeline_skill_node(document_name, pipeline_input, generator, extraction, main_inputs, assembly.skill_node),
            "entrypoint": "main",
            "workflows": {"main": main_node},
        }
        for leaf in leaves:
            root["workflows"][leaf.name] = copy.deepcopy(leaf.workflow_node)
        return yaml.dump(root, Dumper=_NoAliasDumper, sort_keys=False, allow_unicode=False).strip()


    def _build_pipeline_skill_node(
        self,
        document_name: str,
        pipeline_input: dict[str, Any],
        generator: dict[str, Any],
        extraction: _WorkflowPipelineExtraction,
        main_inputs: dict[str, Any],
        generated_skill: dict[str, Any] | None,
    ) -> dict[str, Any]:
        configured_skill = self._resolve_configured_skill(pipeline_input, generator) or {}
        description = (
            configured_skill.get("description")
            or pipeline_input.get("description")
            or generator.get("description")
            or (generated_skill or {}).get("description")
            or self._build_generated_pipeline_skill_description(document_name, pipeline_input, generator, extraction)
        )
        tags = configured_skill.get("tags") or (generated_skill or {}).get("tags") or ["generated", "pipeline"]
        outputs = {}
        for source in (generator.get("outputs"), configured_skill.get("outputs"), pipeline_input.get("outputs"), (generated_skill or {}).get("outputs")):
            if isinstance(source, dict):
                outputs.update(copy.deepcopy(source))
        if not outputs:
            for spec in extraction.subworkflows:
                outputs[f"{spec.name}_outputs"] = {"type": "object", "description": f"Outputs from the {spec.name} leaf workflow."}
        return {"description": description, "tags": tags, "inputs": copy.deepcopy(main_inputs), "outputs": outputs}


    @staticmethod
    def _resolve_configured_skill(pipeline_input: dict[str, Any], generator: dict[str, Any]) -> dict[str, Any] | None:
        skill = pipeline_input.get("skill") if isinstance(pipeline_input.get("skill"), dict) else generator.get("skill")
        return copy.deepcopy(skill) if isinstance(skill, dict) else None


    @staticmethod
    def _resolve_configured_pipeline_document_name(pipeline_input: dict[str, Any], generator: dict[str, Any]) -> str | None:
        for source in (pipeline_input, generator):
            for key in ("name", "workflow_name", "document_name"):
                value = source.get(key)
                if isinstance(value, str) and value.strip():
                    return value.strip()
        return None


    @staticmethod
    def _build_generated_pipeline_skill_description(
        document_name: str,
        pipeline_input: dict[str, Any],
        generator: dict[str, Any],
        extraction: _WorkflowPipelineExtraction,
    ) -> str:
        source = (
            pipeline_input.get("raw_prompt")
            or generator.get("raw_prompt")
            or generator.get("instruction")
            or extraction.main_workflow_prompt
            or document_name
        )
        first_line = next((line.strip() for line in str(source).splitlines() if line.strip() and not line.strip().startswith("#")), "")
        return first_line[:177] + "..." if len(first_line) > 180 else first_line or f"Generated pipeline workflow for {document_name}."


    @staticmethod
    def _build_configured_main_input_contract(pipeline_input: dict[str, Any], generator: dict[str, Any]) -> dict[str, Any]:
        inputs: dict[str, Any] = {}
        for source in (
            (generator.get("skill") if isinstance(generator.get("skill"), dict) else {}).get("inputs"),
            generator.get("inputs"),
            (pipeline_input.get("skill") if isinstance(pipeline_input.get("skill"), dict) else {}).get("inputs"),
            pipeline_input.get("inputs"),
        ):
            if isinstance(source, dict):
                inputs.update(copy.deepcopy(source))
        return inputs


    @staticmethod
    def _build_generated_leaf_contracts(leaves: list[_GeneratedLeafWorkflow]) -> dict[str, Any]:
        contracts: dict[str, Any] = {}
        for leaf in leaves:
            inputs = leaf.workflow_node.get("inputs")
            outputs = leaf.workflow_node.get("outputs")
            contracts[leaf.name] = {
                "workflow": leaf.workflow_name,
                "inputs": copy.deepcopy(inputs) if isinstance(inputs, dict) else {},
                "outputs": copy.deepcopy(outputs) if isinstance(outputs, dict) else {},
            }
        return contracts


    @staticmethod
    def _build_generated_main_input_contract(leaves: list[_GeneratedLeafWorkflow]) -> dict[str, Any]:
        inputs: dict[str, Any] = {}
        available_outputs: set[str] = set()
        for leaf in leaves:
            leaf_inputs = leaf.workflow_node.get("inputs") if isinstance(leaf.workflow_node.get("inputs"), dict) else {}
            for name, schema in leaf_inputs.items():
                if name in available_outputs:
                    continue
                if name not in inputs:
                    inputs[name] = copy.deepcopy(schema)
                elif inputs[name] != schema:
                    inputs[name] = "any"
            leaf_outputs = leaf.workflow_node.get("outputs") if isinstance(leaf.workflow_node.get("outputs"), dict) else {}
            available_outputs.update(leaf_outputs.keys())
        return inputs


    def _resolve_main_input_contract(
        self,
        configured_inputs: dict[str, Any],
        assembly: _GeneratedMainAssembly,
        generated_leaf_inputs: dict[str, Any],
    ) -> dict[str, Any]:
        if configured_inputs:
            return copy.deepcopy(configured_inputs)
        inputs = assembly.main_workflow_node.get("inputs")
        if isinstance(inputs, dict) and inputs:
            return copy.deepcopy(inputs)
        generated_skill_inputs = assembly.skill_node.get("inputs") if isinstance(assembly.skill_node, dict) else None
        if isinstance(generated_skill_inputs, dict) and generated_skill_inputs:
            return copy.deepcopy(generated_skill_inputs)
        return copy.deepcopy(generated_leaf_inputs)


    @classmethod
    def _ensure_main_workflow_outputs(cls, main_node: dict[str, Any], specs: list[_WorkflowPipelineSubworkflowSpec]) -> None:
        if isinstance(main_node.get("outputs"), dict):
            return
        main_node["outputs"] = cls._build_default_main_outputs(main_node, specs)


    @classmethod
    def _build_default_main_outputs(cls, main_node: dict[str, Any], specs: list[_WorkflowPipelineSubworkflowSpec]) -> dict[str, str]:
        outputs: dict[str, str] = {}
        top_level_steps = main_node.get("steps")
        if isinstance(top_level_steps, list):
            for step_id, leaf_name in cls._enumerate_top_level_workflow_calls(top_level_steps):
                key = cls._build_unique_key(outputs, f"{leaf_name}_outputs")
                outputs[key] = f"${{data.steps.{step_id}.outputs}}"
            if outputs:
                return outputs

            for step in top_level_steps:
                if not isinstance(step, dict):
                    continue
                step_id = step.get("id")
                if not isinstance(step_id, str) or not step_id.strip():
                    continue
                key = cls._build_unique_key(outputs, f"{step_id}_output")
                outputs[key] = f"${{data.steps.{step_id}}}"
            if outputs:
                return outputs

        for spec in specs:
            key = cls._build_unique_key(outputs, f"{spec.name}_outputs")
            outputs[key] = f"${{data.steps.call_{spec.name}.outputs}}"
        return outputs


    @staticmethod
    def _enumerate_top_level_workflow_calls(steps: list[Any]) -> list[tuple[str, str]]:
        calls: list[tuple[str, str]] = []
        for step in steps:
            if not isinstance(step, dict) or step.get("type") != "workflow.call":
                continue
            step_id = step.get("id")
            input_obj = step.get("input") if isinstance(step.get("input"), dict) else {}
            ref = input_obj.get("ref") if isinstance(input_obj, dict) and isinstance(input_obj.get("ref"), dict) else {}
            leaf_name = ref.get("name") if isinstance(ref, dict) else None
            if isinstance(step_id, str) and step_id.strip() and isinstance(leaf_name, str) and leaf_name.strip():
                calls.append((step_id, leaf_name))
        return calls


    @staticmethod
    def _validate_declared_main_input_references(main_node: dict[str, Any], main_inputs: dict[str, Any]) -> None:
        dumped = yaml.dump(main_node, Dumper=_NoAliasDumper, sort_keys=False, allow_unicode=False)
        undeclared = sorted(set(re.findall(r"\bdata\.inputs\.([A-Za-z_][A-Za-z0-9_]*)", dumped)) - set(main_inputs.keys()))
        if undeclared:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline main workflow references undeclared inputs: " + ", ".join(undeclared))


    def _enforce_pipeline_workflow_hierarchy(self, doc: WorkflowDocument, leaf_names: set[str]) -> None:
        for workflow_name, workflow in doc.workflows.items():
            for step in self._enumerate_steps(workflow.steps):
                if workflow_name != "main" and step.type in {"workflow.call", "workflow.plan"}:
                    raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_POLICY, f"Leaf workflow '{workflow_name}' must not contain step type '{step.type}'.")
                if workflow_name == "main" and step.type == "workflow.call" and isinstance(step.input, dict):
                    ref = step.input.get("ref")
                    if not isinstance(ref, dict) or str(ref.get("kind", "local")) != "local" or ref.get("name") not in leaf_names:
                        raise WorkflowRuntimeException(
                            ErrorCodes.TEMPLATE_POLICY,
                            f"Pipeline main workflow step '{step.id}' must call a generated local leaf workflow.",
                        )


    def _validate_pipeline_leaf_call_arguments(self, doc: WorkflowDocument, leaves: list[_GeneratedLeafWorkflow]) -> None:
        main = doc.workflows.get("main")
        if main is None:
            return

        required_inputs_by_leaf = {
            leaf.name: [
                input_name
                for input_name, schema in self._build_leaf_input_schema_map(leaf).items()
                if self._is_required_leaf_input(schema)
            ]
            for leaf in leaves
        }

        for step in self._enumerate_steps(main.steps):
            if step.type != "workflow.call" or not isinstance(step.input, dict):
                continue
            ref = step.input.get("ref")
            target_name = ref.get("name") if isinstance(ref, dict) else None
            if not isinstance(target_name, str) or target_name not in required_inputs_by_leaf:
                continue
            args = step.input.get("args") if isinstance(step.input.get("args"), dict) else {}
            missing = sorted(name for name in required_inputs_by_leaf[target_name] if name not in args)
            if missing:
                raise WorkflowRuntimeException(
                    ErrorCodes.TEMPLATE_PLAN,
                    f"Pipeline main workflow call '{step.id}' to leaf '{target_name}' is missing required leaf argument(s): {', '.join(missing)}",
                )


    def _validate_pipeline_main_graph_boundaries(self, doc: WorkflowDocument) -> None:
        main = doc.workflows.get("main")
        if main is None:
            return
        forbidden = {"mcp.call", "llm.call", "template.render", "human.input", "workflow.plan"}
        for step in self._enumerate_steps(main.steps):
            if step.type in forbidden:
                raise WorkflowRuntimeException(
                    ErrorCodes.TEMPLATE_POLICY,
                    f"Pipeline main workflow must not contain step type '{step.type}'. Keep business/tool/LLM work inside leaf workflows.",
                )


    def _validate_pipeline_main_leaf_output_contracts(self, doc: WorkflowDocument, leaves: list[_GeneratedLeafWorkflow]) -> None:
        main = doc.workflows.get("main")
        if main is None:
            return
        leaf_by_name = {leaf.name: leaf for leaf in leaves}
        for step in self._enumerate_steps(main.steps):
            if step.type != "workflow.call" or not isinstance(step.input, dict):
                continue
            ref = step.input.get("ref")
            leaf_name = ref.get("name") if isinstance(ref, dict) else None
            leaf = leaf_by_name.get(leaf_name)
            if leaf is None:
                continue
            outputs = leaf.workflow_node.get("outputs") if isinstance(leaf.workflow_node.get("outputs"), dict) else {}
            for output_name, output_schema in outputs.items():
                if isinstance(output_schema, dict) and is_weak_yaml_output_schema(output_schema):
                    raise WorkflowRuntimeException(
                        ErrorCodes.TEMPLATE_PLAN,
                        f"WEAK_OUTPUT_SCHEMA: Pipeline leaf '{leaf_name}' output '{output_name}' is too weak for main workflow assembly.",
                    )


    @staticmethod
    def _validate_pipeline_main_dataflow_quality(doc: WorkflowDocument) -> None:
        validate_external_artifact_readiness(doc)
