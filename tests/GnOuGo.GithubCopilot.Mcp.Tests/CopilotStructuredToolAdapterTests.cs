using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.GithubCopilot.Core;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class CopilotStructuredToolAdapterTests
{
    [Fact]
    public async Task UsesSharedStructuredBuilderWithoutChangingEndpointCredentialsConversationOrToolDeclarations()
    {
        using var input = Request(); var before = await input.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var prepared = await new CopilotStructuredToolAdapter().PrepareAsync(input, Context());
        var actual = JsonNode.Parse(await prepared.Request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        var original = JsonNode.Parse(before)!;
        Assert.Equal(input.RequestUri, prepared.Request.RequestUri);
        Assert.Equal(input.Headers.Authorization, prepared.Request.Headers.Authorization);
        Assert.Equal(input.Version, prepared.Request.Version);
        Assert.Equal("high", actual["reasoning_effort"]!.ToString());
        Assert.Null(actual["tools"]); Assert.Null(actual["tool_choice"]);
        Assert.Equal("json_schema", actual["response_format"]!["type"]!.ToString());
        Assert.True(actual["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.False(actual["stream"]!.GetValue<bool>());
        var messages = actual["messages"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(original["messages"]![0], messages[0]));
        Assert.True(JsonNode.DeepEquals(original["messages"]![1], messages[3]));
        Assert.DoesNotContain("full contract", messages[1]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[2]!["role"]!.ToString());
        var contracts = JsonNode.Parse(messages[2]!["content"]!.GetValue<string>())!;
        Assert.True(JsonNode.DeepEquals(original["tools"], contracts["tools"]));
        Assert.Equal(before, await input.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualModelSelectionBecomesSdkToolCallsWithStableIdsAndUnchangedUsage(bool stream)
    {
        using var request = Request(stream);
        using var prepared = await new CopilotStructuredToolAdapter().PrepareAsync(request, Context());
        using var response = Response("""{"action":{"kind":"tools","calls":[{"name":"execute","arguments":"{\"command\":\"observed command\"}"}]}}""");
        using var restored = await prepared.RestoreResponseAsync(response, TestContext.Current.CancellationToken);
        var body = await restored.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (!stream)
        {
            var result = JsonNode.Parse(body)!;
            Assert.Equal("tool_calls", result["choices"]![0]!["finish_reason"]!.ToString());
            Assert.Equal("execute", result["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["name"]!.ToString());
            Assert.Equal("{\"command\":\"observed command\"}", result["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"]!.ToString());
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["usage"], result["usage"]));
        }
        else
        {
            Assert.StartsWith("text/event-stream", restored.Content.Headers.ContentType!.ToString());
            var events = body.Split('\n').Where(l => l.StartsWith("data: {")).Select(l => JsonNode.Parse(l[6..])!).ToArray();
            Assert.Equal(3, events.Length); Assert.Equal(0, events[0]["choices"]![0]!["delta"]!["tool_calls"]![0]!["index"]!.GetValue<int>());
            Assert.Equal(2, events[2]["usage"]!["completion_tokens"]!.GetValue<int>());
            Assert.EndsWith("data: [DONE]\n\n", body);
        }
        using var repeat = await prepared.RestoreResponseAsync(response, TestContext.Current.CancellationToken);
        Assert.Equal(body, await repeat.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("unknown_tool")]
    [InlineData("arguments_array")]
    [InlineData("empty_calls")]
    [InlineData("extra_field")]
    [InlineData("length")]
    [InlineData("required_final")]
    [InlineData("parallel_forbidden")]
    public async Task InvalidOrIncompleteSelectionCannotExposeToolEffects(string change)
    {
        using var input = Request(); var wire = JsonNode.Parse(await input.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        if (change == "required_final") wire["tool_choice"] = "required";
        input.Content = new StringContent(wire.ToJsonString());
        using var prepared = await new CopilotStructuredToolAdapter().PrepareAsync(input, Context());
        var decision = JsonNode.Parse("""{"action":{"kind":"tools","calls":[{"name":"execute","arguments":"{}"}]}}""")!;
        switch (change)
        {
            case "unknown_tool": decision["action"]!["calls"]![0]!["name"] = "unregistered"; break;
            case "arguments_array": decision["action"]!["calls"]![0]!["arguments"] = "[]"; break;
            case "empty_calls": decision["action"]!["calls"] = new JsonArray(); break;
            case "extra_field": decision["extra"] = true; break;
            case "required_final": decision = JsonNode.Parse("""{"action":{"kind":"final","content":"claim"}}""")!; break;
            case "parallel_forbidden": decision["action"]!["calls"]!.AsArray().Add(decision["action"]!["calls"]![0]!.DeepClone()); break;
        }
        using var response = Response(decision.ToJsonString(), change == "length" ? "length" : "stop");
        await Assert.ThrowsAsync<InvalidDataException>(() => prepared.RestoreResponseAsync(response, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProviderErrorsPassThroughWithoutInventingASelection()
    {
        using var input = Request(); using var prepared = await new CopilotStructuredToolAdapter().PrepareAsync(input, Context());
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("actual rejection") };
        Assert.Same(response, await prepared.RestoreResponseAsync(response, TestContext.Current.CancellationToken));
        Assert.Equal("actual rejection", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExplicitTransportIsCapturedAndNativeDefaultKeepsTheOldConfigurationShape()
    {
        var settings = new CodeServerSettings();
        var original = CopilotMcpConfiguration.BuildRuntimeConfiguration(settings, Path.GetTempPath(), null);
        var nativeJson = JsonSerializer.SerializeToNode(original, CopilotCoreJsonContext.Default.CopilotRuntimeConfiguration)!;
        Assert.Null(nativeJson["inferenceTransport"]);
        new CodeServerSettingsOptionsConfigurator(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["Code:Copilot:InferenceTransport"] = CopilotStructuredToolAdapter.TransportName }).Build()).Configure(settings);
        var configured = CopilotMcpConfiguration.BuildRuntimeConfiguration(settings, Path.GetTempPath(), null);
        var stored = JsonSerializer.SerializeToNode(configured, CopilotCoreJsonContext.Default.CopilotRuntimeConfiguration)!;
        Assert.Equal(CopilotStructuredToolAdapter.TransportName, stored["inferenceTransport"]!.ToString());
        Assert.Equal(CopilotStructuredToolAdapter.TransportName,
            stored.Deserialize(CopilotCoreJsonContext.Default.CopilotRuntimeConfiguration)!.InferenceTransport);
        Assert.Throws<InvalidDataException>(() => CopilotStructuredToolAdapter.NormalizeTransport("unknown"));
        Assert.Throws<InvalidOperationException>(() => new GitHubCopilotSdkClientFactory().Create(configured));
    }

    private static HttpRequestMessage Request(bool stream = false)
    {
        var body = JsonNode.Parse("""{"model":"configured","reasoning_effort":"high","max_completion_tokens":8192,"parallel_tool_calls":false,"messages":[{"role":"system","content":"existing policy"},{"role":"user","content":"actual objective"}],"tools":[{"type":"function","function":{"name":"execute","description":"full contract","parameters":{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}}}]}""")!;
        body["stream"] = stream;
        var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/deployments/configured/chat/completions?api-version=retained") { Content = new StringContent(body.ToJsonString()) };
        request.Headers.Authorization = new("Bearer", "controlled-credential");
        return request;
    }
    private static HttpResponseMessage Response(string decision, string finish = "stop")
        => new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["id"] = "observed-completion", ["model"] = "configured",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["finish_reason"] = finish,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = decision } }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 2 } }.ToJsonString()) };

    // Exercise the same explicitly adopted, pinned SDK HTTP hook as Core/MCP.
#pragma warning disable GHCP001
    private static GitHub.Copilot.CopilotRequestContext Context() =>
        (GitHub.Copilot.CopilotRequestContext)Activator.CreateInstance(typeof(GitHub.Copilot.CopilotRequestContext),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            ["request-id", "https://provider.example/v1/chat/completions", new Dictionary<string, IReadOnlyList<string>>()], null)!;
#pragma warning restore GHCP001
}
