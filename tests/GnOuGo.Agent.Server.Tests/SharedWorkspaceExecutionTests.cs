using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Cmd.Mcp;
using GnOuGo.Git.Mcp;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class SharedWorkspaceExecutionTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("partial")]
    [InlineData("refused")]
    [InlineData("cancelled")]
    [InlineData("missing")]
    public async Task LocalLifecycleSliceUsesExactApprovedPathAndRealCleanup(string mode)
    {
        using var current = new RecordedWorkspaceContractTests.CurrentContracts();
        var replay = new RecordedWorkspaceContractTests.Replay();
        var saved = replay.State(replay.Recording["finalSession"]!);
        var plan = RecordedWorkspaceContractTests.Corrected(saved.Plan!);
        // Explicit lifecycle slice of the corrected proposal. Review/inference effects are not executed.
        plan.Root.Tasks = plan.Root.Tasks.Where(t => t.Id is "paths" or "clone_once" or "validate_project").ToList();
        plan.Root.Outputs.Clear();
        var clone = plan.Root.Tasks.Single(t => t.Id == "clone_once");
        clone.DependsOn = ["paths"];
        var source = Path.Combine(current.Root, "source"); Repository.Init(source);
        using (var repo = new Repository(source))
        {
            File.WriteAllText(Path.Combine(source, "README.md"), "local source\n"); Commands.Stage(repo, "README.md");
            var signature = new Signature("fixture", "fixture@example.test", DateTimeOffset.UnixEpoch.AddDays(1));
            repo.Commit("fixture", signature, signature);
        }
        clone.Inputs = clone.Inputs.Where(i => i.Name is "targetDirectory" or "remoteUrl").ToList();
        clone.Inputs[clone.Inputs.FindIndex(i => i.Name == "remoteUrl")] = new("remoteUrl", new() { Kind = "string", Text = source });
        plan.Root.Tasks.Single(t => t.Id == "validate_project").DependsOn = ["clone_once"];
        var catalog = saved.Catalog!;
        catalog.Capabilities = catalog.Capabilities.Select(c => current.Replace(c) ?? c).ToList();
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics); Assert.Empty(PlanningGraphValidation.Validate(compilation.Graph!, catalog));
        var generation = replay.State(replay.Recording["responses"]![4]!["pendingSession"]!);
        generation.PendingCall = null; generation.ModelCalls--; generation.Plan = null; generation.Graph = null; generation.Yaml = null;
        generation.Catalog = catalog; replay.Proposal = plan;
        var reviewed = await new HybridWorkflowPlanner().AdvanceAsync(generation, new() { ExpectedRevision = generation.Revision }, replay, TestContext.Current.CancellationToken);
        Assert.True(reviewed.Status == PlanningStatus.FinalReview, string.Join("; ", reviewed.Diagnostics.Select(d => d.Code + " " + d.Message)));
        PlanningArtifactApproval.Verify(reviewed);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(reviewed.Yaml!));
        var target = Path.GetFullPath(Path.Combine(current.Root, RecordedWorkspaceContractTests.Location));
        var unrelated = Path.Combine(current.Root, "unrelated"); Directory.CreateDirectory(unrelated); File.WriteAllText(Path.Combine(unrelated, "keep"), "untouched");
        var settings = new GitServerSettings { DefaultWorkingDirectory = current.Root, AllowMutations = true, AllowNetworkOperations = true, TokenEnvironmentVariables = [] };
        var policy = new GitPolicy(settings, current.Root);
        var git = new GitTools(policy, new(policy, Options.Create(settings)), NullLogger<GitTools>.Instance);
        var cmd = new CmdTools(new(current.Policy, NullLogger<CommandExecutionHost>.Instance), NullLogger<CmdTools>.Instance);
        var snapshot = new JsonObject();
        foreach (var c in catalog.Capabilities.Where(c => c.Method is "git_clone" or "cmd_run"))
            snapshot[c.Server!] = JsonSerializer.SerializeToNode(new[] { new McpToolInfo { Name = c.Method!, InputSchema = c.InputSchema, OutputSchema = c.OutputSchema } }, RealProductContracts.Json);
        using var cancellation = new CancellationTokenSource(); var cleanupCalls = 0;
        var factory = new RealProductContracts.Factory(snapshot) { Handler = async (_, method, input, ct) =>
        {
            var c = catalog.Capabilities.Single(c => c.Method == method);
            Assert.Empty(PlanningContractValidation.ValidateInstance(input, c.InputSchema));
            if (method == "git_clone")
            {
                Assert.Equal(RecordedWorkspaceContractTests.Location, input!["targetDirectory"]!.ToString());
                if (mode == "partial") { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "partial"), "partial"); throw new IOException("Injected partial clone"); }
                if (mode == "missing") throw new IOException("Injected failure before creation");
                var result = git.GitClone(source, RecordedWorkspaceContractTests.Location);
                Assert.True(result.Success, result.ErrorMessage);
                return new() { Content = JsonSerializer.SerializeToNode(result, RealProductContracts.Json) };
            }
            Assert.Equal("cmd_run", method); cleanupCalls++;
            Assert.False(ct.IsCancellationRequested);
            Assert.Equal("delete_directory", input!["commandName"]!.ToString());
            var parameters = input["parameters"]!.AsObject();
            Assert.Equal(new[] { "path" }, parameters.Select(p => p.Key)); Assert.Equal(RecordedWorkspaceContractTests.Location, parameters["path"]!.ToString());
            var deleted = await cmd.RunAsync("delete_directory", parameters, null, ct);
            Assert.True(deleted.Success, deleted.ErrorMessage);
            return new() { Content = JsonSerializer.SerializeToNode(deleted, RealProductContracts.Json) };
        } };
        var runner = new Runner(current.Root, target, mode, cancellation);
        var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human(true), Limits = new() { TenantId = "host-tenant", RunId = "synthetic-local-run" } };
        engine.AgentTaskRunners["coding"] = runner;
        var resultRun = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["pullRequestUrl"] = "https://example.test/project/pull/17", ["reviewInstructions"] = "Mocked lifecycle only" }, cancellation.Token);
        Assert.True(resultRun.Success == (mode == "success"), resultRun.Error?.Message);
        Assert.Equal(1, cleanupCalls); Assert.False(Directory.Exists(target));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(unrelated, "keep")));
        Assert.Equal("local source\n", File.ReadAllText(Path.Combine(source, "README.md")));
        Assert.Equal(mode is "success" or "cancelled" ? 1 : 0, runner.Runs);
        foreach (var file in Directory.EnumerateFiles(current.Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
    }

    private sealed class Runner(string root, string target, string mode, CancellationTokenSource cancellation) : IAgentTaskRunner
    {
        public int Runs;
        public string Description => "Mocked lifecycle runner; no Copilot dispatch";
        public Task<AgentTaskRunnerContract> DescribeAsync(CancellationToken ct) => Task.FromResult(new AgentTaskRunnerContract(Description, AgentTaskContracts.InputSchema));
        public Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext context, CancellationToken ct)
        {
            Assert.Equal("host-tenant", context.TenantId);
            Assert.Equal(RecordedWorkspaceContractTests.Location, context.Task.Workspace);
            var policy = new GnOuGo.GithubCopilot.Mcp.CodePolicy(new() { DefaultWorkingDirectory = root, AllowedWorkingRoots = [root] }, root);
            Assert.Equal(target, policy.ResolveProjectRoot(context.Task.Workspace));
            Assert.True(File.Exists(Path.Combine(target, "README.md")));
            return Task.FromResult<IReadOnlyList<string>>(mode == "refused" ? ["Host policy refusal"] : []);
        }
        public Task<AgentTaskResult> RunAsync(AgentTaskContext context, CancellationToken ct)
        {
            Runs++;
            if (mode == "cancelled") { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            // Explicit mocked observations; no claims about actual shell/model execution.
            var evidence = context.Task.Verification.Select(v => new AgentTaskEvidence(v.Id, v.Kind, v.Subject,
                v.Kind == "command.exit" ? new() { ["exit_code"] = 0, ["working_directory"] = target, ["tool_success"] = true } : new() { ["exists"] = true, ["sha256"] = "mock" })).ToArray();
            return Task.FromResult(new AgentTaskResult("completed", new JsonObject { ["validationPassed"] = true, ["summary"] = "Mocked validation", ["commands"] = new JsonArray() }, evidence, [], new(0, 0, 0)));
        }
        public Task<AgentTaskResult> ReconcileAsync(AgentTaskContext context, CancellationToken ct) => throw new InvalidOperationException("No uncertain work in this fixture");
    }
}
