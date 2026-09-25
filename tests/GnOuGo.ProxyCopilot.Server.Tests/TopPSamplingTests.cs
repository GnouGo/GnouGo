using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.ProxyCopilot.Server.Traffic;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class TopPSamplingTests
{
    private const string Capabilities = "ProxyCopilot:Providers:test:Models:model:Metadata:Capabilities:";

    [Theory]
    [InlineData("openai", false)] [InlineData("openai", true)]
    [InlineData("copilot", false)] [InlineData("copilot", true)]
    [InlineData("anthropic", false)] [InlineData("anthropic", true)]
    [InlineData("ollama", false)] [InlineData("ollama", true)]
    public async Task UnsupportedTopPIsOmittedWithoutChangingSupportedHintsToolsOrCaptures(string type, bool streaming)
    {
        JsonObject? received = null;
        await using var upstream = await TestHost.Upstream(async context =>
        {
            received = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture(type, streaming, false),
                streaming ? type == "ollama" ? "application/x-ndjson" : "text/event-stream" : "application/json");
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, type, new()
        {
            [Capabilities + "SupportsTemperature"] = "true",
            [Capabilities + "UnsupportedRequestParameters:0"] = "top_p"
        });
        var request = ProtocolRoundTripTests.Request(streaming);
        request["temperature"] = 0.4;
        request["top_p"] = 1;
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Bonjour", answer);
        if (streaming) Assert.Contains("[DONE]", answer);
        Assert.NotNull(received);
        var parameters = type == "ollama" ? received["options"]!.AsObject() : received;
        Assert.False(parameters.ContainsKey("top_p"));
        Assert.Equal(0.4, parameters["temperature"]!.GetValue<double>());
        Assert.NotEmpty(received["tools"]!.AsArray());
        var id = response.Headers.GetValues("X-GnOuGo-Request-Id").Single();
        var detail = proxy.App.Services.GetRequiredService<ITrafficStore>().Detail(id)!;
        Assert.True(JsonNode.DeepEquals(request, JsonNode.Parse(detail.Bodies["clientRequest"].Text)));
        Assert.True(JsonNode.DeepEquals(received, JsonNode.Parse(detail.Bodies["upstreamRequest"].Text)));
    }

    [Theory]
    [InlineData("top_p", "-0.1")] [InlineData("top_p", "1.1")]
    [InlineData("top_p", "\"1\"")] [InlineData("top_p", "true")]
    [InlineData("top_p", "{}")] [InlineData("top_p", "[]")]
    [InlineData("seed", "42")]
    public async Task InvalidSamplingValuesAndOtherUnsupportedFieldsStillFailBeforeDispatch(string field, string value)
    {
        var calls = 0;
        await using var upstream = await TestHost.Upstream(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: new() { [Capabilities + "UnsupportedRequestParameters:0"] = field });
        var request = ProtocolRoundTripTests.Request(false);
        request[field] = JsonNode.Parse(value);
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }
}
