using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PublishedReviewQualificationTests
{
    [Theory]
    [InlineData("project_read", true)]
    [InlineData("project_list", true)]
    [InlineData("project_stat", true)]
    [InlineData("project_write", false)]
    [InlineData("read_task_input", true)]
    [InlineData("unreviewed_host_tool", false)]
    public void LocalReviewDialogsMatchTheActiveInvocationAndTypedRead(string tool, bool accepted)
    {
        var owner = new AgentTaskContext("tenant", "run", "step", new() { Runner = "coding", Capabilities = ["project.read"] });
        var details = new JsonObject { ["tenant_id"] = "tenant", ["runner"] = "coding", ["kind"] = "permission" };
        if (tool.StartsWith("project_", StringComparison.Ordinal))
        {
            details["project_tool"] = tool;
            details["project_path"] = "src/reviewed.cs";
        }
        else details["host_tool"] = tool;
        var request = new HumanInputRequest { RunId = owner.RunId, ParentInvocationId = owner.InvocationId,
            StepId = "step/human/" + Guid.NewGuid().ToString("N"), Context = details, Mode = HumanInputContract.ModeChoice,
            Choices = ["Allow once", "Refuse"], AllowAbandon = true };
        Assert.Equal(accepted, LocalReviewQualification.OwnsReadDialog(owner, request));
        Assert.False(LocalReviewQualification.OwnsReadDialog(owner with { InvocationId = "other" }, request));
        details["kind"] = "question";
        Assert.False(LocalReviewQualification.OwnsReadDialog(owner, request));
    }

    [Fact]
    public void LocalReviewPinsAnObservedReadAndUsesOneCompleteComparisonWhenAvailable()
    {
        var target = new PublishedReviewQualification("SlimPlanet", "SlimFaas", 519, new string('a', 40), new string('b', 40));
        var prompt = LocalReviewQualification.Prompt("workflows/review", target, 8, "src/changed.ts");
        Assert.Contains("8 changed paths", prompt);
        Assert.Contains("public object output named report", prompt);
        Assert.Contains("check_annotation_cleanup", prompt);
        Assert.Contains("expectedCompleteFileCount=8", prompt);
        Assert.Contains("always block", prompt);
        var paged = LocalReviewQualification.Prompt("workflows/review", target, 101, "src/changed.ts");
        Assert.Contains("complete ordered page directory", paged);
        Assert.DoesNotContain("expectedCompleteFileCount", paged);
        Assert.Contains("file.read with subject src/changed.ts", prompt);
        Assert.Contains("review.diff with subject " + target.Head, prompt);
        Assert.Contains("bind its files output directly", prompt);
    }

    [Fact]
    public void LocalReportAnchorsOnlyAddedHeadLinesAcrossHunks()
    {
        var lines = QualificationReports.AddedLines("@@ -3,2 +4,2 @@\n context\n-old\n+new\n@@ -20,0 +23,2 @@ scope\n+second\n+third\n\\ No newline at end of file");
        Assert.Equal(new Dictionary<int, string> { [5] = "new", [23] = "second", [24] = "third" }, lines);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Published on GitHub.", false)]
    [InlineData("pnpm test exited 0.", false)]
    [InlineData("pnpm test exited 7. uv run pytest exited 0.", false)]
    [InlineData("pnpm test exited 7 after 0 warnings. uv run pytest exited 0 after 7 seconds.", false)]
    [InlineData("pnpm test exited 0. uv run pytest was requested.", false)]
    [InlineData("pnpm test exited 0. uv run pytest exited 7.", true)]
    [InlineData("{\"result\":{\"checks\":[{\"exitCode\":0,\"command\":\"pnpm test\"},{\"command\":\"uv run pytest\",\"exitCode\":7}]}}", true)]
    public void CallerReceivesTheObservedChecksInTheAnswerToThisInvocation(string? text, bool passed)
    {
        var answer = new JsonObject { ["run"] = "execution", ["agent"] = "saved", ["text"] = text };
        var run = new JsonObject { ["run"] = "saved", ["execution_run_id"] = "execution", ["caller_response"] = answer,
            ["observed_commands"] = new JsonArray(new JsonObject { ["command"] = "pnpm test", ["exit_code"] = 0 },
                new JsonObject { ["command"] = "uv run pytest", ["exit_code"] = 7 }) };
        Assert.Equal(passed, PublishedReviewQualification.ReturnedReportFinding(run) is null);
        if (!passed) return;
        answer["run"] = "other";
        Assert.NotNull(PublishedReviewQualification.ReturnedReportFinding(run));
        answer["run"] = "execution";
        answer["agent"] = "other";
        Assert.NotNull(PublishedReviewQualification.ReturnedReportFinding(run));
    }

    [Fact]
    public async Task LocalUnitCheckUsesTheEntireConfinedCampaignPath()
    {
        var environment = new Dictionary<string, string?>();
        LocalReviewQualification.ConfigureCommands(environment);
        var path = "workflows/pr120-agent-routing-20261126-30/routing30-code-20261127-013140/repository/" + LocalReviewQualification.UnitScript;
        var limit = int.Parse(environment[$"Cmd__AllowedCommands__{LocalReviewQualification.UnitCheck}__Parameters__path__MaxLength"]!,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(122, path.Length);
        Assert.InRange(path.Length, 1, limit);
        var pattern = environment[$"Cmd__AllowedCommands__{LocalReviewQualification.UnitCheck}__Parameters__path__Pattern"]!;
        Assert.Matches(pattern, path);
        Assert.DoesNotMatch(pattern, path.Replace("cleanup.py", "other.py", StringComparison.Ordinal));
        var target = new PublishedReviewQualification("SlimPlanet", "SlimFaas", 519, new string('a', 40), new string('b', 40));
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalReviewQualification.PromptAsync(new string('x', limit), target, new(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LocalReviewRequiresTheObservedPinnedCommandAndExitStatus()
    {
        const string root = "workflows/review";
        var record = new JsonObject { ["server"] = "GnOuGo.Cmd.Mcp", ["tool"] = "cmd_run",
            ["arguments"] = new JsonObject { ["commandName"] = LocalReviewQualification.UnitCheck,
                ["parameters"] = new JsonObject { ["path"] = root + "/repository/" + LocalReviewQualification.UnitScript } },
            ["result"] = new JsonObject { ["commandName"] = LocalReviewQualification.UnitCheck,
                ["stderr"] = "Ran 4 tests in 0.015s\n\nOK\n", ["exitCode"] = 0, ["success"] = true, ["timedOut"] = false } };
        Assert.True(LocalReviewQualification.PassedCheck(record, root));
        Assert.False(LocalReviewQualification.PassedCheck(record, root + "-other"));
        record["result"]!["exitCode"] = 1;
        Assert.False(LocalReviewQualification.PassedCheck(record, root));
        record["result"]!["exitCode"] = 0;
        record["result"]!["stderr"] = "OK";
        Assert.False(LocalReviewQualification.PassedCheck(record, root));
    }

    [Fact]
    public void ForkReviewPinsTheReceivingRepositoryAndKeepsHistoricalGrantsSeparate()
    {
        var target = new PublishedReviewQualification("SlimPlanet", "SlimFaas", 519, "head", "base");
        var args = new JsonObject { ["branch"] = "head", ["remoteUrl"] = "https://github.com/SlimPlanet/SlimFaas.git" };
        Assert.True(target.AllowsClone(args));
        args["remoteUrl"] = "https://github.com/NopeCharon/SlimFaas.git";
        Assert.False(target.AllowsClone(args));
        args["remoteUrl"] = "https://github.com/SlimPlanet/SlimFaas";
        args["branch"] = "main";
        Assert.False(target.AllowsClone(args));
        Assert.True(PublishedReviewQualification.AuthorizesTarget(LiveWorkflowEvaluation.ReviewUrl, PublishedReviewQualification.LocalKindVersion));
        Assert.False(PublishedReviewQualification.AuthorizesTarget(PublishedReviewQualification.HistoricalReviewUrl, PublishedReviewQualification.LocalKindVersion));
        Assert.False(PublishedReviewQualification.AuthorizesTarget(LiveWorkflowEvaluation.ReviewUrl, PublishedReviewQualification.DeliveryVersion));
        Assert.True(PublishedReviewQualification.AuthorizesTarget(PublishedReviewQualification.HistoricalReviewUrl, PublishedReviewQualification.DeliveryVersion));
    }

    private static readonly PublishedReviewQualification Target = new("AxaFrance", "SmartGuide", 718, "head", "base");

    [Theory]
    [InlineData("complete", true)]
    [InlineData("message_with_owned_pending_id", true)]
    [InlineData("message_without_pending_readback", false)]
    [InlineData("message_for_different_pending", false)]
    [InlineData("local_only", false)]
    [InlineData("other_head", false)]
    [InlineData("other_review", false)]
    [InlineData("other_pr", false)]
    [InlineData("other_submit_head", false)]
    [InlineData("other_thread", false)]
    [InlineData("conflicting_receipt", false)]
    [InlineData("malformed_receipt", false)]
    [InlineData("other_verdict", false)]
    [InlineData("no_inline", false)]
    [InlineData("other_line", false)]
    [InlineData("summary_as_comment", false)]
    public void SubmittedReviewAndInlineCommentsMustMatchIndependentGitHubReadback(string variant, bool passed)
    {
        var events = new JsonArray();
        JsonObject Write(string tool, JsonObject args)
        {
            args["owner"] = "AxaFrance"; args["repo"] = "SmartGuide"; args["pullNumber"] = 718;
            var receipt = new JsonObject { ["tool"] = tool, ["server"] = "Github", ["arguments"] = args,
                ["error"] = false, ["result"] = new JsonObject { ["id"] = 1001 } };
            events.Add(receipt);
            return args;
        }
        Write("pull_request_review_write", new() { ["method"] = "create", ["commitID"] = "head" });
        Write("add_comment_to_pending_review", new() { ["subjectType"] = "LINE", ["side"] = "RIGHT", ["line"] = 5, ["path"] = "file.cs", ["body"] = "This case has a regression test." });
        var submit = Write("pull_request_review_write", new() { ["method"] = "submit_pending", ["event"] = "APPROVE", ["body"] = "Verified." });
        var review = new JsonObject { ["id"] = 1001, ["commit_id"] = "head", ["submitted_at"] = "2026-11-19T18:00:00Z", ["state"] = "APPROVED", ["body"] = "Verified." };
        var comment = new JsonObject { ["pull_request_review_id"] = 1001, ["commit_id"] = "head", ["path"] = "file.cs", ["side"] = "RIGHT",
            ["line"] = 5, ["body"] = "This case has a regression test.", ["diff_hunk"] = "@@ -4 +5 @@" };
        var comments = new JsonArray(comment);
        switch (variant)
        {
            case "message_with_owned_pending_id":
            case "message_without_pending_readback":
            case "message_for_different_pending":
                foreach (var evt in events.OfType<JsonObject>()) evt["result"] = new JsonObject { ["message"] = "Success" };
                if (variant != "message_without_pending_readback")
                    events[0]!["observed_pending_review_id"] = variant == "message_for_different_pending" ? "1002" : "1001";
                break;
            case "local_only": events.Clear(); break;
            case "other_head": review["commit_id"] = "different"; break;
            case "other_review": comment["pull_request_review_id"] = 1000; break;
            case "other_submit_head": submit["commitID"] = "other"; break;
            case "other_thread": submit["threadId"] = "other"; break;
            case "conflicting_receipt": events[0]!["observed_pending_review_id"] = "1001"; events[0]!["result"]!["id"] = 1002; break;
            case "malformed_receipt": events[2]!["result"] = "unexpected"; break;
            case "other_pr": submit["pullNumber"] = 610; break;
            case "other_verdict": review["state"] = "CHANGES_REQUESTED"; break;
            case "no_inline": comments.Clear(); break;
            case "other_line": comment["line"] = 6; break;
            case "summary_as_comment": comment.Remove("diff_hunk"); break;
        }
        var result = Target.Verify(events, review, comments);
        Assert.True(passed == result["passed"]!.GetValue<bool>(), result.ToJsonString());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void SubmissionRequiresTheCopilotBusinessReceiptToConfirmCompletion(bool? completed, bool allowed)
    {
        const string instructions = "Check behavior on the changed line.";
        var result = new JsonObject();
        if (completed.HasValue) result["success"] = completed.Value;
        var events = new JsonArray(new JsonObject { ["server"] = "GnOuGo.GithubCopilot.Mcp", ["tool"] = "code_agent_edit",
            ["error"] = false, ["arguments"] = new JsonObject { ["task"] = instructions }, ["result"] = result });
        Assert.Equal(allowed, ServerBusinessExecution.ReviewInstructionsObserved(events, instructions));
    }

    [Theory]
    [InlineData("literal")]
    [InlineData("unused")]
    [InlineData("optional")]
    public void DeclaredOrEchoedReviewInstructionsCannotSubstituteForCallerInput(string variant)
    {
        var inputs = new JsonObject { ["reviewText"] = "Review the changed behavior." };
        var plan = new TaskPlan { Inputs = [new() { Name = "reviewText", Type = new() { Kind = "string" }, Required = variant != "optional" }] };
        var input = new TaskValue { Kind = "input", Source = "reviewText" };
        plan.Root.Outputs.Add(new("text", variant == "literal" ? new() { Kind = "string", Text = inputs["reviewText"]!.ToString() } : input));
        if (variant == "unused") plan.Root.Outputs.Clear();
        Assert.Throws<PlanningConflictException>(() => ServerBusinessExecution.RequireInputs(plan, inputs));
        Assert.False(ServerBusinessExecution.ReviewInstructionsObserved([], inputs["reviewText"]!.GetValue<string>()));
    }

    [Fact]
    public void FreshPublicationDiscoveryExposesReviewButHistoricalLocalGrantDoesNot()
    {
        foreach (var tool in new[] { "pull_request_read", "pull_request_review_write", "add_comment_to_pending_review" })
        {
            Assert.False(DelegatedTestReviewAuthority.AllowsTool("code", "Github", tool));
            Assert.True(DelegatedTestReviewAuthority.AllowsTool(PublishedReviewQualification.Scenario, "Github", tool));
        }
        Assert.False(DelegatedTestReviewAuthority.AllowsTool(PublishedReviewQualification.Scenario, "Github", "merge_pull_request"));
    }
}
