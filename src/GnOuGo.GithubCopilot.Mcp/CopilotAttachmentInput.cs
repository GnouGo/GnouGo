using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GnOuGo.GithubCopilot.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GnOuGo.GithubCopilot.Mcp;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CopilotFileAttachmentInput), "file")]
[JsonDerivedType(typeof(CopilotBlobAttachmentInput), "blob")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record CopilotAttachmentInput;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CopilotFileAttachmentInput(
    [property: JsonPropertyName("path"), JsonRequired, Required, RegularExpression(CopilotAttachmentContract.NonBlankPattern)]
    [property: Description("Existing file inside the configured project workspace; subject to read permission and filesystem confinement.")]
    string Path) : CopilotAttachmentInput;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CopilotBlobAttachmentInput(
    [property: JsonPropertyName("content"), JsonRequired, Required(AllowEmptyStrings = true), RegularExpression(CopilotAttachmentContract.Base64Pattern)]
    [property: Description("Base64-encoded blob content, not arbitrary JSON context.")]
    string Content,
    [property: JsonPropertyName("path"), Description("Optional display name for the blob; not a filesystem path.")] string? Path = null,
    [property: JsonPropertyName("mimeType"), Description("Optional MIME type; omission or null retains application/octet-stream.")] string? MimeType = null) : CopilotAttachmentInput;

/// <summary>Producer-owned wire validation; no session or SDK work is started here.</summary>
internal static partial class CopilotAttachmentContract
{
    internal const string Description = "Optional file/blob attachments matching the typed schema. Omit when none are needed. Business context and instructions belong in prompt, not attachments.";
    internal const string NonBlankPattern = @"[\s\S]*\S[\s\S]*";
    // End assertion is portable to JSON Schema/JavaScript and rejects trailing newlines.
    internal const string Base64Pattern = @"^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?(?![\s\S])";

    [GeneratedRegex(Base64Pattern, RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Base64Syntax();
    [GeneratedRegex(@"^\$(?:\[[0-9]+\])?(?:\.(?:type|path|content|mimeType))?$")]
    private static partial Regex SafeLocation();

    internal static IReadOnlyList<CopilotAttachment>? ToCore(IReadOnlyList<CopilotAttachmentInput>? attachments)
    {
        if (attachments is null) return null;
        var result = new List<CopilotAttachment>(attachments.Count);
        for (var i = 0; i < attachments.Count; i++)
        {
            var location = $"attachments[{i}]";
            result.Add(attachments[i] switch
            {
                CopilotFileAttachmentInput file when !string.IsNullOrWhiteSpace(file.Path) => new("file", file.Path),
                CopilotFileAttachmentInput => throw Invalid(location + ".path", "A nonblank file path is required."),
                CopilotBlobAttachmentInput blob when blob.Content is not null && IsBase64(blob.Content) => new("blob", blob.Path, blob.Content, blob.MimeType),
                CopilotBlobAttachmentInput => throw Invalid(location + ".content", "Base64 blob content is required."),
                _ => throw Invalid(location, "A file or blob attachment object is required.")
            });
        }
        return result;
    }

    private static bool IsBase64(string content)
    {
        try { return Base64Syntax().IsMatch(content); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    internal static void ValidateArguments(CallToolRequestParams request)
    {
        if (!IsAttachmentTool(request.Name)) return;
        var args = request.Arguments;
        if (args?.ContainsKey("attachmentsJson") == true)
            throw Invalid("attachmentsJson", "This argument was removed. Use typed attachments and regenerate/reapprove affected workflows.");
        if (args is null || !args.TryGetValue("attachments", out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.Array)
            throw Invalid("attachments", "An array of file/blob attachment objects is required.");
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String || type.GetString() is not ("file" or "blob"))
                throw Invalid($"attachments[{index}].type", "Declare file or blob attachment type.");
            index++;
        }
        try
        {
            ToCore(JsonSerializer.Deserialize(value, CodeMcpJsonContext.Default.IReadOnlyListCopilotAttachmentInput));
        }
        catch (JsonException error)
        {
            var suffix = error.Path is { } path && SafeLocation().IsMatch(path) ? path[1..] : "";
            throw Invalid("attachments" + suffix, "Expected an array of file/blob objects matching the declared attachment schema.");
        }
    }

    internal static bool IsAttachmentTool(string name)
        => name is "copilot_one_shot" or "copilot_interactive_one_shot" or "copilot_session_send";

    internal static JsonElement InputSchema(JsonElement generated)
    {
        var schema = JsonNode.Parse(generated.GetRawText())!.AsObject();
        var items = schema["properties"]?["attachments"]?["items"] as JsonObject
            ?? throw new InvalidOperationException("The generated attachment array schema is missing.");
        // Generic reference-type schema export includes null even for non-null array items
        // and required constructor members. Publish the producer's enforced contract.
        items["type"] = "object";
        foreach (var variant in items["anyOf"]!.AsArray())
        {
            var properties = variant!["properties"]!.AsObject();
            var field = properties["type"]!["const"]!.GetValue<string>() switch
            {
                "file" => "path",
                "blob" => "content",
                _ => throw new InvalidOperationException("Unknown generated attachment variant.")
            };
            properties[field]!["type"] = "string";
        }
        return JsonSerializer.SerializeToElement(schema, CodeMcpJsonContext.Default.JsonObject);
    }

    internal static void Configure(McpServerOptions options)
    {
        options.Filters.Request.ListToolsFilters.Add(next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            foreach (var tool in result.Tools)
                if (IsAttachmentTool(tool.Name)) tool.InputSchema = InputSchema(tool.InputSchema);
            return result;
        });
        options.Filters.Request.CallToolFilters.Add(next => async (request, cancellationToken) =>
        {
            try { ValidateArguments(request.Params); }
            catch (McpException error)
            {
                var json = JsonSerializer.SerializeToElement(new CodeErrorResult("INVALID_INPUT", error.Message), CodeMcpJsonContext.Default.CodeErrorResult);
                return new CallToolResult { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
            }
            return await next(request, cancellationToken);
        });
    }

    private static McpException Invalid(string location, string message) => new($"{location}: {message}");
}
