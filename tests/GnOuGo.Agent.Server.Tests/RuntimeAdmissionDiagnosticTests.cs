using System.Text.Json.Nodes;
using GnOuGo.Agent.Planning.Benchmark;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RuntimeAdmissionDiagnosticTests
{
    [Theory]
    [InlineData("local", null, true)]
    [InlineData("mixed", "passed", true)]
    [InlineData("mixed", "stopped", false)]
    [InlineData("mixed", null, false)]
    [InlineData("stage1", "passed", false)]
    [InlineData("stage2", "passed", false)]
    [InlineData("replacement", "passed", false)]
    public void OnlyTwoGatedCasesAreAuthorized(string name, string? previous, bool allowed)
    {
        var report = previous is null ? null : new JsonObject { ["status"] = previous };
        if (allowed) RuntimeAdmissionDiagnosticRules.RequireCase(name, report);
        else Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireCase(name, report));
    }
    [Theory]
    [InlineData(8, "intent", "low", true)]
    [InlineData(9, "intent", "low", false)]
    [InlineData(1, "intent_operations", "low", true)]
    [InlineData(1, "intent_operations_repair", "low", true)]
    [InlineData(1, "behavior", "low", false)]
    [InlineData(1, "construction", "low", false)]
    [InlineData(1, "intent", "medium", false)]
    public void AllRequestsIncludingCorrectionsShareTheBound(int count, string phase, string reasoning, bool allowed)
    {
        var state = new PlanningSnapshot();
        for (var i = 0; i < count; i++) state.RequestAccounting.Add(new() { Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Phase = phase, Reasoning = reasoning });
        if (allowed) RuntimeAdmissionDiagnosticRules.RequireRequest(state);
        else Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireRequest(state));
    }
    [Fact]
    public void ReceiptReplayDoesNotIncreaseDistinctRequestAccounting()
    {
        var state = new PlanningSnapshot();
        for (var i = 0; i < 20; i++) state.RequestAccounting.Add(new() { Id = "same", Phase = "intent", Reasoning = "low" });
        RuntimeAdmissionDiagnosticRules.RequireRequest(state);
        state.ApprovedHash = "forbidden";
        Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireRequest(state));
    }

    [Fact]
    public void DomainReportingDoesNotExposeSourceOrTargetContent()
    {
        var schema = JsonNode.Parse("""{"properties":{"decision":{"anyOf":[{"properties":{"status":{"enum":["reuse","unresolved"]},"target":{"enum":["PRIVATE_REFERENCE"]}}}],"description":"PRIVATE_SOURCE"}}}""")!.AsObject();
        var report = RuntimeAdmissionDiagnosticRules.Domains(schema);
        Assert.Equal(["reuse", "unresolved"], report["status"]!.AsArray().Select(v => v!.ToString()));
        Assert.DoesNotContain("PRIVATE", report.ToJsonString());
    }
}
