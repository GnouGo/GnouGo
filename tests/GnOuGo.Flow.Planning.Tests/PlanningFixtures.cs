using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

internal static class PlanningFixtures
{
    internal static PlanningRuntimeEvidence Runtime(PlanningSnapshot state, PlanningReference reference, string kind = "local_processing",
        string occurrence = "distinct", string? subject = null, string? baseline = null, string? resourceAction = null, bool required = true)
    {
        var clause = PlanningChoiceEvidence.Parent(state, reference.Id);
        var evidence = PlanningOperations.SealRuntime(state, new("", reference.Id, clause.Id,
            kind == "local_processing" ? "local_behavior" : "runtime_action", reference.Id, subject ?? clause.Id, reference.Id,
            kind, occurrence, baseline, resourceAction, required, ""));
        state.RuntimeEvidence.RemoveAll(e => e.Id == evidence.Id); state.RuntimeEvidence.Add(evidence);
        EmptyRuntime(state);
        return evidence;
    }
    internal static void EmptyRuntime(PlanningSnapshot state)
    {
        state.RuntimeEvidence.RemoveAll(e => !PlanningChoiceEvidence.Current(state, e.SourceReference));
        foreach (var source in PlanningIntentAssessment.IntentSources(state))
        foreach (var reference in PlanningReferences.Register(state, source.Id, source.Kind, source.Text).ToArray().Where(r => !string.IsNullOrWhiteSpace(source.Text.Substring(r.Start, r.Length))))
        {
            if (state.RuntimeEvidence.Any(e => state.References.Single(r => r.Id == e.SourceReference) is var covered && covered.SourceId == reference.SourceId && covered.Start <= reference.Start && covered.Start + covered.Length >= reference.Start + reference.Length)) continue;
            if (source.Authority == PlanningSourceAuthority.ConstraintsOnly)
                state.RuntimeEvidence.Add(PlanningOperations.PolicyEvidence(state, reference));
            else state.RuntimeEvidence.Add(PlanningOperations.SealRuntime(state, new("", reference.Id, PlanningChoiceEvidence.Parent(state, reference.Id).Id,
                "contract", null, null, null, null, null, null, null, false, "")));
        }
        state.RuntimeEvidenceFingerprint = PlanningOperations.RuntimeFingerprint(state);
    }

    internal static void RefreshAdmission(PlanningSnapshot state)
    {
        EmptyRuntime(state);
        var operations = state.Obligations.Where(o => o.OperationAdmission is not null).Select(o =>
            PlanningOperations.Prove(state, o, o.OperationAdmission! with { EvidenceFingerprint = PlanningOperations.EvidenceFingerprint(state) })).ToList();
        PlanningOperations.Commit(state, operations);
    }

    // Explicit synthetic admission for downstream fixtures. Test-authored operation
    // hints are the answers; production always uses bounded decision pages.
    internal static void AdmitHints(PlanningSnapshot state)
    {
        var hints = state.Obligations.Where(o => o.OperationAdmission is null && PlanningSourceGroundingRules.OperationKinds.Contains(o.Kind)).ToArray();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var admitted = state.Obligations.Where(o => o.OperationAdmission is not null).ToList();
        foreach (var hint in hints)
            Runtime(state, state.References.Single(r => r.Id == hint.EvidenceReferences[0]), hint.Kind,
                baseline: hint.Grounding?.BaselineReference, resourceAction: hint.Kind is "cleanup" or "resource_lifecycle" ? "delete" : null, required: hint.Required);
        EmptyRuntime(state);
        foreach (var hint in hints) state.Obligations.Remove(hint);
        foreach (var hint in hints)
        {
            var clause = PlanningChoiceEvidence.Parent(state, hint.EvidenceReferences[0]);
            var evidence = state.RuntimeEvidence.Single(e => e.ActionReference == hint.EvidenceReferences[0] && e.Kind == hint.Kind);
            var assignment = new PlanningOperationAssignment("operation_" + evidence.Id, clause.Id, hint.EvidenceReferences[0], hint.Kind,
                hint.Required, null, hint.Grounding?.BaselineReference) { RuntimeEvidenceId = evidence.Id };
            var operation = PlanningOperations.Create(state, assignment);
            if (!admitted.Any(o => o.Id == operation.Id)) admitted.Add(operation);
            map.Add(hint.Id, operation.Id);
        }
        // Remap fixture relationships, capability boundaries and assertions' retained
        // objects in place. Evidence and proof objects never participate in remapping.
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Remap(object? value)
        {
            if (value is null or string or PlanningReference or PlanningSourceGrounding or PlanningOperationAdmission or PlanningRuntimeEvidence || !seen.Add(value)) return;
            if (value is IList<string> names) { for (var i = 0; i < names.Count; i++) if (map.TryGetValue(names[i], out var id)) names[i] = id; return; }
            if (value is System.Collections.IEnumerable list) { foreach (var item in list) Remap(item); return; }
            if (value.GetType().Namespace != typeof(PlanningSnapshot).Namespace) return;
            foreach (var property in value.GetType().GetProperties())
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                var current = property.GetValue(value);
                if (current is string text && property.CanWrite && map.TryGetValue(text, out var replacement)) property.SetValue(value, replacement);
                else Remap(current);
            }
        }
        if (map.Count != 0) Remap(state);
        admitted = admitted.Select(o => PlanningOperations.Prove(state, o, o.OperationAdmission! with { EvidenceFingerprint = PlanningOperations.EvidenceFingerprint(state) })).ToList();
        PlanningOperations.Commit(state, admitted);
    }
    internal static PlanningSnapshot PreparedRequest(PlanningRequest request, PlanningPreparationCheckpoint? checkpoint = null)
    {
        var state = new PlanningSnapshot { Request = request, PreparationCheckpoint = checkpoint };
        var scope = PlanningOperations.SourceScopes(state)[0];
        Runtime(state, scope.Clause, "external_read");
        PlanningDeclarations.ResolveAsync(state, new TypedPlannerTests.FakeRuntime(), default).GetAwaiter().GetResult();
        PlanningOperations.ResolveAsync(state, new TypedPlannerTests.FakeRuntime(), default).GetAwaiter().GetResult(); return state;
    }
    internal static string OperationId(PlanningSnapshot state, string evidenceId) => state.Obligations.FirstOrDefault(o =>
        o.OperationAdmission is { } proof && proof.AnchorReference.EndsWith("_" + evidenceId, StringComparison.Ordinal))?.Id ?? evidenceId;

    internal static System.Text.Json.Nodes.JsonObject Workflow(PlanningWorkflow workflow) => PlanningModelValues.Compact(System.Text.Json.JsonSerializer.SerializeToNode(workflow, PlanningJsonContext.Default.PlanningWorkflow))!.AsObject();
    // Fixture setup explicitly models prior human acceptance; production never infers approval from a graph.
    internal static void Accept(PlanningSnapshot state)
    {
        AdmitHints(state);
        var graph = state.Graph!;
        state.BehaviorPlan = new()
        {
            Summary = graph.Summary,
            Entrypoint = graph.Entrypoint,
            Workflows = graph.Workflows.Select(w => new PlanningBehaviorWorkflow
            {
                Key = w.Key,
                Purpose = w.Purpose,
                OperationIds = w.OperationIds.ToList(),
                Inputs = w.Inputs.Select(p => new PlanningBehaviorPort(p.Name, p.Name, p.Required)).ToList(),
                Outputs = w.Outputs.Select(p => new PlanningBehaviorPort(p.Name, p.Name, true)).ToList(),
                Steps = w.Steps.Select(Node).ToList(),
                Finally = w.Finally.Select(Node).ToList()
            }).ToList()
        };
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        PlanningDataflowResolver.Resolve(state);
        foreach (var progress in state.Construction.Workflows) progress.Status = "validated";
        return;

        PlanningBehaviorNode Node(PlanningNode node) => new()
        {
            Key = node.Key,
            Purpose = node.Purpose,
            CapabilityId = node.CapabilityId,
            OperationIds = node.OperationIds.ToList(),
            InputDependencies = [],
            Kind = node.Type switch { "loop.sequential" => "loop", "switch" => "decision", "workflow.call" => "workflow", "human.input" => "confirmation", "sequence" or "parallel" => node.Type, _ => "operation" },
            WorkflowKey = node.Type == "workflow.call" ? node.Input.Members.Single(m => m.Name == "ref").Value.Source : null,
            Steps = node.Type == "parallel" ? node.Branches.Select(b => Node(b.Steps.Single())).ToList() : node.Steps.Select(Node).ToList(),
            Outcomes = node.Type == "switch" ? node.Cases.Select(c => new PlanningBehaviorOutcome(c.Value!, c.Value!, false, c.Steps.Select(Node).ToList()))
                .Append(new PlanningBehaviorOutcome("default", "Default", true, node.Default.Select(Node).ToList())).ToList() : []
        };
    }
}
