using System.Text.Json.Nodes;
using GnOuGo.AI.Core;

namespace GnOuGo.AI.Core.Tests;

public sealed class LocalLLMRoutingTests
{
    [Fact]
    public async Task CallAsync_RetriesInvalidStructuredOutputWithValidationFeedback()
    {
        var calls = new List<LLMClientRequest>();
        var runtime = new FakeRuntime((request, _) =>
        {
            calls.Add(LocalLLMProvider.CloneRequest(request));
            if (calls.Count == 1)
                throw new LocalLLMException(
                    LocalLLMFailureKind.InvalidStructuredOutput,
                    "invalid",
                    validationErrors: ["$.name: missing required property"]);
            return Task.FromResult(new LLMClientResponse
            {
                Text = "{\"name\":\"ok\"}",
                Json = new JsonObject { ["name"] = "ok" }
            });
        });
        var client = CreateClient(runtime);

        var response = await client.CallAsync(new LLMClientRequest
        {
            Model = "qwen3:0.6b",
            Prompt = "Return a name.",
            StructuredOutputSchema = NameSchema()
        }, TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Json!["name"]!.GetValue<string>());
        Assert.Equal(2, calls.Count);
        Assert.Contains("missing required property", calls[1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_UsesConfiguredCloudFallbackAfterTwoLocalFailures()
    {
        var localCalls = 0;
        var cloud = new FakeProvider("fakecloud", new LLMClientResponse { Text = "cloud" });
        var runtime = new FakeRuntime((_, _) =>
        {
            localCalls++;
            throw new LocalLLMException(LocalLLMFailureKind.ModelLoad, "load failed");
        });
        var options = Options();
        options.Models["Cloud"] = new ModelProviderOptions { Type = "fakecloud", Url = "https://cloud.invalid" };
        options.Fallback = new LLMFallbackOptions { Provider = "Cloud", Model = "cloud-model" };
        var client = new RoutingLLMClient(options, [new LocalLLMProvider(runtime), cloud]);

        var response = await client.CallAsync(
            new LLMClientRequest { Model = "qwen3:0.6b", Prompt = "plan" },
            TestContext.Current.CancellationToken);

        Assert.Equal("cloud", response.Text);
        Assert.Equal(2, localCalls);
        Assert.Equal(1, cloud.CallCount);
        Assert.Equal("cloud-model", cloud.LastModel);
    }

    [Fact]
    public async Task CallAsync_DoesNotFallbackWhenCallerCancels()
    {
        var cloud = new FakeProvider("fakecloud", new LLMClientResponse { Text = "cloud" });
        var runtime = new FakeRuntime((_, ct) => Task.FromCanceled<LLMClientResponse>(ct));
        var options = Options();
        options.Models["Cloud"] = new ModelProviderOptions { Type = "fakecloud", Url = "https://cloud.invalid" };
        options.Fallback = new LLMFallbackOptions { Provider = "Cloud", Model = "cloud-model" };
        var client = new RoutingLLMClient(options, [new LocalLLMProvider(runtime), cloud]);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.Cancel();

#pragma warning disable xUnit1051 // Deliberately pass a cancelled linked token to verify cancellation policy.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CallAsync(
            new LLMClientRequest { Model = "qwen3:0.6b", Prompt = "plan" },
            cts.Token));
#pragma warning restore xUnit1051
        Assert.Equal(0, cloud.CallCount);
    }

    [Fact]
    public void StructuredOutputValidator_ValidatesTheFlowContractSubset()
    {
        var schema = NameSchema();

        Assert.Empty(LLMStructuredOutputValidator.ValidateInstance(
            new JsonObject { ["name"] = "ok" },
            schema));
        Assert.Contains(
            LLMStructuredOutputValidator.ValidateInstance(new JsonObject(), schema),
            error => error.Contains("missing required property", StringComparison.Ordinal));
        Assert.Contains(
            LLMStructuredOutputValidator.ValidateInstance(
                new JsonObject { ["name"] = "ok", ["extra"] = true },
                schema),
            error => error.Contains("not allowed", StringComparison.Ordinal));
    }

    [Fact]
    public void StructuredOutputValidator_PreservesConditionalAndDependentRequirements()
    {
        var schema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "mode": { "type": "string" },
                "target": { "type": "string" },
                "offset": { "type": "integer" },
                "partition": { "type": "string" }
              },
              "dependentRequired": { "target": ["offset"] },
              "if": { "properties": { "mode": { "const": "position" } }, "required": ["mode"] },
              "then": { "required": ["target", "partition"] }
            }
            """);

        var errors = LLMStructuredOutputValidator.ValidateInstance(
            new JsonObject { ["mode"] = "position", ["target"] = "record-42" },
            schema);

        Assert.Contains(errors, error => error.Contains("offset", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("partition", StringComparison.Ordinal));
    }

    private static RoutingLLMClient CreateClient(ILocalLLMRuntime runtime)
        => new(Options(), [new LocalLLMProvider(runtime)]);

    private static LLMOptions Options()
        => new()
        {
            DefaultProvider = "Local",
            DefaultModel = "qwen3:0.6b",
            Models = new Dictionary<string, ModelProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Local"] = new() { Type = "local", Url = "embedded://llamasharp" }
            }
        };

    private static JsonObject NameSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray("name"),
            ["additionalProperties"] = false
        };

    private sealed class FakeRuntime(
        Func<LLMClientRequest, CancellationToken, Task<LLMClientResponse>> call) : ILocalLLMRuntime
    {
        public Task<LLMClientResponse> CallAsync(LLMClientRequest request, CancellationToken ct = default)
            => call(request, ct);
    }

    private sealed class FakeProvider(string providerType, LLMClientResponse response) : ILLMProvider
    {
        public string ProviderType => providerType;
        public int CallCount { get; private set; }
        public string? LastModel { get; private set; }

        public Task<LLMClientResponse> CallAsync(
            string model,
            ModelProviderOptions provider,
            LLMClientRequest request,
            CancellationToken ct)
        {
            CallCount++;
            LastModel = model;
            return Task.FromResult(response);
        }
    }
}
