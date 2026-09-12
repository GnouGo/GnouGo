using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Expressions;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class WorkflowInputDefaultsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineAppliesDefaultsBeforeExecutionAndPreservesExplicitNull(bool child)
    {
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                inputs:
                  limit: { type: number, required: false, default: 100 }
                steps: []
                outputs:
                  result: { type: number, expr: "${data.inputs.limit}" }
            """));
        var engine = new WorkflowEngine(); var workflow = document.Workflows["main"]; var input = new JsonObject();
        Task<RunResult> Run(JsonObject value) => child
            ? engine.ExecuteChildWorkflowAsync(workflow, value, new(), 0, [], null, TestContext.Current.CancellationToken)
            : engine.ExecuteAsync(workflow, value, TestContext.Current.CancellationToken);
        var result = await Run(input);
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("100"), result.Outputs!["result"]));
        Assert.Empty(input);
        var exception = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Run(new JsonObject { ["limit"] = null }));
        Assert.Equal(ErrorCodes.InputValidation, exception.Code);
    }

    [Fact]
    public void Apply_AddsMissingDefaults()
    {
        var workflow = new WorkflowDef
        {
            Inputs = new Dictionary<string, InputDef>
            {
                ["site"] = new() { Type = "string", Required = false, Default = "https://example.com/" },
                ["count"] = new() { Type = "number", Required = false, Default = 3 },
                ["flags"] = new() { Type = "array", Required = false, Default = new List<object?> { true, false } }
            }
        };

        var merged = WorkflowInputDefaults.Apply(workflow, new JsonObject());

        Assert.Equal("https://example.com/", merged["site"]!.GetValue<string>());
        Assert.Equal(3, merged["count"]!.GetValue<int>());
        var flags = Assert.IsType<JsonArray>(merged["flags"]);
        Assert.Equal(2, flags.Count);
        Assert.True(flags[0]!.GetValue<bool>());
        Assert.False(flags[1]!.GetValue<bool>());
    }

    [Fact]
    public void Apply_CoercesYamlScalarDefaultsToDeclaredTypes()
    {
        var workflow = new WorkflowDef
        {
            Inputs = new Dictionary<string, InputDef>
            {
                ["count"] = new() { Type = "number", Required = false, Default = "10" },
                ["enabled"] = new() { Type = "boolean", Required = false, Default = "true" },
                ["config"] = new()
                {
                    Type = "object",
                    Required = false,
                    Default = new Dictionary<string, object?> { ["port"] = "8080" },
                    Properties = new Dictionary<string, InputDef>
                    {
                        ["port"] = new() { Type = "number", Required = false }
                    }
                }
            }
        };

        var merged = WorkflowInputDefaults.Apply(workflow, new JsonObject());

        Assert.Equal(10d, merged["count"]!.GetValue<double>());
        Assert.True(merged["enabled"]!.GetValue<bool>());
        var config = Assert.IsType<JsonObject>(merged["config"]);
        Assert.Equal(8080d, config["port"]!.GetValue<double>());
    }

    [Fact]
    public void Apply_AllowsNestedJsonNodeDefaultsWithoutReparenting()
    {
        var workflow = new WorkflowDef
        {
            Inputs = new Dictionary<string, InputDef>
            {
                ["config"] = new()
                {
                    Type = "object",
                    Required = false,
                    Default = new JsonObject
                    {
                        ["service"] = new JsonObject
                        {
                            ["name"] = "api"
                        }
                    },
                    Properties = new Dictionary<string, InputDef>
                    {
                        ["service"] = new()
                        {
                            Type = "object",
                            Properties = new Dictionary<string, InputDef>
                            {
                                ["name"] = new() { Type = "string", Required = true }
                            },
                            RequiredProperties = ["name"]
                        }
                    },
                    RequiredProperties = ["service"]
                }
            }
        };

        var merged = WorkflowInputDefaults.Apply(workflow, new JsonObject());

        var config = Assert.IsType<JsonObject>(merged["config"]);
        var service = Assert.IsType<JsonObject>(config["service"]);
        Assert.Equal("api", service["name"]!.GetValue<string>());
    }

    [Fact]
    public void Apply_DoesNotOverrideExplicitInputs()
    {
        var workflow = new WorkflowDef
        {
            Inputs = new Dictionary<string, InputDef>
            {
                ["site"] = new() { Type = "string", Required = false, Default = "https://example.com/" }
            }
        };

        var merged = WorkflowInputDefaults.Apply(
            workflow,
            new JsonObject { ["site"] = "https://www.iana.org/" });

        Assert.Equal("https://www.iana.org/", merged["site"]!.GetValue<string>());
    }
}
