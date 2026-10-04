using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ArtifactLifecycleTests
{
    [Theory]
    [InlineData("file", "scratch/report.xlsx", "directory", "scratch", true)]
    [InlineData("file", "reports/report.xlsx", "directory", "scratch", false)]
    [InlineData("file", "scratch-copy/report.xlsx", "directory", "scratch", false)]
    [InlineData("handle", "token-a", "handle", "token-a", true)]
    [InlineData("handle", "token-a", "handle", "token-b", false)]
    public void ReturnedDeclaredResourcesMustSurviveCleanup(string kind, string created, string releaseKind, string released, bool conflict)
    {
        var (graph, catalog) = Fixture(kind, created, releaseKind, released);
        Assert.Equal(conflict, PlanningArtifactBindings.LifecycleFindings(graph, catalog).Any(d => d.Code == "ARTIFACT_OUTPUT_RELEASED"));
        catalog.Capabilities[0].ArtifactContract = null;
        Assert.Empty(PlanningArtifactBindings.LifecycleFindings(graph, catalog)); // Missing metadata never becomes an inferred producer.
    }

    [Fact]
    public void StaticallyDisabledReleaseDoesNotInvalidateARetainedArtifact()
    {
        var (graph, catalog) = Fixture("file", "scratch/a", "directory", "scratch");
        var workflow = graph.Workflows[0]; var release = workflow.Finally.Single();
        release.If = new() { Kind = "boolean", Boolean = false };
        Assert.Empty(PlanningArtifactBindings.LifecycleFindings(graph, catalog));
        release.If = null;
        workflow.Finally = [new() { Key = "never", Type = "switch", Expr = new() { Kind = "boolean", Boolean = false }, Cases = [new("true", null, [release])] }];
        Assert.Empty(PlanningArtifactBindings.LifecycleFindings(graph, catalog));
    }

    [Fact]
    public void UseAfterReleaseIsRejectedAndMutuallyExclusiveBranchesRemainIndependent()
    {
        var (graph, catalog) = Fixture("file", "scratch/a", "directory", "scratch");
        var workflow = graph.Workflows[0]; var release = workflow.Finally.Single(); workflow.Finally.Clear(); workflow.Outputs.Clear();
        catalog.Capabilities.Add(new() { Id = "read", ArtifactContract = new(1, [], []) { Locations = [new("/location", "file", "use", "file:///workspace/")] } });
        var use = Operation("use", "read", "scratch/a");
        workflow.Steps.AddRange([release, use]);
        Assert.Contains(PlanningArtifactBindings.LifecycleFindings(graph, catalog), d => d.Code == "ARTIFACT_USE_AFTER_RELEASE");
        workflow.Steps = [new() { Key = "choice", Type = "switch", Cases = [new("yes", null, [release])], Default = [use] }];
        Assert.Empty(PlanningArtifactBindings.LifecycleFindings(graph, catalog));
    }

    [Fact]
    public void ExistingScopeExportsRetainLocationAndUnrelatedAddressSpacesDoNotCollide()
    {
        var (graph, catalog) = Fixture("file", "scratch/a", "directory", "scratch");
        var root = graph.Workflows[0]; var child = new PlanningWorkflow { Key = "nested", Steps = [root.Steps.Single()], Outputs = root.Outputs };
        root.Steps = [new() { Key = "call", Type = "workflow.call", Input = PlanningCorpus.Obj(("ref", new() { Kind = "workflow", Source = child.Key }), ("args", PlanningCorpus.Obj())) }];
        root.Outputs = [new() { Name = "file", Value = PlanningCorpus.Ref("output", "call", "file") }]; graph.Workflows.Add(child);
        Assert.Contains(PlanningArtifactBindings.LifecycleFindings(graph, catalog), d => d.Code == "ARTIFACT_OUTPUT_RELEASED");
        catalog.Capabilities[1].ArtifactContract = new(1, [], []) { Locations = [new("/location", "directory", "release", "file:///another-space/")] };
        Assert.Empty(PlanningArtifactBindings.LifecycleFindings(graph, catalog));
    }

    private static (PlanningGraph, PlanningCatalog) Fixture(string kind, string created, string releaseKind, string released)
    {
        var space = kind == "handle" ? "opaque-pool" : "file:///workspace/";
        return (new() { Workflows = [new() { Steps = [Operation("produce", "create", created)], Finally = [Operation("cleanup", "release", released)],
            Outputs = [new() { Name = "file", Value = PlanningCorpus.Ref("output", "produce", "handle") }] }] },
            new() { Capabilities = [new() { Id = "create", ArtifactContract = new(1, [], []) { Locations = [new("/location", kind, "materialize", space, "/handle")] } },
                new() { Id = "release", ArtifactContract = new(1, [], []) { Locations = [new("/location", releaseKind, "release", space)] } }] });
    }
    private static PlanningNode Operation(string id, string capability, string location) => new() { Key = id, Type = "mcp.call", CapabilityId = capability,
        Input = PlanningCorpus.Obj(("request", PlanningCorpus.Obj(("location", PlanningCorpus.Text(location))))) };
}
