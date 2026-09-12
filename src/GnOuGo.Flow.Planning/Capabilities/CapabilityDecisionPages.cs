using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Capabilities;

/// <summary>Full-catalog relationship assessment. Page selections confer no execution authority.</summary>
internal static class CapabilityDecisionPages
{
    internal sealed record Relationship(string Candidate, string Obligation, bool Supported, bool Contradicted, List<string> Evidence);

    internal static async Task<JsonObject> MatchAsync(PlanningSnapshot state, IPlanningRuntime runtime,
        CapabilityInventory inventory, CapabilityCatalog catalog, CancellationToken ct)
    {
        var operations = new JsonObject(); var constraints = new JsonObject();
        var requirements = inventory.Operations.Where(o => o.ExecutionKind != "local_processing").SelectMany(o =>
        {
            var intrinsic = o.CoverageRequirementEvidence.Where(e => !o.WorkflowStructureCoverageRequirementIds.Contains(e.Id)).ToArray();
            return intrinsic.Length == 0 ? new[] { (Owner: o.Id, Id: o.Id, Text: o.Description) }
                : intrinsic.Select(e => (Owner: o.Id, Id: e.Id, Text: e.Excerpt));
        }).Concat(inventory.Constraints.Where(c => c.EnforcementKind == "exact_denial").Select(c => (Owner: c.Id, Id: c.Id, Text: c.Description))).ToArray();
        var relations = await AssessAsync(state, runtime, requirements, catalog, ct);
        var denials = inventory.Constraints.Where(c => c.EnforcementKind == "exact_denial").ToDictionary(c => c.Id,
            c => relations.Where(r => r.Obligation == c.Id && r.Supported && !r.Contradicted).Select(r => r.Candidate).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var denied = denials.Values.SelectMany(v => v).ToHashSet(StringComparer.Ordinal);
        foreach (var operation in inventory.Operations)
        {
            if (operation.ExecutionKind == "local_processing") { operations[operation.Id] = Match("local", []); continue; }
            var required = requirements.Where(r => r.Owner == operation.Id).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var excluded = denied.ToHashSet(StringComparer.Ordinal);
            if (state.ObligationRelations.Any(r => r.Consumer == operation.Id && r.Role == "owned_resource"))
                foreach (var candidate in catalog.Entries.Where(e => !HasResourceTarget(e))) excluded.Add(candidate.Id);
            var selected = MinimalComposition(catalog, required, relations, excluded);
            if (selected.Count == 0)
            {
                // Every candidate and every page was examined. A negative semantic
                // assertion is still not a deterministic proof of unsupportedness.
                throw new WorkflowRuntimeException("CAPABILITY_PROOF_UNRESOLVED", "No sufficient declared implementation was established for obligation '" + operation.Id + "'. Catalog coverage is complete; no execution authority was granted.");
            }
            var conditional = operation.DecisionSourceOperationId.Length > 0;
            var match = Match(conditional ? "conditional" : selected.Count == 1 ? "matched" : "composed", selected);
            match["decision_operation_id"] = conditional ? operation.DecisionSourceOperationId : "";
            match["conditional_mode"] = conditional ? operation.AllowNoEffectOutcome || CapabilityContractValidation.HasHumanDecisionSource(inventory, operation) ? "all_on_value" : "exactly_one" : "";
            operations[operation.Id] = match;
        }
        foreach (var constraint in inventory.Constraints)
            constraints[constraint.Id] = new JsonObject
            {
                ["status"] = constraint.EnforcementKind == "workflow_policy" ? "policy_only" : "enforced", ["reason"] = "Issued obligation and declared candidate relationships.",
                ["denied_catalog_ids"] = new JsonArray((denials.GetValueOrDefault(constraint.Id) ?? []).Order(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["candidate_catalog_ids"] = new JsonArray()
            };
        return new() { ["operation_matches"] = operations, ["constraint_matches"] = constraints };

        static JsonObject Match(string status, IReadOnlyList<string> selected) => new()
        {
            ["status"] = status, ["reason"] = "Smallest sufficient declared composition after complete paged assessment.",
            ["catalog_ids"] = new JsonArray(selected.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()), ["candidate_catalog_ids"] = new JsonArray(),
            ["decision_operation_id"] = "", ["conditional_mode"] = ""
        };
    }

    internal static async Task<List<Relationship>> AssessAsync(PlanningSnapshot state, IPlanningRuntime runtime,
        IReadOnlyList<(string Owner, string Id, string Text)> requirements, CapabilityCatalog catalog, CancellationToken ct)
    {
        var decisions = new List<PlanningDecisionPages.Decision>();
        var issued = new Dictionary<string, (string Candidate, string Obligation, PlanningReference[] References)>(StringComparer.Ordinal);
        var excluded = new List<Relationship>();
        foreach (var entry in catalog.Entries.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var card = CapabilityCoverageContext.BuildCapabilityCoverageCard(entry, catalog);
            var sourceId = "catalog:" + entry.Id;
            var references = PlanningReferences.Register(state, sourceId, "capability_contract", card);
            foreach (var requirement in requirements.DistinctBy(r => (r.Owner, r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                if (state.ObligationRelations.Any(r => r.Consumer == requirement.Owner && r.Role == "owned_resource") && !HasResourceTarget(entry))
                {
                    excluded.Add(new(entry.Id, requirement.Id, false, true, references.Select(r => r.Id).ToList()));
                    continue;
                }
            foreach (var chunk in references.Chunk(12))
            {
                var id = "relationship_" + PlanningGraphCompiler.Fingerprint(entry.Id + ":" + requirement.Owner + ":" + requirement.Id + ":" + chunk[0].Id)[..24];
                issued[id] = (entry.Id, requirement.Id, chunk);
                var evidence = PlanningReferences.Schema(chunk);
                var schema = new JsonObject { ["anyOf"] = new JsonArray(
                    PlanningHoleRequests.Object(("relation", PlanningHoleRequests.Enum("unrelated"))),
                    PlanningHoleRequests.Object(("relation", PlanningHoleRequests.Enum("supports", "contradicts")), ("evidence", evidence))) };
                decisions.Add(new(id, schema, new JsonObject
                {
                    ["obligation"] = requirement.Text,
                    ["facts"] = PlanningReferences.Context(chunk, new Dictionary<string, string> { [sourceId] = card }),
                    ["task"] = "Assess these declared facts against the intrinsic obligation. Select evidence IDs, never quotations. All pages are combined before selection. Absence on this page is unrelated. Names and descriptive claims cannot prove execution context, file contents, an opaque result schema, or original-artifact provenance. Workflow ordering, confirmation guards and cleanup routing belong to the compiler. Runtime-observable facts require declared discovery operations."
                }, PlanningGraphCompiler.Fingerprint(requirement.Text + ":" + card)));
            }
            }
        }
        var answers = await PlanningDecisionPages.ResolveAsync(state, runtime, "capability_matching", "$plan", decisions, ct);
        return issued.GroupBy(p => (p.Value.Candidate, p.Value.Obligation)).Select(group =>
        {
            var evidence = group.Where(p => answers[p.Key]?["evidence"] is not null).Select(p => answers[p.Key]!["evidence"]!.ToString()).Distinct(StringComparer.Ordinal).ToList();
            return new Relationship(group.Key.Candidate, group.Key.Obligation,
                group.Any(p => answers[p.Key]!["relation"]!.ToString() == "supports"),
                group.Any(p => answers[p.Key]!["relation"]!.ToString() == "contradicts"), evidence);
        }).Concat(excluded).ToList();
    }

    internal static bool HasResourceTarget(CapabilityCatalogEntry candidate)
        => candidate.ArtifactContract?.Consumes.Count > 0;

    internal static IReadOnlyList<string> MinimalComposition(CapabilityCatalog catalog, IReadOnlySet<string> required,
        IReadOnlyList<Relationship> relationships, IReadOnlySet<string> denied)
    {
        var candidates = catalog.Entries.Where(e => !denied.Contains(e.Id) &&
            !relationships.Any(r => r.Candidate == e.Id && required.Contains(r.Obligation) && r.Contradicted) &&
            relationships.Any(r => r.Candidate == e.Id && required.Contains(r.Obligation) && r.Supported))
            .OrderByDescending(e => e.RequestBindings.Count).ThenBy(e => e.RequiredInputs.Count).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var coverage = candidates.ToDictionary(c => c.Id, c => relationships.Where(r => r.Candidate == c.Id && r.Supported && !r.Contradicted).Select(r => r.Obligation).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var candidate in candidates)
            if (required.IsSubsetOf(coverage[candidate.Id])) return [candidate.Id];
        var examined = 0;
        for (var size = 2; size <= Math.Min(8, candidates.Length); size++)
        {
            var selected = new List<CapabilityCatalogEntry>();
            IReadOnlyList<string>? Search(int offset)
            {
                if (++examined > 10000) throw new WorkflowRuntimeException("CAPABILITY_COMPOSITION_LIMIT", "The finite composition proof limit was reached without granting authority.");
                if (selected.Count == size)
                    return required.IsSubsetOf(selected.SelectMany(e => coverage[e.Id]).ToHashSet(StringComparer.Ordinal)) && CapabilityCoverage.IsDeclaredArtifactComposition(selected)
                        ? selected.Select(e => e.Id).ToArray() : null;
                for (var index = offset; index <= candidates.Length - (size - selected.Count); index++)
                {
                    selected.Add(candidates[index]); var found = Search(index + 1); selected.RemoveAt(selected.Count - 1);
                    if (found is not null) return found;
                }
                return null;
            }
            if (Search(0) is { } composition) return composition;
        }
        return [];
    }
}
