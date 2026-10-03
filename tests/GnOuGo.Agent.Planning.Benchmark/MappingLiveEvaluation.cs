using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Persistence;
using GnOuGo.Flow.Planning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// A real stdio MCP with deliberately absent output metadata. Only this disposable source is available.
internal static class MappingLiveEvaluation
{
    internal const string Collection = "planning-mapping-evaluation";
    private const string Prompt = "Read the observation from the available fixture MCP. Extract the observed displayName as a business field named name, using extract mode; it may be supplied as JSON, plain text or HTML. Declare exactly one public workflow output named result, whose value is an object with exactly one required string field named name. Export the whole extraction result as result; do not flatten name into a public output. Use no caller inputs, no synthetic values, no defaults, no other external operations. Keep the original observation available to extraction.";
    internal static async Task ServeAsync(string path)
    {
        FixtureTools.Path = path;
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<FixtureTools>();
        await builder.Build().RunAsync();
    }
    [McpServerToolType]
    public sealed class FixtureTools
    {
        internal static string Path = "";
        [McpServerTool(Name = "observe", ReadOnly = true, UseStructuredContent = false), Description("Read the current observation. The unstructured payload may be JSON, plain text or HTML. No business output fields are declared.")]
        public static string Observe() => File.ReadAllText(Path);
    }
    internal static async Task RunAsync(string[] args, BenchmarkCampaign campaign, KeyVaultBenchmarkModel provider, string root)
    {
        var label = SchemaPortabilityCampaign.Option(args, "--run") ?? throw new ArgumentException("Supply --run for a fresh matrix.");
        if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Invalid run identity.");
        if (await campaign.LoadAsync(Collection, label) is not null) throw new InvalidOperationException("Matrix already retained; use a new identity, never overwrite failures.");
        var samples = new (string Id, string Source, string? Expected, bool ExtraField, bool Fault)[]
        {
            ("json-cold", "{\"displayName\":\"Observed alpha\"}", "Observed alpha", false, false),
            ("json-warm", "{\"displayName\":\"Observed alpha\"}", "Observed alpha", false, false),
            ("json-values", "{\"displayName\":\"Observed beta\"}", "Observed beta", false, false),
            ("json-shape", "{\"displayName\":\"Observed gamma\",\"extra\":true}", "Observed gamma", false, false),
            ("text-cold", "displayName: Observed text", "Observed text", false, false),
            ("html-cold", "<div><span data-field=\"displayName\">Observed &amp; HTML</span></div>", "Observed & HTML", false, false),
            ("target-change", "{\"displayName\":\"Observed delta\",\"reference\":\"REF-7\"}", "Observed delta", true, false),
            ("missing-required", "{\"unrelated\":\"plausible but unsupported\"}", null, false, false),
            ("injected-repair", "displayName: Observed repair", "Observed repair", false, true)
        };
        var evidence = new JsonObject { ["source_sha"] = SchemaPortabilityCampaign.Git("rev-parse", "HEAD"),
            ["harness_sha"] = SchemaPortabilityCampaign.Git("rev-parse", "HEAD"), ["prompt"] = Prompt, ["model"] = provider.Model,
            ["provider_policy_hash"] = provider.ConfigurationFingerprint, ["profile"] = 1, ["model_attempts_per_mapping"] = 2,
            ["reasoning"] = "medium", ["planning_input_limit"] = 96000, ["planning_output_limit"] = 32768,
            ["planning_attempt_limit"] = 8, ["planning_repair_limit"] = 2, ["mapping_output_limit"] = 8192,
            ["environment"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription + "; " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["accounting_before"] = await campaign.InspectAsync(),
            ["corpus_hash"] = PlanningGraphCompiler.Fingerprint(string.Join('\n', samples.Select(s => s.ToString()))), ["runs"] = new JsonArray() };
        await campaign.SaveAsync(Collection, label, evidence);
        var work = Path.Combine(Path.GetTempPath(), "gnougo-mapping-live-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var source = Path.Combine(work, "observation.txt"); await File.WriteAllTextAsync(source, samples[0].Source);
        try
        {
            await using var transport = new ConfiguredMcpClientFactory(new Dictionary<string, GnOuGo.AI.Core.McpServerOptions>
            { ["fixture"] = new() { Type = "stdio", Command = "dotnet", Args = [typeof(MappingLiveEvaluation).Assembly.Location, "--mapping-fixture", source] } });
            var measured = new Measured(provider, label);
            var engine = new WorkflowEngine { McpClientFactory = transport, LLMClient = measured, HumanInputProvider = new Approved(),
                LlmDefaults = new() { Model = provider.Model, Provider = provider.Provider } };
            var runtime = new WorkflowPlanningRuntime(engine, async (state, ct) =>
            { evidence["planning_session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession); await campaign.SaveAsync(Collection, label, evidence, ct); });
            var catalog = await runtime.DiscoverAsync(new(), CancellationToken.None);
            var reads = 0;
            foreach (var server in await runtime.Capabilities.ListSourcesAsync(CancellationToken.None))
                foreach (var capability in (await runtime.Capabilities.ListAsync(server.Id, null, CancellationToken.None)).Capabilities)
                { catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(capability, CancellationToken.None)); reads++; }
            var producer = catalog.Capabilities.Single(c => c.Method == "observe");
            if (producer.OutputSchema.Count != 0) throw new InvalidOperationException("Fixture unexpectedly advertises an output schema.");
            evidence["catalog_hash"] = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(catalog, PlanningJsonContext.Default.PlanningCatalog));
            var session = new PlanningSession { Catalog = catalog, Request = new() { SessionId = label + "-plan", TenantId = "benchmark",
                Prompt = Prompt, MaxModelCalls = 8, MaxReplanAttempts = 2, Generation = new() { Reasoning = "medium", MaxInputTokensPerRequest = 96000, MaxOutputTokens = 32768 } } };
            var timer = Stopwatch.StartNew(); var planner = new HybridWorkflowPlanner();
            for (var i = 0; i < 12 && !PlanningStatus.IsWaiting(session.Status) && !PlanningStatus.IsTerminal(session.Status); i++)
                session = await planner.AdvanceAsync(session, new() { ExpectedRevision = session.Revision }, runtime, CancellationToken.None);
            evidence["planning_ms"] = timer.ElapsedMilliseconds; evidence["planning_calls"] = session.ModelCalls; evidence["repairs"] = session.ReplanAttempts; evidence["discovery_reads"] = reads;
            evidence["planning_usage_receipts"] = new JsonArray(measured.Receipts.Select(r => (JsonNode)r.DeepClone()).ToArray());
            if (session.Status != PlanningStatus.FinalReview) throw new InvalidOperationException("Planning did not reach final review: " + session.Status);
            var tasks = All(session.Plan!.Root).ToArray();
            if (tasks.Count(t => t.Kind == "operation") != 1 || tasks.Single(t => t.Kind == "operation").Operation != producer.Id ||
                tasks.Count(t => t.Kind == "transform" && t.Mode == "extract") != 1 || session.Plan.Inputs.Count != 0 ||
                session.Plan.Root.Outputs.Count != 1 || session.Plan.Root.Outputs[0].Name != "result")
                throw new InvalidOperationException("The generated workflow did not implement the frozen operation/extraction oracle.");
            session = await planner.AdvanceAsync(session, new() { Kind = "approve", ExpectedRevision = session.Revision, ArtifactHash = PlanningArtifactApproval.Hash(session) }, runtime, CancellationToken.None);
            if (session.Status != PlanningStatus.Approved) throw new InvalidOperationException("Artifact approval failed.");
            var store = EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory: root);
            foreach (var sample in samples)
            {
                await File.WriteAllTextAsync(source, sample.Source);
                // Explicit fixture revision for the changed target; no runtime change to the plan.
                var plan = JsonSerializer.Deserialize(JsonSerializer.Serialize(session.Plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
                if (sample.ExtraField) All(plan.Root).Single(t => t.Mode == "extract").ResultType!.Fields.Add(new() { Name = "reference", Type = new() });
                var compilation = new TaskPlanCompiler().Compile(plan, catalog);
                if (compilation.Graph is null || compilation.Diagnostics.Count != 0) throw new InvalidOperationException("Fixture revision failed contract closure.");
                PlanningConfirmationGuards.Apply(compilation.Graph, catalog);
                var yaml = new PlanningGraphCompiler().Compile(compilation.Graph, catalog);
                var row = new JsonObject { ["case"] = sample.Id, ["phase"] = sample.Fault ? "fault-injection" : "live-provider", ["artifact_hash"] = PlanningGraphCompiler.Fingerprint(yaml), ["source_hash"] = PlanningGraphCompiler.Fingerprint(sample.Source) };
                evidence["runs"]!.AsArray().Add(row); await campaign.SaveAsync(Collection, label, evidence);
                measured.Execution = true; measured.Fault = sample.Fault; var before = measured.Calls; var beforeUsage = measured.Receipts.Count; var telemetry = new MappingTelemetry(); timer.Restart();
                engine = new WorkflowEngine { McpClientFactory = transport, LLMClient = measured, HumanInputProvider = new Approved(), RunStore = store, Telemetry = telemetry,
                    Limits = new() { TenantId = "mapping-" + label, RunId = label + "-" + sample.Id }, LlmDefaults = new() { Model = provider.Model, Provider = provider.Provider } };
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"];
                var result = await engine.ExecuteAsync(workflow, new JsonObject(), timeout.Token);
                row["execution_ms"] = timer.ElapsedMilliseconds; row["model_calls"] = measured.Calls - before; row["success"] = result.Success; row["error"] = result.Error?.Code;
                row["outputs"] = result.Outputs?.DeepClone(); row["mapping_telemetry"] = telemetry.Mappings;
                row["usage_receipts"] = new JsonArray(measured.Receipts.Skip(beforeUsage).Select(r => (JsonNode)r.DeepClone()).ToArray());
                row["injected_attempts"] = sample.Fault ? 1 : 0;
                var actual = result.Outputs?["result"];
                row["oracle"] = sample.Expected is null ? !result.Success && result.Error?.Code == "CONTRACT_UNSATISFIED" : result.Success && actual?["name"]?.ToString() == sample.Expected && (!sample.ExtraField || actual?["reference"]?.ToString() == "REF-7");
                if (sample.Id is "json-warm" or "json-values") row["oracle"] = row["oracle"]!.GetValue<bool>() && measured.Calls == before;
                await campaign.SaveAsync(Collection, label, evidence); Console.WriteLine(row.ToJsonString());
                if (!row["oracle"]!.GetValue<bool>()) break;
            }
        }
        catch (Exception ex) { evidence["failure_type"] = ex.GetType().Name; evidence["failure"] = ex.Message; }
        finally
        {
            evidence["accounting"] = await campaign.InspectAsync();
            evidence["passed"] = evidence["runs"]!.AsArray().Count == samples.Length && evidence["runs"]!.AsArray().All(r => r?["oracle"]?.GetValue<bool>() == true);
            await campaign.SaveAsync(Collection, label, evidence); Console.WriteLine(new JsonObject { ["run"] = label, ["passed"] = evidence["passed"]!.DeepClone(), ["failure"] = evidence["failure"]?.DeepClone(), ["cases"] = evidence["runs"]!.AsArray().Count }.ToJsonString());
            if (!evidence["passed"]!.GetValue<bool>()) Environment.ExitCode = 1;
            Directory.Delete(work, true);
        }
    }
    private static IEnumerable<PlanTask> All(TaskScope scope) => scope.Tasks.Concat(scope.Always).SelectMany(t => new[] { t }.Concat(t.Body is null ? [] : All(t.Body)));
    private sealed class Approved : IHumanInputProvider
    { public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => Task.FromResult<JsonNode?>(new JsonObject { ["response"] = true }); }
    private sealed class MappingTelemetry : IWorkflowTelemetry
    {
        internal JsonArray Mappings { get; } = [];
        private sealed class Span : IWorkflowSpan, IStepSpan
        {
            internal JsonObject Values { get; } = new();
            public void SetAttribute(string key, object? value)
            { if (key.StartsWith("gnougo.mapping.", StringComparison.Ordinal)) Values[key] = JsonSerializer.SerializeToNode(value); }
            public void Dispose() { }
        }
        public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info) => new Span();
        public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result) { }
        public IStepSpan StepStart(ITelemetrySpan parent, StepTelemetryInfo info) => new Span();
        public void StepEnd(IStepSpan span, StepResultInfo result)
        { if (span is Span { Values.Count: > 0 } current) Mappings.Add(current.Values.DeepClone()); }
    }
    private sealed class Measured(KeyVaultBenchmarkModel provider, string label) : ILLMClient
    {
        internal int Calls; internal bool Execution; internal bool Fault; internal List<JsonObject> Receipts = [];
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            if (Fault) { Fault = false; return new LLMResponse { Json = new JsonObject { ["script"] = "({name:'injected unsupported value'})" } }; }
            request = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
            request.ClientRequestId = label + (Execution ? "-execution:" : ":") + Calls + ":" + request.ClientRequestId;
            request.Reasoning = "medium";
            var response = await (Execution ? provider.CallExecutionAsync(request, ct) : provider.CallAsync(request, ct));
            Receipts.Add(new() { ["request_id"] = request.ClientRequestId, ["usage"] = response.Usage?.DeepClone() });
            return response;
        }
    }
}
