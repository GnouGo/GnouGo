using System.Text.Json.Nodes;
using GnOuGo.Cmd.Mcp;
using Xunit;

namespace GnOuGo.Cmd.Mcp.Tests;

public class CommandPolicyTests
{
    [Fact]
    public void ResolveWorkingDirectory_UsesConfiguredDefaultWorkingDirectoryAndCreatesIt()
    {
        var contentRoot = CreateTempDirectory();
        var defaultRoot = Path.Combine(contentRoot, "workspace", Guid.NewGuid().ToString("N"));
        var settings = new CmdServerSettings
        {
            DefaultWorkingDirectory = defaultRoot,
            AllowedShells = ["powershell", "sh"],
            AllowedCommands = new Dictionary<string, AllowedCommandSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["test"] = new()
                {
                    Shell = "powershell",
                    Script = "Write-Output 'ok'"
                }
            }
        };

        var policy = new CommandPolicy(settings, contentRoot);

        var resolved = policy.ResolveWorkingDirectory(null);

        Assert.Equal(Path.GetFullPath(defaultRoot), resolved);
        Assert.True(Directory.Exists(resolved));
    }

    [Fact]
    public void DescribePolicy_IncludesDefaultWorkingDirectoryInAllowedRoots()
    {
        var contentRoot = CreateTempDirectory();
        var defaultRoot = Path.Combine(contentRoot, "workspace", Guid.NewGuid().ToString("N"));
        var policy = new CommandPolicy(new CmdServerSettings
        {
            DefaultWorkingDirectory = defaultRoot,
            AllowedShells = ["powershell", "sh"],
            AllowedCommands = new Dictionary<string, AllowedCommandSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["test"] = new()
                {
                    Shell = "powershell",
                    Script = "Write-Output 'ok'"
                }
            }
        }, contentRoot);

        var info = policy.DescribePolicy();

        Assert.Contains(Path.GetFullPath(defaultRoot), info.AllowedWorkingRoots, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(".", info.DefaultWorkingDirectoryRelative);
        Assert.Contains(".", info.AllowedWorkingRootsRelative ?? []);
    }

    [Fact]
    public void BuildCmdRunToolDescription_IncludesAllowedCommandNamesAndParameters()
    {
        var root = CreateTempDirectory();
        var policy = new CommandPolicy(new CmdServerSettings
        {
            DefaultWorkingDirectory = root,
            AllowedShells = ["powershell", "sh"],
            AllowedCommands = new Dictionary<string, AllowedCommandSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["list_files"] = new()
                {
                    Description = "List workspace files.",
                    Shell = "powershell",
                    Script = "Get-ChildItem {{path}}",
                    Parameters = new Dictionary<string, CommandParameterSettings>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["path"] = new()
                        {
                            Description = "Directory to list.",
                            Required = true,
                            IsWorkspacePath = true,
                            PathKind = WorkspacePathKind.Directory
                        }
                    }
                },
                ["echo_test"] = new()
                {
                    Shell = "powershell",
                    Script = "Write-Output 'ok'"
                }
            }
        }, root);

        var description = policy.BuildCmdRunToolDescription();

        Assert.Contains("Allowed commandName values:", description);
        Assert.Contains("- echo_test", description);
        Assert.Contains("Parameters: none; omit parametersJson.", description);
        Assert.Contains("- list_files: List workspace files.", description);
        Assert.Contains("path (required, workspace path, directory, Directory to list.)", description);
        Assert.Contains("Pass parametersJson as a JSON object string", description);
        Assert.DoesNotContain("Get-ChildItem", description);
    }

    [Fact]
    public void BuildListAllowedCommandsToolDescription_IncludesOutputShapeAndLiveAllowlist()
    {
        var root = CreateTempDirectory();
        var policy = new CommandPolicy(new CmdServerSettings
        {
            DefaultWorkingDirectory = root,
            AllowedShells = ["powershell", "sh"],
            AllowedCommands = new Dictionary<string, AllowedCommandSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["list_files"] = new()
                {
                    Description = "List workspace files.",
                    Shell = "powershell",
                    Script = "Get-ChildItem {{path}}",
                    Parameters = new Dictionary<string, CommandParameterSettings>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["path"] = new() { Required = true, IsWorkspacePath = true }
                    }
                }
            }
        }, root);

        var description = policy.BuildListAllowedCommandsToolDescription();

        Assert.Contains("Output shape:", description);
        Assert.Contains("\"commands\"", description);
        Assert.Contains("- list_files: List workspace files.", description);
        Assert.Contains("shell=powershell", description);
        Assert.Contains($"workingDirectory={Path.GetFullPath(root)}", description);
        Assert.Contains("parameters=path", description);
        Assert.Contains("For frozen workflow.plan requests, call cmd_run directly", description);
        Assert.DoesNotContain("Get-ChildItem", description);

        var command = Assert.Single(policy.ListAllowedCommands());
        Assert.Equal(".", command.WorkingDirectoryRelative);
    }

    [Fact]
    public void BuildGetPolicyToolDescription_IncludesOutputShapeAndLivePolicy()
    {
        var root = CreateTempDirectory();
        var policy = CreatePolicy(root, commandScript: "Write-Output 'ok'", maxTimeoutMs: 45000);

        var description = policy.BuildGetPolicyToolDescription();

        Assert.Contains("Output shape:", description);
        Assert.Contains("\"allowedShells\"", description);
        Assert.Contains("- allowedWorkingRoots:", description);
        Assert.Contains(Path.GetFullPath(root), description);
        Assert.Contains("- maxTimeoutMs: 45000", description);
        Assert.Contains("- allowedCommandCount: 1", description);
        Assert.Contains("- environment.operatingSystem:", description);
        Assert.Contains("- environment.availableShells:", description);
        Assert.Contains("Only allowedWorkingRoots are authorized", description);
    }

    [Fact]
    public void ResolveWorkingDirectory_AllowsChildDirectoryInsideAllowedRoot()
    {
        var root = CreateTempDirectory();
        var child = Directory.CreateDirectory(Path.Combine(root, "child"));
        var policy = CreatePolicy(root, commandScript: "Write-Output 'ok'");

        var resolved = policy.ResolveWorkingDirectory(child.FullName);

        Assert.Equal(child.FullName, resolved);
    }

    [Fact]
    public void ResolveWorkingDirectory_RejectsPathOutsideAllowedRoots()
    {
        var root = CreateTempDirectory();
        var outside = CreateTempDirectory();
        var policy = CreatePolicy(root, commandScript: "Write-Output 'ok'");

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveWorkingDirectory(outside));

        Assert.Contains("outside the allowed roots", ex.Message);
    }

    [Fact]
    public void RenderScript_EscapesPowerShellParameters()
    {
        var root = CreateTempDirectory();
        var settings = CreateSettings(root, "Write-Output {{value}}", parameters: new Dictionary<string, CommandParameterSettings>
        {
            ["value"] = new() { Required = true, Pattern = "^[A-Za-z' ]+$", MaxLength = 50 }
        });
        var policy = new CommandPolicy(settings, root);
        var command = policy.GetRequiredCommand("test");

        var rendered = policy.RenderScript(command, new JsonObject { ["value"] = "O'Brien" }, root);

        Assert.Equal("Write-Output 'O''Brien'", rendered);
    }

    [Fact]
    public void RenderScript_NormalizesWorkspacePathParametersToAbsolutePathsInsideWorkspace()
    {
        var root = CreateTempDirectory();
        var settings = CreateSettings(root, "Get-ChildItem {{path}}", parameters: new Dictionary<string, CommandParameterSettings>
        {
            ["path"] = new()
            {
                Required = true,
                Pattern = "^[A-Za-z0-9_.\\/-]{1,120}$",
                MaxLength = 120,
                IsWorkspacePath = true
            }
        });
        var policy = new CommandPolicy(settings, root);
        var command = policy.GetRequiredCommand("test");

        var rendered = policy.RenderScript(command, new JsonObject { ["path"] = "notes/today.md" }, root);

        Assert.Equal($"Get-ChildItem '{Path.Combine(root, "notes", "today.md")}'", rendered);
    }

    [Fact]
    public void RenderScript_RejectsWorkspacePathParameterContainingParentTraversal()
    {
        var root = CreateTempDirectory();
        var settings = CreateSettings(root, "Get-ChildItem {{path}}", parameters: new Dictionary<string, CommandParameterSettings>
        {
            ["path"] = new()
            {
                Required = true,
                Pattern = "^[A-Za-z0-9_.\\/-]{1,120}$",
                MaxLength = 120,
                IsWorkspacePath = true
            }
        });
        var policy = new CommandPolicy(settings, root);
        var command = policy.GetRequiredCommand("test");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            policy.RenderScript(command, new JsonObject { ["path"] = "../secrets.txt" }, root));

        Assert.Contains("must not contain parent directory traversal segments", ex.Message);
    }

    [Fact]
    public void RenderScript_RejectsWorkspacePathParameterWhenAbsolutePathsAreNotAllowed()
    {
        var root = CreateTempDirectory();
        var settings = CreateSettings(root, "Get-ChildItem {{path}}", parameters: new Dictionary<string, CommandParameterSettings>
        {
            ["path"] = new()
            {
                Required = true,
                Pattern = "^.+$",
                MaxLength = 260,
                IsWorkspacePath = true
            }
        });
        var policy = new CommandPolicy(settings, root);
        var command = policy.GetRequiredCommand("test");
        var absolutePath = Path.Combine(root, "notes");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            policy.RenderScript(command, new JsonObject { ["path"] = absolutePath }, root));

        Assert.Contains("must be a relative path inside the workspace", ex.Message);
    }

    [Fact]
    public void RenderScript_RejectsUnknownParameter()
    {
        var root = CreateTempDirectory();
        var policy = CreatePolicy(root, commandScript: "Write-Output 'ok'");
        var command = policy.GetRequiredCommand("test");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            policy.RenderScript(command, new JsonObject { ["extra"] = "value" }, root));

        Assert.Contains("not declared", ex.Message);
    }

    [Fact]
    public void RenderScript_AcceptsLegacyArgsAlias_ForSingleParameterCommand()
    {
        var root = CreateTempDirectory();
        var settings = CreateSettings(root, "Get-ChildItem {{path}}", parameters: new Dictionary<string, CommandParameterSettings>
        {
            ["path"] = new()
            {
                Required = true,
                Pattern = "^[A-Za-z0-9_.\\/-]{1,120}$",
                MaxLength = 120,
                IsWorkspacePath = true
            }
        });
        var policy = new CommandPolicy(settings, root);
        var command = policy.GetRequiredCommand("test");

        var rendered = policy.RenderScript(command, new JsonObject { ["args"] = "notes/today.md" }, root);

        Assert.Equal($"Get-ChildItem '{Path.Combine(root, "notes", "today.md")}'", rendered);
    }

    [Fact]
    public void RenderScript_RejectsLegacyArgsAlias_ForMultiParameterCommand()
    {
        var root = CreateTempDirectory();
        var settings = CreateSettings(root, "Copy-Item {{source}} {{target}}", parameters: new Dictionary<string, CommandParameterSettings>
        {
            ["source"] = new() { Required = true, Pattern = "^.+$", MaxLength = 120 },
            ["target"] = new() { Required = true, Pattern = "^.+$", MaxLength = 120 }
        });
        var policy = new CommandPolicy(settings, root);
        var command = policy.GetRequiredCommand("test");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            policy.RenderScript(command, new JsonObject { ["args"] = "notes" }, root));

        Assert.Contains("Provide declared parameter names", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveTimeoutMs_ClampsRequestedTimeoutToPolicyMaximum()
    {
        var root = CreateTempDirectory();
        var policy = CreatePolicy(root, commandScript: "Write-Output 'ok'", maxTimeoutMs: 5000);
        var command = policy.GetRequiredCommand("test");

        var timeout = policy.ResolveTimeoutMs(command, requestedTimeoutMs: 99999);

        Assert.Equal(5000, timeout);
    }

    private static CommandPolicy CreatePolicy(string root, string commandScript, int maxTimeoutMs = 30000)
        => new(CreateSettings(root, commandScript, maxTimeoutMs: maxTimeoutMs), root);

    private static CmdServerSettings CreateSettings(
        string root,
        string commandScript,
        Dictionary<string, CommandParameterSettings>? parameters = null,
        int maxTimeoutMs = 30000)
        => new()
        {
            MaxTimeoutMs = maxTimeoutMs,
            AllowedShells = ["powershell", "sh"],
            AllowedWorkingRoots = [root],
            AllowedCommands = new Dictionary<string, AllowedCommandSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["test"] = new()
                {
                    Shell = "powershell",
                    Script = commandScript,
                    WorkingDirectory = root,
                    Parameters = parameters ?? new Dictionary<string, CommandParameterSettings>(StringComparer.OrdinalIgnoreCase)
                }
            }
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "GnOuGo.Cmd.Mcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
