using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotInferenceAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualAdaptedPayloadIsReservedBeforeTheExistingPolicyProxy(bool tooLarge)
    {
        var events = new List<string>(); var sends = 0;
        var body = new JsonObject { ["model"] = "configured", ["max_completion_tokens"] = 64,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new string('x', 3000) }) }.ToJsonString();
        var budget = new CopilotInferenceBudget(2, tooLarge ? 7000 : 20_000, DateTimeOffset.UtcNow.AddMinutes(1),
            (_, _, _) => { events.Add("reserve"); return Task.CompletedTask; });
        using var http = new HttpClient(new Handler(async request =>
        {
            sends++; events.Add("send");
            Assert.Equal("http://127.0.0.1:8123/inference", request.RequestUri!.AbsoluteUri);
            Assert.Equal(body, await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("observed response") };
        }));
        var proxy = new CopilotInferenceProxyHandler(http, new Uri("http://127.0.0.1:8123/inference"));
        using var original = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/chat/completions")
            { Content = new StringContent("{\"max_completion_tokens\":1}") };
        Task<HttpResponseMessage> Send() => CopilotInferenceDispatch.SendAsync(original, Context(), new Adapter(body),
            proxy, budget.ReserveAsync, budget.RecordTransportFailure);
        if (tooLarge)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(Send);
            Assert.Equal(0, sends); Assert.Equal(0, budget.ModelCalls); Assert.Empty(events);
        }
        else
        {
            using var response = await Send();
            Assert.Equal("observed response", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(["reserve", "send"], events);
            Assert.Equal(1, sends); Assert.Equal(1, budget.ModelCalls);
            Assert.Equal(Encoding.UTF8.GetByteCount(body) + 4096L + 64, budget.ChargedTokens);
        }
        Assert.Equal("{\"max_completion_tokens\":1}", await original.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReceivedButInvalidDecisionDoesNotBecomeATransportRetryOrReleaseTheReservation()
    {
        var sends = 0;
        var budget = new CopilotInferenceBudget(1, 20_000, DateTimeOffset.UtcNow.AddMinutes(1), (_, _, _) => Task.CompletedTask);
        using var http = new HttpClient(new Handler(_ =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("received malformed decision") });
        }));
        var proxy = new CopilotInferenceProxyHandler(http, new Uri("http://127.0.0.1:8123/inference"));
        using var original = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/chat/completions")
            { Content = new StringContent("{\"max_completion_tokens\":64}") };
        await Assert.ThrowsAsync<InvalidDataException>(() => CopilotInferenceDispatch.SendAsync(original, Context(),
            new Adapter("{\"max_completion_tokens\":64}", invalidResponse: true), proxy, budget.ReserveAsync, budget.RecordTransportFailure));
        Assert.Equal(1, sends); Assert.Equal(1, budget.ModelCalls); Assert.True(budget.ChargedTokens > 4096);
        Assert.False(budget.TransportFailed);
    }

    [Fact]
    public void CapturedAdapterCannotSilentlyUseAnUnregisteredNativeTransport()
    {
        var config = new CopilotRuntimeConfiguration(Path.GetTempPath(), "configured") { InferenceTransport = "explicit-test-format" };
        Assert.Throws<InvalidOperationException>(() => new GitHubCopilotSdkClientFactory().Create(config));
        var factory = new GitHubCopilotSdkClientFactory(null, null, _ => new Adapter("{}"));
        Assert.Throws<InvalidOperationException>(() => factory.Create(config with { InferenceTransport = null }));
    }

    private sealed class Adapter(string body, bool invalidResponse = false) : CopilotInferenceAdapter
    {
        public override Task<CopilotPreparedInference> PrepareAsync(HttpRequestMessage request, GitHub.Copilot.CopilotRequestContext context)
            => Task.FromResult<CopilotPreparedInference>(new Prepared(new HttpRequestMessage(request.Method, request.RequestUri)
                { Content = new StringContent(body) }, invalidResponse));
    }
    private sealed class Prepared(HttpRequestMessage request, bool invalidResponse) : CopilotPreparedInference
    {
        public override HttpRequestMessage Request => request;
        public override Task<HttpResponseMessage> RestoreResponseAsync(HttpResponseMessage response, CancellationToken ct)
            => invalidResponse ? Task.FromException<HttpResponseMessage>(new InvalidDataException("Invalid controlled decision.")) : Task.FromResult(response);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
    private static GitHub.Copilot.CopilotRequestContext Context() =>
        (GitHub.Copilot.CopilotRequestContext)Activator.CreateInstance(typeof(GitHub.Copilot.CopilotRequestContext),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            ["request-id", "https://provider.example/v1/chat/completions", new Dictionary<string, IReadOnlyList<string>>()], null)!;
}
