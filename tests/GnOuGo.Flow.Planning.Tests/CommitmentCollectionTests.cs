using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CommitmentCollectionTests
{
    [Theory]
    [InlineData("direct", "verified")]
    [InlineData("flatten", "verified")]
    [InlineData("lookup", "verified")]
    [InlineData("mixed", "violation")]
    [InlineData("empty", "unverified")]
    public async Task CollectionLineagePreservesRecordsWithoutCertifyingSelectionOrCardinality(string variant, string expected)
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("records", new() { Tools = [new() { Name = "observe", EffectKind = "read", InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"records":{"type":"array","items":{"type":"object","properties":{"key":{"type":"string"},"text":{"type":"string"}},"required":["key","text"],"additionalProperties":false}}},"required":["records"],"additionalProperties":false}""") }] });
        var runtime = new TestRuntime(new() { McpClientFactory = factory });
        var catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual);
        var operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "observe")).Id;
        var record = new TaskType { Kind = "object", Fields = [new() { Name = "key", Type = new() { Kind = "string" } }, new() { Name = "text", Type = new() { Kind = "string" } }] };
        var original = CommitmentCoverageTests.Output("observe", "records");
        TaskValue value = variant switch
        {
            "flatten" => new() { Kind = "flatten", Items = [new() { Kind = "array", Items = [original, original] }] },
            "lookup" => new() { Kind = "lookup", Port = "key", Items = [original, new() { Kind = "array", Items = [CommitmentCoverageTests.Text("same"), CommitmentCoverageTests.Text("same")] }] },
            "mixed" => new() { Kind = "flatten", Items = [new() { Kind = "array", Items = [original, new() { Kind = "array", Items = [new() { Kind = "object", Members = [new("key", CommitmentCoverageTests.Text("invented")), new("text", CommitmentCoverageTests.Text("fabricated"))] }] }] }] },
            "empty" => new() { Kind = "array", Items = [] },
            _ => original
        };
        runtime.Proposal = new()
        {
            Requirements = new() { Summary = "Return exact observed records", Inputs = [], Outputs = [new() { Name = "records", Type = new() { Kind = "array", Items = record } }],
                Outcomes = [new("records", "Return exact observed records") { Commitments = [new(operation) { Result = new("records", "records") }] }] },
            Plan = new() { Root = new() { Tasks = [new() { Id = "observe", Objective = "Observe", Operation = operation }], Outputs = [new("records", value)] } }
        };
        var state = PlannerFixture.Session(); state.IntentVersion = 3; state.Catalog = catalog;
        state.Request.Options["compilation_profile"] = TaskPlanCompiler.CompactProfile;
        Assert.Empty(new TaskPlanCompiler().Compile(runtime.Proposal.Plan, catalog, state.Request).Diagnostics);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(expected, Assert.Single(state.ValidationResults, r => r.Id == "coverage:records:0:result").Outcome);
        Assert.Contains(state.ValidationResults, r => r.Outcome == "unverified" && r.Id.EndsWith(":limits", StringComparison.Ordinal));
        Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
    }
}
