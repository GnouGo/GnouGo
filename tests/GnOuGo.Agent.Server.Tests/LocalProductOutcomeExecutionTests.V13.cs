using System.Text;
using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Fact]
    public async Task V13ChecksReplaceTechnicalInvocationsOnTheSameBusinessPlan()
    {
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new(), AppContext.BaseDirectory)));
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken); catalog.Policy.RequireExternalConfirmation = false;
        var proposal = V10Proposal(catalog, 4);
        var before = BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v12");
        var after = BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v13");
        Assert.Equal(61, before.Steps); Assert.Equal(27, before.Sets);
        Assert.True(after.Steps < before.Steps); Assert.True(after.Sets < before.Sets);
        Assert.True(after.Yaml.Length < before.Yaml.Length);
        Assert.Equal(before.Yaml, BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v12").Yaml);
        Assert.Equal(0, factory.InvocationAttempts);
        output.WriteLine($"v12→v13: tasks={RetainedTasks(proposal.Plan!.Root).Count()}; workflows={WorkflowParser.Parse(before.Yaml).Workflows.Count}→{WorkflowParser.Parse(after.Yaml).Workflows.Count}; steps={before.Steps}→{after.Steps}; sets={before.Sets}→{after.Sets}; YAML={Encoding.UTF8.GetByteCount(before.Yaml)}→{Encoding.UTF8.GetByteCount(after.Yaml)}");
        if (Environment.GetEnvironmentVariable("GNOU_GO_V13_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "before-v12.yaml"), before.Yaml);
            File.WriteAllText(Path.Combine(folder, "after-v13.yaml"), after.Yaml);
        }
    }

    [Fact]
    public Task V13CheckedOutputsPreserveLocalWorkbook() => ExecuteTypedComposition("one", "compact-bindings-v13");

    [Theory]
    [InlineData("complete")]
    [InlineData("repeated")]
    [InlineData("missing")]
    [InlineData("one")]
    [InlineData("empty")]
    [InlineData("unoffered")]
    [InlineData("incomplete")]
    [InlineData("denied")]
    public Task V13CompositionExecutesOriginalObservationsAndExactWorkbook(string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v13");
}
