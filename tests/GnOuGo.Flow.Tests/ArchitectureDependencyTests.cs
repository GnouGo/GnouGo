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
