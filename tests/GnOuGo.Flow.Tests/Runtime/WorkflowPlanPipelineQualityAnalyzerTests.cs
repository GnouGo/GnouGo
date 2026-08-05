using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime.Executors;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowPlanPipelineQualityAnalyzerTests
{
    [Fact]
    public void InferredMainInputs_RejectUnrequestedOperationalArtifact()
    {
        var inferred = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(StringComparer.Ordinal)
        {
            ["request_url"] = System.Text.Json.Nodes.JsonValue.Create("string"),
            ["project_root"] = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "string",
                ["description"] = "Existing workspace-relative project root."
            }
        };

        var ex = Assert.Throws<WorkflowRuntimeException>(() =>
            WorkflowPlanExecutor.ValidateInferredMainArtifactInputs(
                inferred,
                new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(),
                "Create an agent that accepts a request URL."));

        Assert.Contains("PIPELINE_MAIN_UNREQUESTED_ARTIFACT_INPUT", ex.Message, StringComparison.Ordinal);
        Assert.Contains("project_root", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InferredMainInputs_AllowExplicitlyRequestedOrConfiguredArtifact()
    {
        var inputs = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(StringComparer.Ordinal)
        {
            ["project_root"] = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "string",
                ["description"] = "Existing workspace-relative project root."
            }
        };

        WorkflowPlanExecutor.ValidateInferredMainArtifactInputs(
            inputs,
            new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(),
            "Create an agent that accepts a project root as an input.");
        WorkflowPlanExecutor.ValidateInferredMainArtifactInputs(
            inputs,
            inputs,
            "Create an agent.");
    }

    [Fact]
    public void LeafArtifactOutputProvenance_AllowsDirectExternalResponse()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            workflows:
              materialize:
                steps:
                  - id: create_workspace
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      method: create_workspace
                      request: {}
                outputs:
                  workspace_root:
                    expr: "${data.steps.create_workspace.response.workspaceRoot}"
                    type: string
            """);

        Assert.True(WorkflowPlanPipelineQualityAnalyzer.IsLeafArtifactOutputProven(
            document,
            "materialize",
            "workspace_root",
            out var failureReason), failureReason);
    }

    [Fact]
    public void LeafArtifactOutputProvenance_RejectsOpaqueAggregateFunction()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            functions: |
              /**
               * Wraps a workspace result.
               * @param {string} workspaceRoot - Existing workspace root.
               * @returns {{workspace_root:string}} Wrapped result.
               */
              function wrap(workspaceRoot) { return { workspace_root: workspaceRoot }; }
            workflows:
              materialize:
                steps:
                  - id: create_workspace
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      method: create_workspace
                      request: {}
                  - id: aggregate
                    type: set
                    input: "${functions.wrap(data.steps.create_workspace.response.workspaceRoot)}"
                outputs:
                  workspace_root:
                    expr: "${data.steps.aggregate.workspace_root}"
                    type: string
            """);

        Assert.False(WorkflowPlanPipelineQualityAnalyzer.IsLeafArtifactOutputProven(
            document,
            "materialize",
            "workspace_root",
            out var failureReason));
        Assert.Contains("not an exact caller input", failureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void LeafArtifactOutputProvenance_AllowsTransparentSetAliasAndCallerInput()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            workflows:
              reuse:
                inputs:
                  workspace_root: { type: string, required: true }
                steps:
                  - id: alias
                    type: set
                    input:
                      workspace_root: "${data.inputs.workspace_root}"
                outputs:
                  workspace_root:
                    expr: "${data.steps.alias.workspace_root}"
                    type: string
            """);

        Assert.True(WorkflowPlanPipelineQualityAnalyzer.IsLeafArtifactOutputProven(
            document,
            "reuse",
            "workspace_root",
            out var failureReason), failureReason);
    }

    [Fact]
    public void ExternalArtifactReadiness_AllowsSameLogicalStepIdInExclusiveSwitchBranches()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            name: branch_local_result_ids
            workflows:
              main:
                inputs:
                  publish:
                    type: boolean
                steps:
                  - id: route
                    type: switch
                    cases:
                      - when: "${data.inputs.publish}"
                        steps:
                          - id: result
                            type: set
                            input:
                              message: published
                    default:
                      - id: result
                        type: set
                        input:
                          message: skipped
            """);

        WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document);
    }

    [Fact]
    public void ExternalArtifactReadiness_RejectsMainSynthesizedArtifactPassedToExternalConsumer()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            name: artifact_readiness_rejects_synthesized_project_root
            workflows:
              main:
                inputs:
                  issue_number:
                    type: number
                    required: true
                steps:
                  - id: derive_project_context
                    type: set
                    input:
                      project_root: "clones/repo-issue-${toString(data.inputs.issue_number)}"
                  - id: suggest_change
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      kind: tool
                      method: code_suggest_change
                      request:
                        projectRoot: "${data.steps.derive_project_context.project_root}"
                outputs:
                  ok:
                    expr: "${data.steps.suggest_change.response}"
                    type: string
            """);

        var ex = Assert.Throws<WorkflowRuntimeException>(() =>
            WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document));

        Assert.Contains(WorkflowPlanPipelineQualityAnalyzer.UnprovenExternalArtifactCode, ex.Message, StringComparison.Ordinal);
        Assert.Contains("derive_project_context", ex.Message, StringComparison.Ordinal);
        Assert.Contains("projectRoot", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalArtifactReadiness_AllowsCallerProvidedArtifactInput()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            name: artifact_readiness_allows_workflow_input
            workflows:
              main:
                inputs:
                  project_root:
                    type: string
                    required: true
                steps:
                  - id: suggest_change
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      kind: tool
                      method: code_suggest_change
                      request:
                        projectRoot: "${data.inputs.project_root}"
                outputs:
                  ok:
                    expr: "${data.steps.suggest_change.response}"
                    type: string
            """);

        WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document);
    }

    [Fact]
    public void ExternalArtifactReadiness_AllowsDeclaredArtifactInputOnUncalledReusableLeaf()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            workflows:
              main:
                steps:
                  - id: done
                    type: set
                    input: { ok: true }
              reusable_analysis:
                inputs:
                  project_root:
                    type: string
                    required: true
                steps:
                  - id: analyze
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      method: analyze
                      request:
                        projectRoot: "${data.inputs.project_root}"
            """);

        WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document);
    }

    [Fact]
    public void ExternalArtifactReadiness_AllowsUpstreamWorkflowCallProducedArtifact()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            name: artifact_readiness_allows_leaf_output
            workflows:
              main:
                inputs:
                  repository_url:
                    type: string
                    required: true
                steps:
                  - id: clone_repository
                    type: workflow.call
                    input:
                      ref:
                        kind: local
                        name: clone_repository_leaf
                      args:
                        repository_url: "${data.inputs.repository_url}"
                  - id: suggest_change
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      kind: tool
                      method: code_suggest_change
                      request:
                        projectRoot: "${data.steps.clone_repository.project_root}"
                outputs:
                  ok:
                    expr: "${data.steps.suggest_change.response}"
                    type: string
              clone_repository_leaf:
                inputs:
                  repository_url:
                    type: string
                    required: true
                steps:
                  - id: clone
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      method: clone_repository
                      request:
                        repositoryUrl: "${data.inputs.repository_url}"
                outputs:
                  project_root:
                    expr: "${data.steps.clone.response.projectRootRelative}"
                    type: string
            """);

        WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document);
    }

    [Fact]
    public void ExternalArtifactReadiness_TracksNestedArtifactThroughCompositeLeafInput()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            workflows:
              main:
                inputs:
                  repository_url: { type: string, required: true }
                steps:
                  - id: clone_repository
                    type: workflow.call
                    input:
                      ref: { kind: local, name: clone_repository_leaf }
                      args:
                        repository_url: "${data.inputs.repository_url}"
                  - id: shape_review_input
                    type: set
                    input:
                      review_input:
                        projectRoot: "${data.steps.clone_repository.outputs.project_root}"
                  - id: review
                    type: workflow.call
                    input:
                      ref: { kind: local, name: review_leaf }
                      args:
                        review_input: "${data.steps.shape_review_input.review_input}"
              clone_repository_leaf:
                inputs:
                  repository_url: { type: string, required: true }
                steps:
                  - id: clone
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      method: clone
                      request:
                        repositoryUrl: "${data.inputs.repository_url}"
                outputs:
                  project_root:
                    expr: "${data.steps.clone.response.projectRoot}"
                    type: string
              review_leaf:
                inputs:
                  review_input:
                    type: object
                    properties:
                      projectRoot: { type: string }
                    required_properties: [projectRoot]
                steps:
                  - id: analyze
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      method: analyze
                      request:
                        projectRoot: "${data.inputs.review_input.projectRoot}"
            """);

        WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document);
    }

    [Fact]
    public void ExternalArtifactReadiness_TracksArtifactThroughLoopItemProjection()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            workflows:
              main:
                inputs:
                  repository_url: { type: string, required: true }
                steps:
                  - id: clone_repository
                    type: workflow.call
                    input:
                      ref: { kind: local, name: clone_repository_leaf }
                      args:
                        repository_url: "${data.inputs.repository_url}"
                  - id: prepare_review_inputs
                    type: set
                    input:
                      items:
                        - projectRoot: "${data.steps.clone_repository.outputs.project_root}"
                          batch: 0
                  - id: review_batches
                    type: loop.sequential
                    input:
                      items: "${data.steps.prepare_review_inputs.items}"
                    item_var: prepared_review_inputs
                    steps:
                      - id: call_review
                        type: mcp.call
                        input:
                          server: Example.Mcp
                          method: review
                          request:
                            projectRoot: "${data.prepared_review_inputs.projectRoot}"
              clone_repository_leaf:
                inputs:
                  repository_url: { type: string, required: true }
                steps:
                  - id: clone
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      method: clone
                      request:
                        repositoryUrl: "${data.inputs.repository_url}"
                outputs:
                  project_root:
                    expr: "${data.steps.clone.response.projectRoot}"
                    type: string
            """);

        WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document);
    }

    [Fact]
    public void ExternalArtifactReadiness_RejectsFabricatedArtifactInsideLoopItemProjection()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            workflows:
              main:
                steps:
                  - id: prepare_review_inputs
                    type: set
                    input:
                      items:
                        - projectRoot: invented/project/root
                  - id: review_batches
                    type: loop.sequential
                    input:
                      items: "${data.steps.prepare_review_inputs.items}"
                    item_var: prepared_review_inputs
                    steps:
                      - id: call_review
                        type: mcp.call
                        input:
                          server: Example.Mcp
                          method: review
                          request:
                            projectRoot: "${data.prepared_review_inputs.projectRoot}"
            """);

        var ex = Assert.Throws<WorkflowRuntimeException>(() =>
            WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document));

        Assert.Contains(WorkflowPlanPipelineQualityAnalyzer.UnprovenExternalArtifactCode, ex.Message, StringComparison.Ordinal);
        Assert.Contains("prepared_review_inputs.projectRoot", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalArtifactReadiness_RejectsArtifactFabricatedInsideDeterministicLeaf()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            workflows:
              main:
                inputs:
                  repository_url: { type: string, required: true }
                steps:
                  - id: prepare
                    type: workflow.call
                    input:
                      ref: { kind: local, name: prepare_context }
                      args:
                        repository_url: "${data.inputs.repository_url}"
                  - id: analyze
                    type: workflow.call
                    input:
                      ref: { kind: local, name: analyze_context }
                      args:
                        project_root: "${data.steps.prepare.outputs.project_root}"
              prepare_context:
                inputs:
                  repository_url: { type: string, required: true }
                steps:
                  - id: fabricate
                    type: set
                    input:
                      project_root: "artifact://${data.inputs.repository_url}"
                outputs:
                  project_root: { expr: "${data.steps.fabricate.project_root}", type: string }
              analyze_context:
                inputs:
                  project_root: { type: string, required: true }
                steps:
                  - id: analyze
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      method: analyze
                      request:
                        projectRoot: "${data.inputs.project_root}"
            """);

        var ex = Assert.Throws<WorkflowRuntimeException>(() =>
            WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document));

        Assert.Contains(WorkflowPlanPipelineQualityAnalyzer.UnprovenExternalArtifactCode, ex.Message, StringComparison.Ordinal);
        Assert.Contains("projectRoot", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalArtifactReadiness_AllowsNormalScalarShaping()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            name: artifact_readiness_allows_scalar_shaping
            workflows:
              main:
                inputs:
                  issue_number:
                    type: number
                    required: true
                steps:
                  - id: shape_issue
                    type: set
                    input:
                      issue_number: "${data.inputs.issue_number}"
                  - id: fetch_issue
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      kind: tool
                      method: get_issue
                      request:
                        issueNumber: "${data.steps.shape_issue.issue_number}"
                outputs:
                  ok:
                    expr: "${data.steps.fetch_issue.response}"
                    type: string
            """);

        WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document);
    }

    [Fact]
    public void ExternalArtifactReadiness_AllowsLiteralOwnedCreationTarget()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            workflows:
              main:
                steps:
                  - id: create
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      method: create_artifact
                      request:
                        targetDirectory: .data/generated/run
            """);

        WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document);
    }

    [Fact]
    public void ExternalArtifactReadiness_AllowsLogicalPathSelectedLocally()
    {
        var document = WorkflowParser.Parse(
            """
            version: 1
            workflows:
              main:
                inputs:
                  changed_path: { type: string, required: true }
                steps:
                  - id: select_path
                    type: set
                    input:
                      path: "${data.inputs.changed_path}"
                  - id: publish_reference
                    type: mcp.call
                    input:
                      server: Example.Mcp
                      method: publish_reference
                      request:
                        path: "${data.steps.select_path.path}"
            """);

        WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document);
    }
}
