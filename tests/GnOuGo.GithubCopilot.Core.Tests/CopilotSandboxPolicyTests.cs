using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotSandboxPolicyTests
{
    [Theory]
    [InlineData("view", true, false)]
    [InlineData("view", false, true)]
    [InlineData("apply_patch", true, false)]
    [InlineData("apply_patch", false, true)]
    [InlineData("powershell", true, true)]
    [InlineData("rg", true, true)]
    public void RoutedFileViewsUseTheRegisteredFilesystem(string tool, bool controlledFileSystem, bool mandatory)
    {
        var bounds = new CopilotExecutionBounds(1, 1000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string> { tool }, (_, _, _) => Task.CompletedTask);
        Assert.True(bounds.InspectsSandboxPolicy);
        Assert.Equal(mandatory, bounds.RequiresDeviceIsolation(controlledFileSystem));
    }

    [Fact]
    public async Task PatchPermissionIncludesEveryOriginalDestinationAndExpiresOnCompletion()
    {
        var actions = new CopilotAdaptiveActions((_, _) => Task.CompletedTask, new HashSet<string>());
        var bounds = new CopilotExecutionBounds(1, 1000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string> { "apply_patch" }, (_, _, _) => Task.CompletedTask);
        var root = Path.GetTempPath();
        const string patch = "*** Begin Patch\n*** Add File: summary.md\n+notes\n*** Update File: readme.md\n*** Move to: moved.md\n@@\n-old\n+new\n*** End Patch";
        Assert.Null(await actions.AdmitAsync(bounds, "session", "edit", "apply_patch", patch, root));
        var targets = await actions.NativeFileTargetsAsync("session", "edit", "apply_patch", Path.Combine(root, "summary.md"));
        Assert.Equal(new[] { "summary.md", "readme.md", "moved.md" }.Select(p => Path.Combine(root, p)), targets);
        Assert.Null(await actions.NativeFileTargetsAsync("session", "edit", "apply_patch", Path.Combine(root, "different.md")));
        await actions.CompleteAsync(bounds, "session", "edit", "apply_patch", "done", true);
        Assert.Null(await actions.NativeFileTargetsAsync("session", "edit", "apply_patch", Path.Combine(root, "summary.md")));
    }

    [Fact]
    public async Task NativeViewPermissionBelongsToTheOutstandingSessionCallAndPath()
    {
        var actions = new CopilotAdaptiveActions((_, _) => Task.CompletedTask, new HashSet<string>());
        var bounds = new CopilotExecutionBounds(1, 1000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string> { "view" }, (_, _, _) => Task.CompletedTask);
        var path = Path.GetFullPath("review.cs");
        var args = System.Text.Json.JsonSerializer.SerializeToElement(new { path });
        Assert.Null(await actions.AdmitAsync(bounds, "one", "call", "view", args));
        Assert.NotNull(await actions.NativeFileTargetsAsync("one", "call", "view", path));
        Assert.Null(await actions.NativeFileTargetsAsync("two", "call", "view", path));
        Assert.Null(await actions.NativeFileTargetsAsync("one", "another-call", "view", path));
        Assert.Null(await actions.NativeFileTargetsAsync("one", "call", "view", path + ".bak"));
        await actions.CompleteAsync(bounds, "one", "call", "view", "contents", true);
        Assert.Null(await actions.NativeFileTargetsAsync("one", "call", "view", path));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ConfiguredPolicyStillRequiresSuccessfulSessionEnforcement(bool required, bool blocked)
    {
        if (required && !blocked) GitHubCopilotSdkClient.ValidateSandboxEnforcement(required, blocked);
        else
        {
            var error = Assert.Throws<CopilotSandboxRequiredException>(() => GitHubCopilotSdkClient.ValidateSandboxEnforcement(required, blocked));
            Assert.Equal(required ? "AGENT_ISOLATION_UNAVAILABLE" : "AGENT_ISOLATION_REQUIRED", error.Code);
        }
    }

    [Theory]
    [InlineData(null, CopilotSandboxReadiness.NotConfigured)]
    [InlineData("{}", CopilotSandboxReadiness.NotConfigured)]
    [InlineData("{\"sandbox\":{}}", CopilotSandboxReadiness.NotConfigured)]
    [InlineData("{\"sandbox\":{\"enabled\":true}}", CopilotSandboxReadiness.NotConfigured)]
    [InlineData("{\"sandbox\":{\"enabled\":true,\"failIfUnavailable\":false}}", CopilotSandboxReadiness.NotConfigured)]
    [InlineData("{\"sandbox\":{\"enabled\":false,\"failIfUnavailable\":true}}", CopilotSandboxReadiness.NotConfigured)]
    [InlineData("{\"sandbox\":{\"enabled\":true,\"failIfUnavailable\":true}}", CopilotSandboxReadiness.Configured)]
    [InlineData("{\"sandbox\":{\"enabled\":\"true\",\"failIfUnavailable\":true}}", CopilotSandboxReadiness.Invalid)]
    [InlineData("{\"sandbox\":{\"enabled\":true,\"enabled\":false}}", CopilotSandboxReadiness.Invalid)]
    [InlineData("{\"sandbox\":{},\"sandbox\":{}}", CopilotSandboxReadiness.Invalid)]
    [InlineData("{\"sandbox\":null}", CopilotSandboxReadiness.Invalid)]
    [InlineData("not-json", CopilotSandboxReadiness.Invalid)]
    [InlineData("[]", CopilotSandboxReadiness.Invalid)]
    public void OnlyValidatedMandatoryPolicyAdvertisesCommands(string? json, CopilotSandboxReadiness expected)
    {
        Assert.Equal(expected, CopilotSandboxPolicy.Read(json, null));
        Assert.Equal(CopilotSandboxReadiness.Invalid, CopilotSandboxPolicy.Read(json, "private error not for display"));
    }
}
