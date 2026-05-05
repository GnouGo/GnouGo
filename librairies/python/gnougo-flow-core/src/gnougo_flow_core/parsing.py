from __future__ import annotations

from typing import Any

import yaml

from .errors import WorkflowParseException
from .models import (
    BranchDef,
    InputDef,
    OnErrorCase,
    OnErrorDef,
    OutputDef,
    RetryPolicy,
    StepDef,
    SwitchCaseDef,
    WorkflowDef,
    WorkflowDocument,
)


class WorkflowParser:
    @staticmethod
    def parse(yaml_text: str) -> WorkflowDocument:
        raw = yaml.safe_load(yaml_text)
        if not isinstance(raw, dict):
            raise WorkflowParseException("Root must be a YAML mapping")

        version = raw.get("version")
        if version is None:
            raise WorkflowParseException("Missing required field 'version'")
        version = WorkflowParser._parse_workflow_version(version)
        if version != 1:
            raise WorkflowParseException(f"Unsupported workflow version: {version}")

        workflows_raw = raw.get("workflows")
        if not isinstance(workflows_raw, dict):
            raise WorkflowParseException("Missing required field 'workflows'")

        workflows: dict[str, WorkflowDef] = {}
        for wf_name, wf_value in workflows_raw.items():
            if not isinstance(wf_value, dict):
                raise WorkflowParseException(f"Workflow '{wf_name}' must be a mapping")
            workflows[str(wf_name)] = WorkflowParser._parse_workflow_def(wf_value, str(wf_name))

        entrypoint = raw.get("entrypoint")
        if entrypoint is None and "main" in workflows:
            entrypoint = "main"

        return WorkflowDocument(
            version=version,
            name=raw.get("name"),
            meta=raw.get("meta"),
            functions=raw.get("functions"),
            exports=raw.get("exports"),
            entrypoint=entrypoint,
            workflows=workflows,
            raw_yaml=yaml_text,
        )

    @staticmethod
    def _parse_workflow_version(value: Any) -> int:
        if isinstance(value, bool):
            raise WorkflowParseException(f"Unsupported workflow version: {value}")

        if isinstance(value, int):
            return value

        if isinstance(value, float) and value == 1.0:
            return 1

        if isinstance(value, str):
            normalized = value.strip()
            if normalized == "1" or normalized == "1.0":
                return 1
            raise WorkflowParseException(f"Unsupported workflow version: {value}")

        raise WorkflowParseException(f"Unsupported workflow version: {value}")

    @staticmethod
    def _parse_workflow_def(node: dict[str, Any], name: str) -> WorkflowDef:
        steps_raw = node.get("steps")
        if not isinstance(steps_raw, list):
            raise WorkflowParseException(f"Workflow '{name}' missing required 'steps'")

        inputs = None
        if isinstance(node.get("inputs"), dict):
            inputs = {k: WorkflowParser._parse_input_def(v) for k, v in node["inputs"].items()}

        outputs = None
        if isinstance(node.get("outputs"), dict):
            outputs = {k: WorkflowParser._parse_output_def(v) for k, v in node["outputs"].items()}

        return WorkflowDef(
            inputs=inputs,
            functions=node.get("functions"),
            steps=[WorkflowParser._parse_step(step) for step in steps_raw],
            outputs=outputs,
        )

    @staticmethod
    def _parse_input_def(node: Any) -> InputDef:
        if isinstance(node, str):
            return InputDef(type=node)
        if not isinstance(node, dict):
            return InputDef()

        required_node = node.get("required")
        required = required_node if isinstance(required_node, bool) else True
        required_properties = required_node if isinstance(required_node, list) and required_node else None

        return InputDef(
            type=node.get("type", "any"),
            required=required,
            default=node.get("default"),
            items=WorkflowParser._parse_input_def(node["items"]) if "items" in node else None,
            properties={k: WorkflowParser._parse_input_def(v) for k, v in node.get("properties", {}).items()}
            if isinstance(node.get("properties"), dict)
            else None,
            additional_properties=WorkflowParser._parse_input_def(node["additional_properties"])
            if "additional_properties" in node
            else None,
            required_properties=required_properties,
            description=node.get("description"),
        )

    @staticmethod
    def _parse_output_def(node: Any) -> OutputDef:
        if isinstance(node, str):
            return OutputDef.from_expr(node)
        if not isinstance(node, dict):
            return OutputDef.from_expr("")

        # Long form with explicit expression.
        if "expr" in node:
            return OutputDef(
                expr=node.get("expr", ""),
                type=node.get("type", "any"),
                description=node.get("description"),
                items=WorkflowParser._parse_output_def(node["items"]) if "items" in node else None,
                properties={k: WorkflowParser._parse_output_def(v) for k, v in node.get("properties", {}).items()}
                if isinstance(node.get("properties"), dict)
                else None,
                additional_properties=WorkflowParser._parse_output_def(node["additional_properties"])
                if "additional_properties" in node
                else None,
                required_properties=node.get("required") if isinstance(node.get("required"), list) else None,
            )

        # Type-only schema branch (used in nested items/properties in .NET).
        if "type" in node:
            return OutputDef(
                type=node.get("type", "any"),
                description=node.get("description"),
                items=WorkflowParser._parse_output_def(node["items"]) if "items" in node else None,
                properties={k: WorkflowParser._parse_output_def(v) for k, v in node.get("properties", {}).items()}
                if isinstance(node.get("properties"), dict)
                else None,
                additional_properties=WorkflowParser._parse_output_def(node["additional_properties"])
                if "additional_properties" in node
                else None,
                required_properties=node.get("required") if isinstance(node.get("required"), list) else None,
            )

        # Backward-compatible nested mapping without `expr` or `type`.
        return OutputDef(expr="", type="object", properties={k: WorkflowParser._parse_output_def(v) for k, v in node.items()})

    @staticmethod
    def _parse_step(node: Any) -> StepDef:
        if not isinstance(node, dict):
            raise WorkflowParseException("Step must be a mapping")
        if "id" not in node or "type" not in node:
            raise WorkflowParseException("Step missing 'id' or 'type'")

        retry = None
        if isinstance(node.get("retry"), dict):
            retry = RetryPolicy(
                max=node["retry"].get("max", 1),
                backoff_ms=node["retry"].get("backoff_ms", 1000),
                backoff_mult=node["retry"].get("backoff_mult", 2.0),
                jitter_ms=node["retry"].get("jitter_ms", 0),
            )

        on_error = None
        if isinstance(node.get("on_error"), dict):
            on_error = OnErrorDef(cases=[])
            for case in node["on_error"].get("cases", []):
                if not isinstance(case, dict):
                    continue
                on_error.cases.append(
                    OnErrorCase(
                        **{
                            "if": case.get("if"),
                            "action": case.get("action", "stop"),
                            "set_output": case.get("set_output"),
                            "retry": RetryPolicy(**case["retry"]) if isinstance(case.get("retry"), dict) else None,
                        }
                    )
                )

        branches = None
        if isinstance(node.get("branches"), list):
            branches = []
            for branch in node["branches"]:
                if isinstance(branch, dict):
                    branches.append(BranchDef(steps=[WorkflowParser._parse_step(s) for s in branch.get("steps", [])]))

        cases = None
        if isinstance(node.get("cases"), list):
            cases = []
            for case in node["cases"]:
                if isinstance(case, dict):
                    raw_value = case.get("value")
                    if raw_value is None:
                        case_value = None
                    elif isinstance(raw_value, bool):
                        # Lowercase to match JS / .NET JsonValueToString.
                        case_value = "true" if raw_value else "false"
                    else:
                        case_value = str(raw_value)
                    cases.append(
                        SwitchCaseDef(
                            value=case_value,
                            when=case.get("when"),
                            steps=[WorkflowParser._parse_step(s) for s in case.get("steps", [])],
                        )
                    )

        return StepDef(
            **{
                "id": str(node["id"]),
                "type": str(node["type"]),
                "if": node.get("if"),
                "input": node.get("input"),
                "output": node.get("output"),
                "retry": retry,
                "on_error": on_error,
                "steps": [WorkflowParser._parse_step(s) for s in node.get("steps", [])] if isinstance(node.get("steps"), list) else None,
                "branches": branches,
                "cases": cases,
                "expr": node.get("expr"),
                "default": [WorkflowParser._parse_step(s) for s in node.get("default", [])] if isinstance(node.get("default"), list) else None,
                "item_var": node.get("item_var"),
                "index_var": node.get("index_var"),
            }
        )

