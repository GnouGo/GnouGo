using System.Runtime.CompilerServices;
using GnOuGo.AI.Core;

namespace GnOuGo.AI.Local.Tests;

public sealed class NativeInferenceCompletionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task FragmentsPreserveStructuredOutputThinkingAndToolsWithoutInventedUsage()
    {
        var count = 0;
        var result = await LocalInferenceCompletion.ReadAsync(Chunks(["<think>hidden", "</think>{\"value\":", "\"café\"}"], Ct), _ => "stop", () => count++, Ct);
        Assert.Equal(3, count);
        var text = LlamaSharpLocalLLMRuntime.StripThinking(result.Text);
        Assert.Equal("café", LlamaSharpLocalLLMRuntime.TryParseJson(text)!["value"]!.GetValue<string>());
        var tools = await LocalInferenceCompletion.ReadAsync(Chunks(["<tool_call>{\"name\":\"lookup\",", "\"arguments\":{\"id\":1}}</tool_call>"], Ct), _ => "stop", null, Ct);
        Assert.Equal(1, Assert.Single(LlamaSharpLocalLLMRuntime.ParseToolCalls(tools.Text)!).Arguments!["id"]!.GetValue<int>());
    }
    [Theory]
    [InlineData("stop")]
    [InlineData("length")]
    public async Task NativeStopReasonIsRetained(string reason)
    {
        var result = await LocalInferenceCompletion.ReadAsync(Chunks(["{}"], Ct), _ => reason, null, Ct);
        Assert.Equal(reason, result.Reason);
    }
    [Fact]
    public async Task CleanIteratorExitAfterCancellationCannotReturnPartialOutput()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        async IAsyncEnumerable<string> Native([EnumeratorCancellation] CancellationToken token)
        { yield return "{\"value\":"; await Task.Yield(); cancel.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalInferenceCompletion.ReadAsync(Native(cancel.Token), _ => "stop", null, cancel.Token));
    }
    [Fact]
    public async Task MidStreamErrorOrMissingTerminationNeverReturnsACompletion()
    {
        async IAsyncEnumerable<string> Broken([EnumeratorCancellation] CancellationToken token)
        { yield return "{\"value\":"; await Task.Yield(); throw new IOException("inference interrupted"); }
        await Assert.ThrowsAsync<IOException>(() => LocalInferenceCompletion.ReadAsync(Broken(Ct), _ => "stop", null, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalInferenceCompletion.ReadAsync(Chunks(["{}"], Ct), _ => null, null, Ct));
    }
    private static async IAsyncEnumerable<string> Chunks(string[] values, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var value in values) { ct.ThrowIfCancellationRequested(); yield return value; await Task.Yield(); }
    }
}
