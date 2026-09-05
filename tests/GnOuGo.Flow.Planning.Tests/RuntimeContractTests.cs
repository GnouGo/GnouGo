using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RuntimeContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LoopsAndNumericLiterals_RoundTripWithoutPrecisionLoss()
    {
        var graph = TypedPlannerTests.Graph();
        graph.Workflows[0].Steps.Insert(0, new()
        {
            Key = "repeat", Type = "loop.sequential", ItemVar = "item",
            Input = TypedPlannerTests.Obj(("items", new() { Kind = "array", Items = [new() { Kind = "number", Number = 2147483648m }] })),
            Steps = [new() { Key = "copy", Type = "set", Input = TypedPlannerTests.Obj(("value", new() { Kind = "expression", Text = "item" })) }]
        });
        var compiler = new PlanningGraphCompiler();
        var yaml = compiler.Compile(graph, TypedPlannerTests.Preparation());
        var imported = PlanningGraphImporter.Import(yaml, TypedPlannerTests.Preparation());
        Assert.Equal(2147483648m, imported.Workflows[0].Steps[0].Input.Members[0].Value.Items[0].Number);
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(compiler.Compile(imported, TypedPlannerTests.Preparation())));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message);
        graph.Workflows[0].Steps[0].Input.Members[0].Value.Items[0].Number = 9007199254740993m;
        Assert.Throws<InvalidOperationException>(() => compiler.Compile(graph, TypedPlannerTests.Preparation()));
    }

    [Theory]
    [InlineData("source-alpha", "read_value")]
    [InlineData("renamed-source", "opaque_identifier_42")]
    public async Task ExactDeclarations_AreReusableAndCatalogChangesInvalidateAcceptance(string server, string method)
    {
        var calls = 0;
        var factory = new InMemoryMcpClientFactory();
        var tool = new McpToolInfo
        {
            Name = method, Description = "Return the requested message.",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"mode":{"type":"string","enum":["read"]}},"required":["mode"],"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"],"additionalProperties":false}""")
        };
        factory.RegisterServer(server, new() { Tools = [tool], ToolHandlers = new() { [method] = _ => { calls++; throw new InvalidOperationException("Live dispatch is forbidden during planning validation."); } } });
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = factory });
        var request = new PlanningRequest
        {
            TenantId = "tenant", Prompt = "Read the declared message.", Options = new JsonObject
            {
                ["generator"] = new JsonObject { ["model"] = "fake" },
                ["policy"] = new JsonObject { ["allowed_step_types"] = new JsonArray("mcp.call", "set") },
                ["capability_preflight"] = new JsonObject
                {
                    ["mode"] = "explicit", ["requirements"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "read", ["description"] = "Read the declared message.", ["required"] = true,
                        ["alternatives"] = new JsonArray(new JsonObject { ["server"] = server, ["kind"] = "tool", ["method"] = method,
                            ["request_bindings"] = new JsonArray(new JsonObject { ["path"] = "/mode", ["value"] = "read" }) })
                    })
                }
            }
        };
        var preparation = await runtime.PrepareAsync(request, Ct);
        var capability = Assert.Single(preparation.Capabilities);
        Assert.Empty(await runtime.ValidateCatalogAsync(preparation, Ct));
        var graph = new PlanningGraph { Summary = request.Prompt, Workflows = [new()
        {
            Key = "main", OperationIds = capability.OperationIds,
            Steps = [new() { Key = "read", Type = "mcp.call", CapabilityId = capability.Id, OperationIds = capability.OperationIds }],
            Outputs = [new() { Name = "message", Schema = new() { CapabilityId = capability.Id, SchemaPointer = "/output/properties/message" }, Value = new() { Kind = "output", Source = "read", Path = ["message"] } }]
        }] };
        var yaml = new PlanningGraphCompiler().Compile(graph, preparation);
        Assert.Empty(await runtime.ValidateAsync(yaml, request, preparation, Ct));
        var scenarios = await runtime.ValidateScenariosAsync(yaml, preparation, Ct);
        Assert.All(scenarios, scenario => Assert.Equal("passed", scenario.Outcome));
        Assert.Equal(0, calls);
        tool.OutputSchema!["properties"]!["message"]!["type"] = "integer";
        Assert.Contains(await runtime.ValidateCatalogAsync(preparation, Ct), d => d.Code == "CATALOG_CHANGED");
    }

    [Fact]
    public async Task WorkflowCalls_KeepTheirBoundaryBindingsAcrossImportAndExport()
    {
        var graph = TypedPlannerTests.Graph();
        graph.Workflows[0].Steps = [new() { Key = "greeting", Type = "workflow.call", Input = TypedPlannerTests.Obj(("ref", new() { Kind = "workflow", Source = "child" }), ("args", TypedPlannerTests.Obj())) }];
        var child = TypedPlannerTests.Graph().Workflows[0]; child.Key = "child";
        graph.Workflows.Add(child);
        var compiler = new PlanningGraphCompiler();
        var yaml = compiler.Compile(graph, TypedPlannerTests.Preparation());
        var imported = PlanningGraphImporter.Import(yaml, TypedPlannerTests.Preparation());
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(compiler.Compile(imported, TypedPlannerTests.Preparation())));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("Hello", result.Outputs?["message"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task VersionBoundary_DoesNotSilentlyRunLegacyPlanning(int version)
    {
        var yaml = $$"""
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      planner_version: {{version}}
                      generator: {model: fake, instruction: Return a greeting}
            """;
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.False(result.Success);
        Assert.Contains(version == 2 ? "IWorkflowPlanner" : "Unsupported workflow planner version", result.Error!.Message, StringComparison.Ordinal);
    }
}
