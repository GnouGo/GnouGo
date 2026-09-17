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

    internal static void Seed(PlanningSnapshot state, Func<PlanningOperations.Scope, JsonObject>? answer = null, bool rootsOnly = false, Func<string, string, bool>? dependency = null, bool seedDependencies = true, Func<PlanningOperations.Scope[], JsonObject>? qualification = null)
    {
        SeedBoundaries(state);
        var scopes = PlanningOperations.Scopes(state).Where(s => s.Evidence!.BaselineReference is null).ToArray();
        var answers = scopes.ToDictionary(s => s.Evidence!.Id, s => answer?.Invoke(s) ??
            (s.Evidence!.EvidenceRole == "governing" ? Defer(s) : Answer(state, s)));
        SeedContributions(state, answer, qualification);
        var groups = PlanningOperations.CoverageGroups(state);
        var values = new JsonObject(groups.Select(g => new KeyValuePair<string, JsonNode?>(g.Id, CoverageAnswer(state, g, answer))));
        SeedPages(state, groups.Select(g => g.Decision).ToArray(), values);
        if (rootsOnly || values.Any(p => p.Value?["status"]?.ToString() == "unresolved")) return;
        SeedApplicability(state, answer);
        if (seedDependencies)
            try { SeedDependencies(state, Staged(state), dependency); }
            catch (GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException) { /* Invalid synthetic roots are assessed by the production admission call. */ }
    }

    // Explicit synthetic qualification responses, not runtime-label authority in production.
    internal static JsonObject ContributionAnswer(PlanningSnapshot state, PlanningOperations.Scope scope, JsonObject? value = null)
    {
        value ??= scope.Evidence!.EvidenceRole == "governing" || PlanningOperations.EffectDomain(state, scope.Evidence).Count == 0 ? Defer(scope) : Answer(state, scope);
        if (value["status"]?.ToString() == "unresolved") return new() { ["status"] = "unresolved" };
        var excluded = value["status"]?.ToString() == "not_an_effect";
        var role = excluded ? "excluded" : value["status"]?.ToString() == "governing" || value["contribution"]?.ToString() is "governs" or "shared_rule" ? "governing_property" : "supports";
        var domain = PlanningOperations.EffectDomain(state, scope.Evidence!);
        var ids = value["effects"]?.AsArray().Select(v => v!.ToString()).ToArray() ?? (domain.Count == 0 ? [] : new[] { domain.First().Key });
        if (role == "supports" && ids.Length == 0) return new() { ["status"] = "unresolved" };
        var items = new JsonArray();
        foreach (var id in role != "supports" ? new[] { "" } : ids)
        {
            var item = new JsonObject { ["role"] = role == "supports" ? "requested_execution" : role, ["scope"] = scope.Clause.Id, ["evidence"] = scope.Evidence!.ActionReference };
            if (excluded) item["basis"] = "no_operation_relevance";
            if (role == "governing_property") item["governingKind"] = "descriptive_property";
            else if (role == "supports") item["effect"] = id;
            if (role == "supports")
            {
                item["predicate"] = scope.Evidence!.ActionReference;
                item["qualifiers"] = new JsonArray();
                item["evidence"] = Strings([scope.Evidence.ActionReference!]);
                var anchor = domain[id];
                item["basis"] = anchor.BoundaryKind == "result_realization" ? "requested_result_production" : "requested_owned_occurrence";
                item["owner"] = anchor.OwnerReference; item["boundary"] = anchor.BoundaryReference;
                var request = new JsonObject();
                foreach (var field in new[] { "predicate", "evidence", "effect", "basis", "owner", "boundary" })
                { request[field] = item[field]?.DeepClone(); item.Remove(field); }
                item["request"] = request; item.Remove("scope");
            }
            items.Add((JsonNode)item);
        }
        return new() { ["status"] = "qualified", ["units"] = items };
    }

    internal static void SeedContributions(PlanningSnapshot state, Func<PlanningOperations.Scope, JsonObject>? answer = null, Func<PlanningOperations.Scope[], JsonObject>? qualification = null)
    {
        SeedBoundaries(state);
        var scopes = PlanningOperations.Scopes(state).Where(s => s.Evidence!.BaselineReference is null).ToArray();
        var values = qualification is null ? QualificationAnswers(state, s => ContributionAnswer(state, s, answer?.Invoke(s))) : new JsonObject(scopes.GroupBy(s => PlanningOperations.ContributionDecisionId(s.Evidence!)).Select(g => new KeyValuePair<string, JsonNode?>(g.Key, qualification(g.ToArray()))));
        foreach (var scope in PlanningOperations.ContributionScopes(state))
            if (values[PlanningOperations.ContributionDecisionId(scope)] is null)
                values[PlanningOperations.ContributionDecisionId(scope)] = SourcePropertyAnswer(state, scope);
        values = SeparateRequestAuthority(state, values);
        SeedPages(state, PlanningOperations.ContributionDecisions(state), values);
    }

    internal static JsonObject SplitProperty(PlanningSnapshot state, PlanningOperations.Scope scope, int boundary)
    {
        var answer = ContributionAnswer(state, scope);
        var support = answer["units"]![0]!["request"]!;
        support["predicate"] = new JsonObject { ["start"] = "b0", ["end"] = "b" + boundary };
        support["evidence"] = new JsonArray(support["predicate"]!.DeepClone());
        var last = scope.Boundaries["properties"]!["end"]!["enum"]!.AsArray().Last()!.ToString();
        answer["units"]!.AsArray().Add((JsonNode)new JsonObject { ["role"] = "governing_property", ["governingKind"] = "descriptive_property", ["scope"] = scope.Clause.Id,
            ["evidence"] = new JsonObject { ["start"] = "b" + boundary, ["end"] = last } });
        return answer;
    }

    internal static JsonObject QualificationAnswers(PlanningSnapshot state, Func<PlanningOperations.Scope, JsonObject> answer) =>
        new(PlanningOperations.Scopes(state).Where(s => s.Evidence!.BaselineReference is null)
            .GroupBy(s => PlanningOperations.ContributionDecisionId(s.Evidence!)).Select(g =>
                new KeyValuePair<string, JsonNode?>(g.Key, CompleteQualification(state, g.First(), CombineQualifications(g.Select(answer))))));

    internal static JsonObject SourcePropertyAnswer(PlanningSnapshot state, PlanningOperations.Scope scope)
    {
        var units = new JsonArray(state.Obligations.Where(o => o.Grounding?.ClauseReference == scope.Clause.Id && o.Kind is "runtime_condition" or "runtime_fallback")
            .SelectMany(o => o.EvidenceReferences.Select(id => (JsonNode)new JsonObject { ["role"] = "governing_property", ["scope"] = scope.Clause.Id,
                ["evidence"] = id, ["governingKind"] = o.Kind })).ToArray());
        return CompleteQualification(state, scope, new() { ["status"] = "qualified", ["units"] = units });
    }

    // Explicit synthetic assumption for old generic fixtures: otherwise unassigned
    // source words have no additional operation relevance. Production never fills gaps.
    internal static JsonObject CompleteQualification(PlanningSnapshot state, PlanningOperations.Scope scope, JsonObject answer)
    {
        if (answer["status"]?.ToString() != "qualified") return answer;
        var covered = new List<PlanningReference>();
        PlanningReference Resolve(JsonNode n) => n is JsonObject range ? scope.Select(range["start"]!.ToString(), range["end"]!.ToString()) : state.References.Single(r => r.Id == n.ToString());
        foreach (var unit in answer["units"]!.AsArray())
            if (unit!["request"] is { } request) covered.AddRange(request["evidence"]!.AsArray().Select(n => Resolve(n!)));
            else covered.Add(Resolve(unit["evidence"]!));
        covered.AddRange(PlanningOperations.RequestClauseDecision(state, scope).Context["contracts"]!.AsArray().Select(n => Resolve(n!)));
        var count = scope.Words.Count; int? start = null;
        for (var i = 0; i <= count; i++)
        {
            var word = i == count ? null : scope.Select("b" + i, "b" + (i + 1));
            var missing = word is not null && !covered.Any(r => r.Start <= word.Start && r.Start + r.Length >= word.Start + word.Length);
            if (missing) { start ??= i; continue; }
            if (start is { } first)
            {
                answer["units"]!.AsArray().Add((JsonNode)new JsonObject { ["role"] = "excluded", ["scope"] = scope.Clause.Id,
                    ["basis"] = "no_operation_relevance", ["evidence"] = new JsonObject { ["start"] = "b" + first, ["end"] = "b" + i } });
                start = null;
            }
        }
        return answer;
    }

    internal static JsonObject CombineQualifications(IEnumerable<JsonObject> answers)
    {
        var values = answers.ToArray();
        if (values.Any(v => v["status"]?.ToString() == "unresolved")) return new() { ["status"] = "unresolved" };
        return new() { ["status"] = "qualified", ["units"] = new JsonArray(values.SelectMany(v => v["units"]!.AsArray())
            .DistinctBy(v => v!.ToJsonString()).Select(v => v!.DeepClone()).ToArray()) };
    }

    // Explicit two-stage synthetic answers. These helpers are never used by production or historical replay.
    internal static JsonObject RequestAnswer(PlanningSnapshot state, PlanningOperations.RequestCohort cohort, JsonObject joint)
    {
        var result = new JsonObject();
        foreach (var scope in cohort.Scopes)
        {
            var original = joint[PlanningOperations.ContributionDecisionId(scope)]!.AsObject();
            if (original["status"]?.ToString() == "unresolved") { result[scope.Clause.Id] = original.DeepClone(); continue; }
            var requests = original["units"]!.AsArray().Where(u => u!["role"]!.ToString() == "requested_execution").ToArray();
            var units = new JsonArray(requests.Select(u => (JsonNode)new JsonObject { ["role"] = "requested_execution", ["request"] = u!["request"]!.DeepClone() }).ToArray());
            PlanningReference Resolve(JsonNode n) => n is JsonObject range ? scope.Select(range["start"]!.ToString(), range["end"]!.ToString()) : state.References.Single(r => r.Id == n.ToString());
            foreach (var member in PlanningOperations.ContributionMembers(state, scope))
            {
                var domain = PlanningOperations.EffectDomain(state, member.Evidence!);
                var support = requests.Where(u => domain.ContainsKey(u!["request"]!["effect"]!.ToString()))
                    .SelectMany(u => u!["request"]!["evidence"]!.AsArray()).Select(n => Resolve(n!)).ToArray();
                var parent = state.References.Single(r => r.Id == member.Evidence!.ActionReference);
                int? start = null;
                for (var i = 0; i <= scope.Words.Count; i++)
                {
                    var word = i == scope.Words.Count ? null : scope.Select("b" + i, "b" + (i + 1));
                    bool Contains(PlanningReference r) => word is not null && r.Start <= word.Start && r.Start + r.Length >= word.Start + word.Length;
                    var missing = word is not null && Contains(parent) && !support.Any(Contains);
                    if (missing) { start ??= i; continue; }
                    if (start is not { } first) continue;
                    units.Add((JsonNode)new JsonObject { ["role"] = "not_requested", ["runtime"] = member.Evidence!.Id, ["scope"] = scope.Clause.Id, ["basis"] = "not_establishing_request",
                        ["evidence"] = new JsonObject { ["start"] = "b" + first, ["end"] = "b" + i } }); start = null;
                }
            }
            result[scope.Clause.Id] = new JsonObject { ["status"] = "qualified", ["units"] = units };
        }
        JsonNode? Compact(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.Where(p => p.Key is not ("scope" or "basis" or "owner" or "boundary")).Select(p => new KeyValuePair<string, JsonNode?>(p.Key switch
            { "status" => "s", "units" => "u", "role" => "r", "request" => "q", "predicate" => "p", "evidence" => "e", "effect" => "t", "start" => "a", "end" => "z", "runtime" => "v", _ => p.Key }, Compact(p.Value)))),
            JsonArray array => new JsonArray(array.Select(Compact).ToArray()), _ => node?.DeepClone()
        };
        return Compact(result)!.AsObject();
    }

    internal static JsonObject PropertyAnswer(PlanningSnapshot state, PlanningOperations.Scope scope, JsonObject joint)
    {
        if (joint["status"]?.ToString() == "unresolved") return joint.DeepClone().AsObject();
        var parents = PlanningOperations.ReadExecutionRequests(state).SelectMany(p => p.Units)
            .Where(u => u.ScopeReference == scope.Clause.Id && u.Role == "requested_execution").ToArray();
        var units = new JsonArray();
        foreach (var unit in joint["units"]!.AsArray())
        {
            if (unit!["role"]!.ToString() != "requested_execution") { units.Add(unit.DeepClone()); continue; }
            var request = unit["request"]!;
            if (unit["qualifiers"] is not JsonArray qualifiers || qualifiers.Count == 0) continue;
            PlanningReference Resolve(JsonNode n) => n is JsonObject range ? scope.Select(range["start"]!.ToString(), range["end"]!.ToString()) : state.References.Single(r => r.Id == n.ToString());
            var predicate = Resolve(request["predicate"]!);
            var parent = parents.Single(p => p.EffectId == request["effect"]!.ToString() && state.References.Single(r => r.Id == p.PredicateReference) is var r && r.Start == predicate.Start && r.Length == predicate.Length);
            foreach (var qualifier in qualifiers)
            {
                var value = qualifier!.DeepClone().AsObject(); value["role"] = "governing_property"; value["parent"] = parent.Id;
                units.Add((JsonNode)value);
            }
        }
        return new() { ["status"] = "qualified", ["units"] = units };
    }

    internal static JsonObject SeparateRequestAuthority(PlanningSnapshot state, JsonObject joint)
    {
        var cohorts = PlanningOperations.RequestCohorts(state);
        var requestAnswers = new JsonObject(cohorts.Select(c => new KeyValuePair<string, JsonNode?>(c.Id, RequestAnswer(state, c, joint))));
        foreach (var page in state.DecisionPages.Where(p => p.RequestId is null && p.Status == "completed" && p.Decisions.All(id => requestAnswers.ContainsKey(id))))
            page.Candidate = new JsonObject(page.Decisions.Select(id => new KeyValuePair<string, JsonNode?>(id, requestAnswers[id]!.DeepClone())));
        SeedPages(state, cohorts.Select(c => c.Decision).ToArray(), requestAnswers);
        return new JsonObject(PlanningOperations.ContributionScopes(state).Select(scope => new KeyValuePair<string, JsonNode?>(
            PlanningOperations.ContributionDecisionId(scope), PropertyAnswer(state, scope, joint[PlanningOperations.ContributionDecisionId(scope)]!.AsObject()))));
    }

    internal static PlanningExecutionContributionProof ParseQualification(PlanningSnapshot state, PlanningOperations.Scope scope, JsonObject joint)
    {
        var values = QualificationAnswers(state, s => ContributionAnswer(state, s));
        values[PlanningOperations.ContributionDecisionId(scope)] = joint.DeepClone();
        foreach (var source in PlanningOperations.ContributionScopes(state))
            if (values[PlanningOperations.ContributionDecisionId(source)] is null) values[PlanningOperations.ContributionDecisionId(source)] = SourcePropertyAnswer(state, source);
        foreach (var cohort in PlanningOperations.RequestCohorts(state))
            _ = PlanningOperations.ParseExecutionRequest(state, cohort, RequestAnswer(state, cohort, values));
        var answers = SeparateRequestAuthority(state, values);
        return PlanningOperations.ParseContributions(state, scope, answers[PlanningOperations.ContributionDecisionId(scope)]!.AsObject());
    }

    internal static void SeedDefaultRequests(PlanningSnapshot state)
    {
        var cohorts = PlanningOperations.RequestCohorts(state);
        var joint = QualificationAnswers(state, s => ContributionAnswer(state, s));
        SeedPages(state, cohorts.Select(c => c.Decision).ToArray(), new JsonObject(cohorts.Select(c =>
            new KeyValuePair<string, JsonNode?>(c.Id, RequestAnswer(state, c, joint)))));
    }

    internal static PlanningDecisionPages.Decision[] PropertyDecisions(PlanningSnapshot state)
    { SeedDefaultRequests(state); return PlanningOperations.ContributionDecisions(state); }

    internal static PlanningDecisionPages.Decision PropertyDecision(PlanningSnapshot state, PlanningOperations.Scope scope)
    { SeedDefaultRequests(state); return PlanningOperations.ContributionDecision(state, scope); }

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

    internal static List<PlanningObligation> Staged(PlanningSnapshot state) => PlanningOperations.AttachApplicability(
        state, PlanningOperations.MaterializeCoverage(state), PlanningOperations.ReadApplicability(state));

    internal static JsonObject ApplicabilityAnswer(PlanningDecisionPages.Decision decision, IEnumerable<string>? targets = null)
    {
        var selected = targets?.ToArray() ?? [decision.Context["realized"]!.AsObject().First().Key];
        return new() { ["status"] = "active", ["bindings"] = new JsonArray(selected.Select(id => (JsonNode)new JsonObject
        { ["target"] = id, ["evidence"] = Strings([decision.Context["references"]!.AsObject().First().Key]) }).ToArray()) };
    }

    internal static void SeedApplicability(PlanningSnapshot state, Func<PlanningOperations.Scope, JsonObject>? answer = null)
    {
        var decisions = PlanningOperations.ApplicabilityDecisions(state);
        var scopes = PlanningOperations.QualifiedScopes(state);
        SeedPages(state, decisions, new JsonObject(decisions.Select(d =>
        {
            var scope = scopes.Single(s => PlanningOperations.ApplicabilityDecisionId(s) == d.Id);
            var result = answer?.Invoke(scope);
            var targets = result?["effects"]?.AsArray().Select(v => v!.ToString());
            return new KeyValuePair<string, JsonNode?>(d.Id, ApplicabilityAnswer(d, targets));
        })));
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
                value["contribution"]?.ToString() is "governs" or "shared_rule" ? "governing_property" : "supports";
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
        if (p.Key.StartsWith("execution_request_", StringComparison.Ordinal))
            return new KeyValuePair<string, JsonNode?>(p.Key, RequestAnswer(state, PlanningOperations.RequestCohorts(state).Single(c => c.Id == p.Key), QualificationAnswers(state, s => ContributionAnswer(state, s))));
        if (p.Key.StartsWith("contribution_", StringComparison.Ordinal))
        {
            var contributionScope = PlanningOperations.ContributionScopes(state).Single(s => PlanningOperations.ContributionDecisionId(s) == p.Key);
            var joint = contributionScope.Evidence is null ? SourcePropertyAnswer(state, contributionScope) : QualificationAnswers(state, s => ContributionAnswer(state, s))[p.Key]!.AsObject();
            return new KeyValuePair<string, JsonNode?>(p.Key, PropertyAnswer(state, contributionScope, joint));
        }
        if (p.Key.StartsWith("applicability_", StringComparison.Ordinal))
            return new KeyValuePair<string, JsonNode?>(p.Key, ApplicabilityAnswer(PlanningOperations.ApplicabilityDecisions(state).Single(d => d.Id == p.Key)));
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
