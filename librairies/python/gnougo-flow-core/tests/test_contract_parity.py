from __future__ import annotations

import pytest

from gnougo_flow_core.compilation import WorkflowCompilationException, WorkflowCompiler
from gnougo_flow_core.json_schema import input_def_to_schema, output_def_to_schema
from gnougo_flow_core.json_schema_contract_validator import (
    normalize_schema,
    validate_instance,
    validate_schema,
)
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine
from gnougo_flow_core.workflow_plan_semantic_validator import (
    complete_inferable_function_parameter_jsdoc,
    validate_workflow_semantics,
)


def test_nullable_union_and_explicit_empty_required_properties_round_trip() -> None:
    document = WorkflowParser.parse(
        """
        version: 1
        workflows:
          main:
            inputs:
              config:
                type: [object, null]
                required_properties: []
                properties:
                  label: {type: string, required: false}
            steps: []
            outputs:
              result:
                expr: "${data.inputs.config}"
                type: [object, null]
                required_properties: []
                properties:
                  label: string
        """
    )
    workflow = document.workflows["main"]

    assert workflow.inputs["config"].nullable is True
    assert workflow.inputs["config"].required_properties == []
    assert input_def_to_schema(workflow.inputs["config"])["anyOf"][0]["required"] == []
    assert output_def_to_schema(workflow.outputs["result"])["anyOf"][0]["required"] == []


@pytest.mark.asyncio
async def test_nested_optional_properties_are_nullable_at_runtime() -> None:
    workflow = WorkflowCompiler().compile(
        WorkflowParser.parse(
            """
            version: 1
            workflows:
              main:
                inputs:
                  config:
                    type: object
                    properties:
                      label: {type: string, required: false, nullable: true}
                    required_properties: []
                steps:
                  - id: copy
                    type: set
                    input: {value: "${data.inputs.config.label}"}
            """
        )
    ).workflows["main"]

    result = await WorkflowEngine().execute_async(workflow, {"config": {"label": None}})

    assert result.success
    assert result.outputs["copy"] == {"value": None}


def test_conditional_and_dependent_required_schemas_are_validated() -> None:
    schema = normalize_schema(
        {
            "type": "object",
            "properties": {
                "kind": {"type": "string", "enum": ["file", "url"]},
                "path": {"type": "string"},
                "url": {"type": "string"},
                "token": {"type": ["string", None]},
                "tenant": {"type": "string"},
            },
            "if": {"properties": {"kind": {"const": "file"}}},
            "then": {"required": ["path"]},
            "else": {"required": ["url"]},
            "dependentRequired": {"token": ["tenant"]},
        }
    )

    assert validate_schema(schema) == []
    assert validate_instance({"kind": "file", "path": "a.txt", "token": None, "tenant": "t"}, schema) == []
    assert any("path" in error for error in validate_instance({"kind": "file"}, schema))
    assert any("url" in error for error in validate_instance({"kind": "url"}, schema))
    assert any("tenant" in error for error in validate_instance({"kind": "file", "path": "a", "token": "x"}, schema))


def test_enum_and_const_diagnostics_include_the_rejected_value() -> None:
    enum_errors = validate_instance("blue", {"type": "string", "enum": ["red", "green"]})
    const_errors = validate_instance(2, {"type": "integer", "const": 1})

    assert "'blue'" in enum_errors[0] and "'red'" in enum_errors[0]
    assert "2" in const_errors[0] and "1" in const_errors[0]


def test_balanced_nested_jsdoc_types_and_safe_completion() -> None:
    script = """
    /**
     * @param {Array<{id: string, meta: {active: boolean}}>} rows - Input rows.
     * @returns {Array<{id: string}>} - Projected rows.
     */
    function project(rows) { return rows.map(x => ({ id: x.id })); }

    /**
     * @returns {string} - Joined values.
     */
    function join(values) { return values.join(','); }
    """
    completed = complete_inferable_function_parameter_jsdoc(script)
    document = WorkflowParser.parse(
        """
        version: 1
        functions: |
          /**
           * @param {Array<{id: string, meta: {active: boolean}}>} rows - Input rows.
           * @returns {Array<{id: string}>} - Projected rows.
           */
          function project(rows) { return rows.map(x => ({ id: x.id })); }

          /**
           * @param {Array<object>} values - Type inferred from deterministic function usage.
           * @returns {string} - Joined values.
           */
          function join(values) { return values.join(','); }
        workflows:
          main:
            steps: []
        """
    )
    validate_workflow_semantics(document)

    assert "@param {Array<object>} values - Type inferred" in completed
    assert completed.count("@param {Array<object>} values") == 1


def test_step_ids_are_unique_across_main_and_finally_and_finalizer_cycles_count() -> None:
    duplicate = WorkflowParser.parse(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: cleanup
                type: set
                input: {value: main}
            finally:
              - id: cleanup
                type: set
                input: {value: final}
        """
    )

    with pytest.raises(WorkflowCompilationException):
        WorkflowCompiler().compile(duplicate)
