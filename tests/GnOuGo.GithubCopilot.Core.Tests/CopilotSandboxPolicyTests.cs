using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotSandboxPolicyTests
{
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
