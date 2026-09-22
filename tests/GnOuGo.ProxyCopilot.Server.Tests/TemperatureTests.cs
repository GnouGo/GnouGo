using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.ProxyCopilot.Server.Traffic;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class TemperatureTests
{
    private const string Capabilities = "ProxyCopilot:Providers:test:Models:model:Metadata:Capabilities:";

    public static IEnumerable<object[]> UnsupportedCases()
    {
        foreach (var type in new[] { "openai", "copilot", "anthropic", "ollama" })
            foreach (var streaming in new[] { false, true })
                foreach (var listedAsUnsupported in new[] { false, true })
                    yield return [type, streaming, listedAsUnsupported];
    }

    [Theory]
    [MemberData(nameof(UnsupportedCases))]
    public async Task UnsupportedTemperatureIsOmittedAndOriginalTrafficRemainsVisible(string type, bool streaming, bool listedAsUnsupported)
    {
        JsonObject? received = null;
        var calls = 0;
        await using var upstream = await TestHost.Upstream(async context =>
        {
            Interlocked.Increment(ref calls);
            received = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture(type, streaming, false),
                streaming ? type == "ollama" ? "application/x-ndjson" : "text/event-stream" : "application/json");
        });
        var configuration = new Dictionary<string, string?>
        {
            [Capabilities + "SupportsTemperature"] = listedAsUnsupported ? "true" : "false"
        };
        if (listedAsUnsupported) configuration[Capabilities + "UnsupportedRequestParameters:0"] = "temperature";
        await using var proxy = await TestHost.Proxy(upstream.Url, type, configuration);
        foreach (var temperature in new double?[] { 0, 0.7, 2, null })
        {
            var request = ProtocolRoundTripTests.Request(streaming);
            request["temperature"] = temperature;
            request["top_p"] = 0.8;
            request["max_tokens"] = 250;
            using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var answer = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Bonjour", answer);
            if (streaming) Assert.Contains("[DONE]", answer);
            Assert.NotNull(received);
            var parameters = type == "ollama" ? received["options"]!.AsObject() : received;
            Assert.False(parameters.ContainsKey("temperature"));
            Assert.Equal(0.8, parameters["top_p"]!.GetValue<double>());
            Assert.Equal(250, parameters[type switch { "ollama" => "num_predict", "anthropic" => "max_tokens", _ => "max_completion_tokens" }]!.GetValue<int>());
            Assert.NotEmpty(received["messages"]!.AsArray());
            Assert.NotEmpty(received["tools"]!.AsArray());
            var id = response.Headers.GetValues("X-GnOuGo-Request-Id").Single();
            var detail = proxy.App.Services.GetRequiredService<ITrafficStore>().Detail(id)!;
            Assert.True(JsonNode.DeepEquals(request, JsonNode.Parse(detail.Bodies["clientRequest"].Text)));
            Assert.True(JsonNode.DeepEquals(received, JsonNode.Parse(detail.Bodies["upstreamRequest"].Text)));
        }
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData("openai", "true")] [InlineData("openai", null)]
    [InlineData("copilot", "true")] [InlineData("copilot", null)]
    [InlineData("anthropic", "true")] [InlineData("anthropic", null)]
    [InlineData("ollama", "true")] [InlineData("ollama", null)]
    public async Task SupportedOrUnknownTemperatureIsPreserved(string type, string? supportsTemperature)
    {
        JsonObject? received = null;
        await using var upstream = await TestHost.Upstream(async context =>
        {
            received = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture(type, false, false), "application/json");
        });
        var configuration = new Dictionary<string, string?>();
        if (supportsTemperature is not null) configuration[Capabilities + "SupportsTemperature"] = supportsTemperature;
        await using var proxy = await TestHost.Proxy(upstream.Url, type, configuration);
        var request = ProtocolRoundTripTests.Request(false);
        request["temperature"] = 0.6;
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(received);
        var parameters = type == "ollama" ? received["options"]!.AsObject() : received;
        Assert.Equal(0.6, parameters["temperature"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("-0.1")] [InlineData("2.1")] [InlineData("\"0.7\"")]
    [InlineData("true")] [InlineData("{}")] [InlineData("[]")]
    public async Task MalformedTemperatureStillFailsBeforeDispatchWhenUnsupported(string value)
    {
        var calls = 0;
        await using var upstream = await TestHost.Upstream(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: new() { [Capabilities + "SupportsTemperature"] = "false" });
        var request = ProtocolRoundTripTests.Request(false);
        request["temperature"] = JsonNode.Parse(value);
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("temperature", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }
}
