using System.Text.Json.Nodes;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PublishedReviewQualificationTests
{
    private static readonly PublishedReviewQualification Target = new("AxaFrance", "SmartGuide", 718, "head", "base");

    [Theory]
    [InlineData("complete", true)]
    [InlineData("local_only", false)]
    [InlineData("other_head", false)]
    [InlineData("other_review", false)]
    [InlineData("other_pr", false)]
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
            case "local_only": events.Clear(); break;
            case "other_head": review["commit_id"] = "different"; break;
            case "other_review": comment["pull_request_review_id"] = 1000; break;
            case "other_pr": submit["pullNumber"] = 610; break;
            case "other_verdict": review["state"] = "CHANGES_REQUESTED"; break;
            case "no_inline": comments.Clear(); break;
            case "other_line": comment["line"] = 6; break;
            case "summary_as_comment": comment.Remove("diff_hunk"); break;
        }
        var result = Target.Verify(events, review, comments);
        Assert.True(passed == result["passed"]!.GetValue<bool>(), result.ToJsonString());
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
