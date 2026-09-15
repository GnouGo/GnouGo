using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

internal static class HumanInputDslReference
{
    public static string Snippet => $$"""
        ### human.input — Wait for human input
        Pauses the workflow and asks the user for text, a choice, confirmation, or a structured form.
        Always set `input.mode` explicitly when generating workflows.

        Valid modes: {{string.Join(", ", HumanInputContract.KnownModesForDsl)}}.
        Valid field types: {{string.Join(", ", HumanInputContract.KnownFieldTypesForDsl)}}.

        Common input fields:
          - prompt (string, required): question/instruction shown to the user.
          - mode (string, required for generated DSL): text, choice, form, or confirm.
          - context (any, optional): structured data shown next to the prompt.
          - timeout_ms (number, optional): milliseconds before HUMAN_INPUT_TIMEOUT. Default: 36000000 (10 hours). Use 0 for no timeout.
          - allow_abandon (boolean, optional): expose an explicit form-level abandon action. Default: false.

        Mode selection priority (use the most constrained control that matches the required response):
          1. `confirm`: use for a binary approval, confirmation, or accept/reject decision.
          2. `choice`: MUST use whenever the user must select exactly one answer from a finite set of known options.
          3. `form`: use for several values, typed fields, or multiple selections (for example a `multiselect` field).
          4. `text`: ONLY use for genuinely open-ended answers that cannot be represented by known options or structured fields.

        Whenever the prompt, task, or upstream data provides possible choices/options/answers, encode every option in `input.choices` and use `mode: choice`.
        Never place possible choices/options/answers only in `prompt` or `context`.

        Mode patterns:
        ```yaml
        - id: ask_feedback
          type: human.input
          input:
            mode: text
            prompt: "What should be changed?"
            context: "${json(data.steps.draft)}"
        ```

        ```yaml
        - id: review
          type: human.input
          input:
            mode: choice
            prompt: "Choose the next action."
            choices: [approve, modify, reject]
            timeout_ms: 36000000
        ```

        Dynamic questionnaire choices must still be emitted as an actual YAML array:
        ```yaml
        - id: ask_question
          type: human.input
          input:
            mode: choice
            prompt: "Question ${data.question_item.number}: ${data.question_item.question}"
            choices:
              - "${data.question_item.options[0]}"
              - "${data.question_item.options[1]}"
              - "${data.question_item.options[2]}"
              - "${data.question_item.options[3]}"
        ```

        ```yaml
        - id: confirm_publish
          type: human.input
          input:
            mode: confirm
            prompt: "Publish the generated report?"
            choices: [approve, reject]
        ```

        A `confirm` response is always boolean. Choice labels are presentation text and do not
        change that contract. Branch on the boolean directly:
        ```yaml
        - id: route_confirmation
          type: switch
          cases:
            - when: "${data.steps.confirm_publish.response}"
              steps:
                - id: publish
                  type: set
                  input: { approved: true }
        ```
        Never compare a `confirm` response to a choice label such as `"approve"`. Use
        `mode: choice` when the selected label itself is required as a string.

        ```yaml
        - id: user_config
          type: human.input
          input:
            mode: form
            prompt: "Please configure the request."
            fields:
              - name: email
                type: email
                required: true
                description: Contact email
              - name: due_date
                type: date
                required: false
                default: "2026-06-09"
              - name: priority
                type: select
                options: [low, medium, high]
                option_definitions:
                  - { value: low, description: "Minimize urgency.", recommended: false }
                  - { value: medium, description: "Balance urgency and disruption.", recommended: true }
                  - { value: high, description: "Treat this as urgent.", recommended: false }
                allow_custom_answer: true
                default: medium
              - name: notes
                type: textarea
                required: false
        ```

        Invalid anti-pattern — never generate:
        ```yaml
        - id: ask_question
          type: human.input
          input:
            mode: text
            prompt: "Choices: ${json(data.question_item.options)}"
        ```

        Rules:
          - `choice` and `confirm` require a non-empty `choices` array of strings.
          - `form` requires a non-empty `fields` array.
          - `select`, `radio`, `multiselect`, and `checkbox` fields require non-empty `options`.
          - `option_definitions`, when present, must describe every option exactly once and may mark at most one as recommended.
          - `allow_custom_answer` permits a value outside the finite option list; hosts present it as a native Other control.
          - An abandoned request returns `{ "_action": "abandon" }`; never treat abandonment as an answer.
          - Field names must be unique and non-empty.
          - Use `date` for ISO date input (`YYYY-MM-DD`); it is returned as a string.

        Output access patterns:
          - text/choice: `data.steps.<id>.response` (string)
          - confirm: `data.steps.<id>.response` (boolean)
          - form: `data.steps.<id>.<field_name>` (for example `data.steps.user_config.due_date`)
          - Providers may also include `source`; use `data.steps.<id>.source` only when the provider supplies it.
        """;
}

/// <summary>
/// Pauses the workflow and waits for human input via <see cref="IHumanInputProvider"/>.
///
/// Input:
///   - prompt   (string, required)  : The question or instruction shown to the user.
///   - mode     (string, optional)  : text, choice, form, or confirm. Inferred from choices/fields when omitted.
///   - context  (any, optional)     : Structured data shown alongside the prompt.
///   - choices  (array, optional)   : Quick-reply choice strings (e.g. ["approve", "reject"]).
///   - fields   (array, optional)   : Array of { name, type, required?, description?, options?, default? }.
///   - timeout_ms (number, optional): Timeout in milliseconds (default 36 000 000 = 10 hours).
///
/// Output:
///   The user's response as a JSON object (or string).
/// </summary>
public sealed class HumanInputExecutor : IStepExecutor
{
    public string StepType => "human.input";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The input is malformed or the 'prompt' field is missing."),
        new("NO_HITL_PROVIDER", false, "No IHumanInputProvider is configured on the engine."),
        new("HUMAN_INPUT_TIMEOUT", false, "The human did not respond within the configured timeout."),
    };

    public string DslSnippet => HumanInputDslReference.Snippet;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var provider = ctx.Engine.HumanInputProvider
            ?? throw new WorkflowRuntimeException("NO_HITL_PROVIDER",
                "human.input step requires an IHumanInputProvider configured on the engine.");

        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "human.input input must be an object.");

        var prompt = ReadString(input["prompt"])
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "human.input requires a 'prompt' field.");

        var timeoutMs = HumanInputContract.DefaultTimeoutMs;
        if (input.TryGetPropertyValue("timeout_ms", out var tNode) && tNode != null)
            timeoutMs = (int)ExpressionEvaluator.GetNumber(tNode);
        var allowAbandon = ReadBool(input["allow_abandon"], defaultValue: false);

        // Parse choices
        List<string>? choices = null;
        if (input["choices"] is JsonArray choicesArr)
            choices = choicesArr.Select(c => ReadString(c) ?? "").ToList();

        // Parse fields
        List<HumanInputFieldDef>? fields = null;
        if (input["fields"] is JsonArray fieldsArr)
        {
            fields = new List<HumanInputFieldDef>();
            foreach (var fNode in fieldsArr)
            {
                if (fNode is not JsonObject fObj) continue;
                fields.Add(new HumanInputFieldDef
                {
                    Name = ReadString(fObj["name"]) ?? "",
                    Type = (ReadString(fObj["type"]) ?? "string").Trim(),
                    Required = ReadBool(fObj["required"], defaultValue: true),
                    Description = ReadString(fObj["description"]),
                    Options = (fObj["options"] as JsonArray)?.Select(o => ReadString(o) ?? "").ToList(),
                    OptionDefinitions = ParseOptionDefinitions(fObj["option_definitions"] as JsonArray),
                    AllowCustomAnswer = ReadBool(fObj["allow_custom_answer"], defaultValue: false),
                    Default = ReadString(fObj["default"]),
                });
            }
        }

        var mode = ResolveMode(input, choices, fields);
        ValidateRequest(mode, choices, fields);

        var context = input["context"];

        // Compute RunId once so that the telemetry payload (sent to the UI)
        // and the HumanInputRequest (used to key the pending TCS) are consistent.
        var runId = ctx.Limits.RunId ?? Guid.NewGuid().ToString("N");

        // Emit telemetry event so the UI knows we are waiting
        // Build the request
        var request = new HumanInputRequest
        {
            RunId = runId,
            StepId = ctx.Step.Id,
            Prompt = prompt,
            Mode = mode,
            Context = context?.DeepClone(),
            Choices = choices,
            Fields = fields,
            TimeoutMs = timeoutMs,
            AllowAbandon = allowAbandon,
        };

        var requestPayload = HumanInputContract.BuildRequestPayload(request);
        ctx.AddTelemetryEvent("gnougo-flow.step.waiting_for_human", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.human.prompt", prompt),
            new KeyValuePair<string, object?>("gnougo-flow.human.request", requestPayload.ToJsonString()),
        });

        // Wait for user response with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0)
            cts.CancelAfter(timeoutMs);

        try
        {
            var response = await provider.RequestInputAsync(request, cts.Token);

            if (HumanInputContract.IsAbandoned(response))
            {
                if (!allowAbandon)
                    throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                        $"human.input step '{ctx.Step.Id}' received an abandon action when allow_abandon is false.");
                return new JsonObject { [HumanInputContract.ActionProperty] = HumanInputContract.ActionAbandon };
            }

            ctx.AddTelemetryEvent("gnougo-flow.step.human_input_resumed", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.human.run_id", request.RunId),
                new KeyValuePair<string, object?>("gnougo-flow.human.step_id", request.StepId)
            });

            // Emit confirmation
            ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.thinking.message", "Human input received."),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info"),
            });

            return NormalizeResponse(mode, response, choices, ctx.Step.Id);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WorkflowRuntimeException("HUMAN_INPUT_TIMEOUT",
                $"human.input step '{ctx.Step.Id}' timed out after {timeoutMs}ms waiting for user response.");
        }
    }

    private static JsonNode? NormalizeResponse(
        string mode,
        JsonNode? response,
        IReadOnlyList<string>? choices,
        string stepId)
    {
        if (!mode.Equals(HumanInputContract.ModeConfirm, StringComparison.OrdinalIgnoreCase))
            return response ?? new JsonObject { ["response"] = (JsonNode?)null };

        if (!HumanInputContract.TryReadConfirmation(response, choices, out var confirmed))
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                $"human.input confirm step '{stepId}' received a response that could not be normalized to boolean. Return true/false or one of its two configured choices.");
        }

        var normalized = response is JsonObject responseObject
            ? (JsonObject)responseObject.DeepClone()
            : new JsonObject();
        normalized["response"] = confirmed;
        return normalized;
    }

    private static string ResolveMode(JsonObject input, List<string>? choices, List<HumanInputFieldDef>? fields)
    {
        var rawMode = ReadString(input["mode"])?.Trim();
        if (!string.IsNullOrWhiteSpace(rawMode))
            return rawMode;

        if (fields is { Count: > 0 })
            return HumanInputContract.ModeForm;
        if (choices is { Count: > 0 })
            return choices.Count == 2
                   && choices.Any(HumanInputContract.IsAffirmativeConfirmationChoice)
                   && choices.Any(HumanInputContract.IsNegativeConfirmationChoice)
                ? HumanInputContract.ModeConfirm
                : HumanInputContract.ModeChoice;
        return HumanInputContract.ModeText;
    }

    private static void ValidateRequest(string mode, List<string>? choices, List<HumanInputFieldDef>? fields)
    {
        if (!HumanInputContract.KnownModes.Contains(mode))
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                $"human.input mode '{mode}' is not supported. Known modes: {string.Join(", ", HumanInputContract.KnownModes)}.");

        if (mode.Equals(HumanInputContract.ModeChoice, StringComparison.OrdinalIgnoreCase)
            || mode.Equals(HumanInputContract.ModeConfirm, StringComparison.OrdinalIgnoreCase))
        {
            if (choices is not { Count: > 0 })
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    $"human.input mode '{mode}' requires a non-empty 'choices' array.");
        }

        if (mode.Equals(HumanInputContract.ModeForm, StringComparison.OrdinalIgnoreCase))
        {
            if (fields is not { Count: > 0 })
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    "human.input mode 'form' requires a non-empty 'fields' array.");
        }

        if (mode.Equals(HumanInputContract.ModeText, StringComparison.OrdinalIgnoreCase)
            && choices is { Count: > 0 })
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "human.input mode 'text' cannot define 'choices'. Use mode 'choice' or 'confirm'.");

        if (mode.Equals(HumanInputContract.ModeText, StringComparison.OrdinalIgnoreCase)
            && fields is { Count: > 0 })
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "human.input mode 'text' cannot define 'fields'. Use mode 'form'.");

        if (fields == null)
            return;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    "human.input field requires a non-empty 'name'.");
            if (!names.Add(field.Name))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    $"human.input field '{field.Name}' is defined more than once.");
            if (!HumanInputContract.KnownFieldTypes.Contains(field.Type))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    $"human.input field '{field.Name}' uses unsupported type '{field.Type}'. Known types: {string.Join(", ", HumanInputContract.KnownFieldTypes)}.");
            if (HumanInputContract.RequiresOptions(field.Type) && field.Options is not { Count: > 0 })
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    $"human.input field '{field.Name}' of type '{field.Type}' requires non-empty 'options'.");
            if (field.OptionDefinitions is { Count: > 0 })
            {
                var values = field.OptionDefinitions.Select(static option => option.Value).ToArray();
                if (values.Any(string.IsNullOrWhiteSpace)
                    || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                {
                    throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                        $"human.input field '{field.Name}' option_definitions require unique non-empty values.");
                }
                if (field.Options == null
                    || values.Count() != field.Options.Count
                    || !values.SequenceEqual(field.Options, StringComparer.Ordinal))
                {
                    throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                        $"human.input field '{field.Name}' option_definitions must describe every option in the same order.");
                }
                if (field.OptionDefinitions.Count(static option => option.Recommended) > 1)
                {
                    throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                        $"human.input field '{field.Name}' option_definitions may mark at most one option as recommended.");
                }
            }
            if (field.AllowCustomAnswer && !HumanInputContract.RequiresOptions(field.Type))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                    $"human.input field '{field.Name}' can allow a custom answer only for an option-based field.");
        }
    }

    private static List<HumanInputOptionDef>? ParseOptionDefinitions(JsonArray? definitions)
    {
        if (definitions == null)
            return null;

        var parsed = new List<HumanInputOptionDef>();
        foreach (var node in definitions)
        {
            if (node is not JsonObject definition)
                continue;
            parsed.Add(new HumanInputOptionDef
            {
                Value = ReadString(definition["value"]) ?? "",
                Description = ReadString(definition["description"]),
                Recommended = ReadBool(definition["recommended"], defaultValue: false)
            });
        }
        return parsed;
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<string>(out var stringValue))
            return stringValue;
        if (value.TryGetValue<bool>(out var boolValue))
            return boolValue ? "true" : "false";
        if (value.TryGetValue<int>(out var intValue))
            return intValue.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<long>(out var longValue))
            return longValue.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var doubleValue))
            return doubleValue.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<decimal>(out var decimalValue))
            return decimalValue.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static bool ReadBool(JsonNode? node, bool defaultValue)
    {
        if (node is not JsonValue value)
            return defaultValue;
        if (value.TryGetValue<bool>(out var boolValue))
            return boolValue;
        if (value.TryGetValue<string>(out var stringValue))
        {
            var normalized = stringValue.Trim();
            if (bool.TryParse(normalized, out var parsed))
                return parsed;
            if (normalized.Equals("1", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("y", StringComparison.OrdinalIgnoreCase))
                return true;
            if (normalized.Equals("0", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("no", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("n", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (value.TryGetValue<int>(out var intValue))
            return intValue != 0;
        if (value.TryGetValue<long>(out var longValue))
            return longValue != 0L;
        return defaultValue;
    }
}
