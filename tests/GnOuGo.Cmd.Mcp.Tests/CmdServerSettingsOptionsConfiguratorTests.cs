using Microsoft.Extensions.Configuration;
using Xunit;

namespace GnOuGo.Cmd.Mcp.Tests;

public sealed class CmdServerSettingsOptionsConfiguratorTests
{
    [Fact]
    public void Configure_AppliesConfiguredValuesWithoutConfigurationBinder()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cmd:DefaultWorkingDirectory"] = "C:/workspace",
                ["Cmd:DefaultTimeoutMs"] = "1200",
                ["Cmd:MaxTimeoutMs"] = "9000",
                ["Cmd:MaxOutputCharacters"] = "4500",
                ["Cmd:AllowedShells:0"] = "powershell",
                ["Cmd:AllowedShells:1"] = "bash",
                ["Cmd:AllowedWorkingRoots:0"] = "C:/workspace",
                ["Cmd:EnvironmentAllowList:0"] = "PATH",
                ["Cmd:AllowedCommands:list_files:Description"] = "List files",
                ["Cmd:AllowedCommands:list_files:Shell"] = "powershell",
                ["Cmd:AllowedCommands:list_files:Script"] = "Get-ChildItem {{path}}",
                ["Cmd:AllowedCommands:list_files:WorkingDirectory"] = "C:/workspace",
                ["Cmd:AllowedCommands:list_files:TimeoutMs"] = "1500",
                ["Cmd:AllowedCommands:list_files:MaxOutputCharacters"] = "2500",
                ["Cmd:AllowedCommands:list_files:Parameters:path:Description"] = "Path to list",
                ["Cmd:AllowedCommands:list_files:Parameters:path:Required"] = "true",
                ["Cmd:AllowedCommands:list_files:Parameters:path:Pattern"] = "^[A-Za-z0-9_./\\-]{1,120}$",
                ["Cmd:AllowedCommands:list_files:Parameters:path:MaxLength"] = "120",
                ["Cmd:AllowedCommands:list_files:Parameters:path:IsWorkspacePath"] = "true",
                ["Cmd:AllowedCommands:list_files:Parameters:path:AllowAbsolutePath"] = "false",
                ["Cmd:AllowedCommands:list_files:Parameters:path:MustExist"] = "true",
                ["Cmd:AllowedCommands:list_files:Parameters:path:PathKind"] = "Directory",
                ["Cmd:AllowedCommands:list_files:OsOverrides:windows:Shell"] = "powershell",
                ["Cmd:AllowedCommands:list_files:OsOverrides:windows:Script"] = "Get-ChildItem -Name {{path}}",
                ["Cmd:AllowedCommands:list_files:OsOverrides:windows:Parameters:path:Required"] = "true",
                ["Cmd:AllowedCommands:list_files:OsOverrides:windows:Parameters:path:PathKind"] = "Directory"
            })
            .Build();

        var settings = new CmdServerSettings();
        new CmdServerSettingsOptionsConfigurator(configuration).Configure(settings);

        Assert.Equal("C:/workspace", settings.DefaultWorkingDirectory);
        Assert.Equal(1200, settings.DefaultTimeoutMs);
        Assert.Equal(9000, settings.MaxTimeoutMs);
        Assert.Equal(4500, settings.MaxOutputCharacters);
        Assert.Equal(["powershell", "bash"], settings.AllowedShells);
        Assert.Equal(["C:/workspace"], settings.AllowedWorkingRoots);
        Assert.Equal(["PATH"], settings.EnvironmentAllowList);

        var command = Assert.Single(settings.AllowedCommands);
        Assert.Equal("list_files", command.Key);
        Assert.Equal("List files", command.Value.Description);
        Assert.Equal("powershell", command.Value.Shell);
        Assert.Equal("Get-ChildItem {{path}}", command.Value.Script);
        Assert.Equal("C:/workspace", command.Value.WorkingDirectory);
        Assert.Equal(1500, command.Value.TimeoutMs);
        Assert.Equal(2500, command.Value.MaxOutputCharacters);

        var parameter = Assert.Single(command.Value.Parameters);
        Assert.Equal("path", parameter.Key);
        Assert.Equal("Path to list", parameter.Value.Description);
        Assert.True(parameter.Value.Required);
        Assert.Equal("^[A-Za-z0-9_./\\-]{1,120}$", parameter.Value.Pattern);
        Assert.Equal(120, parameter.Value.MaxLength);
        Assert.True(parameter.Value.IsWorkspacePath);
        Assert.False(parameter.Value.AllowAbsolutePath);
        Assert.True(parameter.Value.MustExist);
        Assert.Equal(WorkspacePathKind.Directory, parameter.Value.PathKind);

        var windowsOverride = Assert.Single(command.Value.OsOverrides);
        Assert.Equal("windows", windowsOverride.Key);
        Assert.Equal("powershell", windowsOverride.Value.Shell);
        Assert.Equal("Get-ChildItem -Name {{path}}", windowsOverride.Value.Script);
        Assert.Equal(WorkspacePathKind.Directory, windowsOverride.Value.Parameters["path"].PathKind);
    }

    [Fact]
    public void Configure_PreservesDefaultsWhenValuesAreMissingOrInvalid()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cmd:DefaultTimeoutMs"] = "not-a-number",
                ["Cmd:MaxTimeoutMs"] = "not-a-number",
                ["Cmd:AllowedCommands:test:TimeoutMs"] = "not-a-number",
                ["Cmd:AllowedCommands:test:Parameters:path:PathKind"] = "not-an-enum"
            })
            .Build();

        var settings = new CmdServerSettings();
        var defaultShells = settings.AllowedShells.ToArray();
        var defaultEnvironmentAllowList = settings.EnvironmentAllowList.ToArray();

        new CmdServerSettingsOptionsConfigurator(configuration).Configure(settings);

        Assert.Equal(10_000, settings.DefaultTimeoutMs);
        Assert.Equal(30_000, settings.MaxTimeoutMs);
        Assert.Equal(defaultShells, settings.AllowedShells);
        Assert.Equal(defaultEnvironmentAllowList, settings.EnvironmentAllowList);

        var command = Assert.Single(settings.AllowedCommands);
        Assert.Equal("test", command.Key);
        Assert.Null(command.Value.TimeoutMs);
        Assert.Equal(WorkspacePathKind.Any, command.Value.Parameters["path"].PathKind);
    }
}


