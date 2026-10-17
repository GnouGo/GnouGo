using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowControlContractTests
{
    private const string CheckedControl = """
        version: 1
        workflows:
          main:
            inputs:
              value: {type: any, required: false, nullable: true}
            steps:
            - id: route
              type: switch
              expr: '${(() => { const allowed = data.inputs.value; return allowed; })()}'
              control_contracts:
                /expr:
                  allowed: {order: 0, source: allowed, origin: originalGuard, instance_path: /value, schema: {type: boolean}}
              cases:
              - value: 'true'
                steps:
                - id: effect
                  type: set
                  input: {visited: true}
              default: []
        """;

    [Theory]
    [InlineData("true", true, true)]
    [InlineData("false", true, false)]
    [InlineData("null", false, false)]
    [InlineData("\"true\"", false, false)]
    public async Task ControlIsCheckedBeforeBranchWork(string value, bool success, bool effect)
    {
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(CheckedControl));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], new JsonObject { ["value"] = JsonNode.Parse(value) }, TestContext.Current.CancellationToken);
        Assert.Equal(success, result.Success);
        Assert.Equal(effect, result.StepResults.Any(s => s.Output is JsonObject obj && obj.ContainsKey("effect")));
        if (!success)
        {
            Assert.Equal("originalGuard", result.Error!.Details!["location"]!.GetValue<string>());
            Assert.Contains("$.value", result.Error.Message);
        }
    }

    [Fact]
    public async Task FalseCheckedIfDoesNotResolveInputs()
    {
        var definition = WorkflowParser.Parse(CheckedControl);
        var step = definition.Workflows["main"].Steps[0];
        step.Type = "set"; step.Cases = null; step.Default = null;
        step.If = step.Expr; step.Expr = null;
        step.ControlContracts = new() { ["/if"] = step.ControlContracts!["/expr"]!.DeepClone() };
        step.Input = new JsonObject { ["forbidden"] = "${data.inputs.absent.value}" };
        var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(definition).Workflows["main"],
            new JsonObject { ["value"] = false }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(StepStatus.Skipped, Assert.Single(result.StepResults).Status);
    }

    [Fact]
    public async Task ReachedCaseChecksPreserveFirstMatchOrdering()
    {
        var definition = WorkflowParser.Parse(CheckedControl);
        var step = definition.Workflows["main"].Steps[0];
        var check = step.ControlContracts!["/expr"]!.DeepClone();
        step.Expr = null;
        step.Cases![0].Value = null;
        step.Cases[0].When = "${(() => { const allowed = true; return allowed; })()}";
        step.Cases.Add(new() { When = "${(() => { const allowed = data.inputs.absent; return allowed; })()}" });
        step.ControlContracts = new() { ["/cases/0/when"] = check, ["/cases/1/when"] = check.DeepClone() };
        var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(definition).Workflows["main"],
            new JsonObject { ["value"] = false }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(Assert.Single(result.StepResults).Output!["effect"]!["visited"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/input")]
    [InlineData("/cases/00/when")]
    [InlineData("/cases/0/when")]
    [InlineData("/cases/9/when")]
    [InlineData("/if")]
    public void InvalidOrAmbiguousTargetsFailCompilation(string pointer)
    {
        var document = WorkflowParser.Parse(CheckedControl); var step = document.Workflows["main"].Steps[0];
        step.ControlContracts = new() { [pointer] = step.ControlContracts!["/expr"]!.DeepClone() };
        Assert.Throws<WorkflowCompilationException>(() => new WorkflowCompiler().Compile(document));
    }

    [Fact]
    public async Task MetadataSurvivesPersistenceAndCommittedControlIsNotReevaluated()
    {
        var definition = WorkflowParser.Parse(CheckedControl);
        var json = JsonSerializer.Serialize(definition, WorkflowRunJsonContext.Default.WorkflowDocument);
        var restored = JsonSerializer.Deserialize(json, WorkflowRunJsonContext.Default.WorkflowDocument)!;
        Assert.True(JsonNode.DeepEquals(definition.Workflows["main"].Steps[0].ControlContracts, restored.Workflows["main"].Steps[0].ControlContracts));
        var workflow = new WorkflowCompiler().Compile(restored).Workflows["main"];
        var store = new InMemoryWorkflowRunStore();
        var engine = new WorkflowEngine { RunStore = store, Limits = new() { TenantId = "tenant", RunId = "checked-controls" } };
        var result = await engine.ExecuteAsync(workflow, new JsonObject { ["value"] = true }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        var saved = (await store.ReadAsync("tenant", "checked-controls", TestContext.Current.CancellationToken))!;
        Assert.Contains(saved.Invocations.Values, i => i.Control.ContainsKey("branch"));
        var resumed = await new WorkflowEngine { RunStore = store }.ResumeAsync("tenant", "checked-controls", saved.Revision, workflow, TestContext.Current.CancellationToken);
        Assert.True(resumed.Success, resumed.Error?.Message);
    }
}
