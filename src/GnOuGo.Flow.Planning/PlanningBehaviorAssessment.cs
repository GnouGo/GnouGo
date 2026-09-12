using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal sealed class PlanningBehaviorAssessment(TimeProvider time)
{
    internal async Task AssessAsync(PlanningSnapshot state, IPlanningRuntime runtime, CancellationToken ct)
    {
        state.CurrentPhase = PlanningPhase.Behavior;
        PlanningContext.InvalidateArtifact(state);
        await PlanningClarifications.ResolveAsync(state, runtime, allowQuestions: false, ct);
        if (PlanningStatus.IsTerminal(state.Status)) return;
        var assessment = state.BehaviorAssessment;
        var schema = PlanningSchemas.Behavior(state.Preparation);
        if (state.BehaviorRevision is { Located: false } && assessment.Candidate is not null)
        { await PlanningBehaviorRevision.LocateAsync(state, runtime, ct); return; }
        if (state.BehaviorRevision is { Located: true } && assessment.Candidate is not null)
        {
            var revised = PlanningBehaviorRevision.ApplyRemovals(state, assessment.Candidate, schema);
            if (!JsonNode.DeepEquals(revised, assessment.Candidate))
            { assessment.Candidate = revised; await runtime.CheckpointAsync(state, ct); }
        }
        if (assessment.Candidate is null && state.BehaviorPlan is not null)
        {
            foreach (var workflow in state.BehaviorPlan.Workflows.Where(w => w.Inputs.Count == 0))
                foreach (var node in PlanningBehaviorPlans.Enumerate(workflow.Steps.Concat(workflow.Finally))) node.InputDependencies ??= [];
            assessment.Candidate = JsonSerializer.SerializeToNode(state.BehaviorPlan, PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
        }
        if (assessment.Candidate is null)
        {
            var assembled = await PlanningBehaviorDecisions.BuildAsync(state, runtime, ct);
            assessment.Candidate = JsonSerializer.SerializeToNode(assembled, PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
            Evaluate(state, assessment.Candidate, schema, out var initial, out var stage);
            assessment.Diagnostics = initial; assessment.Stage = stage;
            PlanningConvergence.Failure(state, "$plan", stage == 0 ? PlanningGates.Response : PlanningGates.Behavior, PlanningGraphCompiler.Fingerprint(assessment.Candidate.ToJsonString()), initial);
            await runtime.CheckpointAsync(state, ct);
            await PlanningClarifications.ResolveAsync(state, runtime, allowQuestions: true, ct);
            if (PlanningStatus.IsTerminal(state.Status)) return;
            if (initial.Count == 0) ReadyForBehaviorReview(state, state.BehaviorPlan!);
            else state.Diagnostics = initial;
            return;
        }
        Evaluate(state, assessment.Candidate, schema, out var findings, out var beforeStage);
        await PlanningClarifications.ResolveAsync(state, runtime, allowQuestions: true, ct);
        if (PlanningStatus.IsTerminal(state.Status)) return;
        assessment.Diagnostics = findings; assessment.Stage = beforeStage;
        if (findings.Count == 0) { ReadyForBehaviorReview(state, state.BehaviorPlan!); return; }
        var targets = PlanningBehaviorPatches.Scope(assessment.Candidate, schema, findings);
        if (beforeStage > 0) PlanningBehaviorPatches.RestrictCapabilities(assessment.Candidate, targets, state.Preparation!);
        if (targets.Count == 0)
        {
            state.Diagnostics = findings;
            PlanningContext.Stop(state, "BEHAVIOR_SCOPE_UNRESOLVED", "The behavior finding has no safe, located edit. Revise the intent to clarify the missing obligation.");
            return;
        }
        var owner = PlanningBehaviorPatches.Owner(assessment.Candidate, targets);
        var gate = beforeStage == 0 ? PlanningGates.Response : PlanningGates.Behavior;
        PlanningConvergence.Failure(state, owner, gate, PlanningGraphCompiler.Fingerprint(assessment.Candidate.ToJsonString()), findings);
        var patchSchema = PlanningExactPatches.Schema(targets, schema);
        var fields = new JsonObject(targets.Select(t =>
        {
            var field = new JsonObject { ["path"] = t.Path };
            if (t.Destination is not null || t.Remove || t.Add) field["operation"] = t.Destination is not null ? "move" : t.Remove ? "remove" : "insert";
            if (t.Destination is { } destination) field["destination"] = destination;
            if (!t.Add) field["current"] = PlanningFieldPaths.ReadOptional(assessment.Candidate, t.Path)?.DeepClone();
            return new KeyValuePair<string, JsonNode?>(t.Id, field);
        }));
        // Response-schema findings already express their constraints in the exact
        // field schema. Repeating every allowed enum in prose can exceed the input
        // ceiling during a governing-contract revision with many renamed references.
        var diagnosticContext = new JsonArray(findings.GroupBy(d => (d.Code, d.Rule, Message: beforeStage == 0 ? null : d.Message)).Select(group =>
        {
            var relevant = targets.Where(t => group.Any(d => t.Path == d.Location || t.Path.StartsWith(d.Location + "/", StringComparison.Ordinal))).ToArray();
            var diagnostic = new JsonObject { ["code"] = group.Key.Code, ["rule"] = group.Key.Rule,
                ["targets"] = new JsonArray(relevant.Select(t => (JsonNode?)JsonValue.Create(t.Id)).ToArray()) };
            if (group.Key.Message is { } message) diagnostic["message"] = message;
            if (relevant.Length == 0) diagnostic["locations"] = new JsonArray(group.Select(d => (JsonNode?)JsonValue.Create(d.Location)).ToArray());
            return (JsonNode)diagnostic;
        }).ToArray());
        var referencesOnly = targets.All(t => t.Path.EndsWith("/capabilityId", StringComparison.Ordinal) || t.Path.Split('/')[^2] == "operationIds");
        var decisionEvidence = PlanningGraphCompiler.Fingerprint(PlanningContext.Contracts(state) + ":" + state.BehaviorRevision?.Text);
        var response = await PlanningBehaviorPatches.ResolveAsync(state, runtime, owner, gate, targets, schema,
            new JsonObject { ["fields"] = fields, ["diagnostics"] = diagnosticContext,
                ["intent"] = PlanningContext.Intent(state), ["capabilities"] = CapabilityValues(state.Preparation!, includeArguments: !referencesOnly) }, decisionEvidence, ct);
        var previous = assessment.Candidate;
        JsonObject candidate;
        try { candidate = PlanningExactPatches.Apply(previous, response, targets, patchSchema); }
        catch (InvalidOperationException error)
        {
            PlanningConvergence.Failure(state, owner, PlanningGates.Response, decisionEvidence, [new("PATCH_INVALID", "$", error.Message)]);
            PlanningContext.Stop(state, "PATCH_INVALID", error.Message); return;
        }
        var hash = PlanningGraphCompiler.Fingerprint(candidate.ToJsonString());
        if (hash == PlanningGraphCompiler.Fingerprint(previous.ToJsonString()) || assessment.RejectedCandidates.Contains(hash, StringComparer.Ordinal))
        { PlanningContext.Stop(state, "REPAIR_REPEATED", "The behavior repair repeated an existing candidate."); return; }
        var retainedPlan = state.BehaviorPlan;
        Evaluate(state, candidate, schema, out var remaining, out var nextStage);
        PlanningConvergence.Failure(state, owner, nextStage == 0 ? PlanningGates.Response : PlanningGates.Behavior, hash, remaining);
        var oldIds = findings.Select(d => PlanningFieldPaths.DiagnosticId(d, previous)).ToHashSet(StringComparer.Ordinal);
        var newIds = remaining.Select(d => PlanningFieldPaths.DiagnosticId(d, candidate)).ToHashSet(StringComparer.Ordinal);
        var accepted = remaining.Count == 0 || nextStage > beforeStage || nextStage == beforeStage && newIds.IsProperSubsetOf(oldIds);
        state.Attempts.Add(new(hash, PlanningPhase.Behavior, nextStage, accepted, remaining));
        state.Events.Add(new(accepted ? "behavior_patch_accepted" : "behavior_patch_rejected", PlanningPhase.Behavior, time.GetUtcNow(), targets.Count));
        if (!accepted)
        { state.BehaviorPlan = retainedPlan; assessment.RejectedCandidates.Add(hash); state.Diagnostics = findings; PlanningContext.Stop(state, "REPAIR_REGRESSION", "The behavior correction did not make monotonic progress."); return; }
        assessment.Candidate = candidate; assessment.Diagnostics = remaining; assessment.Stage = nextStage;
        state.Diagnostics = remaining;
        if (remaining.Count == 0) ReadyForBehaviorReview(state, state.BehaviorPlan!);
    }

    private static void Evaluate(PlanningSnapshot state, JsonObject candidate, JsonObject schema, out List<PlanningDiagnostic> diagnostics, out int stage)
    {
        diagnostics = PlanningContractValidation.ValidateInstanceFindings(candidate, schema)
            .Select(f => new PlanningDiagnostic("BEHAVIOR_SCHEMA_INVALID", f.InstancePointer, f.Message, Rule: f.Rule)).ToList();
        stage = 0;
        if (diagnostics.Count != 0) return;
        var plan = JsonSerializer.Deserialize(candidate, PlanningJsonContext.Default.PlanningBehaviorPlan)!;
        var originalLocations = Locations().ToDictionary(p => p.Node, p => p.Path);
        PlanningBehaviorPlans.CompleteOwnership(plan, state.Preparation!);
        PlanningBehaviorPlans.CompleteReviewDefaults(plan);
        PlanningBehaviorPlans.CompleteLockedOutcomes(plan, state.Preparation!);
        var completedLocations = Locations().OrderByDescending(p => p.Path.Length).ToArray();
        diagnostics = PlanningBehaviorPlans.Validate(plan, state.Preparation!).Select(Rebase)
            .Concat(PlanningBehaviorRevision.Findings(state, candidate)).Concat(PlanningBusinessAnswers.ValidateBehavior(state, plan)).ToList(); stage = 1;
        // The raw candidate remains staged; only a validated plan becomes reviewable.
        if (diagnostics.Count == 0) state.BehaviorPlan = plan;

        IEnumerable<(PlanningBehaviorNode Node, string Path)> Locations() => plan.Workflows.SelectMany((workflow, index) =>
            PlanningBehaviorPlans.Located(workflow.Steps, "/workflows/" + index + "/steps")
                .Concat(PlanningBehaviorPlans.Located(workflow.Finally, "/workflows/" + index + "/finally")));

        PlanningDiagnostic Rebase(PlanningDiagnostic diagnostic)
        {
            // Completion can insert a compiler-owned decision producer. Validation
            // sees that review projection; exact repairs still target the staged
            // candidate. Retain node identity instead of transferring array indexes.
            foreach (var (node, path) in completedLocations)
                if (diagnostic.Location == path || diagnostic.Location.StartsWith(path + "/", StringComparison.Ordinal))
                    return originalLocations.TryGetValue(node, out var original)
                        ? diagnostic with { Location = original + diagnostic.Location[path.Length..] }
                        : diagnostic with { Location = "/", Rule = diagnostic.Rule + "|derived:" + node.Key };
            if (diagnostic.Rule?.StartsWith("insert_behavior_node:", StringComparison.Ordinal) == true)
            {
                var collection = diagnostic.Location[..diagnostic.Location.LastIndexOf('/')];
                if (PlanningFieldPaths.ReadOptional(candidate, collection) is JsonArray values)
                    return diagnostic with { Location = collection + "/" + values.Count };
            }
            return diagnostic;
        }
    }

    private static void ReadyForBehaviorReview(PlanningSnapshot state, PlanningBehaviorPlan plan)
    {
        state.BehaviorPlan = plan; state.ApprovedBehaviorHash = null;
        state.Graph = null; state.Construction.Dataflow = null; state.Construction.Workflows.Clear();
        state.Diagnostics.Clear(); state.ArtifactHash = PlanningBehaviorPlans.Fingerprint(plan);
        state.Status = PlanningStatus.BehaviorReview;
        state.ReviewMarkdown = plan.Summary;
        state.Attempts.Add(new(state.ArtifactHash, PlanningPhase.Behavior, 1, true, []));
    }

    internal static string BehaviorCapabilities(PlanningPreparation preparation, bool includeArguments = true)
        => PlanningPromptContext.Instructions + PlanningPromptContext.Json(PlanningPromptContext.Share(CapabilityValues(preparation, includeArguments)));

    internal static string BehaviorContext(PlanningPreparation preparation)
    {
        var locked = preparation.LockedContract.DeepClone().AsObject(); locked.Remove("capabilities");
        if (locked["constraints"] is JsonArray constraints)
        {
            var required = constraints.OfType<JsonObject>().Where(c => c["required"]?.GetValue<bool>() == true &&
                c["denied_alternatives"] is JsonArray { Count: 0 } && c.All(p => p.Key is "id" or "description" or "required" or "denied_alternatives")).ToArray();
            if (required.Length > 0)
            {
                locked["requiredConstraints"] = new JsonObject(required.Select(c => new KeyValuePair<string, JsonNode?>(c["id"]!.ToString(), c["description"]?.DeepClone())));
                foreach (var policy in required) constraints.Remove(policy);
                if (constraints.Count == 0) locked.Remove("constraints");
            }
        }
        return PlanningPromptContext.Instructions + PlanningPromptContext.Json(PlanningPromptContext.Share(new JsonObject
        {
            ["lockedContract"] = locked, ["capabilities"] = CapabilityValues(preparation)
        }));
    }

    private static JsonArray CapabilityValues(PlanningPreparation preparation, bool includeArguments = true)
    {
        var values = JsonSerializer.SerializeToNode(preparation, PlanningJsonContext.Default.PlanningPreparation)!["capabilities"]!.DeepClone().AsArray();
        foreach (var capability in values.OfType<JsonObject>())
        {
            var input = capability["inputSchema"] as JsonObject;
            var required = (input?["required"] as JsonArray ?? []).Select(p => p!.ToString()).ToHashSet(StringComparer.Ordinal);
            var fixedPaths = (capability["requestBindings"] as JsonArray ?? []).Select(b => b!["path"]!.ToString()).ToHashSet(StringComparer.Ordinal);
            capability["declaredArguments"] = !includeArguments || input?["properties"] is not JsonObject fields ? null : new JsonObject(fields
                .Where(p => !fixedPaths.Contains("/" + PlanningFieldPaths.Escape(p.Key))).Select(p =>
                {
                    var argument = new JsonObject();
                    foreach (var name in new[] { "type", "description" }) if ((p.Value as JsonObject)?[name] is { } value) argument[name] = value.DeepClone();
                    return new KeyValuePair<string, JsonNode?>(p.Key, argument);
                }));
            capability["requiredArguments"] = !includeArguments ? null : new JsonArray(required
                .Where(name => !fixedPaths.Contains("/" + PlanningFieldPaths.Escape(name)))
                .Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
            foreach (var field in new[] { "inputSchema", "outputSchema", "declarationFingerprint", "fixedInput", "catalogId" }) capability.Remove(field);
            // These optional metadata fields have no value. Keep argument names,
            // requiredness, descriptions and locked bindings, without serializing
            // absent activation/transport values for every capability repeatedly.
            foreach (var field in capability.Where(p => p.Value is null or JsonArray { Count: 0 } or JsonObject { Count: 0 }).Select(p => p.Key).ToArray()) capability.Remove(field);
        }
        return values;
    }

}
