using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Git.Mcp;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class CopilotListPlanningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void RetainedStringContractAcceptsTheObjectThatFailedBeforeCopilotStarted()
    {
        var recording = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CopilotLists", "retained-failure.json")))!;
        var encoded = recording["request"]!["permissionAllowlistJson"]!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(encoded, recording["oldAllowlistSchema"]!));
        var error = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<List<string>>(encoded.ToString()));
        Assert.Equal(recording["error"]!.ToString(), error.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TypedListsCompileAndExecuteWithOriginalOrRenamedContracts(bool interactive, bool renamed)
    {
        var method = interactive ? "copilot_interactive_one_shot" : "copilot_one_shot";
        var factory = new RealProductContracts.Factory(CopilotAttachmentPlanningTests.Capture(renamed, method));
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var request = new PlanningRequest { TenantId = "fixture", Name = "list-contract", Prompt = "Inspect declared files and return observed results" };
        var catalog = await runtime.DiscoverAsync(request, Ct);
        foreach (var source in await runtime.Capabilities.ListSourcesAsync(Ct))
        foreach (var summary in (await runtime.Capabilities.ListAsync(source.Id, null, Ct)).Capabilities)
            catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(summary, Ct));
        var clone = catalog.Capabilities.Single(c => c.Method == (renamed ? "prepare" : "git_clone"));
        var send = catalog.Capabilities.Single(c => c.Method == (renamed ? "summarize" : method));
        var plan = Plan(clone.Id, send.Id, interactive);
        var target = plan.Root.Tasks[2];
        foreach (var invalid in new TaskValue[]
        {
            new() { Kind = "object", Members = [new("commands", new() { Kind = "array", Items = [Text("npm")] }), new("scope", Text("projectRoot only"))] },
            Text("[\"npm\"]"), new() { Kind = "array", Items = [Text(" ")] }, new() { Kind = "array", Items = [new() { Kind = "null" }] }
        })
        {
            target.Inputs.Add(new("permissionAllowlist", invalid));
            var bad = new TaskPlanCompiler().Compile(plan, catalog);
            Assert.Null(bad.Graph); Assert.Contains(bad.Diagnostics, d => d.Location.Contains("permissionAllowlist", StringComparison.Ordinal));
            target.Inputs.RemoveAt(target.Inputs.Count - 1);
        }
        target.Inputs.Add(new("permissionAllowlistJson", Text("[]")));
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Location.EndsWith("/permissionAllowlistJson", StringComparison.Ordinal));
        target.Inputs.RemoveAt(target.Inputs.Count - 1);

        var calls = 0;
        factory.Handler = (_, called, args, _) =>
        {
            calls++;
            if (called == clone.Method) return Task.FromResult(new McpCallResult
            { Content = JsonSerializer.SerializeToNode(new GitCloneResult("/mock/project", "https://example.test/repo.git", null, "workflows/list-fixture"), RealProductContracts.Json) });
            Assert.Equal(send.Method, called); Assert.Equal("workflows/list-fixture", args!["projectRoot"]!.ToString());
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[\"readme.md\",\"src/main.cs\",\"readme.md\"]"), args["permissionAllowlist"]));
            Assert.Null(args["permissionAllowlistJson"]);
            if (interactive) Assert.False(args.AsObject().ContainsKey("permissionMode"));
            else Assert.Equal("auto_approve_allowlist", args["permissionMode"]!.ToString());
            return Task.FromResult(new McpCallResult { Content = JsonSerializer.SerializeToNode(new CopilotSendResult("fixture", "mock-session", "observed fixture result", "mock", []), RealProductContracts.Json) });
        };
        target.Inputs.Add(new("permissionAllowlist", Output("selection", "entries")));
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics); Assert.NotNull(compilation.Graph);
        typeof(HybridWorkflowPlanner).Assembly.GetType("GnOuGo.Flow.Planning.PlanningConfirmationGuards")!
            .GetMethod("Apply", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [compilation.Graph, catalog]);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph, catalog, request.Name);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, request, catalog, PlanningGraphCompiler.CapabilityBindings(compilation.Graph)), Ct));
        var state = new PlanningSession { Request = request, Requirements = new() { Summary = request.Prompt, Outcomes = [new("result", "Return observed results")] },
            Catalog = catalog, Plan = plan, Graph = compilation.Graph, Yaml = yaml, Status = PlanningStatus.FinalReview };
        PlanningArtifactApproval.Verify(state);
        var hash = state.ComputeArtifactHash();
        var approved = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = hash }, runtime, Ct);
        Assert.Equal(PlanningStatus.Approved, approved.Status); Assert.Equal(0, approved.ModelCalls); Assert.Equal(0, approved.ReplanAttempts);
        var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human(true) };
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var run = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), Ct);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal("observed fixture result", run.Outputs!["result"]!.ToString()); Assert.Equal(2, calls);
        approved.Plan!.Root.Tasks[2].Inputs.RemoveAll(i => i.Name == "permissionAllowlist");
        Assert.NotEqual(hash, approved.ComputeArtifactHash()); Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(approved));
    }

    private static TaskPlan Plan(string clone, string send, bool interactive)
    {
        var plan = new TaskPlan
        {
            Root = new() { Tasks = [
                new() { Id = "prepare", Objective = "Prepare the declared workspace", Operation = clone,
                    Inputs = [new("remoteUrl", Text("https://example.test/repo.git")), new("targetDirectory", Text("workflows/list-fixture"))] },
                new() { Id = "selection", Kind = "value", Objective = "Retain the requested read-only file list",
                    Outputs = [new("entries", new() { Kind = "array", Items = [Text("readme.md"), Text("src/main.cs"), Text("readme.md")] })] },
                new() { Id = "inspect", Objective = "Inspect requested files and report observed results", Operation = send,
                    Inputs = [new("projectRoot", Output("prepare", "projectRootRelative")), new("prompt", Text("Inspect declared files and report the observed results"))] }
            ], Outputs = [new("result", Output("inspect", "content"))] }
        };
        if (!interactive) plan.Root.Tasks[2].Inputs.Add(new("permissionMode", Text("auto_approve_allowlist")));
        return plan;
    }
    private static TaskValue Text(string text) => new() { Kind = "string", Text = text };
    private static TaskValue Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };
}
