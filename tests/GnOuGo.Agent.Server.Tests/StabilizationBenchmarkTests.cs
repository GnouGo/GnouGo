using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class StabilizationBenchmarkTests
{
    private static List<JsonObject> Cohort(string source) => StabilizationBenchmark.Cases.SelectMany(name => Enumerable.Range(1, 3).Select(i => new JsonObject
    {
        ["case"] = name, ["complexity"] = StabilizationBenchmark.Complexity(name), ["source"] = source, ["repetition"] = i,
        ["manifest"] = "same", ["provider"] = "configured", ["model"] = "fixed", ["campaign"] = "new", ["usage_bounded"] = true,
        ["execution_correct"] = true, ["nominal_success"] = true, ["logical_planning_calls"] = 1, ["repairs"] = 0,
        ["execution_variants"] = new JsonArray((JsonNode)new JsonObject { ["safety_violations"] = new JsonArray() })
    })).ToList();

    [Fact]
    public void CompleteComparableExecutionCohortsPass() => Assert.True(StabilizationBenchmark.Compare(Cohort("baseline"), Cohort("candidate"))["passed"]!.GetValue<bool>());

    [Theory]
    [InlineData("missing")][InlineData("duplicate")][InlineData("manifest")][InlineData("model")][InlineData("source")][InlineData("usage_bounded")]
    public void IncomparableCohortsRemainInconclusive(string mutation)
    {
        var after = Cohort("candidate");
        if (mutation == "missing") after.RemoveAt(0);
        else if (mutation == "duplicate") after[0] = after[1].DeepClone().AsObject();
        else after[0][mutation] = mutation == "usage_bounded" ? JsonValue.Create(false) : JsonValue.Create("different");
        Assert.Equal("inconclusive", StabilizationBenchmark.Compare(Cohort("baseline"), after)["status"]!.ToString());
    }
    [Theory]
    [InlineData("execution_correct")][InlineData("nominal_success")][InlineData("repairs")][InlineData("logical_planning_calls")][InlineData("unsafe")]
    public void GenerationOrApprovalCannotSubstituteForCorrectExecution(string mutation)
    {
        var after = Cohort("candidate");
        if (mutation == "unsafe") after[0]["execution_variants"]![0]!["safety_violations"]!.AsArray().Add("unexpected effect");
        else after[0][mutation] = mutation is "repairs" or "logical_planning_calls" ? JsonValue.Create(2) : JsonValue.Create(false);
        Assert.Equal("failed", StabilizationBenchmark.Compare(Cohort("baseline"), after)["status"]!.ToString());
    }
    [Fact]
    public async Task FilesystemOracleRequiresActualFilesAndProtectsSeedData()
    {
        var root = Directory.CreateTempSubdirectory("gnougo-oracle-").FullName;
        await using var fixture = new LocalExecutionCases(root, "/unused-no-process-started");
        fixture.Prepare("files_copy", "nominal");
        var successful = new RunResult { Success = true, Outputs = new JsonObject { ["result"] = "hello" } };
        Assert.False(fixture.Observe("files_copy", "nominal", successful, null)["correct"]!.GetValue<bool>());
        Directory.CreateDirectory(Path.Combine(root, "workflows/copy"));
        File.WriteAllText(Path.Combine(root, "workflows/copy/source.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "workflows/copy/result.txt"), "hello");
        Assert.True(fixture.Observe("files_copy", "nominal", successful, null)["correct"]!.GetValue<bool>());
        File.WriteAllText(Path.Combine(root, "seed.txt"), "unexpected");
        Assert.False(fixture.Observe("files_copy", "nominal", successful, null)["correct"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RealCaseUsesShippedStdioCommandAndReadsActualSeed()
    {
        var root = Directory.CreateTempSubdirectory("gnougo-stdio-oracle-").FullName;
        var executable = Path.Combine(AppContext.BaseDirectory, "tools", "GnOuGo.Cmd.Mcp", "GnOuGo.Cmd.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(executable)) executable = Path.Combine(AppContext.BaseDirectory, "GnOuGo.Cmd.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        await using var fixture = new LocalExecutionCases(root, executable);
        fixture.Prepare("files_read", "alternate");
        var client = await fixture.Factory.GetClientAsync("filesystem", TestContext.Current.CancellationToken);
        var response = await client.CallToolAsync("cmd_run", new JsonObject { ["commandName"] = "cat_file", ["parameters"] = new JsonObject { ["path"] = "seed.txt" } }, TestContext.Current.CancellationToken);
        Assert.Contains("changed", response.Content!.ToJsonString());
        Assert.Equal("changed 東京\n", File.ReadAllText(Path.Combine(root, "seed.txt")));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task SuccessfulExecutionStillFailsOracleForAnUnrelatedLifecycleOperation(bool unrelated)
    {
        // Reproduce the retained live failure: a write effect was mistaken for a
        // business relationship to an unrelated lifecycle operation. Contracts alone
        // permit this plan; the independent execution oracle must keep rejecting it.
        var environment = new PlanningBenchmarkCases.Environment("review_distractors");
        var engine = new WorkflowEngine { McpClientFactory = environment.Factory(), HumanInputProvider = new PlanningCorpus.Human() };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var ct = TestContext.Current.CancellationToken;
        var catalog = await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, ct);
        foreach (var source in await runtime.Capabilities.ListSourcesAsync(ct))
        {
            string? cursor = null;
            do
            {
                var page = await runtime.Capabilities.ListAsync(source.Id, cursor, ct);
                foreach (var summary in page.Capabilities) catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(summary, ct));
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        var plan = PlanningCorpus.Tasks("review_distractors", catalog);
        if (unrelated) plan.Root.Always.Add(new() { Id = "extra", Objective = "Clean up after protected publication", DependsOn = ["publish"],
            Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "cleanup")).Id });
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog)));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], PlanningBenchmarkCases.Inputs("review_distractors", "nominal"), ct);
        Assert.True(result.Success); Assert.Empty(environment.Violations);
        Assert.Equal(!unrelated, environment.Verify(result));
    }
}
