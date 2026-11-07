using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedArtifactPrerequisiteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Location = "/tasks/copilot-review/inputs/filesJson";
    private static string Snapshot(PlanningSession s) => JsonSerializer.Serialize(s, PlanningJsonContext.Default.PlanningSession);
    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);
    private static int Estimate(LLMRequest r) => (Bytes(r.Prompt) + Bytes(r.StructuredOutputSchema!.ToJsonString()) + 2) / 3 + 256;

    [Fact]
    public async Task RetiredResponsesRequireRevisionAndRetainOriginalRequestsAndAccounting()
    {
        var replay = new Replay();
        foreach (var entry in replay.Recording["responses"]!.AsArray())
        {
            replay.Expected = entry!.AsObject();
            var pending = replay.State(entry["pendingSession"]!);
            await RecordedPlanCompilation.RetiredAsync(pending, replay);
        }
        Assert.Empty(replay.Requests);
    }

    [Fact]
    public async Task RealRetainedContractsForwardExactComparisonEvidenceWithMockedEffectsOnly()
    {
        var replay = new Replay();
        var clone = replay.Contracts.Single(c => c.Method == "git_clone"); var compare = replay.Producer;
        var review = replay.Contracts.Single(c => c.Method == "copilot_review");
        var operations = new[] { clone, compare, review };
        const string root = "workflows/example", baseRef = "base-revision", headRef = "head-revision";
        const string filesJson = """[{"path":"src/évidence.cs","previousPath":null,"status":"modified","patch":"-old\n+new","isBinary":false,"isSubmodule":false,"truncated":false,"linesAdded":1,"linesDeleted":1,"oldObjectId":"old","newObjectId":"new"}]""";
        var effects = new List<string>(); var factory = new InMemoryMcpClientFactory();
        foreach (var group in operations.GroupBy(c => c.Server!))
        {
            var config = new MockMcpServerConfig();
            foreach (var c in group)
            {
                config.Tools.Add(new() { Name = c.Method!, Description = c.Description, InputSchema = c.InputSchema, OutputSchema = c.OutputSchema,
                    EffectKind = c.EffectKind, ArtifactContract = new(c.ArtifactContract, []), CompositionContract = new(c.Composition, []) });
                config.ToolHandlers[c.Method!] = args =>
                {
                    Assert.Empty(PlanningContractValidation.ValidateInstance(args, c.InputSchema)); effects.Add(c.Id);
                    JsonObject response;
                    if (c.Id == clone.Id)
                        response = new() { ["repositoryRoot"] = "/workspace/" + root, ["remoteUrl"] = "https://example.test/repository.git", ["branch"] = null, ["projectRootRelative"] = root };
                    else if (c.Id == compare.Id)
                    {
                        Assert.Equal(root, args!["projectRoot"]!.ToString()); Assert.Equal(baseRef, args["baseRef"]!.ToString()); Assert.Equal(headRef, args["headRef"]!.ToString());
                        response = new() { ["repositoryRoot"] = "/workspace/" + root, ["baseRef"] = baseRef, ["headRef"] = headRef, ["baseSha"] = baseRef, ["headSha"] = headRef,
                            ["mergeBaseSha"] = null, ["comparedFromSha"] = baseRef, ["files"] = JsonNode.Parse(filesJson), ["filesJson"] = filesJson, ["totalFiles"] = 1, ["offset"] = 0,
                            ["pageSize"] = 100, ["hasMore"] = false, ["nextCursor"] = null, ["totalPatchCharacters"] = 9, ["truncatedFileCount"] = 0 };
                    }
                    else
                    {
                        Assert.Equal(root, args!["projectRoot"]!.ToString()); Assert.Equal(filesJson, args["filesJson"]!.ToString());
                        Assert.Equal(baseRef, args["baseSha"]!.ToString()); Assert.Equal(headRef, args["headSha"]!.ToString());
                        response = new() { ["baseSha"] = baseRef, ["headSha"] = headRef, ["findings"] = new JsonArray(), ["rejectedFindings"] = new JsonArray(), ["summary"] = "Mocked result",
                            ["coverage"] = new JsonObject { ["totalFiles"] = 1, ["reviewedFiles"] = 1, ["skippedFiles"] = 0, ["truncatedFiles"] = 0, ["skippedPaths"] = new JsonArray(), ["truncatedPaths"] = new JsonArray() } };
                    }
                    Assert.Empty(PlanningContractValidation.ValidateInstance(response, c.OutputSchema));
                    return new() { Content = response };
                };
            }
            factory.RegisterServer(group.Key, config);
        }
        static TaskValue Text(string value) => new() { Kind = "string", Text = value };
        static TaskValue Port(string task, string port) => new() { Kind = "output", Source = task, Port = port };
        var plan = new TaskPlan { Root = new() { Tasks = [
            new() { Id = "allocate", Kind = "operation", Objective = "Allocate", Operation = clone.Id, Inputs = [new("remoteUrl", Text("https://example.test/repository.git")), new("targetDirectory", Text(root))] },
            new() { Id = "compare", Kind = "operation", Objective = "Compare", Operation = compare.Id, Inputs = [new("projectRoot", Port("allocate", "projectRootRelative")), new("baseRef", Text(baseRef)), new("headRef", Text(headRef))] },
            new() { Id = "consume", Kind = "operation", Objective = "Inspect declared evidence", Operation = review.Id, Inputs = [new("projectRoot", Port("allocate", "projectRootRelative")), new("filesJson", Port("compare", "filesJson")), new("baseSha", Port("compare", "baseSha")), new("headSha", Port("compare", "headSha"))] }
        ], Outputs = [new("result", new() { Kind = "output", Source = "consume" })] } };
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, Ct);
        catalog.Capabilities.AddRange(operations);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics); Assert.NotNull(compilation.Graph);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(operations.Select(c => c.Id), effects);
        Assert.Equal("Mocked result", result.Outputs!["result"]!["summary"]!.ToString());
        plan.Root.Tasks[2].Inputs[1] = new("filesJson", Text(filesJson));
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_ARTIFACT_BINDING");
    }

    private static string BindSanitizedReplay(PlanningSession state)
    {
        var authority = PlanningGraphCompiler.Fingerprint(new JsonObject
        {
            ["tenant"] = state.Request.TenantId, ["session"] = state.Request.SessionId,
            ["baseline"] = JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan),
            ["scope"] = new JsonArray(state.RevisionScope.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            ["catalog"] = JsonSerializer.SerializeToNode(state.Catalog, PlanningJsonContext.Default.PlanningCatalog),
            ["resolved"] = JsonSerializer.SerializeToNode(state.Discovery.Resolved, PlanningJsonContext.Default.ListPlanningCapability),
            ["policy"] = JsonSerializer.SerializeToNode(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy),
            ["maxModelCalls"] = state.Request.MaxModelCalls, ["maxReplanAttempts"] = state.Request.MaxReplanAttempts,
            ["generation"] = JsonSerializer.SerializeToNode(state.Request.Generation, PlanningJsonContext.Default.PlanningGenerationOptions),
            ["options"] = state.Request.Options.DeepClone()
        }.ToJsonString());
        var prompt = state.PendingCall!.Request.Prompt; var split = prompt.IndexOf("\n{", StringComparison.Ordinal);
        var context = JsonNode.Parse(prompt[(split + 1)..])!;
        Assert.NotEqual(authority, context["repair"]!["authority"]!.ToString());
        // Preserve formatting and every other byte of the original sanitized prompt.
        return prompt.Replace(context["repair"]!["authority"]!.ToString(), authority, StringComparison.Ordinal);
    }

    private sealed class Replay : IPlanningRuntime, ICapabilityCatalog
    {
        private static JsonObject Read(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ArtifactPrerequisites", name + ".json")))!.AsObject();
        internal readonly JsonObject Recording = Read("retained-prerequisites");
        internal readonly PlanningCapability Producer = Read("declared-producer").Deserialize(PlanningJsonContext.Default.PlanningCapability)!;
        internal JsonObject? Expected;
        internal string? SanitizedReplayPrompt = null;
        internal PlanningSession? Pending;
        internal readonly List<LLMRequest> Requests = [];
        internal int FilterReads, Resolutions;
        private readonly WorkflowPlanningRuntime actual = new(new(), (_, _) => Task.CompletedTask);
        private CapabilityDiscoveryState Discovery => Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
        internal IEnumerable<PlanningCapability> Contracts => Discovery.Resolved.Concat(Recording["catalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!.Capabilities).Append(Producer).DistinctBy(c => (c.Id, c.Version));
        public ICapabilityCatalog Capabilities => this;
        internal PlanningSession State(JsonNode compact)
        {
            var json = compact.DeepClone().AsObject();
            var pages = json["recordedPages"]!.GetValue<int>();
            var resolved = json["recordedResolved"]!.AsArray().Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            var ids = json["recordedCatalogIds"]!.AsArray().Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            var query = json["presentationQuery"]?.GetValue<string>(); var inspections = json["recordedInspections"]?.DeepClone();
            foreach (var key in new[] { "recordedPages", "recordedResolved", "recordedCatalogIds", "presentationQuery", "recordedInspections" }) json.Remove(key);
            var state = json.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
            state.Catalog = Recording["catalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!; state.Catalog.Capabilities.RemoveAll(c => !ids.Contains(c.Id));
            state.Discovery = Discovery; state.Discovery.Pages = state.Discovery.Pages.Take(pages).ToList(); state.Discovery.Resolved.RemoveAll(c => !resolved.Contains(c.Id));
            state.Discovery.PresentationQuery = query; state.Discovery.Inspections = inspections?.Deserialize(PlanningJsonContext.Default.ListPlanningDiscoveryRequest);
            return state;
        }
        internal TaskPlan Corrected()
        {
            var plan = State(Recording["finalSession"]!).Plan!;
            static TaskValue Port(string task, string port) => new() { Kind = "output", Source = task, Port = port };
            plan.Root.Tasks.Insert(plan.Root.Tasks.FindIndex(t => t.Id == "copilot-review"), new()
            {
                Id = "explicit-comparison", Kind = "operation", Objective = "Compare the requested revisions and return declared file evidence", Operation = Producer.Id,
                Inputs = [new("projectRoot", Port("clone-pr", "projectRootRelative")), new("baseRef", Port("pr-runtime", "baseSha")), new("headRef", Port("pr-runtime", "headSha"))]
            });
            var consumer = plan.Root.Tasks.Single(t => t.Id == "copilot-review");
            consumer.Inputs[consumer.Inputs.FindIndex(i => i.Name == "filesJson")] = new("filesJson", Port("explicit-comparison", "filesJson"));
            return plan;
        }
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => throw new InvalidOperationException("Catalog retained");
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException("Sources retained");
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        {
            if (producedArtifactKind is null) return Task.FromResult(Discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor && p.Query == query));
            Assert.Null(cursor); FilterReads++;
            var names = Discovery.Pages.SelectMany(p => p.Capabilities).Where(c => c.SourceId == sourceId).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var servers = Contracts.Where(c => names.Contains(c.Id)).Select(c => c.Server).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var matches = Contracts.Where(c => servers.Contains(c.Server ?? "") && c.ArtifactContract?.Produces.Any(a => a.Kind == producedArtifactKind) == true)
                .Select(c => new CapabilitySummary(c.Id, sourceId, c.Method!, c.Description, c.StepType, c.EffectKind, c.Version, c.Composition, TaskOperations.Describe(c), c.ArtifactContract)).ToList();
            return Task.FromResult(new CapabilityPage(sourceId, cursor, matches, null, Query: query, ProducedArtifactKind: producedArtifactKind));
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
        { Resolutions++; return Task.FromResult(Contracts.Single(c => c.Id == summary.Id && c.Version == summary.Version)); }
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct) =>
            throw new InvalidOperationException("Retired planning recordings are read-only fixtures and cannot dispatch.");
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new InvalidOperationException("No external approval");
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct)
        {
            if (session.PendingCall is not null) Pending = JsonSerializer.Deserialize(Snapshot(session), PlanningJsonContext.Default.PlanningSession)!;
            return Task.CompletedTask;
        }
    }
}
