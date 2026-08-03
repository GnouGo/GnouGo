using GnOuGo.GithubCopilot.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text.Json;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class McpCopilotHumanInputProvider : ICopilotHumanInputProvider
{
    private readonly AsyncLocal<McpServer?> _currentServer = new();

    public IDisposable Push(McpServer server)
    {
        var previous = _currentServer.Value;
        _currentServer.Value = server;
        return new Scope(this, previous);
    }

    public async Task<CopilotHumanInputResponse> RequestAsync(CopilotHumanInputRequest request, CancellationToken cancellationToken)
    {
        var server = _currentServer.Value
            ?? throw new InvalidOperationException("Interactive Copilot callbacks require an active MCP request context.");
        var schema = request.RequestedSchema is { } requestedSchema
            ? ConvertSchema(requestedSchema)
            : BuildAnswerSchema(request);
        var result = await server.ElicitAsync(new ElicitRequestParams
        {
            Mode = "form",
            Message = request.Prompt,
            RequestedSchema = schema
        }, cancellationToken);

        var answer = result.Content is not null
            && result.Content.TryGetValue("answer", out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        var accepted = result.IsAccepted
            && !string.Equals(answer, "deny", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(answer, "cancel", StringComparison.OrdinalIgnoreCase);
        var content = result.Content is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(result.Content, CodeMcpJsonContext.Default.DictionaryStringJsonElement);
        return new CopilotHumanInputResponse(accepted, answer, request.AllowFreeform, content);
    }

    private static ElicitRequestParams.RequestSchema BuildAnswerSchema(CopilotHumanInputRequest request)
    {
        var choices = request.Choices.Count > 0 ? request.Choices : ["continue", "cancel"];
        return new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
            {
                ["answer"] = request.AllowFreeform
                    ? new ElicitRequestParams.StringSchema { Description = request.Details }
                    : new ElicitRequestParams.UntitledSingleSelectEnumSchema { Enum = choices.ToList(), Description = request.Details }
            },
            Required = ["answer"]
        };
    }

    private static ElicitRequestParams.RequestSchema ConvertSchema(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Copilot elicitation requires an MCP-compatible object schema.");

        var converted = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
            converted[property.Name] = ConvertProperty(property.Value);

        var required = source.TryGetProperty("required", out var requiredNode) && requiredNode.ValueKind == JsonValueKind.Array
            ? requiredNode.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()!).ToList()
            : [];
        return new ElicitRequestParams.RequestSchema { Properties = converted, Required = required };
    }

    private static ElicitRequestParams.PrimitiveSchemaDefinition ConvertProperty(JsonElement source)
    {
        var description = ReadString(source, "description");
        if (source.TryGetProperty("enum", out var enumNode) && enumNode.ValueKind == JsonValueKind.Array)
        {
            var values = enumNode.EnumerateArray().Where(static value => value.ValueKind == JsonValueKind.String).Select(static value => value.GetString()!).ToList();
            return new ElicitRequestParams.UntitledSingleSelectEnumSchema { Enum = values, Description = description };
        }

        return ReadString(source, "type") switch
        {
            "boolean" => new ElicitRequestParams.BooleanSchema { Description = description },
            "integer" => new ElicitRequestParams.NumberSchema { Type = "integer", Description = description },
            "number" => new ElicitRequestParams.NumberSchema { Description = description },
            _ => new ElicitRequestParams.StringSchema { Description = description }
        };
    }

    private static string? ReadString(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class Scope(McpCopilotHumanInputProvider owner, McpServer? previous) : IDisposable
    {
        public void Dispose() => owner._currentServer.Value = previous;
    }
}
