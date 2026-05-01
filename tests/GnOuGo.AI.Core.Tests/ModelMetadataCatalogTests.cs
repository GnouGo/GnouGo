using GnOuGo.AI.Core;

namespace GnOuGo.AI.Core.Tests;

public sealed class ModelMetadataCatalogTests
{
    [Fact]
    public void Resolve_ReturnsBuiltinPricingLimitsAndCapabilities()
    {
        var resolver = new LLMModelMetadataResolver(new LLMOptions());

        var metadata = resolver.Resolve("openai", "o4-mini");

        Assert.Equal("o4-mini", metadata.Id);
        Assert.Equal(200000, metadata.ContextWindowTokens);
        Assert.Equal(100000, metadata.MaxOutputTokens);
        Assert.Equal(1.10m, metadata.Pricing!.InputPer1MTokens);
        Assert.False(metadata.Capabilities.SupportsTemperature);
        Assert.True(metadata.Capabilities.SupportsReasoningEffort);
        Assert.Contains("temperature", metadata.Capabilities.UnsupportedRequestParameters!);
    }

    [Fact]
    public void Resolve_AppliesInlineOverrides()
    {
        var options = new LLMOptions();
        options.ModelOverrides["o4-mini"] = new LLMModelMetadata
        {
            Id = "o4-mini",
            MaxOutputTokens = 1234,
            Capabilities = new ModelCapabilityMetadata
            {
                SupportsTemperature = true,
                UnsupportedRequestParameters = []
            }
        };

        var metadata = new LLMModelMetadataResolver(options).Resolve("openai", "o4-mini");

        Assert.Equal(1234, metadata.MaxOutputTokens);
        Assert.True(metadata.Capabilities.SupportsTemperature);
    }

    [Fact]
    public void Resolve_LoadsExternalMetadataFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-model-metadata-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
        {
          "models": {
            "my-model": {
              "providerType": "openai",
              "contextWindowTokens": 999,
              "maxOutputTokens": 111,
              "pricing": { "inputPer1MTokens": 0.5, "outputPer1MTokens": 1.5 },
              "capabilities": { "supportsTemperature": false, "supportsReasoningEffort": false }
            }
          },
          "aliases": { "mine": "my-model" }
        }
        """);

        try
        {
            var resolver = new LLMModelMetadataResolver(new LLMOptions { ModelMetadataFiles = [path] });

            var metadata = resolver.Resolve("openai", "mine");

            Assert.Equal("my-model", metadata.Id);
            Assert.Equal(999, metadata.ContextWindowTokens);
            Assert.Equal(111, metadata.MaxOutputTokens);
            Assert.Equal(0.5m, metadata.Pricing!.InputPer1MTokens);
            Assert.False(metadata.Capabilities.SupportsTemperature);
            Assert.False(metadata.Capabilities.SupportsReasoningEffort);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EstimateCost_UsesBuiltinPricing()
    {
        var cost = ModelMetadataCatalog.EstimateCost("gpt-4o-mini", inputTokens: 500_000, outputTokens: 100_000);

        Assert.Equal(0.135m, cost);
    }

    [Fact]
    public void EstimateCost_ResolvesAliases()
    {
        var direct = ModelMetadataCatalog.EstimateCost("gpt-4o", inputTokens: 1_000_000);
        var alias = ModelMetadataCatalog.EstimateCost("gpt4o", inputTokens: 1_000_000);

        Assert.NotNull(direct);
        Assert.Equal(direct, alias);
    }

    [Fact]
    public void EstimateCost_ReturnsNullForUnknownModelWithoutPricing()
    {
        var cost = ModelMetadataCatalog.EstimateCost("unknown-model", inputTokens: 1_000);

        Assert.Null(cost);
    }

    [Fact]
    public void EstimateCost_UsesUserOverridePricing()
    {
        var options = new LLMOptions();
        options.ModelOverrides["custom-priced-model"] = new LLMModelMetadata
        {
            Id = "custom-priced-model",
            Pricing = new ModelPricingMetadata
            {
                InputPer1MTokens = 3m,
                OutputPer1MTokens = 9m
            }
        };

        var cost = ModelMetadataCatalog.EstimateCost(
            "custom-priced-model",
            inputTokens: 1_000_000,
            outputTokens: 500_000,
            options: options,
            providerType: "openai");

        Assert.Equal(7.5m, cost);
    }

    [Fact]
    public void GetMissingRequiredMetadataFields_ReturnsPricingAndLimitsForUnknownModel()
    {
        var missing = ModelMetadataCatalog.GetMissingRequiredMetadataFields(
            new LLMOptions(),
            "openai",
            "vendor/new-model",
            out var metadata);

        Assert.Equal("new-model", metadata.Id);
        Assert.Contains("contextWindowTokens", missing);
        Assert.Contains("maxInputTokens", missing);
        Assert.Contains("maxOutputTokens", missing);
        Assert.Contains("pricing.inputPer1MTokens", missing);
        Assert.Contains("pricing.outputPer1MTokens", missing);
    }

    [Fact]
    public void HasCompleteRequiredMetadata_ReturnsTrueForCompleteOverride()
    {
        var options = new LLMOptions();
        options.ModelOverrides["custom-model"] = new LLMModelMetadata
        {
            Id = "custom-model",
            ContextWindowTokens = 32768,
            MaxInputTokens = 32768,
            MaxOutputTokens = 4096,
            Pricing = new ModelPricingMetadata
            {
                InputPer1MTokens = 0m,
                OutputPer1MTokens = 0m
            },
            Capabilities = new ModelCapabilityMetadata
            {
                SupportsTemperature = true,
                SupportsReasoningEffort = false,
                SupportsStructuredOutput = true,
                SupportsTools = true,
                SupportsJsonMode = true
            }
        };

        Assert.True(ModelMetadataCatalog.HasCompleteRequiredMetadata(options, "openai", "custom-model"));
    }
}



