using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal const string ValueSemantics = "value copies, projects or assembles typed values using declared arithmetic, predicates, lookup, flatten, JSON encoding and string_operation concat or uri_component. It has no implicit formatting or interpolation; its objective adds no computation.";
    internal const string RollbackSemantics = "Rollback reconstructs every invalidated task in its original scope, phase and order; it cannot discard accepted work. Preserve original objectives, effects, literal arguments, assertions, decisions and iteration limits. Any reference repair must still pass validation. Explain the reconstruction defect in rollbackReason and retrieve exact originals as needed.";
    internal const string RecursiveTaskBudgetSemantics = "maxTasksPerDraft is one total for all new tasks in this fragment. Count control tasks and every task in tasks and always, including nested body, otherwise, branches and group bodies. Array maxItems do not grant separate allowances.";

    internal static string RecursiveTaskBudgetGuidance(PlanningSession state) => PlanningTokenThresholds.OptimizationAdvisory(state.Request)
        ? "maxTasksPerDraft is an advisory target for the whole recursive contribution, including nested bodies and cleanup. Retain coherent behavior intact; exceeding the target produces a host warning and never permits truncation. Contract cardinalities and runtime resource limits still apply."
        : RecursiveTaskBudgetSemantics;

    internal static (bool CloseScope, bool Complete) ClosurePresentation(PlanningSession state, PlanningConstructionStep? selected = null)
    {
        var construction = state.Construction!;
        var compact = State(state);
        var step = selected ?? compact.CurrentStep!;
        var symbols = new TaskPlanSymbols(state.Plan ?? new TaskPlan());
        var open = construction.OpenScopes;
        // Scope addresses are global task identities, not hierarchical paths.
        var hasOpenChild = open.Any(path =>
        {
            for (var parent = symbols.Scopes.SingleOrDefault(s => s.Path == path)?.Parent; parent is not null; parent = parent.Parent)
                if (parent.Path == step.Scope) return true;
            return false;
        });
        var canClose = !hasOpenChild;
        var canComplete = canClose && open.All(path => path == step.Scope) &&
            !compact.Sources.Any(s => s.Kind is "page" or "answer_page" or "revision_page" && !s.Indexed) &&
            construction.Requirements.All(r => step.CloseRequirementIds.Contains(r.Id, StringComparer.Ordinal) ||
                RequirementCompletionClaimed(state, r.Id));
        // This narrows new presentations only. Existing semantic validation still
        // owns returned candidates, including responses to earlier issued schemas.
        return (canClose && (step.Scope != "/root" || canComplete), canComplete);
    }

    internal static JsonObject DraftSchema(PlanningSession state)
    {
        var step = state.Construction!.Compact!.CurrentStep!;
        var kind = step.Kind ?? throw new PlanningConflictException("Bind the immediate step implementation before issuing TaskPlan grammar.");
        var definitions = PlanningSchemas.ConstructionDefinitions(state, kind);
        var tasks = definitions["task"]!["anyOf"]!.AsArray();
        tasks.Clear();
        var kinds = BusinessFragmentKinds(state);
        foreach (var allowed in kinds.Where(allowed => allowed != "operation"))
            foreach (var shape in PlanningSchemas.TaskShapes(state, allowed)) tasks.Add(shape);
        if (step.OperationIds.Count > 0)
            foreach (var capability in SelectedContracts(state)) tasks.Add((JsonNode)OperationTask(capability));
        if (step.GroupId is not null)
            foreach (var task in tasks.Where(task => task?["properties"]?["kind"]?["enum"]?.AsArray().Any(value => value?.ToString() == "call") == true))
                task!["properties"]!["group"] = PlanningSchemas.Enum(step.GroupId);

        var maximum = PlanningOptimizationThresholds.Maximum(state, state.Construction.Limits.MaxTasksPerDraft);
        var nestedTask = "task";
        if (SupportsBusinessFragment(kind) && NestedControlKinds.Except(kinds, StringComparer.Ordinal).ToArray() is { Length: > 0 } controls)
        {
            // Only descendants may compose different control kinds. Exact selected
            // operations/calls stay shared with the top-level anchor grammar.
            var nested = new JsonArray((JsonNode)PlanningSchemas.Ref("task"));
            foreach (var control in controls)
                foreach (var shape in PlanningSchemas.TaskShapes(state, control)) nested.Add(shape);
            definitions["nestedTask"] = new JsonObject { ["anyOf"] = nested };
            nestedTask = "nestedTask";
        }
        // Related technical work shares one bounded recursive TaskPlan fragment.
        // Apply counts descendants, so nesting cannot evade the task allowance.
        definitions["scope"] = PlanningSchemas.Object(("tasks", PlanningSchemas.Array(PlanningSchemas.Ref(nestedTask), 0, maximum)),
            ("outputs", PlanningSchemas.Array(PlanningSchemas.Ref("output"), 0, maximum)),
            ("always", PlanningSchemas.Array(PlanningSchemas.Ref(nestedTask), 0, maximum)));
        var taskArray = tasks.Count == 0 ? EmptyArray() : PlanningSchemas.Array(PlanningSchemas.Ref("task"), 0, maximum);
        var outputs = PlanningSchemas.Array(PlanningSchemas.Ref("output"), 0, maximum);
        if (step.Scope == "/root")
        {
            if (step.OutputNames.Count == 0) outputs = EmptyArray();
            else outputs = PlanningSchemas.Array(PlanningSchemas.Object(("name", PlanningSchemas.Enum(step.OutputNames.ToArray())),
                ("value", PlanningSchemas.Ref("value"))), 0, maximum);
        }
        var closure = ClosurePresentation(state);
        var createsScopes = step.Kind == "group" && !BlockAnchorConstructed(state) || kinds.Any(NestedControlKinds.Contains);
        var scopeChoices = new JsonArray();
        if (closure.CloseScope) scopeChoices.Add((JsonNode)PlanningSchemas.Enum(step.Scope));
        if (createsScopes)
        {
            var childScope = Text(state, 192);
            childScope["pattern"] = "^/(tasks|groups)/";
            scopeChoices.Add((JsonNode)childScope);
        }
        var closeScopes = scopeChoices.Count == 0 ? EmptyArray() : PlanningSchemas.Array(scopeChoices.Count == 1
            ? scopeChoices[0]!.DeepClone().AsObject() : new JsonObject { ["anyOf"] = scopeChoices }, 0, createsScopes ? null : 1);
        var completion = PlanningSchemas.Type("boolean");
        if (!closure.Complete) completion["enum"] = new JsonArray(false);
        var extension = PlanningSchemas.Object(("scope", PlanningSchemas.Enum(step.Scope)),
            ("tasks", taskArray), ("always", taskArray.DeepClone().AsObject()), ("outputs", outputs),
            ("groups", step.Kind == "group" && !BlockAnchorConstructed(state) ? PlanningSchemas.Array(PlanningSchemas.Ref("group"), 0, 1) : EmptyArray()),
            ("choices", step.Kind == "choice" ? PlanningSchemas.Array(PlanningSchemas.Ref("choice"), 0, 1) : EmptyArray()),
            ("closeScopes", closeScopes),
            ("requirementIds", step.RequirementIds.Count == 0 ? EmptyArray() :
                PlanningSchemas.Array(PlanningSchemas.Enum(step.RequirementIds.ToArray()), 0, step.RequirementIds.Count)),
            ("complete", completion), ("completeBlock", PlanningSchemas.Type("boolean")));
        if (SupportsBusinessFragment(step.Kind))
        {
            extension["properties"]!["continueInScope"] = PlanningSchemas.Nullable(Text(state, 192));
            extension["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("continueInScope"));
        }
        if (step.Kind == "close" && step.CloseScopes is { } selectedClosure)
        {
            extension["properties"]!["closeScopes"] = selectedClosure.Count == 0 ? EmptyArray() :
                PlanningSchemas.Array(PlanningSchemas.Enum(step.Scope), 1, 1);
            extension["properties"]!["complete"] = new JsonObject { ["type"] = "boolean", ["enum"] = new JsonArray(selectedClosure.Contains("/root", StringComparer.Ordinal)) };
        }
        var eligible = EligibleContinuationAssociations(state);
        {
            var association = PlanningSchemas.Object(("continuationId", PlanningSchemas.Enum(eligible.Select(item => item.Id).ToArray())),
                ("binding", PlanningSchemas.Ref("continuationBindingValue")));
            {
                association["properties"]!["dischargedContinuationIds"] = PlanningSchemas.Nullable(eligible.Length < 2 ? EmptyArray() :
                    PlanningSchemas.Array(PlanningSchemas.Enum(eligible.Skip(1).Select(item => item.Id).ToArray()), 0, PlanningOptimizationThresholds.Maximum(state, 3)));
                association["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("dischargedContinuationIds"));
            }
            extension["properties"]!["continuationBinding"] = eligible.Length > 0 ? PlanningSchemas.Nullable(association) : PlanningSchemas.Type("null");
            if (eligible.Length > 0)
                definitions["continuationBindingValue"] = new JsonObject { ["anyOf"] = new JsonArray(
                    PlanningSchemas.Object(("kind", PlanningSchemas.Enum("output")), ("source", Text(state, 96)),
                        ("port", PlanningSchemas.Nullable(Text(state, 96)))),
                    PlanningSchemas.Object(("kind", PlanningSchemas.Enum("present")), ("source", Text(state, 96))),
                    PlanningSchemas.Object(("kind", PlanningSchemas.Enum("field")), ("port", Text(state, 96)),
                        ("items", PlanningSchemas.Array(PlanningSchemas.Ref("continuationBindingValue"), 1, 1)))) };
            extension["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("continuationBinding"));
        }
        var hasBoundaryTransitions = false;
        {
            var boundaries = BoundaryTargets(state, step.Scope).Where(boundary => step.RequirementIds.Count > 0 &&
                // Selected closures are mandatory in this draft, so their
                // boundaries cannot remain open for a deferred action.
                (step.Kind != "close" || step.CloseScopes?.Contains(boundary, StringComparer.Ordinal) != true) &&
                (boundary != "/root" || state.Requirements?.Outputs is { Count: > 0 }));
            var transitions = boundaries.Select(boundary => PlanningSchemas.Object(("scope", PlanningSchemas.Enum(boundary)),
                ("requirementIds", PlanningSchemas.Array(PlanningSchemas.Enum(step.RequirementIds.ToArray()), 1, step.RequirementIds.Count)),
                ("outputs", boundary == "/root" ? state.Requirements?.Outputs is { Count: > 0 } publicPorts
                    ? PlanningSchemas.Array(PlanningSchemas.Object(("name", PlanningSchemas.Enum(publicPorts.Select(output => output.Name).ToArray())),
                        ("value", PlanningSchemas.Ref("value"))), 1, maximum) : EmptyArray()
                    : PlanningSchemas.Array(PlanningSchemas.Ref("output"), 0, maximum)),
                ("closeScope", boundary == "/root" ? new JsonObject { ["type"] = "boolean", ["enum"] = new JsonArray(false) } : PlanningSchemas.Type("boolean")))).ToArray();
            hasBoundaryTransitions = transitions.Length > 0;
            extension["properties"]!["boundaryTransitions"] = transitions.Length == 0 ? PlanningSchemas.Type("null") :
                PlanningSchemas.Nullable(PlanningSchemas.Array(transitions.Length == 1 ? transitions[0] :
                    new JsonObject { ["anyOf"] = new JsonArray(transitions.Cast<JsonNode>().ToArray()) }, 0, PlanningOptimizationThresholds.Maximum(state, 4)));
            extension["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("boundaryTransitions"));
        }
        var publicOutputs = step.OutputNames.ToArray();
        var commitments = step.OperationIds.Count == 0 || step.RequirementIds.Count == 0 ? PlanningSchemas.Type("null") :
            PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Object(("requirementId", PlanningSchemas.Enum(step.RequirementIds.ToArray())),
                ("operation", PlanningSchemas.Enum(step.OperationIds.ToArray())),
                ("result", publicOutputs.Length == 0 ? PlanningSchemas.Type("null") : PlanningSchemas.Nullable(PlanningSchemas.Object(
                    ("port", PlanningSchemas.String()), ("output", PlanningSchemas.Enum(publicOutputs)))))), 0, PlanningOptimizationThresholds.Maximum(state, 32)));
        if (step.OperationIds.Count > 0 && step.RequirementIds.Count > 0)
        {
            var alternatives = SelectedContracts(state).Select(TaskOperations.Describe).Select(operation =>
                PlanningSchemas.Object(("requirementId", PlanningSchemas.Enum(step.RequirementIds.ToArray())),
                    ("operation", PlanningSchemas.Enum(operation.Id)),
                    ("result", publicOutputs.Length == 0 || operation.Outputs.Count == 0 ? PlanningSchemas.Type("null") :
                        PlanningSchemas.Nullable(PlanningSchemas.Object(
                            ("port", PlanningSchemas.Enum(operation.Outputs.Select(port => port.Name).ToArray())),
                            ("output", PlanningSchemas.Enum(publicOutputs))))))).ToArray();
            commitments = PlanningSchemas.Nullable(PlanningSchemas.Array(alternatives.Length == 1 ? alternatives[0] :
                new JsonObject { ["anyOf"] = new JsonArray(alternatives.Cast<JsonNode>().ToArray()) }, 0, PlanningOptimizationThresholds.Maximum(state, 32)));
        }
        var rollback = state.Construction.Candidate is null && state.Phase != PlanningPhase.Replanning &&
            state.Plan is { } accepted && TaskPlanRevisions.Tasks(accepted).Any();
        var schema = PlanningSchemas.Object(("requirements", PlanningSchemas.Type("null")),
            ("commitments", commitments),
            ("extension", PlanningSchemas.Nullable(extension)), ("discoveryRequests", PlanningSchemas.Type("null")),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications(PlanningTokenThresholds.OptimizationAdvisory(state.Request)))),
            ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(Text(state, 2048),
                1, PlanningOptimizationThresholds.Maximum(state, 8)))),
            ("rollbackTask", rollback ? PlanningSchemas.Nullable(PlanningSchemas.String()) : PlanningSchemas.Type("null")),
            ("rollbackReason", rollback ? PlanningSchemas.Nullable(PlanningSchemas.String()) : PlanningSchemas.Type("null")),
            ("reselectStep", CanReselectInitialDraft(state) || !EvidenceAdmissionOnly(state) && state.Construction.Candidate?.AuthorizedPaths is null &&
                (state.Diagnostics.Any(d => d.Required) || state.Construction.Candidate?.Diagnostics.Any(d => d.Required) == true)
                ? PlanningSchemas.Nullable(new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 }) : PlanningSchemas.Type("null")));
        schema["$defs"] = definitions;
        ConstrainReselectionSchema(state, schema);
        OmitHostFixedDraftFields(schema, extension);
        var reselection = schema["properties"]!["reselectStep"] is JsonObject reselectionSchema &&
            reselectionSchema["type"]?.ToString() != "null" ? reselectionSchema.DeepClone().AsObject() : null;
        var projected = PlanningContractValidation.ProjectStructuredOutputSchema(PlanningSchemas.Compact(schema));
        if (reselection is not null)
        {
            reselection = projected["properties"]!["reselectStep"]!.DeepClone().AsObject();
            reselection["description"] = "Abandon an unsuitable action or explicitly suspend its retained consumer for one evidenced prerequisite. Scope and original requirements remain fixed.";
            projected["properties"]!["reselectStep"] = reselection;
        }
        // Compact removes presentation annotations from the shared grammar. Put
        // back only the semantics needed by this selected kind and scope.
        projected["description"] = DraftSemantics(state, step);
        if (eligible.Length > 0 && State(state).SuspendedSteps?.LastOrDefault(Active) is { Status: "waiting" })
            projected["description"] += " When this fragment produces the retained consumer's prerequisite, continuationBinding identifies that consumer and the exact output or field binding. This is a proposed semantic association, validated and reviewed with the fragment; compatible types alone do not establish its meaning. Otherwise use null.";
        if (eligible.Length > 0 && ResumedConsumerParent(state) is not null)
            projected["description"] += " This resumed business consumer may supply its immediate waiting parent's prerequisite. continuationBinding may name that offered parent and this fragment's exact new output; keep dischargedContinuationIds empty. Both business consumers and their original obligations remain retained. The child must pass ordinary acceptance before the parent can resume.";
        if (hasBoundaryTransitions)
            projected["description"] += " boundaryTransitions optionally claims exact existing boundaries, once per scope: outputs are exact exports; closeScope asserts no further work belongs in that boundary for its named original requirements. Host parent relationships determine order; this is not a future-task queue. Exports may reach the root, but these claims never close a requirement or the root. Use null when meaning or cleanup remains unresolved.";
        if (eligible.Length > 1)
            projected["description"] += " An ancestor continuation association must explicitly list every bypassed prerequisite-only descendant in dischargedContinuationIds; never discharge original obligations or accepted effects.";
        if (rollback)
            projected["description"] += " " + RollbackSemantics;
        return projected;

        JsonObject OperationTask(PlanningCapability capability)
        {
            var operation = PlanningCapabilityArguments.Editable(capability);
            var ports = SelectedInputPorts(state, capability).Select(port =>
                PlanningSchemas.Object(("name", PlanningSchemas.Enum(port.Name)),
                    ("value", PlanningBindingSchemas.ForOperation(capability, port, definitions)))).ToArray();
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

    private static void OmitHostFixedDraftFields(JsonObject schema, JsonObject extension)
    {
        // These are envelope facts already fixed by the retained step, or DTO
        // defaults. Literal values and semantic choices keep their full grammar.
        Remove(extension, "scope");
        foreach (var name in new[] { "requirements", "commitments", "discoveryRequests", "rollbackTask", "rollbackReason", "reselectStep" })
            if (schema["properties"]![name]?["type"]?.ToString() == "null") Remove(schema, name);
        foreach (var name in new[] { "tasks", "always", "outputs", "groups", "choices", "closeScopes", "requirementIds", "continuationBinding", "boundaryTransitions" })
        {
            var field = extension["properties"]![name];
            if (field?["type"]?.ToString() == "null" ||
                field?["type"]?.ToString() == "array" && field["maxItems"]?.GetValue<int>() == 0)
                Remove(extension, name);
        }
        if (extension["properties"]!["complete"]?["enum"] is JsonArray { Count: 1 } values &&
            values[0]?.GetValue<bool>() == false) Remove(extension, "complete");

        static void Remove(JsonObject owner, string name)
        {
            owner["properties"]!.AsObject().Remove(name);
            var required = owner["required"]!.AsArray();
            for (var i = required.Count - 1; i >= 0; i--)
                if (required[i]?.ToString() == name) required.RemoveAt(i);
        }
    }

    private const string SelectedExpectationsSemantics = "Technical setup, handle and result-shape assumptions in selected expectedOutputs are provisional, not additional original obligations. Resolve them against exact contracts and reselect unsupported mechanisms. Preserve original business results and cleanup, and concrete task objectives and literals.";

    internal static string DraftSemantics(PlanningSession state, PlanningConstructionStep step)
    {
        var scope = new TaskPlanSymbols(state.Plan ?? new TaskPlan()).Scopes.SingleOrDefault(s => s.Path == step.Scope);
        var hints = new List<string> { ReusableGroupSemantics, SelectedExpectationsSemantics };
        if (SupportsBusinessFragment(step.Kind))
            hints.Add("Build one coherent business block with its related value/transform helpers and selected operations. The captured maxTasksPerDraft is shared by this entire extension. Newly supplied complete control/group bodies close automatically, including empty optional branches; no closeScopes bookkeeping is needed for them. continueInScope retains its exact original or block-owned scope and ancestors when this implementation cursor continues; null finishes only this cursor. completeBlock explicitly claims that the retained business objective, expected results and success criteria are fulfilled across all its accepted fragments. Keep completeBlock false for technical prerequisites, helper computations and incomplete contributions, including a finished technical scope. A completed block cannot retain continueInScope. Evidenced empty prerequisite openings remain open. A continuation does not close original requirements or discharge consumers. Existing boundaries still close explicitly; unfinished containers expose no usable result.");
        if (step.Kind is not ("group" or "choice" or "close"))
        {
            hints.Add("dependsOn names preceding same-scope tasks only; data bindings already imply dependencies and ancestor values use captures. requires is a Boolean assertion: false fails.");
            hints.Add("always contains cleanup on completion or failure; preserve available payloads before nested cleanup. present tests preceding local or lexical-ancestor task completion, not caller inputs or field nullability. For a null guard, compare the exact input, output or field with null.");
        }
        if (step.Scope != "/root")
            hints.Add("Later tasks in the same scope can bind ordinary task results directly; no scope export is needed. outputs export values across the enclosing task/group boundary; /root outputs are public results. Outside consumers cannot bind hidden child tasks directly. Open containers expose no consumable results.");
        if (step.Kind == "value") hints.Add(ValueSemantics);
        if (step.Kind == "transform")
            hints.Add("transform resultType is an object of named output ports; output.port selects that field's type, not the whole result object. extract preserves observed facts; interpret uses its objective as the runtime LLM instruction, inputs as data and resultType as the output contract, without a separate expression. Neither executes external effects.");
        if (step.Kind == "operation")
            hints.Add("editableInputs is only the inspected subset. Retrieve the exact schema branches needed for this business action together. " +
                EvidenceBatchGuidance(state) + (PlanningTokenThresholds.Advisory(state.Request)
                    ? " Project input targets only produce host warnings; actual provider limits still apply."
                    : " The complete follow-up must fit its captured input ceiling.") +
                " Retrieval expands binding grammar. [] does not mean no arguments are needed.");
        if (step.Kind == "foreach")
        {
            hints.Add("foreach.maxItems is the TOTAL input-item ceiling; excess fails, it never samples or truncates. maxConcurrency limits simultaneous workers, not total items. Neither bound guarantees a nonempty collection.");
            hints.Add("Use a finite supported capacity; the TaskPlan default maxItems=" + new PlanTask().MaxItems.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". Assess conflicts with explicit original bounds; unspecified capacity remains unverified. Runtime ceilings still apply.");
        }
        if (step.Kind == "foreach" || scope?.Owner?.Kind == "foreach")
            hints.Add("Each foreach body output becomes an array on the enclosing task, preserving input order and repetitions; array-valued outputs remain nested. Within the body, item and index refer to the current iteration.");
        if (step.Kind == "conditional" || scope?.Owner?.Kind == "conditional")
            hints.Add("conditional chooses body or otherwise; both alternatives must export matching names, types, nullability and requiredness. Guard nullable values on the consuming path.");
        if (step.Kind == "parallel" || scope?.Owner?.Kind == "parallel")
            hints.Add("Parallel sibling branches are independent and their exported names must be distinct; maxConcurrency limits simultaneous branches.");
        if (step.Kind == "group" || scope is { Parent: null } && step.Scope != "/root")
            hints.Add("A reusable group has isolated named inputs; its body cannot capture caller inputs or tasks. Close its body before selecting a call.");
        if (step.GroupId is not null)
            hints.Add("Bind the selected closed group's declared input names; its explicit exports are the call's output ports.");
        if (step.Kind == "close")
            hints.Add("A close step may finish a scope while broader requirements remain open, or confirm satisfied closeRequirementIds with empty closeScopes. Withdraw premature selected closeRequirementIds via reselectStep, preserving scope and requirementIds; an unchanged draft preserves the claim. Add needed final exports without rewriting accepted work. Close children before their parent, and root only when all scopes and original requirements are complete, with complete:true.");
        return string.Join(" ", hints);
    }

    private static JsonObject EmptyArray() => PlanningSchemas.Array(PlanningSchemas.Type("string"), 0, 0);
}
