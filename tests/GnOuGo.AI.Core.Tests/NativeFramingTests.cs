using System.Text;

namespace GnOuGo.AI.Core.Tests;

public sealed class NativeFramingTests
{
    [Fact]
    public async Task MultilineCrLfCommentsAndKeepalivesPreserveExactData()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(": keepalive\r\nevent: message\r\nid: ignored\r\ndata: {\r\ndata: \"text\":\"café 😀\"}\r\n\r\ndata: [DONE]\r\n\r\n"));
        var events = new List<string>();
        await foreach (var data in SseParser.ReadDataAsync(input, TestContext.Current.CancellationToken)) events.Add(data);
        Assert.Equal(new[] { "{\n\"text\":\"café 😀\"}", "[DONE]" }, events);
    }
    [Theory]
    [InlineData("data: {\"partial\":true}")]
    [InlineData("data: malformed\n\n")]
    public async Task TruncatedOrMalformedEventsAreNotSilentlySkipped(string value)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(value));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var evt in SseParser.ReadEventsAsync(input, TestContext.Current.CancellationToken)) evt.Dispose();
        });
    }
    [Fact]
    public async Task FramesAreBoundedBeforeUnboundedAllocation()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("data: " + new string('x', SseParser.MaxFrameCharacters) + "\n\n"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in SseParser.ReadDataAsync(input, TestContext.Current.CancellationToken)) { }
        });
    }
}
