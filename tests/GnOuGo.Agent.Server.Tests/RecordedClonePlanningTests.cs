using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Integrations;
using GnOuGo.AI.Core;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedClonePlanningTests
{
    internal static JsonObject Read(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClonePlanning", name + ".json")))!.AsObject();
    internal static string Snapshot(PlanningSession state) => JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
    internal static PlanningSession Recover(PlanningSession state) => JsonSerializer.Deserialize(Snapshot(state), PlanningJsonContext.Default.PlanningSession)!;

    [Fact]
    public async Task RetainedResponsesWereAcceptedDespiteUnconstrainedCloneAndUnrelatedCleanup()
    {
        var replay = new Replay(); var state = await replay.Run();
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Empty(state.Diagnostics);
        Assert.Equal(3, replay.Calls); Assert.Equal(0, state.ReplanAttempts);
        var receipts = replay.Recording["executionReceipts"]!.AsArray();
        var clone = receipts.Single(r => r!["id"]!.ToString().EndsWith("/step/n_342db19f3d6751ed", StringComparison.Ordinal))!;
        Assert.Equal("SmartGuide", clone["resolvedInput"]!["request"]!["targetDirectory"]!.ToString());
        Assert.Equal("failed", clone["status"]!.ToString());
        var cleanup = receipts.Single(r => r!["id"]!.ToString().Contains("/finally/step/", StringComparison.Ordinal))!;
        Assert.Equal("workflows/github-pr-review", JsonNode.Parse(cleanup["resolvedInput"]!["request"]!["parametersJson"]!.ToString())!["path"]!.ToString());
        Assert.Equal("completed", cleanup["status"]!.ToString());
        Assert.Equal(replay.Recording["responses"]!.AsArray().Select(r => r!["id"]!.ToString()), replay.RequestIds);
    }

    [Fact]
    public async Task ActualProducerContractRejectsTheSamePlanBeforeApprovalAndBoundsRecoveredRepair()
    {
        var current = await CurrentClone();
        var replay = new Replay(current); var state = await replay.Run();
        var finding = Assert.Single(state.Diagnostics);
        Assert.Equal("TASK_INPUT_TYPE", finding.Code); Assert.Equal("/tasks/clone_repository/inputs/targetDirectory", finding.Location);
        Assert.Contains("pattern", finding.Message); Assert.Contains("minLength", finding.Message);
        Assert.Contains("/tasks/parse_pr/resultType/fields/targetDirectory/type", finding.Message);
        Assert.Null(state.Graph); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal(new[] { finding.Location }, state.RevisionScope);
        var before = Recover(state);
        var proposal = Read("synthetic-shared-location")["proposal"]!.DeepClone();
        // The semantic revision is legitimate only for a new/revised plan, not this one-slot repair.
        replay.NextResponse = proposal;
        state = await new HybridWorkflowPlanner().AdvanceAsync(Recover(state), new() { ExpectedRevision = state.Revision }, replay, TestContext.Current.CancellationToken);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED");
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(before.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)));
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(before.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState), JsonSerializer.SerializeToNode(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState)));
        Assert.True(JsonNode.DeepEquals(before.Request.Options, state.Request.Options));
        Assert.Equal(before.RevisionScope, state.RevisionScope); Assert.Equal(before.ModelCalls + 1, state.ModelCalls);
        Assert.Equal(before.ReplanAttempts + 1, state.ReplanAttempts);
    }

    [Fact]
    public async Task ExplicitSharedLocationReachesReviewAndChangedContractInvalidatesApproval()
    {
        var current = await CurrentClone();
        var original = await new Replay().Run();
        var before = original.ComputeArtifactHash();
        var index = original.Catalog!.Capabilities.FindIndex(c => c.Id == current.Id);
        Assert.NotEqual(current.Version, original.Catalog.Capabilities[index].Version);
        original.Catalog.Capabilities[index] = current;
        Assert.NotEqual(before, original.ComputeArtifactHash());
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(original));
        var corrected = await new Replay(current, corrected: true).Run();
        Assert.True(corrected.Status == PlanningStatus.FinalReview, string.Join("; ", corrected.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        PlanningArtifactApproval.Verify(corrected);
        Assert.Equal(3, corrected.ModelCalls); Assert.Equal(0, corrected.ReplanAttempts);
        var tasks = corrected.Plan!.Root.Tasks.Concat(corrected.Plan.Root.Always).ToArray();
        Assert.DoesNotContain(tasks.Single(t => t.Id == "parse_pr").ResultType!.Fields, f => f.Name == "targetDirectory");
        Assert.Equal("clone_location", tasks.Single(t => t.Id == "clone_repository").Inputs.Single(i => i.Name == "targetDirectory").Value.Source);
        Assert.Equal("clone_location", tasks.Single(t => t.Id == "cleanup_clone").Inputs.Single(i => i.Name == "parametersJson").Value.Items[0].Members[0].Value.Source);
    }

    internal static async Task<PlanningCapability> CurrentClone()
    {
        var root = Directory.CreateTempSubdirectory("gnougo-clone-metadata-").FullName;
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "GnOuGo.Git.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            await using var factory = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions> { ["GnOuGo.Git.Mcp"] = new()
            {
                Type = "stdio", Command = executable,
                EnvironmentVariables = new Dictionary<string, string?> { ["Git__DefaultWorkingDirectory"] = root, ["OpenTelemetry__Enabled"] = "false" }
            } });
            var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
            var source = Assert.Single(await runtime.Capabilities.ListSourcesAsync(TestContext.Current.CancellationToken));
            var page = await runtime.Capabilities.ListAsync(source.Id, null, TestContext.Current.CancellationToken);
            while (page.Capabilities.All(c => c.Name != "git_clone") && page.NextCursor is not null)
                page = await runtime.Capabilities.ListAsync(source.Id, page.NextCursor, TestContext.Current.CancellationToken);
            var capability = await runtime.Capabilities.ResolveAsync(Assert.Single(page.Capabilities, c => c.Name == "git_clone"), TestContext.Current.CancellationToken);
            Assert.NotNull(capability.ArtifactContract);
            return capability;
        }
        finally { Directory.Delete(root, true); }
    }

    internal sealed class Replay(PlanningCapability? current = null, bool corrected = false, string correctedFixture = "synthetic-shared-location") : IPlanningRuntime, ICapabilityCatalog
    {
        internal readonly JsonObject Recording = Read("retained-clone-path");
        private readonly WorkflowPlanningRuntime actual = new(new(), (_, _) => Task.CompletedTask);
        public ICapabilityCatalog Capabilities => this;
        internal int Calls;
        internal readonly List<string> RequestIds = [];
        internal JsonNode? NextResponse;
        private CapabilityDiscoveryState Discovery => Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => Task.FromResult(Recording["initialCatalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!);
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>(Discovery.Sources);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null)
        {
            var page = Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor);
            if (current is not null)
                for (var i = 0; i < page.Capabilities.Count; i++)
                    if (page.Capabilities[i].Id == current.Id) page.Capabilities[i] = page.Capabilities[i] with { Version = current.Version, Description = current.Description, Operation = TaskOperations.Describe(current) };
            return Task.FromResult(page);
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(current?.Id == summary.Id ? current : Discovery.Resolved.Single(c => c.Id == summary.Id && c.Version == summary.Version));
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            RequestIds.Add(request.ClientRequestId!);
            if (Calls++ >= 3) return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(
                NextResponse ?? throw new InvalidOperationException("No further response or inference authorized."), request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) });
            var entry = Recording["responses"]![Calls - 1]!;
            if (current is null)
            {
                Assert.Equal(entry["id"]!.ToString(), request.ClientRequestId);
                Assert.True(JsonNode.DeepEquals(entry["schema"], request.StructuredOutputSchema));
            }
            return Task.FromResult(corrected && Calls == 3 ? new() { Json = PlanningCorpus.Transport(Read(correctedFixture)["proposal"],
                request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) } : entry["response"]!.Deserialize(PlanningJsonContext.Default.LLMResponse)!);
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new InvalidOperationException("No execution approval in this replay.");
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
        internal async Task<PlanningSession> Run()
        {
            var state = Recording["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
            var responses = Recording["responses"]!.AsArray();
            state.Request.Generation = responses[2]!["generation"]!.Deserialize(PlanningJsonContext.Default.PlanningGenerationOptions)!;
            for (var i = 0; i < 3; i++)
            {
                // Every historical response keeps its issued schema, even when testing a changed producer contract.
                // Only separately labelled synthetic corrections use the newly issued representation.
                if (!corrected || i < 2)
                {
                    var entry = responses[i]!; state.ModelCalls++;
                    state.PendingCall = new() { Id = entry["id"]!.ToString(), Purpose = entry["purpose"]!.ToString(),
                        Request = new() { ClientRequestId = entry["id"]!.ToString(), StructuredOutputSchema = entry["schema"]!.DeepClone() } };
                }
                state = await new HybridWorkflowPlanner().AdvanceAsync(Recover(state), new() { ExpectedRevision = state.Revision }, this, TestContext.Current.CancellationToken);
            }
            return state;
        }
    }
}
