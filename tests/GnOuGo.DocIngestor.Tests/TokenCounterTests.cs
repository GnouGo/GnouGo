using DocIngestor.Core.Tokenization;
using Xunit;

namespace DocIngestor.Tests;

public sealed class TokenCounterTests
{
    [Fact]
    public void DefaultTokenCounter_CountsTokens()
    {
        var counter = new DefaultTokenCounter();
        var count = counter.CountTokens("Hello, world! This is a test.");

        Assert.True(count > 0);
        Assert.True(count < 20); // reasonable range for a short sentence
    }

    [Fact]
    public void DefaultTokenCounter_EmptyString_ReturnsZero()
    {
        var counter = new DefaultTokenCounter();
        Assert.Equal(0, counter.CountTokens(""));
    }

    [Fact]
    public void DefaultTokenCounter_Null_ReturnsZero()
    {
        var counter = new DefaultTokenCounter();
        Assert.Equal(0, counter.CountTokens(null!));
    }

    [Fact]
    public void DefaultTokenCounter_LongText_TokenCountGrows()
    {
        var counter = new DefaultTokenCounter();

        var short1 = counter.CountTokens("Hello");
        var long1 = counter.CountTokens(string.Join(" ", Enumerable.Repeat("Hello world", 100)));

        Assert.True(long1 > short1);
    }

    [Fact]
    public void DefaultTokenCounter_IsDeterministic()
    {
        var counter = new DefaultTokenCounter();
        var text = "The quick brown fox jumps over the lazy dog.";

        var c1 = counter.CountTokens(text);
        var c2 = counter.CountTokens(text);

        Assert.Equal(c1, c2);
    }
}

