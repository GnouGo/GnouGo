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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedCloneExecutionTests
{
    [Theory]
    [InlineData("lifecycle_success")]
    [InlineData("full_review_success")]
    [InlineData("partial")]
    [InlineData("cancelled")]
    [InlineData("absent")]
    [InlineData("refused")]
    [InlineData("human_refused")]
    [InlineData("later_failure")]
    [InlineData("original")]
    public async Task RecordedWorkflowUsesOneLocalCloneAndRealCleanup(string mode)
    {
        using var execution = new Execution(mode);
        var state = await new RecordedClonePlanningTests.Replay(mode == "original" ? null : await RecordedClonePlanningTests.CurrentClone(), corrected: mode != "original", correctedFixture: mode == "lifecycle_success" ? "synthetic-resource-lifecycle" : "synthetic-shared-location").Run();
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Message)));
        var factory = execution.Factory(state.Catalog!);
        var engine = new WorkflowEngine { McpClientFactory = factory, LLMClient = execution, LlmDefaults = new() { Model = "mock" },
            HumanInputProvider = new PlanningCorpus.Human(mode != "human_refused") };
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject
        { ["pullRequestUrl"] = "https://example.test/example/project/pull/17", ["reviewInstructions"] = "Inspect the local test fixture." }, execution.Cancellation.Token);
        Assert.True(result.Success == (mode is "lifecycle_success" or "full_review_success"), JsonSerializer.Serialize(result, RealProductContracts.Json));
        Assert.Equal(mode == "human_refused" ? 0 : 1, execution.CleanupCalls);
        Assert.Equal(mode is "human_refused" or "absent" ? 0 : 1, execution.CloneCalls);
        Assert.False(Directory.Exists(execution.Target));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(execution.Root, "unrelated", "keep.txt")));
        Assert.Equal("fixture\n", File.ReadAllText(Path.Combine(execution.Source, "README.md")));
        Assert.Equal(mode == "human_refused" ? 0 : mode == "absent" ? 1 : mode == "full_review_success" ? 3 : 2, execution.Calls);
        var expectedFailure = mode switch
        {
            "partial" => "Injected interruption after partial creation",
            "absent" => "LLM_NETWORK",
            "refused" => "disabled by policy",
            "later_failure" => "Injected failure after clone",
            "original" => "targetDirectory must be relative",
            "cancelled" => "cancel",
            _ => null
        };
        if (expectedFailure is not null)
            Assert.Contains(expectedFailure, JsonSerializer.Serialize(result, RealProductContracts.Json), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(mode == "full_review_success" ? 1 : 0, execution.MockSubmissions);
        if (mode == "lifecycle_success")
        {
            Assert.Equal(Execution.Location, result.Outputs!["repositoryPath"]!.ToString());
            Assert.Equal(3, execution.RepositoryConsumers);
        }
        if (mode == "full_review_success")
        {
            // The formerly failing full replay must now complete; historical logs/fixtures remain intact.
            Assert.Equal("APPROVE", result.Outputs!["reviewDecision"]!.ToString());
            Assert.Equal("Scripted test decision, not verified real review success", result.Outputs["reviewBody"]!.ToString());
            Assert.Equal(3, execution.RepositoryConsumers);
        }
    }

    private sealed class Execution : ILLMClient, IDisposable
    {
        internal readonly string Root = Directory.CreateTempSubdirectory("gnougo-clone-execution-").FullName;
        internal const string Location = "workflows/github-pr-review";
        internal string Source => Path.Combine(Root, "source");
        internal string Target => Path.Combine(Root, Location);
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        internal int Calls, CloneCalls, CleanupCalls, RepositoryConsumers, MockSubmissions;
        private readonly string mode, sha, branch;
        private readonly GitTools git;
        private readonly CmdTools cmd;
        private readonly CommandPolicy cmdPolicy;

        internal Execution(string mode)
        {
            this.mode = mode;
            Directory.CreateDirectory(Source); Repository.Init(Source);
            File.WriteAllText(Path.Combine(Source, "README.md"), "fixture\n");
            using (var repo = new Repository(Source))
            {
                Commands.Stage(repo, "README.md");
                var signature = new Signature("Fixture", "fixture@example.test", DateTimeOffset.UnixEpoch.AddDays(1));
                sha = repo.Commit("Local fixture", signature, signature).Sha; branch = repo.Head.FriendlyName;
            }
            Directory.CreateDirectory(Path.Combine(Root, "unrelated")); File.WriteAllText(Path.Combine(Root, "unrelated", "keep.txt"), "unrelated");
            var settings = new GitServerSettings { DefaultWorkingDirectory = Root, AllowMutations = mode != "refused", AllowNetworkOperations = true, TokenEnvironmentVariables = [] };
            var policy = new GitPolicy(settings, Root);
            git = new(policy, new(policy, Options.Create(settings)), NullLogger<GitTools>.Instance);
            var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClonePlanning", "cmdsettings.json"))
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Cmd:DefaultWorkingDirectory"] = Root }).Build();
            // Use the producer's actual configuration mapper, without exposing a new production API for tests.
            var mapper = (IConfigureOptions<CmdServerSettings>)Activator.CreateInstance(
                typeof(CmdTools).Assembly.GetType("GnOuGo.Cmd.Mcp.CmdServerSettingsOptionsConfigurator", throwOnError: true)!, configuration)!;
            var cmdSettings = new CmdServerSettings(); mapper.Configure(cmdSettings);
            cmdPolicy = new(cmdSettings, Root);
            cmd = new(new(cmdPolicy, NullLogger<CommandExecutionHost>.Instance), NullLogger<CmdTools>.Instance);
        }

        internal RealProductContracts.Factory Factory(PlanningCatalog catalog)
        {
            var snapshot = new JsonObject();
            foreach (var group in catalog.Capabilities.Where(c => c.Kind == "tool").GroupBy(c => c.Server!))
                snapshot[group.Key] = JsonSerializer.SerializeToNode(group.Select(c => new McpToolInfo
                    { Name = c.Method!, Description = c.Description, InputSchema = c.InputSchema, OutputSchema = c.OutputSchema, EffectKind = c.EffectKind }).ToArray(), RealProductContracts.Json);
            return new(snapshot) { Handler = async (server, method, input, ct) =>
            {
                var contract = catalog.Capabilities.Single(c => c.Server == server && c.Method == method);
                Assert.Empty(PlanningContractValidation.ValidateInstance(input, contract.InputSchema));
                var args = input!.AsObject(); JsonObject output;
                if (method == "cmd_run")
                {
                    Assert.Equal("delete_directory", args["commandName"]!.ToString());
                    var path = JsonNode.Parse(args["parametersJson"]!.ToString())!["path"]!.ToString();
                    Assert.Equal(Location, path); CleanupCalls++;
                    Assert.False(ct.IsCancellationRequested);
                    // Actual packaged command settings, scripts and producer schema; no shell substitution in the test.
                    // Historical contract adapter only: the retained workflow keeps its issued wire shape.
                    var parameters = JsonNode.Parse(args["parametersJson"]!.ToString())!.AsObject();
                    using var baseSchema = JsonDocument.Parse("""{"type":"object","properties":{"commandName":{"type":"string"},"parameters":{"type":"object"}}}""");
                    var current = new JsonObject { ["commandName"] = "delete_directory", ["parameters"] = parameters.DeepClone() };
                    Assert.Empty(PlanningContractValidation.ValidateInstance(current, JsonNode.Parse(cmdPolicy.BuildCmdRunInputSchema(baseSchema.RootElement).GetRawText())!));
                    var result = await cmd.RunAsync("delete_directory", parameters, null, ct);
                    Assert.True(result.Success, result.ErrorMessage + result.Stderr); output = Node(result);
                }
                else output = Respond(method, args);
                Assert.Empty(PlanningContractValidation.ValidateInstance(output, contract.OutputSchema));
                return new() { Content = output, IsError = output["success"]?.ToString() == "false" };
            } };
        }

        private static JsonObject Node<T>(T value) => JsonSerializer.SerializeToNode(value, RealProductContracts.Json)!.AsObject();
        private JsonObject Respond(string method, JsonObject input)
        {
            if (method == "git_clone")
            {
                CloneCalls++;
                Assert.Equal(Source, input["remoteUrl"]!.ToString());
                Assert.Equal(mode == "original" ? "SmartGuide" : Location, input["targetDirectory"]!.ToString());
                if (mode == "partial")
                {
                    Directory.CreateDirectory(Target); File.WriteAllText(Path.Combine(Target, "partial"), "incomplete clone");
                    throw new IOException("Injected interruption after partial creation");
                }
                var result = git.GitClone(Source, input["targetDirectory"]!.ToString(), historyDepth: input["historyDepth"]!.GetValue<int>(), fetchAllBranches: input["fetchAllBranches"]!.GetValue<bool>(), tagFetchMode: input["tagFetchMode"]!.ToString());
                if (mode is "refused" or "original") Assert.False(result.Success);
                else
                {
                    Assert.True(result.Success, result.ErrorMessage); Assert.True(Repository.IsValid(Target));
                    Assert.Equal(Location, result.ProjectRootRelative);
                    Assert.Equal("fixture\n", File.ReadAllText(Path.Combine(Target, "README.md")));
                }
                if (mode == "cancelled") Cancellation.Cancel();
                return Node(result);
            }
            if (input["projectRoot"] is { } project) Assert.Equal(Location, project.ToString());
            switch (method)
            {
                case "git_fetch": RepositoryConsumers++; return Node(git.GitFetch(Location, input["remoteName"]!.ToString(), input["refSpec"]!.ToString()));
                case "git_checkout": RepositoryConsumers++; return Node(git.GitCheckout(Location, input["branchOrCommit"]!.ToString()));
                case "git_compare_refs": RepositoryConsumers++; return Node(git.GitCompareRefs(Location, input["baseRef"]!.ToString(), input["headRef"]!.ToString(), pageSize: input["pageSize"]!.GetValue<int>()));
                case "pull_request_read": return new() { ["base"] = new JsonObject { ["sha"] = sha }, ["head"] = new JsonObject { ["sha"] = sha }, ["state"] = "open" };
                case "copilot_interactive_one_shot":
                    if (mode == "later_failure") throw new IOException("Injected failure after clone");
                    return JsonNode.Parse("""{"handle":"fixture","copilotSessionId":"mock","content":"Mocked checks","model":"mock","progressEvents":[],"completed":true,"modifiedFiles":[],"toolExecutions":[]}""")!.AsObject();
                case "copilot_review": return new() { ["baseSha"] = sha, ["headSha"] = sha, ["findings"] = new JsonArray(), ["rejectedFindings"] = new JsonArray(),
                    ["summary"] = "Mocked review", ["complete"] = true, ["blockingFindingCount"] = 0, ["coverage"] = JsonNode.Parse("""{"totalFiles":0,"reviewedFiles":0,"skippedFiles":0,"truncatedFiles":0,"skippedPaths":[],"truncatedPaths":[]}""") };
                case "pull_request_review_write":
                    Assert.Equal("example", input["owner"]!.ToString());
                    Assert.Equal("project", input["repo"]!.ToString());
                    Assert.Equal(17, input["pullNumber"]!.GetValue<int>());
                    if (input["method"]!.ToString() == "submit_pending")
                    {
                        Assert.Equal("APPROVE", input["event"]!.ToString());
                        Assert.Equal("Scripted test decision, not verified real review success", input["body"]!.ToString());
                        MockSubmissions++;
                    }
                    return new() { ["id"] = 17 };
                default: throw new InvalidOperationException("Unexpected operation: " + method);
            }
        }

        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            var fields = request.StructuredOutputSchema!["properties"]!.AsObject();
            JsonObject output;
            if (fields.ContainsKey("owner"))
            {
                if (mode == "absent") throw new IOException("Injected failure before clone");
                output = new() { ["owner"] = "example", ["repo"] = "project", ["pullNumber"] = 17, ["remoteUrl"] = Source,
                    ["prRefSpec"] = "refs/heads/" + branch + ":refs/remotes/origin/pr-head", ["localPrRef"] = "refs/remotes/origin/pr-head" };
                if (mode == "original") output["targetDirectory"] = "SmartGuide";
                else Assert.False(fields.ContainsKey("targetDirectory"));
            }
            else if (fields.ContainsKey("headRef")) output = new() { ["baseRef"] = sha, ["headRef"] = "refs/remotes/origin/pr-head", ["headSha"] = sha, ["title"] = "Local fixture" };
            else output = new() { ["event"] = "APPROVE", ["decision"] = "APPROVE", ["finalBody"] = "Scripted test decision, not verified real review success", ["comments"] = new JsonArray() };
            return Task.FromResult(new LLMResponse { Json = output });
        }

        public void Dispose()
        {
            Cancellation.Dispose();
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
