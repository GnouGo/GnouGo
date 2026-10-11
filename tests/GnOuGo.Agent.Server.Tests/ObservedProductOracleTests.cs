using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ObservedProductOracleTests
{
    [Theory]
    [InlineData("/arbitrary?q=red%2Fblue&n=1", false)]
    [InlineData("/jump?target=opaque", true)]
    public void CorrelatesOfferedNavigationAndProducerRedirectsWithoutPathRules(string path, bool redirect)
    {
        var requested = "https://fixture.invalid" + path;
        var final = redirect ? "https://destination.invalid/a/completely/different/path?x=1" : requested;
        var events = Events(requested, final);
        var result = LiveWorkflowOracles.VerifyObservedProducts(events, Rows(), 10);
        Assert.True(result["passed"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal(1, result["visit_count"]!.GetValue<int>());
    }

    [Fact]
    public void RepeatedUrlsRetainVisitOccurrencesAndWorkbookOrder()
    {
        var events = Events("https://fixture.invalid/item", "https://fixture.invalid/item");
        events.Add((JsonObject)events[1].DeepClone());
        var result = LiveWorkflowOracles.VerifyObservedProducts(events, [Rows()[0], Rows()[1], Rows()[1]], 10);
        Assert.True(result["passed"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal(2, result["visit_count"]!.GetValue<int>());
        result = LiveWorkflowOracles.VerifyObservedProducts(events, Rows(), 10);
        Assert.False(result["passed"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("redirect")]
    [InlineData("conflicting_navigation")]
    [InlineData("incomplete")]
    [InlineData("fabricated")]
    [InlineData("rewritten")]
    [InlineData("joined")]
    [InlineData("absence")]
    [InlineData("partial_number")]
    [InlineData("interface_error")]
    public void RejectsUnverifiedAssociationsAndNonExactFields(string failure)
    {
        var events = Events("https://fixture.invalid/opaque", "https://destination.invalid/other"); var rows = Rows();
        switch (failure)
        {
            case "unknown": events[1]["arguments"]!["url"] = "https://unknown.invalid"; break;
            case "redirect": events[1]["result"]!.AsObject().Remove("acquisition"); break;
            case "conflicting_navigation": events[1]["result"]!["acquisition"]!["navigation"]!["url"] = "https://different.invalid"; break;
            case "incomplete": events[1]["result"]!["observationSnapshot"]!["captureTruncated"] = true; break;
            case "fabricated": rows[1][0] = "Invented title"; break;
            case "rewritten": rows[1][1] = "Exact Description"; break;
            case "joined": rows[1][1] = "Exact description\nReport a problem"; break;
            case "absence": rows[1][2] = ""; break;
            case "partial_number": rows[1][2] = "2,00 €"; break;
            case "interface_error": rows[1][3] = "Report a problem"; break;
        }
        Assert.False(LiveWorkflowOracles.VerifyObservedProducts(events, rows, 10)["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void FieldsCannotBeBorrowedFromAnotherVisitedProduct()
    {
        var events = Events("https://fixture.invalid/one", "https://fixture.invalid/one");
        events[0]["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray().Add(new JsonObject { ["href"] = "https://fixture.invalid/two" });
        events[0]["result"]!["observationSnapshot"]!["recordCount"] = 2;
        var second = Snapshot("https://fixture.invalid/two", "https://fixture.invalid/two", new JsonArray(
            (JsonNode)new JsonObject { ["text"] = "Another title" }, new JsonObject { ["text"] = "Another description" }, new JsonObject { ["text"] = "10,00 €" }));
        events.Add(second);
        string[][] rows = [Rows()[0], Rows()[1], ["Another title", "Another description", "10,00 €", ""]];
        Assert.True(LiveWorkflowOracles.VerifyObservedProducts(events, rows, 10)["passed"]!.GetValue<bool>());
        (rows[1][1], rows[2][1]) = (rows[2][1], rows[1][1]);
        Assert.False(LiveWorkflowOracles.VerifyObservedProducts(events, rows, 10)["passed"]!.GetValue<bool>());
    }

    internal static List<JsonObject> Events(string requested, string final) =>
    [
        Snapshot("https://fixture.invalid/search", "https://fixture.invalid/search", new JsonArray((JsonNode)new JsonObject { ["href"] = requested, ["reference"] = "opaque-id", ["text"] = "Offered title" })),
        Snapshot(requested, final, new JsonArray((JsonNode)new JsonObject { ["text"] = "Exact title" },
            new JsonObject { ["text"] = "Exact description" }, new JsonObject { ["text"] = "Price: 12,00 €" },
            new JsonObject { ["text"] = "Report a problem", ["kind"] = "button" }))
    ];
    internal static string[][] Rows() => [["name", "description", "price", "error"], ["Exact title", "Exact description", "12,00 €", ""]];
    private static JsonObject Snapshot(string requested, string final, JsonArray records) => new()
    {
        ["tool"] = "browser_get_content", ["error"] = false, ["arguments"] = new JsonObject { ["url"] = requested },
        ["result"] = new JsonObject { ["url"] = final, ["success"] = true, ["truncated"] = false,
            ["acquisition"] = new JsonObject { ["navigation"] = new JsonObject { ["url"] = final } },
            ["observationSnapshot"] = new JsonObject { ["id"] = "snapshot", ["captureTruncated"] = false, ["manifestTruncated"] = false,
                ["recordCount"] = records.Count, ["pages"] = new JsonArray((JsonNode)new JsonObject { ["id"] = "snapshot", ["captureTruncated"] = false, ["nextCursor"] = null, ["records"] = records }) }
        }
    };
}
