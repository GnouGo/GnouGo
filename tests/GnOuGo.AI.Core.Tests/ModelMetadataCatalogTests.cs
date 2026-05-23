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
    public void Resolve_UsesProviderQualifiedInlineOverridesBeforeGenericModelId()
    {
        var options = new LLMOptions();
        options.ModelOverrides["shared-model"] = new LLMModelMetadata
        {
            Id = "shared-model",
            Pricing = new ModelPricingMetadata { InputPer1MTokens = 1m, OutputPer1MTokens = 2m }
        };
        options.ModelOverrides["openai/shared-model"] = new LLMModelMetadata
        {
            Id = "shared-model",
            Pricing = new ModelPricingMetadata { InputPer1MTokens = 3m, OutputPer1MTokens = 4m }
        };
        options.ModelOverrides["copilot/shared-model"] = new LLMModelMetadata
        {
            Id = "shared-model",
            Pricing = new ModelPricingMetadata { InputPer1MTokens = 5m, OutputPer1MTokens = 6m }
        };

        var resolver = new LLMModelMetadataResolver(options);

        var openAi = resolver.Resolve("openai", "shared-model");
        var copilot = resolver.Resolve("copilot", "shared-model");

        Assert.Equal("shared-model", openAi.Id);
        Assert.Equal("openai", openAi.ProviderType);
        Assert.Equal(3m, openAi.Pricing!.InputPer1MTokens);
        Assert.Equal("shared-model", copilot.Id);
        Assert.Equal("copilot", copilot.ProviderType);
        Assert.Equal(5m, copilot.Pricing!.InputPer1MTokens);
        Assert.Equal(7m, ModelMetadataCatalog.EstimateCost("shared-model", 1_000_000, 1_000_000, options, "openai"));
        Assert.Equal(11m, ModelMetadataCatalog.EstimateCost("shared-model", 1_000_000, 1_000_000, options, "copilot"));
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
    public void Resolve_LoadsProviderQualifiedExternalMetadataFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-model-metadata-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
        {
          "models": {
            "openai/shared-model": {
              "contextWindowTokens": 111,
              "pricing": { "inputPer1MTokens": 0.5, "outputPer1MTokens": 1.5 }
            },
            "copilot/shared-model": {
              "contextWindowTokens": 222,
              "pricing": { "inputPer1MTokens": 2.5, "outputPer1MTokens": 3.5 }
            }
          }
        }
        """);

        try
        {
            var resolver = new LLMModelMetadataResolver(new LLMOptions { ModelMetadataFiles = [path] });

            var openAi = resolver.Resolve("openai", "shared-model");
            var copilot = resolver.Resolve("copilot", "shared-model");

            Assert.Equal("shared-model", openAi.Id);
            Assert.Equal("openai", openAi.ProviderType);
            Assert.Equal(111, openAi.ContextWindowTokens);
            Assert.Equal(0.5m, openAi.Pricing!.InputPer1MTokens);
            Assert.Equal("shared-model", copilot.Id);
            Assert.Equal("copilot", copilot.ProviderType);
            Assert.Equal(222, copilot.ContextWindowTokens);
            Assert.Equal(2.5m, copilot.Pricing!.InputPer1MTokens);
            Assert.Single(resolver.ListConfiguredMetadata("openai"), metadata => metadata.Id == "shared-model" && metadata.ProviderType == "openai");
            Assert.Single(resolver.ListConfiguredMetadata("copilot"), metadata => metadata.Id == "shared-model" && metadata.ProviderType == "copilot");
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
    public void Resolve_ReturnsBuiltinMetadataForGpt55()
    {
        var resolver = new LLMModelMetadataResolver(new LLMOptions());

        var metadata = resolver.Resolve("openai", "gpt-5.5");

        Assert.Equal("gpt-5.5", metadata.Id);
        Assert.Equal(1050000, metadata.ContextWindowTokens);
        Assert.Equal(128000, metadata.MaxOutputTokens);
        Assert.Equal(5.0m, metadata.Pricing!.InputPer1MTokens);
        Assert.Equal(30.0m, metadata.Pricing.OutputPer1MTokens);
        Assert.False(metadata.Capabilities.SupportsTemperature);
        Assert.True(metadata.Capabilities.SupportsReasoningEffort);
        Assert.Contains("temperature", metadata.Capabilities.UnsupportedRequestParameters!);
    }

    [Theory]
    [InlineData("gpt-5.5-mini", 15.0, 30.0)]
    [InlineData("gpt-5.5-nano", 3.75, 7.5)]
    [InlineData("openai/gpt-5.5-mini", 15.0, 30.0)]
    public void Resolve_ReturnsBuiltinMetadataForGpt55Variants(string model, decimal expectedInputPrice, decimal expectedOutputPrice)
    {
        var resolver = new LLMModelMetadataResolver(new LLMOptions());

        var metadata = resolver.Resolve("openai", model);

        Assert.EndsWith(model.Replace("openai/", string.Empty, StringComparison.Ordinal), metadata.Id, StringComparison.Ordinal);
        Assert.Equal(400000, metadata.ContextWindowTokens);
        Assert.Equal(128000, metadata.MaxOutputTokens);
        Assert.Equal(expectedInputPrice, metadata.Pricing!.InputPer1MTokens);
        Assert.Equal(expectedOutputPrice, metadata.Pricing.OutputPer1MTokens);
        Assert.False(metadata.Capabilities.SupportsTemperature);
        Assert.True(metadata.Capabilities.SupportsReasoningEffort);
        Assert.Contains("temperature", metadata.Capabilities.UnsupportedRequestParameters!);
    }

    [Fact]
    public void Resolve_ReturnsProviderSpecificBuiltinMetadataForDuplicateModelIds()
    {
        var resolver = new LLMModelMetadataResolver(new LLMOptions());

        var openAi = resolver.Resolve("openai", "gpt-4o");
        var copilot = resolver.Resolve("copilot", "gpt-4o");

        Assert.Equal("gpt-4o", openAi.Id);
        Assert.Equal("openai", openAi.ProviderType);
        Assert.Equal(128000, openAi.ContextWindowTokens);
        Assert.Equal(2.5m, openAi.Pricing!.InputPer1MTokens);

        Assert.Equal("gpt-4o", copilot.Id);
        Assert.Equal("copilot", copilot.ProviderType);
        Assert.Equal(64000, copilot.ContextWindowTokens);
        Assert.Null(copilot.Pricing);
    }

    [Fact]
    public void Resolve_NormalizesProviderAliasesForBuiltinMetadata()
    {
        var resolver = new LLMModelMetadataResolver(new LLMOptions());

        var metadata = resolver.Resolve("anthropic", "claude-sonnet-4-20250514");

        Assert.Equal("claude-sonnet-4-20250514", metadata.Id);
        Assert.Equal("anthropic", metadata.ProviderType);
        Assert.Equal(3.0m, metadata.Pricing!.InputPer1MTokens);
    }

    [Fact]
    public void Resolve_DoesNotTreatNonProviderSlashPrefixAsVendorPrefix()
    {
        var resolver = new LLMModelMetadataResolver(new LLMOptions());

        var metadata = resolver.Resolve("openai", "1024-x-1024/dall-e-2");

        Assert.Equal("1024-x-1024/dall-e-2", metadata.Id);
        Assert.Equal("openai", metadata.ProviderType);
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

        Assert.Equal("vendor/new-model", metadata.Id);
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



