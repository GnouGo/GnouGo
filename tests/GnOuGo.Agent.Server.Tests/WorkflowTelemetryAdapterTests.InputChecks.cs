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
    [InlineData(true)] [InlineData(false)]
    public async Task CheckedInputsUseExistingStreamingAndPersistedStepEvents(bool allowed)
    {
        using var host = SmartFlowTestFactory.CreateTelemetryHarness();
        var events = new List<SmartFlowEvent>();
        var engine = new WorkflowEngine { Telemetry = new CompositeWorkflowTelemetry(new AgentStreamingTelemetry(events.Add), host.Telemetry) };
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                - id: publish
                  type: set
                  description: Check the selected value
                  input: |
                    ${(()=>{const permission={value:data.inputs.allowed};const selectedValue={value:data.inputs.text};return selectedValue;})()}
                  expression_contracts:
                    '':
                      permission:
                        origin: /original/requires
                        schema: {type: object, properties: {value: {const: true}}, required: [value]}
                      selectedValue:
                        origin: /original/selection
                        schema: {type: object, properties: {value: {type: string}}, required: [value]}
            """));
        var result = await engine.ExecuteAsync(document.Workflows["main"],
            new JsonObject { ["allowed"] = allowed, ["text"] = "PRIVATE observed text" }, TestContext.Current.CancellationToken);
        Assert.Equal(allowed, result.Success);
        var checks = events.Where(e => e.Type == "telemetry.step.event").Select(Json)
            .Where(e => e["event.name"]?.GetValue<string>() == "gnougo-flow.input.check").ToArray();
        Assert.Equal(allowed ? 2 : 1, checks.Length);
        Assert.Equal("permission", checks[0]["attributes"]!["gnougo-flow.check.name"]!.GetValue<string>());
        Assert.Equal(allowed ? "passed" : "failed", checks[0]["attributes"]!["gnougo-flow.check.outcome"]!.GetValue<string>());
        Assert.All(checks, c => Assert.DoesNotContain("PRIVATE", c.ToJsonString()));
        var persisted = new List<SpanRow>();
        while (host.Queue.Channel.Reader.TryRead(out var row)) if (row is SpanRow span) persisted.Add(span);
        var checkedSpan = Assert.Single(persisted, s => s.EventsJson?.Contains("gnougo-flow.input.check", StringComparison.Ordinal) == true);
        Assert.Contains("/original/requires", checkedSpan.EventsJson!);
        Assert.DoesNotContain("PRIVATE", checkedSpan.EventsJson!);
        Assert.Contains("Check the selected value", checkedSpan.AttributesJson!);
        if (allowed) Assert.Contains("/original/selection", checkedSpan.EventsJson!);
        else Assert.DoesNotContain("/original/selection", checkedSpan.EventsJson!);
    }
}
