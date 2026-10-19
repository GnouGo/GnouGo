using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal static JsonObject DraftSchema(PlanningSession state)
    {
        var step = state.Construction!.Compact!.CurrentStep!;
        // Reuse existing semantic type/binding definitions, but never issue their
        // full task union or the complete operation catalog in this fresh protocol.
        var template = PlanningSchemas.FullProposal(state, compact: false, admitted: new HashSet<string>(StringComparer.Ordinal), scopeGuidance: false);
        var definitions = template["$defs"]!.AsObject();
        var tasks = definitions["task"]!["anyOf"]!.AsArray();
        foreach (var task in tasks.ToArray())
            if (task!["properties"]!["kind"]!["enum"]![0]!.ToString() != step.Kind ||
                step.Kind == "transform" && task["properties"]!["mode"] is null) tasks.Remove(task);
        if (step.Kind == "operation")
            foreach (var capability in SelectedContracts(state)) tasks.Add((JsonNode)OperationTask(capability));
        if (step.Kind == "call" && step.GroupId is not null)
            foreach (var task in tasks) task!["properties"]!["group"] = PlanningSchemas.Enum(step.GroupId);

        // Opening a container establishes its interface/control input, not a
        // second recursive planning request. Child bodies are subsequent steps.
        definitions["scope"] = PlanningSchemas.Object(("tasks", EmptyArray()), ("outputs", EmptyArray()), ("always", EmptyArray()));
        var maximum = state.Construction.Limits.MaxTasksPerDraft;
        var taskArray = tasks.Count == 0 ? EmptyArray() : PlanningSchemas.Array(PlanningSchemas.Ref("task"), 0, maximum);
        var outputs = PlanningSchemas.Array(PlanningSchemas.Ref("output"), 0, maximum);
        if (step.Scope == "/root")
        {
            if (step.OutputNames.Count == 0) outputs = EmptyArray();
            else outputs = PlanningSchemas.Array(PlanningSchemas.Object(("name", PlanningSchemas.Enum(step.OutputNames.ToArray())),
                ("value", PlanningSchemas.Ref("value"))), 0, maximum);
        }
        var extension = PlanningSchemas.Object(("scope", PlanningSchemas.Enum(step.Scope)),
            ("tasks", taskArray), ("always", taskArray.DeepClone().AsObject()), ("outputs", outputs),
            ("groups", step.Kind == "group" ? PlanningSchemas.Array(PlanningSchemas.Ref("group"), 0, 1) : EmptyArray()),
            ("choices", step.Kind == "choice" ? PlanningSchemas.Array(PlanningSchemas.Ref("choice"), 0, 1) : EmptyArray()),
            ("closeScopes", PlanningSchemas.Array(PlanningSchemas.Enum(step.Scope), 0, 1)),
            ("requirementIds", step.RequirementIds.Count == 0 ? EmptyArray() :
                PlanningSchemas.Array(PlanningSchemas.Enum(step.RequirementIds.ToArray()), 0, step.RequirementIds.Count)),
            ("complete", PlanningSchemas.Type("boolean")));
        var publicOutputs = state.Requirements?.Outputs?.Select(o => o.Name).ToArray() ?? [];
        var commitments = step.OperationIds.Count == 0 || step.RequirementIds.Count == 0 ? PlanningSchemas.Type("null") :
            PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Object(("requirementId", PlanningSchemas.Enum(step.RequirementIds.ToArray())),
                ("operation", PlanningSchemas.Enum(step.OperationIds.ToArray())),
                ("result", publicOutputs.Length == 0 ? PlanningSchemas.Type("null") : PlanningSchemas.Nullable(PlanningSchemas.Object(
                    ("port", PlanningSchemas.String()), ("output", PlanningSchemas.Enum(publicOutputs)))))), 0, 32));
        var schema = PlanningSchemas.Object(("baseHash", PlanningSchemas.Enum(step.BaseHash)), ("requirements", PlanningSchemas.Type("null")),
            ("commitments", commitments),
            ("extension", PlanningSchemas.Nullable(extension)), ("discoveryRequests", PlanningSchemas.Type("null")),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications())),
            ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(new JsonObject { ["type"] = "string", ["maxLength"] = 2048 }, 1, 8))),
            ("rollbackTask", PlanningSchemas.Nullable(PlanningSchemas.String())), ("rollbackReason", PlanningSchemas.Nullable(PlanningSchemas.String())),
            ("reselectStep", state.Construction.Candidate?.AuthorizedPaths is null &&
                (state.Diagnostics.Any(d => d.Required) || state.Construction.Candidate?.Diagnostics.Any(d => d.Required) == true)
                ? PlanningSchemas.Nullable(new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 }) : PlanningSchemas.Type("null")));
        template.Remove("$defs"); schema["$defs"] = definitions;
        return PlanningContractValidation.ProjectStructuredOutputSchema(PlanningSchemas.Compact(schema));

        JsonObject OperationTask(PlanningCapability capability)
        {
            var operation = PlanningCapabilityArguments.Editable(capability);
            var ports = SelectedInputPorts(state, capability).Select(port =>
                PlanningSchemas.Object(("name", PlanningSchemas.Enum(port.Name)),
                    ("value", PlanningBindingSchemas.For(port.Schema, definitions,
                        literalOnly: TaskPlanCompiler.LiteralScopeInput(capability.StepType, port.Path[0]),
                        workspace: TaskPlanCompiler.FixedWorkspaceInput(capability.StepType, port.Path[0]))))).ToArray();
            var inputs = ports.Length switch
            {
                0 => EmptyArray(),
                1 => PlanningSchemas.Array(ports[0]),
                _ => PlanningSchemas.Array(new JsonObject { ["anyOf"] = new JsonArray(ports.Cast<JsonNode>().ToArray()) })
            };
            return PlanningSchemas.Object(("id", PlanningSchemas.Ref("id")), ("kind", PlanningSchemas.Enum("operation")),
                ("objective", PlanningSchemas.Ref("goal")), ("dependsOn", PlanningSchemas.Ref("identities")),
                ("requires", PlanningSchemas.Nullable(PlanningSchemas.Ref("value"))),
                ("operation", PlanningSchemas.Enum(operation.Id)), ("inputs", inputs));
        }
    }

    private static JsonObject EmptyArray() => PlanningSchemas.Array(PlanningSchemas.Type("string"), 0, 0);
}
