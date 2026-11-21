using System.Text.Json;
using Microsoft.Extensions.AI;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>A task-local SDK function whose arguments and result use the host MCP contract.</summary>
public sealed record CopilotHostTool(string Name, string Description, JsonElement InputSchema,
    Func<JsonElement, CancellationToken, Task<JsonElement>> Invoke);

internal sealed class CopilotHostFunction(CopilotHostTool tool, CopilotExecutionBounds bounds) : AIFunction
{
    public override string Name => tool.Name;
    public override string Description => tool.Description;
    public override JsonElement JsonSchema => tool.InputSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        using var operation = bounds.EnterHostOperation(tool.Name);
        var input = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in arguments)
            input[key] = JsonSerializer.SerializeToNode(value, CopilotCoreJsonContext.Default.Object);
        var parameters = JsonSerializer.SerializeToElement(input, CopilotCoreJsonContext.Default.JsonObject);
        var admissions = bounds.AdaptiveActions ?? throw new InvalidOperationException("Host tools require durable action admission.");
        var admission = await admissions.ClaimHostToolAsync(tool.Name, parameters, cancellationToken);
        var result = await tool.Invoke(parameters, cancellationToken);
        await admissions.RecordHostResultAsync(admission, input, result);
        return result;
    }
}
