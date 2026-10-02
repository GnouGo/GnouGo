using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class CheckedProjectionSelectorTests
{
    public static TheoryData<string, string, bool> Cases
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (var executor in new[] { "value.project" })
                foreach (var variant in new[] { "valid", "subset", "checked_fallback", "optional", "nullable", "wider", "fallback", "conditional", "unchecked", "dynamic", "expression" })
                    data.Add(executor, variant, variant is "valid" or "subset" or "checked_fallback");
            return data;
        }
    }

    private static JsonObject Contract => JsonNode.Parse("""{"type":"object","discriminator":{"propertyName":"renamed_selector"},"properties":{"renamed_selector":{"type":"string","enum":["allow","deny"]}},"required":["renamed_selector"]}""")!.AsObject();
    private static WorkflowDocument Document(string executor) => WorkflowParser.Parse($$"""
        version: 1
        workflows:
          main:
            inputs:
              payload: { type: object, required: true }
            steps:
              - id: checked
                type: {{executor}}
                input:
                  value: "${data.inputs.payload}"
                  paths: [[]]
                output_schema:
                  type: object
                  required: [value]
                  properties:
                    value:
                      type: object
                      required: [choice]
                      properties:
                        choice: { type: string, enum: [allow, deny] }
              - id: invoke
                type: mcp.call
                input:
                  server: renamed
                  method: consume
                  request:
                    renamed_selector: "${data.steps.checked.value.choice}"
        """);

    [Theory, MemberData(nameof(Cases))]
    public void OnlyEnforcedAvailableFiniteDomainsProveSelectors(string executor, string variant, bool valid)
    {
        var document = Document(executor); var producer = document.Workflows["main"].Steps[0];
        var choice = producer.OutputSchema!["properties"]!["value"]!["properties"]!["choice"]!;
        switch (variant)
        {
            case "subset": choice["enum"] = new JsonArray("allow"); break;
            case "optional": producer.OutputSchema["properties"]!["value"]!["required"] = new JsonArray(); break;
            case "nullable": choice["type"] = new JsonArray("string", "null"); choice["enum"]!.AsArray().Add((JsonNode?)null); break;
            case "wider": choice["enum"]!.AsArray().Add("outside"); break;
            case "conditional": producer.If = "${false}"; break;
            case "unchecked": producer.OutputSchema = null; break;
            case "dynamic": producer.OutputSchema = JsonValue.Create("${data.inputs.payload}"); break;
            case "expression": document.Workflows["main"].Steps[1].Input!["request"]!["renamed_selector"] = "${data.steps.checked.value.choice + 'outside'}"; break;
            case "fallback": case "checked_fallback": producer.OnError = new() { Cases = [new() { Action = "continue", SetOutput = new JsonObject
                { ["value"] = new JsonObject { ["choice"] = variant == "fallback" ? "outside" : "allow" } } }] }; break;
        }
        if (valid) WorkflowPlanSemanticValidator.Validate(document, [new("renamed", "consume", Contract, null, null)]);
        else
        {
            var error = Assert.Throws<WorkflowSemanticValidationException>(() => WorkflowPlanSemanticValidator.Validate(document, [new("renamed", "consume", Contract, null, null)]));
            Assert.Contains(error.Errors, e => e.Code == "MCP_REQUEST_SELECTOR_NOT_LITERAL");
        }
    }

    [Theory]
    [InlineData("allow", true)]
    [InlineData("outside", false)]
    public async Task ArrayProjectionChecksElementsBeforeScalarSelection(string selected, bool valid)
    {
        var document = Document("value.project");
        var steps = document.Workflows["main"].Steps;
        steps.Insert(0, new()
        {
            Id = "array", Type = "value.project",
            Input = new JsonObject { ["value"] = new JsonArray(new JsonObject { ["decision"] = selected }), ["paths"] = new JsonArray(new JsonArray("decision")), ["each"] = true },
            OutputSchema = JsonNode.Parse("""{"type":"object","required":["value"],"properties":{"value":{"type":"array","items":{"type":"string","enum":["allow","deny"]}}}}""")
        });
        steps[1].Input!["value"] = new JsonObject { ["choice"] = "${data.steps.array.value[0]}" };
        WorkflowPlanSemanticValidator.Validate(document, [new("renamed", "consume", Contract, null, null)]);
        var calls = new List<string>(); var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("renamed", new() { Tools = [new() { Name = "consume", InputSchema = Contract }], ToolHandlers = new()
            { ["consume"] = request => { calls.Add(request!["renamed_selector"]!.ToString()); return new() { Content = new JsonObject() }; } } });
        var compiled = new WorkflowCompiler().Compile(document);
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows["main"],
            new JsonObject { ["payload"] = new JsonObject() }, TestContext.Current.CancellationToken);
        Assert.Equal(valid, result.Success);
        Assert.Equal(valid ? new[] { "allow" } : [], calls);
        if (!valid) Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);

        // Checking an array does not prove that an indexed item exists: scalar use needs its own check.
        steps.RemoveAt(1);
        steps[1].Input!["request"]!["renamed_selector"] = "${data.steps.array.value[0]}";
        var error = Assert.Throws<WorkflowSemanticValidationException>(() => WorkflowPlanSemanticValidator.Validate(document, [new("renamed", "consume", Contract, null, null)]));
        Assert.Contains(error.Errors, e => e.Code == "MCP_REQUEST_SELECTOR_NOT_LITERAL");
    }

    [Theory]
    [InlineData("value.project")]
    public async Task RuntimeChecksRejectMissingNullAndInvalidDataBeforeDispatch(string executor)
    {
        var document = Document(executor);
        WorkflowPlanSemanticValidator.Validate(document, [new("renamed", "consume", Contract, null, null)]);
        var calls = new List<string>(); var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("renamed", new() { Tools = [new() { Name = "consume", InputSchema = Contract }], ToolHandlers = new()
            { ["consume"] = request => { calls.Add(request!["renamed_selector"]!.ToString()); return new() { Content = new JsonObject { ["ok"] = true } }; } } });
        var compiled = new WorkflowCompiler().Compile(document); var engine = new WorkflowEngine { McpClientFactory = factory };
        foreach (var value in new[] { "allow", "deny", "outside", "null", "absent" })
        {
            var payload = new JsonObject();
            if (value != "absent") payload["choice"] = value == "null" ? null : JsonValue.Create(value);
            var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject { ["payload"] = payload }, TestContext.Current.CancellationToken);
            Assert.Equal(value is "allow" or "deny", result.Success);
            if (!result.Success) Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        }
        Assert.Equal(new[] { "allow", "deny" }, calls);
    }
}
