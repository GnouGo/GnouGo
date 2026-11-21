using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ProjectedReferenceContractTests
{
    [Theory]
    [InlineData("/input/properties/alias")]
    [InlineData("/output/properties/alias")]
    [InlineData("/output/$defs/record")]
    public void OriginalSchemaSelectionClosesItsOwnedLocalReferences(string pointer)
    {
        var schema = JsonNode.Parse("""{"type":"object","$defs":{"record":{"type":"object","properties":{"label":{"type":"string","enum":["observed"]}},"required":["label"],"additionalProperties":false}},"properties":{"alias":{"$ref":"#/$defs/record"}},"required":["alias"]}""")!.AsObject();
        if (pointer.EndsWith("/$defs/record", StringComparison.Ordinal))
            schema = new JsonObject { ["$ref"] = "#/$defs/record", ["$defs"] = schema["$defs"]!.DeepClone() };
        var before = schema.ToJsonString();
        var catalog = new PlanningCatalog { Capabilities = [new() { Id = "owned", Version = "1", InputSchema = schema, OutputSchema = schema }] };
        var selected = PlanningSchemaReferences.Resolve(new() { CapabilityId = "owned", SchemaPointer = pointer }, catalog);
        Assert.Empty(PlanningContractValidation.ValidateSchema(selected));
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["label"] = "observed" }, selected));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["label"] = "invented" }, selected));
        Assert.Equal(before, schema.ToJsonString());
    }

    [Theory]
    [InlineData("#", true)]
    [InlineData("#/missing", false)]
    [InlineData("https://unapproved.invalid/schema", false)]
    public void RecursiveUnresolvedAndExternalReferencesRemainSubjectToValidation(string reference, bool valid)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            { ["value"] = new JsonObject { ["$ref"] = reference } },
            ["default"] = new JsonObject { ["$ref"] = "literal data, not a schema" } };
        var before = schema.ToJsonString();
        var expanded = PlanningSchemaReferences.ExpandLocalReferences(schema);
        Assert.Equal(before, schema.ToJsonString()); Assert.True(JsonNode.DeepEquals(schema, expanded));
        Assert.Equal(valid, PlanningContractValidation.ValidateSchema(expanded).Count == 0);
    }

    [Fact]
    public void CompactExponentialReferencesFailBeforeUnboundedExpansion()
    {
        var definitions = new JsonObject { ["leaf"] = new JsonObject { ["type"] = "string" } };
        var previous = "leaf";
        for (var i = 0; i < 20; i++)
        {
            var name = "branch" + i;
            definitions[name] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                ["left"] = new JsonObject { ["$ref"] = "#/$defs/" + previous },
                ["right"] = new JsonObject { ["$ref"] = "#/$defs/" + previous } } };
            previous = name;
        }
        var schema = new JsonObject { ["$ref"] = "#/$defs/" + previous, ["$defs"] = definitions };
        var before = schema.ToJsonString();
        var error = Assert.Throws<InvalidOperationException>(() => PlanningSchemaReferences.ExpandLocalReferences(schema));
        Assert.Contains("schema-node bound", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, schema.ToJsonString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SharedResultReferencesSurviveProjectionAndScopeExport(int variant)
    {
        var shared = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string","enum":["observed"]}},"required":["value"],"additionalProperties":false}""")!;
        if (variant == 2) shared["properties"]!["value"]!.AsObject().Remove("enum");
        var output = JsonNode.Parse("""{"type":"object","properties":{"primary":{},"payload":{"type":["object","null"],"properties":{"copies":{"type":"array","items":{}}},"required":["copies"],"additionalProperties":false}},"required":["primary","payload"]}""")!.AsObject();
        output["properties"]!["primary"] = shared.DeepClone();
        output["properties"]!["payload"]!["properties"]!["copies"]!["items"] = new JsonObject
            { ["$ref"] = variant == 1 ? "#/$defs/shared" : "#/properties/primary" };
        if (variant == 1) output["$defs"] = new JsonObject { ["shared"] = shared.DeepClone() };
        if (variant == 2) output["properties"]!["payload"]!["properties"]!["copies"]!["items"]!["properties"] =
            new JsonObject { ["value"] = new JsonObject { ["enum"] = new JsonArray("observed") } };
        var before = output.ToJsonString();
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        catalog.Capabilities.Add(new() { Id = "read", Version = "1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = "read",
            InputSchema = new() { ["type"] = "object" }, OutputSchema = output, EffectKind = "none" });
        var plan = new TaskPlan { Root = new() { Tasks = [
            new() { Id = "observed", Kind = "operation", Objective = "Read the actual result", Operation = "read" },
            new() { Id = "scope", Kind = "sequence", Objective = "Export the selected result", Body = new() { Tasks = [
                new() { Id = "copy", Kind = "value", Objective = "Preserve the selected payload", Outputs = [new("payload", new() { Kind = "output", Source = "observed", Port = "payload" })] }
            ], Outputs = [new("payload", new() { Kind = "output", Source = "copy", Port = "payload" })] } }
        ], Outputs = [new("payload", new() { Kind = "output", Source = "scope", Port = "payload" })] } };
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics); Assert.NotNull(compilation.Graph);
        Assert.Empty(PlanningGeneratedGraph.Validate(compilation.Graph, catalog));
        Assert.Empty(PlanningExecutableValidation.Validate(compilation.Graph, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph, catalog);
        Assert.Equal(before, output.ToJsonString());
        var mode = "valid"; var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("source", new() { Tools = [new() { Name = "read", InputSchema = new JsonObject { ["type"] = "object" }, OutputSchema = output }],
            ToolHandlers = new() { ["read"] = _ => new() { Content = new JsonObject { ["primary"] = new JsonObject { ["value"] = "observed" },
                ["payload"] = mode == "null" ? null : new JsonObject { ["copies"] = new JsonArray(new JsonObject { ["value"] = mode == "invalid" ? "invented" : "observed" }) } } } } });
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        foreach (var data in new[] { "valid", "null", "invalid" })
        {
            mode = data;
            var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
            Assert.Equal(data != "invalid", result.Success);
            if (data == "valid") Assert.Equal("observed", result.Outputs!["payload"]!["copies"]![0]!["value"]!.ToString());
            if (data == "null") Assert.Null(result.Outputs!["payload"]);
        }
    }
}
