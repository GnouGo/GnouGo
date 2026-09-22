using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.ProxyCopilot.Server.Configuration;
using GnOuGo.ProxyCopilot.Server.Protocols;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class EndpointTemplateTests
{
    [Theory]
    [InlineData("https://gateway.example/deployments/{model_name}", "https://gateway.example/deployments/model-a/chat/completions")]
    [InlineData("https://gateway.example/deployments/{model_name}/chat/completions", "https://gateway.example/deployments/model-a/chat/completions")]
    [InlineData("https://gateway.example/{model_name}/v1/", "https://gateway.example/model-a/v1/chat/completions")]
    [InlineData("https://gateway.example/{model_name}/deployments/{model_name}", "https://gateway.example/model-a/deployments/model-a/chat/completions")]
    [InlineData("https://gateway.example/v1", "https://gateway.example/v1/chat/completions")]
    public void SelectedUpstreamIdIsExpandedBeforeEndpointAndApiVersion(string template, string expected)
    {
        var options = ConfigurationAndTrafficTests.Options();
        var provider = options.Providers["test"];
        provider.Connection.Url = template;
        provider.Connection.ApiVersion = "version+preview";
        provider.Models["model"].UpstreamId = "model-a";
        var route = new ModelRegistry(options).Resolve("test/model");
        Assert.Equal(expected + "?api-version=version%2Bpreview", new OpenAiAdapter().Endpoint(route));
        Assert.Equal(template, provider.Connection.Url);
    }

    [Theory]
    [InlineData("https://{model_name}.example/v1")]
    [InlineData("https://gateway.example:{model_name}/v1")]
    [InlineData("https://gateway.example/v1?deployment={model_name}")]
    [InlineData("https://gateway.example/v1#{model_name}")]
    [InlineData("https://gateway.example/deployments/{model}")]
    [InlineData("https://gateway.example/deployments/{model_name")]
    [InlineData("https://gateway.example/deployments/model_name}")]
    [InlineData("https://gateway.example/deployments/{{model_name}}")]
    [InlineData("https://gateway.example/deployments/%7Bmodel_name%7D")]
    [InlineData("http://gateway.example/deployments/{model_name}")]
    [InlineData("https://user:password@gateway.example/deployments/{model_name}")]
    [InlineData("[https://gateway.example/deployments/{model_name}](https://gateway.example)")]
    public void InvalidTemplatesFailStartupWithoutPrintingTheUrl(string template)
    {
        var options = ConfigurationAndTrafficTests.Options();
        options.Providers["test"].Connection.Url = template;
        var error = Assert.Throws<InvalidOperationException>(() => new ModelRegistry(options));
        Assert.Contains("provider Url", error.Message);
        Assert.DoesNotContain("gateway.example", error.Message);
        Assert.DoesNotContain("password", error.Message);
    }

    [Theory]
    [InlineData(".")] [InlineData("..")]
    public void ModelCannotExpandToADotPathSegment(string upstreamId)
    {
        var options = ConfigurationAndTrafficTests.Options();
        options.Providers["test"].Connection.Url = "https://gateway.example/deployments/{model_name}";
        options.Providers["test"].Models["model"].UpstreamId = upstreamId;
        Assert.Throws<InvalidOperationException>(() => new ModelRegistry(options));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentAliasesReachTheirOwnEncodedDeploymentWithMatchingBody(bool streaming)
    {
        var received = new ConcurrentBag<string>();
        await using var upstream = await TestHost.Upstream(async context =>
        {
            var body = (await JsonNode.ParseAsync(context.Request.Body))!.AsObject();
            var model = body["model"]!.GetValue<string>();
            // RawTarget proves reserved characters cannot change URL structure.
            Assert.Equal("/deployments/" + Uri.EscapeDataString(model) + "/chat/completions?api-version=preview",
                context.Features.Get<IHttpRequestFeature>()!.RawTarget);
            Assert.Equal("Bearer provider-key", context.Request.Headers.Authorization.ToString());
            received.Add(model);
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture("openai", streaming, false),
                streaming ? "text/event-stream" : "application/json");
        });
        await using var proxy = await TestHost.Proxy(upstream.Url + "/deployments/{model_name}", extra: new()
        {
            ["ProxyCopilot:Providers:test:Authentication"] = "ApiKey",
            ["ProxyCopilot:Providers:test:Connection:ApiKey"] = "provider-key",
            ["ProxyCopilot:Providers:test:Connection:ApiVersion"] = "preview",
            ["ProxyCopilot:Providers:test:Models:other:UpstreamId"] = "space /?+#% ü",
            ["ProxyCopilot:Providers:test:Models:other:Metadata:MaxInputTokens"] = "10000",
            ["ProxyCopilot:Providers:test:Models:other:Metadata:MaxOutputTokens"] = "1000",
            ["ProxyCopilot:Providers:test:Models:other:Metadata:Capabilities:SupportsTools"] = "true"
        });
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            var request = ProtocolRoundTripTests.Request(streaming);
            var alias = index % 2 == 0 ? "test/model" : "test/other";
            request["model"] = alias;
            using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(request.ToJsonString()), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var answer = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains(alias, answer);
            Assert.Contains("Bonjour", answer);
            if (streaming) Assert.Contains("[DONE]", answer);
        }));
        Assert.Equal(6, received.Count(m => m == "vendor/upstream-model"));
        Assert.Equal(6, received.Count(m => m == "space /?+#% ü"));
    }
}
