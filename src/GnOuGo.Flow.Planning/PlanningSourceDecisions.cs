using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Interpretation selects exact source spans; identities, text and relationship endpoints are engine owned.</summary>
internal static class PlanningSourceDecisions
{
    internal static IReadOnlyDictionary<string, string> Sources(PlanningSnapshot state)
        => PlanningIntentAssessment.IntentSources(state).ToDictionary(s => s.Id, s => s.Text, StringComparer.Ordinal);
    internal static string Text(PlanningSnapshot state, PlanningObligation obligation)
        => obligation.OperationAdmission is not null ? PlanningOperations.Text(state, obligation)
            : string.Join(" ", obligation.EvidenceReferences.Select(id => PlanningReferences.Resolve(state, id, Sources(state))));

    private static (PlanningReference Reference, PlanningIntentAssessment.IntentSource Source,
        (JsonObject Context, JsonObject Schema, Func<string, string, PlanningReference> Select) Boundaries)[] InterpretationScopes(PlanningSnapshot state)
    {
        var sources = PlanningIntentAssessment.IntentSources(state);
        return sources.Where(s => !s.Structural && !PlanningDeclaredPolicyProjection.Owns(state, s)).SelectMany(source => PlanningReferences.Register(state, source.Id, source.Kind, source.Text)
            .Where(r => !string.IsNullOrWhiteSpace(source.Text.Substring(r.Start, r.Length)))
            .Select(reference => (Reference: reference, Source: source, Boundaries: PlanningReferences.Boundaries(reference, source.Text)))).ToArray();
    }

    internal static PlanningDecisionPages.Decision[] InterpretationDecisions(PlanningSnapshot state)
    {
        var scopes = InterpretationScopes(state);
        return scopes.Select(scope =>
        {
            var item = scope.Source.Baseline is { } owner ? AnnotationSchema(owner, scope.Boundaries.Schema) : InterpretationSchema(state, scope.Source.Authority, scope.Boundaries.Schema);
            var policy = scope.Source.Authority == PlanningSourceAuthority.ConstraintsOnly;
            var fields = new List<(string, JsonObject)> { ("obligations", new JsonObject
            { ["type"] = "array", ["minItems"] = 0, ["maxItems"] = 4, ["items"] = item }) };
            if (!policy && scope.Source.Baseline is null) fields.Add(("runtime", PlanningOperations.RuntimeSchema(state, scope.Source.Authority, scope.Boundaries.Schema)));
            var context = new JsonObject
            {
                ["task"] = "Identify the semantic obligations expressed by this source. Execution kind and necessity belong only to runtime evidence; do not duplicate operation classifications as obligations. Canonical admission establishes action authority. A clause can govern multiple semantic roles. Select word boundary IDs (end is exclusive). Operations require a requested action, not a subject mentioned by a policy or condition. Runtime observations become operations only when their performance is requested. A confirmation requirement already prevents its action on rejection; describing that consequence is rejection_condition. confirmation_forbidden means an explicit prohibition on asking for confirmation. A prohibition of another interaction is workflow_policy. omission_default supplies a public input only when that input is absent; select evidence including the omitted-input condition and its explicit JSON value. An input declaration with an omission default contributes both declaration_candidate and omission_default obligations. runtime_fallback specifies the executable result when other runtime conditions do not match; it is not a declaration default. Public declaration identity evidence is declaration_candidate; direction and public-port identity are established only by canonical adjudication. Evidence constraining a declared public value or its members, including types, enums/value domains, nullability/schema restrictions and preservation requirements, is declaration_constraint. It only attaches to an established declaration and never creates a public port. explicit_value supplies an actual business/runtime value, not a restriction on a declaration or its members. Supplied inputs, omission defaults and runtime conditions/fallbacks are not missing planning choices.",
                ["runtimeTask"] = "Independently account for execution scope. Planning directives author the workflow; contracts describe public values and omission defaults. Neither is a runtime action. Local behavior performs a requested transformation inside the generated workflow, including classification rules and runtime fallbacks. It has no external effect. A runtime_action requires an explicit requested external/human/resource action and execution evidence. Resource lifecycle requires a runtime resource owned and created/acquired/released/deleted by that workflow, never authoring the workflow itself. Select exact owned execution evidence. action describes requested executable work; governing describes rules, conditions or restrictions on executable work. Necessity concerns whether the action is needed to implement the workflow, not whether its runtime condition is true. Use unspecified unless an owned span explicitly establishes required or optional necessity; absence of optionality is not optional. This facet never establishes occurrence identity or selects another clause as an operation target. boundary is null for ordinary action descriptions, result rules, conditions and descriptive statements. Supply boundary only for independently requested execution: a separate invocation, an owned intermediate result/version, an explicit repetition, a call boundary, or an owned external/human/resource transition. Select exact owner and boundary spans supporting that independent occurrence; a different action span or generated-workflow scope is not sufficient. Governing evidence always has a null boundary. For resource lifecycle, also select its owned runtime resource span. Multiple roles may coexist. Constraints-only sources cannot create actions. Unknown execution scope is unresolved.",
                ["role"] = scope.Source.Kind, ["questionContext"] = scope.Source.QuestionContext, ["words"] = scope.Boundaries.Context.DeepClone(),
                ["structuralOwner"] = scope.Source.Baseline is { } structural ? JsonSerializer.SerializeToNode(structural, PlanningJsonContext.Default.PlanningBaselineOwnership) : null
            };
            if (policy || scope.Source.Baseline is not null) { context.Remove("runtimeTask"); }
            if (scope.Source.Baseline is not null) context["task"] = "Classify only unresolved governing semantics in this designated annotation. Its structural owner, execution kind, public contracts and values are authoritative. This annotation cannot create ports or operations, change execution scope or establish resource ownership. Select owned word boundaries for constraints; information requires no additional obligation.";
            return new PlanningDecisionPages.Decision(DecisionId(scope.Reference, scope.Source.Text), PlanningHoleRequests.Object(fields.ToArray()), context,
                PlanningGraphCompiler.Fingerprint(scope.Source.Text.Substring(scope.Reference.Start, scope.Reference.Length)));
        }).ToArray();
    }

    internal static async Task InterpretAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var scopes = InterpretationScopes(state);
        var decisions = InterpretationDecisions(state);
        var values = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent", "$plan", decisions, ct);
        var obligations = PlanningDeclaredPolicyProjection.Obligations(state);
        var runtimeEvidence = PlanningIntentAssessment.IntentSources(state).Where(s => s.Structural).SelectMany(s =>
            PlanningReferences.Register(state, s.Id, s.Kind, s.Text).Select(r => PlanningBaselineProjection.Evidence(state, r))).ToList();
        runtimeEvidence.AddRange(PlanningDeclaredPolicyProjection.Clauses(state).Select(c => PlanningOperations.PolicyEvidence(state, c.Reference)));
        foreach (var scope in scopes)
        {
            var answer = values[DecisionId(scope.Reference, scope.Source.Text)]!;
            if (scope.Source.Baseline is not null)
                runtimeEvidence.Add(PlanningBaselineProjection.Evidence(state, scope.Reference));
            else if (scope.Source.Authority == PlanningSourceAuthority.ConstraintsOnly)
                runtimeEvidence.Add(PlanningOperations.PolicyEvidence(state, scope.Reference));
            else runtimeEvidence.AddRange(PlanningOperations.ParseRuntime(state, scope.Reference, scope.Boundaries.Select, answer["runtime"]!.AsArray()));
            foreach (var item in answer["obligations"]!.AsArray())
            {
                var reference = scope.Boundaries.Select(item!["start"]!.ToString(), item["end"]!.ToString());
                if (!state.References.Contains(reference)) state.References.Add(reference);
                var kind = item["kind"]!.ToString();
                if (kind == "information") continue;
                var owner = kind is "declaration_candidate" or "declaration_constraint" or "business_choice" or "explicit_value" or "omission_default" or "business_preference" ? "business_decision" : kind is "runtime_condition" or "runtime_fallback" or "local_processing" or "workflow_policy" or "confirmation_required" or "confirmation_forbidden" or "iteration" or "workflow_boundary" ? "workflow" : "capability_contract";
                var id = "ob_" + PlanningGraphCompiler.Fingerprint(reference.SourceId + ":" + reference.Start + ":" + PlanningReferences.Resolve(state, reference.Id, Sources(state)) + ":" + kind)[..16];
                var obligation = new PlanningObligation(id, [reference.Id], owner, kind, item["required"]!.GetValue<bool>());
                var grounding = PlanningSourceGroundingRules.Create(state, obligation, item["baseline"]?.GetValue<string>());
                obligation = obligation with { Grounding = grounding,
                    Owner = grounding.Authority == PlanningSourceAuthority.ConstraintsOnly ? "workflow" : owner,
                    Disposition = "preliminary" };
                PlanningSourceGroundingRules.Validate(state, obligation);
                if (obligations.Any(o => o.Id == id))
                    throw new WorkflowRuntimeException("INTENT_DUPLICATE_DECISION", "The same source span and semantic role were assigned twice: " + id);
                obligations.Add(obligation);
            }
        }
        state.Obligations = await ApplyRevisionAsync(state, runtime, obligations, ct);
        state.RuntimeEvidence = runtimeEvidence;
        state.RuntimeEvidenceFingerprint = PlanningOperations.RuntimeFingerprint(state);
        PlanningOperations.RequireRuntimeEvidence(state);
        state.OperationAdmissionFingerprint = null;
        await runtime.CheckpointAsync(state, ct);
    }

    internal static async Task<List<PlanningObligation>> ApplyRevisionAsync(PlanningSnapshot state, IPlanningRuntime runtime,
        List<PlanningObligation> obligations, CancellationToken ct)
    {
        if (state.BehaviorRevision is not { } revision) return obligations;
        var (decisions, owners) = RevisionDomain(state, obligations);
        var choices = await PlanningDecisionPages.ResolveAsync(state, runtime, "behavior_revision_obligations", "$plan", decisions, ct);
        var superseded = choices.Where(p => p.Value!.ToString() != "retained").Select(p => owners[p.Key]).ToHashSet(StringComparer.Ordinal);
        return obligations.Where(o => !superseded.Contains(o.Id)).ToList();
    }

    private static (List<PlanningDecisionPages.Decision> Decisions, Dictionary<string, string> Owners) RevisionDomain(
        PlanningSnapshot state, List<PlanningObligation> obligations)
    {
        var revision = state.BehaviorRevision!;
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
        return (decisions, owners);
    }

    internal static List<PlanningObligation> ReadRevisionProjection(PlanningSnapshot state, List<PlanningObligation> obligations)
    {
        if (state.BehaviorRevision is null) return obligations;
        var (decisions, owners) = RevisionDomain(state, obligations);
        var choices = PlanningDecisionPages.ReadCompleted(state, "behavior_revision_obligations", "$plan", decisions);
        var removed = choices.Where(p => p.Value!.ToString() != "retained").Select(p => owners[p.Key]).ToHashSet(StringComparer.Ordinal);
        return obligations.Where(o => !removed.Contains(o.Id)).ToList();
    }

    internal static async Task RelateAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        PlanningSourceGroundingRules.ValidateAll(state);
        PlanningOperations.RequireCurrent(state);
        PlanningDeclarations.RequireCurrent(state);
        var operations = state.Obligations.Where(o => IsOperation(o)).ToArray();
        var producers = state.Obligations.Where(o => IsOperation(o) || o.Kind == "implementation_policy").Concat(
            state.Declarations.Where(d => d.Direction == "input").Select(d => new PlanningObligation(d.Id, d.ClauseReferences, "business_decision", "business_input", d.Required))).ToArray();
        var grounded = PlanningOperations.EffectRelations(operations).ToArray();
        var pairs = operations.SelectMany(consumer => producers.Where(p => p.Id != consumer.Id && p.Kind != "business_input" &&
                (p.Kind != "implementation_policy" || PlanningBaselineProjection.OwnsOperation(state, p.Grounding!.ClauseReference, consumer)))
            .Select(producer => (Producer: producer, Consumer: consumer))).ToArray();
        var choices = pairs.Select(pair => new PlanningDecisionPages.Decision("relation_" + pair.Producer.Id + "_" + pair.Consumer.Id,
            PlanningHoleRequests.Enum(pair.Producer.Kind == "implementation_policy" ? ["none", "policy"]
                : pair.Producer.Kind == "resource_lifecycle" ? ["none", "owned_resource", "failure"]
                : ["none", "decision", "decision_no_effect", "failure"]),
            new JsonObject { ["producer"] = pair.Producer.Kind == "business_input" ? PlanningDeclarations.Port(state, state.Declarations.Single(d => d.Id == pair.Producer.Id)).Description : Text(state, pair.Producer), ["consumer"] = Text(state, pair.Consumer),
                ["task"] = "Select the explicit producer-to-consumer obligation. A decision controls an effect; decision_no_effect includes an explicit no-action outcome. Failure means handling this producer's failure. owned_resource requires the consumer to target the original resource materialized by this producer. policy applies a declared implementation restriction to this operation. Do not add incidental implementation dependencies." },
            PlanningGraphCompiler.Fingerprint(string.Join("|", pair.Producer.EvidenceReferences.Concat(pair.Consumer.EvidenceReferences))))).ToArray();
        var values = await PlanningDecisionPages.ResolveAsync(state, runtime, "intent_relations", "$plan", choices, ct);
        state.ObligationRelations = pairs.Select(pair => new PlanningObligationRelation(pair.Producer.Id, pair.Consumer.Id,
                values["relation_" + pair.Producer.Id + "_" + pair.Consumer.Id]!.ToString())).Where(r => r.Role != "none").Concat(grounded).Distinct().ToList();
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

    internal static bool IsOperation(PlanningObligation obligation) => PlanningSourceGroundingRules.OperationKinds.Contains(obligation.Kind, StringComparer.Ordinal) &&
        obligation.Grounding?.Role is PlanningSourceSemanticRole.RequestedAction or PlanningSourceSemanticRole.ExistingAction && obligation.Disposition == "admitted" &&
        obligation.OperationAdmission is { Version: 10, Dependencies.Version: 1 } proof && proof.CanonicalId == obligation.Id;

    private static JsonObject AnnotationSchema(PlanningBaselineOwnership owner, JsonObject boundaries)
    {
        var schema = boundaries.DeepClone().AsObject();
        schema["properties"]!["kind"] = PlanningHoleRequests.Enum(PlanningBaselineProjection.AnnotationKinds(owner));
        schema["properties"]!["required"] = PlanningHoleRequests.Type("boolean");
        schema["required"] = new JsonArray("start", "end", "kind", "required");
        return schema;
    }

    internal static JsonObject InterpretationSchema(PlanningSnapshot state, PlanningSourceAuthority authority, JsonObject boundaries)
    {
        JsonObject Item(string[] kinds)
        {
            var item = boundaries.DeepClone().AsObject();
            item["properties"]!["kind"] = PlanningHoleRequests.Enum(kinds);
            item["properties"]!["required"] = PlanningHoleRequests.Type("boolean");
            item["required"] = new JsonArray("start", "end", "kind", "required");
            return item;
        }
        var allowed = PlanningSourceGroundingRules.Kinds(authority).Except(PlanningSourceGroundingRules.OperationKinds, StringComparer.Ordinal).ToArray();
        if (authority != PlanningSourceAuthority.ExistingBehavior) return Item(allowed);
        // Structural operations never enter this response domain. Actual annotations
        // use the narrower owner-specific schema above.
        return Item(allowed.Except(PlanningSourceGroundingRules.OperationKinds, StringComparer.Ordinal).ToArray());
    }
}
