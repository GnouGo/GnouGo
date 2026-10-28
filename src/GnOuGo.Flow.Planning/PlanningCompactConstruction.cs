using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    // Keep room for the final business review throughout construction.
    internal static int RequiredReviewCalls(PlanningSession state)
    {
        if (state.Construction?.Candidate is not { } candidate) return 1;
        if (candidate.Extension.Complete) return candidate.BusinessReviewed ? 0 : 1;
        return HybridWorkflowPlanner.NeedsBusinessReview(state, candidate) && !candidate.BusinessReviewed ? 2 : 1;
    }

    internal const string TransformSemantics = "transform extract preserves observed facts; eligible interpret derives typed data from supplied inputs using the configured runtime LLM, without a utility MCP or separate expression. Operation metadata establishes contracts, not executed producer results. Obtain missing external facts through authorized observations. Neither mode invents external facts or performs external actions.";
    internal const string ReusableGroupSemantics = "group declares a named reusable subworkflow with isolated inputs and explicit exports; call invokes that closed group. A control's inline body is not a callable group, even when compilation creates an internal workflow for it. Objectives and labels do not change these executable kinds. At closure, assess the actual contributing tasks and bindings against the original structural obligations.";

    private static readonly string[] TaskKinds = ["operation", "value", "transform", "sequence", "conditional", "parallel", "foreach", "call", "close", "group", "choice"];
    internal const string IntentPurpose = "construction_intent", SelectPurpose = "construction_select", DiscoveryPurpose = "construction_discovery";
    private const string ContainerBoundaryGuidance = "Open a container only for needed control, reuse, cleanup or export ownership; append sequential work to an existing appropriate scope.";

    internal static string SelectGuidance(PlanningSession state) => $"""
        Select only the next needed business action from originals and available exact results; no outline or preparatory copies. Reuse accepted bindings and inspected contracts. Resume a ready retained consumer with its exact checkpoint/binding, otherwise resolve its missing prerequisite. A closed requirement remains closed until explicit invalidation.
        Keep the complete original requirement attached while implementing one immediate substep. Caller inputs bind at runtime; discovery needs their declared interfaces, not concrete values. Use kind:null when unknown capabilities determine implementation. An inspected offered operationIds contract can bind kind:operation directly. Local and structural kinds need no discovery when their semantics suffice.
        {ContainerBoundaryGuidance} Open containers/groups empty; outputNames and closeRequirementIds remain empty when their children must establish the result. Groups isolate declared inputs; controls capture ancestors. call requires a closed groupId. {ReusableGroupSemantics}
        {ValueSemantics} {TransformSemantics} Input/dependency hints resolve now. Prerequisites cite original evidence, not future task IDs. expectedOutputs are internal; outputNames selects public root results. {SelectedExpectationsSemantics}
        closeScopes:[] closes requirements only; [scope] closes that offered scope; non-close actions use null. Close only satisfied obligations. Return one action with other fields null: step, offered resumeStep, exact evidenceRequests, or clarification of a missing business fact. Evidence grants no execution or edit authority.
        """;

    internal static string DiscoveryGuidance(PlanningSession state) => $"""
        Resolve implementation of this immediate business action within its fixed scope and original requirements. Inspect relevant original descriptions/constraints; partial descriptions or uninspected alternatives do not establish missing behavior. Follow necessary exact continuations. Stop inspecting once suitable.
        Return one action with others null: binding, discoveryRequests, evidenceRequests, reselectStep or clarifications. binding selects a TaskPlan kind plus exact inspected operationIds or closed groupId; local work has no operations. closeScopes:[] means requirement-only closure, [scope] the offered boundary, otherwise null.
        {ContainerBoundaryGuidance} {ValueSemantics} {TransformSemantics} {SelectedExpectationsSemantics}
        The host resolves contracts and admits required constraints before drafting. Never invent setup, handles, results or permissions. Unavailable required data needs a prerequisite reselection; only missing business facts need clarification. Caller inputs bind at runtime.
        discoveryRequests uses issued source/cursor with query:null; host follows bounded continuations for this objective. evidenceRequests copies up to eight exact addresses, or an offered earlier description, and uses supplied evidence. Metadata grants no execution authority.
        """;

    internal static string DraftGuidance(PlanningSession state) => """
        Implement only the selected immediate substep, scope and task kind using exact contracts. Preserve accepted work, original constraints and complete observations. Input hints are not a whitelist: every actual binding resolves at its consumer and supplies the entity/value required by original contract meaning, not merely a compatible type. Never invent inputs, permissions or external results.
        New containers open empty. scopeInterface contains existing exports; extension.outputs adds exports, [] preserves them. Close complete children/exports only. requirementIds associates work without proving completion. Add actual tasks, groups, choices or exports, close a scope, or close an offered still-open requirement.
        Return one permitted action; other action fields null. Commitments accompany extension only. Evidence-only responses keep requirements, commitments and rollbackReason null. Reselect an unsuitable kind or premature claim, retrieve exact originals, or clarify missing business facts. No outline, JavaScript or YAML.
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
            string? continuation = null;
            for (var start = 0; start < text.Length;)
            {
                var (count, continues) = CoherentOriginalPageLength(text, start);
                var original = text.Substring(start, count);
                var id = prefix + ":" + start;
                compact.Sources.Add(new() { Id = id, Version = PlanningGraphCompiler.Fingerprint(original), Text = original, ContinuationOf = continuation,
                    Kind = prefix.StartsWith("answer:", StringComparison.Ordinal) ? "answer_page" :
                        prefix.StartsWith("revision:", StringComparison.Ordinal) ? "revision_page" : "page" });
                continuation = continues ? id : null;
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

    internal static JsonObject InterfaceTypeDefinitions()
    {
        var minimum = PlanningSchemas.Nullable(PlanningSchemas.Integer(0, int.MaxValue));
        minimum["description"] = "Explicit minimum array length from the original source; null when no lower bound is declared. Preserve explicit zero.";
        var maximum = PlanningSchemas.Nullable(PlanningSchemas.Integer(0, int.MaxValue));
        maximum["description"] = "Explicit maximum array length from the original source; null when no upper bound is declared. Zero permits only an empty array.";
        var type = new JsonObject { ["anyOf"] = new JsonArray(
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("string", "number", "integer", "boolean", "any")), ("nullable", PlanningSchemas.Type("boolean"))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("string")), ("nullable", PlanningSchemas.Type("boolean")),
                ("enum", PlanningSchemas.Array(Text(1000), 1, 256))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("array")), ("nullable", PlanningSchemas.Type("boolean")), ("items", PlanningSchemas.Ref("type")),
                ("minItems", minimum), ("maxItems", maximum)),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("object")), ("nullable", PlanningSchemas.Type("boolean")),
                ("fields", PlanningSchemas.Array(PlanningSchemas.Ref("input"), 0, 32)))) };
        var input = PlanningSchemas.Object(("name", Text(96)), ("type", PlanningSchemas.Ref("type")), ("required", PlanningSchemas.Type("boolean")));
        var literal = new JsonObject { ["anyOf"] = new JsonArray(
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("null"))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("string")), ("text", Text(1000))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("number")), ("number", PlanningSchemas.Type("number"))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("boolean")), ("boolean", PlanningSchemas.Type("boolean"))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("array")), ("items", PlanningSchemas.Array(PlanningSchemas.Ref("literal"), 0, 256))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("object")), ("members", PlanningSchemas.Array(
                PlanningSchemas.Object(("name", Text(96)), ("value", PlanningSchemas.Ref("literal"))), 0, 32)))) };
        var withDefault = input.DeepClone().AsObject();
        withDefault["properties"]!["default"] = PlanningSchemas.Nullable(PlanningSchemas.Ref("literal"));
        withDefault["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("default"));
        var types = type["anyOf"]!.AsArray();
        types[0]!["properties"]!["kind"] = PlanningSchemas.Enum("number", "integer", "boolean", "any");
        types[1]!["properties"]!["enum"] = PlanningSchemas.Nullable(types[1]!["properties"]!["enum"]!.DeepClone().AsObject());
        return new JsonObject { ["type"] = type, ["literal"] = literal, ["output"] = input, ["input"] = withDefault };
    }

    internal static JsonObject SelectionSchema(PlanningSession state, JsonObject? page = null)
    {
        var compact = State(state);
        var replacement = compact.ReplacementScope;
        var outputs = OutputPage(state).Select(o => o.Name).ToArray();
        var requirements = SelectionRequirementIds(state);
        var scopes = replacement is null
            ? (page?["scopes"] as JsonArray ?? ScopeDirectory(state)).Select(scope => scope!["path"]!.ToString()).ToArray() : null;
        var hasClosedGroup = state.Plan?.Groups.Any(group =>
            !state.Construction!.OpenScopes.Contains("/groups/" + group.Id + "/body", StringComparer.Ordinal)) == true;
        var completionOnly = CompletionOnly(state);
        var kinds = completionOnly ? ["close"] : TaskKinds.Where(kind => (kind != "call" || hasClosedGroup) && (kind != "operation")).ToArray();
        JsonObject RequirementIds(int minimum) => requirements is { Length: > 0 }
            ? PlanningSchemas.Array(PlanningSchemas.Enum(requirements), minimum, 4) : Strings(minimum, 4);
        var selectedRequirements = replacement is not null && requirements is { Length: > 0 }
            ? PlanningSchemas.Array(PlanningSchemas.Enum(requirements), requirements.Length, requirements.Length)
            : RequirementIds(1);
        var step = PlanningSchemas.Object(("scope", replacement is not null ? PlanningSchemas.Enum(replacement)
                : scopes is { Length: > 0 } ? PlanningSchemas.Enum(scopes) : Text(192)),
            ("kind", !completionOnly ? PlanningSchemas.Nullable(PlanningSchemas.Enum(kinds)) : PlanningSchemas.Enum(kinds)),
            ("objective", Text(512)), ("requirementIds", selectedRequirements), ("inputs", PlanningSchemas.Array(PlanningSchemas.Ref("import"), 0, 8)),
            ("groupId", hasClosedGroup ? PlanningSchemas.Nullable(Text(96)) : PlanningSchemas.Type("null")),
            ("dependencyTaskIds", Strings(0, 8)), ("expectedOutputs", Strings(0, 4)),
            ("closeRequirementIds", RequirementIds(0)), ("outputNames", outputs.Length == 0
                ? PlanningSchemas.Array(Text(256), 0, 0) : PlanningSchemas.Array(PlanningSchemas.Enum(outputs), 0, 8)));
        {
            var prerequisites = PrerequisiteAddresses(state, requirements ?? []).ToArray();
            step["properties"]!["prerequisites"] = prerequisites.Length == 0 ? PlanningSchemas.Type("null") :
                PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Object(
                    ("need", Text(256)), ("evidenceAddresses", PlanningSchemas.Array(PlanningSchemas.Enum(prerequisites), 1, 4))), 0, 4));
            step["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("prerequisites"));
            // Shared members are factored by Compact. A structural action cannot
            // accidentally bind a reusable target merely because it uses one later.
            var local = step.DeepClone().AsObject();
            local["properties"]!["kind"] = completionOnly ? PlanningSchemas.Enum("close") :
                PlanningSchemas.Nullable(PlanningSchemas.Enum(kinds.Where(kind => kind != "call").ToArray()));
            local["properties"]!["groupId"] = PlanningSchemas.Type("null");
            if (hasClosedGroup && !completionOnly)
            {
                step["properties"]!["kind"] = PlanningSchemas.Enum("call");
                step["properties"]!["groupId"] = Text(96);
                step = new JsonObject { ["anyOf"] = new JsonArray(local, step) };
            }
            else step = local;
        }
        {
            var branches = step["anyOf"] is JsonArray alternatives ? alternatives.OfType<JsonObject>().ToArray() : [step];
            var template = branches[0];
            var choices = new JsonArray();
            foreach (var branch in branches)
            {
                if (completionOnly) continue;
                var local = branch.DeepClone().AsObject();
                if (local["properties"]!["kind"]?["enum"] is not JsonArray callKinds || !callKinds.Any(kind => kind?.ToString() == "call"))
                    local["properties"]!["kind"] = PlanningSchemas.Nullable(PlanningSchemas.Enum(kinds.Where(kind => kind is not ("call" or "close")).ToArray()));
                AddSelectionFields(local, EmptyArray(), PlanningSchemas.Type("null"));
                choices.Add((JsonNode)local);
            }
            foreach (var scope in replacement is not null ? [replacement] : scopes ?? state.Construction!.OpenScopes.ToArray())
            {
                var close = template.DeepClone().AsObject();
                close["properties"]!["scope"] = PlanningSchemas.Enum(scope);
                close["properties"]!["kind"] = PlanningSchemas.Enum("close");
                var selected = new PlanningConstructionStep { Scope = scope, Kind = "close",
                    CloseRequirementIds = state.Construction!.Requirements.Where(requirement => requirement.Status != "closed").Select(requirement => requirement.Id).ToList() };
                var target = (scope != "/root" || selected.CloseRequirementIds.Count <= 4) && ClosurePresentation(state, selected).CloseScope
                    ? PlanningSchemas.Array(PlanningSchemas.Enum(scope), 0, 1) : EmptyArray();
                AddSelectionFields(close, EmptyArray(), target);
                choices.Add((JsonNode)close);
            }
            var known = page is null ? SelectionOperationIds(state) :
                new[] { page["knownOperations"] }.Concat((page["evidence"] as JsonObject ?? [])
                    .Where(item => item.Key.StartsWith("page:operations:", StringComparison.Ordinal)).Select(item => item.Value))
                    .SelectMany(value => value?["items"] as JsonArray ?? []).Select(item => item!["id"]!.ToString())
                    .Intersect(SelectionOperationIds(state), StringComparer.Ordinal).ToArray();
            if (!completionOnly && known.Length > 0)
            {
                var operation = template.DeepClone().AsObject();
                operation["properties"]!["kind"] = PlanningSchemas.Enum("operation");
                AddSelectionFields(operation, PlanningSchemas.Array(PlanningSchemas.Enum(known), 1, 2), PlanningSchemas.Type("null"));
                choices.Add((JsonNode)operation);
            }
            step = new JsonObject { ["anyOf"] = choices };

            static void AddSelectionFields(JsonObject branch, JsonObject operations, JsonObject closes)
            {
                branch["properties"]!["operationIds"] = operations;
                branch["properties"]!["closeScopes"] = closes;
                branch["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("operationIds"));
                branch["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("closeScopes"));
            }
        }
        var schema = PlanningSchemas.Object(("baseHash", PlanningSchemas.Enum(PlanningConstruction.Hash(state.Plan))),
            ("step", requirements is { Length: 0 } || scopes is { Length: 0 } ? PlanningSchemas.Type("null") : PlanningSchemas.Nullable(step)),
            ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(Text(2048), 1, 4))),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications())));
        schema["$defs"] = new JsonObject { ["import"] = new JsonObject { ["anyOf"] = new JsonArray(
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("output")), ("source", Text(96)), ("port", PlanningSchemas.Nullable(Text(96)))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("input", "choice", "present")), ("source", Text(96))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("item", "index"))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("field")), ("port", Text(96)),
                ("items", PlanningSchemas.Array(PlanningSchemas.Ref("import"), 1, 1)))) } };
        if ((state.Plan is null || new TaskPlanSymbols(state.Plan).Tasks.Count == 0))
        {
            var imports = schema["$defs"]!["import"]!["anyOf"]!.AsArray();
            imports.RemoveAt(0);
            imports[0]!["properties"]!["kind"] = PlanningSchemas.Enum("input", "choice");
        }
        {
            var imports = schema["$defs"]!["import"]!["anyOf"]!.AsArray();
            var references = imports.Single(import => import?["properties"]?["kind"]?["enum"] is JsonArray kinds &&
                kinds.Any(kind => kind?.ToString() == "input"))!;
            references["properties"]!["kind"] = PlanningSchemas.Enum(references["properties"]!["kind"]!["enum"]!
                .AsArray().Select(kind => kind!.ToString()).Where(kind => kind != "input").ToArray());
            var inputScopes = replacement is not null ? [replacement] : scopes ?? state.Construction!.OpenScopes.ToArray();
            var inputNames = inputScopes
                .SelectMany(scope => ScopeGroup(state, scope)?.Inputs ?? state.Plan?.Inputs ?? state.Requirements!.Inputs!)
                .Select(input => input.Name).Distinct(StringComparer.Ordinal).ToArray();
            if (inputNames.Length > 0)
            {
                var source = PlanningSchemas.Enum(inputNames); source["maxLength"] = 96;
                imports.Insert(0, PlanningSchemas.Object(("kind", PlanningSchemas.Enum("input")), ("source", source)));
            }
        }
        if (ContinuationContext(state) is { } continuation)
        {
            var checkpoints = continuation["acceptedCheckpoints"]!.AsArray().Select(item => item!["sequence"]!.GetValue<int>()).ToArray();
            var checkpoint = PlanningSchemas.Type("integer");
            checkpoint["enum"] = new JsonArray(checkpoints.Select(number => (JsonNode?)JsonValue.Create(number)).ToArray());
            schema["properties"]!["resumeStep"] = continuation["canAttemptResume"]!.GetValue<bool>() && checkpoints.Length > 0
                ? PlanningSchemas.Nullable(PlanningSchemas.Object(("continuationId", PlanningSchemas.Enum(continuation["id"]!.ToString())),
                    ("producerCheckpoint", checkpoint), ("binding", PlanningSchemas.Ref("import")))) : PlanningSchemas.Type("null");
            schema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("resumeStep"));
        }
        ConstrainSelectionEvidence(state, page ?? SelectionContext(state), schema);
        return PlanningSchemas.Compact(schema);
    }

    // Selection reuses exact cached contracts, not descriptor-only discoveries.
    // Pages are derived from current identities; no new cursor or cache is retained.
    private static List<(string Address, JsonObject Page)> KnownOperationPages(PlanningSession state)
    {
        var rows = (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved)
            .GroupBy(capability => TaskOperations.Describe(capability).Id, StringComparer.Ordinal)
            .Where(group => group.Select(capability => (capability.Id, capability.Version, Hash: ContractHash(capability))).Distinct().Count() == 1)
            .Select(group => group.First())
            .Where(capability => state.Catalog!.AllowedStepTypes.Contains(capability.StepType) &&
                !state.Catalog.Policy.DeniedCapabilityIds.Contains(capability.Id) && TaskOperations.Validate(capability).Count == 0)
            .OrderBy(capability => TaskOperations.Describe(capability).Id, StringComparer.Ordinal).ToArray();
        var pages = new List<(string Address, JsonObject Page)>();
        string? next = null;
        for (var offset = (rows.Length - 1) / CompactContractDescriptorPageSize * CompactContractDescriptorPageSize;
             rows.Length > 0 && offset >= 0; offset -= CompactContractDescriptorPageSize)
        {
            var page = new JsonObject { ["offset"] = offset, ["total"] = rows.Length,
                ["items"] = new JsonArray(rows.Skip(offset).Take(CompactContractDescriptorPageSize).Select(capability =>
                {
                    var operation = TaskOperations.Describe(capability);
                    var description = NavigationDescription(operation.Description);
                    return (JsonNode)new JsonObject { ["id"] = operation.Id, ["version"] = operation.Version,
                        ["hash"] = ContractHash(capability), ["name"] = capability.Method ?? capability.StepType,
                        ["effect"] = capability.EffectKind, ["description"] = description,
                        ["descriptionComplete"] = description.Length == operation.Description.Length,
                        ["descriptionEvidence"] = "description:" + ContractIdentity(operation.Id, operation.Version) + ":0" };
                }).ToArray()), ["next"] = next };
            var address = "page:operations:" + PlanningGraphCompiler.Fingerprint(page.ToJsonString()) + ":" +
                offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            pages.Insert(0, (address, page)); next = address;
        }
        return pages;
    }

    internal static string[] SelectionOperationIds(PlanningSession state)
    {
        var pages = KnownOperationPages(state);
        return pages.Where((page, index) => index == 0 || JsonNode.DeepEquals(state.Construction!.Evidence?[page.Address], page.Page))
            .SelectMany(page => page.Page["items"]!.AsArray()).Select(item => item!["id"]!.ToString()).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static void ValidateKnownSelection(PlanningSession state, IEnumerable<string> operations, LLMRequest issued)
    {
        var context = JsonNode.Parse(issued.Prompt[(issued.Prompt.LastIndexOf('\n') + 1)..])!;
        var presented = new[] { context["knownOperations"] }.Concat((context["evidence"] as JsonObject ?? [])
            .Where(item => item.Key.StartsWith("page:operations:", StringComparison.Ordinal)).Select(item => item.Value))
            .SelectMany(page => page?["items"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var current = KnownOperationPages(state).SelectMany(page => page.Page["items"]!.AsArray()).OfType<JsonObject>().ToArray();
        foreach (var operation in operations)
            if (!presented.Any(row => row["id"]?.ToString() == operation && current.Any(actual =>
                new[] { "id", "version", "hash" }.All(field => JsonNode.DeepEquals(row[field], actual[field])))))
                PlanningConstruction.Fail("DISCOVERY_SELECTION_INVALID", "/operationIds", "The selected cached contract no longer matches the exact identity presented in this selection request.");
    }

    private static JsonObject? KnownOperations(PlanningSession state) => KnownOperationPages(state).FirstOrDefault().Page;

    private static JsonNode? LoadKnownOperationPage(PlanningSession state, string address) =>
        KnownOperationPages(state).FirstOrDefault(page => page.Address == address).Page;

    internal static JsonObject DiscoverySchema(PlanningSession state) => CompactDiscoverySchema(state);

    private static JsonObject Text(int maximum) => new() { ["type"] = "string", ["maxLength"] = maximum };
    private static JsonObject Strings(int minimum, int maximum) => PlanningSchemas.Array(Text(256), minimum, maximum);

    internal static LLMRequest Request(string instructions, JsonObject context, JsonObject schema, string purpose)
    {
        schema["title"] = purpose;
        return new() { Prompt = instructions + "\n" + PlanningJsonTransport.Prompt(context),
            StructuredOutputSchema = PlanningContractValidation.ProjectStructuredOutputSchema(schema) };
    }

    private static PlanningRequirement[] SelectionAnchor(PlanningSession state)
    {
        var remaining = state.Construction!.Requirements.Where(r => r.Status != "closed").Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var selected = state.Requirements!.Outcomes.Where(r => remaining.Contains(r.Id)).Take(1).ToArray();
        if (selected.Length > 0)
            return RequirementPage(state, state.Requirements.Outcomes.FindIndex(r => r.Id == selected[0].Id))
                .Where(requirement => remaining.Contains(requirement.Id)).ToArray();
        return selected.Length == 0 ? state.Requirements.Outcomes.Take(1).ToArray() : selected;
    }

    private static string[] SelectionRequirementIds(PlanningSession state)
    {
        var compact = State(state);
        var selected = compact.ReplacementScope is not null
            ? (compact.ReplacementRequirements ?? []).ToHashSet(StringComparer.Ordinal)
            : SelectionAnchor(state).Select(r => r.Id).Concat(EvidenceRequirementIds(state)).ToHashSet(StringComparer.Ordinal);
        var valid = state.Construction!.Requirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        return state.Requirements!.Outcomes.Where(r => selected.Contains(r.Id) && valid.Contains(r.Id)).Select(r => r.Id).ToArray();
    }

    internal static JsonObject SelectionContext(PlanningSession state)
    {
        var compact = State(state);
        var scopeOffset = ScopeOffset(state);
        var outcomes = state.Requirements!.Outcomes;
        var remaining = state.Construction!.Requirements.Where(r => r.Status != "closed").Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        // Fresh selection reuses a bounded original page so adjacent constraints
        // inform the immediate step. Drafts and Judges retain selected clauses only.
        var selected = SelectionAnchor(state);
        var suppliedRequirements = EvidenceRequirementIds(state);
        var lastRequirement = selected.Length == 0 ? -1 : outcomes.FindIndex(r => r.Id == selected[^1].Id);
        if (compact.RequirementPageRequested)
            lastRequirement = Math.Max(lastRequirement, compact.RequirementOffset);
        var nextRequirement = lastRequirement + 1;
        while (nextRequirement < outcomes.Count &&
                suppliedRequirements.Contains(outcomes[nextRequirement].Id)) nextRequirement++;
        var context = new JsonObject
        {
            ["baseHash"] = PlanningConstruction.Hash(state.Plan),
            ["requirements"] = RequirementNodes(state, selected.Select(r => r.Id).Where(id => !suppliedRequirements.Contains(id))),
            ["requirementCount"] = remaining.Count, ["requirementPage"] = selected.Length == 0 ? 0 : outcomes.FindIndex(r => r.Id == selected[0].Id),
            ["originalRequirementCount"] = outcomes.Count,
            ["nextRequirements"] = nextRequirement < outcomes.Count ? "page:requirements:" + nextRequirement : null,
            ["requirementsDirectory"] = "page:requirements:0",
            ["globalConstraints"] = Sources(state, compact.GlobalSourceIds), ["instructions"] = state.Request.Policy.Instructions,
            ["frontierCount"] = state.Construction.OpenScopes.Count,
            ["nextScopes"] = scopeOffset + 8 < state.Construction.OpenScopes.Count ? "page:scopes:" + (scopeOffset + 8) : null,
            ["scopes"] = ScopeDirectory(state),
            ["interfaces"] = state.Construction.Evidence?.ContainsKey("page:interfaces:" + compact.InterfaceOffset) == true ? null : InterfaceDirectory(state, null, compact.InterfaceOffset),
            ["nextInterfaces"] = NextInterfacePage(state, compact.InterfaceOffset, skipRetained: true),
            ["publicOutputs"] = state.Construction.Evidence?.ContainsKey("page:outputs:" + compact.OutputOffset) == true ? null : OutputDirectory(state),
            ["evidence"] = state.Construction.Evidence?.DeepClone(),
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics),
            ["rebuild"] = state.Construction.RebuildTasks.Count > 0 ? RebuildDirectory(state) : null,
            ["reselection"] = compact.ReplacementScope is null ? null : new JsonObject { ["scope"] = compact.ReplacementScope,
                ["requirementIds"] = new JsonArray(compact.ReplacementRequirements!.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["reason"] = compact.RejectedSteps.Last()["reason"]?.DeepClone(), ["evidence"] = "rejected:" + (compact.RejectedSteps.Count - 1) },
            ["budget"] = new JsonObject { ["remainingCalls"] = PlanningModelCalls.RemainingCalls(state) }
        };
        {
            // The anchor and issued continuation already identify the displayed
            // page. These storage counters add no requirement text or authority.
            context.Remove("requirementPage");
            context.Remove("originalRequirementCount");
            // The issued response schema already pins the exact baseline hash.
            // Host admission enforces the captured cumulative call allowance.
            context.Remove("baseHash");
            context.Remove("budget");
        }
        if (state.Construction.Evidence is { Count: > 0 })
        {
            // Preserve requested originals and useful bounded navigation together.
            // Historical presentation keeps its earlier suppression of excerpts.
            foreach (var directory in new[] { "scopes", "interfaces" })
                foreach (var item in (context[directory] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (directory == "scopes" && item["owner"]?.ToString() is { } owner &&
                        new TaskPlanSymbols(state.Plan!).Tasks.TryGetValue(owner, out var site))
                        item["objectiveEvidence"] = TaskObjectiveAddress(site.Task, 0);
                    item.Remove("question"); item.Remove("questionComplete");
                }
        }
        {
            context["interfacesDirectory"] = compact.InterfaceOffset > 0 && state.Construction.Evidence?.ContainsKey("page:interfaces:0") != true
                ? "page:interfaces:0" : null;
            AddAcceptedOperationEvidence(state, context, (context["interfaces"] as JsonArray ??
                context["evidence"]?["page:interfaces:" + compact.InterfaceOffset]?.DeepClone() as JsonArray ?? []).OfType<JsonObject>());
            context["completionOnly"] = CompletionOnly(state);
            context["continuation"] = ContinuationContext(state);
        }
        context["knownOperations"] = KnownOperations(state);
        {
            context["nextDecision"] = SelectionDecision(state, context["continuation"] as JsonObject);
            // Keep the exact need once, as the immediate semantic target. The
            // waiting consumer's original objective and authority remain visible.
            if (context["nextDecision"]!["prerequisite"] is not null)
                context["continuation"]!.AsObject().Remove("prerequisite");
        }
        AddDecisionContext(state, context);
        return TrimNavigation(context);
    }

    private static IEnumerable<TaskInput> OutputPage(PlanningSession state) => (state.Requirements?.Outputs ?? []).Skip(State(state).OutputOffset).Take(8);

    internal static JsonObject OutputDirectory(PlanningSession state)
    {
        var offset = State(state).OutputOffset;
        var count = state.Requirements?.Outputs?.Count ?? 0;
        return new JsonObject { ["offset"] = offset, ["total"] = count,
            ["items"] = new JsonArray(OutputPage(state).Select(o =>
            {
                var item = new JsonObject { ["name"] = o.Name, ["typeKind"] = o.Type.Kind, ["evidence"] = "output:" + o.Name };
                {
                    item["bindingStatus"] = state.Plan?.Root.Outputs.Any(output => output.Name == o.Name) == true ? "accepted_declaration" : "not_bound";
                    item["bindingEvidence"] = "scope:/root";
                }
                return (JsonNode)item;
            }).ToArray()),
            ["next"] = offset + 8 < count ? "page:outputs:" + (offset + 8) : null };
    }

    private static string? NextInterfacePage(PlanningSession state, int offset, bool skipRetained = false)
    {
        var plan = state.Construction?.Candidate?.Plan ?? state.Plan;
        var sizes = new List<(int Count, int Page)> { ((plan?.Inputs ?? state.Requirements?.Inputs ?? []).Count, 8) };
        if (plan is not null)
        {
            sizes.Add((new TaskPlanSymbols(plan).Tasks.Count, 4));
            sizes.Add((plan.Choices.Count, 4)); sizes.Add((plan.Groups.Count, 4));
        }
        var maximum = sizes.Max(s => s.Count);
        while (offset < maximum)
        {
            var next = sizes.Where(s => offset + s.Page < s.Count).Select(s => offset + s.Page).DefaultIfEmpty(-1).Min();
            if (next < 0) break;
            var address = "page:interfaces:" + next;
            if (!skipRetained || state.Construction!.Evidence?.ContainsKey(address) != true) return address;
            offset = next; // Strictly advances within the existing finite directory sizes.
        }
        return null;
    }

    internal static JsonObject Context(PlanningSession state, bool useCandidate = false)
    {
        var construction = state.Construction!; var compact = State(state);
        var step = compact.CurrentStep ?? throw new PlanningConflictException("Select a business substep before presenting a draft.");
        var draft = construction.Candidate;
        // Typed repairs also use this presentation, before their candidate is
        // valid. Their existing repair context owns editable slots and sources.
        var actualImports = useCandidate && draft is { Diagnostics.Count: 0 };
        var appliedInterface = useCandidate && draft is { Diagnostics.Count: 0 } && draft.Extension.CloseScopes.Contains(step.Scope, StringComparer.Ordinal);
        var verified = actualImports ? VerifiedCandidateInterfaces(state, appliedInterface) : null;
        var diagnostics = draft is not null
            ? draft.Diagnostics.Concat(state.Diagnostics).Distinct()
            : draft?.Diagnostics ?? state.Diagnostics;
        var context = new JsonObject
        {
            ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["candidateHash"] = draft?.Hash,
            ["step"] = JsonSerializer.SerializeToNode(step, PlanningJsonContext.Default.PlanningConstructionStep),
            ["requirements"] = RequirementNodes(state, step.RequirementIds.Concat(EvidenceRequirementIds(state))),
            ["commitments"] = new JsonObject(compact.PendingCommitments.Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                JsonSerializer.SerializeToNode(p.Value, PlanningJsonContext.Default.ListPlanningCommitment)))),
            ["globalConstraints"] = Sources(state, compact.GlobalSourceIds), ["instructions"] = state.Request.Policy.Instructions,
            ["interfaces"] = actualImports ? ActualImportedInterfaces(state, verified) : ImportedInterfaces(state, step),
            ["operations"] = ContractViews(state, useCandidate),
            ["evidence"] = construction.Evidence?.DeepClone(),
            ["draft"] = draft is null ? null : PlanningJsonTransport.TaskPlanPart(PlanningConstruction.Node(draft.Extension, PlanningJsonContext.Default.PlanningConstructionExtension)),
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(diagnostics),
            ["rebuild"] = RebuildDirectory(state)
        };
        PlanningConstruction.AddAuthorizedRevisionDraft(state, context);
        AddRequirementNavigation(state, context);
        if (CompanionOperationNavigation(state) is { } companions) context["companionOperations"] = companions;
        if (DraftContinuationContext(state) is { } continuation) context["continuation"] = continuation;
        if (ReviewedContinuationProvenance(state) is { } reviewedContinuation)
            context["reviewedContinuation"] = reviewedContinuation;
        if (step.OutputNames.Count > 0)
            context["outputs"] = PlanningConstruction.Node(state.Requirements!.Outputs!.Where(o => step.OutputNames.Contains(o.Name, StringComparer.Ordinal)).ToList(), PlanningJsonContext.Default.ListTaskInput);
        if (step.GroupId is { } groupId) context["group"] = GroupInterface(state, groupId, verified?.Accepted);
        if (ScopeGroup(state, step.Scope) is { } scopeGroup)
        {
            var candidateGroup = verified is not null;
            context["scopeGroup"] = GroupInterface(state, scopeGroup.Id,
                candidateGroup ? verified!.Candidate : verified?.Accepted, candidateGroup);
        }
        var unchangedExports = actualImports && UsesDerivedImports(state, draft!) && draft!.Extension is { Outputs.Count: 0, CloseScopes.Count: 0, Complete: false } &&
            step.CloseRequirementIds.Count == 0;
        if (ScopeInterface(state, appliedInterface, navigationOnly: unchangedExports, verified: appliedInterface ? verified?.Candidate : verified?.Accepted) is { } scopeInterface) context["scopeInterface"] = scopeInterface;
        var choiceValues = actualImports
            ? context["interfaces"]!.AsArray().Select(i => i!["binding"]!.Deserialize(PlanningJsonContext.Default.TaskValue)!)
            : step.Inputs;
        var choiceIds = choiceValues.SelectMany(TaskPlanCompiler.Values).Where(v => v.Kind == "choice").Select(v => v.Source)
            .Concat(draft?.Extension.Choices.Select(c => c.Id) ?? []).ToHashSet(StringComparer.Ordinal);
        if (choiceIds.Count > 0)
            context["choices"] = PlanningConstruction.Node(((draft?.Plan ?? state.Plan)?.Choices ?? []).Where(c => choiceIds.Contains(c.Id)).ToList(), PlanningJsonContext.Default.ListPlanningChoice);
        AddTaskEvidence(state, context, useCandidate);
        if (!useCandidate)
        {
            context["scopeEvidence"] = "scope:" + step.Scope;
            var interfacePage = "page:interfaces:" + compact.InterfaceOffset;
            if (step.Kind is "foreach" or "sequence" or "conditional" or "parallel")
            {
                var groups = InterfaceDirectory(state, [], compact.InterfaceOffset)
                    .Where(entry => entry?["group"] is not null).Select(entry => entry!.DeepClone()).ToArray();
                if (groups.Length > 0 && construction.Evidence?.ContainsKey(interfacePage) != true)
                    context["reusableInterfaces"] = new JsonArray(groups);
                if (NextInterfacePage(state, compact.InterfaceOffset) is { } next) context["interfacesDirectory"] = next;
            }
            else if (construction.Evidence?.ContainsKey(interfacePage) != true) context["interfacesDirectory"] = interfacePage;
            else if (NextInterfacePage(state, compact.InterfaceOffset) is { } next) context["interfacesDirectory"] = next;
        }
        if (actualImports &&
            (step.CloseRequirementIds.Count > 0 || draft!.Extension.CloseScopes.Count > 0 || draft.Extension.Complete))
        {
            context["scopeEvidence"] = "scope:" + step.Scope;
            // A declaration can satisfy an interface requirement without any
            // fragment reading its value. Keep its navigation distinct from
            // compiler-derived operands and from isolated group parameters.
            var inputs = state.Plan?.Inputs ?? state.Requirements?.Inputs ?? [];
            if (verified is not null)
            {
                if (CandidateInputDeclarations(state) is { } declarations) context["inputDeclarations"] = declarations;
            }
            else if (ScopeGroup(state, step.Scope) is null && inputs.Count > 0)
            {
                var offset = compact.InterfaceOffset < inputs.Count ? compact.InterfaceOffset : 0;
                context["inputDeclarations"] = new JsonObject
                {
                    ["scope"] = "/root", ["offset"] = offset, ["count"] = inputs.Count,
                    ["directory"] = offset > 0 && construction.Evidence?.ContainsKey("page:interfaces:0") != true ? "page:interfaces:0" : null,
                    ["inputs"] = new JsonArray(inputs.Skip(offset).Take(8)
                        .Select(input => (JsonNode)InputDirectoryEntry(state, input)).ToArray()),
                    ["next"] = offset + 8 < inputs.Count ? "page:interfaces:" + (offset + 8) : null
                };
            }
        }
        if (useCandidate)
        {
            // Retain semantic bindings and constraints once, without DTO defaults
            // or a repeated baseline hash. Issued correction contexts remain frozen.
            var presentedStep = context["step"]!.AsObject();
            presentedStep.Remove("baseHash");
            presentedStep.Remove("discoveryComplete");
            presentedStep["inputs"] = PlanningJsonTransport.TaskPlanPart(presentedStep["inputs"]);
            if (actualImports)
            {
                presentedStep.Remove("inputs");
                presentedStep.Remove("inputTaskIds");
                presentedStep.Remove("dependencyTaskIds");
            }
            if (!actualImports)
                foreach (var imported in context["interfaces"]!.AsArray())
                    imported!["value"] = PlanningJsonTransport.TaskPlanPart(imported["value"]);
            // Requested scope evidence includes its exact owning group interface.
            // Keep that evidence intact instead of repeating its complete group.
            foreach (var name in new[] { "group", "scopeGroup" })
                if (context[name] is JsonObject group && construction.Evidence?.Any(item =>
                    item.Value is JsonObject evidence && JsonNode.DeepEquals(group, evidence["group"])) == true)
                    context.Remove(name);
            AddCandidateGroupEvidence(state, context);
        }
        if (PlanningConstruction.TechnicalRejectionContext(state) is { } technicalHistory)
            context["historicalTechnicalRejections"] = technicalHistory;
        if (PlanningConstruction.DefectProgressContext(state, context) is { } progress)
            context["defectProgress"] = progress;
        if (!useCandidate && PlanningConstruction.PriorRepairObservations(state) is { } history)
            context["priorRepairObservations"] = history;
        AddDecisionContext(state, context);
        return context;
    }

    internal static JsonObject DiscoveryContext(PlanningSession state)
    {
        var step = State(state).CurrentStep!;
        var evidence = DiscoveryEvidence(state);
        var descriptors = DecisionOperationPage(state);
        var providerPage = PresentedProviderRequests(state);
        var context = new JsonObject
        {
            ["step"] = DiscoveryStep(state),
            ["requirements"] = RequirementNodes(state, step.RequirementIds),
            ["globalConstraints"] = Sources(state, State(state).GlobalSourceIds),
            ["instructions"] = state.Request.Policy.Instructions,
            ["sources"] = null,
            ["descriptors"] = descriptors,
            ["providerRequests"] = ProviderEvidencePage(state) is null ? providerPage : null,
            ["directoryEvidence"] = new JsonObject { ["sources"] = SourceDirectoryContinuation(state),
                ["operations"] = OperationDirectoryContinuation(state, descriptors), ["providers"] = ProviderDirectoryContinuation(state) },
            ["evidence"] = evidence?.DeepClone(),
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics)
        };
        AddRequirementNavigation(state, context);
        context["continuation"] = ContinuationContext(state);
        if (DescribedOperationChoices(state) is { } described) context["describedOperations"] = described;
        if (LocalComputationEligibility(state) is { } local) context["localComputations"] = local;
        if (ScopeOwnerNavigation(state) is { } owner)
            context["scopeOwner"] = owner;
        if (ScopeGroup(state, step.Scope) is { } scopeGroup)
            context["scopeGroup"] = GroupInterface(state, scopeGroup.Id);
        AddDecisionContext(state, context, discovery: true);
        // These are optional navigation slots, not fields inside original
        // evidence. Preserve historical issued presentation and nested values.
        return TrimNavigation(context);
    }

    internal static JsonArray RequirementNodes(PlanningSession state, IEnumerable<string> ids)
    {
        var compact = State(state); var selected = ids.ToHashSet(StringComparer.Ordinal);
        return new JsonArray(state.Requirements!.Outcomes.Where(r => selected.Contains(r.Id)).Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id, ["evidence"] = "requirement:" + r.Id, ["original"] = r.Description,
            ["status"] = state.Construction!.Requirements.Single(s => s.Id == r.Id).Status,
            ["sources"] = new JsonArray((compact.RequirementSources.GetValueOrDefault(r.Id) ?? []).Select(id =>
                (JsonNode)new JsonObject { ["id"] = id, ["evidence"] = "source:" + id, ["version"] = compact.Sources.Single(s => s.Id == id).Version }).ToArray()),
            ["commitments"] = JsonSerializer.SerializeToNode(r.Commitments, PlanningJsonContext.Default.ListPlanningCommitment)
        }).ToArray());
    }

    private static JsonArray Sources(PlanningSession state, IEnumerable<string> ids) => new(ids.Select(id => State(state).Sources.Single(s => s.Id == id))
        .Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["evidence"] = "source:" + s.Id, ["version"] = s.Version, ["text"] = s.Text }).ToArray());

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
                var inputGroup = group ?? ScopeGroup(state, s.Path);
                var entry = TrimNavigation(new JsonObject { ["path"] = s.Path, ["owner"] = s.Owner?.Id, ["kind"] = group is null ? s.Owner?.Kind : "group",
                ["objective"] = description, [ObjectiveTextCompleteField(state)] = objective is null ? null : JsonValue.Create(description == objective),
                ["requirementIds"] = opening is null ? null : new JsonArray(opening.RequirementIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["iterationInputs"] = s.Owner?.Kind == "foreach" ? "item and index" : null,
                ["inputScope"] = group is null && inputGroup is not null ? "/groups/" + inputGroup.Id + "/body" : null,
                ["inputs"] = null,
                ["evidence"] = group is null ? "scope:" + s.Path : "group:" + group.Id });
                if (s.Owner is { } owner && description != objective)
                    entry["objectiveEvidence"] = TaskObjectiveAddress(owner, 0);
                if (group is not null)
                {
                    // An empty parameter list is a meaningful isolated boundary,
                    // not optional navigation that can disappear during trimming.
                    entry["isolated"] = true; entry["inputCount"] = group.Inputs.Count;
                    entry["inputs"] = new JsonArray(group.Inputs.Take(8).Select(i => (JsonNode)new JsonObject
                        { ["input"] = i.Name, ["typeKind"] = i.Type.Kind, ["evidence"] = "group:" + group.Id }).ToArray());
                }
                {
                    entry["openDescendantCount"] = OpenDescendants(state, symbols, s.Path);
                    entry["acceptedExports"] = AcceptedExportDirectory(state, s);
                }
                return (JsonNode)entry;
            }).ToArray());
    }

    private static JsonObject RebuildDirectory(PlanningSession state)
    {
        var construction = state.Construction!;
        var existing = new TaskPlanSymbols(state.Plan ?? new TaskPlan()).Tasks.Keys.ToHashSet(StringComparer.Ordinal);
        var remaining = construction.RebuildTasks.Where(t => !existing.Contains(t.Id)).ToArray();
        var remainingInputBytes = 4096;
        var result = new JsonObject { ["remaining"] = remaining.Length,
            ["items"] = new JsonArray(remaining.Take(4).Select(t =>
            {
                var placement = construction.RebuildScopes.Single(s => s.Tasks.Contains(t.Id, StringComparer.Ordinal) || s.Always.Contains(t.Id, StringComparer.Ordinal));
                var original = construction.Invalidations.Last(i => i.Tasks.Any(old => old.Id == t.Id));
                var item = new JsonObject { ["id"] = t.Id, ["kind"] = t.Kind, ["objective"] = t.Objective,
                    ["scope"] = placement.Path, ["phase"] = placement.Always.Contains(t.Id, StringComparer.Ordinal) ? "always" : "tasks",
                    ["evidence"] = "original:" + original.Sequence + ":" + t.Id };
                {
                    var inputs = PlanningConstruction.TaskNode(t)["inputs"]?.DeepClone() ?? new JsonArray();
                    var bytes = System.Text.Encoding.UTF8.GetByteCount(inputs.ToJsonString());
                    if (bytes <= remainingInputBytes) { item["originalInputs"] = inputs; remainingInputBytes -= bytes; }
                    else item["originalInputsOmitted"] = true;
                }
                return (JsonNode)item;
            }).ToArray()), ["invalidationCount"] = construction.Invalidations.Count };
        if (remaining.Length > 0) result["semantics"] = RollbackSemantics;
        return result;
    }

    internal static JsonArray InterfaceDirectory(PlanningSession state, IEnumerable<string>? selected, int offset)
    {
        var plan = state.Construction?.Candidate?.Plan ?? state.Plan;
        var result = new JsonArray();
        if (plan is null)
        {
            foreach (var input in (state.Requirements?.Inputs ?? []).Skip(offset).Take(8))
                result.Add((JsonNode)InputDirectoryEntry(state, input));
            return result;
        }
        var symbols = new TaskPlanSymbols(plan);
        var ids = selected?.ToHashSet(StringComparer.Ordinal);
        var tasks = ids is null ? symbols.Tasks.Values.Skip(offset).Take(4) : symbols.Tasks.Values.Where(t => ids.Contains(t.Task.Id));
        foreach (var site in tasks)
        {
            var task = site.Task;
            var open = symbols.Scopes.Any(s => s.Owner?.Id == task.Id && state.Construction!.OpenScopes.Contains(s.Path, StringComparer.Ordinal));
            if (open) continue;
            var ports = NavigationPorts(state, task).ToArray();
            var entry = TaskNavigation(state, task, site.Scope.Path);
            entry["ports"] = new JsonArray(ports.Take(8).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            entry["portCount"] = ports.Length;
            entry["nextPorts"] = ports.Length > 8 ? TaskPortsAddress(task.Id, ports, 8) : null;
            entry["evidence"] = "task:" + task.Id;
            result.Add((JsonNode)TrimNavigation(entry));
        }
        foreach (var input in plan.Inputs.Skip(offset).Take(8))
            result.Add((JsonNode)InputDirectoryEntry(state, input));
        foreach (var choice in plan.Choices.Skip(offset).Take(4))
            result.Add((JsonNode)new JsonObject { ["choice"] = choice.Id, ["typeKind"] = choice.Type.Kind,
                ["question"] = NavigationDescription(choice.Question), ["questionComplete"] = NavigationDescription(choice.Question) == choice.Question,
                ["evidence"] = "choice:" + choice.Id });
        foreach (var group in plan.Groups.Skip(offset).Take(4))
        {
            var open = state.Construction!.OpenScopes.Any(s => s == "/groups/" + group.Id + "/body" || s.StartsWith("/groups/" + group.Id + "/body/", StringComparison.Ordinal));
            if (open) continue; // Open group interfaces are already in the scope directory and are not consumable call targets.
            result.Add((JsonNode)new JsonObject { ["group"] = group.Id, ["open"] = open,
                ["inputs"] = new JsonArray(group.Inputs.Take(8).Select(i => (JsonNode?)JsonValue.Create(i.Name)).ToArray()), ["inputCount"] = group.Inputs.Count,
                ["outputs"] = open ? null : new JsonArray(group.Body.Outputs.Take(8).Select(o => (JsonNode?)JsonValue.Create(o.Name)).ToArray()),
                ["outputCount"] = open ? null : JsonValue.Create(group.Body.Outputs.Count), ["evidence"] = "group:" + group.Id });
        }
        if (state.Plan is not null)
        {
            var rows = result.OfType<JsonObject>().ToArray();
            AddAcceptedProducerPorts(state, rows, rows.Select(row => row["task"]?.ToString()).OfType<string>().ToHashSet(StringComparer.Ordinal));
        }
        return result;
    }

    private static JsonObject InputDirectoryEntry(PlanningSession state, TaskInput input)
    {
        var entry = new JsonObject { ["input"] = input.Name, ["typeKind"] = input.Type.Kind, ["evidence"] = "input:" + input.Name };
        entry["scope"] = "/root";
        return entry;
    }

    internal static JsonObject? GroupInterface(PlanningSession state, string id, TaskConstructionValidation? verified = null, bool candidate = false)
    {
        var plan = candidate ? state.Construction!.Candidate!.Plan : state.Plan;
        var group = plan?.Groups.SingleOrDefault(g => g.Id == id);
        if (group is null) return null;
        var path = "/groups/" + id + "/body";
        var openScopes = candidate ? state.Construction!.Candidate!.OpenScopes : state.Construction!.OpenScopes;
        var open = openScopes.Any(s => s == path || s.StartsWith(path + "/", StringComparison.Ordinal));
        var result = new JsonObject { ["id"] = id, ["open"] = open,
            ["hash"] = PlanningGraphCompiler.Fingerprint(PlanningConstruction.Node(group, PlanningJsonContext.Default.TaskGroup)!.ToJsonString()),
            ["inputs"] = PlanningConstruction.Node(group.Inputs, PlanningJsonContext.Default.ListTaskInput), ["outputs"] = null };
        result["basis"] = candidate ? "tentative_candidate" : "accepted_declarations";
        if (GroupOpening(state, id) is { } opening)
            result["intent"] = new JsonObject { ["objective"] = opening.Objective,
                ["expectedOutputs"] = new JsonArray(opening.ExpectedOutputs.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["requirementIds"] = new JsonArray(opening.RequirementIds.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
        if (open) return result;
        var requested = group.Body.Outputs.Select(o => o.Value).ToArray();
        var validation = verified;
        if (validation is null)
        {
            var probe = PlanningConstruction.Clone(plan!);
            foreach (var choice in probe.Choices.Where(c => c.Selected is null)) choice.Selected = choice.Recommended;
            validation = TaskPlanCompiler.ValidateConstruction(probe, state.Catalog!, state.Request,
                openScopes.ToHashSet(StringComparer.Ordinal), new Dictionary<string, IReadOnlyList<TaskValue>> { [path] = requested });
        }
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
        {
            // Successful probes preserve request order. Diagnose only a missing
            // selected binding; unrelated accepted-plan findings are not evidence
            // that a particular import is invalid.
            if (result.Diagnostics.Count == 0 && interfaces is not null)
            {
                var visible = 0;
                for (var index = 0; index < step.Inputs.Count; index++)
                {
                    var value = step.Inputs[index];
                    if (visible < interfaces.Count && JsonNode.DeepEquals(
                        PlanningConstruction.Node(value, PlanningJsonContext.Default.TaskValue),
                        PlanningConstruction.Node(interfaces[visible].Value, PlanningJsonContext.Default.TaskValue)))
                    { visible++; continue; }
                    var binding = value.Kind + (value.Source is null ? "" : " '" + value.Source + "'") +
                        (value.Port is null ? "" : " port '" + value.Port + "'");
                    var symbols = new TaskPlanSymbols(plan);
                    var boundary = ScopeGroup(state, step.Scope);
                    var origin = value.Kind == "input" && boundary?.Inputs.Any(i => i.Name == value.Source) == true ? "/groups/" + boundary.Id + "/body" :
                        value.Kind == "input" && plan.Inputs.Any(i => i.Name == value.Source) ? "/root" :
                        value.Kind is "output" or "present" && value.Source is not null && symbols.Tasks.TryGetValue(value.Source, out var site)
                            ? site.Scope.Path : null;
                    var detail = boundary is null ? "" : " Isolated group '" + boundary.Id + "' declares " + boundary.Inputs.Count +
                        " inputs: " + new JsonArray(boundary.Inputs.Take(8).Select(i => (JsonNode?)JsonValue.Create(i.Name)).ToArray()).ToJsonString() +
                        (boundary.Inputs.Count > 8 ? " (first eight)" : "") + ". Exact parameters: group:" + boundary.Id +
                        ". Caller inputs/tasks are not captured.";
                    PlanningConstruction.Fail("CONSTRUCTION_STEP_INPUT_INVALID", "/step/inputs/" + index,
                        "Selected " + binding + " is unavailable in scope '" + step.Scope + "'." +
                        (origin is null ? "" : " Its declaring scope is '" + origin + "'.") + detail);
                }
            }
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INPUT_INVALID", "/step/inputs", "Every selected input must have an available typed interface in this scope.");
        }
        return new JsonArray(interfaces!.Select(i => (JsonNode)new JsonObject
        { ["value"] = PlanningConstruction.Node(i.Value, PlanningJsonContext.Default.TaskValue), ["schema"] = i.Schema.DeepClone() }).ToArray());
    }

    internal static void ValidateSelection(PlanningSession state, PlanningConstructionStep step)
    {
        var construction = state.Construction!;
        var compact = State(state);
        if (step.Kind is null ? false : !TaskKinds.Contains(step.Kind, StringComparer.Ordinal))
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/kind", "Select an existing task kind or leave a capability-dependent implementation unset.");
        ValidatePrerequisites(state, step);
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

    private static void ValidatePrerequisites(PlanningSession state, PlanningConstructionStep step)
    {
        if (step.Prerequisites is null) return;
        if (step.Prerequisites.Count > 4)
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/prerequisites", "Retain at most four evidence-linked current prerequisites.");
        // Issued schemas bound new citations to displayed originals. Once a step
        // is retained, its prerequisite may still cite an earlier declaration
        // after that navigation page expires; citation grants no coverage or edits.
        var available = PrerequisiteAddresses(state, state.Construction!.Requirements.Select(requirement => requirement.Id))
            .ToHashSet(StringComparer.Ordinal);
        // String bounds were validated under the exact issued schema before
        // deserialization. Do not reapply UTF-16 bounds to retained declarations.
        foreach (var prerequisite in step.Prerequisites)
        {
            var addresses = prerequisite.EvidenceAddresses.Distinct(StringComparer.Ordinal).ToList();
            if (string.IsNullOrWhiteSpace(prerequisite.Need) ||
                prerequisite.EvidenceAddresses.Count is < 1 or > 4)
                PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/prerequisites", "A prerequisite needs a bounded description and distinct original evidence addresses.");
            foreach (var address in addresses)
            {
                var exactContract = address.StartsWith("schema:", StringComparison.Ordinal) || address.StartsWith("description:", StringComparison.Ordinal) ||
                    address.StartsWith("contract:", StringComparison.Ordinal);
                if (!available.Contains(address))
                    PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/prerequisites", "Link each prerequisite to a retained original clause or exact contract evidence; citations do not expand requirement coverage or authorize future task references.");
                if (exactContract)
                {
                    var retained = state.Construction!.Evidence![address];
                    var current = address.StartsWith("contract:", StringComparison.Ordinal)
                        ? retained?["id"] is { } id && retained["version"] is { } version &&
                          address == "contract:" + ContractIdentity(id.ToString(), version.ToString()) &&
                          retained["hash"]?.ToString() == ContractHash(ExactContract(state, id.ToString(), version.ToString()))
                        : JsonNode.DeepEquals(retained, LoadContractEvidence(state, address, recordSchemaSelection: false));
                    if (!current) PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/prerequisites", "A prerequisite contract reference is stale or unavailable.");
                }
            }
            // Exact repeated citations add no authority or evidence. Normalize only
            // validated fresh navigation; keep raw receipts and historical behavior.
            prerequisite.EvidenceAddresses = addresses;
        }
    }

    private static HashSet<string> CurrentPrerequisiteAddresses(PlanningSession state)
    {
        var addresses = State(state).CurrentStep?.Prerequisites?.SelectMany(item => item.EvidenceAddresses).ToHashSet(StringComparer.Ordinal) ?? [];
        if (State(state).ResumingStepId is { } resumingId &&
            State(state).SuspendedSteps?.SingleOrDefault(item => item.Id == resumingId && Active(item)) is { } resumed)
            addresses.UnionWith(resumed.Prerequisite.EvidenceAddresses);
        return addresses;
    }

    internal static void RetainPrerequisiteEvidence(PlanningSession state)
    {
        var construction = state.Construction!;
        var addresses = CurrentPrerequisiteAddresses(state);
        var retained = new JsonObject();
        foreach (var item in construction.Evidence ?? [])
            if (addresses.Contains(item.Key)) retained[item.Key] = item.Value?.DeepClone();
        construction.Evidence = retained.Count > 0 ? retained : null;
        construction.EvidenceRequests.Clear();
        foreach (var address in retained.Select(item => item.Key))
            construction.EvidenceRequests.Add(PlanningConstruction.EvidenceBatchFingerprint(state, [address]));
    }
}
