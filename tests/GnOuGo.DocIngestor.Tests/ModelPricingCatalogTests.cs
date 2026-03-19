using GnOuGo.AI.Core.Telemetry;
using Xunit;

namespace DocIngestor.Tests;

public sealed class ModelPricingCatalogTests
{
    // ── TryGetPricing ────────────────────────────────────────────────

    [Fact]
    public void TryGetPricing_KnownModel_ReturnsTrue()
    {
        Assert.True(ModelPricingCatalog.TryGetPricing("text-embedding-3-large", out var pricing));
        Assert.Equal(0.13m, pricing.InputPer1MTokens);
        Assert.Equal(0.0m, pricing.OutputPer1MTokens);
    }

    [Fact]
    public void TryGetPricing_CaseInsensitive()
    {
        Assert.True(ModelPricingCatalog.TryGetPricing("TEXT-EMBEDDING-3-LARGE", out var pricing));
        Assert.Equal(0.13m, pricing.InputPer1MTokens);
    }

    [Fact]
    public void TryGetPricing_UnknownModel_ReturnsFalse()
    {
        Assert.False(ModelPricingCatalog.TryGetPricing("unknown-model-xyz", out _));
    }

    [Fact]
    public void TryGetPricing_Alias_ResolvesToCanonical()
    {
        // "embedding-3-large" is an alias for "text-embedding-3-large"
        Assert.True(ModelPricingCatalog.TryGetPricing("embedding-3-large", out var pricing));
        Assert.Equal(0.13m, pricing.InputPer1MTokens);
    }

    [Fact]
    public void TryGetPricing_Alias_CaseInsensitive()
    {
        Assert.True(ModelPricingCatalog.TryGetPricing("GPT4O", out var pricing));
        Assert.True(pricing.InputPer1MTokens > 0);
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4o-mini")]
    [InlineData("text-embedding-3-small")]
    [InlineData("o1")]
    [InlineData("o3-mini")]
    [InlineData("claude-3-5-sonnet-20241022")]
    [InlineData("deepseek-chat")]
    public void TryGetPricing_KnownModels_AllHavePricing(string model)
    {
        Assert.True(ModelPricingCatalog.TryGetPricing(model, out var pricing));
        Assert.True(pricing.InputPer1MTokens >= 0);
    }

    // ── EstimateCost ─────────────────────────────────────────────────

    [Fact]
    public void EstimateCost_EmbeddingModel_InputOnly()
    {
        // text-embedding-3-large: $0.13 per 1M input tokens
        var cost = ModelPricingCatalog.EstimateCost("text-embedding-3-large", inputTokens: 1_000_000);
        Assert.NotNull(cost);
        Assert.Equal(0.13m, cost.Value);
    }

    [Fact]
    public void EstimateCost_ChatModel_InputAndOutput()
    {
        // gpt-4o-mini: $0.15 input, $0.60 output per 1M tokens
        var cost = ModelPricingCatalog.EstimateCost("gpt-4o-mini", inputTokens: 500_000, outputTokens: 100_000);
        Assert.NotNull(cost);
        // 0.5M * 0.15 / 1M + 0.1M * 0.60 / 1M = 0.075 + 0.06 = 0.135
        Assert.Equal(0.135m, cost.Value);
    }

    [Fact]
    public void EstimateCost_SmallTokenCount()
    {
        // 1000 tokens of text-embedding-3-large: 1000/1M * 0.13 = 0.00013
        var cost = ModelPricingCatalog.EstimateCost("text-embedding-3-large", inputTokens: 1000);
        Assert.NotNull(cost);
        Assert.Equal(0.00013m, cost.Value);
    }

    [Fact]
    public void EstimateCost_ZeroTokens_ReturnsZero()
    {
        var cost = ModelPricingCatalog.EstimateCost("gpt-4o", inputTokens: 0, outputTokens: 0);
        Assert.NotNull(cost);
        Assert.Equal(0m, cost.Value);
    }

    [Fact]
    public void EstimateCost_UnknownModel_ReturnsNull()
    {
        var cost = ModelPricingCatalog.EstimateCost("unknown-model", inputTokens: 1000);
        Assert.Null(cost);
    }

    [Fact]
    public void EstimateCost_ViaAlias_Works()
    {
        // "gpt4o" is an alias for "gpt-4o"
        var direct = ModelPricingCatalog.EstimateCost("gpt-4o", inputTokens: 1_000_000);
        var alias = ModelPricingCatalog.EstimateCost("gpt4o", inputTokens: 1_000_000);

        Assert.NotNull(direct);
        Assert.NotNull(alias);
        Assert.Equal(direct, alias);
    }

    [Fact]
    public void EstimateCost_OllamaLocalModels_NotInCatalog()
    {
        // Ollama local models (llama3.2, nomic-embed-text) are not in the catalog
        // so they return null (cost = 0 / free)
        var cost = ModelPricingCatalog.EstimateCost("llama3.2", inputTokens: 1_000_000);
        Assert.Null(cost);
    }

    // ── ModelPricing record ──────────────────────────────────────────

    [Fact]
    public void ModelPricing_RecordEquality()
    {
        var a = new ModelPricing(0.13m, 0.0m);
        var b = new ModelPricing(0.13m, 0.0m);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ModelPricing_RecordInequality()
    {
        var a = new ModelPricing(0.13m, 0.0m);
        var b = new ModelPricing(0.02m, 0.0m);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ModelPricing_DefaultIsZero()
    {
        var p = default(ModelPricing);
        Assert.Equal(0m, p.InputPer1MTokens);
        Assert.Equal(0m, p.OutputPer1MTokens);
    }
}

