using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Planning.Tests;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    // This command never constructs a provider transport or writes a record. The
    // corrected effect answers below are explicitly synthetic fixture facts.
    internal static async Task EffectFixtureAsync(string id, IKeyVaultRecordStore records)
    {
        var ct = CancellationToken.None;
        var captured = await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author, ct)
            ?? throw new InvalidOperationException("The retained checkpoint is required.");
        var budget = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author, ct);
        var state = JsonSerializer.Deserialize(JsonNode.Parse(captured.Value)!["snapshot"], PlanningJsonContext.Default.PlanningSnapshot)!;
        if (state.Obligations.Any(o => o.OperationAdmission is not null) || state.Construction.PendingCalls.Count != 0)
            throw new InvalidOperationException("The offline fixture requires the captured completed-interpretation checkpoint without pending dispatches or admitted operations.");
        var historicalIdentityDecisions = state.DecisionPages.Where(p => p.Phase == "intent_operations" && p.Status == "completed")
            .SelectMany(p => p.Decisions).Where(id => id.StartsWith("operation_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Count();
        var historicalEffectDecisions = state.DecisionPages.Where(p => p.Phase == "intent_operations" && p.Status == "completed")
            .SelectMany(p => p.Decisions).Where(id => id.StartsWith("effect_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Count();
        var accounting = state.RequestAccounting.Count;
        var declarations = state.DeclarationFingerprint;
        var client = new EffectFixtureClient(state);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = client, LLMCapabilities = client }, (_, _) => Task.CompletedTask);
        await PlanningOperations.ResolveAsync(state, runtime, ct);
        await PlanningSourceDecisions.RelateAsync(state, runtime, ct);
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray();
        var governedClauses = operations.SelectMany(o => o.OperationAdmission!.Assignments).Select(a => PlanningChoiceEvidence.Text(state, a.ClauseReference)).ToHashSet(StringComparer.Ordinal);
        var expectedRules = "Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.";
        var expectedDescription = "This is deterministic, local, in-memory business processing.";
        if (operations.Length != 1 || operations[0].Kind != "local_processing" || !operations[0].Required || declarations != state.DeclarationFingerprint ||
            !governedClauses.Contains(expectedRules) || !governedClauses.Contains(expectedDescription) ||
            !state.Declarations.Where(d => d.Direction == "input").Select(d => d.Id).Order(StringComparer.Ordinal)
                .SequenceEqual(state.ObligationRelations.Where(r => state.Declarations.Any(d => d.Id == r.Producer)).Select(r => r.Producer).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException("The synthetic classifier effect fixture did not converge.");
        var requests = client.Requests.ToArray();
        var restarted = PlanningContext.Clone(state); await PlanningOperations.ResolveAsync(restarted, runtime, ct);
        if (client.Requests.Count != requests.Length || restarted.OperationAdmissionFingerprint != state.OperationAdmissionFingerprint)
            throw new InvalidOperationException("Offline effect proof replay changed identity or dispatched again.");
        if ((await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author, ct))?.Value != captured.Value ||
            (await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author, ct))?.Value != budget?.Value)
            throw new InvalidOperationException("Archived records changed.");
        var requestFields = requests.SelectMany(r => r.StructuredOutputSchema!["properties"]!.AsObject().Select(p => p.Key)).ToArray();
        Console.WriteLine(new JsonObject
        {
            ["evidence"] = "Synthetic effect-grounding responses over retained interpretation and declaration fixtures; not historical receipts or live evidence.",
            ["sourceIdentity"] = id, ["sourceUnchanged"] = true, ["providerDispatches"] = 0,
            ["historicalStandaloneIdentityDecisions"] = historicalIdentityDecisions,
            ["historicalEffectGroundingDecisions"] = historicalEffectDecisions,
            ["fixtureStandaloneIdentityDecisions"] = requestFields.Count(k => k.StartsWith("operation_", StringComparison.Ordinal)),
            ["fixtureEffectGroundingDecisions"] = requestFields.Count(k => k.StartsWith("effect_", StringComparison.Ordinal)),
            ["realizationDecisions"] = requestFields.Count(k => k.StartsWith("effect_", StringComparison.Ordinal) && !k.StartsWith("effect_governing_", StringComparison.Ordinal)),
            ["governingDecisions"] = requestFields.Count(k => k.StartsWith("effect_governing_", StringComparison.Ordinal)),
            ["fixtureRelationshipDecisions"] = requestFields.Count(k => k.StartsWith("relation_", StringComparison.Ordinal)),
            ["effectGroundingRequests"] = requests.Count(r => r.StructuredOutputSchema!["properties"]!.AsObject().Any(p => p.Key.StartsWith("effect_", StringComparison.Ordinal))),
            ["relationshipRequests"] = requests.Count(r => r.StructuredOutputSchema!["properties"]!.AsObject().Any(p => p.Key.StartsWith("relation_", StringComparison.Ordinal))),
            ["classificationRulesAndFallbacksAttached"] = true, ["descriptiveEvidenceAttached"] = true,
            ["declarationCoveredOccurrencesExcluded"] = PlanningOperations.DeclarationExclusions(state).Count,
            ["syntheticRequests"] = requests.Length, ["newCoordinatorReservations"] = state.RequestAccounting.Count - accounting,
            ["largestEstimatedInput"] = state.RequestAccounting.Skip(accounting).Max(a => a.EstimatedInputTokens),
            ["actualTokens"] = null, ["additionalRequestsOnRestart"] = 0,
            ["operationProofVersion"] = operations[0].OperationAdmission!.Version,
            ["operations"] = new JsonArray(operations.Select(o => (JsonNode)new JsonObject
            { ["id"] = o.Id, ["kind"] = o.Kind, ["required"] = o.Required,
                ["inputs"] = new JsonArray(state.Declarations.Where(d => state.ObligationRelations.Any(r => r.Producer == d.Id && r.Consumer == o.Id)).Select(d => (JsonNode)JsonValue.Create(PlanningDeclarations.Name(state, d))!).ToArray()),
                ["outputs"] = new JsonArray(o.OperationAdmission!.Assignments.SelectMany(a => a.Effect!.Outputs).Distinct().Select(id => (JsonNode)JsonValue.Create(PlanningDeclarations.Name(state, state.Declarations.Single(d => d.Id == id)))!).ToArray()),
                ["contributions"] = o.OperationAdmission.Assignments.Count, ["governingAttachments"] = o.OperationAdmission.Assignments.Count(a => a.Disposition == "attach") }).ToArray())
        }.ToJsonString());
    }

    private sealed class EffectFixtureClient(PlanningSnapshot state) : ILLMClient, ILLMCapabilityResolver
    {
        internal List<LLMRequest> Requests { get; } = [];
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["low", "medium"]);
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request); var answers = new JsonObject();
            foreach (var field in request.StructuredOutputSchema!["properties"]!.AsObject())
            {
                if (field.Key.StartsWith("relation_", StringComparison.Ordinal))
                {
                    // Explicit synthetic policy relationships; preserve the retained
                    // non-declaration obligations instead of clearing them for replay.
                    answers[field.Key] = field.Value!["enum"]!.AsArray().Any(v => v!.ToString() == "policy") ? "policy" : "none";
                    continue;
                }
                if (!field.Key.StartsWith("effect_", StringComparison.Ordinal)) throw new InvalidOperationException("No standalone identity decision is permitted by this complete synthetic effect fixture.");
                var scope = OperationEffectFixtures.Scope(state, field.Key);
                var target = PlanningOperations.EffectDomain(state, scope.Evidence!).Single(p => p.Value.BoundaryKind == "result_realization").Key;
                var descriptive = PlanningChoiceEvidence.Text(state, scope.Evidence!.ActionReference!) == "This is deterministic, local, in-memory business processing.";
                answers[field.Key] = descriptive && field.Key == PlanningOperations.EffectDecisionId(scope.Evidence) ? OperationEffectFixtures.Defer(scope) : OperationEffectFixtures.Answer(state, scope, [target],
                    descriptive || scope.Evidence.EvidenceRole == "governing" ? "governs" : "realizes",
                    state.Declarations.Where(d => d.Direction == "input").Select(d => d.Id));
            }
            return Task.FromResult(new LLMResponse { Json = answers, CompletionStatus = "completed" });
        }
    }
}
