using System.Text.RegularExpressions;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class CopilotArchitectureTests
{
    [Fact]
    public void McpHasNoDirectSdkExecutionOrPermissionBypass()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GnOuGo.Agent.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(root.FullName, "src", "GnOuGo.GithubCopilot.Mcp");
        foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
            Assert.False(Regex.IsMatch(File.ReadAllText(path), @"\b(CopilotClient|SessionConfig|ResumeSessionConfig|CopilotSession|SessionFsProvider|PermissionHandler|MessageOptions|GitHubCopilotCodeClient|ICodeAssistantClient)\b"), Path.GetFileName(path));
    }
}
