using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowPlanDiagnosticsTests
{
    [Fact]
    public void BuildCompactStructuredPlanError_BoundsAndDeduplicatesRepairContext()
    {
        var diagnostics = new JsonArray();
        for (var i = 0; i < 30; i++)
        {
            var allowedPaths = new JsonArray();
            for (var path = 0; path < 20; path++)
                allowedPaths.Add((JsonNode)JsonValue.Create($"data.steps.step_{path}.value")!);

            diagnostics.Add((JsonNode)new JsonObject
            {
                ["code"] = "STEP_REFERENCE_NOT_AVAILABLE",
                ["phase"] = "semantic_validation",
                ["workflow"] = "main",
                ["step"] = $"consumer_{i}",
                ["field"] = "input.value",
                ["invalid_path"] = $"data.steps.producer_{i}.value",
                ["message"] = new string('m', 4_000),
                ["allowed_paths"] = allowedPaths
            });
        }

        var details = new JsonObject
        {
            ["phase"] = "validation",
            ["summary"] = "30 diagnostics",
            ["generated_yaml"] = "SENSITIVE_FULL_YAML",
            ["invalid_yaml"] = "SENSITIVE_FULL_YAML",
            ["diagnostics"] = diagnostics
        };
        var exception = new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Generated workflow validation failed | repair diagnostics: " + new string('x', 50_000),
            details: details);

        var compact = WorkflowPlanDiagnostics.BuildCompactStructuredPlanError(exception, 3);
        var parsed = JsonNode.Parse(compact)!.AsObject();

        Assert.True(compact.Length < 24_000);
        Assert.DoesNotContain("SENSITIVE_FULL_YAML", compact);
        Assert.Equal("Generated workflow validation failed", parsed["message"]!.GetValue<string>());
        Assert.Equal(12, parsed["details"]!["diagnostics"]!.AsArray().Count);
        Assert.Equal(18, parsed["details"]!["diagnostics_omitted"]!.GetValue<int>());
        Assert.Equal(8, parsed["details"]!["diagnostics"]![0]!["allowed_paths"]!.AsArray().Count);
    }

    [Fact]
    public void BuildCompactStructuredPlanError_GroupsUnavailableReferencesByProducer()
    {
        var diagnostics = new JsonArray();
        for (var i = 0; i < 12; i++)
        {
            diagnostics.Add((JsonNode)new JsonObject
            {
                ["code"] = "STEP_REFERENCE_NOT_AVAILABLE",
                ["workflow"] = "main",
                ["step"] = $"consumer_{i}",
                ["field"] = $"input.field_{i}",
                ["invalid_path"] = $"data.steps.conditional_producer.outputs.field_{i}",
                ["message"] = "Producer is not guaranteed on this path."
            });
        }

        var exception = new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Generated workflow validation failed",
            details: new JsonObject { ["diagnostics"] = diagnostics });

        var parsed = JsonNode.Parse(
            WorkflowPlanDiagnostics.BuildCompactStructuredPlanError(exception, 1))!.AsObject();
        var grouped = Assert.Single(parsed["details"]!["diagnostics"]!.AsArray());

        Assert.Equal("conditional_producer", grouped!["producer_step"]!.GetValue<string>());
        Assert.Equal(8, grouped["affected_references"]!.AsArray().Count);
        Assert.Equal(4, grouped["affected_references_omitted"]!.GetValue<int>());
    }
}
