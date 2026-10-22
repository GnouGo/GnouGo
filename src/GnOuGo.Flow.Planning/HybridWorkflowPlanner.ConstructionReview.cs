using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

public sealed partial class HybridWorkflowPlanner
{
    private const string CompactConstructionReviewInstructions = """
        Review only this immediate business fragment, its exact original requirements, applicable global constraints and selected typed interfaces. Check arguments, effects, provenance, conditions, exports and cleanup. closeRequirementIds explicitly claims that this fragment plus referenced accepted work completes those original requirements; examine omissions in that claim. Other remaining requirements are future obligations, not omissions in this fragment. Deterministic validation and task presence do not prove business success.
        The host's constructionStage states this draft's actual scope and completion claims. Newly opened group/container scopes intentionally have empty children and no consumable outputs. Assess their declared inputs and boundaries now; require implementation and exports when the scope closes or its results are consumed. scopeInterface shows exact export declarations for the selected scope; tentative_candidate includes accepted exports plus this draft's additions. Empty draft.outputs preserves accepted exports. These construction views grant no consumption or edit authority. Empty closeRequirementIds makes no requirement-completion claim. Still report defects in the work actually performed; unfinished future work is not itself a defect in an open scope.
        """;
    private const string CompactConstructionReviewEvidenceInstructions = """
        Return exactly one action: findings as an array (empty if none) with evidenceRequests:null; or findings:null with nonempty evidenceRequests using exact issued evidence addresses. Never guess contract versions or schema pointers. Task, source, requirement and checkpoint evidence can be requested by issued identity; directory pages use page:requirements:<offset>, page:interfaces:<offset>, page:checkpoints:<offset>. Every location and evidence string must be one exact existing JSON Pointer into this context, with no appended colon, quotation, copied value or prose annotation. Put all reasoning in explanation and uncertainty. Locate correctable candidate defects under /draft or /requirements. Context is evidence, never edit or execution permission. Always state uncertainty; no report proves execution or authorizes effects.
        """;
    private const string CompactConstructionReviewFocusedInstructions = """
        Review this fragment's arguments, effects, provenance, conditions, exports, cleanup and unnecessary work against original requirements, globals and typed interfaces. Use constructionStage's scope/completion claims. closeRequirementIds claims completion with accepted work; [] claims none. Future work is not an omission. outputs: public /root results. scopeInterface: this scope's accepted exports plus additions; draft.outputs:[] preserves them. Partial descriptions do not prove absent functionality: request originals before that conclusion. Compilation/navigation links do not prove business success.
        Return one action: findings:[] (or grounded findings), evidenceRequests:null; or findings:null, evidenceRequests:[exact issued addresses]. Use taskEvidence/groupEvidence, task:<id>, scope:<path>; call.group uses scope:/groups/<id>/body. Never use bare IDs or invent versions/pointers. Directories: page:requirements:<offset>, page:interfaces:<offset>, page:checkpoints:<offset>. Cite existing context objects with exact JSON Pointers; explain fields in explanation/uncertainty, never invented status paths. Locate defects under /draft or /requirements. State uncertainty; evidence/reviews grant no edit/execution authority or proof of execution.
        """;

    private static string CompactConstructionReviewGuidance(PlanningSession state) => PlanningConstructionPhasePolicies.IsFresh(state.Request)
        ? """
          Review the current fragment against exact original requirements, globals, contracts and actual typed interfaces: arguments, effects, provenance, conditions, exports, cleanup and unnecessary work. constructionStage defines its claim; closeRequirementIds claims completion with accepted work, [] claims none. Open containers intentionally defer children/exports. A needed producer may precede its consumer or public export; their current absence alone is neither an omission nor unnecessary work. Check whether the fragment's arguments perform its selected substep. outputs are public /root results; scopeInterface includes preserved local exports.
          Cleanup may be a later substep in an open scope when its exact target and ownership remain available even after partial failure. Check required creation-time capture and ownership now; assess missing cleanup at scope or requirement closure. Success-only outputs cannot supply cleanup that must also run after failure.
          interfaces describes imported operands: binding and schema identify what is read at consumerLocation, not the produced result.
          Return one action: findings (empty if none) with evidenceRequests:null, or findings:null with necessary original evidence addresses. Finding locations and evidence use the schema's existing context-object pointers; explain fields and uncertainty in prose. Evidence retrieval uses issued addresses, task:<id>, scope:<path>, or scope:/groups/<call.group>/body; never bare IDs. Navigation, compilation and reviews grant no authority or proof of execution. Partial descriptions cannot prove absent functionality; request the original.
          """ + "\n" + PlanningCompactConstruction.DraftSemantics(state, state.Construction!.Compact!.CurrentStep!, review: PlanningCompactConstruction.UsesDeferredBinding(state))
        :
        (PlanningCompactConstruction.IsCompactPresentation(state) ? CompactConstructionReviewFocusedInstructions :
            CompactConstructionReviewInstructions + "\n" + CompactConstructionReviewEvidenceInstructions) +
        (PlanningCompactConstruction.IsCompactPresentation(state) &&
            state.Construction!.Compact!.CurrentStep?.Kind is "group" or "sequence" or "conditional" or "parallel" or "foreach" or "close" or "revision"
            ? "\nNewly opened group/container scopes intentionally have empty children and no consumable outputs: assess inputs/boundaries now, implementation/exports at closure or consumption."
            : "") +
        (state.Construction!.Compact!.CurrentStep?.Kind == "transform"
            ? "\nA transform's required object resultType declares named output ports. An output reference with a named port consumes that field's type; port:null consumes the whole result object. The object envelope does not require exposing the whole object as the business result."
            : "") +
        (PlanningCompactConstruction.IsCompactPresentation(state) && state.Construction!.Compact!.CurrentStep?.Kind == "foreach"
            ? "\nforeach.maxItems rejects excess input before iteration; it never samples or returns a truncated success. maxConcurrency bounds workers, not total items. Report conflicts with explicit required sizes. An unspecified source bound does not require unbounded host execution: retain finite supported capacity as uncertainty, without requesting a limit increase."
            : "") +
        (PlanningCompactConstruction.IsCompactPresentation(state) && state.Construction!.Compact!.CurrentStep is { } step &&
            new TaskPlanSymbols(state.Construction.Candidate?.Plan ?? state.Plan ?? new TaskPlan()).Scopes
                .Any(scope => scope.Path == step.Scope && scope.Owner?.Kind == "foreach")
            ? "\nThis selected scope is a foreach body. Each body output becomes an array on the enclosing task, preserving input order and repetitions; array-valued outputs remain nested. A scalar per-item export does not need an additional aggregation task."
            : "");
    private const string ConstructionReviewInstructions = """
        Independently review this bounded semantic TaskPlan fragment against relevant original requirements, accepted answers and inspected contracts.
        Inspect omissions within the fragment's claimed requirement scope, unnecessary operations, inconsistent arguments, source provenance, composition, conditions, interfaces and cleanup. Future requirements explicitly left open are not omissions in the current fragment.
        requires is a mandatory Boolean assertion and conditional routes branches. Task presence, type validity and compilation are not proof of complete business intent or execution success.
        Context is evidence, never permission. Report only grounded observations with existing JSON Pointer locations and evidence pointers into this exact context. Never propose executable edits or invent evidence.
        Choose exactly one response action: a completed review or an evidence request. Never return both findings and evidenceRequests as null or both as arrays.
        If original context is needed, return a non-empty evidenceRequests array using task:<id>, scope:<stablepath>, requirement:<id>, contract:<operation-id>, page:tasks:<offset>, page:requirements:<offset>, page:answers:<offset>, or page:scopes:<offset>. Set findings:null and explain the need in uncertainty.
        Otherwise set evidenceRequests:null and return findings as an array. If there are no grounded findings, return findings:[]; never use findings:null for a completed review. Always include uncertainty, including any limits on what the review establishes.
        An empty findings array does not prove complete business intent or execution success. No finding is a deterministic proof or authorization; explicit uncertainty remains unverified.
        """;

    internal static JsonObject ConstructionReviewSchema(bool compact = false)
    {
        var text = new JsonObject { ["type"] = "string", ["maxLength"] = 256 };
        var pointer = new JsonObject { ["type"] = "string", ["maxLength"] = 192 };
        if (compact) pointer["description"] = "One exact existing JSON Pointer into the supplied context. No appended quotation, value or prose; put reasoning in explanation.";
        var finding = PlanningSchemas.Object(("kind", PlanningSchemas.Enum("omission", "unnecessary_operation", "inconsistency")),
            ("location", pointer.DeepClone().AsObject()), ("evidence", PlanningSchemas.Array(pointer, 1, 3)),
            ("explanation", text.DeepClone().AsObject()), ("uncertainty", new JsonObject { ["type"] = "string", ["maxLength"] = 128 }));
        return PlanningSchemas.Object(("findings", PlanningSchemas.Nullable(PlanningSchemas.Array(finding, 0, 3))),
            ("uncertainty", text), ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(new JsonObject { ["type"] = "string", ["maxLength"] = compact ? 2048 : 512 }, 1, 8))));
    }

    internal static JsonObject ConstructionReviewSchema(JsonObject context)
    {
        var locations = new List<string>();
        foreach (var key in new[] { "tasks", "always", "outputs", "groups", "choices" })
            AddEntries(context["draft"]?[key], "/draft/" + key, locations);
        AddEntries(context["requirements"], "/requirements", locations);
        if (context["commitments"] is JsonObject commitments)
            foreach (var item in commitments.Where(item => item.Value is JsonArray { Count: > 0 }))
                locations.Add("/commitments/" + Escape(item.Key));
        var evidence = new List<string>();
        foreach (var key in new[] { "requirements", "globalConstraints", "interfaces", "operations", "outputs", "choices", "checkpoints" })
            AddEntries(context[key], "/" + key, evidence);
        evidence.AddRange(locations);
        foreach (var key in new[] { "draft", "step", "group", "scopeGroup", "scopeInterface", "constructionStage" })
            if (context[key] is JsonObject { Count: > 0 }) evidence.Add("/" + key);
        if (context["evidence"] is JsonObject supplied)
            foreach (var item in supplied) evidence.Add("/evidence/" + Escape(item.Key));
        var grounded = evidence.Distinct(StringComparer.Ordinal)
            .Where(pointer => ConstructionReviewEvidenceGrounded(context, pointer, compact: true)).ToArray();
        var schema = ConstructionReviewSchema(compact: true);
        var findings = schema["properties"]!["findings"]!["anyOf"]!.AsArray()
            .Single(node => node?["type"]?.ToString() == "array")!.AsObject();
        if (locations.Count == 0 || grounded.Length == 0) findings["maxItems"] = 0;
        else
        {
            var properties = findings["items"]!["properties"]!.AsObject();
            properties["location"] = PlanningSchemas.Enum(locations.Distinct(StringComparer.Ordinal).ToArray());
            properties["evidence"] = PlanningSchemas.Array(PlanningSchemas.Enum(grounded), 1, 3);
        }
        return schema;

        static string Escape(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        static void AddEntries(JsonNode? node, string prefix, List<string> target)
        {
            if (node is not JsonArray { Count: > 0 } array) return;
            // Large supplied collections remain groundable through their exact
            // existing parent, without an unbounded nested-pointer directory.
            if (array.Count > 16 && prefix != "/requirements") { target.Add(prefix); return; }
            for (var index = 0; index < array.Count; index++)
                if (array[index] is JsonObject { Count: > 0 }) target.Add(prefix + "/" + index);
        }
    }

    internal static (JsonObject Context, LLMRequest Request) CreateConstructionReviewRequest(PlanningSession state, bool intermediate)
    {
        var construction = state.Construction!;
        var guidance = construction.Version == 2 ? CompactConstructionReviewGuidance(state) : ConstructionReviewInstructions;
        if (!PlanningConstructionPhasePolicies.IsFresh(state.Request))
            return PlanningConstruction.Request(state, guidance, _ => ConstructionReviewSchema(compact: construction.Version == 2),
                construction.Limits.ReviewOutputTokens, review: true, intermediate);
        var context = PlanningCompactConstruction.Context(state, review: true, intermediate);
        var purpose = intermediate ? PlanningConstruction.IntermediatePurpose : PlanningConstruction.LocalPurpose;
        return (context, PlanningCompactConstruction.Request(guidance, context, ConstructionReviewSchema(context), purpose));
    }

    private static async Task ReviewConstructionAsync(PlanningSession state, IPlanningRuntime runtime, bool intermediate, CancellationToken ct)
    {
        var construction = state.Construction!; var draft = construction.Candidate!;
        var purpose = intermediate ? PlanningConstruction.IntermediatePurpose : PlanningConstruction.LocalPurpose;
        state.Phase = purpose;
        if (state.PendingCall is null && PlanningModelCalls.RemainingCalls(state) == 0)
        {
            state.Diagnostics = [new("CONSTRUCTION_REVIEW_ALLOWANCE", "/construction/candidate", "The mandatory fragment review cannot fit the retained allowance. The tentative draft is not accepted.")];
            Stop(state); return;
        }
        GnOuGo.Flow.Core.Runtime.LLMRequest request;
        if (state.PendingCall is null)
        {
            (construction.PendingReviewContext, request) = CreateConstructionReviewRequest(state, intermediate);
            construction.PendingReviewHash = PlanningGraphCompiler.Fingerprint(purpose + "\n" + draft.Hash + "\n" + construction.PendingReviewContext.ToJsonString());
        }
        else request = state.PendingCall.Request;
        var context = construction.PendingReviewContext ?? throw new PlanningConflictException("A retained fragment review has no original context.");
        var fingerprint = PlanningGraphCompiler.Fingerprint(purpose + "\n" + draft.Hash + "\n" + context.ToJsonString());
        if (construction.PendingReviewHash != fingerprint || draft.BaseHash != PlanningConstruction.Hash(state.Plan))
            throw new PlanningConflictException("The retained fragment review no longer matches its candidate and checkpoint.");
        JsonNode response;
        try
        {
            response = await PlanningModelCalls.CallAsync(state, runtime, purpose, request.Prompt,
                request.StructuredOutputSchema!.AsObject(), ct, construction.Limits.ReviewOutputTokens);
        }
        catch (PlanningResponseException)
        {
            Inconclusive("The fragment Judge response did not satisfy its issued schema."); return;
        }
        catch (System.Text.Json.JsonException)
        {
            Inconclusive("The fragment Judge response was not valid JSON."); return;
        }
        catch (WorkflowRuntimeException ex) when (state.PendingCall is null && ex.Code is "MODEL_INPUT_LIMIT" or "MODEL_OUTPUT_LIMIT" or "LLM_BUDGET_EXCEEDED")
        {
            Inconclusive("The mandatory fragment review could not complete within its retained allowance."); return;
        }
        if ((response["findings"] is null) == (response["evidenceRequests"] is null))
        { Inconclusive("The fragment Judge must return either findings or an evidence request."); return; }
        if (response["evidenceRequests"] is JsonArray evidenceRequests)
        {
            try
            {
                PlanningConstruction.LoadEvidence(state, evidenceRequests.Select(e => e!.ToString()).ToList(),
                    followup: probe =>
                    {
                        var followup = CreateConstructionReviewRequest(probe, intermediate).Request;
                        return PlanningModelCalls.PrepareRequest(probe, purpose, followup.Prompt, followup.StructuredOutputSchema!.AsObject(),
                            probe.Construction!.Limits.ReviewOutputTokens);
                    },
                    purpose: purpose);
            }
            catch (PlanningResponseException) { Inconclusive("The fragment Judge requested unavailable or repeated evidence."); return; }
            construction.PendingReviewContext = null; construction.PendingReviewHash = null;
            return;
        }
        var findings = response["findings"]!.AsArray();
        if (PlanningBusinessJudge.ReferenceDiagnostics(response, context, out _).Count > 0 ||
            response["uncertainty"]!.ToString().Length > 8192 || findings.Any(f =>
            f!["explanation"]!.ToString().Length is 0 or > 8192 || f["uncertainty"]!.ToString().Length > 8192 ||
            !PlanningConstruction.TryPointer(context, f["location"]!.ToString(), out _) ||
            f["evidence"]!.AsArray().Any(e => !ConstructionReviewEvidenceGrounded(context, e!.ToString(), construction.Version == 2))))
        { Inconclusive("The fragment Judge supplied invalid references or unsupported report lengths; the entire report is unusable."); return; }
        var retainedReview = new JsonObject { ["purpose"] = purpose, ["fingerprint"] = fingerprint, ["candidateHash"] = draft.Hash,
            ["outcome"] = findings.Count == 0 ? "unverified" : "revision_required", ["response"] = response.DeepClone() };
        if (construction.Version == 2) retainedReview["closedRequirementIds"] = new JsonArray(construction.Compact!.CurrentStep!.CloseRequirementIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        draft.Reviews.Add(retainedReview);
        construction.PendingReviewContext = null; construction.PendingReviewHash = null;
        if (findings.Count > 0)
        {
            var diagnostics = new List<PlanningDiagnostic>(); var unauthorized = false;
            foreach (var finding in findings)
            {
                var location = finding!["location"]!.ToString();
                _ = PlanningConstruction.TryPointer(context, location, out var node);
                string? id = null;
                for (var current = node; current is not null; current = current.Parent)
                    if (current is JsonObject obj && obj["id"] is JsonValue value && draft.TaskIds.Contains(value.ToString(), StringComparer.Ordinal))
                    { id = value.ToString(); break; }
                var allowed = location.StartsWith("/draft/", StringComparison.Ordinal) || id is not null ||
                    location.StartsWith("/requirements/", StringComparison.Ordinal) ||
                    construction.Version == 2 && location.StartsWith("/commitments/", StringComparison.Ordinal);
                unauthorized |= !allowed;
                diagnostics.Add(new(allowed ? "CONSTRUCTION_BUSINESS_REVIEW" : "BUSINESS_REVIEW_REQUIRED",
                    id is null ? "/construction/candidate" : "/tasks/" + id,
                    finding["kind"] + ": " + finding["explanation"] + " Uncertainty: " + finding["uncertainty"],
                    ValidationStage: purpose, Rule: finding["kind"]!.ToString()));
            }
            draft.Diagnostics = diagnostics; state.Diagnostics = diagnostics;
            PlanningConstruction.RecordRejection(state, draft);
            if (unauthorized) Stop(state);
            return;
        }
        if (intermediate) draft.IntermediateReviewed = true; else draft.LocalReviewed = true;
        if (!draft.NeedsIntermediateReview || draft.IntermediateReviewed) await AcceptConstructionAsync(state, runtime, ct);

        void Inconclusive(string description)
        {
            draft.Reviews.Add(new JsonObject { ["purpose"] = purpose, ["fingerprint"] = fingerprint, ["candidateHash"] = draft.Hash,
                ["outcome"] = "inconclusive", ["description"] = description });
            state.Diagnostics = [new("CONSTRUCTION_REVIEW_INCONCLUSIVE", "/construction/candidate", description + " No checkpoint or edit authority was created.")];
            Stop(state);
        }
    }

    internal static bool ConstructionReviewEvidenceGrounded(JsonObject context, string pointer, bool compact)
    {
        var root = pointer.Split('/').ElementAtOrDefault(1);
        if (root is not ("request" or "revision" or "requirements" or "answers" or "tasks" or "operations" or "interfaces" or "draft" or "evidence" or "closures") &&
            !(compact && root is "globalConstraints" or "step" or "checkpoints" or "outputs" or "commitments" or "choices" or "group" or "scopeGroup" or "scopeInterface" or "constructionStage")) return false;
        if (!PlanningConstruction.TryPointer(context, pointer, out var evidence) || evidence is null) return false;
        return evidence is not JsonArray { Count: 0 } && evidence is not JsonObject { Count: 0 } &&
            (evidence is not JsonValue || !string.IsNullOrWhiteSpace(evidence.ToString()));
    }
}
