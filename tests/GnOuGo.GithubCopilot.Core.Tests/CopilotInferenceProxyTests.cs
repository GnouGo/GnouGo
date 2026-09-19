using System.Net;
using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotInferenceProxyTests
{
    [Fact]
    public async Task EveryHttpDispatchUsesThePolicyHostAndPreservesTheWireRequest()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(async request =>
        {
            calls++;
            Assert.Equal("http://127.0.0.1:8123/inference", request.RequestUri!.AbsoluteUri);
            Assert.Equal("https://provider.example/v1/responses", Assert.Single(request.Headers.GetValues(CopilotInferenceProxyHandler.UpstreamHeader)));
            Assert.Equal("request-id", Assert.Single(request.Headers.GetValues(CopilotInferenceProxyHandler.RequestHeader)));
            Assert.Equal("Bearer test-only", request.Headers.Authorization!.ToString());
            Assert.Equal("{\"max_output_tokens\":8192}", await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        }));
        var proxy = new TestProxy(client, new Uri("http://127.0.0.1:8123/inference"));
        using var original = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/responses") { Content = new StringContent("{\"max_output_tokens\":8192}") };
        original.Headers.Authorization = new("Bearer", "test-only");
        original.Headers.TryAddWithoutValidation(CopilotInferenceProxyHandler.UpstreamHeader.ToLowerInvariant(), "https://wrong.example/");
        using var response = await proxy.Send(original, Context("request-id"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode); Assert.Equal(1, calls);
        Assert.Equal("https://provider.example/v1/responses", original.RequestUri!.AbsoluteUri);
        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.Socket(Context("socket")));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("https://external.example/policy")]
    [InlineData("http://user@localhost/policy")]
    [InlineData("http://localhost/policy#fragment")]
    public void InvalidProxyCannotEnableDirectFallback(string endpoint)
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentException>(() => new CopilotInferenceProxyHandler(client, new Uri(endpoint)));
    }

    // SDK contexts are runtime-owned; construct its internal transport envelope for the adapter test.
    private static GitHub.Copilot.CopilotRequestContext Context(string id) =>
        (GitHub.Copilot.CopilotRequestContext)Activator.CreateInstance(typeof(GitHub.Copilot.CopilotRequestContext),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [id, "https://provider.example/v1/responses", new Dictionary<string, IReadOnlyList<string>>()], null)!;

    private sealed class TestProxy(HttpClient client, Uri endpoint) : CopilotInferenceProxyHandler(client, endpoint)
    {
        public Task<HttpResponseMessage> Send(HttpRequestMessage request, GitHub.Copilot.CopilotRequestContext context) => SendRequestAsync(request, context);
        public Task<CopilotWebSocketHandler> Socket(GitHub.Copilot.CopilotRequestContext context) => OpenWebSocketAsync(context);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
