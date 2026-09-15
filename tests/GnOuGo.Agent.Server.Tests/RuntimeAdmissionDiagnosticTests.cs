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
    [InlineData(16, "intent", "low", true)]
    [InlineData(17, "intent", "low", true)] // Checkpoint accounting cannot masquerade as a dispatch limit.
    [InlineData(1, "intent_operations", "low", true)]
    [InlineData(1, "intent_operations_repair", "low", true)]
    [InlineData(1, "intent_relations", "low", true)]
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
    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void PackedRequestsMustFitBeforeDispatch(int pages, bool allowed)
    {
        Assert.Equal(16, RuntimeAdmissionDiagnosticRules.MaxCalls);
        if (allowed) RuntimeAdmissionDiagnosticRules.RequirePreflight(pages);
        else Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequirePreflight(pages));
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

    private static PlanningObligation Operation(string id, string kind, string[] inputs, string[] outputs, string[] producers) =>
        new(id, ["evidence"], "workflow", kind, true)
        {
            OperationAdmission = new(6, id, "evidence", null,
                [new("decision", "clause", "action", kind, PlanningOperationNecessity.Required, null, null)
                { Effect = new(1, "effect", "realizes", [], inputs.ToList(), outputs.ToList(), producers.ToList(), ["clause"], "model", "proof") }], "proof", "fingerprint")
        };

    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing_input", false)]
    [InlineData("missing_output", false)]
    [InlineData("identity_call", false)]
    [InlineData("extra_operation", false)]
    public void LocalGateRequiresCompleteEffectProof(string defect, bool allowed)
    {
        var ops = new List<PlanningObligation> { Operation("local", "local_processing",
            defect == "missing_input" ? ["record"] : ["record", "threshold"], defect == "missing_output" ? [] : ["result"], []) };
        if (defect == "extra_operation") ops.Add(Operation("extra", "external_write", [], [], []));
        void Check() => RuntimeAdmissionDiagnosticRules.RequireEffects("local", ops, ["record", "threshold"], "result", defect == "identity_call" ? 1 : 0);
        if (allowed) Check(); else Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(Check);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedGateRequiresGroundedReadDependency(bool dependency)
    {
        var ops = new[] { Operation("read", "external_read", ["source"], [], []),
            Operation("local", "local_processing", ["threshold"], ["result"], dependency ? ["read"] : []) };
        void Check() => RuntimeAdmissionDiagnosticRules.RequireEffects("mixed", ops, ["source", "threshold"], "result", 0);
        if (dependency) Check(); else Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(Check);
    }
}
