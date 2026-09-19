using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BenchmarkOracleTests
{
    [Theory]
    [InlineData("nominal", "APPROVE")]
    [InlineData("failure", "REQUEST_CHANGES")]
    [InlineData("incomplete", "COMMENT")]
    public async Task ReviewOracleUsesCapturedChecksAndOneWorkspace(string variant, string expected)
    {
        var sample = new PlanningBenchmarkCases.Environment("review_french", variant);
        var factory = sample.Factory();
        await using var client = await factory.GetClientAsync("benchmark", TestContext.Current.CancellationToken);
        async Task<JsonNode?> Call(string method, JsonObject arguments) => (await client.CallToolAsync(method, arguments, TestContext.Current.CancellationToken)).Content;
        var clone = await Call("clone_repository", new() { ["pr_url"] = PlanningBenchmarkCases.Inputs("review_french", variant)["pr_url"]!.ToString() });
        var directory = clone!["directory"]!.ToString();
        foreach (var check in new[] { "dependencies", "lint", "unit", "integration" }) await Call("run_check", new() { ["directory"] = directory, ["check"] = check });
        await Call("review_changes", new() { ["directory"] = directory, ["review_text"] = PlanningBenchmarkCases.Inputs("review_french", variant)["review_text"]!.ToString() });
        var draft = await Call("evaluate_review", new() { ["directory"] = directory });
        Assert.Equal(expected, draft!["event"]!.ToString());
        await Call("publish_review", new() { ["draftId"] = draft["draftId"]!.ToString() });
        await Call("remove_workspace", new() { ["directory"] = directory });
        Assert.True(sample.Verify(new RunResult { Success = true }));
        await Call("clone_repository", new() { ["pr_url"] = PlanningBenchmarkCases.Inputs("review_french", variant)["pr_url"]!.ToString() });
        Assert.False(sample.Verify(new RunResult { Success = true }));
    }

    [Fact]
    public void InjectedWriteFailureCannotCountAsSuccessfulExecution()
    {
        var sample = new PlanningBenchmarkCases.Environment("protected_cleanup", "failure");
        sample.Effects.AddRange(["write", "cleanup"]);
        Assert.False(sample.Verify(new RunResult { Success = true }));
        Assert.True(sample.Verify(new RunResult { Success = false }));
    }

    [Fact]
    public async Task MissingEvidenceCannotPassByPublishingAnInventedDraft()
    {
        var sample = new PlanningBenchmarkCases.Environment("review_french");
        await using var client = await sample.Factory().GetClientAsync("benchmark", TestContext.Current.CancellationToken);
        await client.CallToolAsync("publish_review", new JsonObject { ["draftId"] = "invented" }, TestContext.Current.CancellationToken);
        Assert.Contains("unapproved_draft", sample.Violations);
        Assert.False(sample.Verify(new RunResult { Success = true }));
    }
}
