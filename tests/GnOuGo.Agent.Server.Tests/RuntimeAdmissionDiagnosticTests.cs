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
    [InlineData("local", null, false)]
    [InlineData("local", "passed", false)]
    [InlineData("mixed", "passed", true)]
    [InlineData("mixed", "stopped", false)]
    [InlineData("mixed", null, false)]
    [InlineData("stage1", "passed", false)]
    [InlineData("stage2", "passed", false)]
    [InlineData("replacement", "passed", false)]
    public void OnlyMixedIsAuthorizedAfterVerifiedLocalSuccess(string name, string? previous, bool allowed)
    {
        var report = previous is null ? null : AcceptedLocal();
        if (report is not null) report["status"] = previous;
        if (allowed) RuntimeAdmissionDiagnosticRules.RequireCase(name, report);
        else Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireCase(name, report));
    }

    private static JsonObject AcceptedLocal() => new()
    {
        ["case"] = "local", ["status"] = "passed", ["effectValidationPassed"] = true,
        ["readOnlyRestart"] = new JsonObject { ["passed"] = true, ["providerCalls"] = 0, ["checkpointWrites"] = 0, ["admissionFingerprint"] = "proof" }
    };

    [Theory]
    [InlineData("effectValidationPassed")]
    [InlineData("readOnlyRestart")]
    [InlineData("case")]
    public void IncompleteLocalEvidenceDoesNotAuthorizeMixed(string missing)
    {
        var report = AcceptedLocal(); report.Remove(missing);
        Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireCase("mixed", report));
    }

    [Theory]
    [InlineData("providerCalls")]
    [InlineData("checkpointWrites")]
    public void LocalRestartMustHaveBeenReadOnly(string field)
    {
        var report = AcceptedLocal(); report["readOnlyRestart"]![field] = 1;
        Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireCase("mixed", report));
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

    [Theory]
    [InlineData("valid", true)]
    [InlineData("model_decision", false)]
    [InlineData("model_origin", false)]
    [InlineData("action", false)]
    [InlineData("missing_contract", false)]
    public void PortOnlyBaselineRequiresEngineContractsWithoutModelAuthority(string defect, bool allowed)
    {
        var state = new PlanningSnapshot();
        state.References.Add(new("ref", "owner", 0, "port", "fingerprint", "existing_workflow", 0, 1)
        { Baseline = new(1, "baseline", "port", "main", null, "input", "value", null) });
        if (defect != "missing_contract")
            state.RuntimeEvidence.Add(new("runtime", "ref", "ref", defect == "action" ? "local_behavior" : "contract",
                defect == "action" ? "ref" : null, null, null, null, null, null, null, PlanningOperationNecessity.Unspecified, "proof")
            { Origin = defect == "model_origin" ? PlanningRuntimeEvidenceOrigin.SourceInterpretation : PlanningRuntimeEvidenceOrigin.EngineBaseline,
                ExecutionScope = PlanningRuntimeExecutionScope.PublicContract });
        void Check() => RuntimeAdmissionDiagnosticRules.RequireStructuralBaseline(state, ["port"], defect == "model_decision" ? 1 : 0);
        if (allowed) Check(); else Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(Check);
    }

    private static PlanningObligation Operation(string id, string kind, string[] inputs, string[] outputs, string[] producers) =>
        new(id, ["evidence"], "workflow", kind, true)
        {
            OperationAdmission = new(10, id, "evidence", null,
                [new("decision", "clause", "action", kind, PlanningOperationNecessity.Required, null, null)
                { Effect = new(5, "effect", "realizes", [new("main", "result", "result_realization", "result")], inputs.ToList(), outputs.ToList(), [], ["clause"], "model", "proof") }], "proof", "fingerprint") { Dependencies = new(1, "domain", producers.Select(p =>
                    new PlanningOperationDependencyAssignment(p, id, "data", PlanningDependencyOrigin.ModelSemanticSelection, ["clause"], "decision")).ToList(), "dependency-proof") }
        };

    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing_input", false)]
    [InlineData("missing_output", false)]
    [InlineData("identity_call", false)]
    [InlineData("dependency_call", false)]
    [InlineData("dependency_edge", false)]
    [InlineData("legacy_producer", false)]
    [InlineData("extra_operation", false)]
    [InlineData("stale_admission", false)]
    [InlineData("stale_effect", false)]
    [InlineData("invocation", false)]
    public void LocalGateRequiresCompleteEffectProof(string defect, bool allowed)
    {
        var ops = new List<PlanningObligation> { Operation("local", "local_processing",
            defect == "missing_input" ? ["record"] : ["record", "threshold"], defect == "missing_output" ? [] : ["result"], defect == "dependency_edge" ? ["local"] : []) };
        if (defect == "legacy_producer") ops[0].OperationAdmission!.Assignments[0].Effect!.Producers.Add("local");
        if (defect == "extra_operation") ops.Add(Operation("extra", "external_write", [], [], []));
        if (defect == "stale_admission") ops[0] = ops[0] with { OperationAdmission = ops[0].OperationAdmission! with { Version = 8 } };
        if (defect == "stale_effect") ops[0].OperationAdmission!.Assignments[0] = ops[0].OperationAdmission!.Assignments[0] with { Effect = ops[0].OperationAdmission!.Assignments[0].Effect! with { Version = 3 } };
        if (defect == "invocation") ops[0].OperationAdmission!.Assignments[0].Effect!.Candidates[0] = new("main", "action", "invocation", "action");
        void Check() => RuntimeAdmissionDiagnosticRules.RequireEffects("local", ops, ["record", "threshold"], "result",
            defect == "identity_call" ? 1 : 0, defect == "dependency_call" ? 1 : 0);
        if (allowed) Check(); else Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(Check);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing_dependency", false)]
    [InlineData("reverse", false)]
    [InlineData("self", false)]
    [InlineData("missing_evidence", false)]
    [InlineData("missing_occurrence", false)]
    [InlineData("stale_occurrence", false)]
    [InlineData("foreign_scope", false)]
    [InlineData("local_invocation", false)]
    [InlineData("stale_effect", false)]
    [InlineData("extra_operation", false)]
    [InlineData("identity_call", false)]
    public void MixedGateRequiresProvenOccurrencesAndExactlyOneSupportedEdge(string defect, bool allowed)
    {
        var read = Operation("read", "external_read", ["source"], [], defect == "reverse" ? ["local"] : []);
        var proof = new PlanningOccurrenceBoundaryProof(defect == "stale_occurrence" ? 0 : 1, "runtime", "main", null,
            new("external_effect", "action", "action"), null, "proof");
        read.OperationAdmission!.Assignments[0].Effect!.Candidates[0] = new(defect == "foreign_scope" ? "other" : "main", "action", "invocation", "action")
        { OccurrenceProof = defect == "missing_occurrence" ? null : proof };
        if (defect == "stale_effect") read.OperationAdmission.Assignments[0] = read.OperationAdmission.Assignments[0] with
        { Effect = read.OperationAdmission.Assignments[0].Effect! with { Version = 3 } };
        var local = Operation("local", "local_processing", ["threshold"], ["result"], defect == "missing_dependency" ? [] : defect == "self" ? ["local"] : ["read"]);
        if (defect == "missing_evidence") local.OperationAdmission!.Dependencies!.Assignments[0].EvidenceReferences.Clear();
        if (defect == "local_invocation") local.OperationAdmission!.Assignments[0].Effect!.Candidates[0] = new("main", "action", "invocation", "action");
        var ops = new List<PlanningObligation> { read, local };
        if (defect == "extra_operation") ops.Add(Operation("write", "external_write", [], [], []));
        void Check() => RuntimeAdmissionDiagnosticRules.RequireEffects("mixed", ops, ["source", "threshold"], "result", defect == "identity_call" ? 1 : 0, 2);
        if (allowed) Check(); else Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(Check);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterRelationshipDomainsCannotReintroduceData(bool data)
    {
        var schema = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["enum"] = new JsonArray("none", data ? "data" : "policy") }) };
        if (data) Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => RuntimeAdmissionDiagnosticRules.RequireRelationDomain(schema));
        else RuntimeAdmissionDiagnosticRules.RequireRelationDomain(schema);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing", false)]
    [InlineData("extra", false)]
    [InlineData("reverse", false)]
    public void DownstreamDataIsExactlyTheAdmissionProjection(string defect, bool allowed)
    {
        var ops = new[] { Operation("read", "external_read", ["source"], [], []), Operation("local", "local_processing", ["threshold"], ["result"], ["read"]) };
        var relations = new List<PlanningObligationRelation> { new("source", "read", "data"), new("threshold", "local", "data"), new("read", "local", "data"), new("policy", "read", "policy") };
        if (defect == "missing") relations.RemoveAt(2);
        if (defect == "extra") relations.Add(new("foreign", "local", "data"));
        if (defect == "reverse") relations.Add(new("local", "read", "data"));
        void Check() => RuntimeAdmissionDiagnosticRules.RequireProjectedData(ops, relations);
        if (allowed) Check(); else Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(Check);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrozenProducerAddsOnlyTypedEvidence(bool alreadyTyped)
    {
        var policy = GnOuGo.Agent.Server.Planning.AgentPlanningPolicy.Create();
        if (!alreadyTyped) policy.Remove("declared_evidence");
        var options = new JsonObject { ["policy"] = policy, ["other"] = 123 };
        var before = options.ToJsonString();
        var result = RuntimeAdmissionDiagnosticRules.WithTypedPolicy(options);
        Assert.True(JsonNode.DeepEquals(result["policy"], GnOuGo.Agent.Server.Planning.AgentPlanningPolicy.Create()));
        Assert.Equal(123, result["other"]!.GetValue<int>());
        Assert.Equal(before, options.ToJsonString());
    }

    [Theory]
    [InlineData("instructions")]
    [InlineData("allow_remote_workflow_refs")]
    [InlineData("declared_evidence")]
    public void ChangedPolicyStopsBeforeDispatch(string field)
    {
        var policy = GnOuGo.Agent.Server.Planning.AgentPlanningPolicy.Create(); policy[field] = "changed";
        Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.WithTypedPolicy(new JsonObject { ["policy"] = policy }));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("model_decision", false)]
    [InlineData("model_origin", false)]
    [InlineData("missing_meaning", false)]
    public void TypedHostProjectionMustRemainEngineOwned(string defect, bool accepted)
    {
        var state = new PlanningSnapshot(); var policy = GnOuGo.Agent.Server.Planning.AgentPlanningPolicy.Create(); state.Request.Options["policy"] = policy;
        var clauses = policy["declared_evidence"]!["clauses"]!.AsArray();
        for (var i = 0; i < clauses.Count; i++)
        {
            var id = "ref" + i;
            state.References.Add(new(id, "host", 0, "host", "source", "host_constraint", 0, 1));
            state.RuntimeEvidence.Add(new("runtime" + i, id, id, "policy", null, null, null, null, null, null, null, PlanningOperationNecessity.Unspecified, "proof")
            { Origin = defect == "model_origin" ? PlanningRuntimeEvidenceOrigin.SourceInterpretation : PlanningRuntimeEvidenceOrigin.EngineSourceAuthority });
            foreach (var meaning in clauses[i]!["meanings"]!.AsArray()) state.Obligations.Add(new("ob" + state.Obligations.Count, [id], "workflow", meaning!["kind"]!.ToString(), true)
            { Grounding = new(PlanningSourceAuthority.ConstraintsOnly, PlanningSourceSemanticRole.PolicyConstraint, id, null, "proof") { DeclaredPolicyFingerprint = "declared" } });
        }
        if (defect == "missing_meaning") state.Obligations.RemoveAt(0);
        void Check() => RuntimeAdmissionDiagnosticRules.RequireTypedPolicy(state, defect == "model_decision" ? 1 : 0);
        if (accepted) Check(); else Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(Check);
    }
}
