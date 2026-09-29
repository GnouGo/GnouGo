using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class EffectiveRequestTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing", false)]
    [InlineData("boolean", false)]
    [InlineData("extra", false)]
    [InlineData("null", false)]
    [InlineData("other-branch", false)]
    public void SelectedContractValidatesTheCompleteRequestBeforeEmission(string variant, bool valid)
    {
        var capability = new PlanningCapability { Id = "renamed", StepType = "mcp.call", InputSchema = JsonNode.Parse("""
            {"type":"object","properties":{"action":{"type":"string"},"payload":{"type":"object"}},"required":["action"],"additionalProperties":false,
             "oneOf":[{"properties":{"action":{"const":"first"},"payload":{"type":"object","properties":{"location":{"type":"string"}},"required":["location"],"additionalProperties":false}},"required":["payload"]},
                      {"properties":{"action":{"const":"second"},"payload":{"type":"object","properties":{"count":{"type":"integer"}},"required":["count"],"additionalProperties":false}},"required":["payload"]}]}
            """)!.AsObject(), OutputSchema = new() { ["type"] = "object" } };
        var value = new TaskValue { Kind = "object", Members = [new("location", new() { Kind = "input", Source = "path" })] };
        if (variant == "missing") value.Members.Clear();
        if (variant == "boolean") value.Members[0] = new("location", new() { Kind = "boolean", Boolean = true });
        if (variant == "null") value.Members[0] = new("location", new() { Kind = "null" });
        if (variant == "extra") value.Members.Add(new("unexpected", new() { Kind = "string", Text = "text" }));
        var plan = new TaskPlan { Inputs = [new() { Name = "path", Type = new() { Kind = "string" } }], Root = new() { Tasks =
            [new() { Id = "invoke", Kind = "operation", Operation = "renamed", Objective = "Consume declared data", Inputs =
                [new("action", new() { Kind = "string", Text = variant == "other-branch" ? "second" : "first" }), new("payload", value)] }] } };
        var result = new TaskPlanCompiler().Compile(plan, new() { Capabilities = [capability], AllowedStepTypes = ["mcp.call"] });
        if (valid) { Assert.Empty(result.Diagnostics); Assert.NotNull(result.Graph); }
        else { Assert.Null(result.Graph); Assert.Contains(result.Diagnostics, d => d.Code == "TASK_INPUT_TYPE" && d.Location == "/tasks/invoke/inputs"); }
    }
}
