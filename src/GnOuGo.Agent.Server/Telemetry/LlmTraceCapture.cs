using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Integrations;
using FlowRequest = GnOuGo.Flow.Core.Runtime.LLMRequest;

namespace GnOuGo.Agent.Server.Telemetry;

/// <summary>Observes one logical dispatch. Never owns retries, reservations or accounting.</summary>
public sealed class LlmTraceCapture(AgentOTelTelemetry telemetry, LlmTraceContentStore contents,
    LocalTraceDebugStore local, ILogger<LlmTraceCapture> logger)
{
    public async Task<LLMResponse> CallAsync(FlowRequest request, LLMOptions options,
        Func<FlowRequest, CancellationToken, Task<LLMResponse>> dispatch, CancellationToken ct)
    {
        var parent = Activity.Current;
        using var scope = telemetry.StartActivityScope("gen_ai.generate", ActivityKind.Client);
        var activity = scope.Activity;
        var provider = request.Provider ?? options.DefaultProvider;
        var model = string.IsNullOrWhiteSpace(request.Model) ? options.DefaultModel : request.Model;
        var config = options.ResolveProvider(provider);
        var stage = Inherited(parent, "gnougo.llm.stage") ?? "configuration.or.preparation";
        var session = Inherited(parent, "gnougo-flow.plan.session_id") ?? Inherited(parent, "gnougo.planning.session_id");
        session ??= request.ClientRequestId?.Split(':')[0];
        var id = Guid.NewGuid().ToString("N");
        activity.SetTag("tenant.id", contents.Tenant);
        activity.SetTag("gen_ai.operation.name", "chat");
        activity.SetTag("gen_ai.provider.name", config?.ResolvedType ?? provider);
        activity.SetTag("gen_ai.request.model", model);
        activity.SetTag("gnougo.llm.call_id", id);
        activity.SetTag("gnougo.llm.trace_id", activity.TraceId.ToHexString());
        activity.SetTag("gnougo.llm.stage", stage);
        activity.SetTag("gnougo.llm.request_id", request.ClientRequestId);
        activity.SetTag("gnougo.llm.status", "pending");
        activity.SetTag("gnougo.llm.protocol", config?.ResolvedType is "openai"
            ? request.UseBackgroundMode && config.RequestPolicy.BackgroundProtocol != LLMBackgroundProtocolMode.ChatCompletions ? "Responses (background)" : "Chat Completions (foreground)"
            : "provider native");
        foreach (var key in new[] { "gnougo-flow.step.id", "gnougo-flow.step.type", "gnougo-flow.workflow.name", "gnougo-flow.plan.session_id", "gnougo.planning.session_id" })
            activity.SetTag(key, Inherited(parent, key));
        var content = new LlmTraceContent { TenantId = contents.Tenant, TraceId = activity.TraceId.ToHexString(), SpanId = activity.SpanId.ToHexString(), SessionId = session };
        await Observe(async captureToken =>
        {
            if (!contents.Enabled) return;
            if (stage == "workflow.plan.repair" && RepairContext(request.Prompt) is { } repair)
            {
                activity.SetTag("gnougo.llm.repair.targets", (repair["targets"] as JsonArray)?.Count);
                activity.SetTag("gnougo.llm.repair.deferred", repair["deferredTargetCount"]?.GetValue<int>());
            }
            var journal = await contents.FindJournalAsync(session, request.ClientRequestId, captureToken);
            var input = LlmTraceContentStore.RequestDocument(request);
            contents.SetInput(content, Redact(input, config));
            if (journal is { } existing)
            {
                content.Journal = existing.Journal; content.RequestKey = existing.Key;
                content.Input = null; // Reuse the authoritative encrypted reservation.
            }
            activity.SetTag("gnougo.llm.input.bytes", content.InputBytes);
            activity.SetTag("gnougo.llm.input.estimated_tokens", (Encoding.UTF8.GetByteCount(request.Prompt) + Encoding.UTF8.GetByteCount(request.StructuredOutputSchema?.ToJsonString() ?? "") + 3) / 4);
            await contents.SaveAsync(id, content, captureToken);
            activity.SetTag("gnougo.llm.content_ref", id);
        });
        local.Track(activity);
        try
        {
            var result = await dispatch(request, ct).ConfigureAwait(false);
            activity.SetTag("gnougo.llm.status", result.CompletionStatus == "output_limit" ? "truncated" : "completed");
            activity.SetStatus(ActivityStatusCode.Ok);
            await Observe(async captureToken =>
            {
                var input = Usage(result.Usage, "input_tokens", "prompt_tokens", "inputTokens");
                var output = Usage(result.Usage, "output_tokens", "completion_tokens", "outputTokens");
                activity.SetTag("gen_ai.usage.input_tokens", input);
                activity.SetTag("gen_ai.usage.output_tokens", output);
                if (input.HasValue && output.HasValue && new ModelMetadataUsageCostEstimator(options).EstimateCostWithCurrency(model, input, output, config?.ResolvedType) is { } cost)
                {
                    activity.SetTag("gnougo.llm.estimated_cost", (double)cost.Amount);
                    activity.SetTag("gnougo.llm.cost_currency", cost.Currency);
                }
                if (!contents.Enabled) return;
                contents.SetOutput(content, Redact(LlmTraceContentStore.ResponseDocument(result), config));
                if (content.Journal is not null) content.Output = null;
                activity.SetTag("gnougo.llm.output.bytes", content.OutputBytes);
                await contents.SaveAsync(id, content, captureToken);
            });
            return result;
        }
        catch (Exception ex)
        {
            var status = ct.IsCancellationRequested ? "cancelled" : ex is HttpRequestException or TimeoutException or OperationCanceledException ? "uncertain" : "failed";
            if (!ct.IsCancellationRequested && ex is LLMClientException client && client.Kind is LLMClientFailureKind.Timeout or LLMClientFailureKind.Transport) status = "uncertain";
            // Provider-neutral mapped transport failures carry uncertainty in their code.
            if (ex is WorkflowRuntimeException runtime && runtime.Code is "LLM_BUDGET_UNVERIFIABLE" or "LLM_TIMEOUT" or "LLM_TRANSPORT") status = "uncertain";
            activity.SetTag("gnougo.llm.status", status);
            activity.SetTag("error.type", ex.GetType().Name);
            activity.SetStatus(ActivityStatusCode.Error);
            await Observe(async captureToken =>
            {
                content.OutputStatus = status + " — no retained completion; usage unknown";
                if (contents.Enabled) await contents.SaveAsync(id, content, captureToken);
            });
            throw;
        }
    }

    private async Task Observe(Func<CancellationToken, Task> work)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await work(timeout.Token); }
        catch (Exception ex) { logger.LogWarning("LLM diagnostic capture unavailable ({ErrorType}); inference is unaffected.", ex.GetType().Name); }
    }
    internal static string? Inherited(Activity? activity, string key)
    {
        for (var current = activity; current is not null; current = current.Parent)
            if (current.GetTagItem(key) is { } value) return value.ToString();
        return activity?.GetBaggageItem(key);
    }
    internal static long? Usage(JsonNode? usage, params string[] names)
    {
        foreach (var name in names)
            if (usage is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<long>(out var tokens) && tokens >= 0) return tokens;
        return null;
    }
    private static string Redact(string value, ModelProviderOptions? options)
    {
        foreach (var secret in new[] { options?.ApiKey, options?.ClientSecret, options?.PrivateKeyPem })
            if (!string.IsNullOrEmpty(secret))
            {
                value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
                value = value.Replace(JsonValue.Create(secret)!.ToJsonString()[1..^1], "[redacted]", StringComparison.Ordinal);
            }
        return value;
    }

    internal static JsonObject? RepairContext(string prompt)
    {
        var offset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        if (offset < 0) return null;
        try { return JsonNode.Parse(prompt[(offset + 1)..]) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static IReadOnlyList<LlmContentPart> Breakdown(string input)
    {
        var request = JsonSerializer.Deserialize(input, GnOuGo.Flow.Core.Planning.PlanningJsonContext.Default.LLMRequest);
        if (request is null) return [];
        var parts = new List<LlmContentPart>();
        var prompt = request.Prompt;
        var contextOffset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        if (contextOffset >= 0)
        {
            try
            {
                using var context = JsonDocument.Parse(prompt[(contextOffset + 1)..]);
                var remainder = Encoding.UTF8.GetByteCount(prompt);
                var instructions = Encoding.UTF8.GetByteCount(prompt[..(contextOffset + 1)]);
                parts.Add(new("Instructions / examples", instructions)); remainder -= instructions;
                foreach (var property in context.RootElement.EnumerateObject())
                {
                    var bytes = Encoding.UTF8.GetByteCount(property.Value.GetRawText());
                    parts.Add(new(property.Name, bytes)); remainder -= bytes;
                }
                parts.Add(new("Context keys / JSON framing", remainder));
            }
            catch (JsonException) { parts.Clear(); }
        }
        if (parts.Count == 0) parts.Add(new("Prompt", Encoding.UTF8.GetByteCount(prompt)));
        parts.Add(new("Response schema", Encoding.UTF8.GetByteCount(request.StructuredOutputSchema?.ToJsonString() ?? "")));
        if (request.Tools is { Count: > 0 }) parts.Add(new("Tool definitions", Encoding.UTF8.GetByteCount(JsonSerializer.SerializeToNode(request, GnOuGo.Flow.Core.Planning.PlanningJsonContext.Default.LLMRequest)!["tools"]?.ToJsonString() ?? "")));
        return parts;
    }
}
