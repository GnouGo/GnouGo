using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class CheckedSelectorReferenceTests
{
    [Theory]
    [InlineData("${data.steps.result.choice}", true)]
    [InlineData("${data['steps']['result']['choice']}", true)]
    [InlineData("${data.steps.result?.choice}", false)]
    [InlineData("${data.steps[other].choice}", false)]
    [InlineData("${((data) => data.steps.result.choice)({steps:{result:{choice:'other'}}})}", false)]
    [InlineData("${data.steps.result.choice + 'other'}", false)]
    public void OnlyExactCheckedReferencesProveAnEnum(string expression, bool expected)
    {
        var symbols = WorkflowSymbolTable.Create("main", null, new HashSet<string> { "result" });
        symbols.SetCheckedStepOutput("result", FlowTypeDescriptor.Object(new Dictionary<string, FlowPropertyDescriptor>
            { ["choice"] = new(FlowTypeDescriptor.Enum("allow", "deny"), true) }));
        Assert.Equal(expected, CheckedSelectorReference.IsWithin(expression, [JsonValue.Create("allow")!, JsonValue.Create("deny")!], symbols.Clone()));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("choix_renomme")]
    public async Task RuntimeCheckedEnumsReachMcpArgumentsAndInvalidResultsNeverDispatch(string selector)
    {
        var document = Document(selector);
        var schema = InputSchema(selector);
        WorkflowPlanSemanticValidator.Validate(document, [new("fixture", "send", schema, null, null)]);
        var calls = new List<string>(); var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("fixture", new() { Tools = [new() { Name = "send", InputSchema = schema }], ToolHandlers = new()
            { ["send"] = request => { calls.Add(request![selector]!.GetValue<string>()); return new() { Content = new JsonObject { ["ok"] = true } }; } } });
        var compiled = new WorkflowCompiler().Compile(document);
        var engine = new WorkflowEngine { McpClientFactory = factory };
        foreach (var selected in new[] { "allow", "deny", "invalid" })
        {
            var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject { ["selected"] = selected }, TestContext.Current.CancellationToken);
            Assert.Equal(selected != "invalid", result.Success);
        }
        Assert.Equal(new[] { "allow", "deny" }, calls);
    }

    [Theory]
    [InlineData("optional")]
    [InlineData("nullable")]
    [InlineData("fallback")]
    [InlineData("unchecked")]
    public void MissingOrUncheckedValuesCannotProveASelector(string variation)
    {
        var document = Document("event"); var producer = document.Workflows["main"].Steps[0];
        switch (variation)
        {
            case "optional": producer.OutputSchema!["required"] = new JsonArray(); break;
            case "nullable": producer.OutputSchema!["properties"]!["choice"]!["type"] = new JsonArray("string", "null"); break;
            case "fallback": producer.OnError = new() { Cases = [new() { Action = "continue", SetOutput = new JsonObject { ["choice"] = "other" } }] }; break;
            case "unchecked": producer.OutputSchema = null; break;
        }
        var error = Assert.Throws<WorkflowSemanticValidationException>(() => WorkflowPlanSemanticValidator.Validate(document, [new("fixture", "send", InputSchema("event"), null, null)]));
        Assert.Contains(error.Errors, e => e.Code == "MCP_REQUEST_SELECTOR_NOT_LITERAL");
    }

    private static JsonObject InputSchema(string selector) => new() { ["type"] = "object", ["discriminator"] = new JsonObject { ["propertyName"] = selector },
        ["properties"] = new JsonObject { [selector] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("allow", "deny") } }, ["required"] = new JsonArray(selector) };

    private static WorkflowDocument Document(string selector) => WorkflowParser.Parse($$"""
        version: 1
        workflows:
          main:
            inputs:
              selected: { type: string, required: true }
            steps:
              - id: result
                type: set
                input:
                  choice: "${data.inputs.selected}"
                output_schema:
                  type: object
                  required: [choice]
                  properties:
                    choice: { type: string, enum: [allow, deny] }
              - id: invoke
                type: mcp.call
                input:
                  server: fixture
                  method: send
                  request:
                    {{selector}}: "${data.steps.result.choice}"
        """);
}
