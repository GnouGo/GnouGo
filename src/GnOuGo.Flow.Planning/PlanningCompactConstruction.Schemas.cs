using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private static (bool CloseScope, bool Complete) ClosurePresentation(PlanningSession state)
    {
        var construction = state.Construction!;
        var compact = State(state);
        var step = compact.CurrentStep!;
        var symbols = new TaskPlanSymbols(state.Plan ?? new TaskPlan());
        var open = construction.OpenScopes;
        // Scope addresses are global task identities, not hierarchical paths.
        var hasOpenChild = open.Any(path =>
        {
            for (var parent = symbols.Scopes.SingleOrDefault(s => s.Path == path)?.Parent; parent is not null; parent = parent.Parent)
                if (parent.Path == step.Scope) return true;
            return false;
        });
        // Fresh container presentations leave their empty child bodies for later steps.
        var opensContainer = step.Kind is "group" or "sequence" or "conditional" or "parallel" or "foreach";
        var canClose = !hasOpenChild && !opensContainer;
        var canComplete = canClose && open.All(path => path == step.Scope) &&
            !compact.Sources.Any(s => s.Kind is "page" or "answer_page" or "revision_page" && !s.Indexed) &&
            construction.Requirements.All(r => step.CloseRequirementIds.Contains(r.Id, StringComparer.Ordinal) ||
                r.Status == "closed" && (step.Kind == "close" || !step.RequirementIds.Contains(r.Id, StringComparer.Ordinal)));
        // This narrows new presentations only. Existing semantic validation still
        // owns returned candidates, including responses to earlier issued schemas.
        return (canClose && (step.Scope != "/root" || canComplete), canComplete);
    }

    internal static JsonObject DraftSchema(PlanningSession state)
    {
        var step = state.Construction!.Compact!.CurrentStep!;
        var fresh = PlanningConstructionPhasePolicies.IsFresh(state.Request);
        // Fresh requests construct the selected kind from shared semantic
        // definitions. Historical presentations retain their original template.
        var definitions = fresh ? PlanningSchemas.ConstructionDefinitions(state, step.Kind) :
            PlanningSchemas.FullProposal(state, compact: false, admitted: new HashSet<string>(StringComparer.Ordinal),
                scopeGuidance: false)["$defs"]!.DeepClone().AsObject();
        var tasks = definitions["task"]!["anyOf"]!.AsArray();
        if (!fresh)
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
        var closure = ClosurePresentation(state);
        var completion = PlanningSchemas.Type("boolean");
        if (!closure.Complete) completion["enum"] = new JsonArray(false);
        var extension = PlanningSchemas.Object(("scope", PlanningSchemas.Enum(step.Scope)),
            ("tasks", taskArray), ("always", taskArray.DeepClone().AsObject()), ("outputs", outputs),
            ("groups", step.Kind == "group" ? PlanningSchemas.Array(PlanningSchemas.Ref("group"), 0, 1) : EmptyArray()),
            ("choices", step.Kind == "choice" ? PlanningSchemas.Array(PlanningSchemas.Ref("choice"), 0, 1) : EmptyArray()),
            ("closeScopes", closure.CloseScope ? PlanningSchemas.Array(PlanningSchemas.Enum(step.Scope), 0, 1) : EmptyArray()),
            ("requirementIds", step.RequirementIds.Count == 0 ? EmptyArray() :
                PlanningSchemas.Array(PlanningSchemas.Enum(step.RequirementIds.ToArray()), 0, step.RequirementIds.Count)),
            ("complete", completion));
        var publicOutputs = State(state).PresentationVersion == 2
            ? step.OutputNames.ToArray()
            : state.Requirements?.Outputs?.Select(output => output.Name).ToArray() ?? [];
        var commitments = step.OperationIds.Count == 0 || step.RequirementIds.Count == 0 ? PlanningSchemas.Type("null") :
            PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Object(("requirementId", PlanningSchemas.Enum(step.RequirementIds.ToArray())),
                ("operation", PlanningSchemas.Enum(step.OperationIds.ToArray())),
                ("result", publicOutputs.Length == 0 ? PlanningSchemas.Type("null") : PlanningSchemas.Nullable(PlanningSchemas.Object(
                    ("port", PlanningSchemas.String()), ("output", PlanningSchemas.Enum(publicOutputs)))))), 0, 32));
        var rollback = !fresh || state.Construction.Candidate is null && state.Phase != PlanningPhase.Replanning &&
            state.Plan is { } accepted && TaskPlanRevisions.Tasks(accepted).Any();
        var schema = PlanningSchemas.Object(("baseHash", PlanningSchemas.Enum(step.BaseHash)), ("requirements", PlanningSchemas.Type("null")),
            ("commitments", commitments),
            ("extension", PlanningSchemas.Nullable(extension)), ("discoveryRequests", PlanningSchemas.Type("null")),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications())),
            ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(new JsonObject { ["type"] = "string", ["maxLength"] = 2048 },
                1, fresh ? 8 : State(state).PresentationVersion == 2 ? 1 : 8))),
            ("rollbackTask", rollback ? PlanningSchemas.Nullable(PlanningSchemas.String()) : PlanningSchemas.Type("null")),
            ("rollbackReason", rollback ? PlanningSchemas.Nullable(PlanningSchemas.String()) : PlanningSchemas.Type("null")),
            ("reselectStep", CanReselectInitialDraft(state) || state.Construction.Candidate?.AuthorizedPaths is null &&
                (state.Diagnostics.Any(d => d.Required) || state.Construction.Candidate?.Diagnostics.Any(d => d.Required) == true)
                ? PlanningSchemas.Nullable(new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 }) : PlanningSchemas.Type("null")));
        schema["$defs"] = definitions;
        var reselection = fresh && schema["properties"]!["reselectStep"]!["type"]?.ToString() != "null"
            ? schema["properties"]!["reselectStep"]!.DeepClone().AsObject() : null;
        var projected = PlanningContractValidation.ProjectStructuredOutputSchema(PlanningSchemas.Compact(schema));
        if (reselection is not null)
        {
            reselection["description"] = "A precise reason to replace this tentative step, not a step ID. Explain what prevents it and identify any missing prerequisite; scope and original requirements remain fixed.";
            projected["properties"]!["reselectStep"] = reselection;
        }
        // Compact removes presentation annotations from the shared grammar. Put
        // back only the semantics needed by this selected kind and scope.
        projected["description"] = DraftSemantics(state, step);
        return projected;

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

    internal static string DraftSemantics(PlanningSession state, PlanningConstructionStep step)
    {
        var scope = new TaskPlanSymbols(state.Plan ?? new TaskPlan()).Scopes.SingleOrDefault(s => s.Path == step.Scope);
        var hints = new List<string>();
        if (step.Kind is not ("group" or "choice" or "close"))
        {
            hints.Add("dependsOn names preceding same-scope tasks only; data bindings already imply dependencies and ancestor values use captures. requires is a Boolean assertion: false fails.");
            hints.Add("always contains cleanup on completion or failure; preserve available payloads before nested cleanup and guard absent task results with present.");
        }
        if (step.Scope != "/root")
            hints.Add("outputs explicitly export this scope's bound values through its enclosing task or group call; consumers cannot read hidden child tasks directly.");
        if (step.Kind == "value")
            hints.Add("value only copies, projects or assembles typed values using declared arithmetic, predicates, lookup, flatten and JSON encoding. It cannot concatenate or format strings, URL-encode or interpolate literals; its objective adds no computation.");
        if (PlanningConstructionPhasePolicies.IsFresh(state.Request) && step.Kind == "transform")
            hints.Add("transform resultType is an object of named output ports; output.port selects that field's type, not the whole result object. extract preserves observed facts; interpret uses its objective as the runtime LLM instruction, inputs as data and resultType as the output contract, without a separate expression. Neither executes external effects.");
        if (State(state).PresentationVersion == 2 && step.Kind == "operation")
            hints.Add(PlanningConstructionPhasePolicies.IsFresh(state.Request)
                ? "editableInputs is only the inspected subset. Retrieve the exact schema branches needed for this business action together, up to eight addresses; the complete follow-up must fit its captured ceiling. Retrieval expands binding grammar. [] does not mean no arguments are needed."
                : "editableInputs is only the inspected subset. Retrieve one exact schema address per call for any omitted port this business action needs; retrieval expands the binding grammar. [] does not mean no arguments are needed.");
        if (step.Kind == "foreach")
        {
            hints.Add("foreach.maxItems is the TOTAL input-item ceiling; excess fails, it never samples or truncates. maxConcurrency limits simultaneous workers, not total items. Neither bound guarantees a nonempty collection.");
            if (State(state).PresentationVersion == 2)
                hints.Add("Without an explicit original bound, use the existing TaskPlan default maxItems=" +
                    new PlanTask().MaxItems.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ". 1 is singleton capacity, not a placeholder or worker count. Runtime ceilings still apply.");
        }
        if (step.Kind == "foreach" || scope?.Owner?.Kind == "foreach")
            hints.Add("Each foreach body output becomes an array on the enclosing task, preserving input order and repetitions; array-valued outputs remain nested. Within the body, item and index refer to the current iteration.");
        if (step.Kind == "conditional" || scope?.Owner?.Kind == "conditional")
            hints.Add("conditional chooses body or otherwise; both alternatives must export matching names, types, nullability and requiredness. Guard nullable values on the consuming path.");
        if (step.Kind == "parallel" || scope?.Owner?.Kind == "parallel")
            hints.Add("Parallel sibling branches are independent and their exported names must be distinct; maxConcurrency limits simultaneous branches.");
        if (step.Kind == "group" || scope is { Parent: null } && step.Scope != "/root")
            hints.Add("A reusable group has isolated named inputs; its body cannot capture caller inputs or tasks. Close its body before selecting a call.");
        if (step.Kind == "call")
            hints.Add("Bind the selected closed group's declared input names; its explicit exports are the call's output ports.");
        if (step.Kind == "close")
            hints.Add("A close step may confirm host-issued closeRequirementIds against accepted work with empty closeScopes and no new tasks or exports. Scope closure is separate: add needed final exports without rewriting accepted work. Close children before their parent. Close root only when all scopes and original requirements are complete, with complete:true.");
        return string.Join(" ", hints);
    }

    private static JsonObject EmptyArray() => PlanningSchemas.Array(PlanningSchemas.Type("string"), 0, 0);
}
