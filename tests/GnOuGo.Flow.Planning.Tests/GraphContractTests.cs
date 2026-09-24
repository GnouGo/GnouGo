using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class GraphContractTests
{
    [Fact]
    public void SchemaReferencesCannotCarryConflictingInlineConstraints()
    {
        var catalog = new PlanningCatalog { Capabilities = [new() { Id = "selected", OutputSchema = new() { ["type"] = "number" } }] };
        var root = PlanningSchemas.Proposal(new() { Catalog = catalog });
        var schema = root["$defs"]!["schema"]!.DeepClone().AsObject();
        schema["$defs"] = root["$defs"]!.DeepClone();
        var reference = new JsonObject { ["capabilityId"] = "selected", ["schemaPointer"] = "/output" };
        Assert.Empty(PlanningContractValidation.ValidateInstance(reference, schema));
        var decoded = JsonSerializer.Deserialize(reference, PlanningJsonContext.Default.PlanningSchema)!;
        Assert.Equal("number", PlanningSchemaReferences.Resolve(decoded, catalog)["type"]!.ToString());
        reference["type"] = "any";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(reference, schema));
    }

    [Fact]
    public void GeneratedControlContractsCannotOverrideTheirChildren()
    {
        var graph = PlannerFixture.Greeting();
        graph.Workflows[0].Steps[0].Type = "sequence";
        graph.Workflows[0].Steps[0].OutputSchema = new() { Type = "number" };
        Assert.Contains(PlanningGeneratedGraph.Validate(graph, PlannerFixture.Requirements(), new()),
            d => d.Code == "GENERATED_SCHEMA_OVERRIDE_DENIED");
    }

    [Theory]
    [InlineData("local")]
    [InlineData("collections")]
    public async Task RegisteredTransformsCompileWithoutScriptInference(string name)
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), TestContext.Current.CancellationToken);
        var graph = PlanningCorpus.Graph(name, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        new PlanningGraphCompiler().Compile(graph, catalog);
    }
}
