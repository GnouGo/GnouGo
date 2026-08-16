using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GnOuGo.AI.Core;

namespace GnOuGo.AI.Local.Tests;

public sealed class LocalModelManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GnOuGo.AI.Local.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallAsync_DownloadsVerifiesAndBecomesIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = "tiny-model"u8.ToArray();
        var handler = new RangeHandler(bytes);
        var manager = CreateManager(handler, Entry(bytes));
        var progress = new List<LocalModelProgress>();

        var installed = await manager.InstallAsync("test:tiny", new InlineProgress<LocalModelProgress>(progress.Add), ct);
        var installedAgain = await manager.InstallAsync("test:tiny", ct: ct);

        Assert.Equal(LocalModelStatus.Installed, installed.Status);
        Assert.Equal(LocalModelStatus.Installed, installedAgain.Status);
        Assert.Single(handler.RequestedRanges);
        Assert.Equal(100, progress[^1].Percentage);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_root, "tiny.gguf"), ct));
    }

    [Fact]
    public async Task InstallAsync_ResumesPartialDownload()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = "tiny-model"u8.ToArray();
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(Path.Combine(_root, "tiny.gguf.partial"), bytes[..4], ct);
        var handler = new RangeHandler(bytes);
        var manager = CreateManager(handler, Entry(bytes));

        var installed = await manager.InstallAsync("test:tiny", ct: ct);

        Assert.Equal(LocalModelStatus.Installed, installed.Status);
        Assert.Equal(4, handler.RequestedRanges.Single());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_root, "tiny.gguf"), ct));
    }

    [Fact]
    public async Task InstallAsync_RejectsChecksumMismatchAndDeletesPartialFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = "bad-model"u8.ToArray();
        var entry = Entry(bytes) with { Sha256 = new string('0', 64) };
        var manager = CreateManager(new RangeHandler(bytes), entry);

        var error = await Assert.ThrowsAsync<LocalLLMException>(() => manager.InstallAsync("test:tiny", ct: ct));

        Assert.Equal(LocalLLMFailureKind.ModelUnavailable, error.Kind);
        Assert.False(File.Exists(Path.Combine(_root, "tiny.gguf.partial")));
    }

    [Fact]
    public async Task InstallAsync_ReplacesCorruptCompletedFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = "tiny-model"u8.ToArray();
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(Path.Combine(_root, "tiny.gguf"), new byte[bytes.Length], ct);
        var handler = new RangeHandler(bytes);
        var manager = CreateManager(handler, Entry(bytes));

        var installed = await manager.InstallAsync("test:tiny", ct: ct);

        Assert.Equal(LocalModelStatus.Installed, installed.Status);
        Assert.Single(handler.RequestedRanges);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_root, "tiny.gguf"), ct));
    }

    [Fact]
    public async Task InstallAsync_CancellationKeepsResumablePartialFile()
    {
        var bytes = new byte[384 * 1024];
        Random.Shared.NextBytes(bytes);
        var manager = CreateManager(new RangeHandler(bytes), Entry(bytes));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new InlineProgress<LocalModelProgress>(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.InstallAsync("test:tiny", progress, cancellation.Token));

        var partialPath = Path.Combine(_root, "tiny.gguf.partial");
        Assert.True(File.Exists(partialPath));
        Assert.InRange(new FileInfo(partialPath).Length, 1, bytes.Length - 1);
    }

    [Fact]
    public async Task InstallAsync_ConcurrentCallsShareSingleDownload()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = "tiny-model"u8.ToArray();
        var handler = new RangeHandler(bytes);
        var manager = CreateManager(handler, Entry(bytes));

        var results = await Task.WhenAll(
            manager.InstallAsync("test:tiny", ct: ct),
            manager.InstallAsync("test:tiny", ct: ct));

        Assert.All(results, result => Assert.Equal(LocalModelStatus.Installed, result.Status));
        Assert.Single(handler.RequestedRanges);
    }

    [Fact]
    public async Task InstallAsync_RejectsCatalogPathTraversal()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = "tiny-model"u8.ToArray();
        var manager = CreateManager(new RangeHandler(bytes), Entry(bytes) with { FileName = "../escape.gguf" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InstallAsync("test:tiny", ct: ct));
    }

    [Fact]
    public async Task RemoveAsync_UnloadsBeforeDeletingModelAndPartialFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = "tiny-model"u8.ToArray();
        var unloaded = false;
        var entry = Entry(bytes);
        var manager = new LocalModelManager(
            new HttpClient(new RangeHandler(bytes)),
            _root,
            [entry],
            (_, _) => { unloaded = true; return Task.CompletedTask; });
        await manager.InstallAsync(entry.Id, ct: ct);
        await File.WriteAllBytesAsync(Path.Combine(_root, "tiny.gguf.partial"), [1, 2], ct);

        var removed = await manager.RemoveAsync(entry.Id, ct);

        Assert.True(removed);
        Assert.True(unloaded);
        Assert.False(File.Exists(Path.Combine(_root, "tiny.gguf")));
        Assert.False(File.Exists(Path.Combine(_root, "tiny.gguf.partial")));
    }

    [Fact]
    public void ParseToolCalls_ParsesQwenHermesEnvelope()
    {
        var calls = LlamaSharpLocalLLMRuntime.ParseToolCalls(
            "<tool_call>{\"name\":\"mcp.call\",\"arguments\":{\"server\":\"Git\"}}</tool_call>");

        var call = Assert.Single(calls!);
        Assert.Equal("mcp.call", call.Name);
        Assert.Equal("Git", call.Arguments!["server"]!.GetValue<string>());
    }

    [Fact]
    public void TryParseJson_ParsesPlainAndFencedResponses()
    {
        Assert.Equal("ok", LlamaSharpLocalLLMRuntime.TryParseJson("{\"status\":\"ok\"}")!["status"]!.GetValue<string>());
        Assert.Equal(1, LlamaSharpLocalLLMRuntime.TryParseJson("```json\n{\"count\":1}\n```")!["count"]!.GetValue<int>());
        Assert.Null(LlamaSharpLocalLLMRuntime.TryParseJson("ordinary text"));
    }

    [Theory]
    [InlineData("<think>private reasoning</think>visible", "visible")]
    [InlineData("<think>truncated private reasoning", "")]
    [InlineData("private reasoning</think>visible", "visible")]
    public void StripThinking_NeverReturnsReasoningContent(string response, string expected)
        => Assert.Equal(expected, LlamaSharpLocalLLMRuntime.StripThinking(response));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private LocalModelManager CreateManager(HttpMessageHandler handler, LocalModelCatalogEntry entry)
        => new(new HttpClient(handler), _root, [entry]);

    private static LocalModelCatalogEntry Entry(byte[] bytes)
        => new(
            "test:tiny",
            "Tiny test model",
            "tiny.gguf",
            new Uri("https://models.invalid/tiny.gguf"),
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)),
            "Apache-2.0",
            128);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RangeHandler(byte[] bytes) : HttpMessageHandler
    {
        public List<long?> RequestedRanges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var start = request.Headers.Range?.Ranges.Single().From;
            RequestedRanges.Add(start);
            var offset = checked((int)(start ?? 0));
            var response = new HttpResponseMessage(start.HasValue ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes[offset..])
            };
            response.Content.Headers.ContentLength = bytes.Length - offset;
            if (start.HasValue)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
            return Task.FromResult(response);
        }
    }
}
