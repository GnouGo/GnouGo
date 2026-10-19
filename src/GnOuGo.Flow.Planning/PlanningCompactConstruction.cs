using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal const string IntentPurpose = "construction_intent", SelectPurpose = "construction_select", DiscoveryPurpose = "construction_discovery";
    internal const string IndexInstructions = """
        Index this exact original intent page. Divide its text into consecutive verbatim clauses whose concatenation equals the entire page, including whitespace. Every clause is either a business requirement or an applicable global constraint. Do not summarize, omit, invent, or plan tasks. Keep original order and text. Declare only caller inputs and public result interfaces explicitly supported by this page; a requested external effect need not invent a public output. Other original pages and answers are retained independently. No capability selection or workflow outline.
        """;
    internal const string SelectInstructions = """
        Choose one immediate business substep from available producers toward consumers, using the original requirements. Do not plan future tasks or a workflow outline. No executable task payload. Select an open scope, task kind, exact typed imports, dependencies and relevant requirement IDs. expectedOutputs describes this step's internal results or interfaces. outputNames selects only declared PUBLIC workflow results shown in publicOutputs; use [] for internal work, group definitions and child-scope exports. Never put an input, task, group or internal port name there. For a call select one closed groupId; otherwise null. Construct missing prerequisites before consumers. closeRequirementIds claims full satisfaction by this step plus accepted work; contributions alone do not close requirements. Scope closure, exports, cleanup, groups and choices are valid steps. Discover operations after selecting the step. Return exactly one step, evidenceRequests or clarifications. Use the issued next-page addresses for omitted requirements/interfaces/public outputs. Evidence grants no edit or execution authority.
        """;
    internal const string DiscoveryInstructions = """
        Select capabilities only for the retained immediate business step and its original requirements. Operation descriptions are navigation, not exact contracts or execution authority. Choose discovered operation IDs, request at most two source metadata pages using this step's objective, request exact schema/description evidence, or clarify a material business fact. Return exactly one action. Previously inspected contracts remain stored but do not belong to this step unless selected again. Do not discover future workflow operations or replan the workflow.
        """;
    internal const string DraftInstructions = """
        Implement only the selected immediate business substep in the issued scope and task kind, using the selected exact contract views and typed imports. Extend the accepted TaskPlan; never rewrite accepted work. Preserve original requirements, constraints, identities, order, complete observations and cleanup. References must resolve now; missing prerequisites require evidence or clarification, never fabricated inputs or external work simulated by a transform. The schema permits only this step's task kind. New containers open empty child scopes for later steps. Close scopes only after their children and exports are complete. requirementIds associates this fragment with the selected original requirements; requirement closure is host-issued separately. Return one action. Evidence is read-only and grants no permissions. Do not output JavaScript, YAML or a workflow outline.
        """;

    internal static PlanningCompactConstructionState State(PlanningSession state) => state.Construction!.Compact
        ?? throw new PlanningConflictException("Compact construction has no retained presentation state.");

    internal static void InitializeSources(PlanningSession state)
    {
        var compact = State(state);
        if (compact.Sources.Count == 0) AddPages("request", state.Request.Prompt);
        var retainedPrompt = string.Concat(compact.Sources.Where(s => s.Kind is "page" or "revision_page").Select(s => s.Text));
        if (retainedPrompt != state.Request.Prompt)
        {
            if (!state.Request.Prompt.StartsWith(retainedPrompt, StringComparison.Ordinal))
                throw new PlanningConflictException("The original intent source changed outside an explicit revision.");
            AddPages("revision:" + PlanningGraphCompiler.Fingerprint(retainedPrompt), state.Request.Prompt[retainedPrompt.Length..]);
        }
        var answers = state.AnswerHistory ?? [];
        for (var i = compact.IndexedAnswers; i < answers.Count; i++)
        {
            var batch = answers[i];
            foreach (var answer in batch.Answers)
            {
                var question = batch.Questions.Single(q => q.Id == answer.QuestionId);
                var value = answer.Text ?? question.Alternatives.Single(a => a.Id == answer.AlternativeId).Description;
                AddPages("answer:" + batch.Revision + ":" + answer.QuestionId, question.Question + "\nAnswer: " + value);
            }
        }
        compact.IndexedAnswers = answers.Count;
        void AddPages(string prefix, string text)
        {
            // Exact spans are retained and bounded after both JSON escaping layers.
            for (var start = 0; start < text.Length;)
            {
                var count = OriginalPageLength(text, start);
                var original = text.Substring(start, count);
                compact.Sources.Add(new() { Id = prefix + ":" + start, Version = PlanningGraphCompiler.Fingerprint(original), Text = original,
                    Kind = prefix.StartsWith("answer:", StringComparison.Ordinal) ? "answer_page" :
                        prefix.StartsWith("revision:", StringComparison.Ordinal) ? "revision_page" : "page" });
                start += count;
            }
        }
    }

    internal static int OriginalPageLength(string text, int start, int maximumCharacters = 1000, int maximumEscapedBytes = 1024)
    {
        if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "An exact original page cannot start inside a Unicode character.");
        var low = 1; var high = Math.Min(maximumCharacters, text.Length - start); var best = 0;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var count = middle;
            if (start + count < text.Length && char.IsHighSurrogate(text[start + count - 1]) && char.IsLowSurrogate(text[start + count])) count--;
            // Source text is a JSON string in context, itself a string in the request.
            var encoded = JsonEncodedText.Encode(text.AsSpan(start, count)).ToString();
            var bytes = JsonEncodedText.Encode(encoded).EncodedUtf8Bytes.Length + 12;
            if (bytes <= maximumEscapedBytes) { best = Math.Max(best, count); low = middle + 1; }
            else high = middle - 1;
        }
        if (best == 0 && start < text.Length)
            PlanningConstruction.Fail("CONSTRUCTION_SOURCE_LIMIT", "/sources", "An original source character cannot fit the bounded evidence page.");
        return best;
    }

    internal static JsonObject IntentSchema(PlanningConstructionSource source)
    {
        var type = new JsonObject { ["anyOf"] = new JsonArray(
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("string", "number", "integer", "boolean", "any")), ("nullable", PlanningSchemas.Type("boolean"))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("string")), ("nullable", PlanningSchemas.Type("boolean")),
                ("enum", PlanningSchemas.Array(Text(1000), 1, 256))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("array")), ("nullable", PlanningSchemas.Type("boolean")), ("items", PlanningSchemas.Ref("type")),
                ("minItems", PlanningSchemas.Nullable(PlanningSchemas.Integer(0, int.MaxValue))),
                ("maxItems", PlanningSchemas.Nullable(PlanningSchemas.Integer(0, int.MaxValue)))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("object")), ("nullable", PlanningSchemas.Type("boolean")),
                ("fields", PlanningSchemas.Array(PlanningSchemas.Ref("input"), 0, 32)))) };
        var input = PlanningSchemas.Object(("name", Text(96)), ("type", PlanningSchemas.Ref("type")), ("required", PlanningSchemas.Type("boolean")));
        var withDefault = input.DeepClone().AsObject();
        withDefault["properties"]!["default"] = PlanningSchemas.Ref("literal");
        withDefault["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("default"));
        var literal = new JsonObject { ["anyOf"] = new JsonArray(
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("null"))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("string")), ("text", Text(1000))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("number")), ("number", PlanningSchemas.Type("number"))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("boolean")), ("boolean", PlanningSchemas.Type("boolean"))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("array")), ("items", PlanningSchemas.Array(PlanningSchemas.Ref("literal"), 0, 256))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("object")), ("members", PlanningSchemas.Array(
                PlanningSchemas.Object(("name", Text(96)), ("value", PlanningSchemas.Ref("literal"))), 0, 32)))) };
        var schema = PlanningSchemas.Object(("sourceId", PlanningSchemas.Enum(source.Id)),
            ("clauses", PlanningSchemas.Array(PlanningSchemas.Object(("text", Text(1200)), ("kind", PlanningSchemas.Enum("requirement", "global"))), 1, 16)),
            ("inputs", PlanningSchemas.Array(PlanningSchemas.Ref("input"), 0, 16)), ("outputs", PlanningSchemas.Array(PlanningSchemas.Ref("output"), 0, 16)));
        schema["$defs"] = new JsonObject { ["type"] = type, ["literal"] = literal, ["output"] = input.DeepClone(),
            ["input"] = new JsonObject { ["anyOf"] = new JsonArray(input, withDefault) } };
        return schema;
    }

    internal static JsonObject SelectionSchema(PlanningSession state)
    {
        var replacement = State(state).ReplacementScope;
        var outputs = OutputPage(state).Select(o => o.Name).ToArray();
        var step = PlanningSchemas.Object(("scope", replacement is null ? Text(192) : PlanningSchemas.Enum(replacement)),
            ("kind", PlanningSchemas.Enum("operation", "value", "transform", "sequence", "conditional", "parallel", "foreach", "call", "close", "group", "choice")),
            ("objective", Text(512)), ("requirementIds", Strings(1, 4)), ("inputs", PlanningSchemas.Array(PlanningSchemas.Ref("import"), 0, 8)),
            ("groupId", PlanningSchemas.Nullable(Text(96))),
            ("dependencyTaskIds", Strings(0, 8)), ("expectedOutputs", Strings(0, 4)),
            ("closeRequirementIds", Strings(0, 4)), ("outputNames", outputs.Length == 0
                ? PlanningSchemas.Array(Text(256), 0, 0) : PlanningSchemas.Array(PlanningSchemas.Enum(outputs), 0, 8)));
        var schema = PlanningSchemas.Object(("baseHash", PlanningSchemas.Enum(PlanningConstruction.Hash(state.Plan))),
            ("step", PlanningSchemas.Nullable(step)), ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(Text(2048), 1, 4))),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications())));
        schema["$defs"] = new JsonObject { ["import"] = new JsonObject { ["anyOf"] = new JsonArray(
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("output")), ("source", Text(96)), ("port", PlanningSchemas.Nullable(Text(96)))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("input", "choice", "present")), ("source", Text(96))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("item", "index"))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("field")), ("port", Text(96)),
                ("items", PlanningSchemas.Array(PlanningSchemas.Ref("import"), 1, 1)))) } };
        return schema;
    }

    internal static JsonObject DiscoverySchema(PlanningSession state) => PlanningSchemas.Object(
        ("operationIds", PlanningSchemas.Nullable(Strings(1, 2))),
        ("discoveryRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Object(("sourceId", Text(192)),
            ("cursor", PlanningSchemas.Nullable(Text(512))), ("query", PlanningSchemas.Nullable(PlanningSchemas.Enum(State(state).CurrentStep!.Objective))),
            ("operationIds", PlanningSchemas.Type("null"))), 1, 2))),
        ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(Text(2048), 1, 4))),
        ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications())));

    private static JsonObject Text(int maximum) => new() { ["type"] = "string", ["maxLength"] = maximum };
    private static JsonObject Strings(int minimum, int maximum) => PlanningSchemas.Array(Text(256), minimum, maximum);

    internal static LLMRequest Request(string instructions, JsonObject context, JsonObject schema, string purpose)
    {
        schema["title"] = purpose;
        return new() { Prompt = instructions + "\n" + PlanningJsonTransport.Prompt(context),
            StructuredOutputSchema = PlanningContractValidation.ProjectStructuredOutputSchema(schema) };
    }

    internal static JsonObject SelectionContext(PlanningSession state)
    {
        var compact = State(state);
        var scopeOffset = ScopeOffset(state);
        var remaining = state.Construction!.Requirements.Where(r => r.Status != "closed").Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var selected = compact.RequirementPageRequested
            ? state.Requirements!.Outcomes.Skip(compact.RequirementOffset).Take(3).ToArray()
            : state.Requirements!.Outcomes.Where(r => remaining.Contains(r.Id)).Take(3).ToArray();
        if (selected.Length == 0 && remaining.Count > 0) selected = state.Requirements.Outcomes.Where(r => remaining.Contains(r.Id)).Take(3).ToArray();
        if (selected.Length == 0) selected = state.Requirements.Outcomes.Skip(compact.RequirementOffset).Take(3).ToArray();
        if (selected.Length == 0) selected = state.Requirements.Outcomes.Take(3).ToArray();
        var lastRequirement = selected.Length == 0 ? -1 : state.Requirements.Outcomes.FindIndex(r => r.Id == selected[^1].Id);
        return new JsonObject
        {
            ["baseHash"] = PlanningConstruction.Hash(state.Plan),
            ["requirements"] = RequirementNodes(state, selected.Select(r => r.Id)),
            ["requirementCount"] = remaining.Count, ["requirementPage"] = compact.RequirementOffset,
            ["originalRequirementCount"] = state.Requirements.Outcomes.Count,
            ["nextRequirements"] = lastRequirement + 1 < state.Requirements.Outcomes.Count ? "page:requirements:" + (lastRequirement + 1) : null,
            ["requirementsDirectory"] = "page:requirements:0",
            ["globalConstraints"] = Sources(state, compact.GlobalSourceIds), ["instructions"] = state.Request.Policy.Instructions,
            ["frontier"] = new JsonArray(state.Construction.OpenScopes.Skip(scopeOffset).Take(8).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            ["frontierCount"] = state.Construction.OpenScopes.Count,
            ["nextScopes"] = scopeOffset + 8 < state.Construction.OpenScopes.Count ? "page:scopes:" + (scopeOffset + 8) : null,
            ["scopes"] = ScopeDirectory(state),
            ["interfaces"] = InterfaceDirectory(state, null, compact.InterfaceOffset),
            ["nextInterfaces"] = NextInterfacePage(state, compact.InterfaceOffset),
            ["publicOutputs"] = OutputDirectory(state),
            ["evidence"] = state.Construction.Evidence?.DeepClone(),
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics),
            ["rebuild"] = RebuildDirectory(state),
            ["reselection"] = compact.ReplacementScope is null ? null : new JsonObject { ["scope"] = compact.ReplacementScope,
                ["requirementIds"] = new JsonArray(compact.ReplacementRequirements!.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["reason"] = compact.RejectedSteps.Last()["reason"]?.DeepClone(), ["evidence"] = "rejected:" + (compact.RejectedSteps.Count - 1) },
            ["budget"] = new JsonObject { ["remainingCalls"] = PlanningModelCalls.RemainingCalls(state) }
        };
    }

    private static IEnumerable<TaskInput> OutputPage(PlanningSession state) => (state.Requirements?.Outputs ?? []).Skip(State(state).OutputOffset).Take(8);

    internal static JsonObject OutputDirectory(PlanningSession state)
    {
        var offset = State(state).OutputOffset;
        var count = state.Requirements?.Outputs?.Count ?? 0;
        return new JsonObject { ["offset"] = offset, ["total"] = count,
            ["items"] = new JsonArray(OutputPage(state).Select(o => (JsonNode)new JsonObject
                { ["name"] = o.Name, ["typeKind"] = o.Type.Kind, ["evidence"] = "output:" + o.Name }).ToArray()),
            ["next"] = offset + 8 < count ? "page:outputs:" + (offset + 8) : null };
    }

    private static string? NextInterfacePage(PlanningSession state, int offset)
    {
        var plan = state.Construction?.Candidate?.Plan ?? state.Plan;
        var sizes = new List<(int Count, int Page)> { ((plan?.Inputs ?? state.Requirements?.Inputs ?? []).Count, 8) };
        if (plan is not null)
        {
            sizes.Add((new TaskPlanSymbols(plan).Tasks.Count, 8));
            sizes.Add((plan.Choices.Count, 4)); sizes.Add((plan.Groups.Count, 4));
        }
        var next = sizes.Where(s => offset + s.Page < s.Count).Select(s => offset + s.Page).DefaultIfEmpty(-1).Min();
        return next < 0 ? null : "page:interfaces:" + next;
    }

    internal static JsonObject Context(PlanningSession state, bool review = false, bool intermediate = false)
    {
        var construction = state.Construction!; var compact = State(state);
        var step = compact.CurrentStep ?? throw new PlanningConflictException("Select a business substep before presenting a draft.");
        var draft = construction.Candidate;
        var context = new JsonObject
        {
            ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["candidateHash"] = draft?.Hash,
            ["step"] = JsonSerializer.SerializeToNode(step, PlanningJsonContext.Default.PlanningConstructionStep),
            ["requirements"] = RequirementNodes(state, step.RequirementIds),
            ["commitments"] = new JsonObject(compact.PendingCommitments.Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                JsonSerializer.SerializeToNode(p.Value, PlanningJsonContext.Default.ListPlanningCommitment)))),
            ["globalConstraints"] = Sources(state, compact.GlobalSourceIds), ["instructions"] = state.Request.Policy.Instructions,
            ["interfaces"] = ImportedInterfaces(state, step),
            ["operations"] = ContractViews(state, review),
            ["evidence"] = construction.Evidence?.DeepClone(),
            ["draft"] = draft is null ? null : PlanningJsonTransport.TaskPlanPart(PlanningConstruction.Node(draft.Extension, PlanningJsonContext.Default.PlanningConstructionExtension)),
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(draft?.Diagnostics ?? state.Diagnostics),
            ["rebuild"] = RebuildDirectory(state),
            ["reviewScope"] = intermediate ? "junction" : review ? "draft" : "extension"
        };
        if (step.OutputNames.Count > 0)
            context["outputs"] = PlanningConstruction.Node(state.Requirements!.Outputs!.Where(o => step.OutputNames.Contains(o.Name, StringComparer.Ordinal)).ToList(), PlanningJsonContext.Default.ListTaskInput);
        if (step.GroupId is { } groupId) context["group"] = GroupInterface(state, groupId);
        if (ScopeGroup(state, step.Scope) is { } scopeGroup) context["scopeGroup"] = GroupInterface(state, scopeGroup.Id);
        var choiceIds = step.Inputs.SelectMany(TaskPlanCompiler.Values).Where(v => v.Kind == "choice").Select(v => v.Source)
            .Concat(draft?.Extension.Choices.Select(c => c.Id) ?? []).ToHashSet(StringComparer.Ordinal);
        if (choiceIds.Count > 0)
            context["choices"] = PlanningConstruction.Node(((draft?.Plan ?? state.Plan)?.Choices ?? []).Where(c => choiceIds.Contains(c.Id)).ToList(), PlanningJsonContext.Default.ListPlanningChoice);
        if (review && draft is not null)
        {
            var related = construction.Checkpoints.Where(c => c.TaskIds.Any(draft.ImportedTaskIds.Contains) ||
                intermediate && c.RequirementIds.Any(step.CloseRequirementIds.Contains)).ToArray();
            context["checkpointCount"] = related.Length;
            context["checkpointEvidence"] = "page:checkpoints:0";
            context["checkpoints"] = new JsonArray(related.TakeLast(4).Select(c => (JsonNode)new JsonObject { ["sequence"] = c.Sequence,
                ["hash"] = c.PlanHash, ["requirementIds"] = new JsonArray(c.RequirementIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["tasks"] = new JsonArray(c.TaskIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) }).ToArray());
        }
        return context;
    }

    internal static JsonObject DiscoveryContext(PlanningSession state)
    {
        var step = State(state).CurrentStep!;
        var evidence = state.Construction!.Evidence;
        var supplied = evidence is { Count: > 0 };
        return new JsonObject
        {
            ["step"] = JsonSerializer.SerializeToNode(step, PlanningJsonContext.Default.PlanningConstructionStep),
            ["requirements"] = RequirementNodes(state, step.RequirementIds),
            ["sources"] = supplied ? null : SourceDescriptors(state),
            ["descriptors"] = supplied ? null : ContractDescriptorPage(state),
            ["directoryEvidence"] = supplied ? new JsonObject { ["sources"] = "page:sources:0", ["operations"] = "page:operations:0" } : null,
            ["evidence"] = evidence?.DeepClone(),
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics),
            ["pages"] = new JsonArray(state.Discovery.Pages.Where(p => p.Query == step.Objective).Select(p => (JsonNode)new JsonObject
                { ["sourceId"] = p.SourceId, ["cursor"] = p.Cursor, ["nextCursor"] = p.NextCursor, ["unavailable"] = p.UnavailableReason }).ToArray())
        };
    }

    internal static JsonArray RequirementNodes(PlanningSession state, IEnumerable<string> ids)
    {
        var compact = State(state); var selected = ids.ToHashSet(StringComparer.Ordinal);
        return new JsonArray(state.Requirements!.Outcomes.Where(r => selected.Contains(r.Id)).Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id, ["original"] = r.Description,
            ["status"] = state.Construction!.Requirements.Single(s => s.Id == r.Id).Status,
            ["sources"] = new JsonArray((compact.RequirementSources.GetValueOrDefault(r.Id) ?? []).Select(id =>
                (JsonNode)new JsonObject { ["id"] = id, ["version"] = compact.Sources.Single(s => s.Id == id).Version }).ToArray()),
            ["commitments"] = JsonSerializer.SerializeToNode(r.Commitments, PlanningJsonContext.Default.ListPlanningCommitment)
        }).ToArray());
    }

    private static JsonArray Sources(PlanningSession state, IEnumerable<string> ids) => new(ids.Select(id => State(state).Sources.Single(s => s.Id == id))
        .Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["version"] = s.Version, ["text"] = s.Text }).ToArray());

    private static int ScopeOffset(PlanningSession state) => state.Construction!.ScopeCursor < state.Construction.OpenScopes.Count
        ? state.Construction.ScopeCursor : 0;

    private static JsonArray ScopeDirectory(PlanningSession state)
    {
        var symbols = new TaskPlanSymbols(state.Plan ?? new TaskPlan());
        return new JsonArray(state.Construction!.OpenScopes.Skip(ScopeOffset(state)).Take(8).Select(path => symbols.Scopes.Single(s => s.Path == path))
            .Select(s =>
            {
                var group = state.Plan?.Groups.SingleOrDefault(g => s.Path == "/groups/" + g.Id + "/body");
                var opening = group is null ? null : GroupOpening(state, group.Id);
                var objective = s.Owner?.Objective ?? opening?.Objective;
                var description = objective is null ? null : NavigationDescription(objective);
                return (JsonNode)new JsonObject { ["path"] = s.Path, ["owner"] = s.Owner?.Id, ["kind"] = group is null ? s.Owner?.Kind : "group",
                ["objective"] = description, ["objectiveComplete"] = description == objective,
                ["requirementIds"] = opening is null ? null : new JsonArray(opening.RequirementIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["iterationInputs"] = s.Owner?.Kind == "foreach" ? "item and index" : null,
                ["inputs"] = group is null ? null : new JsonArray(group.Inputs.Take(8).Select(i => (JsonNode)new JsonObject
                    { ["input"] = i.Name, ["typeKind"] = i.Type.Kind, ["evidence"] = "group:" + group.Id }).ToArray()),
                ["evidence"] = group is null ? "scope:" + s.Path : "group:" + group.Id };
            }).ToArray());
    }

    private static JsonObject RebuildDirectory(PlanningSession state)
    {
        var construction = state.Construction!;
        var existing = new TaskPlanSymbols(state.Plan ?? new TaskPlan()).Tasks.Keys.ToHashSet(StringComparer.Ordinal);
        var remaining = construction.RebuildTasks.Where(t => !existing.Contains(t.Id)).ToArray();
        return new JsonObject { ["remaining"] = remaining.Length,
            ["items"] = new JsonArray(remaining.Take(4).Select(t =>
            {
                var placement = construction.RebuildScopes.Single(s => s.Tasks.Contains(t.Id, StringComparer.Ordinal) || s.Always.Contains(t.Id, StringComparer.Ordinal));
                var original = construction.Invalidations.Last(i => i.Tasks.Any(old => old.Id == t.Id));
                return (JsonNode)new JsonObject { ["id"] = t.Id, ["kind"] = t.Kind, ["objective"] = t.Objective,
                    ["scope"] = placement.Path, ["phase"] = placement.Always.Contains(t.Id, StringComparer.Ordinal) ? "always" : "tasks",
                    ["evidence"] = "original:" + original.Sequence + ":" + t.Id };
            }).ToArray()), ["invalidationCount"] = construction.Invalidations.Count };
    }

    internal static JsonArray InterfaceDirectory(PlanningSession state, IEnumerable<string>? selected, int offset)
    {
        var plan = state.Construction?.Candidate?.Plan ?? state.Plan;
        var result = new JsonArray();
        if (plan is null)
        {
            foreach (var input in (state.Requirements?.Inputs ?? []).Skip(offset).Take(8))
                result.Add((JsonNode)new JsonObject { ["input"] = input.Name, ["typeKind"] = input.Type.Kind, ["evidence"] = "input:" + input.Name });
            return result;
        }
        var symbols = new TaskPlanSymbols(plan);
        var ids = selected?.ToHashSet(StringComparer.Ordinal);
        var tasks = ids is null ? symbols.Tasks.Values.Skip(offset).Take(8) : symbols.Tasks.Values.Where(t => ids.Contains(t.Task.Id));
        foreach (var site in tasks)
        {
            var task = site.Task;
            var open = symbols.Scopes.Any(s => s.Owner?.Id == task.Id && state.Construction!.OpenScopes.Contains(s.Path, StringComparer.Ordinal));
            if (open) continue;
            result.Add((JsonNode)new JsonObject { ["task"] = task.Id, ["scope"] = site.Scope.Path, ["kind"] = task.Kind,
                ["objective"] = task.Objective, ["ports"] = new JsonArray(PlanningRepairContext.Ports(task, state).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["evidence"] = "task:" + task.Id });
        }
        foreach (var input in plan.Inputs.Skip(offset).Take(8))
            result.Add((JsonNode)new JsonObject { ["input"] = input.Name, ["typeKind"] = input.Type.Kind, ["evidence"] = "input:" + input.Name });
        foreach (var choice in plan.Choices.Skip(offset).Take(4))
            result.Add((JsonNode)new JsonObject { ["choice"] = choice.Id, ["typeKind"] = choice.Type.Kind,
                ["question"] = choice.Question[..Math.Min(192, choice.Question.Length)], ["questionComplete"] = choice.Question.Length <= 192,
                ["evidence"] = "choice:" + choice.Id });
        foreach (var group in plan.Groups.Skip(offset).Take(4))
        {
            var open = state.Construction!.OpenScopes.Any(s => s == "/groups/" + group.Id + "/body" || s.StartsWith("/groups/" + group.Id + "/body/", StringComparison.Ordinal));
            result.Add((JsonNode)new JsonObject { ["group"] = group.Id, ["open"] = open,
                ["inputs"] = new JsonArray(group.Inputs.Take(8).Select(i => (JsonNode?)JsonValue.Create(i.Name)).ToArray()), ["inputCount"] = group.Inputs.Count,
                ["outputs"] = open ? null : new JsonArray(group.Body.Outputs.Take(8).Select(o => (JsonNode?)JsonValue.Create(o.Name)).ToArray()),
                ["outputCount"] = open ? null : JsonValue.Create(group.Body.Outputs.Count), ["evidence"] = "group:" + group.Id });
        }
        return result;
    }

    internal static JsonObject? GroupInterface(PlanningSession state, string id)
    {
        var plan = state.Plan;
        var group = plan?.Groups.SingleOrDefault(g => g.Id == id);
        if (group is null) return null;
        var path = "/groups/" + id + "/body";
        var open = state.Construction!.OpenScopes.Any(s => s == path || s.StartsWith(path + "/", StringComparison.Ordinal));
        var result = new JsonObject { ["id"] = id, ["open"] = open,
            ["hash"] = PlanningGraphCompiler.Fingerprint(PlanningConstruction.Node(group, PlanningJsonContext.Default.TaskGroup)!.ToJsonString()),
            ["inputs"] = PlanningConstruction.Node(group.Inputs, PlanningJsonContext.Default.ListTaskInput), ["outputs"] = null };
        if (GroupOpening(state, id) is { } opening)
            result["intent"] = new JsonObject { ["objective"] = opening.Objective,
                ["expectedOutputs"] = new JsonArray(opening.ExpectedOutputs.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["requirementIds"] = new JsonArray(opening.RequirementIds.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
        if (open) return result;
        var probe = PlanningConstruction.Clone(plan!);
        foreach (var choice in probe.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
        var requested = group.Body.Outputs.Select(o => o.Value).ToArray();
        var validation = TaskPlanCompiler.ValidateConstruction(probe, state.Catalog!, state.Request,
            state.Construction.OpenScopes.ToHashSet(StringComparer.Ordinal), new Dictionary<string, IReadOnlyList<TaskValue>> { [path] = requested });
        if (!validation.Interfaces.TryGetValue(path, out var ports) || ports.Count != requested.Length)
            PlanningConstruction.Fail("CONSTRUCTION_GROUP_INTERFACE_INVALID", "/step/groupId", "The closed group's exact output interfaces must validate before selecting a call.");
        result["outputs"] = new JsonArray(group.Body.Outputs.Zip(ports!).Select(pair => (JsonNode)new JsonObject
            { ["name"] = pair.First.Name, ["schema"] = pair.Second.Schema.DeepClone() }).ToArray());
        return result;
    }

    internal static TaskGroup? ScopeGroup(PlanningSession state, string scope)
    {
        if (state.Plan is not { } plan) return null;
        var owner = new TaskPlanSymbols(plan).Scopes.SingleOrDefault(s => s.Path == scope);
        while (owner?.Parent is not null) owner = owner.Parent;
        return owner is null ? null : plan.Groups.SingleOrDefault(group => ReferenceEquals(group.Body, owner.Source));
    }

    private static PlanningConstructionStep? GroupOpening(PlanningSession state, string id) => state.Construction!.Checkpoints
        .LastOrDefault(c => c.InvalidatedTaskIds.Count == 0 && c.Step is not null &&
            c.Fragment["groups"] is JsonArray groups && groups.Any(group => group?.ToString() == id))?.Step;

    internal static JsonArray ImportedInterfaces(PlanningSession state, PlanningConstructionStep step)
    {
        if (step.Inputs.Count == 0) return [];
        var plan = PlanningConstruction.Clone(state.Plan ?? new TaskPlan { Inputs = state.Requirements!.Inputs! });
        foreach (var choice in plan.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
        var result = TaskPlanCompiler.ValidateConstruction(plan, state.Catalog!, state.Request,
            state.Construction!.OpenScopes.ToHashSet(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<TaskValue>> { [step.Scope] = step.Inputs });
        if (!result.Interfaces.TryGetValue(step.Scope, out var interfaces) || interfaces.Count != step.Inputs.Count)
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INPUT_INVALID", "/step/inputs", "Every selected input must have an available typed interface in this scope.");
        return new JsonArray(interfaces!.Select(i => (JsonNode)new JsonObject
        { ["value"] = PlanningConstruction.Node(i.Value, PlanningJsonContext.Default.TaskValue), ["schema"] = i.Schema.DeepClone() }).ToArray());
    }

    internal static void ValidateSelection(PlanningSession state, PlanningConstructionStep step)
    {
        var construction = state.Construction!;
        var compact = State(state);
        if (compact.ReplacementScope is { } fixedScope && (step.Scope != fixedScope ||
            !step.RequirementIds.ToHashSet(StringComparer.Ordinal).SetEquals(compact.ReplacementRequirements!)))
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step", "Reselection preserves the original tentative scope and requirement identities.");
        if (!construction.OpenScopes.Contains(step.Scope, StringComparer.Ordinal))
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/scope", "Select an existing open scope from the frontier or its issued evidence.");
        if (step.RequirementIds.Count == 0 || step.RequirementIds.Distinct(StringComparer.Ordinal).Count() != step.RequirementIds.Count ||
            step.RequirementIds.Any(id => construction.Requirements.All(r => r.Id != id)))
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/requirementIds", "Select distinct original requirement IDs. Original pages begin at page:requirements:0.");
        if (step.CloseRequirementIds.Distinct(StringComparer.Ordinal).Count() != step.CloseRequirementIds.Count ||
            step.CloseRequirementIds.Any(id => !step.RequirementIds.Contains(id, StringComparer.Ordinal)))
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/closeRequirementIds", "Close only distinct requirements selected for this step, once their complete original obligation is satisfied.");
        if (step.OutputNames.Distinct(StringComparer.Ordinal).Count() != step.OutputNames.Count ||
            step.OutputNames.Any(name => state.Requirements!.Outputs!.All(o => o.Name != name)))
            PlanningConstruction.Fail("CONSTRUCTION_STEP_OUTPUT_INVALID", "/step/outputNames", "outputNames selects distinct declared public results only. Current page: " +
                new JsonArray(OutputPage(state).Select(o => (JsonNode?)JsonValue.Create(o.Name)).ToArray()).ToJsonString() +
                ". For internal work use outputNames:[] and describe its result in expectedOutputs. Public output pages begin at page:outputs:0.");
        if (step.Kind == "call" ? step.GroupId is null || GroupInterface(state, step.GroupId) is not { } group || group["open"]!.GetValue<bool>() : step.GroupId is not null)
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/groupId", "A call requires one existing closed reusable group; other task kinds do not select a group.");
        var symbols = new TaskPlanSymbols(state.Plan ?? new TaskPlan());
        step.InputTaskIds = step.Inputs.SelectMany(TaskPlanCompiler.Values).Where(v => v.Kind is "output" or "present" && v.Source is not null)
            .Select(v => v.Source!).Concat(step.DependencyTaskIds).Distinct(StringComparer.Ordinal).ToList();
        if (step.InputTaskIds.Any(id => !symbols.Tasks.ContainsKey(id)))
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/inputTaskIds", "Input task identities must already exist; construct prerequisites first.");
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        _ = ImportedInterfaces(state, step);
    }
}
