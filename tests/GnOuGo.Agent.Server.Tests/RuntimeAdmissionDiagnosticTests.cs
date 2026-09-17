using System.Text.Json.Nodes;
using GnOuGo.Agent.Planning.Benchmark;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RuntimeAdmissionDiagnosticTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("stale", false)]
    [InlineData("foreign", false)]
    [InlineData("cardinality_only", false)]
    [InlineData("missing_decision", false)]
    [InlineData("valid_extra_support", true)]
    [InlineData("unqualified_extra_support", false)]
    [InlineData("unknown_origin", false)]
    public void ApplicabilityGateRequiresProofBeyondSingletonCardinality(string defect, bool accepted)
    {
        var operation = Operation("local", "local_processing", ["record", "threshold"], ["result"], []);
        operation.OperationAdmission!.GoverningApplicability.Add(new(defect == "stale" ? 0 : 1, "property", "runtime", "owned",
            "active", [defect == "foreign" ? "foreign" : "local"], null, [new("local", ["owned"])], [], null,
            defect == "cardinality_only" ? PlanningApplicabilityOrigin.DeterministicOwner : defect == "unknown_origin" ? PlanningApplicabilityOrigin.Unknown : PlanningApplicabilityOrigin.ModelApplicability,
            defect == "missing_decision" ? null : "applicability_property", "realized", "domain", "proof"));
        if (defect == "valid_extra_support") AddSupport(operation, "extra");
        if (defect == "unqualified_extra_support") operation.OperationAdmission.Assignments.Add(operation.OperationAdmission.Assignments[0] with { ContributionId = "extra" });
        void Check() => RuntimeAdmissionDiagnosticRules.RequireLocalApplicability(operation);
        if (accepted) Check(); else Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(Check);
    }

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
    [InlineData("local", "RETAINED LOCAL ACCEPTED", true)]
    [InlineData("mixed", "RETAINED LOCAL ACCEPTED", false)]
    [InlineData("mixed", "stopped", false)]
    [InlineData("mixed", null, false)]
    [InlineData("stage1", "RETAINED LOCAL ACCEPTED", false)]
    [InlineData("stage2", "RETAINED LOCAL ACCEPTED", false)]
    [InlineData("replacement", "RETAINED LOCAL ACCEPTED", false)]
    public void OnlyLocalIsAuthorizedRegardlessOfPriorSuccess(string name, string? previous, bool allowed)
    {
        var report = previous is null ? null : AcceptedLocal();
        if (report is not null) report["outcome"] = previous;
        if (allowed) RuntimeAdmissionDiagnosticRules.RequireCase(name, report);
        else Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireCase(name, report));
    }

    private static JsonObject AcceptedLocal() => new()
    {
        ["identity"] = RuntimeAdmissionDiagnosticRules.ComparisonIdentity + ":local", ["originalStatus"] = "stopped",
        ["productionCommit"] = RuntimeAdmissionDiagnosticRules.ProductionCommit, ["outcome"] = "RETAINED LOCAL ACCEPTED",
        ["canonicalResult"] = new JsonObject { ["kind"] = "local_processing", ["required"] = true },
        ["snapshotFingerprintBefore"] = "snapshot", ["snapshotFingerprintAfter"] = "snapshot",
        ["readOnlyRestart"] = new JsonObject { ["passed"] = true, ["providerCalls"] = 0, ["checkpointWrites"] = 0,
            ["admissionFingerprint"] = "proof", ["snapshotFingerprint"] = "snapshot" }
    };

    [Theory]
    [InlineData("canonicalResult")]
    [InlineData("readOnlyRestart")]
    [InlineData("identity")]
    [InlineData("productionCommit")]
    [InlineData("outcome")]
    [InlineData("snapshotFingerprintBefore")]
    public void IncompleteLocalEvidenceDoesNotAuthorizeMixed(string missing)
    {
        var report = AcceptedLocal(); report.Remove(missing);
        Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireCase("mixed", report));
    }

    [Theory]
    [InlineData("providerCalls")]
    [InlineData("checkpointWrites")]
    public void PriorRestartEvidenceCannotAuthorizeMixed(string field)
    {
        var report = AcceptedLocal(); report["readOnlyRestart"]![field] = 1;
        Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireCase("mixed", report));
    }

    [Theory]
    [InlineData("originalStatus", "passed")]
    [InlineData("snapshotFingerprintAfter", "changed")]
    [InlineData("productionCommit", "foreign")]
    [InlineData("identity", "another:local")]
    public void AlteredPriorEvidenceCannotAuthorizeMixed(string field, string value)
    {
        var report = AcceptedLocal(); report[field] = value;
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
    [InlineData(1, "intent_relations", "low", false)]
    [InlineData(1, "intent_relations_repair", "low", false)]
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
    public void UnrecordedProductionBinariesCannotBeFrozen()
    {
        Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireFrozenProduction(new()));
        Assert.Throws<InvalidOperationException>(() => RuntimeAdmissionDiagnosticRules.RequireFrozenProduction(
            new JsonObject { ["GnOuGo.Flow.Planning.dll"] = "foreign" }));
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
            OperationAdmission = new(14, id, "evidence", null,
                [new("decision", "clause", "action", kind, PlanningOperationNecessity.Required, id, null)
                { ContributionId = "qualified", RuntimeEvidenceId = "runtime", RuntimeEvidenceIds = ["runtime"], Disposition = "supports", EffectId = id, Effect = new(7, "effect", "realizes", [new("main", "result", "result_realization", "result")], inputs.ToList(), outputs.ToList(), [], ["clause"], "model", "proof") }], "proof", "fingerprint") { ExecutionContributions = [new(4, "runtime", "qualification", "domain", [new("qualified", "action", "supports", id, "requested_result_production", "result", "result", PlanningContributionOrigin.ModelQualification) { UnitId = "unit", RuntimeEvidenceIds = ["runtime"] }], "proof") { RuntimeEvidenceIds = ["runtime"], ClauseReference = "clause", Units = [new("unit", "requested_execution", "clause", "action", ["action"], ["runtime"], id, "requested_result_production", "result", "result")] }], Dependencies = new(1, "domain", producers.Select(p =>
                    new PlanningOperationDependencyAssignment(p, id, "data", PlanningDependencyOrigin.ModelSemanticSelection, ["clause"], "decision")).ToList(), "dependency-proof"),
                    RealizationCoverage = new(3, "coverage", "domain", [id], [new("runtime", "supports", [id], ["action"]) { ContributionId = "qualified" }],
                        [new(id, new("main", "result", "result_realization", "result"), ["qualified"], inputs.ToList(), outputs.ToList())], "coverage-proof") }
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
    [InlineData("missing_coverage", false)]
    [InlineData("stale_coverage", false)]
    [InlineData("missing_support", false)]
    [InlineData("governing_only", false)]
    [InlineData("foreign_effect", false)]
    public void LocalGateRequiresCompleteEffectProof(string defect, bool allowed)
    {
        var ops = new List<PlanningObligation> { Operation("local", "local_processing",
            defect == "missing_input" ? ["record"] : ["record", "threshold"], defect == "missing_output" ? [] : ["result"], defect == "dependency_edge" ? ["local"] : []) };
        if (defect == "legacy_producer") ops[0].OperationAdmission!.Assignments[0].Effect!.Producers.Add("local");
        if (defect == "extra_operation") ops.Add(Operation("extra", "external_write", [], [], []));
        if (defect == "stale_admission") ops[0] = ops[0] with { OperationAdmission = ops[0].OperationAdmission! with { Version = 8 } };
        if (defect == "stale_effect") ops[0].OperationAdmission!.Assignments[0] = ops[0].OperationAdmission!.Assignments[0] with { Effect = ops[0].OperationAdmission!.Assignments[0].Effect! with { Version = 3 } };
        if (defect == "invocation") ops[0].OperationAdmission!.Assignments[0].Effect!.Candidates[0] = new("main", "action", "invocation", "action");
        if (defect == "missing_coverage") ops[0] = ops[0] with { OperationAdmission = ops[0].OperationAdmission! with { RealizationCoverage = null } };
        if (defect == "stale_coverage") ops[0] = ops[0] with { OperationAdmission = ops[0].OperationAdmission! with { RealizationCoverage = ops[0].OperationAdmission!.RealizationCoverage! with { Version = 0 } } };
        if (defect == "missing_support") ops[0].OperationAdmission!.RealizationCoverage!.Effects[0].SupportingEvidence.Clear();
        if (defect == "governing_only") ops[0].OperationAdmission!.RealizationCoverage!.Contributions[0] = new("runtime", "governs", ["local"], ["action"]);
        if (defect == "foreign_effect") ops[0].OperationAdmission!.RealizationCoverage!.SelectedEffects[0] = "foreign";
        void Check() => RuntimeAdmissionDiagnosticRules.RequireEffects("local", ops, ["record", "threshold"], "result",
            defect == "identity_call" ? 1 : 0, defect == "dependency_call" ? 1 : 0);
        if (allowed) Check(); else Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(Check);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JointClauseGateUsesCompleteProvenanceInsteadOfDiagnosticParent(bool foreignParent)
    {
        var operation = Operation("local", "local_processing", ["record", "threshold"], ["result"], []);
        var proof = operation.OperationAdmission!.ExecutionContributions[0];
        operation.OperationAdmission.ExecutionContributions[0] = proof with
        { RuntimeEvidenceId = "other-member", RuntimeEvidenceIds = foreignParent ? ["other-member"] : ["other-member", "runtime"] };
        void Check() => RuntimeAdmissionDiagnosticRules.RequireEffects("local", [operation], ["record", "threshold"], "result", 0);
        if (foreignParent) Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(Check);
        else Check();
    }

    [Theory]
    [InlineData("governs", true)]
    [InlineData("supports", false)]
    [InlineData("both", false)]
    [InlineData("missing", false)]
    [InlineData("partial", false)]
    public void PropertyEvidenceCannotQualifyAsSupportEvenWhenAlsoGoverning(string role, bool accepted)
    {
        var state = new PlanningSnapshot(); state.Request.Prompt = "Execute task. Deterministic local processing.";
        var start = state.Request.Prompt.IndexOf("Deterministic", StringComparison.Ordinal);
        var length = state.Request.Prompt.Length - start;
        state.References.Add(new("action", "request", 0, "request", "source", "span", 0, 13));
        state.References.Add(new("property", "request", 0, "request", "source", "span", start, role == "partial" ? 5 : length));
        var operation = Operation("local", "local_processing", ["record", "threshold"], ["result"], []);
        var admission = operation.OperationAdmission!;
        foreach (var contributionRole in role switch { "both" => new[] { "governs", "supports" }, "missing" => [], "partial" => ["governs"], _ => [role] })
        {
            admission.ExecutionContributions[0].Contributions.Add(new(contributionRole, "property", contributionRole == "supports" ? "supports" : "governing_property", contributionRole == "supports" ? "local" : null,
                contributionRole == "supports" ? "requested_result_production" : "governing_property", "result", "result", PlanningContributionOrigin.ModelQualification));
            admission.Assignments.Add(admission.Assignments[0] with { ContributionId = contributionRole, ActionReference = "property",
                Disposition = contributionRole == "supports" ? "supports" : "attach" });
        }
        void Check() => RuntimeAdmissionDiagnosticRules.RequireGoverningOnly(state, operation, "request", start, length);
        if (accepted) Check(); else Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(Check);
    }

    [Theory]
    [InlineData("support", true)]
    [InlineData("governing", true)]
    [InlineData("split", true)]
    [InlineData("context_only", false)]
    [InlineData("missing_fallback", false)]
    [InlineData("wrong_effect", false)]
    [InlineData("unproved_governing", false)]
    public void RulesRequireOwnedSemanticCoverageRatherThanAnAttachmentShape(string shape, bool accepted)
    {
        var state = new PlanningSnapshot(); state.Request.Prompt = "Select high when valid; standard otherwise.";
        var operation = Operation("local", "local_processing", [], ["result"], []);
        var proof = operation.OperationAdmission!;
        var length = state.Request.Prompt.Length;
        state.References.Add(new("clause", "owner", 0, "request", "source", "span", 0, length));
        var supportedLength = shape == "context_only" ? 6 : shape is "split" or "missing_fallback" ? 22 : length;
        state.References.Add(new("action", "owner", 0, "request", "source", "span", 0, supportedLength));
        if (shape == "wrong_effect") proof.Assignments[0] = proof.Assignments[0] with { EffectId = "foreign" };
        if (shape is "governing" or "unproved_governing")
        {
            proof.Assignments[0] = proof.Assignments[0] with { Disposition = "attach" };
            proof.ExecutionContributions[0].Contributions[0] = proof.ExecutionContributions[0].Contributions[0] with { Role = "governing_property", EffectId = null };
        }
        if (shape is "governing" or "split")
        {
            var id = shape == "split" ? "fallback" : "qualified";
            if (shape == "split")
            {
                state.References.Add(new(id, "owner", 0, "request", "source", "span", supportedLength, length - supportedLength));
                proof.Assignments.Add(proof.Assignments[0] with { ContributionId = id, ActionReference = id, Disposition = "attach" });
                proof.ExecutionContributions[0].Contributions.Add(new(id, id, "governing_property", null, "governing_property", null, null, PlanningContributionOrigin.ModelQualification));
            }
            proof.GoverningApplicability.Add(new(1, id, "runtime", shape == "split" ? id : "action", "active", ["local"], null,
                [new("local", ["clause"])], [], null, PlanningApplicabilityOrigin.ModelApplicability, "applicability", "realized", "domain", "proof"));
        }
        void Check() => RuntimeAdmissionDiagnosticRules.RequireSemanticEvidence(state, operation, "request", 0, length);
        if (accepted) Check(); else Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(Check);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallCoveredSupportCannotHideBehindAnotherDiagnosticAnchor(bool orphan)
    {
        var state = new PlanningSnapshot(); state.Request.Prompt = "Fetch a value. Keep fields unchanged.";
        state.References.Add(new("action", "owner", 0, "request", "source", "span", 0, 14));
        state.References.Add(new("covered", "owner", 0, "request", "source", "span", 15, 4));
        var operation = Operation("read", "external_read", [], [], []);
        RuntimeAdmissionDiagnosticRules.RequireNoSupportOverlap(state, [operation], "request", 15, state.Request.Prompt.Length - 15);
        operation.OperationAdmission!.ExecutionContributions[0].Contributions.Add(new("invalid", "covered", "supports", "read", "requested_owned_occurrence", "owner", "boundary", PlanningContributionOrigin.ModelQualification));
        if (!orphan) operation.OperationAdmission.Assignments.Add(operation.OperationAdmission.Assignments[0] with { ContributionId = "invalid", ActionReference = "covered" });
        Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => RuntimeAdmissionDiagnosticRules.RequireNoSupportOverlap(state, [operation], "request", 15, state.Request.Prompt.Length - 15));
    }

    private static void AddSupport(PlanningObligation operation, string id)
    {
        var admission = operation.OperationAdmission!;
        admission.ExecutionContributions[0].Contributions.Add(admission.ExecutionContributions[0].Contributions[0] with { Id = id, EvidenceReference = id });
        admission.ExecutionContributions[0].Units[0].EvidenceReferences.Add(id);
        admission.Assignments.Add(admission.Assignments[0] with { ContributionId = id, ActionReference = id });
        admission.RealizationCoverage!.Contributions.Add(new("runtime", "supports", [operation.Id], [id]) { ContributionId = id });
        admission.RealizationCoverage.Effects[0].SupportingEvidence.Add(id);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    public void SeveralQualifiedExecutionSpansMaySupportOneEffect(int count, bool reverse)
    {
        var operation = Operation("local", "local_processing", ["record", "threshold"], ["result"], []);
        for (var i = 1; i < count; i++) AddSupport(operation, "evidence" + i);
        if (reverse)
        {
            operation.OperationAdmission!.Assignments.Reverse();
            operation.OperationAdmission.ExecutionContributions[0].Contributions.Reverse();
            operation.OperationAdmission.RealizationCoverage!.Contributions.Reverse();
        }
        RuntimeAdmissionDiagnosticRules.RequireEffects("local", [operation], ["record", "threshold"], "result", 0);
        Assert.Equal("local", operation.Id);
        Assert.True(operation.Required);
        Assert.Empty(operation.OperationAdmission!.Dependencies!.Assignments);
        Assert.Equal(count, operation.OperationAdmission.Assignments.Count);
    }

    [Theory]
    [InlineData("missing_basis")]
    [InlineData("property_basis")]
    [InlineData("foreign_effect")]
    [InlineData("foreign_target")]
    [InlineData("foreign_owner")]
    [InlineData("foreign_boundary")]
    [InlineData("stale_proof")]
    [InlineData("property_role")]
    [InlineData("necessity_conflict")]
    public void EverySupportNeedsItsOwnCompatiblePositiveAuthority(string defect)
    {
        var operation = Operation("local", "local_processing", ["record", "threshold"], ["result"], []);
        AddSupport(operation, "extra");
        var proof = operation.OperationAdmission!;
        var contribution = proof.ExecutionContributions[0].Contributions[1];
        proof.ExecutionContributions[0].Contributions[1] = defect switch
        {
            "missing_basis" => contribution with { Basis = "" },
            "property_basis" => contribution with { Basis = "governing_property" },
            "foreign_effect" => contribution with { EffectId = "other" },
            "foreign_owner" => contribution with { OwnerReference = "other" },
            "foreign_boundary" => contribution with { BoundaryReference = "other" },
            "property_role" => contribution with { Role = "governing_property" },
            _ => contribution
        };
        if (defect == "foreign_target") proof.Assignments[1] = proof.Assignments[1] with { TargetId = "other" };
        if (defect == "necessity_conflict") proof.Assignments[1] = proof.Assignments[1] with { Necessity = PlanningOperationNecessity.Optional };
        if (defect == "stale_proof") proof.ExecutionContributions[0] = proof.ExecutionContributions[0] with { Version = 1 };
        Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => RuntimeAdmissionDiagnosticRules.RequirePositiveSupports(operation));
    }

    [Theory]
    [InlineData("valid_multiple_supports", true)]
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
        read.OperationAdmission.ExecutionContributions[0].Contributions[0] = read.OperationAdmission.ExecutionContributions[0].Contributions[0] with
        { Basis = "requested_owned_occurrence", OwnerReference = "action", BoundaryReference = "action" };
        read.OperationAdmission.ExecutionContributions[0].Units[0] = read.OperationAdmission.ExecutionContributions[0].Units[0] with
        { Basis = "requested_owned_occurrence", OwnerReference = "action", BoundaryReference = "action" };
        if (defect == "stale_effect") read.OperationAdmission.Assignments[0] = read.OperationAdmission.Assignments[0] with
        { Effect = read.OperationAdmission.Assignments[0].Effect! with { Version = 3 } };
        var local = Operation("local", "local_processing", ["threshold"], ["result"], defect == "missing_dependency" ? [] : defect == "self" ? ["local"] : ["read"]);
        if (defect == "valid_multiple_supports") { AddSupport(read, "read-details"); AddSupport(local, "classification-rules"); }
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
