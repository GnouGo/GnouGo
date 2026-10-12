using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public partial class WorkflowTelemetryTests
{
    [Theory]
    [InlineData("valid", 5)]
    [InlineData("intermediate", 2)]
    [InlineData("final", 5)]
    public async Task OutputChecksUseWorkflowSpansWithOrderedPrivateOutcomes(string variant, int count)
    {
        var yaml = WorkflowOutputContractTests.CheckedOutputYaml;
        if (variant == "intermediate") yaml = yaml.Replace("source: pages, schema: {type: array}", "source: pages, schema: {type: array, maxItems: 0}", StringComparison.Ordinal);
        var recording = new RecordingTelemetry();
        var input = JsonNode.Parse(variant == "final" ? """{"pages":[["PRIVATE a","PRIVATE b","PRIVATE c","PRIVATE d"]]}""" : """{"pages":[["PRIVATE observed",null]]}""");
        var result = await new WorkflowEngine { Telemetry = recording }.ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"], input, TestContext.Current.CancellationToken);
        Assert.Equal(variant == "valid", result.Success);
        var events = Assert.Single(recording.WorkflowSpans).SpanEvents.Where(e => e.Name == "gnougo-flow.output.check").ToArray();
        Assert.Equal(count, events.Length);
        for (var i = 0; i < events.Length; i++)
        {
            var fields = events[i].Attributes!.ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(i, fields["gnougo-flow.check.order"]);
            Assert.Equal(variant != "valid" && i == count - 1 ? "failed" : "passed", fields["gnougo-flow.check.outcome"]);
            Assert.DoesNotContain("PRIVATE", string.Join('|', fields.Values));
            Assert.DoesNotContain("maxItems", string.Join('|', fields.Values));
            if (i == 3)
            {
                Assert.Equal("publish", fields["gnougo-flow.check.name"]);
                Assert.Equal("products", fields["gnougo-flow.check.reference"]);
                Assert.Equal("validateProducts", fields["gnougo-flow.check.origin"]);
            }
        }
        Assert.DoesNotContain(recording.StepSpans.SelectMany(s => s.SpanEvents), e => e.Name == "gnougo-flow.output.check");
    }

    [Fact]
    public async Task OutputCheckEventsDoNotGrowWithObservationsOrReappearOnCompletedRecovery()
    {
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(WorkflowOutputContractTests.CheckedOutputYaml)).Workflows["main"];
        string? reference = null;
        foreach (var size in new[] { 1, 1_200_000 })
        {
            var recording = new RecordingTelemetry(); var store = new InMemoryWorkflowRunStore();
            var engine = new WorkflowEngine { Telemetry = recording, RunStore = store, Limits = new() { TenantId = "tenant", RunId = "output-traces" } };
            var result = await engine.ExecuteAsync(workflow, new JsonObject { ["pages"] = new JsonArray(new JsonArray(new string('x', size))) }, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Error?.Message);
            var checks = string.Join('\n', recording.WorkflowSpans.Single().SpanEvents.Select(e => e.Name + ":" + string.Join('|', e.Attributes!.Select(a => a.Key + "=" + a.Value))));
            if (reference is not null) Assert.Equal(reference, checks); reference = checks;
            var saved = (await store.ReadAsync("tenant", "output-traces", TestContext.Current.CancellationToken))!;
            var resumedTelemetry = new RecordingTelemetry();
            var resumed = await new WorkflowEngine { RunStore = store, Telemetry = resumedTelemetry }.ResumeAsync("tenant", "output-traces", saved.Revision, workflow, TestContext.Current.CancellationToken);
            Assert.Equal(result.Outputs!.ToJsonString(), resumed.Outputs!.ToJsonString());
            Assert.Empty(resumedTelemetry.WorkflowSpans); Assert.Empty(resumedTelemetry.StepSpans);
        }
    }
}
