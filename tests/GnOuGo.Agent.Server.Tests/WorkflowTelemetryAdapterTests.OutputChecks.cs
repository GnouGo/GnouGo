using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using OtlpTenantCollector.Models;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class WorkflowTelemetryAdapterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckedOutputsUseExistingWorkflowStreamingAndOtelEvents(bool allowed)
    {
        using var host = SmartFlowTestFactory.CreateTelemetryHarness();
        var events = new List<SmartFlowEvent>();
        var engine = new WorkflowEngine { Telemetry = new CompositeWorkflowTelemetry(new AgentStreamingTelemetry(events.Add), host.Telemetry) };
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps: []
                outputs:
                  observed:
                    type: string
                    expr: |-
                      ${(()=>{const allowed=data.inputs.allowed;const observed=data.inputs.text;return observed;})()}
                    expression_contracts:
                      '':
                        allowed: {origin: /original/permission, order: 0, source: allowed, schema: {const: true}}
                        observed: {origin: /original/selection, order: 1, source: observed, schema: {type: string}}
                        selected: {origin: /original/export, order: 2, source: observed, schema: {type: string}}
            """)).Workflows["main"];
        var result = await engine.ExecuteAsync(workflow, new JsonObject { ["allowed"] = allowed, ["text"] = "PRIVATE observed" }, TestContext.Current.CancellationToken);
        Assert.Equal(allowed, result.Success);
        var checks = events.Where(e => e.Type == "telemetry.workflow.event").Select(Json)
            .Where(e => e["event.name"]?.GetValue<string>() == "gnougo-flow.output.check").ToArray();
        Assert.Equal(allowed ? 4 : 1, checks.Length);
        Assert.Equal(allowed ? "passed" : "failed", checks[0]["attributes"]!["gnougo-flow.check.outcome"]!.GetValue<string>());
        Assert.All(checks, c => Assert.DoesNotContain("PRIVATE", c.ToJsonString()));
        var persisted = new List<SpanRow>();
        while (host.Queue.Channel.Reader.TryRead(out var row)) if (row is SpanRow span) persisted.Add(span);
        var checkedSpan = Assert.Single(persisted, s => s.EventsJson?.Contains("gnougo-flow.output.check", StringComparison.Ordinal) == true);
        Assert.Contains("/original/permission", checkedSpan.EventsJson!); Assert.DoesNotContain("PRIVATE", checkedSpan.EventsJson!);
        if (allowed) Assert.Contains("/original/export", checkedSpan.EventsJson!);
        else Assert.DoesNotContain("/original/selection", checkedSpan.EventsJson!);
    }
}
