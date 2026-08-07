using GnOuGo.GithubCopilot.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text.Json;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class McpCopilotHumanInputProvider : ICopilotHumanInputProvider
{
    private readonly AsyncLocal<RequestContext?> _currentRequest = new();
    private readonly CodeProgressReporter _progress;
    private readonly CodeMcpTraceContextAccessor _traceContext;

    public McpCopilotHumanInputProvider(
        CodeProgressReporter progress,
        CodeMcpTraceContextAccessor traceContext)
    {
        _progress = progress;
        _traceContext = traceContext;
    }

    public IDisposable Push(McpServer server, CancellationToken cancellationToken = default)
    {
        var previous = _currentRequest.Value;
        _currentRequest.Value = new RequestContext(
            server,
            cancellationToken,
            CodeMcpTraceContext.Capture(_traceContext));
        return new Scope(this, previous);
    }

    public async Task<CopilotHumanInputResponse> RequestAsync(CopilotHumanInputRequest request, CancellationToken cancellationToken)
    {
        var current = _currentRequest.Value
            ?? throw new InvalidOperationException("Interactive Copilot callbacks require an active MCP request context.");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            current.CancellationToken);
        var schema = request.RequestedSchema is { } requestedSchema
            ? ConvertSchema(requestedSchema)
            : BuildAnswerSchema(request);
        var progressKind = NormalizeProgressKind(request.Kind);
        _progress.Report(
            progressKind + ".requested",
            "thinking",
            BuildWaitingMessage(request.Kind),
            fallbackServer: "GnOuGo.GithubCopilot.Mcp",
            fallbackMethod: "copilot_interactive_one_shot",
            fallbackMcpKind: "tool");

        ElicitResult result;
        try
        {
            result = await current.Server.ElicitAsync(new ElicitRequestParams
            {
                Mode = "form",
                Message = request.Prompt,
                RequestedSchema = schema,
                Meta = current.TraceContext?.ToMcpMeta()
            }, linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _progress.Report(
                progressKind + ".cancelled",
                "warning",
                BuildCancelledMessage(request.Kind),
                fallbackServer: "GnOuGo.GithubCopilot.Mcp",
                fallbackMethod: "copilot_interactive_one_shot",
                fallbackMcpKind: "tool");
            throw;
        }

        var answer = result.Content is not null
            && result.Content.TryGetValue("answer", out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        var accepted = result.IsAccepted
            && !string.Equals(answer, "deny", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(answer, "refuse", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(answer, "cancel", StringComparison.OrdinalIgnoreCase);
        var content = result.Content is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(result.Content, CodeMcpJsonContext.Default.DictionaryStringJsonElement);
        _progress.Report(
            progressKind + ".completed",
            accepted ? "info" : "warning",
            BuildCompletedMessage(request.Kind, accepted),
            fallbackServer: "GnOuGo.GithubCopilot.Mcp",
            fallbackMethod: "copilot_interactive_one_shot",
            fallbackMcpKind: "tool");
        return new CopilotHumanInputResponse(accepted, answer, request.AllowFreeform, content);
    }

    private static string NormalizeProgressKind(string? kind)
        => string.IsNullOrWhiteSpace(kind)
            ? "human_input"
            : "human_input." + kind.Trim().ToLowerInvariant().Replace(' ', '_');

    private static string BuildWaitingMessage(string? kind)
        => string.Equals(kind, "permission", StringComparison.OrdinalIgnoreCase)
            ? "Copilot is waiting for permission."
            : "Copilot is waiting for human input.";

    private static string BuildCancelledMessage(string? kind)
        => string.Equals(kind, "permission", StringComparison.OrdinalIgnoreCase)
            ? "The Copilot permission request was cancelled."
            : "The Copilot human-input request was cancelled.";

    private static string BuildCompletedMessage(string? kind, bool accepted)
        => string.Equals(kind, "permission", StringComparison.OrdinalIgnoreCase)
            ? accepted ? "Copilot permission was answered." : "Copilot permission was refused."
            : accepted ? "Copilot human input was received." : "Copilot human input was refused.";

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

    private sealed record RequestContext(
        McpServer Server,
        CancellationToken CancellationToken,
        CodeMcpTraceContext? TraceContext);

    private sealed class Scope(McpCopilotHumanInputProvider owner, RequestContext? previous) : IDisposable
    {
        public void Dispose() => owner._currentRequest.Value = previous;
    }
}
