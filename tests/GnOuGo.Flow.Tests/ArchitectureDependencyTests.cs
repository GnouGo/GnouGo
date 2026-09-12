using System.Reflection;
using System.Xml.Linq;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests;

public sealed class ArchitectureDependencyTests
{
    [Fact]
    public void FlowCore_HasNoGnOuGoProjectPackageOrAssemblyDependencies()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "GnOuGo.Flow.Core",
            "GnOuGo.Flow.Core.csproj");
        var project = XDocument.Load(projectPath);

        var forbiddenBuildReferences = project
            .Descendants()
            .Where(static element => element.Name.LocalName is "ProjectReference" or "PackageReference")
            .Select(static element => (string?)element.Attribute("Include"))
            .Where(static include => include?.Contains("GnOuGo.", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        Assert.Empty(forbiddenBuildReferences);

        var forbiddenAssemblyReferences = typeof(WorkflowEngine).Assembly
            .GetReferencedAssemblies()
            .Where(static reference => reference.Name?.StartsWith("GnOuGo.", StringComparison.OrdinalIgnoreCase) == true)
            .Select(static reference => reference.FullName)
            .ToArray();

        Assert.Empty(forbiddenAssemblyReferences);
    }

    [Fact]
    public void WorkflowPlanner_DoesNotEmbedKnownProviderOrUseCaseIdentifiers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var executorDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "GnOuGo.Flow.Core",
            "Runtime",
            "Executors");
        var plannerSource = string.Join(
            '\n',
            Directory.EnumerateFiles(executorDirectory, "WorkflowPlanExecutor*.cs")
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));

        var forbiddenIdentifiers = new[]
        {
            "copilot_review",
            "GithubCopilot",
            "pull_request_read",
            "pull_request_review_write",
            "cap_000"
        };
        foreach (var identifier in forbiddenIdentifiers)
            Assert.DoesNotContain(identifier, plannerSource, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GnOuGo.Agent.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the GnOuGo repository root.");
    }
}
