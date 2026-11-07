using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal static JsonObject? InspectOverallProgress(PlanningSession state)
    {
        if (state.Requirements is null || state.Construction?.Compact is null) return null;
        var facts = new SelectionFacts(state);
        var view = OverallProgress(state, facts);
        view["acceptedContributions"] = facts.Results.DeepClone();
        view["compositionFindings"] = facts.Findings.DeepClone();
        return view;
    }
    // Request-local facts only. Reuse the ordinary completion assessment when it
    // ran; otherwise coverage and contribution reachability share one traversal.
    private sealed class SelectionFacts
    {
        internal SelectionFacts(PlanningSession state)
        {
            CompletionIds = FinalReviewRequirementIds(state, out var assessment);
            TechnicalValidation = assessment is null ? "not_assessed" : assessment.Diagnostics.Any(diagnostic => diagnostic.Required) ? "blocked" : "passed";
            var bindings = assessment is null && state.Plan is { } plan ? new CommitmentBindings(plan) : null;
            Coverage = assessment?.Coverage ?? PlanningCommitments.Assess(state, bindings);
            var reachable = assessment?.ReachableTaskIds ?? bindings?.ReachableTaskIds ?? new HashSet<string>(StringComparer.Ordinal);
            RequirementIds = BusinessRequirementIds(state, Coverage);
            Results = BusinessResults(state, reachable);
            Findings = BusinessCoverageFindings(state, RequirementIds, Coverage);
        }

        internal IReadOnlyList<PlanningValidationResult> Coverage { get; }
        internal string[] RequirementIds { get; }
        internal string[] CompletionIds { get; }
        internal string TechnicalValidation { get; }
        internal JsonArray Results { get; }
        internal JsonArray Findings { get; }
    }

    internal static LLMRequest SelectionRequest(PlanningSession state)
    {
        var facts = new SelectionFacts(state);
        var context = SelectionContext(state, facts);
        return Request(SelectGuidance, context, SelectionSchema(state, facts, context), SelectPurpose);
    }

    internal static LLMRequest ImplementationRequest(PlanningSession state)
    {
        var context = ImplementationContext(state);
        return Request(state, ImplementationGuidance(state), context, ImplementationSchema(state, context), ImplementationPurpose);
    }

    private static HashSet<string> ProgressEvidence(PlanningSession state, JsonObject context)
    {
        var progress = context["overallProgress"];
        return NavigationEvidence(state, new[] { progress?["requirementsDirectory"], progress?["nextDeclaredResults"],
            progress?["expectedResults"], progress?["declaredResults"], progress?["missingDeliverableBindings"] }
            .Concat((context["evidence"] as JsonObject ?? []).Where(pair => pair.Key.StartsWith("page:requirements:", StringComparison.Ordinal) ||
                pair.Key.StartsWith("page:outputs:", StringComparison.Ordinal)).Select(pair => pair.Value?["next"])));
    }

    private static JsonObject OverallProgress(PlanningSession state, SelectionFacts facts)
    {
        var compact = State(state);
        var originals = state.Requirements?.Outcomes ?? [];
        var shown = originals.Count <= 3 ? originals.ToArray() : SelectionAnchor(state);
        if (shown.Length == 0) shown = originals.Take(3).ToArray();
        var shownIds = shown.Select(requirement => requirement.Id).ToHashSet(StringComparer.Ordinal);
        var outputs = state.Requirements?.Outputs ?? [];
        var outputPage = OutputPage(state);
        var shownOutputs = outputPage.Select(output => output.Name).ToHashSet(StringComparer.Ordinal);
        var missing = outputs.Where(output => output.Required && state.Plan?.Root.Outputs.Any(binding => binding.Name == output.Name) != true).ToArray();
        var declared = new JsonArray(outputPage.Select(output =>
        {
            var bound = state.Plan?.Root.Outputs.Any(binding => binding.Name == output.Name) == true;
            var connections = (state.Requirements?.Outcomes ?? []).SelectMany(requirement =>
                (requirement.Commitments ?? []).Select((commitment, index) => (requirement, commitment, index)))
                .Where(item => item.commitment.Result?.Output == output.Name)
                .Select(item => facts.Coverage.SingleOrDefault(result => result.Id ==
                    "coverage:" + Uri.EscapeDataString(item.requirement.Id) + ":" + item.index + ":result")?.Outcome).ToArray();
            return (JsonNode)new JsonObject
            {
                ["name"] = output.Name, ["required"] = output.Required, ["evidence"] = "output:" + output.Name,
                ["bindingStatus"] = bound ? "accepted_declaration" : "not_bound",
                ["producerConnection"] = connections.Length == 0 ? "not_required" : connections.Contains("violation", StringComparer.Ordinal)
                    ? "disconnected" : connections.All(outcome => outcome == "verified") ? "established" : "unverified"
            };
        }).ToArray());
        // Originals, criteria and accepted contribution descriptions already have
        // canonical presentation/evidence. Index them without copying their text,
        // schemas, technical scopes or operation identities into another view.
        var view = new JsonObject
        {
            ["expectedResultCount"] = originals.Count,
            ["expectedResults"] = new JsonArray(shown.Select(requirement => (JsonNode)new JsonObject
            {
                ["requirementId"] = requirement.Id, ["evidence"] = "requirement:" + requirement.Id,
                ["publicOutputs"] = List(compact.Sources.Where(source =>
                        (compact.RequirementSources.GetValueOrDefault(requirement.Id) ?? []).Contains(source.Id, StringComparer.Ordinal))
                    .SelectMany(source => source.Outputs ?? []).Select(output => output.Name).Where(shownOutputs.Contains).Distinct(StringComparer.Ordinal))
            }).ToArray()),
            ["requirementsDirectory"] = originals.Count > shown.Length ? "page:requirements:0" : null,
            ["globalConstraints"] = List(compact.GlobalSourceIds.Select(id => "source:" + id)),
            ["acceptedResults"] = List(facts.Results.Select(result => result!["id"]!.ToString())),
            ["remainingObligationCount"] = facts.RequirementIds.Length,
            ["remainingObligations"] = List(facts.RequirementIds.Where(shownIds.Contains)),
            ["compositionFindingsAt"] = facts.Findings.Count == 0 ? null : "/currentPlanFindings",
            ["declaredResults"] = declared,
            ["declaredResultCount"] = outputs.Count,
            ["nextDeclaredResults"] = compact.OutputOffset + 8 < outputs.Count ? "page:outputs:" + (compact.OutputOffset + 8) : null,
            ["missingDeliverableBindingCount"] = missing.Length,
            ["missingDeliverableBindings"] = new JsonArray(missing.Take(8).Select(output => (JsonNode)new JsonObject
                { ["name"] = output.Name, ["evidence"] = "output:" + output.Name,
                    ["directory"] = shownOutputs.Contains(output.Name) ? null : "page:outputs:" + (outputs.IndexOf(output) / 8 * 8) }).ToArray()),
            ["completionAvailable"] = facts.CompletionIds.Length > 0,
            ["technicalValidation"] = facts.TechnicalValidation,
            ["technicalNeed"] = facts.TechnicalValidation == "blocked"
                ? "Resolve the remaining interface, binding or boundary violations before final business review." : null,
            ["globalReview"] = "required", ["globalReviewCurrent"] = HybridWorkflowPlanner.ConstructionArtifactReviewValid(state),
            ["executionObserved"] = false
        };
        // Complete small pages already carry their counts. Omitted metadata is
        // inferred from those complete arrays; partial pages retain exact totals
        // and existing retrieval links, never silently clipped originals.
        foreach (var name in view.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray()) view.Remove(name);
        if (compact.GlobalSourceIds.Count == 0) view.Remove("globalConstraints");
        if (originals.Count == shown.Length) view.Remove("expectedResultCount");
        if (facts.RequirementIds.Length == facts.RequirementIds.Count(shownIds.Contains)) view.Remove("remainingObligationCount");
        if (outputs.Count == declared.Count) view.Remove("declaredResultCount");
        if (missing.Length <= 8) view.Remove("missingDeliverableBindingCount");
        return view;
    }
}
