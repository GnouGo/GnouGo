using GnOuGo.Flow.Core.Runtime;
using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    // Applicability is a transient projection of existing authority, never a
    // fulfillment decision. Unknown global constraints remain global.
    internal static HashSet<string> LocalRequirementIds(PlanningSession state,
        IEnumerable<string>? actualTaskIds = null, IEnumerable<string>? actualScopes = null)
    {
        var construction = state.Construction!;
        var compact = State(state);
        var step = compact.CurrentStep;
        var candidate = construction.Candidate;
        // A single complete source is cheaper and safer than deriving applicability
        // between small clauses. Its original restrictions accompany every decision.
        var originals = compact.Sources.Where(source => source.Kind is "page" or "answer_page" or "revision_page").ToArray();
        if (originals.Length == 1 && originals[0].Indexed &&
            state.Requirements!.Outcomes.All(requirement => compact.RequirementSources.ContainsKey(requirement.Id)) &&
            string.Concat(compact.Sources.Where(source => source.Kind is "requirement" or "global").Select(source => source.Text)) == originals[0].Text)
        {
            var original = IntentRequest(state, originals[0]);
            if (ModelRequestAdmission.EstimateTokens(PlanningModelCalls.PrepareRequest(state, IntentPurpose,
                original.Prompt, original.StructuredOutputSchema!.AsObject())) <= PlanningConstructionInputPolicy.EffectiveLimit(state.Request, IntentPurpose))
                return state.Requirements.Outcomes.Select(requirement => requirement.Id).ToHashSet(StringComparer.Ordinal);
        }
        var requirements = (step?.RequirementIds.AsEnumerable() ?? SelectionRequirementIds(state))
            .Concat(step?.CloseRequirementIds ?? []).Concat(EvidenceRequirementIds(state))
            .Concat(candidate?.Extension.RequirementIds ?? []).ToHashSet(StringComparer.Ordinal);
        var sources = compact.GlobalSourceIds.ToHashSet(StringComparer.Ordinal);
        var tasks = (actualTaskIds ?? []).Concat(step?.InputTaskIds ?? []).Concat(step?.DependencyTaskIds ?? [])
            .Concat(candidate?.TaskIds ?? []).Concat(candidate?.ImportedTaskIds ?? []).ToHashSet(StringComparer.Ordinal);
        var scopes = (actualScopes ?? []).Concat(step is null ? [] : [step.Scope])
            .Concat(candidate is null ? [] : [candidate.Extension.Scope]).ToHashSet(StringComparer.Ordinal);
        if (step?.GroupId is { } groupId) scopes.Add("/groups/" + groupId + "/body");
        scopes.UnionWith((step?.BoundaryTransitions ?? []).Concat(candidate?.Extension.BoundaryTransitions ?? [])
            .Select(transition => transition.Scope));
        var associations = new[] { step?.ContinuationBinding, candidate?.Extension.ContinuationBinding }.OfType<PlanningConstructionContinuationBinding>().ToArray();
        foreach (var selectedAssociation in associations)
            foreach (var consumer in (compact.SuspendedSteps ?? []).Where(consumer => consumer.Id == selectedAssociation.ContinuationId ||
                (selectedAssociation.DischargedContinuationIds ?? []).Contains(consumer.Id, StringComparer.Ordinal)))
            {
                requirements.UnionWith(consumer.Consumer.RequirementIds);
                requirements.UnionWith(consumer.Consumer.CloseRequirementIds);
                scopes.Add(consumer.Consumer.Scope);
            }
        requirements.UnionWith((step?.BoundaryTransitions ?? []).Concat(candidate?.Extension.BoundaryTransitions ?? [])
            .SelectMany(transition => transition.RequirementIds));
        var values = (step?.Inputs ?? []).SelectMany(TaskPlanCompiler.Values)
            .Concat((candidate?.Extension.Outputs ?? []).SelectMany(output => TaskPlanCompiler.Values(output.Value)))
            .Concat((step?.BoundaryTransitions ?? []).SelectMany(transition => transition.Outputs).SelectMany(output => TaskPlanCompiler.Values(output.Value)))
            .Concat(step?.ContinuationBinding is { } binding ? TaskPlanCompiler.Values(binding.Binding) : [])
            .Concat((candidate?.Extension.BoundaryTransitions ?? []).SelectMany(transition => transition.Outputs).SelectMany(output => TaskPlanCompiler.Values(output.Value)))
            .Concat(candidate?.Extension.ContinuationBinding is { } association ? TaskPlanCompiler.Values(association.Binding) : []).ToList();
        foreach (var review in (candidate?.Reviews ?? []).Concat(PlanningConstruction.ReplacementDefectReviews(state)))
            foreach (var finding in (review["defectWitness"]?["findings"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (finding["obligation"]?["value"]?["id"]?.ToString() is { } id) requirements.Add(id);
                foreach (var address in (finding["evidenceAddresses"] as JsonArray ?? []).Select(value => value?.ToString()))
                    if (address?.StartsWith("requirement:", StringComparison.Ordinal) == true) requirements.Add(address[12..]);
                    else if (address?.StartsWith("source:", StringComparison.Ordinal) == true) sources.Add(address[7..]);
            }
        foreach (var (address, _) in construction.Evidence ?? new JsonObject())
            if (address.StartsWith("source:", StringComparison.Ordinal)) sources.Add(address[7..]);

        var plan = candidate?.Plan ?? state.Plan;
        if (plan is not null)
        {
            var symbols = new TaskPlanSymbols(plan);
            var pending = new Queue<string>(tasks);
            foreach (var scope in scopes.ToArray()) IncludeScope(symbols.Scopes.SingleOrDefault(item => item.Path == scope));
            foreach (var value in values.Where(value => value.Kind is "output" or "present"))
                if (value.Source is { } id && tasks.Add(id)) pending.Enqueue(id);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (pending.TryDequeue(out var id))
            {
                if (!visited.Add(id) || !symbols.Tasks.TryGetValue(id, out var site)) continue;
                IncludeScope(site.Scope);
                values.AddRange(TaskPlanCompiler.Values(site.Task));
                foreach (var dependency in site.Task.DependsOn.Concat(TaskPlanCompiler.Values(site.Task)
                    .Where(value => value.Kind is "output" or "present").Select(value => value.Source).OfType<string>()))
                    if (tasks.Add(dependency)) pending.Enqueue(dependency);
                if (site.Task.Group is { } group && !symbols.InvalidIds.Contains(group) &&
                    plan.Groups.SingleOrDefault(item => item.Id == group) is { } declaration)
                {
                    scopes.Add("/groups/" + group + "/body");
                    foreach (var member in TaskPlanRevisions.Tasks(declaration.Body))
                        if (tasks.Add(member.Id)) pending.Enqueue(member.Id);
                }
                // Only actual retained contributions establish this association;
                // a matching name or output type cannot add an obligation.
                foreach (var checkpoint in construction.Checkpoints.Where(checkpoint => checkpoint.TaskIds.Contains(id, StringComparer.Ordinal) &&
                    !checkpoint.InvalidatedTaskIds.Contains(id, StringComparer.Ordinal))) requirements.UnionWith(checkpoint.RequirementIds);
            }
            foreach (var checkpoint in construction.Checkpoints.Where(checkpoint => checkpoint.ClosedScopes.Any(scopes.Contains) ||
                (checkpoint.Fragment["groups"] as JsonArray ?? []).Any(group => group is JsonValue value && value.TryGetValue<string>(out var id) &&
                    scopes.Contains("/groups/" + id + "/body") && symbols.Scopes.Any(scope => scope.Path == "/groups/" + id + "/body"))))
                requirements.UnionWith(checkpoint.RequirementIds);

            void IncludeScope(TaskPlanSymbols.Scope? scope)
            {
                for (; scope is not null; scope = scope.Parent)
                {
                    scopes.Add(scope.Path);
                    if (scope.Owner is { } owner && tasks.Add(owner.Id)) pending.Enqueue(owner.Id);
                }
            }
        }

        var inputNames = values.Where(value => value.Kind == "input").Select(value => value.Source).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var outputNames = (step?.OutputNames ?? []).ToHashSet(StringComparer.Ordinal);
        bool changed;
        do
        {
            var before = sources.Count + requirements.Count;
            foreach (var id in requirements)
                sources.UnionWith(compact.RequirementSources.GetValueOrDefault(id) ?? []);
            foreach (var source in compact.Sources.Where(source => source.Kind is "requirement" or "global" && source.ContinuationOf is not null))
                if (sources.Contains(source.Id) || sources.Contains(source.ContinuationOf!))
                { sources.Add(source.Id); sources.Add(source.ContinuationOf!); }
            foreach (var source in compact.Sources.Where(source => source.Indexed))
                if ((source.Inputs ?? []).Any(input => inputNames.Contains(input.Name)) ||
                    (source.Outputs ?? []).Any(output => outputNames.Contains(output.Name))) sources.Add(source.Id);
            foreach (var (id, originalSources) in compact.RequirementSources)
                if (originalSources.Any(sources.Contains)) requirements.Add(id);
            changed = before != sources.Count + requirements.Count;
        } while (changed);
        requirements.IntersectWith(state.Requirements!.Outcomes.Select(requirement => requirement.Id));
        return requirements;
    }

    private static void AddOriginalDecisionContext(PlanningSession state, JsonObject context)
    {
        var ids = LocalRequirementIds(state);
        var rows = OriginalRequirementRows(state, ids);
        if (State(state).CurrentStep is null)
        {
            // Existing exact page validation already proves these originals are
            // displayed. Preserve additional accepted-contribution navigation.
            var supplied = EvidenceRequirementIds(state);
            for (var index = rows.Count - 1; index >= 0; index--)
                if (supplied.Contains(rows[index]!["id"]!.ToString()) &&
                    (rows[index]!["acceptedProgress"] is null || rows[index]!["acceptedProgress"]!["checkpointCount"]!.GetValue<int>() == 0))
                    rows.RemoveAt(index);
        }
        context["requirements"] = rows;
        context["globalConstraints"] = OriginalGlobalRows(state);
        var omitted = state.Requirements!.Outcomes.FindIndex(requirement => !ids.Contains(requirement.Id));
        if (omitted < 0)
        {
            context.Remove("omittedRequirementCount");
            if (context.ContainsKey("nextRequirements")) context["nextRequirements"] = null;
            return;
        }
        context["omittedRequirementCount"] = state.Requirements.Outcomes.Count - ids.Count;
        context["requirementsDirectory"] = "page:requirements:0";
        var next = State(state).CurrentStep is not null ? omitted : state.Requirements.Outcomes.FindIndex(requirement =>
            !ids.Contains(requirement.Id) && state.Construction!.Requirements.Single(item => item.Id == requirement.Id).Status != "closed");
        context["nextRequirements"] = next < 0 ? null : "page:requirements:" + next.ToString(CultureInfo.InvariantCulture);
    }
}
