using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Bounded semantic interpretation of owned evidence, without permission to ask or declare unsupportedness.</summary>
internal static class PlanningBusinessAnalysis
{
    internal static async Task AnalyzeAsync(PlanningSnapshot state, IPlanningRuntime runtime, PlanningBusinessDecision decision, CancellationToken ct)
    {
        _ = PlanningChoiceEvidence.Clauses(state);
        var sources = PlanningSourceDecisions.Sources(state);
        var governors = PlanningChoiceEvidence.Governors(state, decision).ToArray();
        var source = PlanningChoiceEvidence.Parent(state, decision.SubjectReference);
        var boundaries = PlanningReferences.Boundaries(source, sources[source.SourceId]);
        var operations = state.Preparation!.Capabilities.SelectMany(c => c.OperationIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var alternative = boundaries.Schema.DeepClone().AsObject();
        alternative["properties"]!["operations"] = new JsonObject { ["type"] = "array", ["minItems"] = 0, ["maxItems"] = Math.Min(8, operations.Length),
            ["items"] = operations.Length == 0 ? new JsonObject { ["type"] = "string", ["maxLength"] = 0 } : PlanningHoleRequests.Enum(operations) };
        alternative["required"] = new JsonArray("start", "end", "operations");
        var variants = new JsonArray(PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum(["technical"]))));
        if (governors.Length > 0)
            variants.Add((JsonNode)PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum(["settled", "runtime"])), ("reference", PlanningHoleRequests.Enum(governors))));
        // Only user-owned clauses can propose business alternatives. Host constraints never become questions.
        if (PlanningChoiceEvidence.Origin(state, source.Id) is "intent" or "answer" or "baseline")
            variants.Add((JsonNode)PlanningHoleRequests.Object(("kind", PlanningHoleRequests.Enum(["planning"])), ("alternatives", new JsonObject
            { ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 4, ["items"] = alternative })));
        var context = new JsonObject
        {
            ["subject"] = source.Id, ["clause"] = PlanningChoiceEvidence.Text(state, source.Id), ["words"] = boundaries.Context,
            ["governing"] = new JsonObject(governors.Select(id => new KeyValuePair<string, JsonNode?>(id, new JsonObject
            { ["origin"] = PlanningChoiceEvidence.Origin(state, id), ["content"] = PlanningChoiceEvidence.Text(state, PlanningChoiceEvidence.Parent(state, id).Id) }))),
            ["operations"] = new JsonObject(operations.Select(id => new KeyValuePair<string, JsonNode?>(id,
                JsonValue.Create(state.Obligations.SingleOrDefault(o => o.Id == id) is { } o ? PlanningSourceDecisions.Text(state, o) :
                    string.Join("; ", state.Preparation.Capabilities.Where(c => c.OperationIds.Contains(id)).Select(c => c.Description)))))),
            ["task"] = "Interpret this candidate using its complete clause and governing evidence. A business_choice label is not proof that anything is missing. Select settled only when a governing declaration already specifies the behavior, or runtime when it is an execution input, default, condition or confirmation. For a genuinely missing planning choice, select the explicitly offered alternatives and their affected business operation IDs. Tool/implementation selection, schemas and runtime observations are technical. Unknown proof is technical. Never invent alternatives, infer an unsupported result, or choose a preference."
        };
        var response = await PlanningDecisionPages.ResolveAsync(state, runtime, "business_decision", "$plan",
            [new(decision.Id, new JsonObject { ["anyOf"] = variants }, context, decision.DependencyFingerprint)], ct);
        var value = response[decision.Id]!;
        var kind = value["kind"]!.ToString();
        decision.EvidenceReferences = [source.Id];
        if (kind == "technical")
            throw new WorkflowRuntimeException("BUSINESS_CHOICE_PROOF_UNAVAILABLE", "The candidate is not a proven unresolved business choice.");
        if (kind is "settled" or "runtime")
        {
            var reference = value["reference"]!.ToString();
            if (!governors.Contains(reference, StringComparer.Ordinal)) throw new PlanningConflictException("A governing reference is outside this decision.");
            decision.EvidenceReferences.Add(reference);
            decision.Status = kind == "runtime" ? "runtime" : "resolved";
            decision.ResolutionOrigin = PlanningChoiceEvidence.Origin(state, reference);
            decision.Alternatives = [new() { Id = "choice_" + PlanningGraphCompiler.Fingerprint(reference)[..16],
                EvidenceReference = reference, Label = Label(PlanningChoiceEvidence.Text(state, reference)) }];
            decision.SelectedChoiceId = decision.Alternatives[0].Id;
            return;
        }
        var alternatives = new List<PlanningBusinessAlternative>();
        foreach (var item in value["alternatives"]!.AsArray())
        {
            var reference = boundaries.Select(item!["start"]!.ToString(), item["end"]!.ToString());
            if (!state.References.Contains(reference)) state.References.Add(reference);
            var selectedOperations = item["operations"]!.AsArray().Select(v => v!.ToString()).ToList();
            if (selectedOperations.Distinct(StringComparer.Ordinal).Count() != selectedOperations.Count)
                throw new WorkflowRuntimeException("BUSINESS_CHOICE_PROOF_UNAVAILABLE", "An alternative repeats an operation identity.");
            alternatives.Add(new() { Id = "choice_" + PlanningGraphCompiler.Fingerprint(reference.Id + ":" + string.Join('|', selectedOperations.Order(StringComparer.Ordinal)))[..16],
                EvidenceReference = reference.Id, Label = Label(PlanningChoiceEvidence.Text(state, reference.Id)), OperationIds = selectedOperations });
        }
        if (alternatives.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != alternatives.Count)
            throw new WorkflowRuntimeException("BUSINESS_CHOICE_PROOF_UNAVAILABLE", "The alternatives repeat the same decision.");
        decision.Alternatives = alternatives;
        decision.AffectedObligations = alternatives.SelectMany(a => a.OperationIds).Distinct(StringComparer.Ordinal).ToList();
        decision.Status = "analyzed";
        await PlanningBusinessGovernance.AssessAsync(state, runtime, decision, ct);
        // A model-proposed list is never proof of a complete domain.
        decision.CompleteDomain = false;
    }

    internal static string Label(string value) => value.Trim().Length <= 80 ? value.Trim() : value.Trim()[..77] + "…";
}
