using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Explicit JSON-schema file tools keep real project I/O behind the host boundary.</summary>
internal sealed class CopilotProjectFileTool(string operation, ICopilotSessionFileSystem files) : AIFunction
{
    internal static readonly string[] NativeFileTools =
    ["apply_patch", "view", "rg", "glob", "edit", "create", "edit_file", "read_file", "write_file", "create_file", "str_replace_editor", "task", "read_agent", "write_agent", "list_agents"];
    private static readonly string[] Operations = ["read", "write", "append", "list", "stat", "mkdir", "remove", "rename"];
    internal static ICollection<AIFunctionDeclaration> Create(ICopilotSessionFileSystem files)
        => Operations.Select(op => (AIFunctionDeclaration)new CopilotProjectFileTool(op, files)).ToArray();
    public override string Name => "project_" + operation;
    public override string Description => operation switch
    {
        "read" => "Read one allowed UTF-8 project file through the host filesystem policy.",
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
                "write" or "append" => ",\"content\":{\"type\":\"string\",\"description\":\"Complete file text for write, or text to append.\"}",
                "rename" => ",\"destination\":{\"type\":\"string\"}",
                "mkdir" or "remove" => ",\"recursive\":{\"type\":\"boolean\",\"default\":false}",
                _ => ""
            };
            var required = operation is "write" or "append" ? ",\"content\"" : operation == "rename" ? ",\"destination\"" : "";
            using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"Path relative to the project root.\"}" + extra + "},\"required\":[\"path\"" + required + "],\"additionalProperties\":false}");
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
    internal static void Validate(string name, JsonElement args, ICopilotFileAccessPolicy policy)
    {
        var path = RequiredString(args, "path");
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
        var path = Text(arguments, "path");
        var recursive = arguments.TryGetValue("recursive", out var flag) && (flag is true || flag is JsonElement { ValueKind: JsonValueKind.True });
        switch (operation)
        {
            case "read": return await files.ReadFileAsync(path, ct);
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
}
