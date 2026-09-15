namespace GnOuGo.AI.Core.Tests;

public sealed class DeclaredModelCapabilityTests
{
    [Fact]
    public void BuiltinEntriesAndDeclaredAliasesUseTheSameCapabilities()
    {
        var resolver = new LLMModelMetadataResolver();
        Assert.True(resolver.ResolveDeclaredCapabilities("openai", "gpt-4o-mini")!.SupportsStructuredOutput);
        Assert.True(resolver.ResolveDeclaredCapabilities("openai", "gpt4o-mini")!.SupportsStructuredOutput);
        Assert.True(resolver.ResolveDeclaredCapabilities(null, "openai/gpt-4o-mini")!.SupportsStructuredOutput);
    }

    [Theory]
    [InlineData("openai", "gpt-4o-mni")]
    [InlineData("unconfigured", "gpt-4o-mini")]
    [InlineData("ollama", "openai/gpt-4o-mini")]
    [InlineData("unconfigured", "unrecognized-model")]
    public void FuzzyAndForeignProviderEntriesCannotProveCapabilities(string provider, string model)
        => Assert.Null(new LLMModelMetadataResolver().ResolveDeclaredCapabilities(provider, model));

    [Fact]
    public void PartialExactEntriesRemainUnknownEvenAfterTheEditorResolvesDefaults()
    {
        var options = new LLMOptions { ModelOverrides = { ["openai/gpt-custom"] = new() { Id = "gpt-custom", Capabilities = new() { SupportsTemperature = false } } } };
        var resolver = new LLMModelMetadataResolver(options);
        Assert.Equal(LLMModelMetadataMatchKind.Exact, resolver.ResolveWithDetails("openai", "gpt-custom").MatchKind);
        var declared = resolver.ResolveDeclaredCapabilities("openai", "gpt-custom")!;
        Assert.False(declared.SupportsTemperature);
        Assert.Null(declared.SupportsStructuredOutput);
        Assert.Null(declared.SupportsReasoningEffort);
        Assert.Null(declared.SupportedReasoningEfforts);
    }

    [Fact]
    public void SavedOverridesWinOverFilesAndBuiltinValuesIncludingFalseAndEmptyLists()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {"models":{"openai/o4-mini":{"capabilities":{"supportsReasoningEffort":true,"supportedReasoningEfforts":["high"],"supportsStructuredOutput":true}}},
                 "aliases":{"openai/reviewed":"openai/o4-mini"}}
                """);
            var options = new LLMOptions { ModelMetadataFiles = [path], ModelOverrides =
            {
                ["o4-mini"] = new() { Capabilities = new() { SupportsReasoningEffort = false, SupportedReasoningEfforts = [], SupportsStructuredOutput = false } }
            } };
            var resolver = new LLMModelMetadataResolver(options);
            foreach (var model in new[] { "o4-mini", "reviewed" })
            {
                var declared = resolver.ResolveDeclaredCapabilities("openai", model)!;
                Assert.False(declared.SupportsReasoningEffort);
                Assert.False(declared.SupportsStructuredOutput);
                Assert.Empty(declared.SupportedReasoningEfforts!);
                Assert.False(resolver.Resolve("openai", model).Capabilities.SupportsStructuredOutput);
                declared.SupportedReasoningEfforts!.Add("mutated-result");
                Assert.Empty(resolver.ResolveDeclaredCapabilities("openai", model)!.SupportedReasoningEfforts!);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ProviderQualifiedOverridesRemainSeparated()
    {
        var options = new LLMOptions { ModelOverrides =
        {
            ["openai/shared"] = new() { Capabilities = new() { SupportsStructuredOutput = true } },
            ["ollama/shared"] = new() { Capabilities = new() { SupportsStructuredOutput = false } },
            ["owned"] = new() { ProviderType = "openai", Capabilities = new() { SupportsStructuredOutput = true } }
        } };
        var resolver = new LLMModelMetadataResolver(options);
        Assert.True(resolver.ResolveDeclaredCapabilities("openai", "shared")!.SupportsStructuredOutput);
        Assert.False(resolver.ResolveDeclaredCapabilities("ollama", "shared")!.SupportsStructuredOutput);
        Assert.Null(resolver.ResolveDeclaredCapabilities("ollama", "owned"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid json")]
    [InlineData("[]")]
    [InlineData("{\"models\":{\"o4-mini\":42}}")]
    [InlineData("{\"models\":42}")]
    [InlineData("{\"models\":{\"o4-mini\":{\"capabilities\":false}}}")]
    [InlineData("{\"models\":{\"o4-mini\":{\"capabilities\":{\"supportedReasoningEfforts\":\"high\"}}}}")]
    public void UnreadableConfiguredFilesStopProofWithoutChangingEditorResolution(string? content)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            if (content is not null) File.WriteAllText(path, content);
            var resolver = new LLMModelMetadataResolver(new() { ModelMetadataFiles = [path] });
            Assert.NotNull(resolver.Resolve("openai", "o4-mini"));
            var error = Assert.Throws<InvalidOperationException>(() => resolver.ResolveDeclaredCapabilities("openai", "o4-mini"));
            Assert.DoesNotContain(path, error.Message);
        }
        finally { File.Delete(path); }
    }
}
