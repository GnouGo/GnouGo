using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowPlanDiagnosticsTests
{
    [Theory]
    [InlineData(408)]
    [InlineData(425)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void IsTransientProviderFailure_RecognizesTypedOrStatusBasedUpstreamFailures(int status)
    {
        Assert.True(WorkflowPlanDiagnostics.IsTransientProviderFailure(new HttpRequestException(
            "redacted provider failure",
            inner: null,
            statusCode: (System.Net.HttpStatusCode)status)));
    }

    [Fact]
    public void IsTransientProviderFailure_DoesNotInferDispositionFromMessageText()
        => Assert.False(WorkflowPlanDiagnostics.IsTransientProviderFailure(
            new InvalidOperationException("server_error 503 routing failed")));

    [Fact]
    public void IsTransientProviderFailure_DoesNotTreatStableWorkflowDiagnosticsAsTransient()
    {
        Assert.False(WorkflowPlanDiagnostics.IsTransientProviderFailure(
            new InvalidOperationException("Generated output schema at workflows.main.outputs.items is missing array items.")));
    }

    [Fact]
    public void BuildDiagnosticFingerprint_NormalizesGeneratedNamesForSameArtifactObligation()
    {
        static WorkflowRuntimeException Error(string workflow, string step) => new(
            ErrorCodes.TemplatePlan,
            "Artifact provenance failed.",
            details: new JsonObject
            {
                ["diagnostics"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["code"] = "PIPELINE_MAIN_UNPROVEN_EXTERNAL_ARTIFACT",
                        ["workflow"] = workflow,
                        ["step"] = step,
                        ["request_field"] = "input.request.projectRoot"
                    }
                }
            });

        var first = WorkflowPlanDiagnostics.BuildDiagnosticFingerprint(Error("review_one", "analyze_one"));
        var second = WorkflowPlanDiagnostics.BuildDiagnosticFingerprint(Error("review_two", "analyze_two"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void BuildDiagnosticFingerprint_IsIndependentOfDiagnosticOrder()
    {
        static WorkflowRuntimeException Error(params JsonObject[] diagnostics) => new(
            ErrorCodes.TemplatePlan,
            "Validation failed.",
            details: new JsonObject
            {
                ["diagnostics"] = new JsonArray(diagnostics.Select(static diagnostic => (JsonNode)diagnostic).ToArray())
            });
        var first = new JsonObject
        {
            ["code"] = "OUTPUT_CONTRACT_INCOMPLETE",
            ["leaf_name"] = "first_leaf",
            ["invalid_path"] = "outputs.result"
        };
        var second = new JsonObject
        {
            ["code"] = "OUTPUT_CONTRACT_INCOMPLETE",
            ["leaf_name"] = "second_leaf",
            ["invalid_path"] = "outputs.result"
        };

        Assert.Equal(
            WorkflowPlanDiagnostics.BuildDiagnosticFingerprint(Error(
                (JsonObject)first.DeepClone(),
                (JsonObject)second.DeepClone())),
            WorkflowPlanDiagnostics.BuildDiagnosticFingerprint(Error(
                (JsonObject)second.DeepClone(),
                (JsonObject)first.DeepClone())));
    }
}
