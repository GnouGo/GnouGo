using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    private static string[] BoundaryKinds(string kind) => kind switch
    {
        "local_processing" => ["invocation", "intermediate_result", "iteration", "call"],
        "resource_lifecycle" or "cleanup" => ["resource_transition"],
        "human_interaction" => ["interaction"],
        _ => ["external_effect", "iteration"]
    };

    private static JsonObject OccurrenceEvidenceSchema(string[] kinds, JsonObject boundaries) => new()
    {
        ["anyOf"] = new JsonArray(PlanningHoleRequests.Type("null"), PlanningHoleRequests.Object(
            ("kind", PlanningHoleRequests.Enum(kinds.SelectMany(BoundaryKinds).Distinct(StringComparer.Ordinal).ToArray())),
            ("owner", boundaries.DeepClone().AsObject()), ("span", boundaries.DeepClone().AsObject())))
    };

    private static void ValidateOccurrenceEvidence(PlanningSnapshot state, PlanningRuntimeEvidence evidence)
    {
        if (evidence.OccurrenceBoundary is not { } boundary) return;
        if (evidence.EvidenceRole != "action" || evidence.BaselineReference is not null ||
            !BoundaryKinds(evidence.Kind!).Contains(boundary.Kind, StringComparer.Ordinal))
            throw Failure(evidence.Id, "Only a requested action with compatible explicit occurrence evidence can establish an invocation boundary.");
        var source = state.References.Single(r => r.Id == evidence.SourceReference);
        foreach (var id in new[] { boundary.OwnerReference, boundary.BoundaryReference })
        {
            if (!PlanningChoiceEvidence.Current(state, id)) throw Failure(evidence.Id, "Occurrence evidence is stale or foreign.");
            var reference = state.References.Single(r => r.Id == id);
            if (reference.SourceId != source.SourceId || reference.Start < source.Start || reference.Start + reference.Length > source.Start + source.Length)
                throw Failure(evidence.Id, "An occurrence needs exact owned subject and execution-boundary evidence.");
        }
        if (boundary.Kind == "resource_transition" && boundary.OwnerReference != evidence.ResourceReference)
            throw Failure(evidence.Id, "The occurrence must retain its exact runtime resource ownership.");
    }

    // Possible workflow owners are scope questions, not occurrence identities. A
    // selected scope must carry its existing owned boundary; no Cartesian expansion.
    private static Dictionary<string, string?> OccurrenceScopes(PlanningSnapshot state)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal) { ["main"] = null };
        foreach (var obligation in state.Obligations.Where(o => o.Kind == "workflow_boundary"))
            result.Add(obligation.Id, obligation.Grounding!.ClauseReference);
        foreach (var reference in state.References.Where(r => r.Baseline is { OwnerKind: "workflow", Field: null }))
            result.TryAdd(reference.Baseline!.Workflow!, reference.Id);
        return result.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    internal static PlanningDecisionPages.Decision? OccurrenceDecision(PlanningSnapshot state, PlanningRuntimeEvidence evidence)
    {
        ValidateRuntime(state, evidence);
        if (evidence.OccurrenceBoundary is null) throw Failure(evidence.Id, "An action label is not an independent occurrence boundary.");
        var scopes = OccurrenceScopes(state);
        if (scopes.Count == 1) return null;
        return new("boundary_" + evidence.Id, new JsonObject { ["anyOf"] = new JsonArray(
            scopes.Select(p => (JsonNode)PlanningHoleRequests.Object(("scope", PlanningHoleRequests.Enum([p.Key])),
                ("scopeEvidence", p.Value is null ? PlanningHoleRequests.Type("null") : PlanningHoleRequests.Enum([p.Value]))))
                .Append(PlanningHoleRequests.Object(("unresolved", PlanningHoleRequests.Enum(["unresolved"])))).ToArray()) }, new()
        {
            ["stage"] = "occurrence_boundary", ["task"] = "Locate this independently evidenced execution boundary in its owned workflow. Select only an established scope supported by the complete evidence; otherwise unresolved. Do not create another execution or infer scope from names.",
            ["clause"] = PlanningChoiceEvidence.Text(state, evidence.ClauseReference),
            ["owner"] = PlanningChoiceEvidence.Text(state, evidence.OccurrenceBoundary.OwnerReference),
            ["boundary"] = PlanningChoiceEvidence.Text(state, evidence.OccurrenceBoundary.BoundaryReference),
            ["kind"] = evidence.OccurrenceBoundary.Kind,
            ["scopes"] = new JsonObject(scopes.Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                p.Value is null ? JsonValue.Create("Root workflow") : JsonValue.Create(PlanningChoiceEvidence.Text(state, p.Value)))))
        }, EffectFingerprint(state) + ":occurrence-v1:" + evidence.ProofFingerprint + ":" +
            PlanningGraphCompiler.Fingerprint(new JsonObject(scopes.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value)))).ToJsonString()));
    }

    private static PlanningDecisionPages.Decision[] OccurrenceDecisions(PlanningSnapshot state) => DeriveScopes(state)
        .Where(s => s.Evidence!.OccurrenceBoundary is not null).Select(s => OccurrenceDecision(state, s.Evidence!))
        .OfType<PlanningDecisionPages.Decision>().OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();

    private static async Task GroundOccurrenceBoundaries(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
        => _ = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", OccurrenceDecisions(state), ct);

    internal static PlanningOccurrenceBoundaryProof ReadOccurrenceBoundary(PlanningSnapshot state, PlanningRuntimeEvidence evidence)
    {
        var decision = OccurrenceDecision(state, evidence);
        var scope = "main"; string? reference = null;
        if (decision is not null)
        {
            var answer = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", OccurrenceDecisions(state))[decision.Id]!;
            if (answer["unresolved"] is not null) throw Failure(evidence.Id, "The independent occurrence has no proven workflow owner.");
            scope = answer["scope"]!.ToString(); reference = answer["scopeEvidence"]?.ToString();
            if (!OccurrenceScopes(state).TryGetValue(scope, out var issued) || issued != reference)
                throw Failure(evidence.Id, "The occurrence selected a foreign execution scope.");
        }
        var proof = new PlanningOccurrenceBoundaryProof(1, evidence.Id, scope, reference, evidence.OccurrenceBoundary!, decision?.Id, "");
        return proof with { Fingerprint = PlanningGraphCompiler.Fingerprint("occurrence-proof-v1:" + evidence.ProofFingerprint + ":" +
            (reference is null ? "" : PlanningChoiceEvidence.Text(state, reference)) + ":" +
            JsonSerializer.Serialize(proof, PlanningJsonContext.Default.PlanningOccurrenceBoundaryProof)) };
    }
}
