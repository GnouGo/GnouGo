using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class CodeProjectServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-code-service-tests-" + Guid.NewGuid().ToString("N"));

    public CodeProjectServiceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "App.sln"), "Microsoft Visual Studio Solution File\n");
        File.WriteAllText(Path.Combine(_root, "src", "App.csproj"), "<Project />\n");
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "Console.WriteLine(\"Hello\");\n");
    }

    [Fact]
    public void GetSummary_ReturnsProjectShape()
    {
        var service = CreateService();

        var summary = service.GetSummary(_root);

        Assert.Equal(_root, summary.RootPath);
        Assert.Contains("App.sln", summary.SolutionFiles);
        Assert.Contains("src\\App.csproj", summary.ProjectFiles.Select(p => p.Replace('/', '\\')));
        Assert.True(summary.CodeFileCount >= 2);
    }

    [Fact]
    public void Search_FindsText()
    {
        var service = CreateService();

        var results = service.Search(_root, "Console", "*.cs");

        var result = Assert.Single(results.Results);
        Assert.Equal("src\\Program.cs", result.Path.Replace('/', '\\'));
        Assert.Equal(1, result.Line);
    }

    [Fact]
    public void WriteFile_WritesWhenEnabled()
    {
        var settings = CreateSettings();
        settings.AllowWrites = true;
        var service = CreateService(settings);

        var result = service.WriteFile(_root, "src/NewFile.cs", "public class NewFile {}\n");

        Assert.True(File.Exists(Path.Combine(_root, "src", "NewFile.cs")));
        Assert.Equal("src\\NewFile.cs", result.Path.Replace('/', '\\'));
    }

    private CodeProjectService CreateService(CodeServerSettings? settings = null)
    {
        settings ??= CreateSettings();
        var policy = new CodePolicy(settings, _root);
        return new CodeProjectService(policy, Options.Create(settings));
    }

    private CodeServerSettings CreateSettings() => new()
    {
        DefaultWorkingDirectory = _root,
        AllowedWorkingRoots = [_root],
        AllowedExtensions = [".cs", ".csproj", ".sln", ".md"],
        MaxFileSizeBytes = 1024 * 1024,
        MaxSearchResults = 10,
        AllowWrites = false
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}


