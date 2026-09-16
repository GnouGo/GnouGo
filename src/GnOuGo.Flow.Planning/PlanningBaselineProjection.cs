using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Derived views of the authoritative graph. Structural units never enter prose interpretation.
/// Only designated annotations expose text decisions, with their exact owner locked by the engine.</summary>
internal static class PlanningBaselineProjection
{
    internal const int Version = 1;

    internal static List<PlanningIntentAssessment.IntentSource> Sources(PlanningSnapshot state)
    {
        var result = new List<PlanningIntentAssessment.IntentSource>();
        if (state.Request.Baseline is not { } graph) return result;
        var fingerprint = PlanningGraphCompiler.Fingerprint(graph);
        var nodes = PlanningSourceGroundingRules.BaselineNodes(state); // Validate exact node identities.
        _ = PlanningDeclarations.Baselines(state); // Validate exact public identities.
        if (graph.Workflows.Select(w => w.Key).Distinct(StringComparer.Ordinal).Count() != graph.Workflows.Count ||
            graph.Workflows.All(w => w.Key != graph.Entrypoint))
            throw Failure("$baseline", "The baseline has ambiguous workflows or an unknown entrypoint.");
        PlanningBaselineOwnership Owner(string kind, string? workflow = null, string? node = null, string? direction = null, string? port = null)
            => new(Version, fingerprint, kind, workflow, node, direction, port, null);
        void Add(PlanningBaselineOwnership owner, string text)
        {
            var id = "baseline_source_" + PlanningGraphCompiler.Fingerprint(new JsonArray(owner.OwnerKind,
                owner.Workflow, owner.Node, owner.Direction, owner.Port, owner.Field).ToJsonString())[..24];
            result.Add(new(id, "existing_workflow", text) { Baseline = owner });
        }
        void Annotation(PlanningBaselineOwnership owner, string field, string? text)
        { if (!string.IsNullOrWhiteSpace(text)) Add(owner with { Field = field }, text); }
        void Schema(PlanningBaselineOwnership owner, string path, PlanningSchema? schema)
        {
            if (schema is null) return;
            Annotation(owner, path + "/description", schema.Description);
            Schema(owner, path + "/items", schema.Items);
            Schema(owner, path + "/additionalProperties", schema.AdditionalProperties);
            foreach (var property in schema.Properties)
                Schema(owner, path + "/properties/" + PlanningFieldPaths.Escape(property.Name), property.Schema);
        }
        var root = Owner("graph");
        // The fingerprint covers all typed fields (including expressions, literals and metadata);
        // a structural source indexes its owner, never splits that owner's JSON into prose.
        Add(root, new JsonObject { ["entrypoint"] = graph.Entrypoint, ["baselineFingerprint"] = fingerprint }.ToJsonString());
        Annotation(root, "summary", graph.Summary);
        foreach (var workflow in graph.Workflows.OrderBy(w => w.Key, StringComparer.Ordinal))
        {
            var container = Owner("workflow", workflow.Key);
            Add(container, new JsonObject { ["workflow"] = workflow.Key, ["steps"] = new JsonArray(workflow.Steps.Select(n => (JsonNode)JsonValue.Create(n.Key)!).ToArray()),
                ["finally"] = new JsonArray(workflow.Finally.Select(n => (JsonNode)JsonValue.Create(n.Key)!).ToArray()) }.ToJsonString());
            Annotation(container, "purpose", workflow.Purpose);
            foreach (var port in workflow.Inputs.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var owner = Owner("port", workflow.Key, direction: "input", port: port.Name);
                Add(owner, JsonSerializer.Serialize(port, PlanningJsonContext.Default.PlanningPort));
                Schema(owner, "schema", port.Schema);
            }
            foreach (var port in workflow.Outputs.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var owner = Owner("port", workflow.Key, direction: "output", port: port.Name);
                Add(owner, JsonSerializer.Serialize(port, PlanningJsonContext.Default.PlanningOutput));
                Schema(owner, "schema", port.Schema);
            }
            foreach (var (_, value) in nodes.Where(p => p.Value.Workflow == workflow.Key).OrderBy(p => p.Value.Node.Key, StringComparer.Ordinal))
            {
                var owner = Owner("node", workflow.Key, value.Node.Key);
                Add(owner, JsonSerializer.Serialize(value.Node, PlanningJsonContext.Default.PlanningNode));
                Annotation(owner, "purpose", value.Node.Purpose);
                Schema(owner, "outputSchema", value.Node.OutputSchema);
                Schema(owner, "structuredOutput/schema", value.Node.StructuredOutput?.Schema);
            }
        }
        if (result.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw Failure("$baseline", "The baseline contains duplicate structural field identities.");
        return result;
    }

    internal static string? NodeReference(PlanningSnapshot state, PlanningBaselineOwnership owner) => owner.OwnerKind == "node"
        ? PlanningSourceGroundingRules.BaselineNodes(state).Single(p => p.Value.Workflow == owner.Workflow && p.Value.Node.Key == owner.Node).Key : null;

    internal static PlanningRuntimeEvidence Evidence(PlanningSnapshot state, PlanningReference reference)
    {
        var source = PlanningIntentAssessment.IntentSources(state).Single(s => s.Id == reference.SourceId);
        if (source.Baseline is not { } owner || owner != reference.Baseline)
            throw Failure(reference.Id, "Structural evidence needs its current exact baseline owner.");
        var baseline = NodeReference(state, owner);
        var kinds = baseline is null ? [] : PlanningOperations.BaselineKinds(PlanningSourceGroundingRules.BaselineNodes(state)[baseline].Node);
        // Opaque executor semantics confer no action authority. Admission assesses completeness below.
        var kind = kinds.Length == 1 ? kinds[0] : null;
        return PlanningOperations.SealRuntime(state, new("", reference.Id, PlanningChoiceEvidence.Parent(state, reference.Id).Id,
            kind is null ? "contract" : kind == "local_processing" ? "local_behavior" : "runtime_action",
            kind is null ? null : reference.Id, null, kind is null ? null : reference.Id, kind,
            kind is null ? null : source.Structural ? "action" : "governing", kind is null ? null : baseline,
            null, PlanningOperationNecessity.Unspecified, "") { Origin = PlanningRuntimeEvidenceOrigin.EngineBaseline });
    }

    internal static void RequireExecutableCoverage(PlanningSnapshot state)
    {
        foreach (var (id, node) in PlanningSourceGroundingRules.BaselineNodes(state))
        {
            // A purpose cannot select an external effect or manufacture lifecycle ownership.
            if (PlanningOperations.BaselineKinds(node.Node).Length != 1 || !state.RuntimeEvidence.Any(e =>
                e.BaselineReference == id && e.Origin == PlanningRuntimeEvidenceOrigin.EngineBaseline &&
                state.References.Single(r => r.Id == e.SourceReference).Baseline is { Field: null, OwnerKind: "node" }))
                throw Failure(id, "The exact baseline node lacks a declared execution effect or resource ownership proof.");
        }
    }

    internal static string[] AnnotationKinds(PlanningBaselineOwnership owner) => owner.OwnerKind == "port"
        ? ["declaration_constraint", "information"]
        : ["workflow_policy", "implementation_policy", "confirmation_required", "confirmation_forbidden", "rejection_condition", "exact_denial", "runtime_condition", "runtime_fallback", "information"];

    internal static bool OwnsDeclaration(PlanningSnapshot state, PlanningObligation candidate, PlanningBusinessDeclaration target)
    {
        var owners = candidate.EvidenceReferences.Select(id => state.References.Single(r => r.Id == id).Baseline).OfType<PlanningBaselineOwnership>().ToArray();
        return owners.Length == 0 || owners.All(o => o.OwnerKind == "port" && target.BaselineReference is { } id &&
            PlanningDeclarations.Baselines(state).TryGetValue(id, out var port) && port.Name == o.Port && port.Direction == o.Direction &&
            port.Scope == (o.Workflow == state.Request.Baseline!.Entrypoint ? "main" : o.Workflow));
    }

    internal static bool OwnsOperation(PlanningSnapshot state, string reference, PlanningObligation operation)
    {
        var owner = state.References.Single(r => r.Id == reference).Baseline;
        return owner is null || owner.OwnerKind == "graph" ||
            owner.OwnerKind == "node" && operation.OperationAdmission?.BaselineReference == NodeReference(state, owner) ||
            owner.OwnerKind == "workflow" && PlanningOperations.EffectAnchor(state, operation).WorkflowScope == owner.Workflow;
    }

    private static WorkflowRuntimeException Failure(string id, string message) => new("INTENT_OPERATION_UNRESOLVED", message,
        details: new JsonObject { ["location"] = "/baseline/@" + PlanningFieldPaths.Escape(id) });
}
