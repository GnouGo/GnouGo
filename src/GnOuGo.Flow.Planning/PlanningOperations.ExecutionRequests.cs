using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    internal sealed record RequestCohort(string Id, Scope[] Scopes, PlanningDecisionPages.Decision Decision);

    internal static RequestCohort[] RequestCohorts(PlanningSnapshot state)
    {
        var scopes = ContributionScopes(state).Where(s => ContributionMembers(state, s).Length != 0).ToArray();
        var domains = scopes.ToDictionary(s => s.Clause.Id, s => ContributionMembers(state, s)
            .SelectMany(m => EffectDomain(state, m.Evidence!).Keys).ToHashSet(StringComparer.Ordinal));
        var remaining = scopes.ToList(); var result = new List<RequestCohort>();
        while (remaining.Count != 0)
        {
            var members = new HashSet<string>(StringComparer.Ordinal) { remaining[0].Clause.Id };
            var effects = new HashSet<string>(domains[remaining[0].Clause.Id], StringComparer.Ordinal);
            bool changed;
            do
            {
                changed = false;
                foreach (var scope in remaining.Where(s => domains[s.Clause.Id].Overlaps(effects)))
                    if (members.Add(scope.Clause.Id)) { effects.UnionWith(domains[scope.Clause.Id]); changed = true; }
            } while (changed);
            var selected = remaining.Where(s => members.Contains(s.Clause.Id)).OrderBy(s => s.Clause.Id, StringComparer.Ordinal).ToArray();
            remaining.RemoveAll(s => members.Contains(s.Clause.Id));
            var id = "execution_request_" + PlanningGraphCompiler.Fingerprint(string.Join('|', selected.Select(s => s.Clause.Id)))[..24];
            var clauses = selected.Select(s => (Scope: s, Decision: RequestClauseDecision(state, s))).ToArray();
            // Local $defs must be namespaced before embedding independent clause schemas.
            var definitions = new JsonObject(); var fields = new List<(string, JsonObject)>();
            foreach (var (scope, decision) in clauses)
            {
                var schema = decision.Schema.DeepClone().AsObject();
                var prefix = "c" + fields.Count + "_";
                var rewritten = JsonNode.Parse(schema.ToJsonString().Replace("#/$defs/", "#/$defs/" + prefix, StringComparison.Ordinal))!.AsObject();
                foreach (var definition in rewritten["$defs"]!.AsObject()) definitions[prefix + definition.Key] = definition.Value!.DeepClone();
                rewritten.Remove("$defs"); fields.Add((scope.Clause.Id, rewritten));
            }
            var response = PlanningHoleRequests.Object(fields.ToArray()); response["$defs"] = definitions;
            var context = new JsonObject { ["stage"] = "execution_request", ["clauses"] = new JsonObject(clauses.Select(c =>
                new KeyValuePair<string, JsonNode?>(c.Scope.Clause.Id, c.Decision.Context.DeepClone()))) };
            var fingerprint = PlanningGraphCompiler.Fingerprint("execution-request-v1:" + EvidenceFingerprint(state) + ":" +
                response.ToJsonString() + ":" + context.ToJsonString());
            var decisionIds = clauses.SelectMany(c => c.Decision.SourceDecisionIds ?? [c.Decision.Id]).Distinct().Order(StringComparer.Ordinal).ToArray();
            result.Add(new(id, selected, new(id, response, context, fingerprint, SourceDecisionIds: decisionIds)));
        }
        return result.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }

    internal static PlanningDecisionPages.Decision[] ExecutionRequestDecisions(PlanningSnapshot state) => RequestCohorts(state).Select(c => c.Decision).ToArray();

    internal static PlanningExecutionRequestProof ParseExecutionRequest(PlanningSnapshot state, RequestCohort cohort, JsonObject answer)
    {
        if (PlanningContractValidation.ValidateInstance(answer, cohort.Decision.Schema).Count != 0)
            throw Failure(cohort.Id, "Execution request changed its issued ownership or evidence domain.");
        var units = new List<PlanningContributionUnit>();
        foreach (var scope in cohort.Scopes)
        {
            var value = ExpandRequestAnswer(state, scope, answer[scope.Clause.Id]!.AsObject());
            if (value["status"]!.ToString() != "qualified") throw Failure(scope.Clause.Id, "Execution request authority is unresolved.");
            var projected = ProjectContributionUnits(state, scope, value, cohort.Decision, true, cohort.Id);
            units.AddRange(projected.Units);
        }
        return SealRequestProof(new(1, cohort.Id, cohort.Id, cohort.Decision.EvidenceFingerprint,
            units.OrderBy(u => u.Id, StringComparer.Ordinal).ToList(), "model", ""));
    }

    private static PlanningExecutionRequestProof SealRequestProof(PlanningExecutionRequestProof proof) => proof with
    { ProofFingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(proof with { ProofFingerprint = "" }, PlanningJsonContext.Default.PlanningExecutionRequestProof)) };

    private static readonly Dictionary<string, string> RequestFields = new(StringComparer.Ordinal)
    { ["status"] = "s", ["units"] = "u", ["role"] = "r", ["request"] = "q", ["predicate"] = "p", ["evidence"] = "e", ["effect"] = "t", ["start"] = "a", ["end"] = "z", ["runtime"] = "v" };

    private static JsonObject CompactRequestSchema(JsonObject schema)
    {
        void Visit(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var child in array) Visit(child); return; }
            if (node is not JsonObject obj) return;
            foreach (var child in obj.ToArray()) Visit(child.Value);
            if (obj["properties"] is JsonObject fields)
                foreach (var field in fields.ToArray())
                    if (RequestFields.TryGetValue(field.Key, out var name)) { fields.Remove(field.Key); fields[name] = field.Value; }
            if (obj["required"] is JsonArray required)
                for (var i = 0; i < required.Count; i++)
                    if (RequestFields.TryGetValue(required[i]!.ToString(), out var name)) required[i] = name;
        }
        Visit(schema); return schema;
    }

    private static JsonObject ExpandRequestAnswer(PlanningSnapshot state, Scope scope, JsonObject answer)
    {
        JsonNode? Expand(JsonNode? value) => value switch
        {
            JsonObject obj => new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(
                RequestFields.FirstOrDefault(f => f.Value == p.Key).Key ?? p.Key, Expand(p.Value)))),
            JsonArray array => new JsonArray(array.Select(Expand).ToArray()),
            _ => value?.DeepClone()
        };
        var result = Expand(answer)!.AsObject();
        if (result["status"]?.ToString() != "qualified") return result;
        var domain = ContributionMembers(state, scope).SelectMany(m => EffectDomain(state, m.Evidence!)).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value);
        foreach (var unit in result["units"]!.AsArray())
            if (unit!["role"]!.ToString() == "requested_execution")
            {
                var request = unit["request"]!; var anchor = domain[request["effect"]!.ToString()];
                request["basis"] = anchor.BoundaryKind == "result_realization" ? "requested_result_production" : "requested_owned_occurrence";
                request["owner"] = anchor.OwnerReference; request["boundary"] = anchor.BoundaryReference;
            }
            else { unit["scope"] = scope.Clause.Id; unit["basis"] = "not_establishing_request"; }
        return result;
    }

    private static PlanningExecutionRequestProof BaselineRequest(PlanningSnapshot state, PlanningRuntimeEvidence evidence)
    {
        var anchor = EffectDomain(state, evidence).Single();
        var id = "execution_request_baseline_" + evidence.Id;
        var unit = SealContributionUnit(new("", "requested_execution", evidence.ClauseReference, evidence.ActionReference,
            [evidence.ActionReference!], [evidence.Id], anchor.Key, "existing_baseline_execution", anchor.Value.OwnerReference, anchor.Value.BoundaryReference)
        { ExecutionRequestId = id });
        return SealRequestProof(new(1, id, null, PlanningGraphCompiler.Fingerprint(EvidenceFingerprint(state) + ":" + evidence.ProofFingerprint),
            [unit], "deterministic_baseline", ""));
    }

    internal static PlanningExecutionRequestProof[] ReadExecutionRequests(PlanningSnapshot state)
    {
        var cohorts = RequestCohorts(state);
        JsonObject values;
        try { values = PlanningDecisionPages.ReadCompleted(state, "intent_operations", "$plan", cohorts.Select(c => c.Decision).ToArray()); }
        catch (PlanningConflictException) { throw Failure("$plan", "Current canonical execution request authority is missing.", "INTENT_OPERATION_PROOF_MISSING"); }
        var proofs = cohorts.Select(c => ParseExecutionRequest(state, c, values[c.Id]!.AsObject())).ToList();
        foreach (var scope in DeriveScopes(state).Where(s => s.Evidence!.BaselineReference is not null && s.Source.Structural))
            proofs.Add(BaselineRequest(state, scope.Evidence!));
        return proofs.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
    }

    private static async Task GroundExecutionRequests(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_operations", "$plan", ExecutionRequestDecisions(state), ct);
        _ = ReadExecutionRequests(state);
    }

    internal static PlanningDecisionPages.Decision RequestClauseDecision(PlanningSnapshot state, Scope scope)
    {
        if (scope.Evidence is not null) RequireEligibleContribution(state, scope.Evidence);
        var members = ContributionMembers(state, scope);
        foreach (var member in members) RequireEligibleContribution(state, member.Evidence!);
        var definitions = new JsonObject();
        var alternatives = new JsonArray(members.Select(member => (JsonNode)PlanningHoleRequests.Object(
            ("role", PlanningHoleRequests.Enum(["not_requested"])),
            ("runtime", PlanningHoleRequests.Enum([member.Evidence!.Id])),
            ("evidence", ContributionOwnedSchema(state, [member])))).ToArray());
        var domains = members.ToDictionary(s => s.Evidence!.Id, s => EffectDomain(state, s.Evidence!), StringComparer.Ordinal);
        var domain = domains.Values.SelectMany(d => d).GroupBy(p => p.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        var requests = new JsonArray();
        var domainIndex = 0;
        foreach (var (id, anchor) in domain)
        {
            if (anchor.BoundaryKind != "result_realization" && anchor.OccurrenceProof is null) continue;
            var eligible = members.Where(s => domains[s.Evidence!.Id].ContainsKey(id)).ToArray();
            var supportName = "e" + domainIndex++;
            definitions[supportName] = ContributionOwnedSchema(state, eligible);
            var support = new JsonObject { ["$ref"] = "#/$defs/" + supportName };
            requests.Add((JsonNode)PlanningHoleRequests.Object(
                ("predicate", support.DeepClone().AsObject()),
                ("evidence", new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 4, ["items"] = support }),
                ("effect", PlanningHoleRequests.Enum([id]))));
        }
        if (requests.Count != 0)
        {
            definitions["request"] = new JsonObject { ["anyOf"] = requests };
            alternatives.Add((JsonNode)PlanningHoleRequests.Object(("role", PlanningHoleRequests.Enum(["requested_execution"])),
                ("request", new JsonObject { ["$ref"] = "#/$defs/request" })));
        }
        var schema = new JsonObject { ["$defs"] = definitions, ["anyOf"] = new JsonArray(
            PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["unresolved"]))),
            PlanningHoleRequests.Object(("status", PlanningHoleRequests.Enum(["qualified"])),
                ("units", new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxContributionUnits,
                    ["items"] = new JsonObject { ["anyOf"] = alternatives } }))) };
        var context = new JsonObject
        {
            ["stage"] = "execution_request", ["scope"] = scope.Clause.Id,
            ["task"] = "Establish whether execution of each issued effect is actually requested, using all owned clauses in this cohort. Bind positive request predicates and their exact execution/result-selection evidence. Account for remaining candidate evidence as not establishing a request, or unresolved. Candidate effects, preliminary execution facts, necessity, properties, declarations and boundaries alone confer no request authority. Several clauses may support one effect; independently owned occurrences remain separate. No capability, data binding, governing qualification or applicability is decided here.",
            ["clause"] = PlanningChoiceEvidence.Text(state, scope.Clause.Id), ["boundaries"] = scope.Words.DeepClone(),
            ["sourceOwnership"] = "Account for every eligible runtime record using exact request evidence or an explicit not-requested outcome bound to that record. Complete clauses and existing obligations provide context, not additional executable eligibility. Not establishing a request does not establish irrelevance, governing qualification or applicability. Rules, conditions, fallbacks and properties are qualified separately after request authority is fixed. Unknown request authority requires unresolved.",
            ["semanticEvidence"] = new JsonArray(ContributionObligations(state, scope).Select(o => (JsonNode)new JsonObject
            { ["id"] = o.Id, ["kind"] = o.Kind, ["references"] = CoverageStrings(o.EvidenceReferences), ["grounding"] = o.Grounding!.Fingerprint }).ToArray()),
            ["contracts"] = CoverageStrings(ContractCoverage(state).Where(c => Overlaps(scope.Clause, c.Span)).Select(c => c.Span.Id)),
            ["provenance"] = new JsonObject(members.Select(s => new KeyValuePair<string, JsonNode?>(s.Evidence!.Id, new JsonObject
            { ["reference"] = s.Evidence.ActionReference, ["span"] = PlanningChoiceEvidence.Text(state, s.Evidence.ActionReference!),
                ["kind"] = s.Evidence.Kind, ["necessity"] = s.Evidence.Necessity.ToString(),
                ["effects"] = CoverageStrings(domains[s.Evidence.Id].Keys) }))),
            ["effects"] = new JsonObject(domain.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject
            { ["scope"] = p.Value.WorkflowScope, ["owner"] = p.Value.OwnerReference, ["boundary"] = p.Value.BoundaryKind,
                ["boundaryReference"] = p.Value.BoundaryReference, ["ownershipProof"] = p.Value.OccurrenceProof?.Fingerprint,
                ["result"] = state.Declarations.SingleOrDefault(d => d.Id == p.Value.OwnerReference) is { } declaration ? PlanningDeclarations.Name(state, declaration) : null })))
        };
        schema = CompactRequestSchema(schema);
        context["wire"] = "s=status; u=units; r=role; q=request; p=predicate; e=evidence; t=effect; v=runtime evidence; a=start; z=end. Ownership and positive basis are fixed by the issued effect, never selected independently.";
        return new(ContributionDecisionId(scope), schema, context,
            PlanningGraphCompiler.Fingerprint("execution-request-clause-v1:" + EvidenceFingerprint(state) + ":" +
                string.Join('|', members.Select(s => s.Evidence!.ProofFingerprint)) + ":" + schema.ToJsonString() + ":" + context.ToJsonString()),
            SourceDecisionIds: members.Select(s => "contribution_" + s.Evidence!.Id).Append(ContributionDecisionId(scope))
                .Concat(ContributionObligations(state, scope).Where(o => o.Kind is "runtime_condition" or "runtime_fallback").Select(o => "contribution_" + o.Id))
                .Distinct().Order(StringComparer.Ordinal).ToArray());
    }

}
