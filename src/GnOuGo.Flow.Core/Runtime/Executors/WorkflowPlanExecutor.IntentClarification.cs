using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private const int DefaultIntentClarificationMaxRounds = 2;
    private const int DefaultIntentClarificationMaxQuestions = 8;
    private const int DefaultIntentClarificationMaxQuestionsPerRound = 5;
    private const int MaxIntentClarificationAnswerCharacters = 8_000;
    private const int MaxIntentClarificationTotalAnswerCharacters = 32_000;

    private sealed record IntentClarificationConfig(
        string Mode,
        int TimeoutMs,
        int MaxRounds,
        int MaxQuestions,
        int MaxQuestionsPerRound)
    {
        public bool Enabled => !string.Equals(Mode, "off", StringComparison.Ordinal);
    }

    private sealed record IntentClarificationOption(
        string Value,
        string Description,
        bool Recommended,
        string ExternalWriteConfirmationPolicy = "unchanged");

    private sealed record IntentClarificationQuestion(
        string Id,
        string Prompt,
        IReadOnlyList<IntentClarificationOption> Options);

    private sealed record IntentClarificationAssessment(
        string Outcome,
        string Reason,
        IReadOnlyList<IntentClarificationQuestion> Questions);

    private sealed record IntentClarificationAnswer(
        string QuestionId,
        string Question,
        string Answer,
        string SelectedDescription,
        string ExternalWriteConfirmationPolicy,
        bool IsCustom);

    private sealed class IntentClarificationSession
    {
        public IntentClarificationSession(
            IntentClarificationConfig config,
            string rawRequest,
            string callerContext)
        {
            Config = config;
            RawRequest = rawRequest;
            CallerContext = callerContext;
        }

        public IntentClarificationConfig Config { get; }
        public string RawRequest { get; }
        public string CallerContext { get; }
        public List<IntentClarificationAnswer> Answers { get; } = [];
        private HashSet<string> HandledCapabilityRelaxations { get; } = new(StringComparer.Ordinal);
        public int FormsUsed { get; private set; }
        public int QuestionsUsed { get; private set; }
        public int RemainingRounds => Math.Max(0, Config.MaxRounds - FormsUsed);
        public int RemainingQuestions => Math.Max(0, Config.MaxQuestions - QuestionsUsed);

        public int CurrentRoundQuestionLimit => Math.Min(
            Config.MaxQuestionsPerRound,
            RemainingQuestions);

        public bool CanAsk(int questionCount) =>
            RemainingRounds > 0
            && questionCount > 0
            && questionCount <= CurrentRoundQuestionLimit;

        public bool TryBeginCapabilityRelaxation(string fingerprint)
            => HandledCapabilityRelaxations.Add(fingerprint);

        public void AddAnswers(
            IReadOnlyList<IntentClarificationQuestion> questions,
            IReadOnlyDictionary<string, string> answers)
        {
            if (!CanAsk(questions.Count))
                throw new InvalidOperationException("Intent clarification budget was exceeded.");

            foreach (var question in questions)
            {
                var answer = answers[question.Id];
                var selectedOption = question.Options.FirstOrDefault(option =>
                    string.Equals(option.Value, answer, StringComparison.Ordinal));
                Answers.Add(new IntentClarificationAnswer(
                    question.Id,
                    question.Prompt,
                    answer,
                    selectedOption?.Description ?? string.Empty,
                    selectedOption?.ExternalWriteConfirmationPolicy ?? "unchanged",
                    selectedOption is null));
            }
            FormsUsed++;
            QuestionsUsed += questions.Count;
        }

        public JsonArray BuildAnswersJson() => new(Answers.Select(static answer =>
            (JsonNode)new JsonObject
            {
                ["question_id"] = answer.QuestionId,
                ["question"] = answer.Question,
                ["answer"] = answer.Answer,
                ["selected_description"] = answer.SelectedDescription,
                ["external_write_confirmation_policy"] = answer.ExternalWriteConfirmationPolicy,
                ["is_custom"] = answer.IsCustom
            }).ToArray());

        public JsonObject BuildSafeMetadata(string stage, string outcome, string recommendedAction) => new()
        {
            ["planning_outcome"] = outcome,
            ["clarification_stage"] = stage,
            ["clarification_rounds"] = FormsUsed,
            ["clarification_questions"] = QuestionsUsed,
            ["recommended_action"] = recommendedAction
        };
    }

    /// <summary>Internal control-flow signal used to restart the complete planning attempt.</summary>
    private sealed class WorkflowPlanClarificationRestartException : Exception
    {
    }

    private static IntentClarificationConfig ParseIntentClarificationConfig(JsonObject input)
    {
        if (input["intent_clarification"] is null)
        {
            return new IntentClarificationConfig(
                "off",
                HumanInputContract.DefaultTimeoutMs,
                DefaultIntentClarificationMaxRounds,
                DefaultIntentClarificationMaxQuestions,
                DefaultIntentClarificationMaxQuestionsPerRound);
        }

        if (input["intent_clarification"] is not JsonObject config)
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.plan intent_clarification must be an object.");

        var mode = config["mode"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "off";
        if (mode is not ("off" or "when_needed" or "always"))
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                $"workflow.plan intent_clarification mode '{mode}' is invalid. Use off, when_needed, or always.");

        var timeoutMs = ReadBoundedIntentClarificationInteger(
            config,
            "timeout_ms",
            HumanInputContract.DefaultTimeoutMs,
            1,
            int.MaxValue);
        var maxRounds = ReadBoundedIntentClarificationInteger(
            config,
            "max_rounds",
            DefaultIntentClarificationMaxRounds,
            1,
            5);
        var maxQuestions = ReadBoundedIntentClarificationInteger(
            config,
            "max_questions",
            DefaultIntentClarificationMaxQuestions,
            1,
            20);
        var maxQuestionsPerRound = ReadBoundedIntentClarificationInteger(
            config,
            "max_questions_per_round",
            DefaultIntentClarificationMaxQuestionsPerRound,
            1,
            10);
        if (maxQuestionsPerRound > maxQuestions)
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "workflow.plan intent_clarification.max_questions_per_round cannot exceed max_questions.");

        return new IntentClarificationConfig(
            mode,
            timeoutMs,
            maxRounds,
            maxQuestions,
            maxQuestionsPerRound);
    }

    private static int ReadBoundedIntentClarificationInteger(
        JsonObject config,
        string property,
        int defaultValue,
        int minimum,
        int maximum)
    {
        if (config[property] == null)
            return defaultValue;
        if (config[property] is not JsonValue value
            || !value.TryGetValue<int>(out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                $"workflow.plan intent_clarification.{property} must be an integer between {minimum} and {maximum}.");
        }
        return parsed;
    }

    private async Task<IntentClarificationSession?> PrepareIntentClarificationAsync(
        StepExecutionContext ctx,
        JsonObject input,
        CancellationToken ct)
    {
        var config = ParseIntentClarificationConfig(input);
        if (!config.Enabled)
            return null;

        var generator = input["generator"] as JsonObject ?? new JsonObject();
        var rawRequest = input["raw_prompt"]?.GetValue<string>()
                         ?? generator["raw_prompt"]?.GetValue<string>()
                         ?? generator["instruction"]?.GetValue<string>()
                         ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawRequest))
            throw BuildIntentClarificationFailure(
                null,
                "up_front",
                "clarification_request_missing",
                "Intent clarification requires a non-empty raw prompt or generator instruction.");

        var session = new IntentClarificationSession(
            config,
            rawRequest,
            generator["context"]?.GetValue<string>() ?? string.Empty);
        var requireQuestions = string.Equals(config.Mode, "always", StringComparison.Ordinal);
        const string stage = "up_front";
        var assessment = await AnalyzeIntentClarificationAsync(
            ctx,
            input,
            session,
            stage,
            issueContext: null,
            requireQuestions,
            ct);

        if (assessment.Outcome == "sufficient")
            return session;
        if (assessment.Outcome == "cannot_plan_safely")
            throw BuildCannotPlanSafely(session, stage, assessment.Reason);
        if (!session.CanAsk(assessment.Questions.Count))
            throw BuildCannotPlanSafely(
                session,
                stage,
                "The request still needs clarification, but the configured clarification budget is exhausted.");

        await RequestIntentClarificationFormAsync(ctx, session, stage, assessment, ct);
        return session;
    }

    private async Task RequestReactiveIntentClarificationAsync(
        StepExecutionContext ctx,
        JsonObject input,
        IntentClarificationSession session,
        string stage,
        JsonNode issueContext,
        CancellationToken ct)
    {
        if (session.RemainingRounds == 0 || session.RemainingQuestions == 0)
            throw BuildCannotPlanSafely(
                session,
                stage,
                "A genuine user-intent ambiguity remains after the configured clarification budget.");

        var assessment = await AnalyzeIntentClarificationAsync(
            ctx,
            input,
            session,
            stage,
            issueContext,
            requireQuestions: true,
            ct);
        if (assessment.Outcome == "cannot_plan_safely")
            throw BuildCannotPlanSafely(session, stage, assessment.Reason);
        if (assessment.Outcome != "questions" || !session.CanAsk(assessment.Questions.Count))
            throw BuildCannotPlanSafely(
                session,
                stage,
                "The remaining ambiguity could not be converted into a valid clarification form within the configured budget.");

        await RequestIntentClarificationFormAsync(ctx, session, stage, assessment, ct);
    }

    private async Task<IntentClarificationAssessment> AnalyzeIntentClarificationAsync(
        StepExecutionContext ctx,
        JsonObject input,
        IntentClarificationSession session,
        string stage,
        JsonNode? issueContext,
        bool requireQuestions,
        CancellationToken ct)
    {
        var llmClient = ctx.Engine.LLMClient
            ?? throw BuildIntentClarificationFailure(
                session,
                stage,
                "clarification_llm_unavailable",
                "Intent clarification requires an LLM client.");
        var generator = input["generator"] as JsonObject ?? new JsonObject();
        var (provider, resolvedModel) = ctx.Engine.ResolveLlmTarget(
            generator["provider"]?.GetValue<string>(),
            generator["model"]?.GetValue<string>());
        var model = resolvedModel ?? "gpt-4";
        var reasoning = generator["reasoning"]?.GetValue<string>() ?? "medium";
        string? previousResponse = null;
        string? previousError = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var prompt = BuildIntentClarificationPrompt(
                session,
                stage,
                issueContext,
                requireQuestions,
                previousResponse,
                previousError);
            try
            {
                var response = await ctx.CallLLMAsync(llmClient, new LLMRequest
                {
                    Provider = provider,
                    Model = model,
                    Prompt = prompt,
                    Reasoning = reasoning,
                    UseBackgroundMode = true,
                    StructuredOutputSchema = BuildIntentClarificationSchema(session.CurrentRoundQuestionLimit),
                    StructuredOutputStrict = true
                }, "workflow.plan.intent_clarification", ct);
                previousResponse = response.Text;
                var assessment = ParseIntentClarificationAssessment(
                    ParseStructuredObject(response, "intent clarification"),
                    session,
                    requireQuestions);

                ctx.SetTelemetryAttribute("gnougo-flow.plan.intent_clarification.last_outcome", assessment.Outcome);
                ctx.SetTelemetryAttribute("gnougo-flow.plan.intent_clarification.forms_used", session.FormsUsed);
                ctx.SetTelemetryAttribute("gnougo-flow.plan.intent_clarification.questions_used", session.QuestionsUsed);
                return assessment;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (WorkflowPlanDiagnostics.IsNonRepairableLlmFailure(ex))
            {
                throw;
            }
            catch (Exception ex) when (attempt == 1)
            {
                previousError = TruncateIntentClarificationText(ex.Message, 1_000);
                ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
                {
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.message", "Intent clarification analysis was invalid; performing one bounded contract repair."),
                    new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info")
                });
            }
            catch (Exception ex)
            {
                throw BuildIntentClarificationFailure(
                    session,
                    stage,
                    "clarification_analysis_invalid",
                    "Intent clarification analysis remained invalid after one bounded repair attempt.",
                    ex);
            }
        }

        throw BuildIntentClarificationFailure(
            session,
            stage,
            "clarification_analysis_invalid",
            "Intent clarification analysis did not return a result.");
    }

    private static string BuildIntentClarificationPrompt(
        IntentClarificationSession session,
        string stage,
        JsonNode? issueContext,
        bool requireQuestions,
        string? previousResponse,
        string? previousError)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a provider-neutral workflow intent clarification analyst.");
        sb.AppendLine("Return only the structured JSON required by the supplied schema.");
        sb.AppendLine("Classify the request as questions, sufficient, or cannot_plan_safely.");
        sb.AppendLine("Ask every material product-intent question needed for behavior, scope, inputs, outputs, success criteria, failure policy, permissions, and externally visible effects, without adding filler questions.");
        sb.AppendLine("Never ask for MCP server names, tool names, catalog identifiers, implementation details discoverable from contracts, repair of malformed model output, or a decision whose value will only be known while the workflow runs.");
        sb.AppendLine("For runtime-dependent behavior, preserve the decision rule and future data source; do not ask the human to predict the future result.");
        sb.AppendLine("Each question must have two or three mutually exclusive proposed answers. Put the best AI recommendation first, mark only it recommended, and explain the impact of every answer.");
        sb.AppendLine("Every option must classify its provider-neutral external-write confirmation consequence as required, forbidden, or unchanged. Use required or forbidden only when that option directly decides whether a human confirmation must occur immediately before an externally visible write; otherwise use unchanged. The option description must clearly disclose any required or forbidden consequence to the user.");
        sb.AppendLine("Every option value is a short visible answer label: 1-300 characters, non-empty after trimming, and pairwise distinct after trimming within its question. Never repeat the same value for two options; descriptions do not make duplicate values distinct.");
        sb.AppendLine("Question ids must be unique lower-snake-case identifiers and must not reuse an id already present in clarification_answers_json.");
        sb.AppendLine("Do not add an Other option; the host adds a native custom-answer control.");
        sb.AppendLine("Write question and option content in the same language as the raw request. Fall back to English only if its language cannot be determined.");
        sb.AppendLine($"This form may contain at most {session.CurrentRoundQuestionLimit} questions. There are {session.RemainingRounds} form round(s) and {session.RemainingQuestions} question(s) left in the shared budget.");
        if (requireQuestions)
            sb.AppendLine("For this stage, outcome must be questions unless the request is intrinsically contradictory, unsafe, or impossible to clarify; do not return sufficient.");
        else
            sb.AppendLine("Return sufficient when the accumulated intent is decision-complete. Return questions only for genuine remaining user-intent ambiguity.");
        sb.AppendLine($"Clarification stage: {stage}");
        sb.AppendLine("<raw_request>");
        sb.AppendLine(session.RawRequest);
        sb.AppendLine("</raw_request>");
        if (!string.IsNullOrWhiteSpace(session.CallerContext))
        {
            sb.AppendLine("<caller_context>");
            sb.AppendLine(session.CallerContext);
            sb.AppendLine("</caller_context>");
        }
        if (session.Answers.Count > 0)
        {
            sb.AppendLine("<clarification_answers_json>");
            sb.AppendLine(session.BuildAnswersJson().ToJsonString());
            sb.AppendLine("</clarification_answers_json>");
        }
        if (issueContext != null)
        {
            sb.AppendLine("<validated_ambiguity_context_json>");
            sb.AppendLine(issueContext.ToJsonString());
            sb.AppendLine("</validated_ambiguity_context_json>");
        }
        if (!string.IsNullOrWhiteSpace(previousError))
        {
            sb.AppendLine("The previous response violated the contract. Correct every reported issue without changing the request:");
            sb.AppendLine(previousError);
            sb.AppendLine("Return one complete corrected object. Recheck all question ids, option value lengths, trimmed option value uniqueness, option counts, and that only the first option is recommended before responding.");
            sb.AppendLine("<invalid_previous_response>");
            sb.AppendLine(TruncateIntentClarificationText(previousResponse ?? string.Empty, 8_000));
            sb.AppendLine("</invalid_previous_response>");
        }
        return sb.ToString();
    }

    private static JsonNode BuildIntentClarificationSchema(int maxQuestions)
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["outcome", "reason", "questions"],
          "properties": {
            "outcome": { "type": "string", "enum": ["questions", "sufficient", "cannot_plan_safely"] },
            "reason": { "type": "string" },
            "questions": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["id", "prompt", "options"],
                "properties": {
                  "id": { "type": "string" },
                  "prompt": { "type": "string" },
                  "options": {
                    "type": "array",
                    "minItems": 2,
                    "maxItems": 3,
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["value", "description", "recommended", "external_write_confirmation_policy"],
                      "properties": {
                        "value": { "type": "string" },
                        "description": { "type": "string" },
                        "recommended": { "type": "boolean" },
                        "external_write_confirmation_policy": { "type": "string", "enum": ["required", "forbidden", "unchanged"] }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """)!.AsObject();
        schema["properties"]!["questions"]!["maxItems"] = maxQuestions;
        return schema;
    }

    private static IntentClarificationAssessment ParseIntentClarificationAssessment(
        JsonObject response,
        IntentClarificationSession session,
        bool requireQuestions)
    {
        var outcome = response["outcome"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (outcome is not ("questions" or "sufficient" or "cannot_plan_safely"))
            throw new InvalidOperationException("Intent clarification outcome is invalid.");
        var reason = response["reason"]?.GetValue<string>()?.Trim() ?? string.Empty;
        if (reason.Length > 2_000)
            throw new InvalidOperationException("Intent clarification reason exceeds 2000 characters.");
        if (outcome == "cannot_plan_safely" && reason.Length == 0)
            throw new InvalidOperationException("cannot_plan_safely requires a non-empty reason.");

        if (response["questions"] is not JsonArray questionNodes)
            throw new InvalidOperationException("Intent clarification questions must be an array.");
        if (outcome != "questions" && questionNodes.Count != 0)
            throw new InvalidOperationException("Only the questions outcome may include questions.");
        if (requireQuestions && outcome == "sufficient")
            throw new InvalidOperationException("This clarification stage requires at least one question or cannot_plan_safely.");
        if (outcome == "questions"
            && (session.CurrentRoundQuestionLimit == 0
                ? questionNodes.Count != 0
                : questionNodes.Count == 0 || questionNodes.Count > session.CurrentRoundQuestionLimit))
        {
            throw new InvalidOperationException(
                $"Intent clarification must contain between 1 and {session.CurrentRoundQuestionLimit} questions.");
        }

        var knownIds = session.Answers.Select(static answer => answer.QuestionId).ToHashSet(StringComparer.Ordinal);
        var questions = new List<IntentClarificationQuestion>();
        foreach (var questionNode in questionNodes)
        {
            if (questionNode is not JsonObject questionObject)
                throw new InvalidOperationException("Every intent clarification question must be an object.");
            var id = questionObject["id"]?.GetValue<string>()?.Trim() ?? string.Empty;
            if (!Regex.IsMatch(id, "^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Intent clarification question ids must be lower-snake-case identifiers.");
            id = EnsureUniqueIntentClarificationQuestionId(id, knownIds);
            var prompt = questionObject["prompt"]?.GetValue<string>()?.Trim() ?? string.Empty;
            if (prompt.Length is < 1 or > 1_000)
                throw new InvalidOperationException($"Intent clarification question '{id}' requires a prompt of at most 1000 characters.");
            if (questionObject["options"] is not JsonArray optionNodes || optionNodes.Count is < 2 or > 3)
                throw new InvalidOperationException($"Intent clarification question '{id}' requires two or three options.");

            var values = new HashSet<string>(StringComparer.Ordinal);
            var options = new List<IntentClarificationOption>();
            for (var optionIndex = 0; optionIndex < optionNodes.Count; optionIndex++)
            {
                if (optionNodes[optionIndex] is not JsonObject optionObject)
                    throw new InvalidOperationException($"Intent clarification question '{id}' has a malformed option.");
                var value = optionObject["value"]?.GetValue<string>()?.Trim() ?? string.Empty;
                var description = optionObject["description"]?.GetValue<string>()?.Trim() ?? string.Empty;
                var recommended = optionObject["recommended"]?.GetValue<bool>() ?? false;
                var externalWriteConfirmationPolicy = optionObject["external_write_confirmation_policy"]
                    ?.GetValue<string>()?.Trim().ToLowerInvariant() ?? string.Empty;
                if (value.Length is < 1 or > 300)
                    throw new InvalidOperationException($"Intent clarification question '{id}' option value {optionIndex + 1} must contain between 1 and 300 characters after trimming.");
                if (!values.Add(value))
                    throw new InvalidOperationException($"Intent clarification question '{id}' option value {optionIndex + 1} duplicates an earlier value after trimming; every option value must be distinct.");
                if (description.Length is < 1 or > 1_000)
                    throw new InvalidOperationException($"Intent clarification question '{id}' option descriptions must be non-empty and at most 1000 characters.");
                if (recommended != (optionIndex == 0))
                    throw new InvalidOperationException($"Intent clarification question '{id}' must mark only its first option as recommended.");
                if (externalWriteConfirmationPolicy is not ("required" or "forbidden" or "unchanged"))
                    throw new InvalidOperationException($"Intent clarification question '{id}' option {optionIndex + 1} has an invalid external-write confirmation consequence.");
                options.Add(new IntentClarificationOption(
                    value,
                    description,
                    recommended,
                    externalWriteConfirmationPolicy));
            }
            questions.Add(new IntentClarificationQuestion(id, prompt, options));
        }

        return new IntentClarificationAssessment(outcome, reason, questions);
    }

    private static string EnsureUniqueIntentClarificationQuestionId(
        string proposedId,
        HashSet<string> knownIds)
    {
        if (knownIds.Add(proposedId))
            return proposedId;

        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var suffixText = $"_{suffix}";
            var prefixLength = Math.Min(proposedId.Length, 64 - suffixText.Length);
            var candidate = proposedId[..prefixLength] + suffixText;
            if (knownIds.Add(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Intent clarification could not allocate a unique question id.");
    }

    private static async Task RequestIntentClarificationFormAsync(
        StepExecutionContext ctx,
        IntentClarificationSession session,
        string stage,
        IntentClarificationAssessment assessment,
        CancellationToken ct)
    {
        var provider = ctx.Engine.HumanInputProvider
            ?? throw BuildIntentClarificationFailure(
                session,
                stage,
                "clarification_provider_unavailable",
                "Intent clarification is required, but no human-input provider is configured.");
        var request = new HumanInputRequest
        {
            RunId = string.IsNullOrWhiteSpace(ctx.Limits.RunId) ? Guid.NewGuid().ToString("N") : ctx.Limits.RunId!,
            StepId = $"{ctx.Step.Id}:intent_clarification:{session.FormsUsed + 1}:{Guid.NewGuid():N}",
            Prompt = assessment.Reason.Length == 0
                ? "Clarify the requested workflow behavior."
                : assessment.Reason,
            Mode = HumanInputContract.ModeForm,
            Fields = assessment.Questions.Select(static question => new HumanInputFieldDef
            {
                Name = question.Id,
                Type = "radio",
                Required = true,
                Description = question.Prompt,
                Options = question.Options.Select(static option => option.Value).ToList(),
                OptionDefinitions = question.Options.Select(static option => new HumanInputOptionDef
                {
                    Value = option.Value,
                    Description = BuildIntentClarificationOptionDescription(option),
                    Recommended = option.Recommended
                }).ToList(),
                AllowCustomAnswer = true,
                Default = question.Options[0].Value
            }).ToList(),
            TimeoutMs = session.Config.TimeoutMs,
            AllowAbandon = true
        };

        ctx.AddTelemetryEvent("gnougo-flow.step.waiting_for_human", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.human.prompt", request.Prompt),
            new KeyValuePair<string, object?>("gnougo-flow.human.request", HumanInputContract.BuildRequestPayload(request).ToJsonString()),
            new KeyValuePair<string, object?>("gnougo-flow.human.purpose", "intent_clarification"),
            new KeyValuePair<string, object?>("gnougo-flow.human.clarification_stage", stage)
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(session.Config.TimeoutMs);
        JsonNode? response;
        try
        {
            response = await provider.RequestInputAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw BuildIntentClarificationFailure(
                session,
                stage,
                "clarification_timeout",
                $"Intent clarification timed out after {session.Config.TimeoutMs}ms.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw BuildIntentClarificationFailure(
                session,
                stage,
                "clarification_provider_failed",
                "Intent clarification failed because the human-input provider returned an error.",
                ex);
        }

        if (HumanInputContract.IsAbandoned(response))
            throw BuildIntentClarificationAborted(session, stage);
        if (response is not JsonObject responseObject)
            throw BuildIntentClarificationFailure(
                session,
                stage,
                "clarification_invalid_response",
                "Intent clarification must return the complete structured form.");

        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        var totalCharacters = session.Answers.Sum(static answer => answer.Answer.Length);
        foreach (var question in assessment.Questions)
        {
            var answer = responseObject[question.Id] is JsonValue value
                         && value.TryGetValue<string>(out var text)
                ? text.Trim()
                : string.Empty;
            if (answer.Length is < 1 or > MaxIntentClarificationAnswerCharacters)
            {
                throw BuildIntentClarificationFailure(
                    session,
                    stage,
                    "clarification_invalid_response",
                    $"Intent clarification field '{question.Id}' requires an answer of at most {MaxIntentClarificationAnswerCharacters} characters.");
            }
            totalCharacters += answer.Length;
            answers[question.Id] = answer;
        }
        if (totalCharacters > MaxIntentClarificationTotalAnswerCharacters)
            throw BuildIntentClarificationFailure(
                session,
                stage,
                "clarification_invalid_response",
                $"Intent clarification exceeds the {MaxIntentClarificationTotalAnswerCharacters}-character total limit.");

        session.AddAnswers(assessment.Questions, answers);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.intent_clarification.forms_used", session.FormsUsed);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.intent_clarification.questions_used", session.QuestionsUsed);
        ctx.SetTelemetryAttribute("gnougo-flow.plan.intent_clarification.last_stage", stage);
        ctx.AddTelemetryEvent("gnougo-flow.step.human_input_resumed", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.human.run_id", request.RunId),
            new KeyValuePair<string, object?>("gnougo-flow.human.step_id", request.StepId),
            new KeyValuePair<string, object?>("gnougo-flow.human.purpose", "intent_clarification"),
            new KeyValuePair<string, object?>("gnougo-flow.human.clarification_stage", stage),
            new KeyValuePair<string, object?>("gnougo-flow.human.question_count", assessment.Questions.Count)
        });
    }

    private static string BuildIntentClarificationOptionDescription(IntentClarificationOption option)
    {
        var consequence = option.ExternalWriteConfirmationPolicy switch
        {
            "required" => "External-write confirmation policy: required.",
            "forbidden" => "External-write confirmation policy: forbidden.",
            _ => string.Empty
        };
        return consequence.Length == 0 ? option.Description : $"{option.Description} {consequence}";
    }

    private static JsonObject ApplyIntentClarification(
        JsonObject originalInput,
        IntentClarificationSession? session)
    {
        var input = (JsonObject)originalInput.DeepClone();
        if (session is not { Answers.Count: > 0 })
            return input;

        var envelope = "\n\n<user_intent_clarification_json>\n"
                       + session.BuildAnswersJson().ToJsonString()
                       + "\n</user_intent_clarification_json>";
        if (input["raw_prompt"] is JsonValue rawPrompt
            && rawPrompt.TryGetValue<string>(out var rawPromptText))
        {
            input["raw_prompt"] = rawPromptText + envelope;
        }

        if (input["generator"] is JsonObject generator)
        {
            if (generator["raw_prompt"] is JsonValue generatorRaw
                && generatorRaw.TryGetValue<string>(out var generatorRawText))
            {
                generator["raw_prompt"] = generatorRawText + envelope;
            }
            if (generator["instruction"] is JsonValue instruction
                && instruction.TryGetValue<string>(out var instructionText))
            {
                generator["instruction"] = instructionText + envelope;
            }
        }
        return input;
    }

    private static bool TryGetExtractionIntentAmbiguity(
        WorkflowRuntimeException exception,
        out JsonNode ambiguityContext)
    {
        ambiguityContext = new JsonObject();
        if (!string.Equals(exception.Code, ErrorCodes.TemplatePlan, StringComparison.Ordinal)
            || exception.Details is not JsonObject details
            || details["quality_review"] is not JsonObject qualityReview
            || qualityReview["diagnostics"] is not JsonArray diagnostics)
        {
            return false;
        }

        var blocking = diagnostics
            .OfType<JsonObject>()
            .Where(static diagnostic => string.Equals(
                diagnostic["severity"]?.GetValue<string>(),
                "critical",
                StringComparison.Ordinal)
                && diagnostic["evidence_qualified"]?.GetValue<bool>() == true)
            .ToArray();
        if (blocking.Length == 0
            || blocking.Any(static diagnostic => !string.Equals(
                diagnostic["kind"]?.GetValue<string>(),
                "intent_ambiguity",
                StringComparison.Ordinal)))
        {
            return false;
        }

        ambiguityContext = new JsonObject
        {
            ["quality_review"] = qualityReview.DeepClone()
        };
        return true;
    }

    private static WorkflowRuntimeException BuildIntentClarificationFailure(
        IntentClarificationSession? session,
        string stage,
        string classification,
        string message,
        Exception? inner = null)
    {
        var details = session?.BuildSafeMetadata(stage, "clarification_failed", "retry_or_refine_request")
                      ?? new JsonObject
                      {
                          ["planning_outcome"] = "clarification_failed",
                          ["clarification_stage"] = stage,
                          ["recommended_action"] = "retry_or_refine_request"
                      };
        details["classification"] = classification;
        if (inner is not null)
            details["reason"] = BuildSafeIntentClarificationFailureReason(inner);
        return new WorkflowRuntimeException(
            ErrorCodes.WorkflowPlanClarificationFailed,
            message,
            inner: inner,
            details: details);
    }

    private static string BuildSafeIntentClarificationFailureReason(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Text.Json.JsonException)
                return "The clarification analyst returned invalid JSON.";

            var message = current.Message.Trim();
            if (message.StartsWith("Intent clarification", StringComparison.Ordinal)
                || message.StartsWith("Every intent clarification", StringComparison.Ordinal)
                || message.StartsWith("Only the questions outcome", StringComparison.Ordinal)
                || message.StartsWith("This clarification stage", StringComparison.Ordinal)
                || message.StartsWith("cannot_plan_safely", StringComparison.Ordinal)
                || message.StartsWith("Capability intent clarification", StringComparison.Ordinal))
            {
                return TruncateIntentClarificationText(message, 2_000);
            }
        }

        return "The clarification analyst did not satisfy the validated structured-output contract.";
    }

    private static WorkflowRuntimeException BuildCannotPlanSafely(
        IntentClarificationSession session,
        string stage,
        string reason)
    {
        var details = session.BuildSafeMetadata(stage, "cannot_plan_safely", "refine_request_or_abandon");
        details["reason"] = TruncateIntentClarificationText(reason, 2_000);
        return new WorkflowRuntimeException(
            ErrorCodes.WorkflowPlanCannotPlanSafely,
            "workflow.plan cannot safely generate a workflow from the clarified intent: "
            + TruncateIntentClarificationText(reason, 2_000),
            details: details);
    }

    private static WorkflowRuntimeException BuildIntentClarificationAborted(
        IntentClarificationSession session,
        string stage)
    {
        var details = session.BuildSafeMetadata(stage, "aborted", "none");
        return new WorkflowRuntimeException(
            ErrorCodes.WorkflowPlanAborted,
            "Workflow planning was explicitly abandoned by the user.",
            details: details);
    }

    private static string TruncateIntentClarificationText(string text, int maximumLength) =>
        text.Length <= maximumLength ? text : text[..maximumLength];
}
