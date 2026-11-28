using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Integrations;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class ConfigureProvidersServiceTests
{
    [Fact]
    public async Task ProviderValidationReportsTheBackgroundReceiptCooldownAndSafeCorrelations()
    {
        const string secret = "private-fixture-marker";
        var handler = new RateLimitedReceipt();
        using var http = new HttpClient(handler);
        var options = new LLMOptions { DefaultProvider = "openai", DefaultModel = "fixture" };
        options.Models["openai"] = new() { Type = "openai", Url = "https://receipt.example/v1", ApiKey = secret,
            RetryPolicy = new() { MaxTotalDelayMilliseconds = 10 } };
        var model = new RoutingLLMClientAdapter(new RoutingLLMClient(http, options));
        var service = SmartFlowTestFactory.CreateProvidersService(model, options: options, telemetry: _telemetry.Telemetry);
        var ct = TestContext.Current.CancellationToken;
        var events = await SmartFlowTestFactory.CollectAsync(service.ValidateConfigAsync(new JsonObject
            { ["llm_provider"] = "openai", ["llm_model"] = "fixture", ["llm_auth_type"] = "key" }, ct), ct);
        var message = events[^1].Text!;
        Assert.Contains("429", message);
        Assert.Contains("45000", message);
        Assert.Contains("provider_rejection", message);
        Assert.Contains("Provider retry permitted from", message);
        Assert.Contains("Physical request", message);
        Assert.Contains("Provider request SHA-256", message);
        Assert.DoesNotContain(secret, message);
        Assert.Equal(["POST /v1/responses", "GET /v1/responses/resp_fixture"], handler.Calls);
    }

    private sealed class RateLimitedReceipt : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
                    { Content = new StringContent("""{"id":"resp_fixture","status":"queued"}""") });
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent("""{"error":{"code":"rate_limit_exceeded","message":"private-fixture-marker"}}""") };
            response.Headers.TryAddWithoutValidation("retry-after-ms", "45000");
            response.Headers.TryAddWithoutValidation("x-request-id", "private-fixture-marker");
            return Task.FromResult(response);
        }
    }
}
