using System.Reflection;
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
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace GnOuGo.Agent.Server.Tests;

public sealed class CopilotAttachmentPlanningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonNode Recording(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CopilotAttachments", name + ".json")))!;

    [Fact]
    public void RetainedStringContractAcceptsTheInvocationThatTheOldParserRejects()
    {
        var request = Recording("retained-invocation")["resolvedInput"]!["request"]!;
        var old = Recording("retained-contracts")["copilot_one_shot"]!["InputSchema"]!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(request, old));
        var error = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IReadOnlyList<CopilotAttachment>>(request["attachmentsJson"]!.ToString(), RealProductContracts.Json));
        Assert.Contains("IReadOnlyList", error.Message); Assert.Equal("$", error.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealContractsRejectContextObjectsAndAcceptExplicitPromptBindings(bool renamed)
    {
        var snapshot = Capture(renamed);
        var factory = new RealProductContracts.Factory(snapshot);
        var model = new Proposal();
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory, LLMClient = model }, (_, _) => Task.CompletedTask);
        var state = new PlanningSession
        {
            Request = new() { TenantId = "fixture", Mode = PlanningMode.Auto, Prompt = "Summarize the supplied review context without executing commands.", Generation = new() { MaxInputTokensPerRequest = 24000, MaxOutputTokens = 32768 } },
            Requirements = new() { Summary = "Summarize supplied review context", Outcomes = [new("report", "Return the summary")] },
            Phase = PlanningPhase.Tasks,
            Catalog = await runtime.DiscoverAsync(new(), Ct)
        };
        foreach (var source in await runtime.Capabilities.ListSourcesAsync(Ct))
        {
            state.Discovery.Sources.Add(source);
            var page = await runtime.Capabilities.ListAsync(source.Id, null, Ct); state.Discovery.Pages.Add(page);
            foreach (var summary in page.Capabilities)
            {
                var contract = await runtime.Capabilities.ResolveAsync(summary, Ct);
                state.Discovery.Resolved.Add(contract); state.Catalog.Capabilities.Add(contract);
            }
        }
        var clone = state.Catalog.Capabilities.Single(c => c.Method == (renamed ? "prepare" : "git_clone"));
        var send = state.Catalog.Capabilities.Single(c => c.Method == (renamed ? "summarize" : "copilot_one_shot"));
        var plan = SyntheticCorrectedProposal(clone.Id, send.Id);
        var context = new TaskValue { Kind = "object", Members = [new("pullRequestUrl", Input("pullRequestUrl")), new("reviewInstructions", Input("reviewInstructions"))] };
        plan.Root.Tasks[1].Inputs.Add(new("attachments", context));
        var bad = new TaskPlanCompiler().Compile(plan, state.Catalog);
        Assert.Null(bad.Graph);
        Assert.Contains(bad.Diagnostics, d => d.Code == "TASK_INPUT_TYPE" && d.Location == "/tasks/inspect/inputs/attachments");
        var before = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var scope = Scope(plan, bad.Diagnostics);
        var corrected = SyntheticCorrectedProposal(clone.Id, send.Id);
        Assert.Empty(ValidateRevision(plan, corrected, scope, state.Catalog));
        corrected.Root.Tasks[0].Objective = "Unrelated rewrite";
        Assert.NotEmpty(ValidateRevision(plan, corrected, scope, state.Catalog));
        Assert.Equal(before, JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
        plan.Root.Tasks[1].Inputs[^1] = new("attachmentsJson", new() { Kind = "json", Items = [context] });
        var legacy = new TaskPlanCompiler().Compile(plan, state.Catalog);
        Assert.Null(legacy.Graph); Assert.Contains(legacy.Diagnostics, d => d.Location == "/tasks/inspect/inputs/attachmentsJson");

        // Explicitly synthetic corrected intent, not an edited historical recording.
        model.Plan = SyntheticCorrectedProposal(clone.Id, send.Id);
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + " " + d.Location + " " + d.Message)));
        Assert.Equal(1, model.Calls); Assert.Equal(0, state.ReplanAttempts); Assert.Equal(0, factory.InvocationAttempts);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        PlanningArtifactApproval.Verify(state);
        var originalHash = state.ComputeArtifactHash();
        var reviewed = state.Catalog!.Capabilities.Single(c => c.Id == send.Id);
        reviewed.InputSchema["properties"]!["attachments"]!["description"] = "Changed producer contract";
        Assert.NotEqual(originalHash, state.ComputeArtifactHash());
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = originalHash }, runtime, Ct));
        reviewed.InputSchema = send.InputSchema.DeepClone().AsObject(); PlanningArtifactApproval.Verify(state);

        var calls = new List<string>();
        factory.Handler = (_, method, args, _) =>
        {
            calls.Add(method);
            if (method == clone.Method)
                return Task.FromResult(new McpCallResult { Content = JsonSerializer.SerializeToNode(new GitCloneResult("/mock/project", "https://example.test/repo.git", null, "workflows/attachment-fixture"), RealProductContracts.Json) });
            Assert.Equal(send.Method, method); Assert.Equal("workflows/attachment-fixture", args!["projectRoot"]!.ToString());
            Assert.Null(args["attachments"]); Assert.Null(args["attachmentsJson"]); Assert.Equal("deny", args["permissionMode"]!.ToString());
            var prompt = JsonNode.Parse(args["prompt"]!.ToString())!;
            Assert.Equal("https://example.test/repo/pull/23", prompt["pullRequestUrl"]!.ToString());
            Assert.Equal("Explain the supplied context", prompt["reviewInstructions"]!.ToString());
            return Task.FromResult(new McpCallResult { Content = JsonSerializer.SerializeToNode(new CopilotSendResult("fixture", "mock-session", "fixture summary", "mock", []), RealProductContracts.Json) });
        };
        var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human(true) };
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var run = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["pullRequestUrl"] = "https://example.test/repo/pull/23", ["reviewInstructions"] = "Explain the supplied context" }, Ct);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal("fixture summary", run.Outputs!["summary"]!.ToString());
        Assert.Equal(new[] { clone.Method, send.Method }, calls); Assert.Equal(1, model.Calls);
    }

    private static Type Revisions => typeof(HybridWorkflowPlanner).Assembly.GetType("GnOuGo.Flow.Planning.TaskPlanRevisions")!;
    private static IReadOnlyList<string> Scope(TaskPlan plan, IReadOnlyList<PlanningDiagnostic> diagnostics)
        => (IReadOnlyList<string>)Revisions.GetMethod("Scope", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [plan, diagnostics, false])!;
    private static IEnumerable<PlanningDiagnostic> ValidateRevision(TaskPlan plan, TaskPlan changed, IReadOnlyList<string> scope, PlanningCatalog catalog)
        => (IEnumerable<PlanningDiagnostic>)Revisions.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [plan, changed, scope, catalog])!;

    private static TaskPlan SyntheticCorrectedProposal(string clone, string send) => new()
    {
        Inputs = [new() { Name = "pullRequestUrl" }, new() { Name = "reviewInstructions" }],
        Root = new()
        {
            Tasks = [
                new() { Id = "prepare", Objective = "Prepare the declared repository workspace", Operation = clone,
                    Inputs = [new("remoteUrl", Text("https://example.test/repo.git")), new("targetDirectory", Text("workflows/attachment-fixture"))] },
                new() { Id = "inspect", Objective = "Summarize supplied review context without executing commands", Operation = send, DependsOn = ["prepare"],
                    Inputs = [new("projectRoot", Output("prepare", "projectRootRelative")), new("permissionMode", Text("deny")),
                        new("prompt", new() { Kind = "json", Items = [new() { Kind = "object", Members = [new("pullRequestUrl", Input("pullRequestUrl")), new("reviewInstructions", Input("reviewInstructions"))] }] })] }
            ],
            Outputs = [new("summary", Output("inspect", "content"))]
        }
    };
    private static TaskValue Text(string text) => new() { Kind = "string", Text = text };
    private static TaskValue Input(string name) => new() { Kind = "input", Source = name };
    private static TaskValue Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };

    internal static JsonObject Capture(bool renamed, string copilotMethod = "copilot_one_shot")
    {
        var result = new JsonObject();
        var copilot = typeof(GnOuGo.GithubCopilot.Mcp.CodePolicy).Assembly;
        CaptureTools(typeof(GitTools), "GnOuGo.Git.Mcp.GitMcpJson", "git_clone", "prepare", "producer");
        CaptureTools(copilot.GetType("GnOuGo.GithubCopilot.Mcp.CopilotTools")!, "GnOuGo.GithubCopilot.Mcp.CodeMcpJson", copilotMethod, "summarize", "consumer");
        return result;
        void CaptureTools(Type type, string serializer, string method, string alias, string source)
        {
            var options = (JsonSerializerOptions)type.Assembly.GetType(serializer)!.GetProperty("SerializerOptions", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            var services = new ServiceCollection(); services.AddLogging(); services.AddMcpServer().WithTools([type], options);
            using var provider = services.BuildServiceProvider();
            var tool = provider.GetServices<McpServerTool>().Single(t => t.ProtocolTool.Name == method).ProtocolTool;
            if (type == typeof(GitTools))
                type.Assembly.GetType("GnOuGo.Git.Mcp.GitCloneTargetContract")!.GetMethod("Publish", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [tool]);
            else
            {
                tool.InputSchema = (JsonElement)copilot.GetType("GnOuGo.GithubCopilot.Mcp.CopilotAttachmentContract")!.GetMethod("InputSchema", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [tool.InputSchema])!;
                copilot.GetType("GnOuGo.GithubCopilot.Mcp.CopilotListContract")!.GetMethod("Publish", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [tool]);
            }
            result[source] = JsonSerializer.SerializeToNode(new[] { new McpToolInfo { Name = renamed ? alias : method, Description = tool.Description, InputSchema = JsonNode.Parse(tool.InputSchema.GetRawText()), OutputSchema = JsonNode.Parse(tool.OutputSchema!.Value.GetRawText()), Meta = tool.Meta?.DeepClone() } }, RealProductContracts.Json);
        }
    }

    private sealed class Proposal : ILLMClient
    {
        internal TaskPlan Plan = null!; internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            var proposal = JsonSerializer.SerializeToNode(new PlanningProposal { Plan = Plan }, PlanningJsonContext.Default.PlanningProposal);
            return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(proposal, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) });
        }
    }
}
