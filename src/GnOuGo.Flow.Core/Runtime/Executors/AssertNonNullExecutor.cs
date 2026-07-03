using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Refines nullable values into non-null values by failing early when an input is null.
/// </summary>
public sealed class AssertNonNullExecutor : IStepExecutor
{
    public string StepType => "assert.non_null";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "One or more asserted values are null. The step fails before nullable values reach downstream non-null inputs.")
    };

    public string DslSnippet => """
        ### assert.non_null — Require values before using them
        Fails if any resolved input value is null, and exposes the same fields as non-null outputs for subsequent steps.
        Use it after deriving nullable values and before passing them to strict workflow or MCP inputs.
        ```yaml
        - id: derive_repository_identity
          type: set
          output_schema:
            type: object
            properties:
              owner:
                anyOf: [{ type: string }, { type: "null" }]
              repo:
                anyOf: [{ type: string }, { type: "null" }]
            required: [owner, repo]
            additionalProperties: false
          input:
            owner: "${functions.extractOwner(data.inputs.repository_url)}"
            repo: "${functions.extractRepo(data.inputs.repository_url)}"

        - id: require_repository_identity
          type: assert.non_null
          input:
            owner: "${data.steps.derive_repository_identity.owner}"
            repo: "${data.steps.derive_repository_identity.repo}"
        ```
        Downstream strict calls should use `data.steps.require_repository_identity.owner`, not the nullable source.
        """;

    public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var input = ctx.Engine.GetResolvedInput(ctx) as JsonObject
            ?? throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "assert.non_null input must be object");

        var nullPaths = new List<string>();
        CollectNullPaths(input, "$", nullPaths);
        if (nullPaths.Count > 0)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.InputValidation,
                "assert.non_null failed: " + string.Join(", ", nullPaths.Select(static path => $"{path} is null")));
        }

        return Task.FromResult<JsonNode?>(input.DeepClone());
    }

    private static void CollectNullPaths(JsonNode? node, string path, List<string> nullPaths)
    {
        switch (node)
        {
            case null:
                nullPaths.Add(path);
                break;
            case JsonObject obj:
                foreach (var (name, value) in obj)
                    CollectNullPaths(value, $"{path}.{name}", nullPaths);
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    CollectNullPaths(array[i], $"{path}[{i}]", nullPaths);
                break;
        }
    }
}
