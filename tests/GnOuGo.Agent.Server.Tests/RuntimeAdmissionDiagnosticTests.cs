using System.Text.Json.Nodes;
using GnOuGo.Agent.Planning.Benchmark;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RuntimeAdmissionDiagnosticTests
{
    [Theory]
    [InlineData("valid", "passed")]
    [InlineData("missing", "blocked")]
    [InlineData("foreign", "blocked")]
    [InlineData("future", "blocked")]
    [InlineData("transport", "blocked")]
    public async Task CurrencyPrerequisiteChecksOnceWithoutRetry(string condition, string expected)
    {
        var rates = new RecordingRates(condition);
        var report = await RuntimeAdmissionDiagnostic.CheckExchangeRateAsync(new(0.01m, "USD"), "EUR", rates, TestContext.Current.CancellationToken);
        Assert.Equal(expected, report["status"]!.ToString());
        Assert.Equal(1, rates.Calls);
        Assert.Equal(0, report["modelCalls"]!.GetValue<int>());
        Assert.Equal("USD", report["sourceCurrency"]!.ToString());
        Assert.Equal("EUR", report["targetCurrency"]!.ToString());
        Assert.DoesNotContain("PRIVATE", report.ToJsonString());
    }

    private sealed class RecordingRates(string condition) : IExchangeRateProvider
    {
        public int Calls { get; private set; }
        public ValueTask<CurrencyExchangeQuote?> GetQuoteAsync(string sourceCurrency, string targetCurrency, CancellationToken ct)
        {
            Calls++;
            if (condition == "transport") throw new HttpRequestException("PRIVATE_TRANSPORT_DETAIL");
            return ValueTask.FromResult<CurrencyExchangeQuote?>(condition == "missing" ? null : new(sourceCurrency,
                condition == "foreign" ? "GBP" : targetCurrency, 0.9m,
                DateTimeOffset.UtcNow.AddHours(condition == "future" ? 1 : -1), "test"));
        }
    }

    [Theory]
    [InlineData("local", null, true)]
    [InlineData("mixed", "passed", false)]
    [InlineData("mixed", "stopped", false)]
    [InlineData("mixed", null, false)]
    [InlineData("stage1", "passed", false)]
    [InlineData("stage2", "passed", false)]
    [InlineData("replacement", "passed", false)]
    public void OnlyLocalIsAuthorizedEvenAfterLocalSuccess(string name, string? previous, bool allowed)
    {
        var report = previous is null ? null : new JsonObject { ["status"] = previous };
        if (allowed) RuntimeAdmissionDiagnosticRules.RequireCase(name, report);
        else Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireCase(name, report));
    }
    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(true, true, true, true, false)]
    public void ExistingDurableEvidencePreventsASecondStart(bool checkpoint, bool report, bool budget, bool reservations, bool allowed)
    {
        if (allowed) RuntimeAdmissionDiagnosticRules.RequireFreshStart(checkpoint, report, budget, reservations);
        else Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireFreshStart(checkpoint, report, budget, reservations));
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
            OperationAdmission = new(7, id, "evidence", null,
                [new("decision", "clause", "action", kind, PlanningOperationNecessity.Required, null, null)
                { Effect = new(3, "effect", "realizes", [], inputs.ToList(), outputs.ToList(), [], ["clause"], "model", "proof") }], "proof", "fingerprint") { Dependencies = new(1, "domain", producers.Select(p =>
                    new PlanningOperationDependencyAssignment(p, id, "data", PlanningDependencyOrigin.ModelSemanticSelection, ["clause"], "decision")).ToList(), "dependency-proof") }
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
