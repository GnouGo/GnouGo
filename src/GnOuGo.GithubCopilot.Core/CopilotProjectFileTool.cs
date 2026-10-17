using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Explicit JSON-schema file tools keep real project I/O behind the host boundary.</summary>
internal sealed class CopilotProjectFileTool(string operation, ICopilotSessionFileSystem files, CopilotExecutionBounds? bounds = null, CopilotTransientSessionState? state = null) : AIFunction
{
    internal const int ReadLimit = 16_384;
    internal static readonly string[] NativeFileTools =
    ["apply_patch", "view", "rg", "glob", "edit", "create", "edit_file", "read_file", "write_file", "create_file", "str_replace_editor", "task", "read_agent", "write_agent", "list_agents"];
    private static readonly string[] Operations = ["read", "write", "append", "list", "stat", "mkdir", "remove", "rename"];
    internal static ICollection<AIFunctionDeclaration> Create(ICopilotSessionFileSystem files, CopilotExecutionBounds? bounds = null, CopilotTransientSessionState? state = null)
        => Operations.Select(op => (AIFunctionDeclaration)new CopilotProjectFileTool(op, files, bounds, state)).ToArray();
    public override string Name => "project_" + operation;
    public override string Description => operation switch
    {
        "read" => "Read a bounded text range from an allowed project file or an SDK-published virtual output log. Returns text, offset, nextOffset and truncated. Follow nextOffset for more; maxCharacters defaults to 16384 and cannot exceed it. The serialized response also has a 16384-byte cap, so escaped text may return fewer characters. SDK outputFilePath paths belong to the session filesystem, not the host shell: read them here, never with shell commands or by rerunning the original work.",
        "write" => "Create or replace one allowed UTF-8 project file with the complete content. Requires user permission and host write policy.",
        "append" => "Append UTF-8 text to one allowed project file. Requires user permission and host write policy.",
        "list" => "List allowed files and directories under a project path; use '.' for the project root.",
        "stat" => "Inspect an allowed project path's file metadata.",
        "mkdir" => "Create a project directory, with optional parent directories. Requires write permission.",
        "remove" => "Remove an allowed project file or directory, validating every descendant before recursive deletion.",
        "rename" => "Move an allowed project file or directory to a destination within the same project.",
        _ => throw new InvalidOperationException()
    };
    public override JsonElement JsonSchema
    {
        get
        {
            var extra = operation switch
            {
                "read" => ",\"offset\":{\"type\":\"integer\",\"minimum\":0,\"default\":0},\"maxCharacters\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":16384,\"default\":16384}",
                "write" or "append" => ",\"content\":{\"type\":\"string\",\"description\":\"Complete file text for write, or text to append.\"}",
                "rename" => ",\"destination\":{\"type\":\"string\"}",
                "mkdir" or "remove" => ",\"recursive\":{\"type\":\"boolean\",\"default\":false}",
                _ => ""
            };
            var required = operation is "write" or "append" ? ",\"content\"" : operation == "rename" ? ",\"destination\"" : "";
            using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"Project-relative path; project_read also accepts an exact SDK-published virtual outputFilePath.\"}" + extra + "},\"required\":[\"path\"" + required + "],\"additionalProperties\":false}");
            return document.RootElement.Clone();
        }
    }
    internal static bool IsProjectTool(string name) => Operations.Any(op => name == "project_" + op);
    internal static bool IsWrite(string name) => name is "project_write" or "project_append" or "project_mkdir" or "project_remove" or "project_rename";
    internal static string RequiredString(JsonElement args, string key)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new ArgumentException($"File operation requires '{key}'.");
    internal static JsonElement Arguments(object? args) => args is JsonElement json ? json
        : JsonSerializer.SerializeToElement(args, CopilotCoreJsonContext.Default.Object);
    internal static void Validate(string name, JsonElement args, ICopilotFileAccessPolicy policy, CopilotTransientSessionState? state = null)
    {
        var path = RequiredString(args, "path");
        if (CopilotTransientSessionState.Contains(path))
        {
            if (name != "project_read" || state is null) throw new UnauthorizedAccessException("Virtual session state is not a project path; only published output logs can be read.");
            state.ValidateOutputRead(path); return;
        }
        if (name == "project_mkdir") { policy.ValidateDirectoryWrite(path); return; }
        if (name == "project_rename") { policy.ValidateRename(path, RequiredString(args, "destination")); return; }
        if (IsWrite(name))
        {
            var content = args.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            policy.ValidateWrite(path, content);
        }
        else policy.ValidateRead(path);
    }
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken ct)
    {
        static string Text(AIFunctionArguments args, string key) => args.TryGetValue(key, out var value)
            ? value is JsonElement json ? json.GetString() ?? "" : value?.ToString() ?? "" : throw new ArgumentException($"Missing '{key}'.");
        using var admitted = bounds?.EnterFileOperation(Name);
        var path = Text(arguments, "path");
        ct.ThrowIfCancellationRequested();
        Validate(Name, Arguments(arguments), files, state);
        var recursive = arguments.TryGetValue("recursive", out var flag) && (flag is true || flag is JsonElement { ValueKind: JsonValueKind.True });
        switch (operation)
        {
            case "read":
                var offset = Integer(arguments, "offset", 0);
                var count = Integer(arguments, "maxCharacters", ReadLimit);
                if (offset < 0 || count is < 1 or > ReadLimit) throw new ArgumentException("Invalid read range: offset must be nonnegative and maxCharacters must be 1..16384.");
                var content = CopilotTransientSessionState.Contains(path) ? state!.Read(path) : await files.ReadFileAsync(path, ct);
                if (offset > content.Length) throw new ArgumentException("Read offset exceeds the file length.");
                if (offset > 0 && offset < content.Length && char.IsLowSurrogate(content[offset]) && char.IsHighSurrogate(content[offset - 1]))
                    throw new ArgumentException("Read offset splits a Unicode character.");
                var end = (int)Math.Min(content.Length, (long)offset + count);
                JsonElement Payload(int boundary) => JsonSerializer.SerializeToElement(new CopilotFileReadResult(content[offset..boundary], offset,
                    boundary < content.Length ? boundary : null, boundary < content.Length), CopilotCoreJsonContext.Default.CopilotFileReadResult);
                // The SDK serializes function results before applying its own output cap.
                // Return an object, not JSON-in-a-string, and reserve space for escaping and metadata.
                if (System.Text.Encoding.UTF8.GetByteCount(Payload(end).GetRawText()) > ReadLimit)
                {
                    var low = offset; var high = end;
                    while (low < high)
                    {
                        ct.ThrowIfCancellationRequested();
                        var middle = low + (high - low + 1) / 2;
                        if (System.Text.Encoding.UTF8.GetByteCount(Payload(middle).GetRawText()) <= ReadLimit) low = middle;
                        else high = middle - 1;
                    }
                    end = low;
                }
                if (end < content.Length && end > offset && char.IsHighSurrogate(content[end - 1]) && char.IsLowSurrogate(content[end])) end--;
                if (end == offset && end < content.Length) throw new ArgumentException("The range is too small for the next Unicode character.");
                return Payload(end);
            case "write": await files.WriteFileAsync(path, Text(arguments, "content"), null, ct); break;
            case "append": await files.AppendFileAsync(path, Text(arguments, "content"), null, ct); break;
            case "list": return JsonSerializer.Serialize(await files.ReadDirectoryAsync(path, ct), CopilotCoreJsonContext.Default.IReadOnlyListCopilotDirectoryEntry);
            case "stat": return JsonSerializer.Serialize(await files.StatAsync(path, ct), CopilotCoreJsonContext.Default.CopilotFileStat);
            case "mkdir": await files.MakeDirectoryAsync(path, recursive, null, ct); break;
            case "remove": await files.RemoveAsync(path, recursive, false, ct); break;
            case "rename": await files.RenameAsync(path, Text(arguments, "destination"), ct); break;
            default: throw new InvalidOperationException();
        }
        return "Project filesystem operation completed.";
    }

    private static int Integer(AIFunctionArguments args, string key, int fallback)
    {
        if (!args.TryGetValue(key, out var value)) return fallback;
        if (value is int number) return number;
        if (value is JsonElement json && json.TryGetInt32(out var parsed)) return parsed;
        throw new ArgumentException($"{key} must be an integer.");
    }
}

public sealed record CopilotFileReadResult(string Text, int Offset, int? NextOffset, bool Truncated);
