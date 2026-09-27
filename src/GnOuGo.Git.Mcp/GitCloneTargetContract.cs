using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Workspace;
using ModelContextProtocol.Protocol;

namespace GnOuGo.Git.Mcp;

/// <summary>Producer-owned lexical contract. Filesystem state remains GitPolicy's responsibility.</summary>
internal static partial class GitCloneTargetContract
{
    internal const string Prefix = GnOuGoWorkspace.WorkflowWorkspacesSubfolder + "/";
    // The final negative lookahead is an exact end assertion in both .NET and ECMAScript Unicode mode.
    // Unlike $, it cannot accept a trailing newline. Segments are literal names, not normalized paths.
    internal const string Pattern = "^" + Prefix + @"(?!\.{1,2}(?:/|$))[^/\\*?\x00-\x1F\x7F-\x9F]+(?:/(?!\.{1,2}(?:/|$))[^/\\*?\x00-\x1F\x7F-\x9F]+)*(?![\s\S])";
    internal const string Description = "Workspace-relative creation target below workflows/, using forward slashes, for example workflows/repository-name. Absolute paths, backslashes, empty or dot segments, parent traversal, wildcards and control characters are invalid. The target may be absent or empty; prerequisite directory creation is unnecessary. Retain this exact declared target for cleanup even if clone fails and leaves partial content. After success, use response.projectRootRelative for subsequent repository operations. .GnOuGo is reserved for internal state.";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Syntax();

    internal static bool IsValid(string? path) => path is not null && Syntax().IsMatch(path);

    internal static void Publish(Tool tool)
    {
        if (tool.Name != "git_clone") return;
        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
        var target = schema["properties"]!["targetDirectory"]!.AsObject();
        target["pattern"] = Pattern;
        target["minLength"] = Prefix.Length + 1;
        using var document = JsonDocument.Parse(schema.ToJsonString());
        tool.InputSchema = document.RootElement.Clone();
    }
}
