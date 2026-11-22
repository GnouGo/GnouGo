using GnOuGo.AI.Core;
using GnOuGo.GithubCopilot.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal sealed class SdkAttemptCreditRequired(int number) : Exception("The next physical attempt needs an SDK reservation.")
{
    public int Number { get; } = number;
}

internal sealed class SdkAttemptCreditJournal(ILLMHttpRetryJournal journal, int limit) : ILLMHttpRetryJournal
{
    public Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct) => journal.LoadAsync(ct);
    public Task SaveAsync(LLMHttpRetryState state, CancellationToken ct)
    {
        if (state.Attempts.Count > limit) throw new SdkAttemptCreditRequired(state.Attempts.Count);
        return journal.SaveAsync(state, ct);
    }
}

internal sealed record CampaignInferenceResponse(int Status, string ContentType, string Body, System.Text.Json.Nodes.JsonObject? RetryHeader, System.Text.Json.Nodes.JsonObject? Failure);

// The existing SDK interception hook attests at /ready before its MCP starts.
// This test host admits every inference through the same encrypted EUR ledger.
internal sealed class CampaignInferenceProxy(WebApplication app) : IAsyncDisposable
{
    internal string Endpoint => app.Urls.Single() + "/inference";
    private volatile bool _ready;
    private volatile bool _executionEnabled;
    internal bool ExecutionEnabled { get => _executionEnabled; set => _executionEnabled = value; }
    internal void RequireSdkAdmission(IReadOnlyDictionary<string, McpServerOptions> servers)
    {
        if (!_ready || !servers.TryGetValue(BenchmarkCampaign.SdkServer, out var server) ||
            server.EnvironmentVariables?.GetValueOrDefault("Code__Copilot__InferenceProxyEndpoint") != Endpoint)
            throw new GnOuGo.Flow.Core.Planning.PlanningConflictException("The reviewed Copilot backend has not attested this campaign inference relay.");
    }
    internal static async Task<CampaignInferenceProxy> StartAsync(KeyVaultBenchmarkModel model, string run, string? chatToolsReasoningEffort = null,
        string? wireApi = null, string? inferenceTransport = null, int outputLimit = 32768)
    {
        KeyVaultBenchmarkModel.ValidateSdkChatToolsReasoningEffort(chatToolsReasoningEffort);
        ValidateWireApi(wireApi);
        var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
        var app = builder.Build(); var proxy = new CampaignInferenceProxy(app);
        app.MapPost("/inference/ready", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            if (await reader.ReadToEndAsync(context.RequestAborted) != "sdk-http-interception-v1") { context.Response.StatusCode = 400; return; }
            context.Response.Headers[CopilotInferenceProxyHandler.RetryOwnerHeader] = CopilotInferenceProxyHandler.RetryOwner;
            proxy._ready = true; context.Response.StatusCode = 204;
        });
        app.MapPost("/inference", async (HttpContext context) =>
        {
            if (!proxy._ready || !proxy.ExecutionEnabled) { context.Response.StatusCode = 403; return; }
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            try
            {
                var value = context.Request.Headers[CopilotInferenceProxyHandler.AttemptHeader].ToString();
                var credit = value.Length == 0 ? 1 : int.TryParse(value, out var parsed) && parsed is >= 1 and <= 3
                    ? parsed : throw new InvalidOperationException("Invalid SDK physical attempt allowance.");
                var result = await model.ProxyInferenceAsync(run, context.Request.Headers.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase), body, context.RequestAborted, chatToolsReasoningEffort, wireApi, inferenceTransport, outputLimit, credit, value.Length == 0 ? 1 : null);
                context.Response.StatusCode = result.Status;
                if (result.Failure is not null) context.Response.Headers[CopilotInferenceFailure.Header] = result.Failure.ToJsonString();
                context.Response.ContentType = result.ContentType;
                if (result.RetryHeader?["name"]?.ToString() is "Retry-After" or "retry-after-ms" or "x-ms-retry-after-ms")
                    context.Response.Headers[result.RetryHeader["name"]!.ToString()] = result.RetryHeader["value"]!.ToString();
                await context.Response.WriteAsync(result.Body, context.RequestAborted);
            }
            catch (SdkAttemptCreditRequired next)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                context.Response.Headers[CopilotInferenceProxyHandler.NextAttemptHeader] = next.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                await model.RetainProxyFailureAsync(run, body, ex, context.Request.Headers["X-GnOuGo-Inference-Upstream"].ToString());
                context.Response.StatusCode = ex is InvalidOperationException ? 400 : 502;
                if (ex.Data[CopilotInferenceFailure.Header] is CopilotInferenceFailure failure)
                    context.Response.Headers[CopilotInferenceFailure.Header] = System.Text.Json.JsonSerializer.Serialize(failure, CopilotCoreJsonContext.Default.CopilotInferenceFailure);
                await context.Response.WriteAsync("Campaign inference admission or transport failed; encrypted evidence retained.", CancellationToken.None);
            }
        });
        await app.StartAsync(); return proxy;
    }
    internal static void ValidateWireApi(string? wireApi)
    {
        if (wireApi is not (null or "completions" or "responses")) throw new ArgumentException("Invalid explicit SDK wire API.");
    }
    internal static void ConfigureWireApi(McpServerOptions settings, string provider, string? wireApi)
    {
        ValidateWireApi(wireApi);
        if (wireApi is null) return;
        if (string.IsNullOrEmpty(provider) || !provider.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException("An exact SDK provider configuration name is required.");
        settings.EnvironmentVariables ??= new();
        settings.EnvironmentVariables["Code__Copilot__Providers__" + provider + "__WireApi"] = wireApi;
        // Existing KeyVault precedence remains intact. The proxy checks the actual
        // selected endpoint before admission; a conflicting saved setting cannot fall back.
    }
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}
