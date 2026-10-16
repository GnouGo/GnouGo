using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Git.Mcp;
using GnOuGo.Planning.Examples;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

// Disposable composition, not a modification of a saved agent. Tool contracts are
// deterministic fixture contracts, not evidence that the operator enabled remote
// review, command execution or GitHub permissions in the retained host.
public sealed class CodeReviewCompositionTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("approve")]
    [InlineData("findings")]
    [InlineData("failed_checks")]
    [InlineData("incomplete")]
    [InlineData("denied")]
    [InlineData("post_failure")]
    [InlineData("unknown")]
    public async Task ExplicitBusinessRevisionExecutesLocalReviewAndNeverRegeneratesAWorkflow(string variant)
    {
        using var fixture = new Execution(variant);
        var store = new InMemoryWorkflowRunStore();
        var engine = new WorkflowEngine { McpClientFactory = fixture.Factory, RunStore = store,
            Limits = new() { TenantId = "fixture", RunId = Guid.NewGuid().ToString("N") } };
        var actual = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await actual.DiscoverAsync(new(), Ct);
        var discovery = new CapabilityDiscoveryState();
        foreach (var source in await actual.Capabilities.ListSourcesAsync(Ct))
        {
            discovery.Sources.Add(source);
            var page = await actual.Capabilities.ListAsync(source.Id, null, Ct); discovery.Pages.Add(page);
            foreach (var entry in page.Capabilities)
            {
                var capability = await actual.Capabilities.ResolveAsync(entry, Ct);
                catalog.Capabilities.Add(capability); discovery.Resolved.Add(capability);
            }
        }
        catalog.Policy.RequireExternalConfirmation = false; // Test adapters only; no external service exists.
        var corrected = Proposal(catalog);
        var blueprint = Blueprint(catalog, corrected.Plan!.Inputs);
        var runtime = new LocalProductOutcomeExecutionTests.ProposalRuntime(actual, blueprint);
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(new() { Catalog = catalog, Discovery = discovery, Request = new()
        {
            TenantId = "fixture", Prompt = "Review the supplied change in one temporary clone, run available checks, publish exact findings and a review decision, then clean up.",
            Options = new() { ["compilation_profile"] = "compact-bindings-v15" }
        } }, new(), runtime, Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Empty(fixture.Calls); // Compilation does not establish business completeness.
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var oldHash = state.ComputeArtifactHash();
        RecordedPlanCompilation.InspectSelected(state, corrected.Plan);
        runtime.Patch = _ => JsonSerializer.SerializeToNode(corrected, PlanningJsonContext.Default.PlanningProposal)!.AsObject();
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", PreserveRequirements = false,
            ExpectedRevision = state.Revision, ArtifactHash = oldHash,
            Text = "Explicit disposable requirement revision: replace workflow-file generation with execution of the requested review. Remove workflow_file and its artifact outcome; return the submitted decision and summary. Inspect, clone once, run checks, review, post findings, submit, then release the clone. Preserve caller inputs." }, runtime, Ct);
        if (state.Status != PlanningStatus.FinalReview)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, runtime.Requests.Count); Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.ApprovedHash); Assert.NotEqual(oldHash, state.ComputeArtifactHash());
        Assert.Equal("compact-bindings-v15", state.Request.Options["compilation_profile"]!.ToString());
        Assert.Empty(fixture.Calls); Assert.DoesNotContain(state.Plan!.Root.Tasks, t => t.Kind == "transform");
        Assert.Contains("Execute the requested work", runtime.Requests[1].Prompt);
        Assert.InRange(runtime.InputEstimate, 1, state.Request.Generation.MaxInputTokensPerRequest);
        PlanningArtifactApproval.Verify(state);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject
        { ["changeUrl"] = Execution.ChangeUrl, ["instructions"] = Execution.Instructions }, Ct);
        var success = variant is "approve" or "findings" or "failed_checks";
        if (!result.Success) output.WriteLine(JsonSerializer.Serialize(result, RealProductContracts.Json));
        output.WriteLine("Operations: " + string.Join(",", fixture.Calls));
        Assert.True(result.Success == success, result.Error?.Message);
        Assert.DoesNotContain("write_blueprint", fixture.Calls);
        Assert.Equal(variant == "denied" ? 0 : 1, fixture.CloneCount);
        Assert.Equal(variant is "denied" or "unknown" ? 0 : 1, fixture.CleanupCount);
        Assert.Equal(variant == "unknown", Directory.Exists(fixture.Target));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(fixture.Root, "keep.txt")));
        Assert.Equal("original source\n", File.ReadAllText(Path.Combine(fixture.Source, "readme.txt")));
        if (variant != "denied") Assert.Equal(new[] { "install", "lint", "unit", "integration" }, fixture.Checks);
        if (success)
        {
            Assert.Equal(variant == "approve" ? "APPROVE" : "REQUEST_CHANGES", result.Outputs!["decision"]!.ToString());
            Assert.Equal(Execution.Summary, result.Outputs["summary"]!.ToString());
            Assert.Equal(new[] { "inspect", "clone", "checks", "review", "begin" }, fixture.Calls.Take(5));
            Assert.Equal(new[] { "submit", "cleanup" }, fixture.Calls.TakeLast(2));
            Assert.Equal(variant == "findings" ? 3 : 0, fixture.Posted.Count);
            if (variant == "findings")
            {
                Assert.Equal(new[] { "inline", "global", "inline" }, fixture.Posted.Select(p => p["method"]!.ToString()));
                Assert.Equal(new[] { "line:α", "global:β", "line:α" }, fixture.Posted.Select(p => p["key"]!.ToString()));
                Assert.Equal(new[] { Execution.InlineText, Execution.GlobalText, Execution.InlineText }, fixture.Posted.Select(p => p["text"]!.ToString()));
            }
        }
        else Assert.DoesNotContain("submit", fixture.Calls);
        if (variant == "incomplete") Assert.DoesNotContain("begin", fixture.Calls);
        var before = fixture.Calls.ToArray();
        var journal = (await store.ReadAsync("fixture", engine.Limits.RunId, Ct))!;
        var recovered = await new WorkflowEngine { RunStore = store }.ResumeAsync("fixture", engine.Limits.RunId,
            journal.Revision, document.Workflows[document.Entrypoint!], Ct);
        Assert.Equal(result.Success, recovered.Success); Assert.True(JsonNode.DeepEquals(result.Outputs, recovered.Outputs));
        Assert.Equal(before, fixture.Calls);
        if (variant == "unknown") Assert.Equal("RUN_NEEDS_RECONCILIATION", recovered.Error?.Code);
        if (variant == "approve" && Environment.GetEnvironmentVariable("GNOU_GO_CODE_REVIEW_EVIDENCE") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "artifact-only-plan.json"), JsonSerializer.Serialize(blueprint, PlanningJsonContext.Default.PlanningProposal), Ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "corrected-proposal.json"), JsonSerializer.Serialize(corrected, PlanningJsonContext.Default.PlanningProposal), Ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "corrected-v15.yaml"), state.Yaml, Ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "composition-metrics.json"), new JsonObject
            { ["planningCalls"] = 2, ["repairs"] = 0, ["providerCalls"] = 0, ["runtimeLlmCalls"] = 0, ["reviewAdapterCalls"] = 1,
                ["requestEstimates"] = new JsonArray(runtime.Requests.Select(r => (JsonNode?)JsonValue.Create((System.Text.Encoding.UTF8.GetByteCount(r.Prompt) + System.Text.Encoding.UTF8.GetByteCount(r.StructuredOutputSchema!.ToJsonString()) + 2) / 3 + 256)).ToArray()),
                ["yamlBytes"] = System.Text.Encoding.UTF8.GetByteCount(state.Yaml!), ["effects"] = JsonSerializer.SerializeToNode(before),
                ["originalRequirements"] = JsonNode.Parse(retained)!["requirements"]!.DeepClone(),
                ["correctedRequirements"] = JsonSerializer.SerializeToNode(state.Requirements, PlanningJsonContext.Default.PlanningRequirements) }.ToJsonString(new() { WriteIndented = true }), Ct);
        }
        output.WriteLine($"{variant}: planning=2, corrections=0, runtimeLLM=0, cloned={fixture.CloneCount}, checks={fixture.Checks.Count}, posted={fixture.Posted.Count}, cleanup={fixture.CleanupCount}; committed recovery added zero effects.");
    }

    private static TaskValue Ref(string id, string port) => new() { Kind = "output", Source = id, Port = port };
    private static TaskValue Input(string name) => new() { Kind = "input", Source = name };
    private static TaskValue Text(string text) => new() { Kind = "string", Text = text };
    private static TaskValue Field(string name) => new() { Kind = "field", Port = name, Items = [new() { Kind = "item" }] };
    private static TaskType Obj(params (string Name, TaskType Type)[] fields) => new() { Kind = "object", Fields = fields.Select(f => new TaskInput { Name = f.Name, Type = f.Type }).ToList() };
    private static TaskType Bool() => new() { Kind = "boolean" };
    private static PlanTask Op(PlanningCatalog catalog, string id, string method, params TaskOutput[] inputs) => new()
    { Id = id, Objective = id + " the requested change", Operation = catalog.Capabilities.Single(c => c.Method == method).Id, Inputs = inputs.ToList() };

    private static PlanningProposal Blueprint(PlanningCatalog catalog, List<TaskInput> inputs) => new()
    {
        Plan = new() { Inputs = inputs, Root = new() { Tasks = [Op(catalog, "generate_workflow", "write_blueprint", new TaskOutput("instructions", Input("instructions")))], Outputs = [new("workflow_file", Ref("generate_workflow", "path"))] } },
        Requirements = new() { Summary = "Create a workflow artifact", Inputs = inputs, Outputs = [new() { Name = "workflow_file", Type = new() }], Outcomes = [new("artifact", "Write another workflow file")] }
    };

    private static PlanningProposal Proposal(PlanningCatalog catalog)
    {
        var inputs = new List<TaskInput> { new() { Name = "changeUrl" }, new() { Name = "instructions" } };
        var inspect = Op(catalog, "inspect", "inspect", new TaskOutput("url", Input("changeUrl")));
        var clone = Op(catalog, "clone", "clone", new TaskOutput("source", Ref("inspect", "source"))); clone.Requires = Ref("inspect", "open");
        var checks = Op(catalog, "checks", "checks", new TaskOutput("path", Ref("clone", "path")));
        var review = Op(catalog, "review", "review", new("path", Ref("clone", "path")), new("instructions", Input("instructions")), new("checks", Ref("checks", "passed")));
        var begin = Op(catalog, "begin", "begin", new TaskOutput("url", Input("changeUrl"))); begin.Requires = Ref("review", "complete"); begin.DependsOn = ["review"];
        var comments = new PlanTask { Id = "comments", Kind = "foreach", Objective = "Post each exact finding once per occurrence", Items = Ref("review", "findings"), MaxItems = 10,
            DependsOn = ["begin"], Body = new() { Tasks = [new() { Id = "route", Kind = "conditional", Objective = "Post inline when a location is available, otherwise globally",
                Condition = new() { Kind = "predicate", Predicate = "not_equal", Items = [Field("line"), new()] },
                Body = new() { Tasks = [Op(catalog, "inline", "inline", new("review", Ref("begin", "id")), new("key", Field("key")), new("text", Field("text")), new("line", Field("line")))] },
                Otherwise = new() { Tasks = [Op(catalog, "global", "global", new("review", Ref("begin", "id")), new("key", Field("key")), new("text", Field("text")))] } }] } };
        var decision = new PlanTask { Id = "decision", Kind = "conditional", Objective = "Approve only complete passing checks and an unblocked review",
            Condition = new() { Kind = "predicate", Predicate = "and", Items = [Ref("checks", "passed"), Ref("review", "approvable")] },
            Body = new() { Outputs = [new("event", Text("APPROVE"))] }, Otherwise = new() { Outputs = [new("event", Text("REQUEST_CHANGES"))] } };
        var submit = Op(catalog, "submit", "submit", new("review", Ref("begin", "id")), new("event", Ref("decision", "event")), new("summary", Ref("review", "summary")));
        submit.DependsOn = ["comments"];
        var cleanup = new PlanTask { Id = "release", Kind = "conditional", Objective = "Release the successfully acquired clone after known completion",
            Condition = new() { Kind = "present", Source = "clone" }, Body = new() { Tasks = [Op(catalog, "cleanup", "cleanup", new TaskOutput("path", Ref("clone", "path")))] }, Otherwise = new() };
        var plan = new TaskPlan { Inputs = inputs, Root = new() { Tasks = [inspect, clone, checks, review, begin, comments, decision, submit],
            Always = [cleanup], Outputs = [new("decision", Ref("submit", "event")), new("summary", Ref("submit", "summary"))] } };
        return new() { Plan = plan, Requirements = new() { Summary = "Execute the requested review, publish original findings and the observed decision, then release the clone",
            Inputs = inputs, Outputs = [new() { Name = "decision", Type = new() }, new() { Name = "summary", Type = new() }],
            Outcomes = [new("review", "Inspect the supplied change and run available checks in one clone"), new("feedback", "Publish exact findings inline where possible, otherwise globally, and submit a truthful decision"), new("cleanup", "Release the clone after known completion")] } };
    }

    private sealed class Execution : IDisposable
    {
        internal const string ChangeUrl = "https://fixture.invalid/change?id=17", Instructions = "Review correctness; preserve exact findings.",
            InlineText = "Exact inline finding\t— café\nsecond line", GlobalText = "Global finding: no supported inline location", Summary = "Deterministic reviewed summary; not a real provider assessment.";
        internal readonly string Root = Directory.CreateTempSubdirectory("code-review-composition-").FullName;
        internal string Source => Path.Combine(Root, "source");
        internal const string Location = "workflows/code-review";
        internal string Target => Path.Combine(Root, Location);
        internal readonly InMemoryMcpClientFactory Factory = new();
        internal readonly List<string> Calls = [], Checks = [];
        internal readonly List<JsonObject> Posted = [];
        internal int CloneCount, CleanupCount;
        internal Execution(string variant)
        {
            Directory.CreateDirectory(Source); Repository.Init(Source);
            File.WriteAllText(Path.Combine(Source, "readme.txt"), "original source\n");
            foreach (var name in new[] { "install", "lint", "unit", "integration" })
            {
                var exit = variant == "failed_checks" && name == "unit" ? "1" : "0";
                File.WriteAllText(Path.Combine(Source, name + ".sh"), "test -f readme.txt || exit 9\nprintf '" + name + "\\n'\nexit " + exit + "\n");
                File.WriteAllText(Path.Combine(Source, name + ".cmd"), "@echo off\r\nif not exist readme.txt exit /b 9\r\necho " + name + "\r\nexit /b " + exit + "\r\n");
            }
            using (var repository = new Repository(Source))
            { Commands.Stage(repository, "*"); var who = new Signature("Fixture", "fixture@example.invalid", DateTimeOffset.UnixEpoch.AddDays(1)); repository.Commit("Local checks", who, who); }
            File.WriteAllText(Path.Combine(Root, "keep.txt"), "untouched");
            var settings = new GitServerSettings { DefaultWorkingDirectory = Root, AllowMutations = true, AllowNetworkOperations = true, TokenEnvironmentVariables = [] };
            var policy = new GitPolicy(settings, Root); var git = new GitTools(policy, new(policy, Options.Create(settings)), NullLogger<GitTools>.Instance);
            var config = new MockMcpServerConfig();
            void Tool(string name, TaskType input, TaskType result, Func<JsonObject, JsonObject> respond)
            {
                var incoming = TaskPlanCompiler.TypeSchema(input); var outgoing = TaskPlanCompiler.TypeSchema(result);
                config.Tools.Add(new() { Name = name, EffectKind = "execute", InputSchema = incoming, OutputSchema = outgoing });
                config.ToolHandlers[name] = args =>
                {
                    Assert.Empty(PlanningContractValidation.ValidateInstance(args, incoming)); Calls.Add(name);
                    try
                    {
                        var response = respond(args!.AsObject()); Assert.Empty(PlanningContractValidation.ValidateInstance(response, outgoing));
                        return new() { Content = response };
                    }
                    catch (WorkflowRuntimeException error) when (error.Code is "PERMISSION_DENIED" or "COMMENT_REJECTED")
                    { return new() { IsError = true, Content = new JsonObject { ["error_code"] = error.Code, ["message"] = error.Message } }; }
                };
            }
            Tool("write_blueprint", Obj(("instructions", new())), Obj(("path", new())), _ => throw new InvalidOperationException("Artifact generation is not review execution."));
            Tool("inspect", Obj(("url", new())), Obj(("source", new()), ("open", Bool())), a => { Assert.Equal(ChangeUrl, a["url"]!.ToString()); return new() { ["source"] = Source, ["open"] = true }; });
            Tool("clone", Obj(("source", new())), Obj(("path", new())), a =>
            {
                Assert.Equal(Source, a["source"]!.ToString());
                if (variant == "denied") throw new WorkflowRuntimeException("PERMISSION_DENIED", "Disposable fixture denies cloning.");
                CloneCount++; var result = git.GitClone(Source, Location); Assert.True(result.Success, result.ErrorMessage);
                Assert.Equal(Location, result.ProjectRootRelative); return new() { ["path"] = result.ProjectRootRelative };
            });
            void AssertClone(JsonObject a) { Assert.Equal(Location, a["path"]!.ToString()); Assert.True(Repository.IsValid(Target)); }
            Tool("checks", Obj(("path", new())), Obj(("passed", Bool())), a =>
            {
                AssertClone(a); var passed = true;
                foreach (var name in new[] { "install", "lint", "unit", "integration" })
                {
                    using var process = new Process { StartInfo = new() { FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", WorkingDirectory = Target, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
                    if (OperatingSystem.IsWindows()) process.StartInfo.ArgumentList.Add("/c");
                    process.StartInfo.ArgumentList.Add(name + (OperatingSystem.IsWindows() ? ".cmd" : ".sh")); process.Start();
                    var text = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
                    Assert.Equal(name + Environment.NewLine, text); Assert.Equal("", error); Checks.Add(name); passed &= process.ExitCode == 0;
                }
                return new() { ["passed"] = passed };
            });
            var finding = Obj(("key", new()), ("text", new()), ("line", new() { Kind = "integer", Nullable = true }));
            Tool("review", Obj(("path", new()), ("instructions", new()), ("checks", Bool())), Obj(("complete", Bool()), ("approvable", Bool()), ("summary", new()), ("findings", new() { Kind = "array", Items = finding, MaxItems = 10 })), a =>
            {
                AssertClone(a); Assert.Equal(Instructions, a["instructions"]!.ToString()); Assert.Equal(variant != "failed_checks", a["checks"]!.GetValue<bool>());
                JsonObject Finding(string key, string text, int? line) => new() { ["key"] = key, ["text"] = text, ["line"] = line };
                var findings = variant is "findings" or "post_failure" or "unknown" ? new JsonArray(Finding("line:α", InlineText, 3), Finding("global:β", GlobalText, null), Finding("line:α", InlineText, 3)) : [];
                return new() { ["complete"] = variant != "incomplete", ["approvable"] = findings.Count == 0, ["summary"] = Summary, ["findings"] = findings };
            });
            Tool("begin", Obj(("url", new())), Obj(("id", new())), a => { Assert.Equal(ChangeUrl, a["url"]!.ToString()); return new() { ["id"] = "review:original" }; });
            foreach (var method in new[] { "inline", "global" })
            {
                var incoming = Obj(("review", new()), ("key", new()), ("text", new()));
                if (method == "inline") incoming.Fields.Add(new() { Name = "line", Type = new() { Kind = "integer" } });
                Tool(method, incoming, Obj(("id", new())), a =>
                {
                    Assert.Equal("review:original", a["review"]!.ToString()); var posted = a.DeepClone().AsObject(); posted["method"] = method; Posted.Add(posted);
                    if (variant == "unknown") throw new WorkflowRuntimeException("RUN_NEEDS_RECONCILIATION", "Comment completion is unknown.");
                    if (variant == "post_failure") throw new WorkflowRuntimeException("COMMENT_REJECTED", "Confirmed rejection.");
                    return new() { ["id"] = "posted:" + Posted.Count };
                });
            }
            Tool("submit", Obj(("review", new()), ("event", new()), ("summary", new())), Obj(("event", new()), ("summary", new())), a =>
            { Assert.Equal("review:original", a["review"]!.ToString()); return new() { ["event"] = a["event"]!.DeepClone(), ["summary"] = a["summary"]!.DeepClone() }; });
            Tool("cleanup", Obj(("path", new())), Obj(), a => { AssertClone(a); CleanupCount++; Delete(Target); return new(); });
            Factory.RegisterServer("local-review", config);
        }
        private static void Delete(string path)
        { foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(path, true); }
        public void Dispose() => Delete(Root);
    }
}
