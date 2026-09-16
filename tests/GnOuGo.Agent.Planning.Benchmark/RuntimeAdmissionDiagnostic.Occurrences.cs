using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    // Read-only archive input; corrected answers are synthetic and cannot reach a provider or journal.
    internal static async Task OccurrenceFixtureAsync(string id, IKeyVaultRecordStore records)
    {
        var captured = await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author) ?? throw new InvalidOperationException("Missing retained checkpoint.");
        var budget = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author);
        var original = JsonSerializer.Deserialize(JsonNode.Parse(captured.Value)!["snapshot"], PlanningJsonContext.Default.PlanningSnapshot)!;
        var source = PlanningContext.Clone(original);
        var policy = AgentPlanningPolicy.Create();
        if (policy["instructions"]!.ToString() != source.Request.Options["policy"]!["instructions"]!.ToString()) throw new InvalidOperationException("Host policy text changed.");
        source.Request.Options["policy"] = policy;
        var state = new PlanningSnapshot { Request = source.Request, References = source.References };
        var decisions = PlanningSourceDecisions.InterpretationDecisions(state);
        var pages = PlanningDecisionPages.PackedPageCount(state, decisions);
        state.Obligations = source.Obligations.Where(o => o.EvidenceReferences.All(r => source.References.Single(x => x.Id == r).SourceId != "host") &&
            o.OperationAdmission is null).Select(o => o with { Grounding = PlanningSourceGroundingRules.Create(state, o, o.Grounding?.BaselineReference) }).ToList();
        state.Obligations.AddRange(PlanningDeclaredPolicyProjection.Obligations(state));
        // Explicit synthetic reassessment of the retained owned facts under current proof versions.
        state.RuntimeEvidence = source.RuntimeEvidence.Where(e => source.References.Single(r => r.Id == e.SourceReference).SourceId != "host")
            .Select(e => PlanningOperations.SealRuntime(state, e with { OccurrenceBoundary = null })).ToList();
        state.RuntimeEvidence.AddRange(PlanningDeclaredPolicyProjection.Clauses(state).Select(c => PlanningOperations.PolicyEvidence(state, c.Reference)));
        state.RuntimeEvidenceFingerprint = PlanningOperations.RuntimeFingerprint(state);
        InstallDeclarations("local", state);
        var domains = PlanningOperations.Scopes(state).Select(s => PlanningOperations.EffectDomain(state, s.Evidence!)).ToArray();
        if (domains.Any(d => d.Count != 1 || d.Single().Value.BoundaryKind != "result_realization")) throw new InvalidOperationException("Action prose still issued invocation alternatives.");
        var client = new EffectFixtureClient(state);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = client, LLMCapabilities = client }, (_, _) => Task.CompletedTask);
        await PlanningOperations.ResolveAsync(state, runtime, CancellationToken.None);
        CheckEffectFixture("local", state, state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray());
        foreach (var text in new[] { "Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.", "This is deterministic, local, in-memory business processing." })
        {
            var invalid = PlanningContext.Clone(state);
            invalid.Obligations.Single(PlanningSourceDecisions.IsOperation).OperationAdmission!.Assignments.RemoveAll(a => PlanningChoiceEvidence.Text(invalid, a.ClauseReference).Trim() == text);
            try { CheckEffectFixture("local", invalid, invalid.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray()); }
            catch (GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException) { continue; }
            throw new InvalidOperationException("Missing governing evidence passed LOCAL acceptance.");
        }
        await PlanningSourceDecisions.RelateAsync(state, runtime, CancellationToken.None);
        var operation = state.Obligations.Single(PlanningSourceDecisions.IsOperation);
        var inputs = state.Declarations.Where(d => state.ObligationRelations.Any(r => r.Producer == d.Id && r.Consumer == operation.Id)).Select(d => PlanningDeclarations.Name(state, d)).Order(StringComparer.Ordinal).ToArray();
        var clauses = operation.OperationAdmission!.Assignments.Select(a => PlanningChoiceEvidence.Text(state, a.ClauseReference).Trim()).ToHashSet(StringComparer.Ordinal);
        var rulesAttached = clauses.Contains("Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.");
        var descriptionAttached = clauses.Contains("This is deterministic, local, in-memory business processing.");
        var output = state.Declarations.Single(d => d.Direction == "output");
        var preservationAttached = output.ModifierReferences.Any(r => PlanningChoiceEvidence.Text(state, r).Trim() == "Preserve the original id and amount.");
        if (operation.Kind != "local_processing" || !operation.Required || !inputs.SequenceEqual(new[] { "record", "threshold" }) ||
            !rulesAttached || !descriptionAttached || !preservationAttached ||
            !operation.OperationAdmission.Assignments.Any(a => a.Effect!.Outputs.Contains(output.Id)) ||
            operation.OperationAdmission.Dependencies!.Assignments.Count != 0) throw new InvalidOperationException("Synthetic admission acceptance failed.");
        var count = client.Requests.Count;
        var restarted = PlanningContext.Clone(state); await PlanningOperations.ResolveAsync(restarted, runtime, CancellationToken.None);
        if (count != client.Requests.Count || state.OperationAdmissionFingerprint != restarted.OperationAdmissionFingerprint) throw new InvalidOperationException("Restart changed proof or requests.");
        if ((await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author))?.Value != captured.Value ||
            (await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author))?.Value != budget?.Value) throw new InvalidOperationException("Archive changed.");
        var fields = client.Requests.SelectMany(r => r.StructuredOutputSchema!["properties"]!.AsObject().Select(p => p.Key)).ToArray();
        Console.WriteLine(new JsonObject
        {
            ["evidence"] = "Synthetic current-proof reassessment and corrected effect responses; historical requests and receipts unchanged. No live convergence claim.",
            ["sourceIdentity"] = id, ["archiveUnchanged"] = true, ["providerDispatches"] = 0,
            ["interpretationDecisions"] = decisions.Length, ["interpretationPages"] = pages,
            ["hostInterpretationDecisions"] = decisions.Count(d => d.Context["role"]!.ToString() == "host_constraint"),
            ["domainSize"] = domains.SelectMany(d => d.Keys).Distinct().Count(),
            ["realizationDecisions"] = fields.Count(f => f.StartsWith("effect_runtime_", StringComparison.Ordinal)),
            ["governingDecisions"] = fields.Count(f => f.StartsWith("effect_governing_", StringComparison.Ordinal)),
            ["boundaryScopeDecisions"] = fields.Count(f => f.StartsWith("boundary_", StringComparison.Ordinal)),
            ["identityDecisions"] = fields.Count(f => f.StartsWith("operation_", StringComparison.Ordinal)),
            ["dependencyDecisions"] = fields.Count(f => f.StartsWith("data_", StringComparison.Ordinal)),
            ["syntheticRequests"] = count, ["requestsOnRestart"] = client.Requests.Count - count,
            ["operation"] = operation.Id, ["kind"] = operation.Kind, ["required"] = operation.Required,
            ["inputs"] = new JsonArray(inputs.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["output"] = PlanningDeclarations.Name(state, state.Declarations.Single(d => d.Direction == "output")),
            ["classificationRulesAndFallbacksAttached"] = rulesAttached, ["descriptiveEvidenceAttached"] = descriptionAttached,
            ["preservationRemainsDeclarationEvidence"] = preservationAttached,
            ["governingAttachments"] = operation.OperationAdmission.Assignments.Count(a => a.Disposition == "attach"),
            ["coveredOccurrences"] = PlanningOperations.DeclarationExclusions(state).Count,
            ["admissionProof"] = state.OperationAdmissionFingerprint, ["dependencyProof"] = operation.OperationAdmission.Dependencies.ProofFingerprint
        }.ToJsonString());
    }
}
