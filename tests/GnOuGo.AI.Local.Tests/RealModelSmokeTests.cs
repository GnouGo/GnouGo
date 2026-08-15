using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using Microsoft.Extensions.Options;

namespace GnOuGo.AI.Local.Tests;

public sealed class RealModelSmokeTests
{
    [Fact]
    [Trait("Category", "LocalModelSmoke")]
    public async Task Qwen3_GeneratesValidatedStructuredPlan()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GNOUOGO_LOCAL_MODEL_SMOKE"), "1", StringComparison.Ordinal))
            return;

        var modelPath = Environment.GetEnvironmentVariable("GNOUOGO_LOCAL_MODEL_PATH");
        Assert.False(string.IsNullOrWhiteSpace(modelPath));
        Assert.True(File.Exists(modelPath));
        Assert.Equal(LocalModelCatalog.Qwen3.FileName, Path.GetFileName(modelPath));

        await using var runtime = new LlamaSharpLocalLLMRuntime(
            Path.GetDirectoryName(Path.GetFullPath(modelPath))!,
            Options.Create(new LocalLLMOptions { ContextSize = 4096, MaxOutputTokens = 256, Seed = 1337 }));
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["steps"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = 5,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["id"] = new JsonObject { ["type"] = "string" },
                            ["action"] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = new JsonArray("id", "action"),
                        ["additionalProperties"] = false
                    }
                }
            },
            ["required"] = new JsonArray("steps"),
            ["additionalProperties"] = false
        };

        LLMClientResponse response;
        try
        {
            response = await runtime.CallAsync(new LLMClientRequest
            {
                Provider = "local",
                Model = LocalModelCatalog.Qwen3Id,
                Prompt = "Create exactly one step. The result must be {\"steps\":[{\"id\":\"list-files\",\"action\":\"List files in the current workspace\"}]} with semantically equivalent string values.",
                Temperature = 0,
                MaxOutputTokens = 256,
                StructuredOutputSchema = schema,
                StructuredOutputStrict = true
            }, TestContext.Current.CancellationToken);
        }
        catch (LocalLLMException ex) when (ex.ValidationErrors.Count > 0)
        {
            Assert.Fail(string.Join("; ", ex.ValidationErrors));
            throw;
        }

        Assert.Empty(LLMStructuredOutputValidator.ValidateInstance(response.Json, schema));
    }
}
