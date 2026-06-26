using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Sets one or more variables in the workflow data context.
/// Each key in the input becomes a field in the step output, with its expression evaluated.
/// Variables are then accessible via data.steps.&lt;step_id&gt;.&lt;key&gt; by subsequent steps.
///
/// Input:
///   Any key-value pairs where values can be literals or ${...} expressions.
///
/// Output:
///   A JsonObject containing all the evaluated key-value pairs.
///
/// Example YAML:
///   - id: init
///     type: set
///     input:
///       base_url: "https://api.example.com"
///       max_retries: 3
///       full_name: "${data.inputs.first} ${data.inputs.last}"
///       items_count: "${len(data.inputs.items)}"
/// </summary>
public sealed class SetExecutor : IStepExecutor
{
    public string StepType => "set";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The resolved input for `set` must be an object and must satisfy `output_schema` when one is declared.")
    };

    public string DslSnippet => """
        ### set — Set or compute variables
        Assigns one or more variables. Each key in input is evaluated (supports ${...} expressions) and stored in the step output.
        Access results via data.steps.<step_id>.<key> in subsequent steps.
        ```yaml
        - id: vars
          type: set
          output_schema:
            type: object
            properties:
              base_url: { type: string }
              max_retries: { type: integer }
              full_name: { type: string }
              items_count: { type: integer }
              is_admin: { type: boolean }
            required: [base_url, max_retries, full_name, items_count, is_admin]
            additionalProperties: false
          input:
            base_url: "https://api.example.com"
            max_retries: 3
            full_name: "${data.inputs.first} ${data.inputs.last}"
            items_count: "${len(data.inputs.items)}"
            is_admin: "${data.inputs.role == \"admin\"}"
        ```
        Output: `{ base_url: "https://api.example.com", max_retries: 3, full_name: "John Doe", items_count: 4, is_admin: true }`
        Use multiple set steps to update variables at different points in the workflow.
        """;

    public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "set input must be object");

        // The input is already resolved by the engine (ResolveDeep),
        // so all ${...} expressions have been evaluated.
        // We simply return the resolved input as the step output.
        var result = new JsonObject();
        foreach (var kv in input)
        {
            result[kv.Key] = kv.Value?.DeepClone();
        }

        if (ctx.Step.Source.OutputSchema != null)
        {
            var errors = JsonSchemaContractValidator.ValidateInstance(result, ctx.Step.Source.OutputSchema);
            if (errors.Count > 0)
            {
                throw new WorkflowRuntimeException(
                    ErrorCodes.InputValidation,
                    "set output does not satisfy output_schema: " + string.Join("; ", errors));
            }
        }

        return Task.FromResult<JsonNode?>(result);
    }
}
