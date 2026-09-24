using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>Selects a declared property path from every item, preserving order and duplicates.</summary>
public sealed class ArrayProjectExecutor : IStepExecutor
{
    public string StepType => "array.project";
    public StepContract Contract => new(
        JsonNode.Parse("""{"type":"object","description":"Select path from every array item, preserving order and duplicates. Requires output_schema for the result object containing values.","required":["items","path"],"additionalProperties":false,"properties":{"items":{"type":"array"},"path":{"type":"array","items":{"type":"string"}}}}""")!.AsObject(),
        JsonNode.Parse("""{"type":"object","required":["values"],"additionalProperties":false,"properties":{"values":{"type":"array"}}}""")!.AsObject(), InputRequired: true);

    public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var input = ctx.Engine.GetResolvedInput(ctx);
        var schema = ctx.Step.Source.OutputSchema as JsonObject;
        if (schema is null || JsonSchemaContractValidator.ValidateSchema(schema, strictProfile: false).Count > 0 ||
            JsonSchemaContractValidator.ValidateInstance(input, Contract.InputSchema).Count > 0)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Array projection requires items, a property path and a valid output_schema.");
        var values = new JsonArray();
        foreach (var item in input!["items"]!.AsArray())
        {
            ct.ThrowIfCancellationRequested(); var current = item;
            foreach (var part in input["path"]!.AsArray())
            {
                if (current is not JsonObject obj || !obj.TryGetPropertyValue(part!.GetValue<string>(), out current))
                    throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "An array item is missing the declared projection path.");
            }
            values.Add(current?.DeepClone());
        }
        var result = new JsonObject { ["values"] = values };
        if (JsonSchemaContractValidator.ValidateInstance(result, schema).Count > 0)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Projected values do not satisfy the declared output contract.");
        return Task.FromResult<JsonNode?>(result);
    }
}
