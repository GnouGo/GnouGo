using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FixedRepairFieldTests
{
    [Theory]
    [InlineData("project", "context")]
    [InlineData("repertoire", "contexte")]
    public void ParentFindingCannotNarrowRepairToAnAlreadyCorrectLockedBinding(string locked, string editable)
    {
        var graph = Graph(); graph.Workflows[0].Steps[0].Input = Obj((locked, Str("fixed")), (editable, Str("old")));
        var fixedValue = new JsonObject { ["kind"] = "binding", ["reference"] = "original-producer" };
        var values = new JsonObject { [locked] = fixedValue, [editable] = new JsonObject { ["kind"] = "string", ["text"] = "old" } };
        var unit = new PlanningConstructionUnit { Key = "unit", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["greeting"],
            Candidate = new() { ["nodes"] = new JsonObject { ["greeting"] = new JsonObject { ["values"] = values } } },
            Diagnostics = [new("OPERATION_INPUT_BINDING_MISSING", "/workflows/0/steps/0/input", "A required observation is missing.")] };
        var fieldSchemas = new JsonObject { [locked] = PlanningDataflow.BindingSchema(["original-producer"]),
            [editable] = Object(new() { ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("string") }, ["text"] = new JsonObject { ["type"] = "string" } }) };
        var schema = Object(new() { ["nodes"] = Object(new() { ["greeting"] = Object(new() { ["values"] = Object(fieldSchemas) }) }) }); schema["$defs"] = new JsonObject();
        var original = unit.Candidate.ToJsonString();
        var patch = PlanningUnitPatches.Create(graph, unit, schema);
        var coordinate = Assert.Single(patch.Context(unit.Candidate)).Key;
        Assert.EndsWith("/" + editable, coordinate); Assert.Same(patch, patch.Narrow());
        var repaired = patch.Apply(unit.Candidate, new() { ["changes"] = new JsonObject { [coordinate] = new JsonObject { ["kind"] = "string", ["text"] = "updated context" } }, ["remove"] = new JsonArray() });
        Assert.True(JsonNode.DeepEquals(fixedValue, repaired["nodes"]!["greeting"]!["values"]![locked])); Assert.Equal(original, unit.Candidate.ToJsonString());
        // With no editable fields, there is no possible model repair to dispatch.
        fieldSchemas.Remove(editable); schema["properties"]!["nodes"]!["properties"]!["greeting"]!["properties"]!["values"]!["required"] = new JsonArray(locked);
        values.Remove(editable);
        Assert.True(PlanningUnitPatches.Create(graph, unit, schema).IsEmpty);
        // A wrong value under the same fixed schema remains repairable.
        fixedValue["reference"] = "wrong-producer";
        Assert.Single(PlanningUnitPatches.Create(graph, unit, schema).Context(unit.Candidate));
    }

    private static JsonObject Object(JsonObject properties) => new() { ["type"] = "object", ["properties"] = properties,
        ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()), ["additionalProperties"] = false };
}
