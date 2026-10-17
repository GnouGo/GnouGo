using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Fact]
    public async Task V14SimplifiesTheSameLexicalCompositionWithoutRemovingChecksOrBoundaries()
    {
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new(), AppContext.BaseDirectory)));
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken); catalog.Policy.RequireExternalConfirmation = false;
        var proposal = V10Proposal(catalog, 4);
        var before = BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v13");
        var after = BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v14");
        if (Environment.GetEnvironmentVariable("GNOU_GO_V14_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "comparison-v13.yaml"), before.Yaml);
            File.WriteAllText(Path.Combine(folder, "comparison-v14.yaml"), after.Yaml);
        }
        static string[] Origins(string yaml)
        {
            IEnumerable<string> Read(JsonNode? node) => node switch
            {
                JsonObject obj => (obj.ContainsKey("schema") && obj["origin"] is JsonValue origin ? new[] { origin.GetValue<string>() } : [])
                    .Concat(obj.SelectMany(p => Read(p.Value))),
                JsonArray array => array.SelectMany(Read), _ => []
            };
            return Read(JsonSerializer.SerializeToNode(WorkflowParser.Parse(yaml), WorkflowRunJsonContext.Default.WorkflowDocument)).Order(StringComparer.Ordinal).ToArray();
        }
        Assert.Equal(Origins(before.Yaml), Origins(after.Yaml));
        Assert.Equal(before.Steps, after.Steps); Assert.Equal(before.Sets, after.Sets);
        Assert.Equal(WorkflowParser.Parse(before.Yaml).Workflows.Count, WorkflowParser.Parse(after.Yaml).Workflows.Count);
        Assert.True(after.Yaml.Length < before.Yaml.Length, $"v13={before.Yaml.Length}; v14={after.Yaml.Length}");
        Assert.Equal(before.Yaml, BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v13").Yaml);
        Assert.Equal(0, factory.InvocationAttempts);
        output.WriteLine($"Same TaskPlan: steps={before.Steps}→{after.Steps}; sets={before.Sets}→{after.Sets}; bytes={Encoding.UTF8.GetByteCount(before.Yaml)}→{Encoding.UTF8.GetByteCount(after.Yaml)}; all check origins retained.");
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
    [InlineData("redirected")]
    public Task V14CompositionPreservesExactLocalWorkbookAndRecovery(string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v14");

    [Fact]
    public Task V14ResourceBaselineUsesTheIdenticalV13BusinessComposition()
        => ExecuteTypedComposition("complete", "compact-bindings-v13");
}
