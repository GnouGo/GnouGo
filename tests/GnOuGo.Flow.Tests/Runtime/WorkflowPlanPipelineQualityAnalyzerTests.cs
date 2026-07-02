using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime.Executors;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowPlanPipelineQualityAnalyzerTests
{
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
                  - id: emit
                    type: set
                    input:
                      project_root: "clones/repo"
                outputs:
                  project_root:
                    expr: "${data.steps.emit.project_root}"
                    type: string
            """);

        WorkflowPlanPipelineQualityAnalyzer.ValidateExternalArtifactReadiness(document);
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
}
