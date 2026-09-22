using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>Establishes a runtime contract for an opaque whole value; never supplies missing evidence.</summary>
public sealed class ValidateValueExecutor : IStepExecutor
{
    public string StepType => "value.validate";
    public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "value.validate requires object input.");
        if (!input.ContainsKey("value")) throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "value.validate requires a value, including explicit null.");
        var schema = ctx.Step.Source.OutputSchema as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "value.validate requires a literal output_schema.");
        JsonNode? value = input["value"]?.DeepClone();
        var format = input["format"]?.GetValue<string>() ?? "json_value";
        if (format == "json_text")
        {
            if (value is not JsonValue text || !text.TryGetValue<string>(out var json))
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "The JSON-text adapter requires a string.");
            try { value = JsonNode.Parse(json); }
            catch (JsonException) { throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "The adapter received invalid JSON text."); }
        }
        else if (format != "json_value") throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Unknown value validation format.");
        var result = new JsonObject { ["value"] = value };
        if (JsonSchemaContractValidator.ValidateSchema(schema, strictProfile: false).Count != 0 || JsonSchemaContractValidator.ValidateInstance(result, schema).Count != 0)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "The value does not satisfy the adapter's declared output contract.");
        return Task.FromResult<JsonNode?>(result);
    }
}
