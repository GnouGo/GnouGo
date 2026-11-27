using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Scripting;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ContinuationObjectViewTests
{
    private static readonly JsonObject Source = JsonNode.Parse("""
        {"type":"object","required":["endpoint"],"properties":{
          "endpoint":{"type":"string"},
          "capture":{"type":["object","null"],"properties":{
            "records":{"type":"array","items":{"type":"object","properties":{
              "href":{"type":"string","pattern":"^https://"},"position":{"type":"integer"}}}},
            "total":{"type":"integer"}},"required":["records"]}}}
        """)!.AsObject();

    private static TaskType Expected() => new()
    {
        Kind = "object", Fields = [new() { Name = "capture", Required = false, Type = new()
        {
            Kind = "object", Nullable = true, Fields = [new() { Name = "records", Type = new()
                { Kind = "array", Items = new() { Kind = "any" } } }]
        } }]
    };

    [Theory]
    [InlineData("""{"endpoint":"/"}""", "{}")]
    [InlineData("""{"endpoint":"/","capture":null}""", """{"capture":null}""")]
    [InlineData("""{"endpoint":"/","capture":{"records":[],"total":0}}""", """{"capture":{"records":[]}}""")]
    [InlineData("""{"endpoint":"/","capture":{"total":2,"records":[{"href":"https://h/?x=%2B","position":0},{"href":"https://h/?x=%2B","position":1}]}}""",
        """{"capture":{"records":[{"href":"https://h/?x=%2B","position":0},{"href":"https://h/?x=%2B","position":1}]}}""")]
    public void CopiesOnlyPresentFieldsAndKeepsOriginalArrayItems(string input, string expected)
    {
        var fields = Assert.IsType<JsonObject>(PlanningObjectView.SelectFields(Source, Expected(), new()));
        var schema = Assert.IsType<JsonObject>(PlanningObjectView.Contract(Source, fields));
        var source = JsonNode.Parse(input)!;
        var view = new JintSandbox().ExecuteMapping(PlanningObjectView.Mapping(fields), source, PlannerFixture.Ct);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), view));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(input), source));
        Assert.Empty(PlanningContractValidation.ValidateInstance(view, schema));
        Assert.Equal("^https://", schema["properties"]!["capture"]!["properties"]!["records"]!["items"]!
            ["properties"]!["href"]!["pattern"]!.GetValue<string>());
        Assert.True(PlanningObjectView.CopiesPath(fields, ["capture", "records", null, "href"]));
        Assert.False(PlanningObjectView.CopiesPath(fields, ["capture"]));
        Assert.False(PlanningObjectView.CopiesPath(fields, ["capture", "total"]));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("incompatible")]
    [InlineData("optional")]
    [InlineData("alternatives")]
    public void RefusesUnestablishedFieldContracts(string defect)
    {
        var source = Source.DeepClone().AsObject();
        var expected = Expected();
        var capture = expected.Fields.Single();
        switch (defect)
        {
            case "missing": capture.Name = "other"; break;
            case "incompatible": capture.Type.Fields.Single().Type.Kind = "integer"; break;
            case "optional": capture.Required = true; break;
            case "alternatives": source = new() { ["anyOf"] = new JsonArray(source, new JsonObject { ["type"] = "string" }) }; break;
        }
        Assert.Null(PlanningObjectView.SelectFields(source, expected, new()));
    }
}
