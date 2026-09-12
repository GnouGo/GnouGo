using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Interpretation selects exact source spans; identities, text and relationship endpoints are engine owned.</summary>
internal static class PlanningSourceDecisions
{
    private static readonly string[] Kinds = ["external_read", "external_write", "external_execute", "resource_lifecycle", "cleanup", "human_interaction", "local_processing", "workflow_policy", "implementation_policy", "confirmation_required", "confirmation_forbidden", "exact_denial", "business_input", "business_output", "business_choice", "iteration", "workflow_boundary", "information"];
    internal static IReadOnlyDictionary<string, string> Sources(PlanningSnapshot state)
        => PlanningIntentAssessment.IntentSources(state).ToDictionary(s => s.Id, s => s.Text, StringComparer.Ordinal);
    internal static string Text(PlanningSnapshot state, PlanningObligation obligation)
        => string.Join(" ", obligation.EvidenceReferences.Select(id => PlanningReferences.Resolve(state, id, Sources(state))));

    internal static async Task InterpretAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var sources = PlanningIntentAssessment.IntentSources(state);
        var scopes = sources.SelectMany(source => PlanningReferences.Register(state, source.Id, source.Kind, source.Text)
            .Where(r => !string.IsNullOrWhiteSpace(source.Text.Substring(r.Start, r.Length)))
            .Select(reference => (Reference: reference, Source: source, Boundaries: PlanningReferences.Boundaries(reference, source.Text)))).ToArray();
        var decisions = scopes.Select(scope =>
        {
            var item = scope.Boundaries.Schema.DeepClone().AsObject();
            item["properties"]!["kind"] = PlanningHoleRequests.Enum(Kinds);
            item["properties"]!["required"] = PlanningHoleRequests.Type("boolean");
            item["required"] = new JsonArray("start", "end", "kind", "required");
            return new PlanningDecisionPages.Decision(DecisionId(scope.Reference, scope.Source.Text), new JsonObject
            { ["type"] = "array", ["minItems"] = 0, ["maxItems"] = 4, ["items"] = item }, new JsonObject
            {
                ["task"] = "Identify distinct requested operations, business inputs/outputs and policies in this source span. Select word boundary IDs (end is exclusive). Information has no execution authority. Runtime observations are operations, not missing business values. Do not invent intentions from these instructions.",
                ["role"] = scope.Source.Kind, ["questionContext"] = scope.Source.QuestionContext, ["words"] = scope.Boundaries.Context.DeepClone()
            }, PlanningGraphCompiler.Fingerprint(scope.Source.Text.Substring(scope.Reference.Start, scope.Reference.Length)));
        }).ToArray();
        var values = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent", "$plan", decisions, ct);
        var obligations = new List<PlanningObligation>();
        foreach (var scope in scopes)
        {
            foreach (var item in values[DecisionId(scope.Reference, scope.Source.Text)]!.AsArray())
            {
                var reference = scope.Boundaries.Select(item!["start"]!.ToString(), item["end"]!.ToString());
                if (!state.References.Contains(reference)) state.References.Add(reference);
                var kind = item["kind"]!.ToString();
                if (kind == "information") continue;
                var owner = kind is "business_input" or "business_output" or "business_choice" ? "business_decision" : kind is "local_processing" or "workflow_policy" or "confirmation_required" or "confirmation_forbidden" or "iteration" or "workflow_boundary" ? "workflow" : "capability_contract";
                var id = "ob_" + PlanningGraphCompiler.Fingerprint(reference.SourceId + ":" + reference.Start + ":" + PlanningReferences.Resolve(state, reference.Id, Sources(state)) + ":" + kind)[..16];
                var obligation = new PlanningObligation(id, [reference.Id], owner, kind, item["required"]!.GetValue<bool>());
                if (obligations.Any(o => o.Id == id))
                    throw new WorkflowRuntimeException("INTENT_DUPLICATE_DECISION", "The same source span and semantic role were assigned twice: " + id);
                obligations.Add(obligation);
            }
        }
        state.Obligations = await ApplyRevisionAsync(state, runtime, obligations, ct);
        await runtime.CheckpointAsync(state, ct);
    }

    internal static async Task<List<PlanningObligation>> ApplyRevisionAsync(PlanningSnapshot state, IPlanningRuntime runtime,
        List<PlanningObligation> obligations, CancellationToken ct)
    {
        if (state.BehaviorRevision is not { } revision) return obligations;
        var references = PlanningReferences.Register(state, "revision", "user_revision", revision.Text);
        var decisions = new List<PlanningDecisionPages.Decision>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        // A user revision cannot retire host policy.
        foreach (var obligation in obligations.Where(o => !o.EvidenceReferences.Any(id => state.References.Single(r => r.Id == id).SourceId == "host")))
        foreach (var page in references.Chunk(8))
        {
            var id = "governing_" + obligation.Id + "_" + page[0].Id; owners[id] = obligation.Id;
            decisions.Add(new(id, PlanningHoleRequests.Enum(["retained", .. page.Select(r => r.Id)]), new JsonObject
            {
                ["obligation"] = Text(state, obligation), ["kind"] = obligation.Kind,
                ["revision"] = PlanningReferences.Context(page, new Dictionary<string, string> { ["revision"] = revision.Text }),
                ["task"] = "Keep an obligation that still governs the revised workflow. Select the exact revision reference only when it explicitly removes or supersedes this obligation, or this span describes a removal instruction rather than an operation to execute. Unrelated obligations remain retained. Absence of evidence on this page means retained."
            }, PlanningGraphCompiler.Fingerprint(string.Join("|", obligation.EvidenceReferences) + ":" + revision.Text)));
        }
        var choices = await PlanningDecisionPages.ResolveAsync(state, runtime, "behavior_revision_obligations", "$plan", decisions, ct);
        var superseded = choices.Where(p => p.Value!.ToString() != "retained").Select(p => owners[p.Key]).ToHashSet(StringComparer.Ordinal);
        return obligations.Where(o => !superseded.Contains(o.Id)).ToList();
    }

    internal static async Task RelateAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var operations = state.Obligations.Where(IsOperation).ToArray();
        var producers = state.Obligations.Where(o => IsOperation(o) || o.Kind is "business_input" or "implementation_policy").ToArray();
        var pairs = operations.SelectMany(consumer => producers.Where(p => p.Id != consumer.Id).Select(producer => (Producer: producer, Consumer: consumer))).ToArray();
        var choices = pairs.Select(pair => new PlanningDecisionPages.Decision("relation_" + pair.Producer.Id + "_" + pair.Consumer.Id,
            PlanningHoleRequests.Enum(pair.Producer.Kind == "business_input" ? ["none", "data"] : pair.Producer.Kind == "implementation_policy" ? ["none", "policy"]
                : pair.Producer.Kind == "resource_lifecycle" ? ["none", "data", "owned_resource", "failure"] : ["none", "data", "decision", "decision_no_effect", "failure"]),
            new JsonObject { ["producer"] = Text(state, pair.Producer), ["consumer"] = Text(state, pair.Consumer),
                ["task"] = "Select the explicit producer-to-consumer obligation. A decision controls an effect; decision_no_effect includes an explicit no-action outcome. Failure means handling this producer's failure. owned_resource requires the consumer to target the original resource materialized by this producer. policy applies a declared implementation restriction to this operation. Do not add incidental implementation dependencies." },
            PlanningGraphCompiler.Fingerprint(string.Join("|", pair.Producer.EvidenceReferences.Concat(pair.Consumer.EvidenceReferences))))).ToArray();
        var values = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_relations", "$plan", choices, ct);
        state.ObligationRelations = pairs.Select(pair => new PlanningObligationRelation(pair.Producer.Id, pair.Consumer.Id,
            values["relation_" + pair.Producer.Id + "_" + pair.Consumer.Id]!.ToString())).Where(r => r.Role != "none").ToList();
        foreach (var consumer in operations)
            if (state.ObligationRelations.Count(r => r.Consumer == consumer.Id && r.Role is "decision" or "decision_no_effect") > 1)
                throw new WorkflowRuntimeException("INTENT_DECISION_CONFLICT", "Multiple governing decisions require an explicit business composition for " + consumer.Id);
        var visiting = new HashSet<string>(StringComparer.Ordinal); var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new WorkflowRuntimeException("INTENT_DEPENDENCY_CYCLE", "The issued dependency decisions form a cycle at " + id);
            foreach (var source in state.ObligationRelations.Where(r => r.Consumer == id && r.Role != "failure")) Visit(source.Producer);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var operation in operations) Visit(operation.Id);
        await runtime.CheckpointAsync(state, ct);
    }

    private static string DecisionId(PlanningReference reference, string source) => "interpret_" + PlanningGraphCompiler.Fingerprint(reference.Owner + ":" + reference.SourceId + ":" + reference.Start + ":" + source.Substring(reference.Start, reference.Length))[..24];

    internal static bool IsOperation(PlanningObligation obligation) => obligation.Kind is "external_read" or "external_write" or "external_execute" or "resource_lifecycle" or "cleanup" or "human_interaction" or "local_processing";
}
