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
          - timeout_ms (number, optional): milliseconds before HUMAN_INPUT_TIMEOUT. Default: 300000. Use 0 for no timeout.

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
            timeout_ms: 300000
        ```

        ```yaml
        - id: confirm_publish
          type: human.input
          input:
            mode: confirm
            prompt: "Publish the generated report?"
            choices: [approve, reject]
        ```

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
                default: medium
              - name: notes
                type: textarea
                required: false
        ```

        Rules:
          - `choice` and `confirm` require a non-empty `choices` array of strings.
          - `form` requires a non-empty `fields` array.
          - `select`, `radio`, `multiselect`, and `checkbox` fields require non-empty `options`.
          - Field names must be unique and non-empty.
          - Use `date` for ISO date input (`YYYY-MM-DD`); it is returned as a string.

        Output access patterns:
          - text/choice/confirm: `data.steps.<id>.response`
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
///   - timeout_ms (number, optional): Timeout in milliseconds (default 300 000 = 5 min).
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

        var timeoutMs = 300_000;
        if (input.TryGetPropertyValue("timeout_ms", out var tNode) && tNode != null)
            timeoutMs = (int)ExpressionEvaluator.GetNumber(tNode);

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
        var requestPayload = new JsonObject
        {
            ["prompt"] = prompt,
            ["mode"] = mode,
            ["run_id"] = runId,
            ["step_id"] = ctx.Step.Id,
        };
        requestPayload["timeout_ms"] = timeoutMs;
        if (context != null) requestPayload["context"] = context.DeepClone();
        if (choices != null) requestPayload["choices"] = new JsonArray(choices.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        if (fields != null)
        {
            var fArr = new JsonArray();
            foreach (var f in fields)
            {
                var fObj = new JsonObject { ["name"] = f.Name, ["type"] = f.Type, ["required"] = f.Required };
                if (f.Description != null) fObj["description"] = f.Description;
                if (f.Options != null) fObj["options"] = new JsonArray(f.Options.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray());
                if (f.Default != null) fObj["default"] = f.Default;
                fArr.Add((JsonNode)fObj);
            }
            requestPayload["fields"] = fArr;
        }

        ctx.AddTelemetryEvent("gnougo-flow.step.waiting_for_human", new[]
        {
            new KeyValuePair<string, object?>("gnougo-flow.human.prompt", prompt),
            new KeyValuePair<string, object?>("gnougo-flow.human.request", requestPayload.ToJsonString()),
        });

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
        };

        // Wait for user response with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0)
            cts.CancelAfter(timeoutMs);

        try
        {
            var response = await provider.RequestInputAsync(request, cts.Token);

            // Emit confirmation
            ctx.AddTelemetryEvent("gnougo-flow.step.thinking", new[]
            {
                new KeyValuePair<string, object?>("gnougo-flow.thinking.message", "Human input received."),
                new KeyValuePair<string, object?>("gnougo-flow.thinking.level", "info"),
            });

            return response ?? new JsonObject { ["response"] = (JsonNode?)null };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WorkflowRuntimeException("HUMAN_INPUT_TIMEOUT",
                $"human.input step '{ctx.Step.Id}' timed out after {timeoutMs}ms waiting for user response.");
        }
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
                   && choices.Any(c => IsConfirmChoice(c))
                   && choices.Any(c => IsRejectChoice(c))
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
        }
    }

    private static bool IsConfirmChoice(string value) =>
        value.Equals("approve", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("confirm", StringComparison.OrdinalIgnoreCase)
        || value.Equals("ok", StringComparison.OrdinalIgnoreCase);

    private static bool IsRejectChoice(string value) =>
        value.Equals("reject", StringComparison.OrdinalIgnoreCase)
        || value.Equals("no", StringComparison.OrdinalIgnoreCase)
        || value.Equals("false", StringComparison.OrdinalIgnoreCase)
        || value.Equals("cancel", StringComparison.OrdinalIgnoreCase);

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
