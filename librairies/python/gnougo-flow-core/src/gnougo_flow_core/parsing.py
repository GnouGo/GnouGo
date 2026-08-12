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
    WorkflowSkillDef,
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

        skill_raw = raw.get("skill") if "skill" in raw else raw.get("skills")

        return WorkflowDocument(
            version=version,
            name=raw.get("name"),
            meta=raw.get("meta"),
            skill=WorkflowParser._parse_workflow_skill(skill_raw) if isinstance(skill_raw, dict) else None,
            functions=raw.get("functions"),
            exports=raw.get("exports"),
            entrypoint=entrypoint,
            workflows=workflows,
            raw_yaml=yaml_text,
        )

    @staticmethod
    def parse_skill(yaml_text: str) -> WorkflowSkillDef | None:
        raw = yaml.safe_load(yaml_text)
        if not isinstance(raw, dict):
            return None
        skill_raw = raw.get("skill") if "skill" in raw else raw.get("skills")
        return WorkflowParser._parse_workflow_skill(skill_raw) if isinstance(skill_raw, dict) else None

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
            **{
                "finally": [WorkflowParser._parse_step(step) for step in node.get("finally", [])]
                if isinstance(node.get("finally"), list)
                else []
            },
            outputs=outputs,
        )

    @staticmethod
    def _parse_workflow_skill(node: dict[str, Any]) -> WorkflowSkillDef:
        tags = None
        if isinstance(node.get("tags"), list):
            tags = [str(tag).strip() for tag in node["tags"] if str(tag).strip()]

        inputs = None
        if isinstance(node.get("inputs"), dict):
            inputs = {
                str(k): WorkflowParser._parse_input_def(v)
                for k, v in node["inputs"].items()
                if str(k).strip()
            }

        outputs = None
        if isinstance(node.get("outputs"), dict):
            outputs = {
                str(k): WorkflowParser._parse_output_def(v)
                for k, v in node["outputs"].items()
                if str(k).strip()
            }

        return WorkflowSkillDef(
            description=node.get("description"),
            tags=tags,
            inputs=inputs,
            outputs=outputs,
        )

    @staticmethod
    def _parse_input_def(node: Any) -> InputDef:
        if isinstance(node, str):
            return InputDef(type=node)
        if not isinstance(node, dict):
            return InputDef()

        type_name, nullable = WorkflowParser._parse_contract_type(node)
        required_node = node.get("required")
        required = required_node if isinstance(required_node, bool) else True
        if "required_properties" in node:
            raw_required_properties = node.get("required_properties")
            required_properties = [str(value) for value in raw_required_properties] if isinstance(raw_required_properties, list) else []
        else:
            # Preserve the historical Python spelling as a compatible extension.
            required_properties = [str(value) for value in required_node] if isinstance(required_node, list) else None

        return InputDef(
            type=type_name,
            required=required,
            nullable=nullable,
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
            type_name, nullable = WorkflowParser._parse_contract_type(node)
            return OutputDef(
                expr=node.get("expr", ""),
                type=type_name,
                nullable=nullable,
                description=node.get("description"),
                items=WorkflowParser._parse_output_def(node["items"]) if "items" in node else None,
                properties={k: WorkflowParser._parse_output_def(v) for k, v in node.get("properties", {}).items()}
                if isinstance(node.get("properties"), dict)
                else None,
                additional_properties=WorkflowParser._parse_output_def(node["additional_properties"])
                if "additional_properties" in node
                else None,
                required_properties=WorkflowParser._parse_required_properties(node),
            )

        # Type-only schema branch (used in nested items/properties in .NET).
        if "type" in node:
            type_name, nullable = WorkflowParser._parse_contract_type(node)
            return OutputDef(
                type=type_name,
                nullable=nullable,
                description=node.get("description"),
                items=WorkflowParser._parse_output_def(node["items"]) if "items" in node else None,
                properties={k: WorkflowParser._parse_output_def(v) for k, v in node.get("properties", {}).items()}
                if isinstance(node.get("properties"), dict)
                else None,
                additional_properties=WorkflowParser._parse_output_def(node["additional_properties"])
                if "additional_properties" in node
                else None,
                required_properties=WorkflowParser._parse_required_properties(node),
            )

        # Backward-compatible nested mapping without `expr` or `type`.
        return OutputDef(expr="", type="object", properties={k: WorkflowParser._parse_output_def(v) for k, v in node.items()})

    @staticmethod
    def _parse_contract_type(node: dict[str, Any]) -> tuple[str, bool]:
        nullable = node.get("nullable") is True
        raw_type = node.get("type", "any")
        if isinstance(raw_type, str):
            return raw_type, nullable
        if isinstance(raw_type, list):
            normalized = list(
                dict.fromkeys(
                    "null" if value is None else str(value).strip().lower()
                    for value in raw_type
                    if value is None or str(value).strip()
                )
            )
            non_null = [value for value in normalized if value != "null"]
            if len(non_null) == 1 and "null" in normalized:
                return non_null[0], True
        return "any", nullable

    @staticmethod
    def _parse_required_properties(node: dict[str, Any]) -> list[str] | None:
        if "required_properties" in node:
            raw = node.get("required_properties")
            return [str(value) for value in raw] if isinstance(raw, list) else []
        raw = node.get("required")
        return [str(value) for value in raw] if isinstance(raw, list) else None

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
                "output_schema": node.get("output_schema"),
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
