using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Cmd.Mcp;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedWorkspaceContractTests
{
    internal const string Location = "workflows/github-pr-auto-review/project";
    internal static JsonObject Recording() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "WorkspaceContracts", "retained-workspace.json")))!.AsObject();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        Assert.Equal(0, replay.Calls);
    }

    [Fact]
    public void StaticPlanUsesCurrentProducerContractsAndExactWorkspace()
    {
        using var current = new CurrentContracts();
        var replay = new Replay(); var state = replay.State(replay.Recording["finalSession"]!);
        var plan = Corrected(state.Plan!);
        state.Catalog!.Capabilities = state.Catalog.Capabilities.Select(c => current.Replace(c) ?? c).ToList();
        var result = RecordedPlanCompilation.CompileStatic(new() { Plan = plan, Requirements = state.Requirements }, state.Catalog, state.Request);
        Assert.True(result.Status == PlanningStatus.FinalReview, string.Join(';', result.Diagnostics));
        Assert.DoesNotContain(result.Plan!.Inputs, i => i.Name == "tenantId");
        var scope = result.Graph!.Workflows.SelectMany(w => w.Steps).Single(n => n.Type == "agent.run");
        Assert.Equal(Location, scope.Input.Members.Single(m => m.Name == "workspace").Value.Text);
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(result));
    }

    [Fact]
    public void CurrentCmdContractRejectsInventedNestedParametersBeforeGraphEmission()
    {
        using var current = new CurrentContracts(); var replay = new Replay(); var state = replay.State(replay.Recording["finalSession"]!);
        var cmd = state.Catalog!.Capabilities.Single(c => c.Method == "cmd_run");
        state.Catalog.Capabilities[state.Catalog.Capabilities.IndexOf(cmd)] = current.Replace(cmd)!;
        var parameterPort = TaskOperations.Describe(state.Catalog.Capabilities.Single(c => c.Id == cmd.Id)).Inputs.Single(i => i.Name == "parameters");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["recursive"] = true }, parameterPort.Schema));
        var plan = Corrected(state.Plan!);
        var cleanup = plan.Root.Always.Single(t => t.Id == "cleanup_clone");
        cleanup.Inputs.Single(i => i.Name == "parameters").Value.Members.Add(new("recursive", new() { Kind = "boolean", Boolean = true }));
        var compiled = new TaskPlanCompiler().Compile(plan, state.Catalog);
        Assert.Null(compiled.Graph);
        Assert.Contains(compiled.Diagnostics, d => d.Location.StartsWith("/tasks/cleanup_clone", StringComparison.Ordinal));
    }

    internal static TaskPlan Corrected(TaskPlan original)
    {
        var plan = JsonSerializer.Deserialize(JsonSerializer.Serialize(original, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        plan.Inputs.RemoveAll(i => i.Name == "tenantId");
        var agent = plan.Root.Tasks.Single(t => t.Id == "validate_project");
        agent.Inputs[agent.Inputs.FindIndex(i => i.Name == "workspace")] = new("workspace", new() { Kind = "output", Source = "paths", Port = "targetDirectory" });
        plan.Root.Tasks.Single(t => t.Id == "copilot_review").Inputs.RemoveAll(i => i.Name == "tenantId");
        var cleanup = plan.Root.Always.Single(t => t.Id == "cleanup_clone");
        cleanup.Inputs[cleanup.Inputs.FindIndex(i => i.Name == "parametersJson")] = new("parameters", new() { Kind = "object", Members = [new("path", new() { Kind = "output", Source = "paths", Port = "targetDirectory" })] });
        return plan;
    }

    private static int Estimate(LLMRequest r) => (System.Text.Encoding.UTF8.GetByteCount(r.Prompt) + System.Text.Encoding.UTF8.GetByteCount(r.StructuredOutputSchema!.ToJsonString()) + 2) / 3 + 256;

    internal sealed class CurrentContracts : IDisposable
    {
        internal readonly string Root = Directory.CreateTempSubdirectory("gnougo-current-contracts-").FullName;
        internal readonly CommandPolicy Policy;
        private readonly Dictionary<string, ModelContextProtocol.Protocol.Tool> tools = [];
        internal CurrentContracts()
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClonePlanning", "cmdsettings.json"))
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Cmd:DefaultWorkingDirectory"] = Root }).Build();
            var settings = new CmdServerSettings();
            ((IConfigureOptions<CmdServerSettings>)Activator.CreateInstance(typeof(CmdTools).Assembly.GetType("GnOuGo.Cmd.Mcp.CmdServerSettingsOptionsConfigurator")!, configuration)!).Configure(settings);
            Policy = new(settings, Root);
            Capture(typeof(CmdTools), "GnOuGo.Cmd.Mcp.CmdMcpJson");
            var assembly = typeof(GnOuGo.GithubCopilot.Mcp.CodePolicy).Assembly;
            Capture(assembly.GetType("GnOuGo.GithubCopilot.Mcp.CopilotTools")!, "GnOuGo.GithubCopilot.Mcp.CodeMcpJson");
        }
        private void Capture(Type type, string serializer)
        {
            var options = (JsonSerializerOptions)type.Assembly.GetType(serializer)!.GetProperty("SerializerOptions", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            var services = new ServiceCollection(); services.AddLogging(); services.AddMcpServer().WithTools([type], options);
            using var provider = services.BuildServiceProvider();
            foreach (var tool in provider.GetServices<McpServerTool>()) tools.Add(tool.ProtocolTool.Name, tool.ProtocolTool);
        }
        internal PlanningCapability? Replace(PlanningCapability old)
        {
            if (old.Method is null || !tools.TryGetValue(old.Method, out var tool)) return null;
            var c = JsonSerializer.Deserialize(JsonSerializer.Serialize(old, PlanningJsonContext.Default.PlanningCapability), PlanningJsonContext.Default.PlanningCapability)!;
            c.InputSchema = JsonNode.Parse((old.Method == "cmd_run" ? Policy.BuildCmdRunInputSchema(tool.InputSchema) : tool.InputSchema).GetRawText())!.AsObject();
            c.Description = old.Method == "cmd_run" ? Policy.BuildCmdRunToolDescription() : tool.Description!;
            c.Operation = null; c.Version = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(c, PlanningJsonContext.Default.PlanningCapability));
            return c;
        }
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }

    internal sealed class Replay : IPlanningRuntime, ICapabilityCatalog
    {
        internal readonly JsonObject Recording = RecordedWorkspaceContractTests.Recording();
        internal readonly Dictionary<string, PlanningCapability> Replacements = [];
        internal JsonNode? Expected;
        internal int Calls => 0;
        private readonly WorkflowPlanningRuntime actual = new(new(), (_, _) => Task.CompletedTask);
        public ICapabilityCatalog Capabilities => this;
        internal PlanningSession State(JsonNode compact)
        {
            var payload = compact.DeepClone().AsObject();
            foreach (var key in new[] { "recordedPages", "recordedResolved", "recordedCatalogIds", "recordedInspections", "presentationQuery" }) payload.Remove(key);
            var s = payload.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
            var ids = compact["recordedCatalogIds"]!.AsArray().Select(n => n!.ToString()).ToHashSet();
            s.Catalog = Recording["catalog"]!.Deserialize(PlanningJsonContext.Default.PlanningCatalog)!; s.Catalog.Capabilities.RemoveAll(c => !ids.Contains(c.Id));
            s.Discovery = Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
            s.Discovery.Pages = s.Discovery.Pages.Take(compact["recordedPages"]!.GetValue<int>()).ToList();
            var resolved = compact["recordedResolved"]!.AsArray().Select(n => n!.ToString()).ToHashSet(); s.Discovery.Resolved.RemoveAll(c => !resolved.Contains(c.Id));
            s.Discovery.Inspections = compact["recordedInspections"]?.Deserialize(PlanningJsonContext.Default.ListPlanningDiscoveryRequest);
            s.Discovery.PresentationQuery = compact["presentationQuery"]?.GetValue<string>();
            Update(s); return s;
        }
        internal void Update(PlanningSession state)
        {
            state.Catalog!.Capabilities = state.Catalog.Capabilities.Select(c => Replacements.GetValueOrDefault(c.Id) ?? c).ToList();
            state.Discovery.Resolved = state.Discovery.Resolved.Select(c => Replacements.GetValueOrDefault(c.Id) ?? c).ToList();
            state.Discovery.Pages = state.Discovery.Pages.Select(p => p with { Capabilities = p.Capabilities.Select(Replace).ToList() }).ToList();
        }
        private CapabilitySummary Replace(CapabilitySummary s) => Replacements.TryGetValue(s.Id, out var c) ? s with { Version = c.Version, Description = c.Description, Operation = TaskOperations.Describe(c) } : s;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest r, CancellationToken ct) => throw new InvalidOperationException("Retained catalog only");
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException("Retained sources only");
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        {
            var discovery = Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!;
            var page = discovery.Pages.Single(p => p.SourceId == sourceId && p.Cursor == cursor && p.Query == query && p.ProducedArtifactKind == producedArtifactKind);
            return Task.FromResult(page with { Capabilities = page.Capabilities.Select(Replace).ToList() });
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary s, CancellationToken ct) => Task.FromResult(Replacements.GetValueOrDefault(s.Id) ?? Recording["discovery"]!.Deserialize(PlanningJsonContext.Default.CapabilityDiscoveryState)!.Resolved.Single(c => c.Id == s.Id && c.Version == s.Version));
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct) =>
            throw new InvalidOperationException("Retired planning recordings are read-only fixtures and cannot dispatch.");
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new InvalidOperationException("No execution approval");
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
    }
}
