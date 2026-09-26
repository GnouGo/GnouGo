using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RecordedReviewFieldExecutionTests
{
    [Theory]
    [InlineData("nominal")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("wrong_type")]
    [InlineData("invalid_enum")]
    [InlineData("failed_comment")]
    [InlineData("cancelled")]
    [InlineData("absent_producer")]
    [InlineData("refused")]
    public async Task RecordedWorkflowExecutesTypedRecordBindingsWithMockedExternalEffects(string mode)
    {
        var planning = new RecordedReviewFieldPlanningTests.Replay(historical: false);
        var state = await planning.Run();
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Message)));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var observed = new Execution(mode, cancellation);
        var engine = new WorkflowEngine { McpClientFactory = observed.Factory(state.Catalog!), LLMClient = observed,
            LlmDefaults = new() { Model = "mock" }, HumanInputProvider = new PlanningCorpus.Human(mode != "refused") };
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject
        {
            ["pullRequestUrl"] = "https://example.test/example/project/pull/17", ["reviewInstructions"] = "Check the observed changes."
        }, cancellation.Token);
        Assert.Equal(mode is "nominal" or "empty", result.Success);
        if (mode is "refused" or "absent_producer")
        {
            Assert.Empty(observed.Effects); Assert.Equal(mode == "refused" ? 0 : 1, observed.Calls);
            return;
        }
        Assert.Equal(4, observed.Calls); // Four retained semantic transforms; selecting five fields adds zero inference.
        Assert.Equal("cleanup", observed.Effects[^1]); Assert.Equal("workflows/review-test", observed.CleanupPath);
        if (mode is "nominal" or "empty")
        {
            Assert.Equal(mode == "empty" ? "APPROVE" : "REQUEST_CHANGES", result.Outputs!["reviewDecision"]!.ToString());
            Assert.Equal(mode == "empty" ? 0 : 3, observed.Comments.Count);
            Assert.Equal("submit", observed.Effects[^3]); Assert.Equal("cmd_run", observed.Effects[^2]);
            if (mode == "nominal")
            {
                AssertComment(observed.Comments[0], "src/first.cs", "First issue", "RIGHT", 2, 5);
                AssertComment(observed.Comments[1], "docs/été 東京.md", "Second \"issue\"", "LEFT", 11, 13);
                AssertComment(observed.Comments[2], "src/third.cs", "Third issue", "RIGHT", 21, 21);
            }
        }
        else
        {
            Assert.DoesNotContain("submit", observed.Effects);
            Assert.Equal(mode is "failed_comment" or "cancelled" ? 1 : 0, observed.Comments.Count);
        }
    }

    private static void AssertComment(JsonObject actual, string path, string body, string side, int start, int end)
    {
        Assert.Equal("example", actual["owner"]!.GetValue<string>()); Assert.Equal("project", actual["repo"]!.GetValue<string>());
        Assert.Equal(17, actual["pullNumber"]!.GetValue<int>()); Assert.Equal("LINE", actual["subjectType"]!.GetValue<string>());
        Assert.Equal(path, actual["path"]!.GetValue<string>()); Assert.Equal(body, actual["body"]!.GetValue<string>());
        Assert.Equal(side, actual["side"]!.GetValue<string>()); Assert.Equal(start, actual["startLine"]!.GetValue<int>());
        Assert.Equal(end, actual["line"]!.GetValue<int>()); Assert.Equal(9, actual.Count);
    }

    private sealed class Execution(string mode, CancellationTokenSource cancellation) : ILLMClient
    {
        public int Calls;
        public List<string> Effects = [];
        public List<JsonObject> Comments = [];
        public string? CleanupPath;
        private const string Base = "1111111111111111111111111111111111111111", Head = "2222222222222222222222222222222222222222";
        private static JsonObject Parse(string text) => JsonNode.Parse(text)!.AsObject();
        private static JsonObject Coverage() => Parse("""{"totalFiles":3,"reviewedFiles":3,"skippedFiles":0,"truncatedFiles":0,"skippedPaths":[],"truncatedPaths":[]}""");
        private JsonArray Findings()
        {
            if (mode == "empty") return [];
            var records = JsonNode.Parse("""[{"path":"src/first.cs","side":"Right","startLine":2,"endLine":5,"explanation":"First issue"},{"path":"docs/été 東京.md","side":"Left","startLine":11,"endLine":13,"explanation":"Second \"issue\""},{"path":"src/third.cs","side":"Right","startLine":21,"endLine":21,"explanation":"Third issue"}]""")!.AsArray();
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i]!; record["fingerprint"] = "finding-" + i; record["severity"] = "High";
                record["category"] = "correctness"; record["confidence"] = 0.9; record["evidence"] = "Mocked observed diff";
            }
            return records;
        }
        internal InMemoryMcpClientFactory Factory(PlanningCatalog catalog)
        {
            var factory = new InMemoryMcpClientFactory();
            foreach (var group in catalog.Capabilities.Where(c => c.Kind == "tool").GroupBy(c => c.Server!))
            {
                var server = new MockMcpServerConfig();
                foreach (var c in group)
                {
                    server.Tools.Add(new() { Name = c.Method!, InputSchema = c.InputSchema, OutputSchema = c.OutputSchema, EffectKind = c.EffectKind });
                    server.ToolHandlers[c.Method!] = input =>
                    {
                        Assert.Empty(PlanningContractValidation.ValidateInstance(input, c.InputSchema));
                        var response = Respond(c.Method!, input!.AsObject());
                        Assert.Empty(PlanningContractValidation.ValidateInstance(response, c.OutputSchema));
                        return new() { Content = response };
                    };
                }
                factory.RegisterServer(group.Key, server);
            }
            return factory;
        }
        private JsonObject Respond(string method, JsonObject input)
        {
            Effects.Add(method);
            switch (method)
            {
                case "pull_request_read": return new() { ["base"] = new JsonObject { ["sha"] = Base }, ["head"] = new JsonObject { ["sha"] = Head }, ["state"] = "open" };
                case "git_clone": return new() { ["repositoryRoot"] = "/workspace/workflows/review-test", ["projectRootRelative"] = "workflows/review-test", ["remoteUrl"] = "https://example.test/example/project.git", ["branch"] = "refs/pull/17/head" };
                case "copilot_interactive_one_shot": return Parse("""{"handle":"checks","copilotSessionId":"mock","content":"Checks observed","model":"mock","progressEvents":[],"completed":true,"modifiedFiles":[],"toolExecutions":[{"toolCallId":"check","parentToolCallId":null,"toolName":"shell","argumentsJson":"{\"command\":\"run-tests\"}","completionObserved":true,"toolSucceeded":true,"conflictingCompletion":false,"terminals":[{"workingDirectory":"workflows/review-test","exitCode":0,"text":"passed"}],"errorCode":null}]}""");
                case "git_compare_refs": return new() { ["repositoryRoot"] = "/workspace/workflows/review-test", ["baseRef"] = Base, ["headRef"] = Head, ["baseSha"] = Base, ["headSha"] = Head,
                    ["mergeBaseSha"] = Base, ["comparedFromSha"] = Base, ["files"] = new JsonArray(), ["filesJson"] = "[]", ["totalFiles"] = 0, ["offset"] = 0, ["pageSize"] = 100,
                    ["hasMore"] = false, ["nextCursor"] = null, ["totalPatchCharacters"] = 0, ["truncatedFileCount"] = 0 };
                case "copilot_review_start": return new() { ["reviewHandle"] = "review", ["sessionHandle"] = "mock", ["baseSha"] = Base, ["headSha"] = Head, ["batchCount"] = 1, ["coverage"] = Coverage() };
                case "copilot_review_analyze_batch": return new() { ["reviewHandle"] = "review", ["batchIndex"] = 0, ["findings"] = Findings(), ["rejectedFindings"] = new JsonArray() };
                case "copilot_review_finish": return new() { ["baseSha"] = Base, ["headSha"] = Head, ["findings"] = Findings(), ["coverage"] = Coverage(), ["rejectedFindings"] = new JsonArray(), ["summary"] = "Mocked review", ["complete"] = true, ["blockingFindingCount"] = Findings().Count };
                case "pull_request_review_write": if (input["method"]!.ToString() == "submit_pending") Effects.Add("submit"); return new() { ["id"] = 17 };
                case "add_comment_to_pending_review":
                    Comments.Add(input.DeepClone().AsObject());
                    if (mode == "failed_comment") throw new IOException("Mocked comment failure");
                    if (mode == "cancelled") cancellation.Cancel();
                    return new() { ["id"] = Comments.Count };
                case "cmd_run":
                    Assert.Equal("delete_directory", input["commandName"]!.ToString());
                    CleanupPath = JsonNode.Parse(input["parametersJson"]!.GetValue<string>())!["path"]!.GetValue<string>(); Effects.Add("cleanup");
                    return Parse("""{"commandName":"delete_directory","shell":null,"workingDirectory":null,"exitCode":0,"success":true,"timedOut":false,"stdout":null,"stderr":null,"outputTruncated":false,"startedAtUtc":null,"finishedAtUtc":null,"durationMs":0}""");
                default: throw new InvalidOperationException("Unexpected mocked operation: " + method);
            }
        }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            var fields = request.StructuredOutputSchema!["properties"]!.AsObject();
            var data = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf("Business data (JSON):\n", StringComparison.Ordinal) + "Business data (JSON):\n".Length)..])!;
            if (fields.ContainsKey("owner"))
            {
                if (mode == "absent_producer") throw new IOException("No parsed input");
                Assert.Equal("https://example.test/example/project/pull/17", data["pullRequestUrl"]!.ToString());
                return Task.FromResult(new LLMResponse { Json = Parse("""{"owner":"example","repo":"project","pullNumber":17,"remoteUrl":"https://example.test/example/project.git","cloneDirectory":"workflows/review-test"}""") });
            }
            if (fields.ContainsKey("headRefSpec")) return Task.FromResult(new LLMResponse { Json = new JsonObject { ["baseSha"] = Base, ["headSha"] = Head, ["headRefSpec"] = "refs/pull/17/head", ["prOpen"] = true } });
            if (fields.ContainsKey("checksPassed")) return Task.FromResult(new LLMResponse { Json = Parse("""{"runtimeContextJson":"{\"checks\":\"passed\"}","checksPassed":true,"checksSummary":"Mocked checks passed"}""") });
            Assert.True(fields.ContainsKey("findings"));
            var review = data["review"]!["findings"]!.AsArray();
            Assert.Equal(mode == "empty" ? 0 : 3, review.Count);
            var findings = new JsonArray(review.Select(r => (JsonNode?)new JsonObject { ["path"] = r!["path"]!.DeepClone(),
                ["side"] = r["side"]!.GetValue<string>().ToUpperInvariant(), ["startLine"] = r["startLine"]!.DeepClone(),
                ["endLine"] = r["endLine"]!.DeepClone(), ["body"] = r["explanation"]!.DeepClone() }).ToArray());
            if (mode == "missing") findings[0]!.AsObject().Remove("body");
            if (mode == "null") findings[0]!["path"] = null;
            if (mode == "wrong_type") findings[0]!["endLine"] = "5";
            if (mode == "invalid_enum") findings[0]!["side"] = "MIDDLE";
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["event"] = mode == "empty" ? "APPROVE" : "REQUEST_CHANGES", ["body"] = "Mocked review summary", ["findings"] = findings } });
        }
    }
}
