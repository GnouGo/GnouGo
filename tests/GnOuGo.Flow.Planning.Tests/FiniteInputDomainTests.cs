using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FiniteInputDomainTests
{
    private static PlanningSession State(bool resolved, string contract = """{"type":"string","enum":["alpha","beta"]}""")
    {
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements();
        var capability = new PlanningCapability { Id = "op", Version = "v1", StepType = "mcp.call", Kind = "tool", Server = "source", Method = "arbitrary",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["selector"] = JsonNode.Parse(contract), ["text"] = new JsonObject { ["type"] = "string" } } },
            OutputSchema = new() { ["type"] = "object" } };
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"] }; state.Discovery.Sources = [new("source", "Declared metadata")];
        state.Discovery.Pages = [new("source", null, [new("op", "source", "arbitrary", "No inferred semantics", "mcp.call", "none", "v1", Operation: TaskOperations.Describe(capability))], null)];
        if (resolved) state.Discovery.Resolved.Add(capability);
        return state;
    }

    private static JsonObject Plan(JsonNode value, string operation = "op") => new()
    {
        ["inputs"] = new JsonArray(), ["groups"] = new JsonArray(), ["choices"] = new JsonArray(),
        ["root"] = new JsonObject
        {
            ["outputs"] = new JsonArray(), ["always"] = new JsonArray(), ["tasks"] = new JsonArray(new JsonObject
            {
                ["id"] = "consume", ["kind"] = "operation", ["objective"] = "Use a declared value", ["dependsOn"] = new JsonArray(), ["requires"] = null,
                ["operation"] = operation, ["inputs"] = new JsonArray(new JsonObject { ["name"] = "selector", ["value"] = value })
            })
        }
    };

    [Fact]
    public void ResolvedFiniteLiteralsHaveNoUnrestrictedOperationOrValueAlternative()
    {
        var schema = PlanningSchemas.TaskPlanGrammar(State(true));
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        foreach (var text in new[] { "alpha", "beta" })
            Assert.Empty(PlanningContractValidation.ValidateInstance(Plan(new JsonObject { ["kind"] = "string", ["text"] = text }), schema));
        foreach (var value in new[] { """{"kind":"string","text":"other"}""", """{"kind":"number","number":1}""", """{"kind":"null"}""", """{"kind":"object","members":[]}""" })
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Plan(JsonNode.Parse(value)!), schema));
        foreach (var value in new[] { """{"kind":"input","source":"choice"}""", """{"kind":"output","source":"producer","port":"decision"}""", """{"kind":"field","items":[{"kind":"item"}],"port":"decision"}""" })
            Assert.Empty(PlanningContractValidation.ValidateInstance(Plan(JsonNode.Parse(value)!), schema));
    }

    [Theory]
    [InlineData("{\"const\":\"exact\"}", "{\"kind\":\"string\",\"text\":\"exact\"}", "{\"kind\":\"string\",\"text\":\"other\"}")]
    [InlineData("{\"enum\":[1,2]}", "{\"kind\":\"number\",\"number\":2}", "{\"kind\":\"number\",\"number\":3}")]
    [InlineData("{\"const\":true}", "{\"kind\":\"boolean\",\"boolean\":true}", "{\"kind\":\"boolean\",\"boolean\":false}")]
    [InlineData("{\"const\":null}", "{\"kind\":\"null\"}", "{\"kind\":\"string\",\"text\":\"null\"}")]
    [InlineData("{\"type\":[\"string\",\"null\"],\"enum\":[\"x\",null]}", "{\"kind\":\"null\"}", "{\"kind\":\"string\",\"text\":\"wrong\"}")]
    public void ScalarDomainsPreserveJsonTypesAndExplicitNull(string contract, string valid, string invalid)
    {
        var schema = PlanningSchemas.TaskPlanGrammar(State(true, contract));
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Plan(JsonNode.Parse(valid)!), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Plan(JsonNode.Parse(invalid)!), schema));
    }

    [Fact]
    public void EqualPortNamesDoNotMergeDifferentOperationDomains()
    {
        var state = State(true); var other = State(true, """{"enum":["different"]}""").Discovery.Resolved[0];
        other.Id = "other"; state.Discovery.Resolved.Add(other);
        var schema = PlanningSchemas.TaskPlanGrammar(state);
        var value = JsonNode.Parse("""{"kind":"string","text":"different"}""")!;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Plan(value.DeepClone()), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Plan(value, "other"), schema));
    }

    [Fact]
    public void KnownOwnedDomainsNeverBecomeEditableOrDisclosed()
    {
        var state = State(true); state.Discovery.Resolved[0].RequestBindings.Add(new("/selector", JsonValue.Create("alpha")));
        var schema = PlanningSchemas.TaskPlanGrammar(state);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Plan(JsonNode.Parse("""{"kind":"string","text":"alpha"}""")!), schema));
        var editable = PlanningCapabilityArguments.Editable(state.Discovery.Resolved[0]);
        Assert.DoesNotContain(editable.Inputs, input => input.Name == "selector");
        Assert.Equal("alpha", state.Discovery.Resolved[0].InputSchema["properties"]!["selector"]!["enum"]![0]!.ToString());
    }

    [Fact]
    public void ExactResolutionConstrainsPreviouslyIndexOnlyOperationsWithoutMutatingReceipts()
    {
        var state = State(false); var invalid = Plan(JsonNode.Parse("""{"kind":"string","text":"invented"}""")!);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, PlanningSchemas.TaskPlanGrammar(state)));
        state.Discovery.Resolved.Add(State(true).Discovery.Resolved[0]);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, PlanningSchemas.TaskPlanGrammar(state)));
        state.Catalog!.Capabilities.Add(state.Discovery.Resolved[0]);
        var result = new TaskPlanCompiler().Compile(invalid.Deserialize(PlanningJsonContext.Default.TaskPlan)!, state.Catalog);
        Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "TASK_INPUT_TYPE");
    }

}
