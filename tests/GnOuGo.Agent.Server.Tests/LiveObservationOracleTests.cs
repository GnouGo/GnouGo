using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LiveObservationOracleTests
{
    [Theory]
    [InlineData("complete", true)]
    [InlineData("missing", false)]
    [InlineData("capture_truncated", false)]
    [InlineData("manifest_truncated", false)]
    [InlineData("mixed_snapshot", false)]
    [InlineData("wrong_count", false)]
    [InlineData("wrong_url", false)]
    [InlineData("wrong_next", false)]
    public void OnlyFullyConsumedMatchingSnapshotsProvideWorkbookEvidence(string variant, bool accepted)
    {
        var events = new List<JsonObject> { Read(new JsonObject { ["url"] = "https://fixture.invalid/item/1",
            ["content"] = "", ["truncated"] = false, ["observationManifest"] = new JsonObject
            { ["id"] = "snapshot", ["captureTruncated"] = variant == "capture_truncated", ["manifestTruncated"] = variant == "manifest_truncated",
                ["recordCount"] = 3, ["pages"] = new JsonArray(Enumerable.Range(0, 3).Select(i => (JsonNode)new JsonObject
                    { ["cursor"] = "snapshot:page:" + i, ["recordCount"] = 1 }).ToArray()) } }) };
        for (var i = 0; i < 3; i++) events.Add(Read(new JsonObject { ["url"] = variant == "wrong_url" && i == 1 ? "https://different.invalid" : "https://fixture.invalid/item/1",
            ["content"] = "", ["observation"] = new JsonObject { ["id"] = variant == "mixed_snapshot" && i == 1 ? "other" : "snapshot",
                ["captureTruncated"] = false, ["nextCursor"] = variant == "wrong_next" ? "unknown" : i < 2 ? "snapshot:page:" + (i + 1) : null,
                ["records"] = new JsonArray((JsonNode)new JsonObject { ["text"] = new[] { "Observed name", "Actual description", "12.99 EUR" }[i] }) } }, "snapshot:page:" + i));
        if (variant == "missing") events.RemoveAt(2);
        if (variant == "wrong_count") events[0]["result"]!["observationManifest"]!["recordCount"] = 4;
        var observations = LiveWorkflowOracles.CompleteBrowserObservations(events).ToArray();
        Assert.Equal(accepted ? 1 : 0, observations.Length);
        if (accepted) Assert.Equal("Observed name\nActual description\n12.99 EUR", observations[0]["content"]!.GetValue<string>());
    }

    [Fact]
    public void FlatTruncationDoesNotBecomeEvidenceAndEmptyCompleteSnapshotsRemainEmpty()
    {
        var events = new[] { Read(new JsonObject { ["url"] = "https://fixture.invalid", ["content"] = "partial", ["truncated"] = true }),
            Read(new JsonObject { ["url"] = "https://fixture.invalid", ["content"] = "", ["truncated"] = false }) };
        Assert.Equal("", Assert.Single(LiveWorkflowOracles.CompleteBrowserObservations(events))["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("complete", true)]
    [InlineData("empty", true)]
    [InlineData("missing", false)]
    [InlineData("capture", false)]
    [InlineData("manifest", false)]
    [InlineData("mixed", false)]
    [InlineData("continuation", false)]
    [InlineData("page_truncated", false)]
    public void CompleteAcquisitionRequiresAllPagesFromOneGeneration(string variant, bool accepted)
    {
        var pages = new JsonArray(Enumerable.Range(0, 3).Select(i => (JsonNode)new JsonObject
        {
            ["id"] = variant == "mixed" && i == 1 ? "old" : "current",
            ["captureTruncated"] = variant == "page_truncated" && i == 1,
            ["nextCursor"] = variant == "continuation" && i == 1 ? "unknown" : null,
            ["records"] = new JsonArray((JsonNode)new JsonObject { ["text"] = "Observed " + i })
        }).ToArray());
        if (variant == "missing") pages.RemoveAt(1);
        if (variant == "empty") pages.Clear();
        var result = Read(new JsonObject { ["url"] = "https://fixture.invalid/item", ["truncated"] = false,
            ["observationSnapshot"] = new JsonObject { ["id"] = "current", ["pages"] = pages,
                ["recordCount"] = variant == "empty" ? 0 : 3, ["captureTruncated"] = variant == "capture", ["manifestTruncated"] = variant == "manifest" } });
        var observations = LiveWorkflowOracles.CompleteBrowserObservations([result]).ToArray();
        Assert.Equal(accepted ? 1 : 0, observations.Length);
        if (accepted) Assert.Equal(variant == "empty" ? "" : "Observed 0\nObserved 1\nObserved 2", observations[0]["content"]!.ToString());
    }

    private static JsonObject Read(JsonObject result, string? cursor = null) => new()
    { ["tool"] = "browser_get_content", ["error"] = false, ["arguments"] = new JsonObject { ["cursor"] = cursor }, ["result"] = result };
}
