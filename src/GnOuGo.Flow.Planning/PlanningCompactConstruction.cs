using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal static bool IsCompactPresentation(PlanningSession state) => state.Construction?.Compact?.PresentationVersion is 2 or 3 or 4 or 5 or 6;
    internal static bool UsesDeferredBinding(PlanningSession state) => state.Construction?.Compact?.PresentationVersion is 3 or 4 or 5 or 6;
    internal static bool UsesVerifiedReview(PlanningSession state) => state.Construction?.Compact?.PresentationVersion is 4 or 5 or 6;
    internal static bool UsesConvergentConstruction(PlanningSession state) => state.Construction?.Compact?.PresentationVersion is 5 or 6;
    internal static bool UsesDecisionView(PlanningSession state) => state.Construction?.Compact?.PresentationVersion == 6;

    // Remaining mandatory review calls; issuance captures this once for physical-attempt admission.
    internal static int RequiredReviewCalls(PlanningSession state)
    {
        var construction = state.Construction!;
        if (UsesDecisionView(state)) return PlanningConstructionReviewCoverage.RemainingCalls(state);
        if (UsesConvergentConstruction(state))
            return construction.Candidate is { LocalReviewed: true } candidate &&
                (!candidate.NeedsIntermediateReview || candidate.IntermediateReviewed) ? 0 : 1;
        if (construction.Candidate is not { } draft) return construction.Checkpoints.Count > 0 ? 2 : 1;
        return (draft.LocalReviewed ? 0 : 1) + (draft.NeedsIntermediateReview && !draft.IntermediateReviewed ? 1 : 0);
    }

    internal const string TransformSemantics = "transform extract preserves observed facts; interpret uses the configured runtime LLM to derive typed data from supplied inputs under its objective, without a separate expression. Neither supplies unobserved external facts or performs external actions.";

    private static readonly string[] TaskKinds = ["operation", "value", "transform", "sequence", "conditional", "parallel", "foreach", "call", "close", "group", "choice"];
    internal const string IntentPurpose = "construction_intent", SelectPurpose = "construction_select", DiscoveryPurpose = "construction_discovery";
    internal const string IndexInstructions = """
        Index this exact original intent page. Divide its text into consecutive verbatim clauses whose concatenation equals the entire page, including whitespace. Every clause is either a business requirement or an applicable global constraint. Required actions and results, including setup and cleanup, are business requirements with explicit completion obligations. Keep action-specific or resource-specific constraints with their requirement; reserve global for invariants applying across the workflow. Do not summarize, omit, invent, or plan tasks. Keep original order and text. Declare only caller inputs and returned public result interfaces explicitly supported by this page. Effect payloads and descriptions of files, artifacts or resources belong to requirements; they do not automatically declare public return interfaces. Do not reconstruct those resources' schemas during indexing. A requested effect need not invent a public output. Other original pages and answers are retained independently. No capability selection or workflow outline.
        Array minItems/maxItems are explicit length restrictions, not placeholders. Use null for a bound absent from the original source; maxItems:0 permits only an empty array. Never infer bounds from examples or invent them.
        """;
    internal const string SelectInstructions = """
        Select one immediate business action to execute; discovery is a separate host phase. No outline. Choose scope, kind, imports/dependencies. Reuse inputs; no preparatory copies. operation executes external work: discover its contract before preparing tool-specific arguments. value: copy/projection/assembly, declared arithmetic, predicates, lookup, flatten, JSON encoding; no string concatenation, formatting, URL encoding or interpolation. transform extracts/interprets data. Control scopes capture ancestors; groups isolate inputs declared at opening, never caller inputs/tasks. expectedOutputs: internal; outputNames: PUBLIC ([] otherwise). call: closed groupId, otherwise null. closeRequirementIds: satisfied obligations only. Cleanup, opening, exports and closure are steps. Return step, evidenceRequests or missing-business-fact clarifications. Read originals/interfaces/outputs at issued addresses; evidence grants no authority.
        """;
    internal const string DiscoveryInstructions = """
        Select capabilities for this step and its original requirements/global constraints. Descriptors provide navigation, not executable contracts or permissions. Return one action: operationIds, discoveryRequests, evidenceRequests, reselectStep or clarifications. All page:*, schema: and description: addresses go in evidenceRequests, never cursor. discoveryRequests uses issued source/cursor pairs and query:null; the host supplies this step's exact objective. After bounded inspection, reselectStep explains an unsuitable kind within retained scope, requirements and correction limits. Never simulate external work or plan future work. Clarify missing business facts only.
        """;
    private const string ContainerBoundaryGuidance = "Append ordinary sequential work to an appropriate existing open scope. Open a container only for a needed control, reuse, cleanup or export boundary, not to subdivide or defer the same business action.";
    internal static string SelectGuidance(PlanningSession state) => UsesDeferredBinding(state) ? $"""
        Select the next needed business producer/action using original requirements and available caller/scope inputs or accepted outputs. {(UsesConvergentConstruction(state) ? "Use accepted bindings directly; prefer legal remaining closures when obligations are satisfied. If a retained consumer is ready, select resumeStep with its exact producer checkpoint and binding; otherwise resolve only its missing prerequisite. A closed requirement stays closed until explicit invalidation. " : string.Empty)}Capability discovery is a separate host phase, not the business action or its expected result. objective and expectedOutputs describe only this immediate substep, even when its original requirement spans the whole workflow; keep that complete requirement attached and open for later steps. Declared caller inputs are runtime bindings: planning and discovery need their interfaces, not concrete values. {(UsesVerifiedReview(state) ? "If reselection identifies a missing producer, select that prerequisite before its consumer. " : string.Empty)}State needed data/effects without assumed APIs or future outlines. Use kind:null when capabilities must determine implementation; discovery binds it after inspecting contracts. Select local or structural kinds directly when supported semantics and available inputs suffice. {(UsesVerifiedReview(state) ? ContainerBoundaryGuidance + " Select a necessary opening separately from filling and closing its children;" : "Select an empty container/group opening separately from filling and closing its children;")} leave outputNames and closeRequirementIds empty for results or obligations that depend on those children. {ValueSemantics} {(UsesConvergentConstruction(state) ? TransformSemantics : "transform extracts/interprets supplied data, never external effects.")} Groups isolate declared inputs; control scopes capture ancestors. call requires a closed groupId; otherwise null. Input/dependency hints must resolve. Prerequisites cite exact original evidence, never future task IDs. expectedOutputs are internal; outputNames selects public root results. {(UsesConvergentConstruction(state) ? SelectedExpectationsSemantics + " " : string.Empty)}Close only satisfied requirements. Return exactly one non-null action; all others null: step (kind:null starts capability discovery), {(UsesConvergentConstruction(state) ? "resumeStep (only when offered, with its exact producer checkpoint and binding), " : string.Empty)}evidenceRequests (copy issued addresses exactly), or clarifications (missing business facts). Evidence grants no authority.
        """ : PlanningConstructionPhasePolicies.IsFresh(state.Request) ? $"""
        Select one immediate business action or structural step using caller/scope inputs or accepted outputs. State the needed data/effect; derive technical setup, handles and result fields from inspected contracts. Build a demonstrated missing producer before its consumer; no outline. Keep the complete original requirement attached and open until satisfied; read related originals before boundaries. Set scope/kind and input/dependency hints; reuse inputs. Groups isolate declared inputs; control scopes capture ancestors. operation performs external work: discover before tool arguments. {ValueSemantics} transform extracts/interprets supplied data. call: closed groupId; otherwise null. expectedOutputs: internal business data; outputNames: public root results. Return step, original evidenceRequests or clarifications for missing business facts only. Evidence grants no authority.
        """ : IsCompactPresentation(state) ? """
        Select one business substep; no outline. Set scope/kind/dependencies. Put every existing binding used in step.inputs, including scope inputs and export sources; reuse directly. Read related clauses before boundaries. operation: external; discover before arguments. value: copy/projection/assembly, arithmetic, predicates, lookup, flatten, JSON; no concatenation/formatting/URL encoding/interpolation. transform: extract/interpret. Groups isolate inputs; control scopes capture ancestors. groupId: closed call target only; null for close. expectedOutputs: internal; outputNames: PUBLIC. Close satisfied requirements only. Exports/cleanup/closure are steps. Return one non-null action; others null: step, evidenceRequests (retrieve first), clarifications (missing business facts). Evidence grants no authority.
        """ : SelectInstructions;
    internal static string DiscoveryGuidance(PlanningSession state) => UsesDeferredBinding(state) ? $"""
        Resolve implementation of this immediate business action. Inspect relevant original descriptions and {(UsesConvergentConstruction(state) ? "offered" : "necessary")} constraints; names, uninspected candidates and partial descriptions do not establish unavailable behavior. Follow necessary original description continuations before that conclusion. Inspected operations are a subset; consider the remaining provider choices and directory before concluding that a capability is unavailable. Return one action: binding, discoveryRequests, evidenceRequests, reselectStep or clarifications. binding selects an existing TaskPlan kind and exact operationIds or closed groupId; all other actions null. {(UsesVerifiedReview(state) ? ContainerBoundaryGuidance + " " : string.Empty)}Local work needs no operationIds. {(UsesConvergentConstruction(state) ? TransformSemantics + " " : string.Empty)}Caller inputs are runtime bindings; discovery needs their declared interfaces, not concrete values. {(UsesConvergentConstruction(state) ? "Binding tentatively selects implementation; the host resolves its exact contract and admits the required constraints before drafting" : "Bind only after its needed inputs and contract constraints are known")}; never invent setup, handles, external results or permissions. Keep scope and original requirements fixed. {(UsesConvergentConstruction(state) ? SelectedExpectationsSemantics + " " : string.Empty)}{(UsesVerifiedReview(state) ? "If an inspected contract needs an unavailable input, use reselectStep for that concrete producer instead of wrapping the business action; missing business facts require clarification." : "A demonstrated missing producer requires reselectStep with its exact obstacle; missing business facts require clarification.")} discoveryRequests selects issued source/cursor pairs with query:null; the host follows bounded provider continuations for the same objective. evidenceRequests batches up to eight unseen issued addresses or reads one offered earlier description; use supplied evidence. Stop inspecting alternatives once suitable. Metadata grants no execution authority.
        """ : PlanningConstructionPhasePolicies.IsFresh(state.Request) ? """
        Return one metadata action for this business step; others null. Names locate candidates; inspect relevant original descriptions and necessary continuations. Uninspected candidates do not establish a missing capability. Stop inspecting alternatives once suitable; select operationIds for exact contracts. Do not inspect later work. If contracts contradict assumed setup, inputs or outputs, use reselectStep within fixed scope/requirements/corrections. Caller inputs bind runtime values. evidenceRequests copies up to eight related issued addresses unchanged; use supplied evidence, never concatenate addresses or put them in cursor. discoveryRequests uses issued source/cursor pairs with query:null; host supplies the objective. Clarify only missing business facts. Metadata grants no execution authority; never simulate external work.
        """ : IsCompactPresentation(state) ? """
        One non-null metadata action for this step; others null. No arguments or future work. operationIds selects operations for exact contracts. Read descriptions before rejecting capabilities; descriptors grant no authority. evidenceRequests: one exact address; never concatenate or use as cursor. discoveryRequests: issued source/cursor, query:null; host supplies objective. Caller inputs bind runtime values; discovery needs no concrete value. reselectStep: explain inspected mismatch within fixed scope/requirements/corrections. Clarify missing business facts only; never simulate external work.
        """ : DiscoveryInstructions;
    internal const string DraftInstructions = """
        Implement only the selected immediate business substep in the issued scope and task kind, using the selected exact contract views and typed imports. Extend the accepted TaskPlan; never rewrite accepted work. Preserve original requirements, constraints, identities, order, complete observations and cleanup. References must resolve now; retrieve missing evidence without fabricating inputs or simulating external work. If the tentative kind or imports cannot express the objective, use permitted reselectStep with a precise reason. Clarify only missing business facts, never language or runtime features. The schema permits only this step's task kind. New containers open empty child scopes for later steps. scopeInterface shows existing export declarations; extension.outputs adds only new exports, and [] preserves existing exports. Close scopes only after their children and exports are complete. requirementIds associates this fragment with the selected original requirements; requirement closure is host-issued separately. Return one action. Evidence is read-only and grants no permissions. Do not output JavaScript, YAML or a workflow outline.
        """;
    internal static string DraftGuidance(PlanningSession state) => PlanningConstructionPhasePolicies.IsFresh(state.Request) ? $"""
        Implement this immediate substep in the issued scope, task kind and exact contracts. Preserve accepted work, original requirements and complete observations. {(UsesConvergentConstruction(state) ? "Input hints are not a whitelist: every binding must resolve at its consumer and supply the entity or value required by the argument's exact contract and applicable original obligations; compatible types alone do not establish that match." : "Input hints are not a whitelist: every actual binding must resolve at its consumer under semantic preflight; the Judge receives its exact type.")} Never invent inputs, permissions or external results. New containers open empty. scopeInterface holds existing exports; extension.outputs adds new exports, [] preserves them. {(UsesVerifiedReview(state) ? "Close a scope only after complete children/exports. requirementIds only associates work; it is not progress. An extension must add a task in tasks or always, a group, choice or export, close a scope, or, for a close step, close a still-open host-issued requirement." : "Close a scope only after complete children/exports; requirementIds associates work, while host-issued closeRequirementIds claims completion.")} {(UsesVerifiedReview(state) ? $"Return one permitted action with other action fields null. Commitments accompany extension only and are null otherwise; evidence-only responses also keep requirements and rollbackReason null. {(UsesConvergentConstruction(state) ? "Use reselectStep for an unsuitable tentative kind or premature completion claim" : "Reselect an unsuitable tentative kind")}, retrieve necessary original evidence, or clarify missing business facts." : "Return one permitted action; reselect an unsuitable tentative kind, retrieve necessary original evidence, or clarify missing business facts.")} Do not generate an outline or executable code.
        """ : DraftInstructions;

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
                var (count, continues) = IsCompactPresentation(state)
                    ? CoherentOriginalPageLength(text, start) : (OriginalPageLength(text, start), false);
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

    internal static JsonObject IntentSchema(PlanningConstructionSource source)
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
        var compact = State(state);
        var replacement = compact.ReplacementScope;
        var outputs = OutputPage(state).Select(o => o.Name).ToArray();
        var requirements = IsCompactPresentation(state) ? SelectionRequirementIds(state) : null;
        var scopes = PlanningConstructionPhasePolicies.IsFresh(state.Request) && replacement is null
            ? ScopeDirectory(state).Select(scope => scope!["path"]!.ToString()).ToArray() : null;
        var hasClosedGroup = !IsCompactPresentation(state) || state.Plan?.Groups.Any(group =>
            !state.Construction!.OpenScopes.Contains("/groups/" + group.Id + "/body", StringComparer.Ordinal)) == true;
        var completionOnly = CompletionOnly(state);
        var kinds = completionOnly ? ["close"] : TaskKinds.Where(kind => (kind != "call" || hasClosedGroup) && (!UsesDeferredBinding(state) || kind != "operation")).ToArray();
        JsonObject RequirementIds(int minimum) => requirements is { Length: > 0 }
            ? PlanningSchemas.Array(PlanningSchemas.Enum(requirements), minimum, 4) : Strings(minimum, 4);
        var selectedRequirements = replacement is not null && requirements is { Length: > 0 }
            ? PlanningSchemas.Array(PlanningSchemas.Enum(requirements), requirements.Length, requirements.Length)
            : RequirementIds(1);
        var step = PlanningSchemas.Object(("scope", replacement is not null ? PlanningSchemas.Enum(replacement)
                : scopes is { Length: > 0 } ? PlanningSchemas.Enum(scopes) : Text(192)),
            ("kind", UsesDeferredBinding(state) && !completionOnly ? PlanningSchemas.Nullable(PlanningSchemas.Enum(kinds)) : PlanningSchemas.Enum(kinds)),
            ("objective", Text(512)), ("requirementIds", selectedRequirements), ("inputs", PlanningSchemas.Array(PlanningSchemas.Ref("import"), 0, 8)),
            ("groupId", hasClosedGroup ? PlanningSchemas.Nullable(Text(96)) : PlanningSchemas.Type("null")),
            ("dependencyTaskIds", Strings(0, 8)), ("expectedOutputs", Strings(0, 4)),
            ("closeRequirementIds", RequirementIds(0)), ("outputNames", outputs.Length == 0
                ? PlanningSchemas.Array(Text(256), 0, 0) : PlanningSchemas.Array(PlanningSchemas.Enum(outputs), 0, 8)));
        if (UsesDeferredBinding(state))
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
        if (UsesConvergentConstruction(state) &&
            (state.Plan is null || new TaskPlanSymbols(state.Plan).Tasks.Count == 0))
        {
            var imports = schema["$defs"]!["import"]!["anyOf"]!.AsArray();
            imports.RemoveAt(0);
            imports[0]!["properties"]!["kind"] = PlanningSchemas.Enum("input", "choice");
        }
        if (UsesDecisionView(state))
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
        if (UsesConvergentConstruction(state) && ContinuationContext(state) is { } continuation)
        {
            var checkpoints = continuation["acceptedCheckpoints"]!.AsArray().Select(item => item!["sequence"]!.GetValue<int>()).ToArray();
            var checkpoint = PlanningSchemas.Type("integer");
            checkpoint["enum"] = new JsonArray(checkpoints.Select(number => (JsonNode?)JsonValue.Create(number)).ToArray());
            schema["properties"]!["resumeStep"] = continuation["canAttemptResume"]!.GetValue<bool>() && checkpoints.Length > 0
                ? PlanningSchemas.Nullable(PlanningSchemas.Object(("continuationId", PlanningSchemas.Enum(continuation["id"]!.ToString())),
                    ("producerCheckpoint", checkpoint), ("binding", PlanningSchemas.Ref("import")))) : PlanningSchemas.Type("null");
            schema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("resumeStep"));
        }
        if (UsesDeferredBinding(state)) ConstrainSelectionEvidence(state, SelectionContext(state), schema);
        return IsCompactPresentation(state) ? PlanningSchemas.Compact(schema) : schema;
    }

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
        if (selected.Length > 0 && PlanningConstructionPhasePolicies.IsFresh(state.Request))
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
        if (IsCompactPresentation(state) && compact.RequirementPageRequested)
            lastRequirement = Math.Max(lastRequirement, compact.RequirementOffset);
        var nextRequirement = lastRequirement + 1;
        if (IsCompactPresentation(state))
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
            ["nextInterfaces"] = NextInterfacePage(state, compact.InterfaceOffset, skipRetained: UsesConvergentConstruction(state)),
            ["publicOutputs"] = state.Construction.Evidence?.ContainsKey("page:outputs:" + compact.OutputOffset) == true ? null : OutputDirectory(state),
            ["evidence"] = state.Construction.Evidence?.DeepClone(),
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(state.Diagnostics),
            ["rebuild"] = state.Construction.RebuildTasks.Count > 0 ? RebuildDirectory(state) : null,
            ["reselection"] = compact.ReplacementScope is null ? null : new JsonObject { ["scope"] = compact.ReplacementScope,
                ["requirementIds"] = new JsonArray(compact.ReplacementRequirements!.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["reason"] = compact.RejectedSteps.Last()["reason"]?.DeepClone(), ["evidence"] = "rejected:" + (compact.RejectedSteps.Count - 1) },
            ["budget"] = new JsonObject { ["remainingCalls"] = PlanningModelCalls.RemainingCalls(state) }
        };
        if (IsCompactPresentation(state))
        {
            // The anchor and issued continuation already identify the displayed
            // page. These storage counters add no requirement text or authority.
            context.Remove("requirementPage");
            context.Remove("originalRequirementCount");
            // The issued response schema already pins the exact baseline hash.
            // Host admission enforces the captured cumulative call allowance.
            context.Remove("baseHash");
            context.Remove("budget");
            // Keep the bounded meaning of accepted work alongside its exact
            // retrieval link. Historical presentation retains its issued shape.
            if (!PlanningConstructionPhasePolicies.IsFresh(state.Request))
                foreach (var item in (context["interfaces"] as JsonArray ?? []).OfType<JsonObject>())
                    if (item[ObjectiveTextCompleteField(state)]?.GetValue<bool>() == false && item["objectiveEvidence"] is JsonValue)
                        item.Remove("objective");
        }
        if (IsCompactPresentation(state) && state.Construction.Evidence is { Count: > 0 })
        {
            // Preserve requested originals and useful bounded navigation together.
            // Historical presentation keeps its earlier suppression of excerpts.
            foreach (var directory in new[] { "scopes", "interfaces" })
                foreach (var item in (context[directory] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (directory == "scopes" && item["owner"]?.ToString() is { } owner &&
                        new TaskPlanSymbols(state.Plan!).Tasks.TryGetValue(owner, out var site))
                        item["objectiveEvidence"] = TaskObjectiveAddress(site.Task, 0);
                    if (!PlanningConstructionPhasePolicies.IsFresh(state.Request))
                    { item.Remove("objective"); item.Remove(ObjectiveTextCompleteField(state)); }
                    item.Remove("question"); item.Remove("questionComplete");
                }
        }
        if (UsesConvergentConstruction(state))
        {
            context["interfacesDirectory"] = compact.InterfaceOffset > 0 && state.Construction.Evidence?.ContainsKey("page:interfaces:0") != true
                ? "page:interfaces:0" : null;
            AddAcceptedOperationEvidence(state, context, (context["interfaces"] as JsonArray ??
                context["evidence"]?["page:interfaces:" + compact.InterfaceOffset]?.DeepClone() as JsonArray ?? []).OfType<JsonObject>());
            context["completionOnly"] = CompletionOnly(state);
            context["continuation"] = ContinuationContext(state);
        }
        AddDecisionContext(state, context, review: false);
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
                if (UsesConvergentConstruction(state))
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

    internal static JsonObject Context(PlanningSession state, bool review = false, bool intermediate = false)
    {
        var construction = state.Construction!; var compact = State(state);
        var step = compact.CurrentStep ?? throw new PlanningConflictException("Select a business substep before presenting a draft.");
        var draft = construction.Candidate;
        // Typed repairs also use this presentation, before their candidate is
        // valid. Their existing repair context owns editable slots and sources.
        var actualImports = review && draft is { Diagnostics.Count: 0 } && PlanningConstructionPhasePolicies.IsFresh(state.Request);
        var appliedInterface = review && draft is { Diagnostics.Count: 0 } && draft.Extension.CloseScopes.Contains(step.Scope, StringComparer.Ordinal);
        var verified = actualImports && UsesVerifiedReview(state) ? VerifiedReviewInterfaces(state, appliedInterface) : null;
        var diagnostics = draft is not null && PlanningConstructionPhasePolicies.IsFresh(state.Request)
            ? draft.Diagnostics.Concat(state.Diagnostics).Distinct()
            : draft?.Diagnostics ?? state.Diagnostics;
        var context = new JsonObject
        {
            ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["candidateHash"] = draft?.Hash,
            ["step"] = JsonSerializer.SerializeToNode(step, PlanningJsonContext.Default.PlanningConstructionStep),
            ["requirements"] = RequirementNodes(state, UsesConvergentConstruction(state)
                ? step.RequirementIds.Concat(EvidenceRequirementIds(state)) : step.RequirementIds),
            ["commitments"] = new JsonObject(compact.PendingCommitments.Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                JsonSerializer.SerializeToNode(p.Value, PlanningJsonContext.Default.ListPlanningCommitment)))),
            ["globalConstraints"] = Sources(state, compact.GlobalSourceIds), ["instructions"] = state.Request.Policy.Instructions,
            ["interfaces"] = actualImports ? ActualImportedInterfaces(state, verified) : ImportedInterfaces(state, step),
            ["operations"] = ContractViews(state, review),
            ["evidence"] = construction.Evidence?.DeepClone(),
            ["draft"] = draft is null ? null : PlanningJsonTransport.TaskPlanPart(PlanningConstruction.Node(draft.Extension, PlanningJsonContext.Default.PlanningConstructionExtension)),
            ["diagnostics"] = PlanningJsonTransport.Diagnostics(diagnostics),
            ["rebuild"] = RebuildDirectory(state),
            ["reviewScope"] = intermediate ? "junction" : review ? "draft" : "extension"
        };
        PlanningConstruction.AddAuthorizedRevisionDraft(state, context);
        AddRequirementNavigation(state, context);
        if (CompanionOperationNavigation(state) is { } companions) context["companionOperations"] = companions;
        if (DraftContinuationContext(state) is { } continuation) context["continuation"] = continuation;
        if (step.OutputNames.Count > 0)
            context["outputs"] = PlanningConstruction.Node(state.Requirements!.Outputs!.Where(o => step.OutputNames.Contains(o.Name, StringComparer.Ordinal)).ToList(), PlanningJsonContext.Default.ListTaskInput);
        if (step.GroupId is { } groupId) context["group"] = GroupInterface(state, groupId, verified?.Accepted);
        if (ScopeGroup(state, step.Scope) is { } scopeGroup)
        {
            var candidateGroup = verified is not null && UsesDecisionView(state);
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
        AddTaskEvidence(state, context, review);
        if (!review && UsesDeferredBinding(state))
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
        if (actualImports && UsesDeferredBinding(state) &&
            (step.CloseRequirementIds.Count > 0 || draft!.Extension.CloseScopes.Count > 0 || draft.Extension.Complete))
        {
            context["scopeEvidence"] = "scope:" + step.Scope;
            // A declaration can satisfy an interface requirement without any
            // fragment reading its value. Keep its navigation distinct from
            // compiler-derived operands and from isolated group parameters.
            var inputs = state.Plan?.Inputs ?? state.Requirements?.Inputs ?? [];
            if (verified is not null)
            {
                if (ReviewInputDeclarations(state) is { } declarations) context["inputDeclarations"] = declarations;
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
        if (review && draft is not null)
        {
            context["constructionStage"] = new JsonObject
            {
                ["openedScopeCount"] = draft.OpenScopes.Except(construction.OpenScopes, StringComparer.Ordinal).Count(),
                ["closedScopeCount"] = draft.Extension.CloseScopes.Count,
                ["requirementCompletionClaimed"] = step.CloseRequirementIds.Count > 0,
                ["planCompletionClaimed"] = draft.Extension.Complete
            };
            if (UsesVerifiedReview(state))
            {
                var stage = context["constructionStage"]!.AsObject();
                stage.Remove("requirementCompletionClaimed");
                stage["closeRequirementIds"] = new JsonArray(step.CloseRequirementIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            }
            if (verified is not null)
                context["constructionStage"]!["preflight"] = new JsonObject
                {
                    ["status"] = "passed", ["basis"] = "tentative_candidate", ["scope"] = step.Scope,
                    ["scopeOpen"] = draft.OpenScopes.Contains(step.Scope, StringComparer.Ordinal),
                    ["openScopeCount"] = draft.OpenScopes.Count,
                    ["deferred"] = "Unfinished scope interfaces, unresolved business choices, complete requirement coverage, final whole-plan validation, runtime effects and execution authorization remain unverified. Unselected choices use recommendations only for this preflight probe."
                };
            var related = construction.Checkpoints.Where(c => c.TaskIds.Any(draft.ImportedTaskIds.Contains) ||
                intermediate && (c.RequirementIds.Any(step.CloseRequirementIds.Contains) ||
                    UsesConvergentConstruction(state) && draft.Extension.CloseScopes.Count > 0 && c.Step is { } previous &&
                    ScopeWithin(draft.Plan, previous.Scope, step.Scope))).ToArray();
            context["checkpointCount"] = related.Length;
            context["checkpointEvidence"] = "page:checkpoints:0";
            context["checkpoints"] = new JsonArray(related.TakeLast(4).Select(c => (JsonNode)new JsonObject { ["sequence"] = c.Sequence,
                ["hash"] = c.PlanHash, ["requirementIds"] = new JsonArray(c.RequirementIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["tasks"] = new JsonArray(c.TaskIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) }).ToArray());
        }
        if (review && IsCompactPresentation(state))
        {
            if (intermediate)
            {
                // Candidate/checkpoint hashes remain host-owned review identity,
                // not business evidence. Keep the checkpoint navigation itself.
                context.Remove("baseHash");
                context.Remove("candidateHash");
                if (context["checkpoints"] is JsonArray checkpoints)
                    foreach (var checkpoint in checkpoints)
                        checkpoint!.AsObject().Remove("hash");
            }
            // Retain semantic bindings and constraints once, without DTO defaults
            // or a repeated baseline hash. Issued review contexts remain frozen.
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
            AddReviewGroupEvidence(state, context);
        }
        if (PlanningConstruction.TechnicalRejectionContext(state) is { } technicalHistory)
            context["historicalTechnicalRejections"] = technicalHistory;
        if (UsesConvergentConstruction(state) && PlanningConstruction.DefectProgressContext(state, context) is { } progress)
            context["defectProgress"] = progress;
        AddDecisionContext(state, context, review);
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
        if (UsesDecisionView(state)) context["continuation"] = ContinuationContext(state);
        if (DescribedOperationChoices(state) is { } described) context["describedOperations"] = described;
        if (PlanningConstructionPhasePolicies.IsFresh(state.Request) && ScopeOwnerNavigation(state) is { } owner)
            context["scopeOwner"] = owner;
        AddDecisionContext(state, context, review: false);
        // These are optional navigation slots, not fields inside original
        // evidence. Preserve historical issued presentation and nested values.
        return IsCompactPresentation(state) ? TrimNavigation(context) : context;
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
        var explicitBoundaries = IsCompactPresentation(state);
        return new JsonArray(state.Construction!.OpenScopes.Skip(ScopeOffset(state)).Take(8).Select(path => symbols.Scopes.Single(s => s.Path == path))
            .Select(s =>
            {
                var group = state.Plan?.Groups.SingleOrDefault(g => s.Path == "/groups/" + g.Id + "/body");
                var opening = group is null ? null : GroupOpening(state, group.Id);
                var objective = s.Owner?.Objective ?? opening?.Objective;
                var description = objective is null ? null : NavigationDescription(objective);
                var inputGroup = explicitBoundaries ? group ?? ScopeGroup(state, s.Path) : null;
                var entry = TrimNavigation(new JsonObject { ["path"] = s.Path, ["owner"] = s.Owner?.Id, ["kind"] = group is null ? s.Owner?.Kind : "group",
                ["objective"] = description, [ObjectiveTextCompleteField(state)] = objective is null ? null : JsonValue.Create(description == objective),
                ["requirementIds"] = opening is null ? null : new JsonArray(opening.RequirementIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["iterationInputs"] = s.Owner?.Kind == "foreach" ? "item and index" : null,
                ["inputScope"] = group is null && inputGroup is not null ? "/groups/" + inputGroup.Id + "/body" : null,
                ["inputs"] = explicitBoundaries || group is null ? null : new JsonArray(group.Inputs.Take(8).Select(i => (JsonNode)new JsonObject
                    { ["input"] = i.Name, ["typeKind"] = i.Type.Kind, ["evidence"] = "group:" + group.Id }).ToArray()),
                ["evidence"] = group is null ? "scope:" + s.Path : "group:" + group.Id });
                if (explicitBoundaries && PlanningConstructionPhasePolicies.IsFresh(state.Request) && s.Owner is { } owner && description != objective)
                    entry["objectiveEvidence"] = TaskObjectiveAddress(owner, 0);
                if (explicitBoundaries && group is not null)
                {
                    // An empty parameter list is a meaningful isolated boundary,
                    // not optional navigation that can disappear during trimming.
                    entry["isolated"] = true; entry["inputCount"] = group.Inputs.Count;
                    entry["inputs"] = new JsonArray(group.Inputs.Take(8).Select(i => (JsonNode)new JsonObject
                        { ["input"] = i.Name, ["typeKind"] = i.Type.Kind, ["evidence"] = "group:" + group.Id }).ToArray());
                }
                if (UsesConvergentConstruction(state))
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
                if (UsesConvergentConstruction(state))
                {
                    var inputs = PlanningConstruction.TaskNode(t)["inputs"]?.DeepClone() ?? new JsonArray();
                    var bytes = System.Text.Encoding.UTF8.GetByteCount(inputs.ToJsonString());
                    if (bytes <= remainingInputBytes) { item["originalInputs"] = inputs; remainingInputBytes -= bytes; }
                    else item["originalInputsOmitted"] = true;
                }
                return (JsonNode)item;
            }).ToArray()), ["invalidationCount"] = construction.Invalidations.Count };
        if (UsesConvergentConstruction(state) && remaining.Length > 0) result["semantics"] = RollbackSemantics;
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
        if (UsesConvergentConstruction(state) && state.Plan is not null)
        {
            var rows = result.OfType<JsonObject>().ToArray();
            AddAcceptedProducerPorts(state, rows, rows.Select(row => row["task"]?.ToString()).OfType<string>().ToHashSet(StringComparer.Ordinal));
        }
        return result;
    }

    private static JsonObject InputDirectoryEntry(PlanningSession state, TaskInput input)
    {
        var entry = new JsonObject { ["input"] = input.Name, ["typeKind"] = input.Type.Kind, ["evidence"] = "input:" + input.Name };
        if (IsCompactPresentation(state)) entry["scope"] = "/root";
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
        if (UsesDecisionView(state)) result["basis"] = candidate ? "tentative_candidate" : "accepted_declarations";
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
            if (IsCompactPresentation(state) && result.Diagnostics.Count == 0 && interfaces is not null)
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
        if (step.Kind is null ? !UsesDeferredBinding(state) : !TaskKinds.Contains(step.Kind, StringComparer.Ordinal))
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
        if (!UsesDeferredBinding(state) || step.Prerequisites.Count > 4)
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/prerequisites", "Retain at most four evidence-linked current prerequisites.");
        // Issued schemas bound new citations to displayed originals. Once a step
        // is retained, its prerequisite may still cite an earlier declaration
        // after that navigation page expires; citation grants no coverage or edits.
        var available = PrerequisiteAddresses(state, state.Construction!.Requirements.Select(requirement => requirement.Id))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var prerequisite in step.Prerequisites)
        {
            var addresses = prerequisite.EvidenceAddresses.Distinct(StringComparer.Ordinal).ToList();
            if (string.IsNullOrWhiteSpace(prerequisite.Need) || prerequisite.Need.Length > 256 ||
                prerequisite.EvidenceAddresses.Count is < 1 or > 4 ||
                !UsesConvergentConstruction(state) && addresses.Count != prerequisite.EvidenceAddresses.Count)
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
            if (UsesConvergentConstruction(state)) prerequisite.EvidenceAddresses = addresses;
        }
    }

    internal static void RetainPrerequisiteEvidence(PlanningSession state)
    {
        var construction = state.Construction!;
        var addresses = UsesDeferredBinding(state)
            ? State(state).CurrentStep?.Prerequisites?.SelectMany(item => item.EvidenceAddresses).ToHashSet(StringComparer.Ordinal) ?? []
            : [];
        var retained = new JsonObject();
        foreach (var item in construction.Evidence ?? [])
            if (addresses.Contains(item.Key)) retained[item.Key] = item.Value?.DeepClone();
        construction.Evidence = retained.Count > 0 ? retained : null;
        construction.EvidenceRequests.Clear();
        foreach (var address in retained.Select(item => item.Key))
            construction.EvidenceRequests.Add(PlanningConstruction.EvidenceBatchFingerprint(state, [address]));
    }
}
