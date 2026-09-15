using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Constructs a genuinely new business schema from bounded type/layout decisions, never a model schema body.</summary>
internal static class PlanningSchemaDecisions
{
    internal static PlanningDecisionPages.Dispatch? Advance(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole)
    {
        // This is the same typed assignment gate as value holes. Its internal schema
        // is a validator for coordinator materialization, never a model response.
        var transport = PlanningHoleRequests.Create(state, workflow, [hole]);
        var strict = hole.Path.Contains("/structuredOutput/", StringComparison.Ordinal);
        var objectRoot = strict || hole.Path.EndsWith("/outputSchema", StringComparison.Ordinal);
        var contract = Build(state, hole, "root", objectRoot, strict, 0, out var pending, transport.Context.AsObject());
        if (contract is null) return pending;
        var progress = state.Construction.Workflows.Single(w => w.WorkflowKey == workflow.Key);
        state.Construction.Candidates.Add(new()
        {
            WorkflowKey = workflow.Key, GraphFingerprint = PlanningGraphCompiler.Fingerprint(state.Graph!),
            WorkflowFingerprint = PlanningHoleAssignments.WorkflowFingerprint(workflow), DependencyFingerprint = PlanningWorkflowConstruction.DependencyFingerprint(state, progress),
            ScopeFingerprint = PlanningHoleRequests.Scope([hole], transport.Schema), Targets = [hole], ResponseSchema = transport.Schema,
            Payload = new JsonObject { ["assignments"] = new JsonObject { [hole.Id] = PlanningModelValues.Compact(JsonSerializer.SerializeToNode(contract, PlanningJsonContext.Default.PlanningSchema)) } }
        });
        return null;
    }

    internal static PlanningSchema? Build(PlanningSnapshot state, PlanningHole hole,
        string member, bool objectRequired, bool strict, int depth, out PlanningDecisionPages.Dispatch? pending, JsonObject? contractContext = null)
    {
        pending = null;
        if (depth > 16) throw new WorkflowRuntimeException("BUSINESS_SCHEMA_DEPTH", "The new business schema exceeds the representable decision depth at " + hole.CanonicalLocation + ":" + member);
        var fingerprint = PlanningGraphCompiler.Fingerprint(hole.Purpose + ":" + state.Preparation?.Fingerprint + ":" + member);
        var type = "object"; var nullable = false;
        if (!objectRequired)
        {
            var id = hole.Id + ":type:" + member;
            var decision = new PlanningDecisionPages.Decision(id, PlanningHoleRequests.Object(("type", PlanningHoleRequests.Enum("string", "number", "integer", "boolean", "array", "object")),
                    ("nullable", PlanningHoleRequests.Type("boolean"))), new JsonObject { ["businessObligation"] = hole.Purpose, ["member"] = member,
                        ["contracts"] = contractContext?.DeepClone() }, fingerprint, hole.Id);
            pending = PlanningDecisionPages.Next(state, "construction_schema", hole.WorkflowKey, [decision]);
            if (pending is not null) return null;
            var answers = PlanningDecisionPages.Completed(state, "construction_schema", hole.WorkflowKey, [decision])!;
            type = answers[id]!["type"]!.ToString(); nullable = answers[id]!["nullable"]!.GetValue<bool>();
        }
        var schema = new PlanningSchema { Type = type, Nullable = nullable };
        if (type == "array")
        {
            schema.Items = Build(state, hole, member + "/item", false, strict, depth + 1, out pending, contractContext);
            if (pending is not null) return null;
        }
        if (type != "object") return schema;
        for (var page = 0; page < 16; page++)
        {
            var id = hole.Id + ":layout:" + member + ":" + page;
            var field = PlanningHoleRequests.Object(("name", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 48 }));
            if (!strict)
            { field["properties"]!["required"] = PlanningHoleRequests.Type("boolean"); field["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("required")); }
            var responseSchema = PlanningHoleRequests.Object(("members", new JsonObject { ["type"] = "array", ["minItems"] = 0, ["maxItems"] = 4, ["items"] = field }),
                ("more", PlanningHoleRequests.Type("boolean")));
            var decision = new PlanningDecisionPages.Decision(id, responseSchema, new JsonObject { ["businessObligation"] = hole.Purpose, ["member"] = member,
                    ["establishedNames"] = new JsonArray(schema.Properties.Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray()), ["contracts"] = contractContext?.DeepClone(),
                    ["task"] = "Define up to four new business members, or finish. Existing names are immutable. Do not reproduce a known contract; only explicitly delegated, unresolved business structure is in scope." }, fingerprint, hole.Id);
            pending = PlanningDecisionPages.Next(state, "construction_schema", hole.WorkflowKey, [decision]);
            if (pending is not null) return null;
            var answers = PlanningDecisionPages.Completed(state, "construction_schema", hole.WorkflowKey, [decision])!;
            var assignment = answers[id]!; var members = assignment["members"]!.AsArray();
            if (members.Count == 0 && assignment["more"]!.GetValue<bool>())
                throw new WorkflowRuntimeException("SCHEMA_DECISION_NO_PROGRESS", "An empty layout page cannot request another page.");
            foreach (var value in members)
            {
                var name = value!["name"]!.ToString();
                if (schema.Properties.Any(p => p.Name == name))
                    throw new WorkflowRuntimeException("SCHEMA_DECISION_DUPLICATE", "A schema decision repeated an established member at " + hole.CanonicalLocation);
                var child = Build(state, hole, member + "/" + PlanningFieldPaths.Escape(name), false, strict, depth + 1, out pending, contractContext);
                if (pending is not null) return null;
                schema.Properties.Add(new() { Name = name, Required = strict || value["required"]!.GetValue<bool>(), Schema = child! });
            }
            if (!assignment["more"]!.GetValue<bool>()) return schema;
        }
        throw new WorkflowRuntimeException("BUSINESS_SCHEMA_MEMBER_LIMIT", "The new business schema exceeds its bounded member count at " + hole.CanonicalLocation);
    }
}
