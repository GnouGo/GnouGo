using Microsoft.Extensions.Configuration;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class CodeServerSettingsOptionsConfiguratorTests
{
    [Fact]
    public void Configure_AppliesConfiguredValuesWithoutConfigurationBinder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Code:DefaultWorkingDirectory"] = "C:/work",
                ["Code:MaxFileSizeBytes"] = "1024",
                ["Code:MaxSearchResults"] = "12",
                ["Code:MaxPromptCharacters"] = "2048",
                ["Code:AllowWrites"] = "true",
                ["Code:AllowedWorkingRoots:0"] = "C:/work",
                ["Code:AllowedWorkingRoots:1"] = "D:/repo",
                ["Code:AllowedExtensions:0"] = ".cs",
                ["Code:AllowedExtensions:1"] = ".md",
                ["Code:Copilot:Provider"] = "anthropic",
                ["Code:Copilot:Model"] = "claude-sonnet-4-20250514",
                ["Code:Copilot:Mode"] = "agent",
                ["Code:Copilot:ReasoningEffort"] = "medium",
                ["Code:Copilot:Endpoint"] = "https://api.anthropic.com/v1",
                ["Code:Copilot:ApiKey"] = "secret",
                ["Code:Copilot:UseLoggedInUser"] = "true",
                ["Code:Copilot:ForwardTraceContext"] = "false",
                ["Code:Copilot:LogLevel"] = "debug",
                ["Code:Copilot:RequestTimeoutSeconds"] = "42",
                ["Code:Copilot:ManagedSessionTtlSeconds"] = "84",
                ["Code:Copilot:EnableApproveAll"] = "true",
                ["Code:Copilot:EnableSandboxBypassGrants"] = "true",
                ["Code:Copilot:WorkflowGrantTtlSeconds"] = "3600",
                ["Code:Copilot:TokenEnvironmentVariables:0"] = "TOKEN_ONE",
                ["Code:Copilot:Telemetry:Enabled"] = "false",
                ["Code:Copilot:Telemetry:ExporterType"] = "file",
                ["Code:Copilot:Telemetry:OtlpEndpoint"] = "http://localhost:4317",
                ["Code:Copilot:Telemetry:FilePath"] = "trace.jsonl",
                ["Code:Copilot:Telemetry:SourceName"] = "custom-source",
                ["Code:Copilot:Telemetry:CaptureContent"] = "true"
            })
            .Build();
        var settings = new CodeServerSettings();

        new CodeServerSettingsOptionsConfigurator(configuration).Configure(settings);

        Assert.Equal("C:/work", settings.DefaultWorkingDirectory);
        Assert.Equal(1024, settings.MaxFileSizeBytes);
        Assert.Equal(12, settings.MaxSearchResults);
        Assert.Equal(2048, settings.MaxPromptCharacters);
        Assert.True(settings.AllowWrites);
        Assert.Equal(["C:/work", "D:/repo"], settings.AllowedWorkingRoots);
        Assert.Equal([".cs", ".md"], settings.AllowedExtensions);
        Assert.Equal("anthropic", settings.Copilot.Provider);
        Assert.Equal("claude-sonnet-4-20250514", settings.Copilot.Model);
        Assert.Equal("agent", settings.Copilot.Mode);
        Assert.Equal("medium", settings.Copilot.ReasoningEffort);
        Assert.Equal("https://api.anthropic.com/v1", settings.Copilot.Endpoint);
        Assert.Equal("secret", settings.Copilot.ApiKey);
        Assert.True(settings.Copilot.UseLoggedInUser);
        Assert.False(settings.Copilot.ForwardTraceContext);
        Assert.Equal("debug", settings.Copilot.LogLevel);
        Assert.Equal(42, settings.Copilot.RequestTimeoutSeconds);
        Assert.Equal(84, settings.Copilot.ManagedSessionTtlSeconds);
        Assert.True(settings.Copilot.EnableApproveAll);
        Assert.True(settings.Copilot.EnableSandboxBypassGrants);
        Assert.Equal(3600, settings.Copilot.WorkflowGrantTtlSeconds);
        Assert.Equal(["TOKEN_ONE"], settings.Copilot.TokenEnvironmentVariables);
        Assert.False(settings.Copilot.Telemetry.Enabled);
        Assert.Equal("file", settings.Copilot.Telemetry.ExporterType);
        Assert.Equal("http://localhost:4317", settings.Copilot.Telemetry.OtlpEndpoint);
        Assert.Equal("trace.jsonl", settings.Copilot.Telemetry.FilePath);
        Assert.Equal("custom-source", settings.Copilot.Telemetry.SourceName);
        Assert.True(settings.Copilot.Telemetry.CaptureContent);
    }

    [Fact]
    public void Configure_PreservesDefaultsWhenValuesAreMissingOrInvalid()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Code:MaxFileSizeBytes"] = "not-a-number",
                ["Code:MaxSearchResults"] = "not-a-number",
                ["Code:AllowWrites"] = "not-a-bool",
                ["Code:Copilot:RequestTimeoutSeconds"] = "not-a-number",
                ["Code:Copilot:ManagedSessionTtlSeconds"] = "not-a-number"
            })
            .Build();
        var settings = new CodeServerSettings();
        var defaultExtensions = settings.AllowedExtensions.ToArray();
        var defaultTokenEnvironmentVariables = settings.Copilot.TokenEnvironmentVariables.ToArray();

        new CodeServerSettingsOptionsConfigurator(configuration).Configure(settings);

        Assert.Equal(512 * 1024, settings.MaxFileSizeBytes);
        Assert.Equal(100, settings.MaxSearchResults);
        Assert.False(settings.AllowWrites);
        Assert.Equal(120, settings.Copilot.RequestTimeoutSeconds);
        Assert.Equal(1800, settings.Copilot.ManagedSessionTtlSeconds);
        Assert.Equal(defaultExtensions, settings.AllowedExtensions);
        Assert.Equal(defaultTokenEnvironmentVariables, settings.Copilot.TokenEnvironmentVariables);
    }

    [Fact]
    public void Configure_ShippedAppSettingsDisableReusableSandboxBypass()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var settings = new CodeServerSettings();

        new CodeServerSettingsOptionsConfigurator(configuration).Configure(settings);

        Assert.False(settings.Copilot.EnableSandboxBypassGrants);
    }

    [Fact]
    public void Configure_ReadsReusableSandboxBypassFromEnvironmentVariables()
    {
        const string prefix = "GNOU_GO_COPILOT_SETTINGS_TEST_";
        const string variable = prefix + "Code__Copilot__EnableSandboxBypassGrants";
        Environment.SetEnvironmentVariable(variable, "true");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables(prefix)
                .Build();
            var settings = new CodeServerSettings();

            new CodeServerSettingsOptionsConfigurator(configuration).Configure(settings);

            Assert.True(settings.Copilot.EnableSandboxBypassGrants);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
