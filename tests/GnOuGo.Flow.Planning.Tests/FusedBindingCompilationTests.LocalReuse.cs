using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class FusedBindingCompilationTests
{
    [Fact]
    public async Task RepeatedPhysicalContainersKeepTheirDistinctIdentityWhenCompared()
    {
        var collection = JsonNode.Parse("""{"type":"array","items":{"type":"string"}}""")!.AsObject();
        var shape = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = collection.DeepClone() },
            ["required"] = new JsonArray("value"), ["additionalProperties"] = false };
        var graph = new PlanningGraph { Workflows = [new() {
            Inputs = [new() { Name = "records", Schema = new() { Contract = collection } }],
            Steps = [new() { Key = "first", Input = Project(new() { Kind = "input", Source = "records" }), OutputSchema = new() { Contract = shape } },
                new() { Key = "second", Input = Project(new() { Kind = "input", Source = "records" }), OutputSchema = new() { Contract = shape.DeepClone().AsObject() } },
                new() { Key = "identity", Input = Obj("same", PlanningValues.Predicate("equal", Ref("first", "value"), Ref("second", "value"))),
                    OutputSchema = Schema("""{"type":"object","properties":{"same":{"type":"boolean"}},"required":["same"],"additionalProperties":false}""") }],
            Outputs = [new() { Name = "same", Schema = new() { Type = "boolean" }, Value = Ref("identity", "same") }]
        }] };
        var compiler = new PlanningGraphCompiler(); var catalog = await Catalog();
        foreach (var current in new[] { false, true })
        {
            var yaml = compiler.Compile(graph, catalog, "identity", true, true, true, true, true, true, true, true, true, true, true, true, current);
            var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
                JsonNode.Parse("""{"records":["same payload"]}"""), PlannerFixture.Ct);
            Assert.True(result.Success, result.Error?.Message);
            Assert.False(result.Outputs!["same"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task DerivedSelectionsPreserveCopiedElementIdentityInsideBranches()
    {
        var record = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""")!.AsObject();
        var collection = new JsonObject { ["type"] = "array", ["items"] = record.DeepClone(), ["minItems"] = 1 };
        static PlanningSchema Wrapped(JsonObject schema) => new() { Contract = new JsonObject { ["type"] = "object",
            ["properties"] = new JsonObject { ["value"] = schema.DeepClone() }, ["required"] = new JsonArray("value"), ["additionalProperties"] = false } };
        static PlanningValue Input(params string[] path) => new() { Kind = "input", Source = "records", Path = path.ToList() };
        var boolean = new JsonObject { ["type"] = "boolean" };
        var graph = new PlanningGraph { Workflows = [new() {
            Inputs = [new() { Name = "records", Schema = new() { Contract = collection } }, new() { Name = "active", Schema = new() { Type = "boolean" } }],
            Steps = [new() { Key = "choose", Type = "switch", Expr = new() { Kind = "input", Source = "active" },
                Cases = [new("true", null, [
                    new() { Key = "pages", Input = Project(Input()), OutputSchema = Wrapped(collection) },
                    new() { Key = "copy", Input = new() { Kind = "projection", Members = [new("value", Ref("pages", "value")),
                        new("paths", new() { Kind = "array", Items = [new() { Kind = "array" }] }),
                        new("each", new() { Kind = "boolean", Boolean = true })] }, OutputSchema = Wrapped(collection) },
                    new() { Key = "selectedCopy", Input = Project(Ref("copy", "value", "0")), OutputSchema = Wrapped(record) },
                    new() { Key = "selectedOriginal", Input = Project(Ref("pages", "value", "0")), OutputSchema = Wrapped(record) },
                    new() { Key = "compare", Input = Project(PlanningValues.Predicate("equal", Ref("selectedCopy", "value"), Ref("selectedOriginal", "value"))), OutputSchema = Wrapped(boolean) }
                ])], Default = [new() { Key = "missing", Input = Project(new() { Kind = "boolean", Boolean = false }), OutputSchema = Wrapped(boolean) }] },
                new() { Key = "merge", Input = new() { Kind = "projection", Members = [new("value", Ref("choose")),
                    new("paths", new() { Kind = "array", Items = new[] { "compare", "missing" }.Select(n => new PlanningValue { Kind = "array", Items =
                        [new() { Kind = "string", Text = n }, new() { Kind = "string", Text = "value" }] }).ToList() })] }, OutputSchema = Wrapped(boolean) }],
            Outputs = [new() { Name = "same", Schema = new() { Type = "boolean" }, Value = Ref("merge", "value") }]
        }] };
        var compiler = new PlanningGraphCompiler(); var catalog = await Catalog();
        foreach (var current in new[] { false, true })
        {
            var yaml = compiler.Compile(graph, catalog, "derived-identity", true, true, true, true, true, true, true, true, true, true, true, true, current);
            foreach (var active in new[] { true, false })
            {
                var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
                    new JsonObject { ["active"] = active, ["records"] = new JsonArray(new JsonObject { ["text"] = "original" }) }, PlannerFixture.Ct);
                Assert.True(result.Success, result.Error?.Message);
                Assert.False(result.Outputs!["same"]!.GetValue<bool>());
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedArrayReadsKeepEveryOrderedCheck(bool stricterSecondRead)
    {
        var array = JsonNode.Parse("""{"type":"array","items":{"type":"array","items":{"type":["number","null"]}}}""")!.AsObject();
        var first = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = array.DeepClone() },
            ["required"] = new JsonArray("value"), ["additionalProperties"] = false };
        var second = first.DeepClone().AsObject();
        if (stricterSecondRead) second["properties"]!["value"]!["minItems"] = 2;
        PlanningValue Pages() => new() { Kind = "input", Source = "searchPages" };
        PlanningValue Flat(PlanningValue value) => new() { Kind = "flatten", Items = [value] };
        var graph = new PlanningGraph { Workflows = [new() {
            Inputs = [new() { Name = "searchPages", Schema = new() { Contract = array } }],
            Steps = [new() { Key = "first", Input = Project(Pages()), OutputSchema = new() { Contract = first } },
                new() { Key = "guard", Input = Project(PlanningValues.Predicate("not_equal", Flat(Ref("first", "value")), new() { Kind = "null" })),
                    OutputSchema = Schema("""{"type":"object","properties":{"value":{"type":"boolean","enum":[true]}},"required":["value"],"additionalProperties":false}""") },
                new() { Key = "second", Input = Project(Pages()), OutputSchema = new() { Contract = second } },
                new() { Key = "assemble", Input = Obj("products", Flat(Ref("second", "value"))),
                    OutputSchema = Schema("""{"type":"object","properties":{"products":{"type":"array","items":{"type":["number","null"]}}},"required":["products"],"additionalProperties":false}""") }],
            Outputs = [new() { Name = "products", Schema = new() { Contract = array["items"]!.DeepClone().AsObject() }, Value = Ref("assemble", "products") }]
        }] };
        var compiler = new PlanningGraphCompiler(); var catalog = await Catalog();
        var old = compiler.Compile(graph, catalog, "array-reads", true, true, true, true, true, true, true, true, true, true, true, true);
        var current = compiler.Compile(graph, catalog, "array-reads", true, true, true, true, true, true, true, true, true, true, true, true, true);
        SaveV15Comparison(stricterSecondRead ? "array-reads-stricter" : "array-reads", old, current);
        var beforeExpression = WorkflowParser.Parse(old).Workflows["main"].Outputs!["products"].Expr!;
        var after = WorkflowParser.Parse(current).Workflows["main"].Outputs!["products"];
        Assert.Equal(2, beforeExpression.Split("data.inputs.searchPages").Length - 1);
        Assert.Equal(1, after.Expr!.Split("data.inputs.searchPages").Length - 1);
        Assert.Contains("!== null", after.Expr); // The original assertion still evaluates.
        Assert.Equal(new[] { "first", "guard", "second", "assemble" }, after.ExpressionContracts![""]!.AsObject()
            .OrderBy(c => c.Value!["order"]!.GetValue<int>()).Select(c => c.Value!["origin"]!.GetValue<string>()));
        Assert.True(after.Expr.Length < beforeExpression.Length);
        string? expected = null;
        foreach (var yaml in new[] { old, current })
        {
            var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
                JsonNode.Parse("{\"searchPages\":[[9007199254740993.00,null,1.23000000000000000000001]]}"), PlannerFixture.Ct);
            Assert.Equal(!stricterSecondRead, result.Success);
            var outcome = result.Success ? result.Outputs!.ToJsonString() : result.Error!.Code + ":" + result.Error.Message + ":" + result.Error.Details?["location"];
            if (expected is not null) Assert.Equal(expected, outcome); expected = outcome;
        }
        Assert.Equal(old, compiler.Compile(graph, catalog, "array-reads", true, true, true, true, true, true, true, true, true, true, true, true));
    }

    [Theory]
    [InlineData("id:one", true)]
    [InlineData(null, true)]
    [InlineData("unknown", false)]
    public async Task NullableLookupsPreserveMembershipChecksAndCompleteText(string? selected, bool success)
    {
        var records = JsonNode.Parse("""{"type":"array","items":{"type":"object","properties":{"reference":{"type":"string"},"text":{"type":"string"}},"required":["reference","text"],"additionalProperties":false}}""")!.AsObject();
        static PlanningValue Input(string name) => new() { Kind = "input", Source = name };
        static PlanningSchema Wrapped(JsonObject shape) => new() { Contract = new JsonObject { ["type"] = "object",
            ["properties"] = new JsonObject { ["value"] = shape.DeepClone() }, ["required"] = new JsonArray("value"), ["additionalProperties"] = false } };
        var strings = JsonNode.Parse("""{"type":"array","items":{"type":["string","null"]}}""")!.AsObject();
        PlanningValue Lookup(string source) => new() { Kind = "lookup", Text = "reference", Items =
            [Input(source), Ref("selection", "value")] };
        var graph = new PlanningGraph { Workflows = [new() {
            Inputs = [new() { Name = "selected", Schema = new() { Type = "string", Nullable = true } },
                new() { Name = "offeredBlocks", Schema = new() { Contract = records } },
                new() { Name = "originalBlocks", Schema = new() { Contract = records.DeepClone().AsObject() } }],
            Steps = [new() { Key = "choose", Type = "switch", Expr = PlanningValues.Predicate("not_equal", Input("selected"), new() { Kind = "null" }),
                Cases = [new("true", null, [
                    new() { Key = "identity", Input = Project(Input("selected")), OutputSchema = Wrapped(new() { ["type"] = "string" }) },
                    new() { Key = "selection", Input = Project(new() { Kind = "array", Items = [Ref("identity", "value")] }),
                        OutputSchema = Wrapped(new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }) },
                    new() { Key = "offered", Input = Project(Lookup("offeredBlocks")), OutputSchema = Wrapped(records) },
                    new() { Key = "original", Input = Project(Lookup("originalBlocks")), OutputSchema = Wrapped(records) },
                    new() { Key = "text", Input = new() { Kind = "projection", Members = [new("value", Ref("original", "value")),
                        new("paths", new() { Kind = "array", Items = [new() { Kind = "array", Items = [new() { Kind = "string", Text = "text" }] }] }),
                        new("each", new() { Kind = "boolean", Boolean = true })] }, OutputSchema = Wrapped(strings) }
                ])], Default = [new() { Key = "missing", Input = Project(new() { Kind = "array", Items = [new() { Kind = "null" }] }), OutputSchema = Wrapped(strings) }] },
                new() { Key = "merge", Input = new() { Kind = "projection", Members = [new("value", Ref("choose")),
                    new("paths", new() { Kind = "array", Items = new[] { "text", "missing" }.Select(n => new PlanningValue { Kind = "array", Items =
                        [new() { Kind = "string", Text = n }, new() { Kind = "string", Text = "value" }] }).ToList() })] }, OutputSchema = Wrapped(strings) }],
            Outputs = [new() { Name = "description", Schema = new() { Contract = strings }, Value = Ref("merge", "value") }]
        }] };
        var compiler = new PlanningGraphCompiler(); var catalog = await Catalog();
        var before = compiler.Compile(graph, catalog, "description", true, true, true, true, true, true, true, true, true, true, true, true);
        var after = compiler.Compile(graph, catalog, "description", true, true, true, true, true, true, true, true, true, true, true, true, true);
        SaveV15Comparison("nullable-lookup", before, after);
        Assert.Equal(2, WorkflowParser.Parse(after).Workflows["main"].Outputs!["description"].Expr!.Split("m.lookup(").Length - 1);
        string? expected = null;
        foreach (var yaml in new[] { before, after })
        {
            var context = new JsonObject { ["selected"] = selected,
                ["offeredBlocks"] = JsonNode.Parse("""[{"reference":"id:one","text":"  Complete\ntext\t é  "}]"""),
                ["originalBlocks"] = JsonNode.Parse("""[{"reference":"id:one","text":"  Complete\ntext\t é  "},{"reference":"other","text":"unrelated"}]""") };
            var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"], context, PlannerFixture.Ct);
            Assert.Equal(success, result.Success);
            var outcome = result.Success ? result.Outputs!.ToJsonString() : result.Error!.Code + ":" + result.Error.Message + ":" + result.Error.Details?["location"];
            if (expected is not null) Assert.Equal(expected, outcome); expected = outcome;
            if (success) Assert.Equal(selected is null ? null : "  Complete\ntext\t é  ", result.Outputs!["description"]![0]?.GetValue<string>());
        }
    }

    private static void SaveV15Comparison(string name, string before, string after)
    {
        if (Environment.GetEnvironmentVariable("GNOU_GO_V15_EXAMPLES") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + "-v14.yaml"), before);
        File.WriteAllText(Path.Combine(folder, name + "-v15.yaml"), after);
    }
}
