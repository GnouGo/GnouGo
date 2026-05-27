using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Pauses the workflow and waits for human input via <see cref="IHumanInputProvider"/>.
///
/// Input:
///   - prompt   (string, required)  : The question or instruction shown to the user.
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

    public string DslSnippet => """
        ### human.input — Wait for human input
        Pauses the workflow and sends a prompt to the user.
        The workflow resumes when the user submits a response.
        ```yaml
        - id: review
          type: human.input
          input:
            prompt: "The agent wants to call API X. Approve?"
            context: "${json(data.steps.plan)}"
            choices:
              - approve
              - reject
              - modify
            timeout_ms: 300000
        ```
        With structured fields:
        ```yaml
        - id: user_info
          type: human.input
          input:
            prompt: "Please provide the following details:"
            fields:
              - name: email
                type: string
                required: true
                description: Your email address
              - name: priority
                type: select
                options: [low, medium, high]
                default: medium
        ```
        Output: the user's response as a JSON object.
        """;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var provider = ctx.Engine.HumanInputProvider
            ?? throw new WorkflowRuntimeException("NO_HITL_PROVIDER",
                "human.input step requires an IHumanInputProvider configured on the engine.");

        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "human.input input must be an object.");

        var prompt = input["prompt"]?.GetValue<string>()
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                "human.input requires a 'prompt' field.");

        var timeoutMs = 300_000;
        if (input.TryGetPropertyValue("timeout_ms", out var tNode) && tNode != null)
            timeoutMs = (int)ExpressionEvaluator.GetNumber(tNode);

        // Parse choices
        List<string>? choices = null;
        if (input["choices"] is JsonArray choicesArr)
            choices = choicesArr.Select(c => c?.GetValue<string>() ?? "").ToList();

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
                    Name = fObj["name"]?.GetValue<string>() ?? "",
                    Type = fObj["type"]?.GetValue<string>() ?? "string",
                    Required = fObj["required"]?.GetValue<bool>() ?? true,
                    Description = fObj["description"]?.GetValue<string>(),
                    Options = (fObj["options"] as JsonArray)?.Select(o => o?.GetValue<string>() ?? "").ToList(),
                    Default = fObj["default"]?.GetValue<string>(),
                });
            }
        }

        var context = input["context"];

        // Compute RunId once so that the telemetry payload (sent to the UI)
        // and the HumanInputRequest (used to key the pending TCS) are consistent.
        var runId = ctx.Limits.RunId ?? Guid.NewGuid().ToString("N");

        // Emit telemetry event so the UI knows we are waiting
        var requestPayload = new JsonObject
        {
            ["prompt"] = prompt,
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
}



