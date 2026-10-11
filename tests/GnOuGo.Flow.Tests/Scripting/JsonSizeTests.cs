using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Scripting;

public sealed class JsonSizeTests(ITestOutputHelper output)
{
    [Fact]
    public void AccountingMatchesDefaultJsonBytesIncludingEveryUnicodeCodeUnit()
    {
        var chars = new string(Enumerable.Range(0, char.MaxValue + 1).Select(i => (char)i).ToArray());
        JsonNode?[] values = [null, new JsonObject(), new JsonArray(), JsonNode.Parse("[1.2300,-0,1e+25,null,true,false]"),
            new JsonObject { ["é\"\\\r\n"] = chars, ["child"] = new JsonArray("😀", "\ud800", "\udc00", "${literal}", "</script>\t") },
            JsonValue.Create(123.4500m), JsonValue.Create(1e100), JsonNode.Parse("{\"value\":\"\\u0061\"}")];
        foreach (var value in values)
            Assert.Equal(Encoding.UTF8.GetByteCount(value?.ToJsonString() ?? "null"), JintSandbox.JsonBytes(value, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RetainedObservationCountingUsesBoundedScratchMemory()
    {
        var value = new JsonObject { ["records"] = new JsonArray(Enumerable.Range(0, 1501).Select(i => (JsonNode)new JsonObject
            { ["text"] = new string('x', 780), ["id"] = "reference-" + i, ["number"] = JsonNode.Parse("1.2300"), ["absent"] = null }).ToArray()) };
        JintSandbox.JsonBytes(value, TestContext.Current.CancellationToken); // warm code paths, not a value cache
        var before = GC.GetAllocatedBytesForCurrentThread();
        var baseline = Encoding.UTF8.GetByteCount(value.ToJsonString());
        var originalAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var measured = JintSandbox.JsonBytes(value, TestContext.Current.CancellationToken);
        var countedAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(baseline, measured);
        Assert.True(countedAllocation < originalAllocation / 2, $"original={originalAllocation}; counted={countedAllocation}");
        output.WriteLine($"charged_bytes={measured}; old_allocation={originalAllocation}; bounded_allocation={countedAllocation}");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => JintSandbox.JsonBytes(value, cancellation.Token));
    }
}
