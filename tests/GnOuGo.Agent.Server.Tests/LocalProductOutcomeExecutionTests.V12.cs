using System.Text;
using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Fact]
    public async Task V12DirectValuesPreserveTheSameBusinessPlanAndIndependentBoundaries()
    {
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new(), AppContext.BaseDirectory)));
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken); catalog.Policy.RequireExternalConfirmation = false;
        var proposal = V10Proposal(catalog, 4);
        var before = BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v11");
        var after = BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v12");
        Assert.Equal(61, before.Steps); Assert.Equal(27, before.Sets);
        Assert.Equal(before.Steps, after.Steps); Assert.Equal(before.Sets, after.Sets);
        Assert.True(after.Yaml.Length < before.Yaml.Length, $"YAML {before.Yaml.Length}→{after.Yaml.Length}");
        var oldSteps = WorkflowParser.Parse(before.Yaml).Workflows.Values.SelectMany(w => V11Steps(w.Steps.Concat(w.Finally))).ToArray();
        var newSteps = WorkflowParser.Parse(after.Yaml).Workflows.Values.SelectMany(w => V11Steps(w.Steps.Concat(w.Finally))).ToArray();
        Assert.Equal(oldSteps.Select(s => (s.Id, s.Type)), newSteps.Select(s => (s.Id, s.Type)));
        Assert.Equal(before.Yaml, BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v11").Yaml);
        Assert.Equal(0, factory.InvocationAttempts);
        output.WriteLine($"v11→v12 same TaskPlan: tasks={RetainedTasks(proposal.Plan!.Root).Count()}; steps={before.Steps}→{after.Steps}; sets={before.Sets}→{after.Sets}; YAML={Encoding.UTF8.GetByteCount(before.Yaml)}→{Encoding.UTF8.GetByteCount(after.Yaml)}");
        if (Environment.GetEnvironmentVariable("GNOU_GO_V12_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "before-v11.yaml"), before.Yaml);
            File.WriteAllText(Path.Combine(folder, "after-v12.yaml"), after.Yaml);
        }
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("repeated")]
    [InlineData("missing")]
    [InlineData("one")]
    [InlineData("empty")]
    [InlineData("unoffered")]
    [InlineData("incomplete")]
    [InlineData("denied")]
    public Task V12CompositionExecutesOriginalObservationsAndExactWorkbook(string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v12");
}
