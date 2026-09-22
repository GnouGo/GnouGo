using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class OutputTokenLimitTests
{
    private const string Policy = "ProxyCopilot:Providers:test:Connection:RequestPolicy:";
    private const string Unsupported = "ProxyCopilot:Providers:test:Models:model:Metadata:Capabilities:UnsupportedRequestParameters:";

    [Theory]
    [InlineData("openai", false, null)] [InlineData("openai", true, null)]
    [InlineData("copilot", false, null)] [InlineData("copilot", true, null)]
    [InlineData("openai", false, "max_tokens")] [InlineData("openai", true, "max_tokens")]
    [InlineData("copilot", false, "max_tokens")] [InlineData("copilot", true, "max_tokens")]
    [InlineData("openai", false, "max_completion_tokens")] [InlineData("openai", true, "max_completion_tokens")]
    [InlineData("copilot", false, "max_completion_tokens")] [InlineData("copilot", true, "max_completion_tokens")]
    public async Task LimitsUseOneSupportedParameterBeforeFirstDispatch(string type, bool streaming, string? unsupported)
    {
        var received = new List<JsonObject>();
        var expectedField = unsupported == "max_completion_tokens" ? "max_tokens" : "max_completion_tokens";
        var rejectedField = expectedField == "max_tokens" ? "max_completion_tokens" : "max_tokens";
        await using var upstream = await TestHost.Upstream(async context =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            received.Add(body);
            // Reproduce the model's rejection, including a legacy parameter set to null.
            if (body.ContainsKey(rejectedField) || body[expectedField] is null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("{\"error\":{\"code\":\"unsupported_parameter\"}}", context.RequestAborted);
                return;
            }
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture(type, streaming, false),
                streaming ? "text/event-stream" : "application/json");
        });
        var configuration = new Dictionary<string, string?>
        {
            [Policy + "DefaultMaxOutputTokens"] = "600",
            [Policy + "MaxOutputTokensCap"] = "700"
        };
        if (unsupported is not null) configuration[Unsupported + "0"] = unsupported;
        await using var proxy = await TestHost.Proxy(upstream.Url, type, configuration);
        (string? Field, int? Value, int Expected)[] inputs =
            [(null, null, 600), ("max_tokens", 250, 250), ("max_completion_tokens", 2000, 700), ("max_tokens", null, 600)];
        foreach (var (field, value, expected) in inputs)
        {
            var request = ProtocolRoundTripTests.Request(streaming);
            if (field is not null) request[field] = value;
            using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var answer = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Bonjour", answer);
            if (streaming) Assert.Contains("[DONE]", answer);
            Assert.Equal(expected, received[^1][expectedField]!.GetValue<int>());
            Assert.Equal("vendor/upstream-model", received[^1]["model"]!.GetValue<string>());
        }
        Assert.Equal(inputs.Length, received.Count);
    }

    [Theory]
    [InlineData("Omit", null)]
    [InlineData("ModelMaximum", 1000)]
    public async Task UnspecifiedLimitPolicyIsPreserved(string mode, int? expected)
    {
        JsonObject? received = null;
        await using var upstream = await TestHost.Upstream(async context =>
        {
            received = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture("openai", false, false), "application/json");
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: new() { [Policy + "UnspecifiedOutputTokens"] = mode });
        var request = ProtocolRoundTripTests.Request(false);
        request["max_tokens"] = null;
        request["max_completion_tokens"] = null;
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(received);
        Assert.False(received.ContainsKey("max_tokens"));
        Assert.Equal(expected, received["max_completion_tokens"]?.GetValue<int>());
        Assert.Equal(expected.HasValue, received.ContainsKey("max_completion_tokens"));
    }

    [Theory]
    [InlineData("openai", "{\"max_tokens\":0}", "max_tokens")]
    [InlineData("openai", "{\"max_completion_tokens\":-1}", "max_completion_tokens")]
    [InlineData("openai", "{\"max_tokens\":\"100\"}", "max_tokens")]
    [InlineData("openai", "{\"max_tokens\":1.5}", "max_tokens")]
    [InlineData("openai", "{\"max_tokens\":100,\"max_completion_tokens\":200}", "max_tokens")]
    [InlineData("openai", "{\"temperature\":1}", "temperature")]
    [InlineData("openai", "{}", "max_tokens,max_completion_tokens")]
    [InlineData("anthropic", "{\"max_tokens\":100}", "max_tokens")]
    public async Task InvalidOrUntranslatableParametersStillFailBeforeDispatch(string type, string fields, string unsupported)
    {
        var calls = 0;
        await using var upstream = await TestHost.Upstream(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        var configuration = new Dictionary<string, string?>();
        foreach (var (field, index) in unsupported.Split(',').Select((field, index) => (field, index)))
            configuration[Unsupported + index] = field;
        await using var proxy = await TestHost.Proxy(upstream.Url, type, configuration);
        var request = ProtocolRoundTripTests.Request(false);
        foreach (var field in JsonNode.Parse(fields)!.AsObject()) request[field.Key] = field.Value?.DeepClone();
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("unsupported_parameter", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }
}
