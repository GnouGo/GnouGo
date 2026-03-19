using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Models;
using DocIngestor.Core.Reranking;
using GnOuGo.AI.Core;
using Moq;
using Xunit;

namespace DocIngestor.Tests;

public sealed class RerankerTests
{
    private static VectorSearchResult MakeResult(string chunkId, string text, double score)
    {
        var chunk = new TextChunk(chunkId, "d1", "s1", 0, text, new Dictionary<string, string>());
        var embedded = new EmbeddedChunk(chunk, "hash", new float[] { 1, 0, 0 });
        return new VectorSearchResult(score, embedded);
    }

    // ── Bm25Reranker ─────────────────────────────────────────────────

    [Fact]
    public async Task Bm25_RanksRelevantDocHigher()
    {
        var reranker = new Bm25Reranker();

        var candidates = new[]
        {
            MakeResult("c1", "The dog ran in the park far away", 0.0),
            MakeResult("c2", "The cat sat on the cat mat with cat", 0.0),
        };

        var results = await reranker.RerankAsync("cat", candidates,
            new RerankerOptions(TopK: 10, VectorWeight: 0.0, RerankWeight: 1.0));

        Assert.Equal(2, results.Count);
        // "The cat sat on the cat mat with cat" has 3 "cat" mentions → ranked first
        Assert.Equal("c2", results[0].Chunk.Chunk.ChunkId);
    }

    [Fact]
    public async Task Bm25_EmptyCandidates_ReturnsEmpty()
    {
        var reranker = new Bm25Reranker();
        var results = await reranker.RerankAsync("query", Array.Empty<VectorSearchResult>(), new RerankerOptions());
        Assert.Empty(results);
    }

    [Fact]
    public async Task Bm25_EmptyQuery_ReturnsCandidatesUnchanged()
    {
        var reranker = new Bm25Reranker();
        var candidates = new[] { MakeResult("c1", "some text", 0.8) };

        var results = await reranker.RerankAsync("", candidates, new RerankerOptions());

        Assert.Single(results);
    }

    [Fact]
    public async Task Bm25_RespectsTopK()
    {
        var reranker = new Bm25Reranker();
        var candidates = Enumerable.Range(0, 20)
            .Select(i => MakeResult($"c{i}", $"word{i} text content", 0.5))
            .ToArray();

        var results = await reranker.RerankAsync("word5", candidates, new RerankerOptions(TopK: 3));

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task Bm25_BlendsVectorAndBm25Scores()
    {
        var reranker = new Bm25Reranker();

        var candidates = new[]
        {
            MakeResult("c1", "irrelevant gibberish nothing", 0.95),  // high vector, low BM25
            MakeResult("c2", "cat cat cat cat", 0.1),                // low vector, high BM25
        };

        // Equal weights
        var results = await reranker.RerankAsync("cat", candidates,
            new RerankerOptions(TopK: 10, VectorWeight: 0.5, RerankWeight: 0.5));

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Bm25_Name_Is_bm25()
    {
        Assert.Equal("bm25", new Bm25Reranker().Name);
    }

    // ── CrossEncoderReranker ─────────────────────────────────────────

    [Fact]
    public async Task CrossEncoder_UsesScorer_ToRerank()
    {
        var scorer = new Mock<IChatScorer>(MockBehavior.Strict);
        scorer.SetupGet(s => s.Name).Returns("mock");
        // "cat text" gets a high score, "dog text" gets a low score
        scorer.Setup(s => s.ScoreAsync("cat", It.Is<string>(p => p.Contains("cat")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(9.0);
        scorer.Setup(s => s.ScoreAsync("cat", It.Is<string>(p => p.Contains("dog")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2.0);

        var reranker = new CrossEncoderReranker(scorer.Object, maxConcurrency: 2);

        var candidates = new[]
        {
            MakeResult("c1", "dog text here", 0.8),
            MakeResult("c2", "cat text here", 0.3),
        };

        var results = await reranker.RerankAsync("cat", candidates, new RerankerOptions(TopK: 10));

        Assert.Equal(2, results.Count);
        Assert.Equal("c2", results[0].Chunk.Chunk.ChunkId); // cat should be ranked first
    }

    [Fact]
    public async Task CrossEncoder_EmptyCandidates_ReturnsEmpty()
    {
        var scorer = new Mock<IChatScorer>();
        scorer.SetupGet(s => s.Name).Returns("mock");

        var reranker = new CrossEncoderReranker(scorer.Object);
        var results = await reranker.RerankAsync("q", Array.Empty<VectorSearchResult>(), new RerankerOptions());

        Assert.Empty(results);
    }

    [Fact]
    public void CrossEncoder_DefaultName_IncludesScorerName()
    {
        var scorer = new Mock<IChatScorer>();
        scorer.SetupGet(s => s.Name).Returns("openai");

        var reranker = new CrossEncoderReranker(scorer.Object);
        Assert.Equal("cross-encoder-openai", reranker.Name);
    }

    [Fact]
    public void CrossEncoder_CustomName()
    {
        var scorer = new Mock<IChatScorer>();
        scorer.SetupGet(s => s.Name).Returns("openai");

        var reranker = new CrossEncoderReranker(scorer.Object, name: "my-reranker");
        Assert.Equal("my-reranker", reranker.Name);
    }

    // ── RerankerRegistry ─────────────────────────────────────────────

    [Fact]
    public void Registry_Get_ReturnsRegisteredReranker()
    {
        var bm25 = new Bm25Reranker();
        var registry = new RerankerRegistry(new IReranker[] { bm25 });

        Assert.Same(bm25, registry.Get("bm25"));
    }

    [Fact]
    public void Registry_Get_IsCaseInsensitive()
    {
        var bm25 = new Bm25Reranker();
        var registry = new RerankerRegistry(new IReranker[] { bm25 });

        Assert.Same(bm25, registry.Get("BM25"));
        Assert.Same(bm25, registry.Get("Bm25"));
    }

    [Fact]
    public void Registry_Get_ThrowsForUnknown()
    {
        var registry = new RerankerRegistry(new IReranker[] { new Bm25Reranker() });
        Assert.Throws<KeyNotFoundException>(() => registry.Get("unknown"));
    }

    [Fact]
    public void Registry_Available_ListsNames()
    {
        var bm25 = new Bm25Reranker();
        var registry = new RerankerRegistry(new IReranker[] { bm25 });

        Assert.Contains("bm25", registry.Available);
    }

    // ── ScoreParser.Parse ──────────────────────────────────────

    [Theory]
    [InlineData("7", 7.0)]
    [InlineData("10", 10.0)]
    [InlineData("0", 0.0)]
    [InlineData("  8  ", 8.0)]
    public void ParseScore_ValidNumber(string input, double expected)
    {
        Assert.Equal(expected, ScoreParser.Parse(input));
    }

    [Theory]
    [InlineData("Score: 7", 7.0)]
    [InlineData("The relevance is 9.", 9.0)]
    public void ParseScore_ExtractsDigitFromText(string input, double expected)
    {
        Assert.Equal(expected, ScoreParser.Parse(input));
    }

    [Fact]
    public void ParseScore_NoDigit_ReturnsZero()
    {
        Assert.Equal(0.0, ScoreParser.Parse("no digits here"));
    }

    [Fact]
    public void ParseScore_ClampsAbove10()
    {
        Assert.Equal(10.0, ScoreParser.Parse("15"));
    }

    [Fact]
    public void ParseScore_ClampsBelow0()
    {
        Assert.Equal(0.0, ScoreParser.Parse("-5"));
    }
}




