using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public partial class WorkflowTelemetryTests
{
    [Theory]
    [InlineData("valid", 2, 1)]
    [InlineData("first", 1, 0)]
    [InlineData("second", 2, 0)]
    [InlineData("skipped", 0, 0)]
    public async Task InputChecksAreOrderedPrivateAndRecordedBeforeDispatch(string variant, int checks, int calls)
    {
        var recording = new RecordingTelemetry(); var dispatched = 0;
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("local", new() { Tools = [new() { Name = "send", InputSchema = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"},"authorized":{"type":"boolean"}},"required":["text","authorized"]}""") }],
            ToolHandlers = new() { ["send"] = _ => {
                Assert.Equal(2, recording.StepSpans.Single().SpanEvents.Count(e => e.Name == "gnougo-flow.input.check"));
                dispatched++; return new() { Content = new JsonObject { ["done"] = true } };
            } } });
        var step = new StepDef { Id = "publish", Description = "Publish the observed value", Type = "mcp.call",
            If = variant == "skipped" ? "${false}" : null,
            Input = new JsonObject { ["server"] = "local", ["method"] = "send", ["request"] = new JsonObject {
                ["text"] = "${(()=>{const observedText={value:data.inputs.text};return observedText.value;})()}",
                ["authorized"] = "${(()=>{const permission={value:data.inputs.authorized};return permission.value;})()}" } },
            ExpressionContracts = JsonNode.Parse("""
                {"/request/text":{"observedText":{"origin":"/original/select","schema":{"type":"object","properties":{"value":{"type":"string","minLength":1}},"required":["value"]}}},
                 "/request/authorized":{"permission":{"origin":"/original/permission","schema":{"type":"object","properties":{"value":{"const":true}},"required":["value"]}}}}
                """)!.AsObject() };
        var workflow = new WorkflowCompiler().Compile(new WorkflowDocument { Workflows = new() { ["main"] = new() { Steps = [step] } } }).Workflows["main"];
        var result = await new WorkflowEngine { Telemetry = recording, McpClientFactory = factory }.ExecuteAsync(workflow,
            new JsonObject { ["text"] = variant == "first" ? "" : "PRIVATE observation ${value}", ["authorized"] = variant != "second" }, TestContext.Current.CancellationToken);
        Assert.True(result.Success == (variant is "valid" or "skipped"), result.Error?.Code + ": " + result.Error?.Message); Assert.Equal(calls, dispatched);
        var span = Assert.Single(recording.StepSpans);
        var checkEvents = span.SpanEvents.Where(e => e.Name == "gnougo-flow.input.check").ToArray();
        Assert.Equal(checks, checkEvents.Length);
        for (var i = 0; i < checks; i++)
        {
            var entry = checkEvents[i]; Assert.Equal("gnougo-flow.input.check", entry.Name);
            var fields = entry.Attributes!.ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(i, fields["gnougo-flow.check.order"]);
            Assert.Equal(i == 0 ? "observedText" : "permission", fields["gnougo-flow.check.name"]);
            Assert.Equal(i == 0 ? "/original/select" : "/original/permission", fields["gnougo-flow.check.origin"]);
            Assert.Equal(i == 0 ? "/expression_contracts/~1request~1text/observedText/schema" : "/expression_contracts/~1request~1authorized/permission/schema", fields["gnougo-flow.check.contract_pointer"]);
            Assert.Equal(!result.Success && i == checks - 1 ? "failed" : "passed", fields["gnougo-flow.check.outcome"]);
            Assert.DoesNotContain("PRIVATE", string.Join('|', fields.Values));
            Assert.DoesNotContain("minLength", string.Join('|', fields.Values));
        }
        if (!result.Success)
        {
            Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
            Assert.Equal(variant == "first" ? "/original/select" : "/original/permission", result.Error.Details!["location"]!.GetValue<string>());
            Assert.Equal("INPUT_VALIDATION", checkEvents[^1].Attributes!.Single(p => p.Key == "error.type").Value);
        }
    }

    [Fact]
    public async Task CheckEventsStayConstantWhenUnrelatedObservationsGrow()
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                - id: consume
                  type: set
                  input: |
                    ${(()=>{const selected={value:data.inputs.selected};return selected;})()}
                  expression_contracts:
                    '':
                      selected:
                        origin: /original/selection
                        schema: {type: object, properties: {value: {type: boolean}}, required: [value]}
            """;
        string? expected = null;
        foreach (var size in new[] { 0, 1_200_000 })
        {
            var trace = new RecordingTelemetry();
            var result = await new WorkflowEngine { Telemetry = trace }.ExecuteAsync(CompileMain(yaml),
                new JsonObject { ["selected"] = true, ["unread"] = new string('x', size) }, TestContext.Current.CancellationToken);
            Assert.True(result.Success);
            var check = Assert.Single(Assert.Single(trace.StepSpans).SpanEvents);
            var rendered = string.Join('|', check.Attributes!.Select(p => p.Key + "=" + p.Value));
            if (expected is not null) Assert.Equal(expected, rendered); expected = rendered;
            Assert.InRange(rendered.Length, 1, 512);
            TestContext.Current.TestOutputHelper?.WriteLine($"unreadBytes={size}; referenceOnlyEventCharacters={rendered.Length}; {rendered}");
        }
    }
}
