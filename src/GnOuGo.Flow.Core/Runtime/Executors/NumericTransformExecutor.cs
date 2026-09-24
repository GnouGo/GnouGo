using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>Registered, typed numeric transformations; no generated scripts or inferred contracts.</summary>
public sealed class NumericTransformExecutor : IStepExecutor
{
    public string StepType { get; }
    public NumericTransformExecutor(string stepType)
    {
        if (stepType is not ("number.add" or "number.multiply" or "number.default"))
            throw new ArgumentException("Unknown numeric transformation.", nameof(stepType));
        StepType = stepType;
    }
    public StepContract Contract => new(
        new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("left", "right"),
            ["properties"] = new JsonObject
            {
                ["left"] = new JsonObject { ["type"] = StepType == "number.default" ? new JsonArray("number", "null") : JsonValue.Create("number") },
                ["right"] = new JsonObject { ["type"] = "number" }
            },
            ["description"] = StepType switch
            {
                "number.add" => "Add left and right.",
                "number.multiply" => "Multiply left and right.",
                _ => "Use left when non-null; otherwise use right. Preserves numeric zero."
            }
        },
        JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"number"}},"required":["value"],"additionalProperties":false}""")!.AsObject(),
        InputRequired: true);

    public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var input = ctx.Engine.GetResolvedInput(ctx);
        if (JsonSchemaContractValidator.ValidateInstance(input, Contract.InputSchema).Count != 0)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Numeric operands do not satisfy the transformation contract.");
        try
        {
            var left = input!["left"] is { } number ? decimal.Parse(number.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : (decimal?)null; var right = decimal.Parse(input["right"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
            var value = StepType switch { "number.add" => left!.Value + right, "number.multiply" => left!.Value * right, _ => left ?? right };
            return Task.FromResult<JsonNode?>(new JsonObject { ["value"] = value });
        }
        catch (Exception ex) when (ex is OverflowException or FormatException)
        { throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Numeric transformation exceeded the supported decimal range."); }
    }
}
