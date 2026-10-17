using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Fact]
    public async Task V11InlinesPreparationWithoutMovingMappingInferenceOrContracts()
    {
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new(), AppContext.BaseDirectory)));
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken); catalog.Policy.RequireExternalConfirmation = false;
        var proposal = V10Proposal(catalog, 4);
        var before = BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v10");
        var after = BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v11");
        Assert.True(after.Sets <= before.Sets - 2, $"sets {before.Sets}→{after.Sets}");
        Assert.True(after.Steps <= before.Steps - 2, $"steps {before.Steps}→{after.Steps}");
        Assert.True(after.Yaml.Length < before.Yaml.Length);
        Assert.DoesNotMatch(@"\btask[A-Fa-f0-9]{12,}\b", after.Yaml);
        Assert.Equal(before.Yaml, BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v10").Yaml);
        var oldDocument = WorkflowParser.Parse(before.Yaml); var newDocument = WorkflowParser.Parse(after.Yaml);
        var oldMappings = oldDocument.Workflows.Values.SelectMany(w => V11Steps(w.Steps)).Where(s => s.Type == "mapping.dynamic").ToArray();
        var newMappings = newDocument.Workflows.Values.SelectMany(w => V11Steps(w.Steps)).Where(s => s.Type == "mapping.dynamic").ToArray();
        Assert.Equal(2, newMappings.Length); Assert.Equal(oldMappings.Select(s => s.Id), newMappings.Select(s => s.Id));
        foreach (var (oldStep, newStep) in oldMappings.Zip(newMappings))
        {
            Assert.NotNull(newStep.ExpressionContracts);
            foreach (var field in oldStep.Input!.AsObject().Where(p => p.Key != "sources"))
                Assert.True(JsonNode.DeepEquals(field.Value, newStep.Input![field.Key]), field.Key);
            Assert.True(JsonNode.DeepEquals(oldStep.OutputSchema, newStep.OutputSchema));
        }
        Assert.Equal(0, factory.InvocationAttempts);
        output.WriteLine($"v10→v11: tasks={RetainedTasks(proposal.Plan!.Root).Count()}; steps={before.Steps}→{after.Steps}; sets={before.Sets}→{after.Sets}; YAML={Encoding.UTF8.GetByteCount(before.Yaml)}→{Encoding.UTF8.GetByteCount(after.Yaml)}");
        if (Environment.GetEnvironmentVariable("GNOU_GO_V11_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "before-v10.yaml"), before.Yaml);
            File.WriteAllText(Path.Combine(folder, "after-v11.yaml"), after.Yaml);
        }
    }

    private static IEnumerable<GnOuGo.Flow.Core.Models.StepDef> V11Steps(IEnumerable<GnOuGo.Flow.Core.Models.StepDef> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in V11Steps((step.Steps ?? []).Concat(step.Default ?? []).Concat((step.Cases ?? []).SelectMany(c => c.Steps)).Concat((step.Branches ?? []).SelectMany(b => b.Steps)))) yield return child;
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
    public Task V11CompositionExecutesOriginalObservationsAndExactWorkbook(string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v11");
}
