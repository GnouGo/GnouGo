using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GnOuGo.Cmd.Mcp.Tests;

/// <summary>Executes the shipped scripts with the host platform's actual shell.</summary>
public sealed class PackagedFilesystemTests : IDisposable
{
    internal static readonly string[] ExpectedCommands =
    [
        "cat_file", "copy_directory", "copy_file", "create_directory", "delete_directory", "delete_file",
        "find_files", "grep_recursive", "head_file", "list_relative_path", "list_root", "move_directory",
        "move_file", "print_working_directory", "tail_file", "tree", "wc", "write_file"
    ];
    private readonly string _root = Directory.CreateTempSubdirectory("gnougo-filesystem-tests-").FullName;
    private readonly CmdServerSettings _settings;
    private readonly CommandPolicy _policy;
    private readonly CmdTools _tools;

    public PackagedFilesystemTests()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cmd:DefaultWorkingDirectory"] = _root }).Build();
        _settings = new();
        new CmdServerSettingsOptionsConfigurator(configuration).Configure(_settings);
        _policy = new(_settings, _root);
        _tools = new(new(_policy, NullLogger<CommandExecutionHost>.Instance), NullLogger<CmdTools>.Instance);
    }

    private string At(string relative) => Path.Combine(_root, relative);
    private Task<CmdRunResult> Run(string command, JsonObject? parameters = null, CancellationToken? ct = null, int? timeout = null)
        => _tools.RunAsync(command, parameters, timeout, ct ?? TestContext.Current.CancellationToken);
    private void FileAt(string relative, string content = "Unicode: é東京\nsecond line\n")
    {
        var path = At(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
    }
    private static void Success(CmdRunResult result)
    {
        Assert.True(result.Success, result.ErrorMessage + "\n" + result.Stderr);
        Assert.Equal(0, result.ExitCode); Assert.NotNull(result.StartedAtUtc); Assert.False(result.TimedOut);
    }

    [Fact]
    public void PackagedCatalogAndDiscoveryContainOnlyTheEighteenFilesystemCommands()
    {
        Assert.Equal(ExpectedCommands, _policy.ListAllowedCommands().Select(c => c.Name));
        using var baseSchema = JsonDocument.Parse("""{"type":"object","properties":{"commandName":{"type":"string"},"parameters":{"type":["object","null"]}}} """);
        var schema = _policy.BuildCmdRunInputSchema(baseSchema.RootElement);
        Assert.Equal(ExpectedCommands, schema.GetProperty("properties").GetProperty("commandName").GetProperty("enum").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(18, schema.GetProperty("oneOf").GetArrayLength());
        Assert.Equal(18, _policy.DescribePolicy().AllowedCommandCount);
        var metadata = new JsonObject { ["gnougo"] = new JsonObject { ["artifacts"] = _policy.BuildArtifactMetadata() } };
        var artifacts = GnOuGo.Mcp.Core.McpArtifactContractParser.ParseAndValidate(metadata, JsonNode.Parse(schema.GetRawText()), null);
        Assert.True(artifacts.IsValid, string.Join("; ", artifacts.Errors));
        Assert.Contains(artifacts.Contract!.Locations!, l => l.Action == "release" && l.Kind == "directory" && l.SelectorValue == "delete_directory");
    }

    [Theory]
    [InlineData("dotnet_build")][InlineData("dotnet_restore")][InlineData("dotnet_test")]
    [InlineData("pnpm_install")][InlineData("pnpm_run")][InlineData("os_info")][InlineData("which")]
    [InlineData("env")][InlineData("rm")][InlineData("write_markdown_file")]
    public async Task RemovedDefaultsAreRejectedBeforeDispatch(string command)
    {
        var result = await Run(command);
        Assert.Equal("INVALID_INPUT", result.ErrorCode); Assert.Null(result.StartedAtUtc);
        Assert.Contains("not allowed", result.ErrorMessage);
        Assert.DoesNotContain("- " + command + ":", _policy.BuildCmdRunToolDescription());
    }

    [Fact]
    public async Task DeleteDirectoryAcceptsAbsenceAndRepeatedCleanupWithoutCreatingAnything()
    {
        Success(await Run("delete_directory", new() { ["path"] = "workflows/first-run" }));
        Assert.False(Directory.Exists(At("workflows")));
        FileAt("workflows/first-run/nested/data.txt");
        Success(await Run("delete_directory", new() { ["path"] = "workflows/first-run" }));
        Success(await Run("delete_directory", new() { ["path"] = "workflows/first-run" }));
        Assert.False(Directory.Exists(At("workflows/first-run")));
    }

    [Theory]
    [InlineData("recursive", true)]
    [InlineData("ignoreMissing", true)]
    [InlineData("invented", "true")]
    [InlineData("args", "workflows/first-run")]
    public async Task UnknownOrNonstringsAreRejectedBeforeDeletion(string name, object value)
    {
        FileAt("workflows/first-run/keep.txt", "unchanged");
        var parameters = new JsonObject { ["path"] = "workflows/first-run", [name] = value is bool b ? JsonValue.Create(b) : JsonValue.Create((string)value) };
        var before = parameters.ToJsonString();
        var result = await Run("delete_directory", parameters);
        Assert.Equal("INVALID_INPUT", result.ErrorCode); Assert.Null(result.StartedAtUtc);
        Assert.Equal(before, parameters.ToJsonString());
        Assert.Equal("unchanged", File.ReadAllText(At("workflows/first-run/keep.txt")));
    }

    [Fact]
    public void RenamedCommandPublishesExactEffectiveParameterContract()
    {
        var command = _settings.AllowedCommands["delete_directory"];
        _settings.AllowedCommands.Remove("delete_directory");
        _settings.AllowedCommands["remove_tree"] = command;
        using var input = JsonDocument.Parse("""{"type":"object","properties":{"commandName":{"type":"string"},"parameters":{"type":"object"}}}""");
        var schema = _policy.BuildCmdRunInputSchema(input.RootElement);
        var branch = schema.GetProperty("oneOf").EnumerateArray().Single(b => b.GetProperty("properties").GetProperty("commandName").GetProperty("const").GetString() == "remove_tree");
        var parameters = branch.GetProperty("properties").GetProperty("parameters");
        Assert.False(parameters.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "path" }, parameters.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "path" }, parameters.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("string", parameters.GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
        Assert.Contains("parameters", branch.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains(_policy.BuildArtifactMetadata()!["locations"]!.AsArray(), l => l!["selectorValue"]!.ToString() == "remove_tree" && l["action"]!.ToString() == "release");
    }

    [Fact]
    public async Task DirectoryDeletionDoesNotDeleteAFileAndFileDeletionKeepsItsStrictSemantics()
    {
        FileAt("file.txt", "keep");
        var result = await Run("delete_directory", new() { ["path"] = "file.txt" });
        Assert.False(result.Success); Assert.Equal("keep", File.ReadAllText(At("file.txt")));
        Assert.False((await Run("delete_file", new() { ["path"] = "absent.txt" })).Success);
    }

    [Fact]
    public async Task ExplicitStrictOverrideRemainsAuthoritative()
    {
        _settings.AllowedCommands["delete_directory"].Parameters["path"].MustExist = true;
        var result = await Run("delete_directory", new() { ["path"] = "absent" });
        Assert.Equal("INVALID_INPUT", result.ErrorCode); Assert.Null(result.StartedAtUtc);
        FileAt("present/child.txt"); Success(await Run("delete_directory", new() { ["path"] = "present" }));
    }

    [Theory]
    [InlineData("copy_file", false)][InlineData("move_file", false)]
    [InlineData("copy_directory", true)][InlineData("move_directory", true)]
    public async Task CopyAndMovePreserveContentsAndUseTheExactDestination(string command, bool directory)
    {
        var source = directory ? "original" : "original.txt";
        FileAt(directory ? "original/nested/file.txt" : source);
        if (directory) { FileAt("original/.hidden", "hidden"); Directory.CreateDirectory(At("original/empty")); }
        var expected = File.ReadAllText(At(directory ? "original/nested/file.txt" : source));
        Directory.CreateDirectory(At("target"));
        Success(await Run(command, new() { ["source"] = source, ["destination"] = "target/renamed" }));
        Assert.Equal(expected, File.ReadAllText(At(directory ? "target/renamed/nested/file.txt" : "target/renamed")));
        Assert.Equal(command.StartsWith("copy", StringComparison.Ordinal), Path.Exists(At(source)));
        if (directory)
        {
            Assert.Equal("hidden", File.ReadAllText(At("target/renamed/.hidden")));
            Assert.True(Directory.Exists(At("target/renamed/empty")));
            Assert.False(Directory.Exists(At("target/renamed/original")));
        }
    }

    [Theory]
    [InlineData("copy_file", false)][InlineData("move_file", false)]
    [InlineData("copy_directory", true)][InlineData("move_directory", true)]
    public async Task CollisionsAndMissingParentsFailWithoutChangingSourceOrDestination(string command, bool directory)
    {
        var source = directory ? "source" : "source.txt";
        var sentinel = directory ? "source/data.txt" : source;
        FileAt(sentinel, "source content"); FileAt("destination", "destination content");
        foreach (var target in new[] { "destination", "missing/child" })
        {
            Assert.False((await Run(command, new() { ["source"] = source, ["destination"] = target })).Success);
            Assert.Equal("source content", File.ReadAllText(At(sentinel)));
            Assert.Equal("destination content", File.ReadAllText(At("destination")));
        }
        Directory.CreateDirectory(At("existing-directory")); FileAt("existing-directory/sentinel", "keep");
        Assert.False((await Run(command, new() { ["source"] = source, ["destination"] = "existing-directory" })).Success);
        Assert.Equal("keep", File.ReadAllText(At("existing-directory/sentinel")));
        Assert.Single(Directory.EnumerateFileSystemEntries(At("existing-directory")));
        Assert.Equal("source content", File.ReadAllText(At(sentinel)));
        Assert.False(Directory.Exists(At("missing")));
    }

    [Theory]
    [InlineData("copy_file")][InlineData("move_file")][InlineData("copy_directory")][InlineData("move_directory")]
    public async Task MissingOrWrongTypeSourcesFailBeforeDispatch(string command)
    {
        FileAt("file"); Directory.CreateDirectory(At("directory"));
        foreach (var source in new[] { "absent", command.EndsWith("file", StringComparison.Ordinal) ? "directory" : "file" })
        {
            var result = await Run(command, new() { ["source"] = source, ["destination"] = "result" });
            Assert.Equal("INVALID_INPUT", result.ErrorCode); Assert.Null(result.StartedAtUtc);
            Assert.False(Path.Exists(At("result")));
        }
    }

    [Theory]
    [InlineData("copy_directory")][InlineData("move_directory")]
    public async Task CannotCopyOrMoveDirectoryIntoItselfOrItsDescendants(string command)
    {
        FileAt("source/data.txt", "unchanged");
        foreach (var target in new[] { "source", "source/child", "SOURCE/child" })
        {
            Assert.False((await Run(command, new() { ["source"] = "source", ["destination"] = target })).Success);
            Assert.Equal("unchanged", File.ReadAllText(At("source/data.txt")));
            Assert.False(Directory.Exists(At("source/child")));
        }
    }

    [Theory]
    [InlineData("../escape")][InlineData(".GnOuGo/data")][InlineData("/")][InlineData(".")]
    public async Task UnsafeDestinationsAndDeletionTargetsFailBeforeDispatch(string path)
    {
        FileAt("source/file.txt");
        foreach (var command in new[] { "copy_directory", "move_directory", "delete_directory" })
        {
            var args = command == "delete_directory" ? new JsonObject { ["path"] = path } : new JsonObject { ["source"] = "source", ["destination"] = path };
            var result = await Run(command, args);
            Assert.Equal("INVALID_INPUT", result.ErrorCode); Assert.Null(result.StartedAtUtc);
            Assert.True(File.Exists(At("source/file.txt")));
        }
    }

    [Fact]
    public async Task CancellationBeforeDispatchLeavesSourceUntouched()
    {
        FileAt("source/file.txt"); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var result = await Run("move_directory", new() { ["source"] = "source", ["destination"] = "moved" }, cancelled.Token);
        Assert.Equal("CANCELLED", result.ErrorCode); Assert.Null(result.StartedAtUtc);
        Assert.True(File.Exists(At("source/file.txt"))); Assert.False(Path.Exists(At("moved")));
    }

    [Theory]
    [InlineData("copy_file", "source")][InlineData("copy_file", "destination")]
    [InlineData("move_file", "source")][InlineData("move_file", "destination")]
    [InlineData("copy_directory", "source")][InlineData("copy_directory", "destination")]
    [InlineData("move_directory", "source")][InlineData("move_directory", "destination")]
    public async Task LinksInEitherOperandCannotEscapeTheWorkspace(string command, string operand)
    {
        var outside = Directory.CreateTempSubdirectory("gnougo-external-fixture-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "outside content");
            Directory.CreateSymbolicLink(At("link"), outside);
            var directory = command.EndsWith("directory", StringComparison.Ordinal);
            FileAt(directory ? "source/file.txt" : "source.txt");
            var args = new JsonObject { ["source"] = directory ? "source" : "source.txt", ["destination"] = "result" };
            args[operand] = operand == "destination" ? "link/new" : directory ? "link" : "link/sentinel.txt";
            var result = await Run(command, args);
            Assert.Equal("INVALID_INPUT", result.ErrorCode); Assert.Null(result.StartedAtUtc);
            Assert.Equal("outside content", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
            Assert.Single(Directory.EnumerateFileSystemEntries(outside));
            Assert.False(Path.Exists(At("result")));
        }
        finally { Directory.Delete(At("link")); Directory.Delete(outside, recursive: true); }
    }

    [Theory]
    [InlineData("copy_directory")][InlineData("move_directory")]
    public async Task DirectoryTransfersRejectNestedLinksWithoutChangingTheSource(string command)
    {
        FileAt("source/data.txt", "source"); FileAt("elsewhere/secret.txt", "untouched");
        Directory.CreateSymbolicLink(At("source/nested-link"), At("elsewhere"));
        try
        {
            var result = await Run(command, new() { ["source"] = "source", ["destination"] = "target" });
            Assert.False(result.Success); Assert.Equal("NON_ZERO_EXIT", result.ErrorCode);
            Assert.Equal("untouched", File.ReadAllText(At("elsewhere/secret.txt")));
            Assert.Equal("source", File.ReadAllText(At("source/data.txt")));
            Assert.False(Path.Exists(At("target")));
        }
        finally { Directory.Delete(At("source/nested-link")); }
    }

    [Fact]
    public async Task DirectoryDeletionReportsActualOsFailures()
    {
        FileAt("protected/file.txt", "keep");
        if (OperatingSystem.IsWindows())
        {
            using var locked = File.Open(At("protected/file.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
            var result = await Run("delete_directory", new() { ["path"] = "protected" });
            Assert.False(result.Success); Assert.Equal("NON_ZERO_EXIT", result.ErrorCode);
        }
        else
        {
            Assert.SkipWhen(Environment.UserName == "root", "Filesystem permission denial requires an unprivileged test process.");
            var mode = File.GetUnixFileMode(At("protected"));
            try
            {
                File.SetUnixFileMode(At("protected"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
                var result = await Run("delete_directory", new() { ["path"] = "protected" });
                Assert.False(result.Success); Assert.Equal("NON_ZERO_EXIT", result.ErrorCode);
            }
            finally { File.SetUnixFileMode(At("protected"), mode); }
        }
        Assert.Equal("keep", File.ReadAllText(At("protected/file.txt")));
    }

    [Fact]
    public async Task CommandTimeoutRemainsBounded()
    {
        _settings.AllowedCommands["slow_fixture"] = new()
        {
            Shell = OperatingSystem.IsWindows() ? "powershell" : "sh",
            Script = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30"
        };
        var result = await Run("slow_fixture", timeout: 100);
        Assert.False(result.Success); Assert.True(result.TimedOut); Assert.Equal("TIMEOUT", result.ErrorCode);
    }

    [Fact]
    public async Task CustomAliasesRemainSupportedWithoutNameBasedBehavior()
    {
        var command = _settings.AllowedCommands["delete_directory"];
        _settings.AllowedCommands.Remove("delete_directory");
        _settings.AllowedCommands["custom_cleanup"] = command;
        Success(await Run("custom_cleanup", new() { ["path"] = "absent" }));
        // Packaged removal is not a blacklist for an explicit custom configuration.
        _settings.AllowedCommands["dotnet_test"] = command;
        Success(await Run("dotnet_test", new() { ["path"] = "absent" }));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
