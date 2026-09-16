using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Explicit synthetic effect facts. Never imports or replaces historical receipts.</summary>
internal static class OperationEffectFixtures
{
    internal static JsonObject Answer(PlanningSnapshot state, PlanningOperations.Scope scope,
        IEnumerable<string>? effects = null, string? contribution = null, IEnumerable<string>? inputs = null,
        IEnumerable<string>? outputs = null)
    {
        var domain = PlanningOperations.EffectDomain(state, scope.Evidence!);
        var known = PlanningOperations.Scopes(state).SelectMany(s => PlanningOperations.EffectDomain(state, s.Evidence!))
            .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        var ids = effects?.ToArray() ?? [(domain.FirstOrDefault(p => p.Value.BoundaryReference == scope.Evidence!.ActionReference).Key ?? domain.FirstOrDefault(p => p.Value.BoundaryKind == "result_realization").Key ?? domain.First().Key)];
        return new()
        {
            ["status"] = "mapped", ["contribution"] = contribution ?? (scope.Evidence!.EvidenceRole == "governing" ? "governs" : "realizes"),
            ["effects"] = Strings(ids), ["inputs"] = Strings(inputs ?? []),
            ["outputs"] = Strings(outputs ?? ids.Select(id => known[id]).Where(a => a.BoundaryKind == "result_realization").Select(a => a.OwnerReference)),
            ["evidence"] = Strings([scope.Evidence!.ClauseReference])
        };
    }

    internal static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    internal static PlanningOperations.Scope Scope(PlanningSnapshot state, string id) => PlanningOperations.Scopes(state).Single(s =>
        PlanningOperations.EffectDecisionId(s.Evidence!) == id || PlanningOperations.EffectDecisionId(s.Evidence!, true) == id);

    internal static JsonObject Defer(PlanningOperations.Scope scope) => new() { ["status"] = "governing", ["evidence"] = Strings([scope.Clause.Id]) };

    internal static void Seed(PlanningSnapshot state, Func<PlanningOperations.Scope, JsonObject>? answer = null, bool rootsOnly = false, Func<string, string, bool>? dependency = null, bool seedDependencies = true)
    {
        SeedBoundaries(state);
        var scopes = PlanningOperations.Scopes(state).Where(s => s.Evidence!.BaselineReference is null).ToArray();
        var answers = scopes.ToDictionary(s => s.Evidence!.Id, s => answer?.Invoke(s) ??
            (s.Evidence!.EvidenceRole == "governing" ? Defer(s) : Answer(state, s)));
        SeedContributions(state, answer);
        var groups = PlanningOperations.CoverageGroups(state);
        var values = new JsonObject(groups.Select(g => new KeyValuePair<string, JsonNode?>(g.Id, CoverageAnswer(state, g, answer))));
        SeedPages(state, groups.Select(g => g.Decision).ToArray(), values);
        if (rootsOnly || values.Any(p => p.Value?["status"]?.ToString() == "unresolved")) return;
        var covered = groups.SelectMany(g => g.Scopes).Select(s => s.Evidence!.Id).ToHashSet();
        var realized = PlanningOperations.ReadRealizations(state);
        var pending = scopes.Where(s => !covered.Contains(s.Evidence!.Id) && s.Evidence.EvidenceRole == "governing")
            .Where(s => PlanningOperations.RealizedDomain(state, s.Evidence!, realized).Count != 0 && PlanningOperations.DeterministicGoverning(state, s, realized) is null).ToArray();
        values = new JsonObject(pending.Select(s => new KeyValuePair<string, JsonNode?>(PlanningOperations.EffectDecisionId(s.Evidence!, true),
            answer is not null ? answers[s.Evidence!.Id].DeepClone() : Answer(state, s, [PlanningOperations.RealizedDomain(state, s.Evidence!, realized).First().Key], "governs"))));
        SeedPages(state, pending.Select(s => PlanningOperations.EffectDecision(state, s, realized)).ToArray(), values);
        if (seedDependencies)
            try { SeedDependencies(state, Staged(state), dependency); }
            catch (GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException) { /* Invalid synthetic roots are assessed by the production admission call. */ }
    }

    // Explicit synthetic qualification responses, not runtime-label authority in production.
    internal static JsonObject ContributionAnswer(PlanningSnapshot state, PlanningOperations.Scope scope, JsonObject? value = null)
    {
        value ??= scope.Evidence!.EvidenceRole == "governing" ? Defer(scope) : Answer(state, scope);
        if (value["status"]?.ToString() == "unresolved") return new() { ["status"] = "unresolved" };
        var excluded = value["status"]?.ToString() == "not_an_effect";
        var role = excluded ? "excluded" : value["status"]?.ToString() == "governing" || value["contribution"]?.ToString() is "governs" or "shared_rule" ? "governs" : "supports";
        var domain = PlanningOperations.EffectDomain(state, scope.Evidence!);
        var ids = value["effects"]?.AsArray().Select(v => v!.ToString()).ToArray() ?? (domain.Count == 0 ? [] : new[] { domain.First().Key });
        if (!excluded && ids.Length == 0) return new() { ["status"] = "unresolved" };
        var items = new JsonArray();
        foreach (var id in excluded ? new[] { "" } : ids)
        {
            var item = new JsonObject { ["role"] = role, ["evidence"] = scope.Evidence!.ActionReference };
            if (excluded) item["basis"] = "no_requested_execution";
            else item["effect"] = id;
            if (role == "supports")
            {
                var anchor = domain[id];
                item["basis"] = anchor.BoundaryKind == "result_realization" ? "requested_result_production" : "requested_owned_occurrence";
                item["owner"] = anchor.OwnerReference; item["boundary"] = anchor.BoundaryReference;
            }
            items.Add((JsonNode)item);
        }
        return new() { ["status"] = "qualified", ["contributions"] = items };
    }

    internal static void SeedContributions(PlanningSnapshot state, Func<PlanningOperations.Scope, JsonObject>? answer = null)
    {
        SeedBoundaries(state);
        var scopes = PlanningOperations.Scopes(state).Where(s => s.Evidence!.BaselineReference is null).ToArray();
        var values = new JsonObject(scopes.Select(s => new KeyValuePair<string, JsonNode?>(PlanningOperations.ContributionDecisionId(s.Evidence!),
            ContributionAnswer(state, s, answer?.Invoke(s)))));
        SeedPages(state, PlanningOperations.ContributionDecisions(state), values);
    }

    internal static PlanningOperations.CoverageGroup[] Groups(PlanningSnapshot state, Func<PlanningOperations.Scope, JsonObject>? answer = null)
    { SeedContributions(state, answer); return PlanningOperations.CoverageGroups(state); }

    // Explicit synthetic boundary ownership, separate from action labels and historical receipts.
    internal static void SeedBoundaries(PlanningSnapshot state, Func<PlanningOperations.Scope, string>? workflow = null)
    {
        var scopes = PlanningOperations.Scopes(state).Where(s => s.Evidence!.OccurrenceBoundary is not null).ToArray();
        var decisions = scopes.Select(s => PlanningOperations.OccurrenceDecision(state, s.Evidence!)).OfType<PlanningDecisionPages.Decision>().ToArray();
        var values = new JsonObject(decisions.Select(d =>
        {
            var scope = scopes.Single(s => d.Id == "boundary_" + s.Evidence!.Id);
            var selected = workflow?.Invoke(scope) ?? "main";
            var variant = d.Schema["anyOf"]!.AsArray().Single(v => v!["properties"]?["scope"]?["enum"]?[0]?.ToString() == selected)!;
            return new KeyValuePair<string, JsonNode?>(d.Id, new JsonObject { ["scope"] = selected,
                ["scopeEvidence"] = variant["properties"]!["scopeEvidence"]!["enum"]?[0]?.DeepClone() });
        }));
        SeedPages(state, decisions, values);
    }

    internal static List<PlanningObligation> Staged(PlanningSnapshot state)
    {
        var realized = PlanningOperations.ReadRealizations(state);
        var operations = PlanningOperations.MaterializeCoverage(state);
        var covered = PlanningOperations.ReadCoverage(state).SelectMany(p => p.Contributions).Select(c => c.RuntimeEvidenceId).ToHashSet();
        void Add(PlanningOperationAssignment assignment)
        {
            var existing = operations.Single(o => o.Id == assignment.EffectId);
            operations.Remove(existing);
            operations.Add(PlanningOperations.Prove(state, existing, existing.OperationAdmission! with
            { Assignments = [.. existing.OperationAdmission!.Assignments, assignment] }));
        }
        foreach (var scope in PlanningOperations.Scopes(state))
        {
            if (covered.Contains(scope.Evidence!.Id)) continue;
            var deterministic = scope.Evidence!.EvidenceRole == "governing" && PlanningOperations.RealizedDomain(state, scope.Evidence, realized).Count > 0
                ? PlanningOperations.DeterministicGoverning(state, scope, realized) : null;
            var value = state.DecisionPages.LastOrDefault(p => p.Candidate?[PlanningOperations.EffectDecisionId(scope.Evidence, true)] is not null)?
                .Candidate![PlanningOperations.EffectDecisionId(scope.Evidence, true)] as JsonObject;
            if (deterministic is null && value is null) continue;
            var proof = deterministic ?? PlanningOperations.ParseEffect(state, scope, value!, realized);
            foreach (var anchor in proof.Candidates)
            {
                var id = PlanningOperations.CanonicalId(state, anchor, scope.Evidence.Kind!, scope.Evidence.BaselineReference);
                Add(PlanningOperations.Assignment(scope, proof, id, id, "attach", "deterministic"));
            }
        }
        return operations;
    }

    internal static JsonObject CoverageAnswer(PlanningSnapshot state, PlanningOperations.CoverageGroup group,
        Func<PlanningOperations.Scope, JsonObject>? answer = null)
    {
        var answers = group.Scopes.DistinctBy(s => s.Evidence!.Id).ToDictionary(s => s.Evidence!.Id, s => answer?.Invoke(s) ??
            (s.Evidence!.EvidenceRole == "governing" ? Defer(s) : Answer(state, s)));
        var plan = group.Plans.FirstOrDefault(p => p.Value.All(c =>
        {
            var value = answers[c.RuntimeEvidenceId];
            var role = value["status"]?.ToString() is "omitted" ? "omitted" : value["status"]?.ToString() == "governing" ||
                value["contribution"]?.ToString() is "governs" or "shared_rule" ? "governs" : "supports";
            if (value["status"]?.ToString() is "not_an_effect" or "unresolved") return false;
            var targets = value["effects"]?.AsArray().Select(v => v!.ToString()).Order().ToArray();
            return c.Disposition == role && (targets is null || c.Effects.All(targets.Contains));
        }));
        if (plan.Key is null) return new() { ["status"] = "unresolved" };
        var effects = new JsonObject();
        foreach (var id in plan.Value.Where(c => c.Disposition == "supports").SelectMany(c => c.Effects).Distinct().Order())
        {
            var values = plan.Value.Where(c => c.Effects.Contains(id)).Select(c => answers[c.RuntimeEvidenceId]).ToArray();
            IEnumerable<string> Refs(string field) => values.SelectMany(v => v[field]?.AsArray().Select(r => r!.ToString()) ?? []).Distinct().Order();
            effects[id] = new JsonObject { ["inputs"] = Strings(Refs("inputs")), ["outputs"] = Strings(Refs("outputs")) };
        }
        return new() { ["status"] = "complete", ["mapping"] = plan.Key, ["effects"] = effects };
    }

    internal static JsonObject DependencyAnswer(JsonObject schema, bool data = false) => new()
    {
        ["relation"] = data ? "data" : "none",
        ["producerEvidence"] = new JsonArray(schema["anyOf"]![0]!["properties"]!["producerEvidence"]!["items"]!["enum"]![0]!.DeepClone()),
        ["consumerEvidence"] = new JsonArray(schema["anyOf"]![0]!["properties"]!["consumerEvidence"]!["items"]!["enum"]![0]!.DeepClone())
    };

    // Explicitly synthetic dependency assessments, never historical receipts.
    internal static void SeedDependencies(PlanningSnapshot state, List<PlanningObligation> operations, Func<string, string, bool>? dependency = null)
    {
        var domain = PlanningOperations.DependencyDecisions(state, operations);
        var answers = new JsonObject(domain.Decisions.Select(d => new KeyValuePair<string, JsonNode?>(d.Id,
            DependencyAnswer(d.Schema, dependency?.Invoke(d.Context["producer"]!.ToString(), d.Context["consumer"]!.ToString()) == true))));
        SeedPages(state, domain.Decisions, answers);
        PlanningOperations.InstallDependencies(state, operations, domain, answers);
    }

    internal static void SeedPages(PlanningSnapshot state, PlanningDecisionPages.Decision[] decisions, JsonObject answers)
    {
        // Synthetic fixture precondition, deliberately no dispatch/receipt claim.
        while (PlanningDecisionPages.Completed(state, "intent_operations", "$plan", decisions) is null)
        {
            var page = state.DecisionPages.Last(p => p.Phase == "intent_operations" && p.Status == "pending" && p.ParentId is null);
            page.Status = "completed"; page.Candidate = new(page.Decisions.Select(id => new KeyValuePair<string, JsonNode?>(id, answers[id]!.DeepClone())));
        }
    }

    internal static JsonObject Response(PlanningSnapshot state, LLMRequest request) => new(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
    {
        if (p.Key.StartsWith("contribution_", StringComparison.Ordinal))
            return new KeyValuePair<string, JsonNode?>(p.Key, ContributionAnswer(state, PlanningOperations.Scopes(state).Single(s => PlanningOperations.ContributionDecisionId(s.Evidence!) == p.Key)));
        if (p.Key.StartsWith("coverage_", StringComparison.Ordinal))
            return new KeyValuePair<string, JsonNode?>(p.Key, CoverageAnswer(state, PlanningOperations.CoverageGroups(state).Single(g => g.Id == p.Key)));
        if (p.Key.StartsWith("boundary_", StringComparison.Ordinal))
            return new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject { ["scope"] = "main", ["scopeEvidence"] = null });
        if (p.Key.StartsWith("data_", StringComparison.Ordinal)) return new KeyValuePair<string, JsonNode?>(p.Key, DependencyAnswer(p.Value!.AsObject()));
        if (p.Key.StartsWith("operation_", StringComparison.Ordinal)) return new KeyValuePair<string, JsonNode?>(p.Key, p.Value!["enum"]![0]!.DeepClone());
        var scope = Scope(state, p.Key);
        var governing = p.Key == PlanningOperations.EffectDecisionId(scope.Evidence!, true);
        var domain = governing ? PlanningOperations.RealizedDomain(state, scope.Evidence!, PlanningOperations.ReadRealizations(state)) : PlanningOperations.EffectDomain(state, scope.Evidence!);
        var result = domain.FirstOrDefault(d => d.Value.BoundaryKind == "result_realization");
        var effects = result.Key is not null ? new[] { result.Key } : governing ? [domain.First().Key] : null;
        return new KeyValuePair<string, JsonNode?>(p.Key, Answer(state, scope, effects, governing ? "governs" : "realizes"));
    }));
}
