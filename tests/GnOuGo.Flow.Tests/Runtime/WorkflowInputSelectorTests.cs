using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowInputSelectorTests
{
    private static readonly JsonObject Contract = JsonNode.Parse("""{"type":"object","discriminator":{"propertyName":"choice"},"properties":{"choice":{"type":"string","enum":["allow","deny","other"]}},"required":["choice"]}""")!.AsObject();

    private static WorkflowDocument Document() => WorkflowParser.Parse("""
        version: 1
        workflows:
          parent:
            inputs:
              proposed: { type: string, required: true }
            steps:
              - id: dispatch
                type: workflow.call
                input:
                  ref: { kind: local, name: child }
                  args: { decision: '${data.inputs.proposed}' }
          child:
            inputs:
              decision: { type: string, required: true, enum: [allow, deny] }
            steps:
              - id: consumer
                type: mcp.call
                input:
                  server: example
                  method: apply
                  request: { choice: '${data.inputs.decision}' }
        """);

    [Fact]
    public async Task RequiredInputCarriesItsCheckedDomainAcrossTheCallBoundary()
    {
        var doc = Document();
        WorkflowPlanSemanticValidator.Validate(doc, [new("example", "apply", Contract, null, null)]);
        var workflows = new WorkflowCompiler().Compile(doc).Workflows;
        var calls = new List<string>();
        var mcp = new InMemoryMcpClientFactory();
        mcp.RegisterServer("example", new() { Tools = [new() { Name = "apply", InputSchema = Contract }], ToolHandlers = new()
            { ["apply"] = args => { calls.Add(args!["choice"]!.ToString()); return new() { Content = new JsonObject() }; } } });
        var engine = new WorkflowEngine { McpClientFactory = mcp };
        foreach (var choice in new[] { "allow", "deny", "other" })
        {
            var result = await engine.ExecuteAsync(workflows["parent"], new JsonObject { ["proposed"] = choice }, TestContext.Current.CancellationToken);
            Assert.Equal(choice != "other", result.Success);
            if (!result.Success) Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        }
        Assert.Equal(new[] { "allow", "deny" }, calls);
    }

    [Theory]
    [InlineData("optional")]
    [InlineData("nullable")]
    [InlineData("open")]
    [InlineData("outside")]
    [InlineData("conditional_write")]
    [InlineData("loop_shadow")]
    public void UncheckedOrReplacedInputsCannotProveTheSelector(string variation)
    {
        var doc = Document();
        var child = doc.Workflows["child"];
        var input = child.Inputs!["decision"];
        switch (variation)
        {
            case "optional": input.Required = false; break;
            case "nullable": input.Nullable = true; break;
            case "open": input.Enum = null; break;
            case "outside": input.Enum = ["allow", "unknown"]; break;
            case "conditional_write": child.Steps.Insert(0, new() { Id = "replace", Type = "set", If = "${true}", Output = "inputs", Input = new JsonObject { ["decision"] = "outside" } }); break;
            case "loop_shadow": child.Steps.Insert(0, new() { Id = "prior", Type = "loop.sequential", ItemVar = "inputs", Input = new JsonObject { ["items"] = new JsonArray("unverified") }, Steps = [] }); break;
        }
        var error = Assert.Throws<WorkflowSemanticValidationException>(() => WorkflowPlanSemanticValidator.Validate(doc, [new("example", "apply", Contract, null, null)]));
        Assert.Contains(error.Errors, finding => finding.Code == "MCP_REQUEST_SELECTOR_NOT_LITERAL");
    }

    [Fact]
    public async Task NonObjectEntryCannotSkipTheDeclaredInputContracts()
    {
        var doc = Document();
        var child = new WorkflowCompiler().Compile(doc).Workflows["child"];
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => new WorkflowEngine().ExecuteAsync(child, new JsonArray("outside"), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InputValidation, error.Code);
    }
}
