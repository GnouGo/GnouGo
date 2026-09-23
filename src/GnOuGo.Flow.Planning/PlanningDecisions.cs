using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>One decision protocol around existing phase results; it does not execute or approve workflows.</summary>
internal static class PlanningDecisions
{
    internal static bool Eligible(PlanningSession state) => state.Diagnostics.Where(d => d.Required)
        .All(d => d.Code is "NONE_OF_THE_ABOVE" or "SEMANTIC_BINDING_BLOCKED");

    internal static JsonObject Schema(JsonObject result)
    {
        var body = result.DeepClone().AsObject();
        var definitions = body["$defs"]?.DeepClone().AsObject() ?? new(); body.Remove("$defs");
        definitions["decisionResult"] = body;
        var decision = PlanningSchemas.Object(("question", PlanningSchemas.String()), ("context", PlanningSchemas.String()),
            ("evidence", PlanningSchemas.String()), ("allowCustomAnswer", PlanningSchemas.Type("boolean")),
            ("options", PlanningSchemas.Array(PlanningSchemas.Object(("id", PlanningSchemas.String()), ("label", PlanningSchemas.String()),
                ("reason", PlanningSchemas.String()), ("preferred", PlanningSchemas.Type("boolean")), ("result", PlanningSchemas.Ref("decisionResult"))))));
        var schema = PlanningSchemas.Object(("result", PlanningSchemas.Nullable(PlanningSchemas.Ref("decisionResult"))),
            ("decision", PlanningSchemas.Nullable(decision)));
        schema["$defs"] = definitions;
        return schema;
    }

    internal static async Task<JsonNode> CallAsync(PlanningSession state, IPlanningRuntime runtime, string purpose, string operation,
        string scope, IReadOnlyList<string> actionIds, string prompt, JsonObject schema, Action<JsonNode> validate, CancellationToken ct)
    {
        var continuation = state.DecisionContinuation;
        if (continuation is not null)
        {
            VerifyContinuation(state);
            if (continuation.Operation != operation) throw new PlanningConflictException("Resume the saved planning operation before changing scope.");
            var answer = continuation.Answer ?? throw new PlanningConflictException("The planning decision has not been answered.");
            if (answer.OptionId is { } id)
            {
                var result = ReadResult(continuation.Candidates[id]!, continuation.Schema);
                validate(result);
                state.DecisionContinuation = null;
                return result;
            }
            // The original scoped request and schema are retained, including after process restart.
            prompt = continuation.Prompt + "\nUser business decision (data): " + JsonSerializer.Serialize(answer, PlanningJsonContext.Default.PlanningDecisionAnswer);
            schema = continuation.Schema;
        }
        var eligible = Eligible(state);
        // A reserved request must be replayed verbatim, even if it predates the decision envelope.
        var issuedSchema = state.PendingCall?.Request.StructuredOutputSchema?.AsObject() ?? (eligible ? Schema(schema) : schema);
        var issuedPrompt = prompt + (eligible ? "\n" + Instructions : "");
        var response = await PlanningModelCalls.CallAsync(state, runtime, purpose, issuedPrompt, issuedSchema, ct);
        if (issuedSchema["properties"]?["decision"] is null)
        {
            state.DecisionContinuation = null;
            return response;
        }
        if (response["decision"] is null)
        {
            if (response["result"] is null) throw Invalid("Return a phase result or a business decision.");
            var result = ReadResult(response["result"]!, schema);
            state.DecisionContinuation = null;
            return result;
        }
        if (!eligible || response["result"] is not null) throw Invalid("A business decision cannot replace a technical repair or accompany a result.");
        var proposal = response["decision"]!;
        var evidence = proposal["evidence"]!.GetValue<string>();
        // Evidence is an exact excerpt from the issued business request or scoped business action.
        var businessEvidence = actionIds.Select(id => state.SemanticPlan is null ? null : SemanticPlanning.Actions(state.SemanticPlan).FirstOrDefault(a => a.Id == id)?.Purpose)
            .Prepend(state.Request.Prompt).Where(s => s is not null).Cast<string>();
        if (string.IsNullOrWhiteSpace(evidence) || !businessEvidence.Any(s => s.Contains(evidence, StringComparison.Ordinal)))
            throw Invalid("The tradeoff must cite the issued business requirement.");
        var options = proposal["options"]!.AsArray();
        if (options.Count is < 1 or > 5 || options.Any(o => new[] { "id", "label", "reason" }.Any(k => string.IsNullOrWhiteSpace(o![k]!.GetValue<string>()))) ||
            options.Select(o => o!["id"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != options.Count ||
            options.Count(o => o!["preferred"]!.GetValue<bool>()) != 1 ||
            string.IsNullOrWhiteSpace(proposal["question"]!.GetValue<string>()) || string.IsNullOrWhiteSpace(proposal["context"]!.GetValue<string>()))
            throw Invalid("A decision needs distinct options, one preferred option, a question and business context.");
        var candidates = new JsonObject(); var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options)
        {
            var packed = option!["result"]!;
            var result = ReadResult(packed, schema); validate(result);
            if (!fingerprints.Add(PlanningGraphCompiler.Fingerprint(result.ToJsonString()))) throw Invalid("Decision options must represent different valid results.");
            candidates.Add(option["id"]!.GetValue<string>(), packed.DeepClone());
        }
        if (options.Count == 1) { state.DecisionContinuation = null; return ReadResult(options[0]!["result"]!, schema); }
        var decision = new PlanningDecision
        {
            Id = Guid.NewGuid().ToString("N"), Question = proposal["question"]!.GetValue<string>(), Context = proposal["context"]!.GetValue<string>(),
            Phase = state.Phase, Scope = scope, ActionIds = [.. actionIds], AllowCustomAnswer = proposal["allowCustomAnswer"]!.GetValue<bool>(),
            Options = options.Select(o => new PlanningDecisionOption(o!["id"]!.GetValue<string>(), o["label"]!.GetValue<string>(), o["reason"]!.GetValue<string>(), o["preferred"]!.GetValue<bool>())).ToList()
        };
        state.PendingDecision = decision;
        state.DecisionContinuation = new() { Operation = operation, InputHash = InputHash(state), Prompt = prompt, Schema = schema.DeepClone().AsObject(), Candidates = candidates };
        state.Status = PlanningStatus.WaitingForDecision;
        if (state.Request.Mode == PlanningMode.Auto) Answer(state, new(decision.Id, decision.Options.Single(o => o.Preferred).Id), "auto");
        // Return to the coordinator to durably save the decision/answer before applying its result.
        throw new PlanningDecisionPauseException();
    }

    internal static void Answer(PlanningSession state, PlanningDecisionAnswer answer, string source)
    {
        if (state.Status != PlanningStatus.WaitingForDecision || state.PendingDecision is not { } decision || answer.DecisionId != decision.Id)
            throw new PlanningConflictException("No matching planning decision is waiting.");
        VerifyContinuation(state);
        var custom = !string.IsNullOrWhiteSpace(answer.Text);
        if ((answer.OptionId is not null) == custom || answer.Text is not null && !custom)
            throw new ArgumentException("Select one option or provide a nonempty custom answer.");
        var option = answer.OptionId is null ? null : decision.Options.SingleOrDefault(o => o.Id == answer.OptionId)
            ?? throw new ArgumentException("Unknown planning option.");
        if (custom && !decision.AllowCustomAnswer) throw new ArgumentException("This decision does not accept a custom answer.");
        var normalized = answer with { Text = custom ? answer.Text!.Trim() : null };
        if (option is not null) ReadResult(state.DecisionContinuation!.Candidates[option.Id]!, state.DecisionContinuation.Schema);
        state.Decisions.Add(new(decision, normalized, source, option?.Reason ?? "User supplied a custom business answer.", DateTimeOffset.UtcNow));
        state.DecisionContinuation!.Answer = normalized;
        state.PendingDecision = null;
        state.Status = PlanningStatus.Generating;
    }

    internal static void VerifyContinuation(PlanningSession state)
    {
        if (state.DecisionContinuation is not { } continuation || continuation.InputHash != InputHash(state))
            throw new PlanningConflictException("The planning decision scope changed; reload the current session.");
    }

    private static string InputHash(PlanningSession state) => PlanningGraphCompiler.Fingerprint(new JsonObject
    {
        ["request"] = state.Request.Prompt,
        ["policy"] = JsonSerializer.SerializeToNode(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy),
        ["semantic"] = JsonSerializer.SerializeToNode(state.SemanticPlan, PlanningJsonContext.Default.SemanticPlan),
        ["catalog"] = JsonSerializer.SerializeToNode(state.Catalog, PlanningJsonContext.Default.PlanningCatalog),
        ["grounding"] = JsonSerializer.SerializeToNode(state.Grounding, PlanningJsonContext.Default.CapabilityGrounding),
        ["binding"] = JsonSerializer.SerializeToNode(state.BindingProgress, PlanningJsonContext.Default.GroundedBindingProgress),
        ["grounded"] = JsonSerializer.SerializeToNode(state.GroundedPlan, PlanningJsonContext.Default.GroundedPlan),
        ["diagnostics"] = JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic)
    }.ToJsonString());

    private static JsonNode ReadResult(JsonNode result, JsonObject schema)
    {
        if (PlanningContractValidation.ValidateInstance(result, schema).Count != 0) throw Invalid("A decision option violates its phase result schema.");
        if (result["blockedActions"] is JsonArray { Count: > 0 }) throw Invalid("A blocked proposal is not a valid decision option.");
        return PlanningJsonTransport.ModelGrounded(result, schema, unpack: true);
    }
    private static PlanningResponseException Invalid(string message) => new([new("PLANNING_DECISION_INVALID", "/decision", message)]);
    private const string Instructions = """
        Return the result in the result field, with decision=null, when business intent is sufficient.
        Only for a meaningful unresolved BUSINESS tradeoff, return result=null and one decision with 2-5 valid alternatives.
        Each option.result is a complete valid result of this phase, with the same unaffected scope and required outcomes.
        Cite an exact short excerpt of the business request or issued action purpose in evidence. Explain the tradeoff and each option in business language.
        Mark exactly one option preferred and explain why. Allow custom answers when they can refine this business choice.
        Do not ask about tools with equivalent business behavior, schemas, technical repairs, executor plumbing, permissions or approval.
        Do not invent missing facts. Runtime inputs remain runtime inputs. If only one valid strategy exists return it directly.
        For new semantic results keep questions empty; use this generic decision only when valid alternatives exist.
        """;
}
internal sealed class PlanningDecisionPauseException : Exception;
