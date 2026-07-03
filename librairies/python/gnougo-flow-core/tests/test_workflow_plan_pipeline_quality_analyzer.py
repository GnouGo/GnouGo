import pytest

from gnougo_flow_core.errors import WorkflowRuntimeException
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.workflow_plan_pipeline_quality_analyzer import (
    UNPROVEN_EXTERNAL_ARTIFACT_CODE,
    analyze_external_artifact_readiness,
    build_main_dataflow_quality_details,
    validate_external_artifact_readiness,
)


def test_pipeline_quality_analyzer_rejects_main_synthesized_artifact_locator() -> None:
    document = WorkflowParser.parse(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: derive
                type: set
                input:
                  project_root: /tmp/generated
              - id: read
                type: mcp.call
                input:
                  server: files
                  method: read_file
                  request:
                    path: "${data.steps.derive.project_root}"
        """
    )

    diagnostics = analyze_external_artifact_readiness(document)

    assert len(diagnostics) == 1
    assert diagnostics[0]["code"] == UNPROVEN_EXTERNAL_ARTIFACT_CODE
    assert diagnostics[0]["consumer_step"] == "read"
    assert diagnostics[0]["producer_step"] == "derive"
    assert diagnostics[0]["source_kind"] == "main_set"

    details = build_main_dataflow_quality_details(diagnostics)
    assert details["root_causes"][0]["category"] == "unproven_external_artifact"

    with pytest.raises(WorkflowRuntimeException):
        validate_external_artifact_readiness(document)


def test_pipeline_quality_analyzer_allows_caller_provided_artifact_locator() -> None:
    document = WorkflowParser.parse(
        """
        version: 1
        workflows:
          main:
            inputs:
              project_root: string
            steps:
              - id: read
                type: mcp.call
                input:
                  server: files
                  method: read_file
                  request:
                    path: "${data.inputs.project_root}"
        """
    )

    assert analyze_external_artifact_readiness(document) == []


def test_pipeline_quality_analyzer_allows_external_leaf_produced_artifact_locator() -> None:
    document = WorkflowParser.parse(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: checkout
                type: workflow.call
                input:
                  ref:
                    kind: local
                    name: clone_repo
              - id: read
                type: mcp.call
                input:
                  server: files
                  method: read_file
                  request:
                    path: "${data.steps.checkout.outputs.project_root}"
          clone_repo:
            steps:
              - id: done
                type: set
                input:
                  project_root: /tmp/repo
        """
    )

    assert analyze_external_artifact_readiness(document) == []
