using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.GithubCopilot.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LiveIntentAgentGenerationTests
{
    /// <summary>One durable campaign reservation for every actual SDK inference dispatch.</summary>
    internal sealed class LiveInferenceGateway : IAsyncDisposable
    {
        private const string EnvironmentKey = "Code__Copilot__InferenceProxyEndpoint";
        private readonly LLMUsageBudgetScope _budget;
        private readonly LiveBudgetLedger _ledger;
        private readonly IExchangeRateProvider _rates;
        private readonly Func<CancellationToken, Task<LLMOptions>> _options;
        private readonly ConcurrentDictionary<string, byte> _requests = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _dispatch = new(1, 1);
        private readonly HttpClient _http;
        private WebApplication? _host;
        private string? _previousEndpoint;
        internal int CompletedCalls { get; private set; }
        private int _readyProcesses;
        internal void RequireReady()
        {
            if (Volatile.Read(ref _readyProcesses) == 0) throw new InvalidOperationException("No SDK process has attested inference interception; live execution is blocked before any fixture action.");
        }

        internal LiveInferenceGateway(LLMUsageBudgetScope budget, LiveBudgetLedger ledger, IExchangeRateProvider rates,
            Func<CancellationToken, Task<LLMOptions>> options, HttpClient http)
        { _budget = budget; _ledger = ledger; _rates = rates; _options = options; _http = http; }

        internal static async Task<LiveInferenceGateway> StartAsync(IServiceProvider services, LLMUsageBudgetScope budget, LiveBudgetLedger ledger, CancellationToken ct)
        {
            var factory = services.GetRequiredService<SecureWorkflowRuntimeFactory>();
            async Task<LLMOptions> Options(CancellationToken token) { await using var runtime = await factory.CreateAsync(token); return runtime.Options; }
            var options = await Options(ct);
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            if (options.DangerousAcceptAnyServerCertificate) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            var gateway = new LiveInferenceGateway(budget, ledger, services.GetRequiredService<IExchangeRateProvider>(), Options,
                new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 16 * 1024 * 1024 });
            var builder = WebApplication.CreateSlimBuilder(); builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0").ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 4 * 1024 * 1024);
            gateway._host = builder.Build();
            gateway._host.MapPost("/inference/ready", async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body);
                if (await reader.ReadToEndAsync(context.RequestAborted) != "sdk-http-interception-v1") { context.Response.StatusCode = 400; return; }
                Interlocked.Increment(ref gateway._readyProcesses); context.Response.StatusCode = 204;
            });
            gateway._host.MapPost("/inference", async (HttpContext context) =>
            {
                try
                {
                    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync(context.RequestAborted);
                    if (Encoding.UTF8.GetByteCount(body) > 4 * 1024 * 1024) throw new InvalidOperationException("Inference request too large.");
                    using var request = new HttpRequestMessage(HttpMethod.Post, context.Request.Headers[CopilotInferenceProxyHandler.UpstreamHeader].ToString()) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    foreach (var header in context.Request.Headers)
                        if (!IsTransportHeader(header.Key)) request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    using var response = await gateway.ForwardAsync(request, context.Request.Headers[CopilotInferenceProxyHandler.RequestHeader].ToString(), context.RequestAborted);
                    context.Response.StatusCode = (int)response.StatusCode;
                    context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
                    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Neither provider responses, prompts, credentials nor endpoint details enter logs.
                    WriteLiveProgress("sdk_inference_blocked", providerDiagnostics: new JsonObject { ["failure_type"] = ex.GetType().Name });
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await context.Response.WriteAsync("Inference policy rejected or could not verify this dispatch.", CancellationToken.None);
                }
            });
            await gateway._host.StartAsync(ct);
            gateway._previousEndpoint = Environment.GetEnvironmentVariable(EnvironmentKey);
            Environment.SetEnvironmentVariable(EnvironmentKey, gateway._host.Urls.Single() + "/inference");
            return gateway;
        }

        internal async Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage original, string requestId, CancellationToken ct)
        {
            await _dispatch.WaitAsync(ct);
            try
            {
                if (string.IsNullOrWhiteSpace(requestId) || !_requests.TryAdd(requestId, 0)) throw new InvalidOperationException("Missing or duplicate inference request identifier.");
                var options = await _options(ct);
                var upstream = original.RequestUri ?? throw new InvalidOperationException("Missing inference destination.");
                var configured = options.ResolveProvider(options.DefaultProvider) ?? throw new InvalidOperationException("Missing configured campaign provider.");
                var allowed = new Uri(configured.Url, UriKind.Absolute);
                if (original.Method != HttpMethod.Post || upstream.Scheme != allowed.Scheme || upstream.Authority != allowed.Authority || upstream.UserInfo.Length != 0 ||
                    !(upstream.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) || upstream.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Inference destination is outside the configured campaign provider.");
                var body = JsonNode.Parse(await original.Content!.ReadAsStringAsync(ct)) as JsonObject ?? throw new InvalidOperationException("Inference requires an object request.");
                if (body["model"]?.GetValue<string>() != options.DefaultModel) throw new InvalidOperationException("Inference must retain the configured campaign model.");
                var responses = upstream.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal);
                var ceiling = new[] { "max_tokens", "max_completion_tokens", "max_output_tokens" }
                    .Where(key => body[key] is not null).Select(key => body[key]!.GetValue<int>()).Append(8192).Min();
                if (ceiling <= 0) throw new InvalidOperationException("Inference requires a positive output ceiling.");
                if (responses) { body["reasoning"] ??= new JsonObject(); body["reasoning"]!["effort"] = "low"; }
                else body["reasoning_effort"] = "low";
                body.Remove("max_tokens"); body.Remove(responses ? "max_completion_tokens" : "max_output_tokens");
                body[responses ? "max_output_tokens" : "max_completion_tokens"] = ceiling;
                if (!responses && body["stream"]?.GetValue<bool>() == true)
                { body["stream_options"] ??= new JsonObject(); body["stream_options"]!["include_usage"] = true; }
                var payload = body.ToJsonString();
                var accounting = new LLMRequest { Provider = options.DefaultProvider, Model = options.DefaultModel, Prompt = payload,
                    MaxTokens = ceiling, RequireOutputTokenLimit = true, DisableTransportRetries = true };
                if (_budget.Limits.MaxCalls is { } callLimit && _budget.Snapshot.Calls >= callLimit)
                    throw new InvalidOperationException("The campaign inference call allowance is exhausted.");
                if (_budget.Limits.MaxTotalTokens is { } tokenLimit && _budget.Snapshot.TotalTokens + ConservativeTextInputReservation(accounting) + ceiling > tokenLimit)
                    throw new InvalidOperationException("The remaining campaign token allowance cannot cover this inference request.");
                var reservation = _ledger.ReserveCall(await MaximumCallCostAsync(options, accounting, _budget.Snapshot, _rates, ct));
                using var request = new HttpRequestMessage(HttpMethod.Post, upstream) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
                foreach (var header in original.Headers) if (!IsTransportHeader(header.Key)) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                HttpResponseMessage? response = null;
                var client = new ForwardClient(async token =>
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
                    var content = await response.Content.ReadAsStringAsync(token);
                    var usage = Receipt(content, response.Content.Headers.ContentType?.MediaType == "text/event-stream");
                    if (usage is null) throw new InvalidOperationException("SDK inference has no verified usage receipt.");
                    if (usage["output_tokens"]!.GetValue<long>() > ceiling) throw new InvalidOperationException("The provider exceeded the enforced inference output ceiling.");
                    return new LLMResponse { Usage = usage };
                });
                try
                {
                    await _budget.CallAsync(client, new ModelMetadataUsageCostEstimator(options), accounting, "live.sdk_inference", ct);
                    _ledger.CompleteCall(reservation); CompletedCalls++;
                    WriteLiveProgress("sdk_inference_receipted");
                    return response!;
                }
                catch { response?.Dispose(); throw; } // Retain the conservative reservation on uncertain usage.
            }
            finally { _dispatch.Release(); }
        }

        internal static JsonObject? Receipt(string content, bool streaming)
        {
            var payloads = streaming ? content.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].Trim()).Where(line => line != "[DONE]") : [content];
            JsonObject? receipt = null;
            foreach (var payload in payloads)
            {
                if (string.IsNullOrWhiteSpace(payload)) continue;
                var json = JsonNode.Parse(payload);
                var usage = json?["usage"] as JsonObject ?? json?["response"]?["usage"] as JsonObject;
                if (usage is null) continue;
                var input = usage["prompt_tokens"] ?? usage["input_tokens"]; var output = usage["completion_tokens"] ?? usage["output_tokens"];
                if (input is not JsonValue i || output is not JsonValue o || !i.TryGetValue<long>(out var incoming) || !o.TryGetValue<long>(out var outgoing) || incoming < 0 || outgoing < 0 || outgoing > 8192)
                    throw new InvalidOperationException("Invalid SDK inference usage receipt.");
                var normalized = new JsonObject { ["input_tokens"] = incoming, ["output_tokens"] = outgoing, ["total_tokens"] = checked(incoming + outgoing) };
                if (receipt is not null && !JsonNode.DeepEquals(receipt, normalized)) throw new InvalidOperationException("Conflicting SDK inference receipts.");
                receipt = normalized;
            }
            return receipt;
        }

        private static bool IsTransportHeader(string key) => new[] { "Host", "Content-Type", "Content-Length", "Transfer-Encoding", "Connection", "Accept-Encoding", CopilotInferenceProxyHandler.UpstreamHeader, CopilotInferenceProxyHandler.RequestHeader }.Contains(key, StringComparer.OrdinalIgnoreCase);
        public async ValueTask DisposeAsync()
        {
            if (_host is not null) { Environment.SetEnvironmentVariable(EnvironmentKey, _previousEndpoint); await _host.StopAsync(); await _host.DisposeAsync(); }
            _http.Dispose(); _dispatch.Dispose();
        }
        private sealed class ForwardClient(Func<CancellationToken, Task<LLMResponse>> call) : ILLMClient
        { public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) => call(ct); }
    }
}
