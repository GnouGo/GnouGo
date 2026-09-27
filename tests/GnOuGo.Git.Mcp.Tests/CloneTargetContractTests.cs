using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using Xunit;

namespace GnOuGo.Git.Mcp.Tests;

public sealed class CloneTargetContractTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("gnougo-clone-contract-").FullName;
    public static TheoryData<string, bool> Paths => new()
    {
        { "workflows/repository", true }, { "workflows/review/nested", true },
        { "workflows/été 東京", true }, { "workflows/.hidden/repo-name_2", true },
        { "SmartGuide", false }, { "", false }, { "workflows", false }, { "workflows/", false },
        { "workflows//repo", false }, { "workflows/repo/", false },
        { "workflows/.", false }, { "workflows/..", false }, { "workflows/a/../b", false },
        { "workflows/./repo", false }, { "workflows/a/./b", false },
        { "workflows/repo\n", false }, { "workflows/repo\r", false }, { "workflows/repo\t", false },
        { "workflows/repo\u0085", false }, { "workflows/repo\u009f", false }, { "workflows/repo\u007f", false }, { "workflows/repo\0", false },
        { "workflows/r*", false }, { "workflows/r?", false }, { "workflows\\repo", false },
        { "workflows/a\\b", false }, { "../workflows/repo", false },
        { "/tmp/workflows/repo", false }, { "C:/workflows/repo", false }, { "\\\\host\\workflows\\repo", false },
        { "~/workflows/repo", false }, { "file:///workflows/repo", false },
        { ".GnOuGo/data/repo", false }, { " workflows/repo", false }
    };

    [Theory, MemberData(nameof(Paths))]
    public void RuntimeEnforcesPortableRelativeSyntax(string path, bool valid)
    {
        var policy = new GitPolicy(new() { DefaultWorkingDirectory = root }, root);
        if (valid) Assert.Equal(Path.GetFullPath(Path.Combine(root, path)), policy.ResolveCloneTargetDirectory(path));
        else Assert.Throws<InvalidOperationException>(() => policy.ResolveCloneTargetDirectory(path));
    }

    [Fact]
    public async Task ActualStdioDiscoveryPublishesTheSameDotNetAndJavaScriptContract()
    {
        var executable = Environment.GetEnvironmentVariable("GNOU_GO_GIT_MCP_TEST_EXECUTABLE") ??
            Path.Combine(AppContext.BaseDirectory, "GnOuGo.Git.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = executable, WorkingDirectory = Path.GetDirectoryName(executable),
            EnvironmentVariables = new Dictionary<string, string?> { ["Git__DefaultWorkingDirectory"] = root, ["OpenTelemetry__Enabled"] = "false" }
        }), cancellationToken: TestContext.Current.CancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var tool = Assert.Single(tools, t => t.Name == "git_clone");
        var schema = tool.JsonSchema.GetProperty("properties").GetProperty("targetDirectory");
        var pattern = schema.GetProperty("pattern").GetString()!;
        Assert.Equal(11, schema.GetProperty("minLength").GetInt32());
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        foreach (var row in Paths) Assert.Equal(row.Data.Item2, regex.IsMatch(row.Data.Item1));
        using var process = new Process { StartInfo = new("node") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add("let t='';process.stdin.on('data',c=>t+=c);process.stdin.on('end',()=>{const x=JSON.parse(t),r=new RegExp(x.pattern,'u');if(x.paths.some(([s,v])=>r.test(s)!==v))process.exit(1);});");
        Assert.True(process.Start());
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { pattern, paths = Paths.Select(r => new object[] { r.Data.Item1, r.Data.Item2 }) }));
        process.StandardInput.Close();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Contains("partial", tool.Description);
        Assert.DoesNotContain("Call this first", Assert.Single(tools, t => t.Name == "git_get_policy").Description);
    }

    public void Dispose() => Directory.Delete(root, true);
}
