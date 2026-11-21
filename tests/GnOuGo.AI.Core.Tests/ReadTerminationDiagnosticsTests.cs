using System.Net.Sockets;
using System.Text.Json;

namespace GnOuGo.AI.Core.Tests;

public sealed class ReadTerminationDiagnosticsTests
{
    [Fact]
    public void LegacyAttemptSerializationKeepsExactBytesAndAddsNoGuessedDiagnostics()
    {
        const string legacy = """{"Fingerprint":"old","Attempts":[{"Id":"old-attempt","Status":null,"Body":null,"ContentType":null,"RetryAfter":null,"Failure":"transport","NotBefore":null}],"DelayMilliseconds":0}""";
        var state = JsonSerializer.Deserialize(legacy, LLMHttpRetryJsonContext.Default.LLMHttpRetryState)!;
        Assert.Null(state.Attempts[0].ReadTermination);
        Assert.Null(state.Attempts[0].CancellationSource);
        Assert.Null(state.Attempts[0].ResponseHttpVersion);
        Assert.Equal(legacy, JsonSerializer.Serialize(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState));
    }

    [Fact]
    public void NetworkEvidenceKeepsOnlyFrameworkCodesAndSurvivesSerialization()
    {
        var exception = new HttpIOException(HttpRequestError.ResponseEnded, "SECRET https://secret.invalid/?token=SECRET",
            new IOException("SECRET", new SocketException((int)SocketError.ConnectionReset)));
        var observed = LLMHttpReadTermination.FromException(exception, DateTimeOffset.UtcNow);
        Assert.Equal("exception", observed.Outcome);
        Assert.Equal("http_io", observed.ErrorType);
        Assert.Equal("ResponseEnded", observed.HttpError);
        Assert.Equal("ConnectionReset", observed.SocketError);
        var state = new LLMHttpRetryState { Attempts = [new() { Id = "new", ReadTermination = observed }] };
        var bytes = JsonSerializer.Serialize(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState);
        Assert.DoesNotContain("SECRET", bytes);
        var restored = JsonSerializer.Deserialize(bytes, LLMHttpRetryJsonContext.Default.LLMHttpRetryState)!;
        Assert.Equal(observed.SocketError, restored.Attempts[0].ReadTermination!.SocketError);
    }

    [Fact]
    public void FirstTerminalObservationCannotBeRewrittenByLateReaderCancellation()
    {
        var attempt = new LLMHttpAttempt();
        var initial = LLMHttpReadTermination.FromException(new OperationCanceledException(), DateTimeOffset.UtcNow, "native_reader");
        attempt.ObserveReadTermination(initial);
        Parallel.For(0, 32, _ => attempt.ObserveReadTermination(new() { Observer = "response_stream", Outcome = "eof", AtUtc = DateTimeOffset.UtcNow }));
        Assert.Same(initial, attempt.ReadTermination);
    }

    [Fact]
    public async Task JournalWriteFailureIsNotMisreportedAsAResponseReadFailure()
    {
        var attempt = new LLMHttpAttempt();
        await using var bytes = new MemoryStream("received bytes"u8.ToArray());
        var failure = new IOException("controlled storage failure");
        await using var stream = new CapturedResponseStream(bytes, attempt, _ => throw failure, () => DateTimeOffset.UtcNow);
        var buffer = new byte[100];
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask()));
        Assert.Null(attempt.ReadTermination);
        Assert.NotNull(attempt.FirstByteAtUtc);
    }
}
