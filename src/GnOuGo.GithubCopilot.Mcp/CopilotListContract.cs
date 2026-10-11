using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GnOuGo.GithubCopilot.Mcp;

/// <summary>Native list contracts, enforced before tool binding or any session work.</summary>
internal static class CopilotListContract
{
    private static IReadOnlyList<string> Parameters(string tool) => tool switch
    {
        "copilot_session_create" => ["permissionAllowlist", "availableTools", "excludedTools", "skillDirectories", "disabledSkills"],
        "copilot_one_shot" or "copilot_interactive_one_shot" => ["permissionAllowlist"],
        "code_suggest_change" or "code_agent_edit" => ["contextFiles"],
        _ => []
    };

    internal static IReadOnlyList<string>? Normalize(IReadOnlyList<string>? values, string parameter, StringComparer? comparer = null)
    {
        if (values is null) return null;
        for (var index = 0; index < values.Count; index++)
            if (string.IsNullOrWhiteSpace(values[index])) throw Invalid($"{parameter}[{index}]", "A nonblank string is required.");
        return values.Distinct(comparer ?? StringComparer.Ordinal).ToArray();
    }

    internal static void ValidateArguments(CallToolRequestParams request)
    {
        var args = request.Arguments;
        if (args is null) return;
        var parameters = Parameters(request.Name);
        // Check all removed arguments first, even when a replacement is also present.
        foreach (var parameter in parameters)
            if (args.ContainsKey(parameter + "Json"))
                throw Invalid(parameter + "Json", $"This argument was removed. Use the native {parameter} array, refresh discovery, and regenerate/reapprove affected workflows.");
        foreach (var parameter in parameters)
        {
            if (!args.TryGetValue(parameter, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.Array) throw Invalid(parameter, "An array of nonblank strings is required.");
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    throw Invalid($"{parameter}[{index}]", "A nonblank string is required.");
                index++;
            }
        }
    }

    internal static void Publish(Tool tool)
    {
        var parameters = Parameters(tool.Name);
        if (parameters.Count == 0) return;
        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
        foreach (var parameter in parameters)
        {
            var property = schema["properties"]?[parameter] as JsonObject
                ?? throw new InvalidOperationException($"The generated {parameter} array schema is missing.");
            // The SDK includes null in reference-type items. Match our enforced contract.
            property["items"] = new JsonObject { ["type"] = "string", ["pattern"] = @"\S" };
        }
        tool.InputSchema = JsonSerializer.SerializeToElement(schema, CodeMcpJsonContext.Default.JsonObject);
    }

    internal static void Configure(McpServerOptions options)
    {
        options.Filters.Request.ListToolsFilters.Add(next => async (request, ct) =>
        {
            var result = await next(request, ct);
            foreach (var tool in result.Tools) Publish(tool);
            return result;
        });
        options.Filters.Request.CallToolFilters.Add(next => async (request, ct) =>
        {
            try { ValidateArguments(request.Params); }
            catch (ArgumentException error)
            {
                var json = JsonSerializer.SerializeToElement(new CodeErrorResult("INVALID_INPUT", error.Message), CodeMcpJsonContext.Default.CodeErrorResult);
                return new CallToolResult { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
            }
            return await next(request, ct);
        });
    }

    private static ArgumentException Invalid(string location, string message) => new($"{location}: {message}");
}
