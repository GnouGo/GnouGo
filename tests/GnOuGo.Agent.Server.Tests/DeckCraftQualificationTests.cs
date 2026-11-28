using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class DeckCraftQualificationTests
{
    [Fact]
    public void CorrectionPassesGroupFindingsBeforeEditsAndExcludeUnrelatedEarlierMutations()
    {
        static string At(int minute) => new DateTimeOffset(2026, 1, 1, 12, minute, 0, TimeSpan.Zero).ToString("O");
        static JsonObject Edit(int minute) => new() { ["tool"] = "set_text", ["started"] = At(minute) };
        static JsonObject Finding(int minute) => new() { ["received"] = At(minute),
            ["response"] = new JsonObject { ["findings"] = new JsonArray((JsonNode)new JsonObject { ["problem"] = "clipped label" }) } };
        var edits = new[] { Edit(0), Edit(3), Edit(4), Edit(6), Edit(8) };
        Assert.Equal(0, DeckCraftQualification.CorrectionCycles(edits, []));
        Assert.Equal(2, DeckCraftQualification.CorrectionCycles(edits, [Finding(1), Finding(2), Finding(5)]));
        Assert.Equal(3, DeckCraftQualification.CorrectionCycles(edits, [Finding(1), Finding(5), Finding(7)]));
        var limit = new DeckCraftQualification.CorrectionLimit();
        limit.Observe(Finding(1));
        limit.Observe(Finding(2));
        limit.BeforeTool("set_text");
        limit.BeforeTool("set_text");
        limit.Observe(Finding(5));
        limit.BeforeTool("batch");
        limit.Observe(Finding(7));
        limit.BeforeTool("render_slide");
        Assert.Throws<GnOuGo.Flow.Core.Planning.PlanningConflictException>(() => limit.BeforeTool("set_text"));
    }
}
