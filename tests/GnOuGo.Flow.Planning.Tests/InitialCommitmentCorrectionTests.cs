using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class InitialCommitmentCorrectionTests
{
    private const string Location = "/requirements/outcomes/0/commitments/0";

    [Theory]
    [InlineData("operation", "/operation")]
    [InlineData("port", "/result/port")]
    [InlineData("output", "/result/output")]
    public async Task MissingDeclarationsHavePreciseSemanticDiagnostics(string field, string suffix)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        var requirements = runtime.Proposal.Requirements!;
        var original = requirements.Outcomes[0].Commitments![0];
        requirements.Outcomes[0].Commitments![0] = field switch
        {
            "operation" => original with { Operation = " " },
            "port" => original with { Result = original.Result! with { Port = " " } },
            _ => original with { Result = original.Result! with { Output = " " } }
        };
        var finding = Assert.Single(PlanningCommitments.Validate(state, requirements, true));
        Assert.Equal(Location + suffix, finding.Location); Assert.Contains("missing", finding.Message);
    }

    [Theory]
    [InlineData("location", "filePath")]
    [InlineData("résultat},{/路径", "报告路径")]
    public async Task OperationOnlyAndExactUnicodeNamesRemainValidAndCaseSensitive(string port, string output)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture(port, output);
        var requirements = runtime.Proposal.Requirements!;
        Assert.Empty(PlanningCommitments.Validate(state, requirements, true));
        var original = requirements.Outcomes[0].Commitments![0];
        requirements.Outcomes[0].Commitments![0] = original with { Result = null };
        Assert.Empty(PlanningCommitments.Validate(state, requirements, true));
        requirements.Outcomes[0].Commitments![0] = original with { Result = new(port + " ", output) };
        Assert.Equal(Location + "/result/port", Assert.Single(PlanningCommitments.Validate(state, requirements, true)).Location);
        requirements.Outcomes[0].Commitments![0] = original with { Result = new(port, output + " ") };
        Assert.Equal(Location + "/result/output", Assert.Single(PlanningCommitments.Validate(state, requirements, true)).Location);
        requirements.Outcomes[0].Commitments![0] = original with { Result = new(port.ToUpperInvariant(), output) };
        Assert.Equal(Location + "/result/port", Assert.Single(PlanningCommitments.Validate(state, requirements, true)).Location);
    }

    [Fact]
    public async Task DiagnosticNameListsAreBoundedAndNeverContainResultValues()
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        var requirements = runtime.Proposal.Requirements!;
        requirements.Outputs = Enumerable.Range(0, 100).Select(i => new TaskInput { Name = $"field{i}_" + new string('x', 1000), Type = new() { Kind = "string" } }).ToList();
        var finding = Assert.Single(PlanningCommitments.Validate(state, requirements, true));
        Assert.InRange(finding.Message.Length, 1, 1400); Assert.Contains("first 8 shown", finding.Message);
        Assert.DoesNotContain("field8_", finding.Message); Assert.DoesNotContain(new string('x', 100), finding.Message);
    }

    [Theory]
    [InlineData("path", "/result/output", "public-output")]
    [InlineData("description", "/result/output", "public-output")]
    [InlineData("port", "/result/port", "not declared")]
    [InlineData("operation", "/operation", "not unambiguously inspected")]
    [InlineData("duplicate", "", "Duplicate")]
    public async Task InitialCommitmentDefectRegeneratesOnceWithoutAcceptingOrGuessing(string defect, string suffix, string reason)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        state.Request.Options["automatic_correction"] = true;
        runtime.Respond = (request, _) => Response(runtime, request, runtime.Calls.Count == 1 ? defect : null);
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Null(state.Requirements); Assert.Null(state.Plan); Assert.Null(state.Yaml);
        var finding = Assert.Single(state.Diagnostics);
        Assert.Equal("REQUIREMENTS_COMMITMENT_INVALID", finding.Code);
        Assert.Equal((defect == "duplicate" ? Location[..^1] + "1" : Location) + suffix, finding.Location);
        Assert.Contains(reason, finding.Message);
        var issued = JsonSerializer.Serialize(runtime.Calls[0], PlanningJsonContext.Default.LLMRequest);
        var catalog = JsonSerializer.Serialize(state.Catalog, PlanningJsonContext.Default.PlanningCatalog);
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        Assert.Null(state.Request.Baseline); Assert.Equal("regenerate", state.Request.Options["automatic_correction_progress"]!["action"]!.ToString());
        Assert.Contains(finding.Location, runtime.Calls[1].Prompt); Assert.Contains(finding.Code, runtime.Calls[1].Prompt);
        Assert.False(PlanningRepairPatch.Issued(runtime.Calls[1].StructuredOutputSchema!.AsObject()));
        Assert.Equal(issued, JsonSerializer.Serialize(runtime.Calls[0], PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(catalog, JsonSerializer.Serialize(state.Catalog, PlanningJsonContext.Default.PlanningCatalog));
        var (control, baseline) = await CommitmentCoverageTests.Fixture(); baseline = await PlannerFixture.RunAsync(control, baseline);
        Assert.Equal(baseline.Yaml, state.Yaml); Assert.Equal(state.ComputeArtifactHash(), PlannerFixture.Clone(state).ComputeArtifactHash());
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task RepeatedInvalidCommitmentStopsAndHistoricalStopNeverReopens()
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        state.Request.Options["automatic_correction"] = true; state.Request.MaxReplanAttempts = 5;
        runtime.Respond = (request, _) => Response(runtime, request, "path");
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Null(state.Requirements); Assert.Null(state.Plan); Assert.Null(state.Yaml);
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var recovered = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(saved, JsonSerializer.Serialize(recovered, PlanningJsonContext.Default.PlanningSession)); Assert.Equal(2, runtime.Calls.Count);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("plan")]
    [InlineData("baseline")]
    [InlineData("approved")]
    [InlineData("calls")]
    [InlineData("repairs")]
    [InlineData("time")]
    [InlineData("mixed")]
    [InlineData("location")]
    [InlineData("v2")]
    public async Task InitialRouteCannotBypassAuthorityOrAdmission(string blocker)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        state.Request.Options["automatic_correction"] = true; state.ModelCalls = 1;
        state.Diagnostics = [new("REQUIREMENTS_COMMITMENT_INVALID", Location + "/result/output", "Invalid public-output name")];
        if (blocker == "accepted") state.Requirements = runtime.Proposal.Requirements;
        if (blocker == "plan") state.Plan = runtime.Proposal.Plan;
        if (blocker == "baseline") state.Request.Baseline = runtime.Proposal.Plan;
        if (blocker == "approved") { state.ApprovedHash = "historical"; state.Status = PlanningStatus.Approved; }
        if (blocker == "calls") state.Request.MaxModelCalls = 1;
        if (blocker == "repairs") state.Request.MaxReplanAttempts = 0;
        if (blocker == "time") { state.Request.Options["llm_budget"] = new JsonObject { ["max_elapsed_ms"] = 1 }; state.ActiveMilliseconds = 2000; }
        if (blocker == "mixed") state.Diagnostics.Add(new("PERMISSION_DENIED", "/tasks/work", "Denied operation"));
        if (blocker == "location") state.Diagnostics[0] = state.Diagnostics[0] with { Location = "/tasks/work" };
        if (blocker == "v2") state.IntentVersion = 2;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(blocker == "approved" ? PlanningStatus.Approved : PlanningStatus.Stopped, state.Status); Assert.Empty(runtime.Calls); Assert.Null(state.Yaml);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrOmittedPolicyDoesNotEnableCommitmentCorrection(bool explicitDisabled)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        if (explicitDisabled) state.Request.Options["automatic_correction"] = false;
        runtime.Respond = (request, _) => Response(runtime, request, "path");
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Requirements);
        Assert.Null(state.Request.Options["automatic_correction_progress"]);
        // Preserve the pre-policy bounded retry behavior; do not create correction authority.
        Assert.Equal(3, runtime.Calls.Count); Assert.Equal(2, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("compiler")]
    [InlineData("coverage")]
    public async Task CorrectMetadataDoesNotHideTheNextPlanFailureWithoutRemainingAllowance(string defect)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture(); state.Request.Options["automatic_correction"] = true;
        state.Request.MaxReplanAttempts = 1;
        runtime.Respond = (request, _) =>
        {
            if (runtime.Calls.Count == 1) return Response(runtime, request, "path");
            var proposal = JsonSerializer.SerializeToNode(runtime.Proposal, PlanningJsonContext.Default.PlanningProposal)!.Deserialize(PlanningJsonContext.Default.PlanningProposal)!;
            proposal.Plan!.Root.Outputs[0] = new("filePath", defect == "coverage"
                ? CommitmentCoverageTests.Text("invented") : CommitmentCoverageTests.Output("perform", "unknown"));
            return TestRuntime.Response(request, proposal);
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.NotNull(state.Requirements); Assert.Null(state.ApprovedHash);
        Assert.Contains(state.Diagnostics, d => defect == "coverage" ? d.Code == PlanningCommitments.DisconnectedResult : d.Code == "TASK_OUTPUT_UNKNOWN");
    }

    [Fact]
    public async Task CommittedInitialCorrectionUsesOriginalRequestWithoutAnotherAllowance()
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        state.Request.Options["automatic_correction"] = true; state.Request.MaxModelCalls = 2; state.Request.MaxReplanAttempts = 1;
        runtime.Respond = (request, _) => Response(runtime, request, runtime.Calls.Count == 1 ? "path" : null);
        var completed = await PlannerFixture.RunAsync(runtime, state);
        var pending = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall?.Purpose == "replan"));
        var issued = JsonSerializer.Serialize(pending.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var receipt = Response(runtime, pending.PendingCall.Request, null);
        var recovery = new ReceiptOnly(runtime, issued, receipt);
        var recovered = await new HybridWorkflowPlanner().AdvanceAsync(pending, new() { ExpectedRevision = pending.Revision }, recovery, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, recovered.Status); Assert.Equal(completed.Yaml, recovered.Yaml);
        Assert.Equal(2, recovered.ModelCalls); Assert.Equal(1, recovered.ReplanAttempts); Assert.Equal(1, recovery.Reads);
        Assert.Equal(2, runtime.Calls.Count); Assert.Null(recovered.ApprovedHash);
    }

    private sealed class ReceiptOnly(TestRuntime runtime, string issued, LLMResponse receipt) : IPlanningRuntime
    {
        internal int Reads;
        public ICapabilityCatalog Capabilities => runtime.Capabilities;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => throw new InvalidOperationException("No discovery during recovery");
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        { Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)); Assert.Equal(0, Reads++); return Task.FromResult(receipt); }
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => runtime.CheckpointAsync(state, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => runtime.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => runtime.ValidateCatalogAsync(catalog, ct);
    }

    private static LLMResponse Response(TestRuntime runtime, LLMRequest request, string? defect)
    {
        var response = TestRuntime.Response(request, runtime.Proposal);
        var commitments = response.Json!["requirements"]!["outcomes"]![0]!["commitments"]!.AsArray();
        var obligation = commitments[0]!;
        switch (defect)
        {
            case "path": obligation["result"]!["output"] = "workspace/run/report.xlsx"; break;
            case "description": obligation["result"]!["output"] = "write the report then remove the workspace"; break;
            case "output_missing": obligation["result"]!["output"] = " "; break;
            case "port": obligation["result"]!["port"] = "made_up"; break;
            case "port_missing": obligation["result"]!["port"] = " "; break;
            case "operation": obligation["operation"] = "uninspected"; break;
            case "operation_missing": obligation["operation"] = " "; break;
            case "duplicate": commitments.Add(obligation.DeepClone()); break;
        }
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response.Json, request.StructuredOutputSchema!));
        return response;
    }
}
