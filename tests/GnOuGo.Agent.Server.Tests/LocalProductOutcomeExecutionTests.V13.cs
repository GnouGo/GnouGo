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
        var factory = new RealProductContracts.Factory(CaptureIsolatedProductContracts());
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken); catalog.Policy.RequireExternalConfirmation = false;
        var proposal = V10Proposal(catalog, 4);
        var before = BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v12");
        var after = BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v13");
        if (Environment.GetEnvironmentVariable("GNOU_GO_V13_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "before-v12.yaml"), before.Yaml);
            File.WriteAllText(Path.Combine(folder, "after-v13.yaml"), after.Yaml);
        }
        var originalGraph = new TaskPlanCompiler().Compile(proposal.Plan!, catalog, new GnOuGo.Flow.Core.Planning.PlanningRequest
            { Options = new() { ["compilation_profile"] = "compact-bindings-v12" } }).Graph!;
        var origins = originalGraph.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally))).ToDictionary(
            n => "n_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(n.Key))).ToLowerInvariant()[..16], n => n.Key);
        var checkedOrigins = new HashSet<string>(StringComparer.Ordinal);
        void Checks(System.Text.Json.Nodes.JsonNode? value)
        {
            if (value is System.Text.Json.Nodes.JsonObject obj)
            {
                if (obj["origin"] is System.Text.Json.Nodes.JsonValue origin && origin.TryGetValue<string>(out var text) && obj.ContainsKey("schema")) checkedOrigins.Add(text);
                foreach (var member in obj) Checks(member.Value);
            }
            else if (value is System.Text.Json.Nodes.JsonArray array) foreach (var item in array) Checks(item);
        }
        var currentDefinition = WorkflowParser.Parse(after.Yaml);
        Checks(System.Text.Json.JsonSerializer.SerializeToNode(currentDefinition, GnOuGo.Flow.Core.Runtime.WorkflowRunJsonContext.Default.WorkflowDocument));
        var survivingOrigins = checkedOrigins.ToHashSet(StringComparer.Ordinal);
        checkedOrigins.Clear();
        Checks(System.Text.Json.JsonSerializer.SerializeToNode(WorkflowParser.Parse(before.Yaml), GnOuGo.Flow.Core.Runtime.WorkflowRunJsonContext.Default.WorkflowDocument));
        Assert.All(checkedOrigins, origin => Assert.Contains(origin, survivingOrigins));
        foreach (var original in WorkflowParser.Parse(before.Yaml).Workflows.Values.SelectMany(w => V11Steps(w.Steps.Concat(w.Finally))).Where(n => n.Type == "set"))
            Assert.Contains(origins[original.Id], survivingOrigins);
        Assert.Equal(61, before.Steps); Assert.Equal(27, before.Sets);
        Assert.True(after.Steps < before.Steps); Assert.True(after.Sets < before.Sets);
        Assert.True(after.Yaml.Length < before.Yaml.Length, $"YAML bytes: {before.Yaml.Length} → {after.Yaml.Length}");
        Assert.Equal(before.Yaml, BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v12").Yaml);
        Assert.Equal(0, factory.InvocationAttempts);
        output.WriteLine($"v12→v13: tasks={RetainedTasks(proposal.Plan!.Root).Count()}; workflows={WorkflowParser.Parse(before.Yaml).Workflows.Count}→{WorkflowParser.Parse(after.Yaml).Workflows.Count}; steps={before.Steps}→{after.Steps}; sets={before.Sets}→{after.Sets}; YAML={Encoding.UTF8.GetByteCount(before.Yaml)}→{Encoding.UTF8.GetByteCount(after.Yaml)}");

    }

    [Theory]
    [InlineData("compact-bindings-v12")]
    [InlineData("compact-bindings-v13")]
    public Task V13ResourceComparisonPreservesCompleteLocalOutcome(string profile) => ExecuteTypedComposition("complete", profile);

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
