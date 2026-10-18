using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningBusinessJudgeReferenceTests
{
    [Fact]
    public async Task FreshContextIncludesOnlyDeterministicInputReadinessAndGenericExecutionSemantics()
    {
        var (state, runtime) = await PreparedAsync();
        state.ValidationResults.Add(new("input_readiness:/tasks/read/inputs/source", "unverified",
            "Source JSON is runtime-dependent; complete request size is unknown.", []));
        state.ValidationResults.Add(new("judge:old", "unverified", "OLD_JUDGE_OPINION", []));
        state.ValidationResults.Add(new("other", "unverified", "UNRELATED_REVIEW_TEXT", []));
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        var request = runtime.Calls[^1];
        var context = JsonNode.Parse(request.Prompt[(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
        var readiness = Assert.Single(context["inputReadiness"]!.AsArray());
        Assert.Equal("input_readiness:/tasks/read/inputs/source", readiness!["id"]!.ToString());
        Assert.DoesNotContain("OLD_JUDGE_OPINION", request.Prompt);
        Assert.DoesNotContain("UNRELATED_REVIEW_TEXT", request.Prompt);
        Assert.Contains("requires is a mandatory Boolean assertion", request.Prompt);
        Assert.Contains("conditional selects an execution branch", request.Prompt);
        Assert.Contains("does not require a successful result containing false", request.Prompt);

        state = PlannerFixture.Clone(state);
        state.ValidationResults.Add(new("judge:another", "unverified", "ANOTHER_OPINION", []));
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        Assert.Equal(2, runtime.Calls.Count);
        state.ValidationResults.RemoveAll(r => r.Id.StartsWith("input_readiness:", StringComparison.Ordinal));
        state.ValidationResults.Add(new("input_readiness:/tasks/read/inputs/source", "unverified", "Source JSON contains 20 UTF-8 bytes.", []));
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        Assert.Equal(3, runtime.Calls.Count);
    }

    [Fact]
    public async Task MixedInvalidReferencesAreWhollyInconclusiveAndNeverUseExistingAuthority()
    {
        var (state, runtime) = await PreparedAsync();
        state.Request.Options["automatic_correction"] = true;
        state.Request.Options[PlanningBusinessJudge.AuthorityKey] = new JsonArray("/tasks/greet/outputs/message");
        state.Plan!.Root.Tasks[0].Outputs[0].Value.Text = "PRIVATE_NEIGHBOR_VALUE";
        var report = PlanningBusinessJudgeTests.Report("/taskPlan/root/tasks/0/outputs/0");
        var rejected = PlanningBusinessJudgeTests.Report("/taskPlan/root/tasks/999")["findings"]![0]!.DeepClone();
        rejected["explanation"] = "REJECTED_PRIVATE_EXPLANATION";
        rejected["evidence"] = new JsonArray("/taskPlan/root/tasks/0/outputs/88/value");
        report["findings"]!.AsArray().Add(rejected);
        runtime.Respond = (_, _) => new() { Json = report };
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);

        var entry = Assert.Single(state.Request.Options[PlanningBusinessJudge.HistoryKey]!.AsArray())!;
        Assert.Equal("2", entry["version"]!.ToString());
        Assert.Equal("invalid", entry["validation_status"]!.ToString());
        Assert.Equal("inconclusive", entry["outcome"]!.ToString());
        Assert.Null(entry["response"]);
        Assert.DoesNotContain("PRIVATE_NEIGHBOR_VALUE", entry.ToJsonString());
        Assert.DoesNotContain("REJECTED_PRIVATE_EXPLANATION", entry.ToJsonString());
        var errors = entry["validation_diagnostics"]!.AsArray();
        Assert.Equal(2, errors.Count);
        Assert.Equal("/findings/1/location", errors[0]!["location"]!.ToString());
        Assert.Equal("/taskPlan/root/tasks/999", errors[0]!["reference"]!.ToString());
        Assert.Equal("reference_not_found", errors[0]!["reason"]!.ToString());
        Assert.Equal("/findings/1/evidence/0", errors[1]!["location"]!.ToString());
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.EditablePaths);
        Assert.Empty(state.RevisionScope);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(2, state.ValidationResults.Count(r => r.Id.StartsWith("judge:reference:", StringComparison.Ordinal)));

        state = PlannerFixture.Clone(state);
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Null(state.EditablePaths);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, state.ValidationResults.Count(r => r.Id.StartsWith("judge:reference:", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("inconclusive", "invalid")]
    [InlineData("revision_required", "invalid")]
    [InlineData("inconclusive", "valid")]
    public async Task InconclusiveHistoryCannotRouteEvenWhenItContainsAResponseAndAuthorizedLocation(string outcome, string validation)
    {
        var (state, runtime) = await PreparedAsync();
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        var entry = Assert.Single(state.Request.Options[PlanningBusinessJudge.HistoryKey]!.AsArray())!;
        entry["outcome"] = outcome;
        entry["validation_status"] = validation;
        entry["response"] = PlanningBusinessJudgeTests.Report("/taskPlan/root/tasks/0/outputs/0");
        state.Request.Options["automatic_correction"] = true;
        state.Request.Options[PlanningBusinessJudge.AuthorityKey] = new JsonArray("/tasks/greet/outputs/message");
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Null(state.EditablePaths);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, runtime.Calls.Count);
    }

    [Fact]
    public async Task ValidV2ReferenceMayUseOnlyAnExistingAuthorizedSlot()
    {
        var (state, runtime) = await PreparedAsync();
        state.Request.Options["automatic_correction"] = true;
        state.Request.Options[PlanningBusinessJudge.AuthorityKey] = new JsonArray("/tasks/greet/outputs/message");
        runtime.Respond = (_, _) => new() { Json = PlanningBusinessJudgeTests.Report("/taskPlan/root/tasks/0/outputs/0/value/text") };
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        var diagnostic = Assert.Single(state.Diagnostics);
        Assert.Equal("JUDGE_SCOPED_REVIEW", diagnostic.Code);
        Assert.Equal("/tasks/greet/outputs/message", diagnostic.Location);
        Assert.Equal(["/tasks/greet/outputs/message"], state.EditablePaths);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task InvalidSchemaIsRetainedAsBoundedMetadataWithoutItsRejectedValues()
    {
        var (state, runtime) = await PreparedAsync();
        runtime.Respond = (_, _) => new() { Json = new JsonObject { ["secret"] = "PRIVATE_REJECTED_VALUE" } };
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        var entry = Assert.Single(state.Request.Options[PlanningBusinessJudge.HistoryKey]!.AsArray())!;
        Assert.Equal("invalid", entry["validation_status"]!.ToString());
        Assert.Equal("inconclusive", entry["outcome"]!.ToString());
        Assert.All(entry["validation_diagnostics"]!.AsArray(), error =>
        {
            Assert.Equal("response_schema_invalid", error!["reason"]!.ToString());
            Assert.Null(error["reference"]);
        });
        Assert.DoesNotContain("PRIVATE_REJECTED_VALUE", entry.ToJsonString());
        Assert.Null(entry["response"]);
        Assert.Null(state.RejectedProposalHash);
        await PlanningBusinessJudge.ReviewAsync(PlannerFixture.Clone(state), runtime, PlannerFixture.Ct);
        Assert.Equal(2, runtime.Calls.Count);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("evidence")]
    public async Task NullReferenceTokensFailTheirIssuedSchemaWithoutPartialRouting(string field)
    {
        var (state, runtime) = await PreparedAsync();
        var report = PlanningBusinessJudgeTests.Report("/taskPlan/root/tasks/0/outputs/0");
        if (field == "location") report["findings"]![0]![field] = null;
        else report["findings"]![0]![field] = new JsonArray((JsonNode?)null);
        state.Request.Options["automatic_correction"] = true;
        state.Request.Options[PlanningBusinessJudge.AuthorityKey] = new JsonArray("/tasks/greet/outputs/message");
        runtime.Respond = (_, _) => new() { Json = report };
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        var entry = Assert.Single(state.Request.Options[PlanningBusinessJudge.HistoryKey]!.AsArray())!;
        Assert.Equal("inconclusive", entry["outcome"]!.ToString());
        Assert.Equal("invalid", entry["validation_status"]!.ToString());
        Assert.Null(entry["response"]);
        Assert.Null(state.EditablePaths);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(2, state.ModelCalls);
    }

    [Fact]
    public async Task AbsentReviewVersionRetainsHistoricalContextAndInstructionFingerprint()
    {
        var (state, runtime) = await PreparedAsync();
        state.Request.Options.Remove("business_judge_review_version");
        state.ValidationResults.Add(new("input_readiness:/tasks/read", "unverified", "NEW_REVIEW_FACT", []));
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        var request = runtime.Calls[^1];
        Assert.DoesNotContain("inputReadiness", request.Prompt);
        Assert.DoesNotContain("NEW_REVIEW_FACT", request.Prompt);
        Assert.DoesNotContain("requires is a mandatory Boolean assertion", request.Prompt);
        var context = JsonNode.Parse(request.Prompt[(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
        var entry = Assert.Single(state.Request.Options[PlanningBusinessJudge.HistoryKey]!.AsArray())!;
        Assert.Equal("1", entry["version"]!.ToString());
        Assert.Equal(PlanningGraphCompiler.Fingerprint("business-judge-v1\n" + context.ToJsonString()), entry["fingerprint"]!.ToString());
        Assert.Null(entry["validation_status"]);
    }

    [Theory]
    [InlineData("/taskPlan/root/tasks/0/condition", true, null)]
    [InlineData("/contracts/0/inputSchema/a~1b~0雪", true, null)]
    [InlineData("/contracts/0/inputSchema/a~2b", false, "invalid_pointer_escape")]
    [InlineData("/taskPlan/root/tasks/00", false, "reference_not_found")]
    [InlineData("taskPlan/root", false, "invalid_pointer_syntax")]
    public async Task PointerValidationPreservesNullUnicodeAndLiteralEscapes(string reference, bool valid, string? reason)
    {
        var (state, runtime) = await PreparedAsync();
        state.Discovery.Resolved.Add(new() { Id = "declared", Version = "1",
            InputSchema = new JsonObject { ["a/b~雪"] = null, ["privateAdjacent"] = "PRIVATE_ADJACENT" } });
        runtime.Respond = (_, _) =>
        {
            var report = PlanningBusinessJudgeTests.Report("/taskPlan/root/tasks/0");
            report["findings"]![0]!["evidence"] = new JsonArray(reference);
            return new() { Json = report };
        };
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        var entry = Assert.Single(state.Request.Options[PlanningBusinessJudge.HistoryKey]!.AsArray())!;
        Assert.Equal(valid ? "valid" : "invalid", entry["validation_status"]!.ToString());
        if (!valid)
        {
            var error = Assert.Single(entry["validation_diagnostics"]!.AsArray())!;
            Assert.Equal("/findings/0/evidence/0", error["location"]!.ToString());
            Assert.Equal(reference, error["reference"]!.ToString());
            Assert.Equal(reason, error["reason"]!.ToString());
        }
        Assert.DoesNotContain("PRIVATE_ADJACENT", entry.ToJsonString());
    }

    [Fact]
    public async Task RejectionDiagnosticsAreBoundedAndDoNotCopyReportText()
    {
        var (state, runtime) = await PreparedAsync();
        var report = PlanningBusinessJudgeTests.Report();
        for (var i = 0; i < 32; i++)
        {
            var finding = PlanningBusinessJudgeTests.Report("/" + new string('x', 2200))["findings"]![0]!.DeepClone();
            finding["evidence"] = new JsonArray("/<img src=x onerror=alert(1)>");
            finding["explanation"] = "PRIVATE_REJECTED_EXPLANATION";
            report["findings"]!.AsArray().Add(finding);
        }
        runtime.Respond = (_, _) => new() { Json = report };
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        var entry = Assert.Single(state.Request.Options[PlanningBusinessJudge.HistoryKey]!.AsArray())!;
        var errors = entry["validation_diagnostics"]!.AsArray();
        Assert.Equal(16, errors.Count);
        Assert.Equal(48, entry["validation_diagnostics_omitted"]!.GetValue<int>());
        Assert.Equal("pointer_too_long", errors[0]!["reason"]!.ToString());
        Assert.True(errors[0]!["reference_truncated"]!.GetValue<bool>());
        Assert.True(errors[0]!["reference"]!.ToString().Length <= 256);
        Assert.DoesNotContain("PRIVATE_REJECTED_EXPLANATION", entry.ToJsonString());
        Assert.Null(entry["response"]);
        Assert.True(entry.ToJsonString().Length < 10_000);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
    }

    [Fact]
    public async Task V2PendingReceiptRecoversTheIssuedIdentityWithoutRetryingAnInvalidReview()
    {
        var (state, runtime) = await PreparedAsync();
        runtime.Respond = (_, _) => throw new IOException("Receipt retained; checkpoint interrupted.");
        await Assert.ThrowsAsync<IOException>(() => PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct));
        var id = state.PendingCall!.Id;
        var prompt = state.PendingCall.Request.Prompt;
        Assert.Equal("2", state.Request.Options["business_judge_pending_context"]!["version"]!.ToString());
        state = PlannerFixture.Clone(state);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(id, request.ClientRequestId);
            Assert.Equal(prompt, request.Prompt);
            return new() { Json = PlanningBusinessJudgeTests.Report("/missing") };
        };
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        Assert.Null(state.PendingCall);
        Assert.Null(state.Request.Options["business_judge_pending_context"]);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.ValidationResults, r => r.Id == "judge:summary" && r.Outcome == "inconclusive");
        await PlanningBusinessJudge.ReviewAsync(PlannerFixture.Clone(state), runtime, PlannerFixture.Ct);
        Assert.Equal(3, runtime.Calls.Count); // Generation, interrupted receipt, receipt recovery; no new request.
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> PreparedAsync()
    {
        var runtime = new TestRuntime();
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        state.Request.Options["business_judge"] = true;
        state.Request.Options["business_judge_review_version"] = 2;
        runtime.Respond = (_, _) => new() { Json = PlanningBusinessJudgeTests.Report() };
        return (state, runtime);
    }
}
