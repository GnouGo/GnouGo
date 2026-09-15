using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningOperations
{
    internal static string RuntimeFingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint("runtime-evidence-v3:" +
        new JsonArray(PlanningIntentAssessment.IntentSources(state).Select(s => (JsonNode)new JsonArray(s.Id, s.Authority.ToString(), s.Text)).ToArray()).ToJsonString() + ":" +
        JsonSerializer.Serialize(state.RuntimeEvidence.OrderBy(e => e.Id, StringComparer.Ordinal).ToList(), PlanningJsonContext.Default.ListPlanningRuntimeEvidence));

    internal static JsonObject RuntimeSchema(PlanningSnapshot state, PlanningSourceAuthority authority, JsonObject boundaries)
    {
        if (authority == PlanningSourceAuthority.ConstraintsOnly)
            throw new InvalidOperationException("Policy execution scope is engine owned and has no model response domain.");
        var variants = new JsonArray(PlanningHoleRequests.Object(("role", PlanningHoleRequests.Enum(["planning_directive", "contract", "policy", "unresolved"]))));
        JsonObject Action(string role, string[] kinds, string? baseline = null)
        {
            var fields = new List<(string, JsonObject)>
            {
                ("role", PlanningHoleRequests.Enum([role])), ("kind", PlanningHoleRequests.Enum(kinds)),
                ("action", boundaries.DeepClone().AsObject()), ("execution", PlanningHoleRequests.Enum(["generated_workflow"])),
                ("evidence", PlanningHoleRequests.Enum(["action", "governing"])),
                ("required", PlanningHoleRequests.Type("boolean")),
                ("baseline", baseline is null ? PlanningHoleRequests.Type("null") : PlanningHoleRequests.Enum([baseline]))
            };
            if (kinds[0] is "resource_lifecycle" or "cleanup")
            {
                fields.Add(("resource", boundaries.DeepClone().AsObject()));
                fields.Add(("ownership", PlanningHoleRequests.Enum(["workflow_runtime_resource"])));
                fields.Add(("resourceAction", PlanningHoleRequests.Enum(kinds[0] == "cleanup" ? ["release", "delete"] : ["create", "acquire", "release", "delete"])));
            }
            return PlanningHoleRequests.Object(fields.ToArray());
        }
        if (authority == PlanningSourceAuthority.RequestedBehavior)
        {
            variants.Add((JsonNode)Action("local_behavior", ["local_processing"]));
            variants.Add((JsonNode)Action("runtime_action", ["external_read", "external_write", "external_execute", "human_interaction"]));
            variants.Add((JsonNode)Action("runtime_action", ["resource_lifecycle"]));
            variants.Add((JsonNode)Action("runtime_action", ["cleanup"]));
        }
        if (authority == PlanningSourceAuthority.ExistingBehavior)
            foreach (var (id, node) in PlanningSourceGroundingRules.BaselineNodes(state))
            foreach (var kind in BaselineKinds(node.Node))
                variants.Add((JsonNode)Action(kind == "local_processing" ? "local_behavior" : "runtime_action", [kind], id));
        return new() { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 4, ["items"] = new JsonObject { ["anyOf"] = variants } };
    }

    internal static List<PlanningRuntimeEvidence> ParseRuntime(PlanningSnapshot state, PlanningReference source,
        Func<string, string, PlanningReference> select, JsonArray values)
    {
        var result = new List<PlanningRuntimeEvidence>();
        string Select(JsonNode value)
        {
            var reference = select(value["start"]!.ToString(), value["end"]!.ToString());
            if (!state.References.Contains(reference)) state.References.Add(reference);
            return reference.Id;
        }
        foreach (var value in values)
        {
            var role = value!["role"]!.ToString();
            var actionReference = value["action"] is { } action ? Select(action) : null;
            var evidence = new PlanningRuntimeEvidence("", source.Id, PlanningChoiceEvidence.Parent(state, source.Id).Id, role,
                actionReference, value["resource"] is { } resource ? Select(resource) : null,
                value["execution"]?.ToString() == "generated_workflow" ? actionReference : null, value["kind"]?.ToString(), value["evidence"]?.ToString(),
                value["baseline"]?.ToString(), value["resourceAction"]?.ToString(), value["required"]?.GetValue<bool>() ?? false, "") { ResourceOwnership = value["ownership"]?.ToString() };
            result.Add(SealRuntime(state, evidence));
        }
        if (result.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw Failure(source.Id, "Runtime evidence contains a repeated assignment.");
        return result;
    }

    internal static PlanningRuntimeEvidence PolicyEvidence(PlanningSnapshot state, PlanningReference source)
        => SealRuntime(state, new("", source.Id, PlanningChoiceEvidence.Parent(state, source.Id).Id, "policy",
            null, null, null, null, null, null, null, false, "")
            { Origin = PlanningRuntimeEvidenceOrigin.EngineSourceAuthority });

    internal static PlanningRuntimeEvidence SealRuntime(PlanningSnapshot state, PlanningRuntimeEvidence evidence)
    {
        var identity = PlanningGraphCompiler.Fingerprint(new JsonArray(evidence.SourceReference, evidence.Role,
            evidence.ActionReference, evidence.ResourceReference, evidence.ExecutionReference, evidence.Kind, evidence.EvidenceRole).ToJsonString());
        var value = evidence with { Id = "runtime_" + identity[..24], ProofFingerprint = "",
            Origin = evidence.Origin == PlanningRuntimeEvidenceOrigin.EngineSourceAuthority
                ? PlanningRuntimeEvidenceOrigin.EngineSourceAuthority : PlanningRuntimeEvidenceOrigin.SourceInterpretation,
            ExecutionScope = evidence.Role switch
            {
                "planning_directive" => PlanningRuntimeExecutionScope.PlanningArtifact,
                "contract" => PlanningRuntimeExecutionScope.PublicContract,
                "policy" => PlanningRuntimeExecutionScope.Policy,
                "local_behavior" or "runtime_action" => PlanningRuntimeExecutionScope.GeneratedWorkflow,
                _ => PlanningRuntimeExecutionScope.Unknown
            } };
        return value with { ProofFingerprint = RuntimeProof(state, value) };
    }

    private static string RuntimeProof(PlanningSnapshot state, PlanningRuntimeEvidence value) => PlanningGraphCompiler.Fingerprint("runtime-proof-v3:" +
        state.Request.TenantId + ":" + state.Request.SessionId + ":" +
        JsonSerializer.Serialize(value with { ProofFingerprint = "" }, PlanningJsonContext.Default.PlanningRuntimeEvidence) + ":" +
        string.Join('|', new[] { value.SourceReference, value.ClauseReference, value.ActionReference, value.ResourceReference, value.ExecutionReference }
            .OfType<string>().Select(id => id + ":" + PlanningChoiceEvidence.Text(state, id))));

    internal static void RequireRuntimeEvidence(PlanningSnapshot state)
    {
        if (state.RuntimeEvidenceFingerprint is null || state.RuntimeEvidenceFingerprint != RuntimeFingerprint(state))
            throw Failure("$plan", "Current runtime evidence is required; preliminary operation labels cannot authorize admission.", "INTENT_OPERATION_PROOF_MISSING");
        foreach (var evidence in state.RuntimeEvidence) ValidateRuntime(state, evidence);
        foreach (var source in PlanningIntentAssessment.IntentSources(state))
        foreach (var span in PlanningReferences.Register(state, source.Id, source.Kind, source.Text).ToArray()
            .Where(r => !string.IsNullOrWhiteSpace(source.Text.Substring(r.Start, r.Length))))
            if (!state.RuntimeEvidence.Any(e => state.References.Single(r => r.Id == e.SourceReference) is var covered &&
                covered.SourceId == span.SourceId && covered.Start <= span.Start && covered.Start + covered.Length >= span.Start + span.Length))
                throw Failure(span.Id, "Runtime interpretation did not account for this owned source scope.");
    }

    internal static void ValidateRuntime(PlanningSnapshot state, PlanningRuntimeEvidence evidence)
    {
        var references = new[] { evidence.SourceReference, evidence.ClauseReference, evidence.ActionReference, evidence.ResourceReference, evidence.ExecutionReference }.OfType<string>();
        if (references.Any(id => !PlanningChoiceEvidence.Current(state, id)) || evidence != SealRuntime(state, evidence) ||
            PlanningChoiceEvidence.Parent(state, evidence.SourceReference).Id != evidence.ClauseReference)
            throw Failure(evidence.Id, "Runtime evidence is stale, foreign or has changed.");
        var source = state.References.Single(r => r.Id == evidence.SourceReference);
        var authority = PlanningIntentAssessment.IntentSources(state).Single(s => s.Id == source.SourceId).Authority;
        if (authority == PlanningSourceAuthority.ConstraintsOnly
            ? evidence.Role != "policy" || evidence.Origin != PlanningRuntimeEvidenceOrigin.EngineSourceAuthority
            : evidence.Origin != PlanningRuntimeEvidenceOrigin.SourceInterpretation)
            throw Failure(evidence.Id, "Runtime evidence does not match its engine-owned source authority.");
        if (evidence.Role == "unresolved") throw Failure(evidence.Id, "The source does not establish its runtime execution boundary.");
        if (evidence.Role is "planning_directive" or "contract" or "policy")
        {
            if (evidence.ActionReference is not null || evidence.Kind is not null || evidence.ResourceReference is not null || evidence.ExecutionReference is not null ||
                evidence.ResourceAction is not null || evidence.ResourceOwnership is not null || evidence.BaselineReference is not null || evidence.EvidenceRole is not null || evidence.Required)
                throw Failure(evidence.Id, "Non-executable evidence cannot contain action authority.");
            return;
        }
        if (authority == PlanningSourceAuthority.ConstraintsOnly || evidence.ActionReference is null || evidence.ExecutionReference is null ||
            evidence.EvidenceRole is not ("action" or "governing") || !PlanningSourceGroundingRules.OperationKinds.Contains(evidence.Kind) ||
            evidence.Role != (evidence.Kind == "local_processing" ? "local_behavior" : "runtime_action"))
            throw Failure(evidence.Id, "Runtime evidence needs its owned action span, execution boundary and action/governing role.");
        foreach (var id in new[] { evidence.ActionReference, evidence.ExecutionReference, evidence.ResourceReference }.OfType<string>())
        {
            var selected = state.References.Single(r => r.Id == id);
            if (selected.SourceId != source.SourceId || selected.Start < source.Start || selected.Start + selected.Length > source.Start + source.Length)
                throw Failure(evidence.Id, "Action and execution proof must be inside their owned source scope.");
        }
        if (authority == PlanningSourceAuthority.ExistingBehavior)
        {
            if (evidence.BaselineReference is null || !PlanningSourceGroundingRules.BaselineNodes(state).TryGetValue(evidence.BaselineReference, out var node) ||
                !BaselineKinds(node.Node).Contains(evidence.Kind)) throw Failure(evidence.Id, "Existing behavior requires its exact compatible baseline node.");
        }
        else if (authority != PlanningSourceAuthority.RequestedBehavior || evidence.BaselineReference is not null)
            throw Failure(evidence.Id, "Only requested behavior can establish a new runtime action.");
        if (evidence.Kind is "resource_lifecycle" or "cleanup")
        {
            if (evidence.ResourceAction is not ("create" or "acquire" or "release" or "delete") || evidence.Kind == "cleanup" && evidence.ResourceAction is not ("release" or "delete"))
                throw Failure(evidence.Id, "Resource lifecycle requires an explicit runtime ownership action.");
            if (evidence.ResourceReference is null || evidence.ResourceOwnership != "workflow_runtime_resource")
                throw Failure(evidence.Id, "Resource lifecycle requires owned resource evidence inside the requested runtime action scope.");
            var resource = state.References.Single(r => r.Id == evidence.ResourceReference);
            if (state.RuntimeEvidence.Where(e => e.Role == "planning_directive").Any(e =>
                state.References.Single(r => r.Id == e.SourceReference) is var directive && directive.SourceId == resource.SourceId &&
                directive.Start <= resource.Start && directive.Start + directive.Length >= resource.Start + resource.Length))
                throw Failure(evidence.Id, "The planning artifact is not an owned runtime resource.");
        }
        else if (evidence.ResourceAction is not null || evidence.ResourceReference is not null || evidence.ResourceOwnership is not null) throw Failure(evidence.Id, "This action cannot claim resource lifecycle authority.");
    }

    internal static void RequireExecutableIntent(PlanningSnapshot state)
    {
        RequireCurrent(state);
        if (!state.Obligations.Any(PlanningSourceDecisions.IsOperation))
            throw Failure("$plan", "Neither a proven runtime action nor proven local executable behavior was established.");
    }
}
