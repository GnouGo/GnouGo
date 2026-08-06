using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class GitHubCopilotSdkPermissionTests
{
    [Fact]
    public void InteractiveResponses_MapAllowOnceRefuseAndMatchingSessionDecision()
    {
        var sessionDecision = new PermissionDecisionApproveForSession
        {
            Approval = new PermissionDecisionApproveForSessionApprovalRead()
        };

        var allowOnce = GitHubCopilotSdkClient.ResolveInteractivePermissionResponse(
            new CopilotHumanInputResponse(true, GitHubCopilotSdkClient.PermissionAllowOnceChoice),
            sessionDecision);
        var refuse = GitHubCopilotSdkClient.ResolveInteractivePermissionResponse(
            new CopilotHumanInputResponse(true, GitHubCopilotSdkClient.PermissionRefuseChoice),
            sessionDecision);
        var allowForSession = GitHubCopilotSdkClient.ResolveInteractivePermissionResponse(
            new CopilotHumanInputResponse(true, GitHubCopilotSdkClient.PermissionAllowForSessionChoice),
            sessionDecision);
        var unsupportedSessionChoice = GitHubCopilotSdkClient.ResolveInteractivePermissionResponse(
            new CopilotHumanInputResponse(true, GitHubCopilotSdkClient.PermissionAllowForSessionChoice),
            null);

        Assert.IsType<PermissionDecisionApproveOnce>(allowOnce);
        Assert.IsType<PermissionDecisionReject>(refuse);
        Assert.Same(sessionDecision, allowForSession);
        Assert.IsType<PermissionDecisionReject>(unsupportedSessionChoice);
    }

    [Fact]
    public void ShellApproval_IsScopedToDetectedCommandIdentifiers()
    {
        var request = new PermissionRequestShell
        {
            CanOfferSessionApproval = true,
            FullCommandText = "dotnet test && npm test",
            HasWriteFileRedirection = false,
            Intention = "Run tests",
            PossiblePaths = [],
            PossibleUrls = [],
            Commands =
            [
                new PermissionRequestShellCommand { Identifier = "dotnet", ReadOnly = false },
                new PermissionRequestShellCommand { Identifier = "npm", ReadOnly = false },
                new PermissionRequestShellCommand { Identifier = "dotnet", ReadOnly = false }
            ]
        };

        var supported = GitHubCopilotSdkClient.TryBuildSessionApproval(request, out var decision, out var scope);

        Assert.True(supported);
        var forSession = Assert.IsType<PermissionDecisionApproveForSession>(decision);
        var commands = Assert.IsType<PermissionDecisionApproveForSessionApprovalCommands>(forSession.Approval);
        Assert.Equal(["dotnet", "npm"], commands.CommandIdentifiers);
        Assert.Contains("dotnet, npm", scope);
    }

    [Fact]
    public void ShellApproval_IsHiddenWhenSdkDisallowsIt()
    {
        var request = new PermissionRequestShell
        {
            CanOfferSessionApproval = false,
            FullCommandText = "dotnet test",
            HasWriteFileRedirection = false,
            Intention = "Run tests",
            PossiblePaths = [],
            PossibleUrls = [],
            Commands = [new PermissionRequestShellCommand { Identifier = "dotnet", ReadOnly = false }]
        };

        Assert.False(GitHubCopilotSdkClient.TryBuildSessionApproval(request, out var decision, out var scope));
        Assert.Null(decision);
        Assert.Null(scope);
    }

    [Fact]
    public void McpApproval_IsScopedToExactServerAndTool()
    {
        var request = new PermissionRequestMcp
        {
            ServerName = "github",
            ToolName = "pull_request_read",
            ToolTitle = "Read pull request",
            ReadOnly = true
        };

        Assert.True(GitHubCopilotSdkClient.TryBuildSessionApproval(request, out var decision, out _));
        var forSession = Assert.IsType<PermissionDecisionApproveForSession>(decision);
        var approval = Assert.IsType<PermissionDecisionApproveForSessionApprovalMcp>(forSession.Approval);
        Assert.Equal("github", approval.ServerName);
        Assert.Equal("pull_request_read", approval.ToolName);
    }

    [Fact]
    public void FilesystemApprovals_UseOnlySupportedReadOrWriteCategories()
    {
        var read = new PermissionRequestRead
        {
            Path = "src/App.cs",
            Intention = "Inspect source"
        };
        var write = new PermissionRequestWrite
        {
            CanOfferSessionApproval = true,
            Diff = "@@ -1 +1 @@",
            FileName = "src/App.cs",
            Intention = "Apply a fix"
        };
        var unsafeWrite = new PermissionRequestWrite
        {
            CanOfferSessionApproval = false,
            Diff = "@@ -1 +1 @@",
            FileName = "src/App.cs",
            Intention = "Apply a fix"
        };

        Assert.True(GitHubCopilotSdkClient.TryBuildSessionApproval(read, out var readDecision, out _));
        Assert.IsType<PermissionDecisionApproveForSessionApprovalRead>(
            Assert.IsType<PermissionDecisionApproveForSession>(readDecision).Approval);
        Assert.True(GitHubCopilotSdkClient.TryBuildSessionApproval(write, out var writeDecision, out _));
        Assert.IsType<PermissionDecisionApproveForSessionApprovalWrite>(
            Assert.IsType<PermissionDecisionApproveForSession>(writeDecision).Approval);
        Assert.False(GitHubCopilotSdkClient.TryBuildSessionApproval(unsafeWrite, out _, out _));
    }

    [Fact]
    public void CustomToolApproval_IsScopedToExactToolName()
    {
        var request = new PermissionRequestCustomTool
        {
            ToolName = "run_repository_check",
            ToolDescription = "Runs a repository check"
        };

        Assert.True(GitHubCopilotSdkClient.TryBuildSessionApproval(request, out var decision, out _));
        var approval = Assert.IsType<PermissionDecisionApproveForSessionApprovalCustomTool>(
            Assert.IsType<PermissionDecisionApproveForSession>(decision).Approval);
        Assert.Equal("run_repository_check", approval.ToolName);
    }

    [Fact]
    public void UrlApproval_IsScopedToDomain()
    {
        var request = new PermissionRequestUrl
        {
            Url = "https://packages.example.test/v1/index.json",
            Intention = "Restore packages"
        };

        Assert.True(GitHubCopilotSdkClient.TryBuildSessionApproval(request, out var decision, out var scope));
        var forSession = Assert.IsType<PermissionDecisionApproveForSession>(decision);
        Assert.Null(forSession.Approval);
        Assert.Equal("packages.example.test", forSession.Domain);
        Assert.Contains("packages.example.test", scope);
    }

    [Fact]
    public void UrlApproval_IsHiddenForInvalidOrNonHttpUrl()
    {
        var request = new PermissionRequestUrl
        {
            Url = "file:///tmp/secret",
            Intention = "Read a local URL"
        };

        Assert.False(GitHubCopilotSdkClient.TryBuildSessionApproval(request, out var decision, out var scope));
        Assert.Null(decision);
        Assert.Null(scope);
    }

    [Fact]
    public void Description_IncludesCommandWarningAndSandboxBypassReason()
    {
        var request = new PermissionRequestShell
        {
            FullCommandText = "dotnet test",
            Intention = "Run the test suite",
            CanOfferSessionApproval = true,
            Commands = [new PermissionRequestShellCommand { Identifier = "dotnet", ReadOnly = false }],
            HasWriteFileRedirection = false,
            PossiblePaths = [],
            PossibleUrls = [],
            Warning = "This command may restore packages.",
            RequestSandboxBypass = true,
            RequestSandboxBypassReason = "Needs package cache access"
        };

        var details = GitHubCopilotSdkClient.DescribePermission(request);

        Assert.Contains("dotnet test", details);
        Assert.Contains("This command may restore packages.", details);
        Assert.Contains("Needs package cache access", details);
        Assert.Contains("Sandbox bypass requested: yes", details);
    }

    [Fact]
    public void Description_ExplicitlyReportsWhenSandboxBypassIsNotRequested()
    {
        var request = new PermissionRequestRead
        {
            Path = "README.md",
            Intention = "Inspect documentation",
            RequestSandboxBypass = false
        };

        var details = GitHubCopilotSdkClient.DescribePermission(request);

        Assert.Contains("Read path: README.md", details);
        Assert.Contains("Sandbox bypass requested: no", details);
    }
}
