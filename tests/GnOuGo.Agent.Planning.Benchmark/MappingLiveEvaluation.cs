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
        public static string Observe(int? index = null)
        {
            var content = File.ReadAllText(Path);
            return index is null ? content : JsonNode.Parse(content)!.AsArray()[index.Value]!.GetValue<string>();
        }
    }
    internal static async Task RunAsync(string[] args, BenchmarkCampaign campaign, KeyVaultBenchmarkModel provider, string root)
    {
        var label = SchemaPortabilityCampaign.Option(args, "--run") ?? throw new ArgumentException("Supply a fresh --run.");
        if (label.Length > 80 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Invalid run identity.");
        var variant = SchemaPortabilityCampaign.Option(args, "--variant") ?? "scalar";
        if (variant is not ("scalar" or "extended" or "each")) throw new ArgumentException("Choose scalar, extended or each.");
        var revision = SchemaPortabilityCampaign.Git("rev-parse", "HEAD");
        var evidence = await campaign.LoadAsync(Collection, label);
        if (evidence is not null && (evidence["source_sha"]?.ToString() != revision || evidence["provider_policy_hash"]?.ToString() != provider.ConfigurationFingerprint))
            throw new InvalidOperationException("Probe source/configuration changed; use a new identity.");
        var work = Directory.CreateTempSubdirectory("gnougo-mapping-probe-").FullName;
        var source = Path.Combine(work, "observation.txt");
        await File.WriteAllTextAsync(source, "{}");
        try
        {
            var human = new LiveWorkflowEvaluation.ConsoleHuman(campaign, label);
            await using var transport = new ConfiguredMcpClientFactory(new Dictionary<string, GnOuGo.AI.Core.McpServerOptions>
                { ["fixture"] = new() { Type = "stdio", Command = "dotnet", Args = [typeof(MappingLiveEvaluation).Assembly.Location, "--mapping-fixture", source] } });
            var measured = new Measured(provider, label);
            var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = transport }, (_, _) => Task.CompletedTask);
            var catalog = await runtime.DiscoverAsync(new(), CancellationToken.None);
            foreach (var server in await runtime.Capabilities.ListSourcesAsync(CancellationToken.None))
                foreach (var capability in (await runtime.Capabilities.ListAsync(server.Id, null, CancellationToken.None)).Capabilities)
                    catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(capability, CancellationToken.None));
            var producer = catalog.Capabilities.Single(c => c.Method == "observe");
            if (producer.OutputSchema.Count != 0) throw new InvalidOperationException("Fixture unexpectedly advertises output fields.");
            var catalogHash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(catalog, PlanningJsonContext.Default.PlanningCatalog));
            if (evidence is null)
            {
                evidence = new JsonObject { ["source_sha"] = revision, ["harness_sha"] = revision, ["model"] = provider.Model,
                    ["provider_policy_hash"] = provider.ConfigurationFingerprint, ["catalog_hash"] = catalogHash,
                    ["profile"] = GnOuGo.Flow.Core.Scripting.JintSandbox.MappingProfileVersion, ["model_attempts_per_mapping"] = 2,
                    ["readiness"] = await provider.ReadinessAsync(CancellationToken.None), ["accounting_before"] = await campaign.InspectAsync(),
                    ["planning_calls"] = 0, ["repairs"] = 0, ["artifacts"] = new JsonObject(), ["runs"] = new JsonArray(),
                    ["corpus_hash"] = PlanningGraphCompiler.Fingerprint(string.Join('\n', Samples().Select(s => s.ToString()))) };
                foreach (var kind in new[] { "scalar", "extended", "each" })
                {
                    var state = Prepare(catalog, producer.Id, label + "-" + kind, kind);
                    evidence["artifacts"]![kind] = new JsonObject { ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession),
                        ["artifact_hash"] = state.ComputeArtifactHash() };
                }
                await campaign.SaveAsync(Collection, label, evidence);
            }
            if (evidence["catalog_hash"]?.ToString() != catalogHash) throw new InvalidOperationException("Probe discovery contracts changed.");
            var retained = evidence["artifacts"]?[variant]?.AsObject() ?? throw new InvalidOperationException("Legacy probe cannot be resumed.");
            var review = SchemaPortabilityCampaign.Option(args, "--review-command");
            if (review is null)
            {
                var state = retained["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
                Console.WriteLine(new JsonObject { ["status"] = "waiting_for_explicit_review", ["run"] = label, ["variant"] = variant,
                    ["revision"] = state.Revision, ["artifact_hash"] = state.ComputeArtifactHash(),
                    ["requirements"] = retained["session"]!["requirements"]!.DeepClone(), ["plan"] = retained["session"]!["plan"]!.DeepClone(),
                    ["review"] = PlanningReviewFormatter.TaskDiagram(state.Plan) }.ToJsonString());
                return;
            }
            var command = JsonSerializer.Deserialize(await File.ReadAllTextAsync(review), PlanningJsonContext.Default.PlanningCommand) ?? throw new ArgumentException("Supply an explicit approval command.");
            var approved = await LiveWorkflowEvaluation.ApproveAsync(retained, command, runtime, CancellationToken.None);
            var readiness = await provider.ReadinessAsync(CancellationToken.None);
            if (readiness["ready"]?.GetValue<bool>() != true) throw new InvalidOperationException("Exact deployment allowances remain unavailable; no inference dispatched.");
            retained["session"] = JsonSerializer.SerializeToNode(approved, PlanningJsonContext.Default.PlanningSession);
            retained["execution_started"] = DateTimeOffset.UtcNow.ToString("O");
            await campaign.SaveAsync(Collection, label, evidence);
            foreach (var sample in Samples().Where(s => s.Variant == variant))
            {
                await File.WriteAllTextAsync(source, sample.Source);
                var row = new JsonObject { ["case"] = sample.Id, ["phase"] = sample.Fault ? "fault-injection" : "live-provider",
                    ["artifact_hash"] = approved.ApprovedHash, ["source_hash"] = PlanningGraphCompiler.Fingerprint(sample.Source),
                    ["source_bytes"] = System.Text.Encoding.UTF8.GetByteCount(sample.Source), ["execution_started"] = DateTimeOffset.UtcNow.ToString("O") };
                evidence["runs"]!.AsArray().Add(row); await campaign.SaveAsync(Collection, label, evidence);
                measured.Fault = sample.Fault; var before = measured.Calls; var beforeUsage = measured.Receipts.Count;
                var telemetry = new MappingTelemetry(); var timer = Stopwatch.StartNew();
                var engine = new WorkflowEngine { McpClientFactory = transport, LLMClient = measured, HumanInputProvider = human,
                    RunStore = EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory: root), Telemetry = telemetry,
                    Limits = new() { TenantId = "mapping-" + label, RunId = label + "-" + sample.Id, MaxMappingInputTokens = 96000 },
                    LlmDefaults = new() { Model = provider.Model, Provider = provider.Provider } };
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(approved.Yaml!)).Workflows["main"];
                var count = variant == "each" ? JsonNode.Parse(sample.Source)!.AsArray().Count : 0;
                var inputs = variant == "each" ? new JsonObject { ["indices"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) } : new();
                var result = await engine.ExecuteAsync(workflow, inputs, timeout.Token);
                row["execution_ms"] = timer.ElapsedMilliseconds; row["model_calls"] = measured.Calls - before;
                row["success"] = result.Success; row["error"] = result.Error?.Code; row["outputs"] = result.Outputs?.DeepClone();
                row["mapping_telemetry"] = telemetry.Mappings;
                row["usage_receipts"] = new JsonArray(measured.Receipts.Skip(beforeUsage).Select(r => (JsonNode)r.DeepClone()).ToArray());
                row["injected_attempts"] = sample.Fault ? 1 : 0;
                row["oracle"] = sample.Expected is null ? !result.Success && result.Error?.Code == "CONTRACT_UNSATISFIED"
                    : result.Success && JsonNode.DeepEquals(result.Outputs?["result"], JsonNode.Parse(sample.Expected));
                if (sample.Id is "json-warm" or "json-values" or "each-warm") row["oracle"] = row["oracle"]!.GetValue<bool>() && measured.Calls == before;
                row["execution_completed"] = DateTimeOffset.UtcNow.ToString("O");
                evidence["accounting"] = await campaign.InspectAsync();
                await campaign.SaveAsync(Collection, label, evidence); Console.WriteLine(row.ToJsonString());
                if (!row["oracle"]!.GetValue<bool>()) { Environment.ExitCode = 1; break; }
            }
            evidence["passed"] = evidence["runs"]!.AsArray().Count == Samples().Count && evidence["runs"]!.AsArray().All(r => r?["oracle"]?.GetValue<bool>() == true);
            await campaign.SaveAsync(Collection, label, evidence);
        }
        catch (Exception ex)
        {
            if (evidence is not null) { evidence["failures"] ??= new JsonArray(); evidence["failures"]!.AsArray().Add(new JsonObject { ["type"] = ex.GetType().Name, ["message"] = ex.Message }); await campaign.SaveAsync(Collection, label, evidence); }
            throw;
        }
        finally { Directory.Delete(work, true); }
    }

    internal static PlanningSession Prepare(PlanningCatalog catalog, string operation, string label, string variant)
    {
        TaskValue Output(string task, string? port = null) => new() { Kind = "output", Source = task, Port = port };
        var row = new TaskType { Kind = "object", Fields = [new() { Name = "name", Type = new() }] };
        if (variant == "extended") row.Fields.Add(new() { Name = "reference", Type = new() });
        var target = variant == "each" ? new TaskType { Kind = "object", Fields = [new() { Name = "rows", Type = new() { Kind = "array", Items = row } }] } : row;
        var observe = new PlanTask { Id = "observe", Objective = "Read the complete observed source", Operation = operation };
        var plan = new TaskPlan();
        if (variant == "each")
        {
            plan.Inputs.Add(new() { Name = "indices", Type = new() { Kind = "array", Items = new() { Kind = "integer" } } });
            observe.Inputs.Add(new("index", new() { Kind = "item" }));
            plan.Root.Tasks.Add(new() { Id = "collect", Kind = "foreach", Objective = "Read every source observation in order", MaxItems = 160,
                Items = new() { Kind = "input", Source = "indices" }, Body = new() { Tasks = [observe], Outputs = [new("observations", Output("observe"))] } });
        }
        else plan.Root.Tasks.Add(observe);
        plan.Root.Tasks.Add(new() { Id = "extract", Kind = "transform", Mode = "extract", Each = variant == "each" ? new("observation", "rows") : null,
            Objective = "Extract only the observed displayName as name" + (variant == "extended" ? " and observed reference as reference" : "") + ". Preserve all values and order; no invented values or defaults.",
            Inputs = [new("observation", variant == "each" ? Output("collect", "observations") : Output("observe"))], ResultType = target });
        plan.Root.Outputs.Add(new("result", Output("extract")));
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        if (compiled.Graph is null || compiled.Diagnostics.Count != 0) throw new InvalidOperationException(string.Join("; ", compiled.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        PlanningConfirmationGuards.Apply(compiled.Graph, catalog);
        var validation = PlanningExecutableValidation.Validate(compiled.Graph, catalog);
        if (validation.Count > 0) throw new InvalidOperationException(string.Join("; ", validation.Select(d => d.Message)));
        return new() { IntentVersion = 2, Revision = 1, Request = new() { SessionId = label, TenantId = "benchmark", Name = "Mapping probe", Prompt = Prompt },
            Status = PlanningStatus.FinalReview, Catalog = catalog, Plan = plan, Graph = compiled.Graph,
            Yaml = new PlanningGraphCompiler().Compile(compiled.Graph, catalog, "Mapping probe"),
            Requirements = new() { Summary = "Extract source-grounded fixture values", Inputs = plan.Inputs, Outputs = [new() { Name = "result", Type = target }],
                Outcomes = [new("extraction", plan.Root.Tasks[^1].Objective), new("completeness", variant == "each" ? "Read and extract every source item exactly once, preserving order and duplicates." : "Preserve the complete source and reject absent required values.")] } };
    }

    internal static List<(string Id, string Variant, string Source, string? Expected, bool Fault)> Samples()
    {
        var result = new List<(string, string, string, string?, bool)>
        {
            ("json-cold", "scalar", "{\"displayName\":\"Observed alpha\"}", "{\"name\":\"Observed alpha\"}", false),
            ("json-warm", "scalar", "{\"displayName\":\"Observed alpha\"}", "{\"name\":\"Observed alpha\"}", false),
            ("json-values", "scalar", "{\"displayName\":\"Observed beta\"}", "{\"name\":\"Observed beta\"}", false),
            ("json-shape", "scalar", "{\"displayName\":\"Observed gamma\",\"extra\":true}", "{\"name\":\"Observed gamma\"}", false),
            ("text-cold", "scalar", "displayName: Observed text", "{\"name\":\"Observed text\"}", false),
            ("html-cold", "scalar", "<div><span data-field=\"displayName\">Observed &amp; HTML</span></div>", "{\"name\":\"Observed & HTML\"}", false),
            ("missing-required", "scalar", "{\"unrelated\":\"plausible but unsupported\"}", null, false),
            ("injected-repair", "scalar", "displayName: Observed repair", "{\"name\":\"Observed repair\"}", true),
            ("target-change", "extended", "{\"displayName\":\"Observed delta\",\"reference\":\"REF-7\"}", "{\"name\":\"Observed delta\",\"reference\":\"REF-7\"}", false)
        };
        var noise = string.Join(' ', Enumerable.Range(0, 3000));
        var data = new JsonArray(Enumerable.Range(0, 80).Select(i => (JsonNode?)JsonValue.Create("<p data-field=\"displayName\">Observed " + i + "</p><pre>" + noise + "</pre>")).ToArray()).ToJsonString();
        var expected = new JsonObject { ["rows"] = new JsonArray(Enumerable.Range(0, 80).Select(i => (JsonNode)new JsonObject { ["name"] = "Observed " + i }).ToArray()) }.ToJsonString();
        result.Add(("each-cold", "each", data, expected, false)); result.Add(("each-warm", "each", data, expected, false));
        return result;
    }
    internal sealed class MappingTelemetry : IWorkflowTelemetry
    {
        internal JsonArray Mappings { get; } = [];
        private sealed class Span : IWorkflowSpan, IStepSpan
        {
            internal string? StepId { get; init; }
            internal JsonObject Values { get; } = new();
            public void SetAttribute(string key, object? value)
            { if (key.StartsWith("gnougo.mapping.", StringComparison.Ordinal)) Values[key] = JsonSerializer.SerializeToNode(value); }
            public void Dispose() { }
        }
        public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info) => new Span();
        public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result) { }
        public IStepSpan StepStart(ITelemetrySpan parent, StepTelemetryInfo info) => new Span { StepId = info.StepId };
        public void StepEnd(IStepSpan span, StepResultInfo result)
        {
            if (span is not Span { Values.Count: > 0 } current) return;
            var values = current.Values.DeepClone().AsObject();
            values["step"] = current.StepId; values["status"] = result.Status.ToString(); values["duration_ms"] = result.Duration.TotalMilliseconds;
            lock (Mappings) Mappings.Add(values);
        }
    }
    internal sealed class Measured(ILLMClient provider, string label) : ILLMClient, ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? p, string model, int output, CancellationToken ct)
            => provider is ILLMCapabilityResolver resolver ? resolver.InputTokenAllowanceAsync(p, model, output, ct) : Task.FromResult<int?>(null);
        public Task<bool?> SupportsStructuredOutputAsync(string? p, string model, CancellationToken ct)
            => provider is ILLMCapabilityResolver resolver ? resolver.SupportsStructuredOutputAsync(p, model, ct) : Task.FromResult<bool?>(null);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? p, string model, CancellationToken ct)
            => provider is ILLMCapabilityResolver resolver ? resolver.SupportedReasoningLevelsAsync(p, model, ct) : Task.FromResult<IReadOnlyList<string>?>(null);
        internal int Calls; internal bool Fault; internal List<JsonObject> Receipts = [];
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            if (Fault) { Fault = false; return new LLMResponse { Json = new JsonObject { ["script"] = "({name:'injected unsupported value'})" } }; }
            request = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
            request = LiveWorkflowEvaluation.ExecutionRequest(request, label, Calls);
            request.Reasoning = "medium";
            var response = await (provider is KeyVaultBenchmarkModel live ? live.CallExecutionAsync(request, ct) : provider.CallAsync(request, ct));
            Receipts.Add(new() { ["request_id"] = request.ClientRequestId, ["usage"] = response.Usage?.DeepClone() });
            return response;
        }
    }
}
