using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactConvergentJudgeTests
{
    private const string OriginalReviewOpening = "Review the business meaning of this fragment against its exact original requirements and globals: whether its arguments, results and effects perform the selected objective, whether work is unnecessary, and whether the current completion claims omit an obligation.";
    private const string ContractReviewOpening = "Review current contract-defined arguments, results and effects against applicable original obligations; assess unnecessary work and actual completion claims. An operation objective describes its intended contribution, not additional contract behavior or a requirement to perform future work now; transform objectives remain executable instructions.";

    [Theory]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public async Task OperationPurposeDefersLaterWorkWithoutExcusingCurrentBindingDefects(int presentation, bool wrongBinding)
    {
        var state = await State(distinctIdentities: true);
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        const string argumentMeaning = "The exact subject identity to retain, not the identity of a related record.";
        var capability = new PlanningCapability
        {
            Id = "retain_label", Version = "v1", Kind = "tool", StepType = "mcp.call",
            Server = "labels", Method = "retain", EffectKind = "write",
            Description = "Retain the supplied label for later work; this operation does not perform that work.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
                { ["label"] = new JsonObject { ["type"] = "string", ["description"] = argumentMeaning } },
                ["required"] = new JsonArray("label"), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
                { ["value"] = new JsonObject { ["type"] = "string" } }, ["additionalProperties"] = false }
        };
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"], Capabilities = [capability] };
        state.Discovery.Resolved = [capability];
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "operation"; step.OperationIds = [capability.Id];
        step.Inputs = [new() { Kind = "input", Source = "label" }, new() { Kind = "input", Source = "relatedLabel" }];
        step.Objective = "Retain the caller label so later work can compare it with the requested target.";
        var consumed = wrongBinding ? "relatedLabel" : "label";
        var task = new PlanTask { Id = step.Id, Kind = "operation", Objective = step.Objective, Operation = capability.Id,
            Inputs = [new("label", new() { Kind = "input", Source = consumed })] };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"], Tasks = [task] });
        var candidate = state.Construction.Candidate;
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        var compiled = new TaskPlanCompiler().Compile(candidate.Plan, state.Catalog, state.Request);
        Assert.Empty(compiled.Diagnostics);
        var operation = Assert.Single(Assert.Single(compiled.Graph!.Workflows).Steps, node => node.Type == "mcp.call");
        Assert.Equal(task.Objective, operation.Purpose);
        Assert.DoesNotContain(task.Objective, JsonSerializer.Serialize(operation.Input, PlanningJsonContext.Default.PlanningValue), StringComparison.Ordinal);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var draftContext = Context(PlanningConstruction.DraftRequest(state, repair: false));
        Assert.Equal(state.Request.Prompt, draftContext["requirements"]![0]!["original"]!.ToString());
        Assert.Equal(argumentMeaning, draftContext["operations"]![0]!["inputSchema"]!["properties"]!["label"]!["description"]!.ToString());
        Assert.Equal(new[] { "label", "relatedLabel" }, draftContext["interfaces"]!.AsArray()
            .Select(input => input!["value"]!["source"]!.ToString()).Order(StringComparer.Ordinal));
        foreach (var intermediate in new[] { false, true })
        {
            var review = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate).Request;
            AssertCurrentArgumentEvidence(Context(review));
        }
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var context = Context(request);
            Assert.True(context["constructionStage"]!["preflight"]!["scopeOpen"]!.GetValue<bool>());
            Assert.Empty(context["constructionStage"]!["closeRequirementIds"]!.AsArray());
            Assert.False(context["constructionStage"]!["planCompletionClaimed"]!.GetValue<bool>());
            Assert.Equal(task.Objective, context["draft"]!["tasks"]![0]!["objective"]!.ToString());
            AssertCurrentArgumentEvidence(context);
            var findings = new JsonArray();
            if (wrongBinding)
            {
                var finding = Finding();
                finding["implementation"] = "/interfaces/0";
                finding["explanation"] = "The current argument consumes the related record identity instead of the required subject identity. Both values are strings, but they identify different entities.";
                findings.Add(finding);
            }
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Report(findings.DeepClone().AsArray()), request.StructuredOutputSchema!));
            return Response(Report(findings));
        } };
        state = await Advance(state, runtime);
        if (wrongBinding)
        {
            Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
            Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
            Assert.Empty(state.Construction!.Checkpoints);
            Assert.Null(state.Construction.Candidate!.AuthorizedPaths);
            Assert.Equal("/tasks/use_label", state.Diagnostics[0].Location);
            Assert.Equal("relatedLabel", Assert.Single(Assert.Single(state.Construction.Candidate.Extension.Tasks).Inputs).Value.Source);
        }
        else
        {
            Assert.Empty(state.Diagnostics);
            Assert.Single(state.Construction!.Checkpoints);
            Assert.Contains("/root", state.Construction.OpenScopes);
            Assert.NotEqual("closed", Assert.Single(state.Construction.Requirements).Status);
        }
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, state.Construction!.DraftCorrections);
        Assert.Null(state.ApprovedHash);

        void AssertCurrentArgumentEvidence(JsonObject context)
        {
            Assert.Equal(state.Request.Prompt, context["requirements"]![0]!["original"]!.ToString());
            var source = Assert.Single(state.Construction!.Compact!.Sources, item => item.Kind == "requirement");
            Assert.Equal(source.Id, context["requirements"]![0]!["sources"]![0]!["id"]!.ToString());
            Assert.Equal(source.Version, context["requirements"]![0]!["sources"]![0]!["version"]!.ToString());
            var contract = Assert.Single(context["operations"]!.AsArray())!;
            Assert.Equal(capability.Id, contract["id"]!.ToString());
            Assert.Equal(capability.Version, contract["version"]!.ToString());
            var argument = Assert.Single(contract["boundInputs"]!.AsArray())!;
            Assert.Equal("label", argument["name"]!.ToString());
            if (presentation == 6)
            {
                var originalInput = Assert.Single(contract["selectedInputs"]!.AsArray())!;
                Assert.Equal(argumentMeaning, originalInput["schema"]!["description"]!.ToString());
                Assert.EndsWith(originalInput["pointer"]!.ToString(), argument["schemaEvidence"]!.ToString(), StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(argumentMeaning, argument["description"]!.ToString());
                Assert.True(argument["descriptionComplete"]!.GetValue<bool>());
            }
            var imported = Assert.Single(context["interfaces"]!.AsArray())!;
            Assert.Equal(consumed, imported["binding"]!["source"]!.ToString());
            Assert.Equal("string", imported["schema"]!["type"]!.ToString());
            Assert.Equal(PlanningConstruction.TaskNode(task)["inputs"]!.ToJsonString(), context["draft"]!["tasks"]![0]!["inputs"]!.ToJsonString());
        }
    }

    [Theory]
    [InlineData(2, "2d21f8f71813270de53cc16fc3ecc2fe3a166358a037cf7959ba04b95520e091")]
    [InlineData(3, "f7bf1082bb059c73b0f42fd12f72a8adc546d1dfe5d9df9c9769263cb382de8c")]
    [InlineData(4, "ead711e6f0a4eb115b90d593cb2f9129495f83ea32c8b901e83eb9ce1adb9b22")]
    [InlineData(5, "ac37b7a879ef5cad45a1ee0351049153c24cdc43e2333811f76d2cc79aedb3e6")]
    public async Task HistoricalReviewRequestBytesRemainUnchanged(int presentation, string expected)
    {
        var state = await State();
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        var request = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false).Request;
        var actual = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        Assert.True(expected == actual, actual);
        if (presentation < 5) Assert.DoesNotContain(ContractReviewOpening, request.Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingVersionFiveReviewRetainsItsOriginalGuidance(bool originalPurpose)
    {
        var state = await State();
        var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false);
        var originalPrompt = request.Prompt.Replace(
            "Current arguments and effects must satisfy their exact contracts and applicable original obligations, including their combination with accepted work. Check the consumed value's meaning and entity identity, not only type compatibility; open future work or unobserved execution does not defer a present argument mismatch.",
            "Current effects must satisfy applicable original constraints, including constraints on their combination with accepted work, even while scopes or requirements remain open.", StringComparison.Ordinal);
        originalPrompt = originalPrompt.Replace(
            "A contract prerequisite does not establish that the fragment changes the environment or expands permissions; do not assume that prerequisite is satisfied. Request offered exact evidence when needed to finish this business review; if unavailable, return inconclusive. Otherwise retain unknown prerequisite availability and unobserved execution in uncertainty. No report authorizes effects.",
            "Residual uncertainty such as unobserved execution belongs in uncertainty; inability to finish this business review requires inconclusive. No report authorizes effects.", StringComparison.Ordinal);
        Assert.NotEqual(request.Prompt, originalPrompt);
        if (originalPurpose) originalPrompt = originalPrompt.Replace(ContractReviewOpening, OriginalReviewOpening, StringComparison.Ordinal);
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, originalPrompt, request.StructuredOutputSchema!.AsObject());
        var current = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, request.Prompt, request.StructuredOutputSchema.AsObject());
        Assert.True(JsonNode.DeepEquals(current.StructuredOutputSchema, issued.StructuredOutputSchema));
        state.Construction!.PendingReviewContext = context;
        state.Construction.PendingReviewHash = PlanningGraphCompiler.Fingerprint(PlanningConstruction.LocalPurpose + "\n" + state.Construction.Candidate!.Hash + "\n" + context.ToJsonString());
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningConstruction.LocalPurpose, Request = issued };
        state.ModelCalls++;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var runtime = new TestRuntime { Respond = (recovered, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(recovered, PlanningJsonContext.Default.LLMRequest));
            return Response(Report());
        } };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics); Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.PendingCall);
    }

    [Theory]
    [InlineData(5, "all_null")]
    [InlineData(5, "missing_obligation")]
    [InlineData(5, "navigation_implementation")]
    [InlineData(5, "two_actions")]
    [InlineData(6, "all_null")]
    [InlineData(6, "missing_obligation")]
    [InlineData(6, "navigation_implementation")]
    [InlineData(6, "two_actions")]
    public async Task EmittedSchemaRequiresOneActionAndSeparateOriginalAndConcreteCitations(int presentation, string defect)
    {
        var state = await State();
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        var (_, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, false);
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject());
        Assert.True(HybridWorkflowPlanner.ConvergentConstructionReviewIssued(issued.StructuredOutputSchema));
        Assert.Empty(PlanningContractValidation.ValidateSchema(issued.StructuredOutputSchema!.AsObject(), strict: true));
        var valid = Report(new JsonArray(Finding()));
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(valid, issued.StructuredOutputSchema));
        var invalid = valid.DeepClone().AsObject();
        switch (defect)
        {
            case "all_null": invalid = new() { ["action"] = null, ["uncertainty"] = "Unobserved execution." }; break;
            case "missing_obligation": invalid["action"]!["findings"]![0]!.AsObject().Remove("obligation"); break;
            case "navigation_implementation": invalid["action"]!["findings"]![0]!["implementation"] = "/constructionStage"; break;
            case "two_actions": invalid["action"]!["addresses"] = new JsonArray("scope:/root"); break;
        }
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(invalid, issued.StructuredOutputSchema));
    }

    [Fact]
    public async Task GroundedBusinessFindingRemainsAdvisoryCorrectionAndRetainsRawReport()
    {
        var state = await State();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var report = Report(new JsonArray(Finding()));
        state = await Advance(state, new() { Respond = (_, _) => Response(report.DeepClone()) });
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        Assert.Equal("/tasks/use_label", state.Diagnostics[0].Location);
        var review = Assert.Single(state.Construction.Candidate!.Reviews);
        Assert.Equal(report.ToJsonString(), review["rawResponse"]!.ToJsonString());
        Assert.Equal(new[] { "/requirements/0", "/draft/tasks/0" }, review["response"]!["findings"]![0]!["evidence"]!.AsArray().Select(item => item!.ToString()));
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task SoleContradictoryTypedFactCanBeCorrectedOnceWithoutChangingTheCandidateOrErasingAValidFinding()
    {
        var state = await State(); var accepted = PlanningConstruction.Hash(state.Plan);
        var candidate = state.Construction!.Candidate!.Hash;
        var finding = Finding();
        finding["fact"] = new JsonObject { ["pointer"] = "/constructionStage/preflight/scopeOpen", ["equals"] = false };
        var original = Report(new JsonArray(finding.DeepClone()));
        var runtime = new TestRuntime { Respond = (_, _) => Response(original.DeepClone()) };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(candidate, state.Construction!.Candidate!.Hash);
        Assert.Empty(state.Construction.Checkpoints); Assert.Empty(state.Construction.Candidate.Diagnostics);
        Assert.Equal(0, state.ReplanAttempts);
        var retained = Assert.Single(state.Construction.Candidate.Reviews);
        Assert.Equal("ready", retained["reassessment"]!.ToString());
        Assert.Equal(original.ToJsonString(), retained["response"]!.ToJsonString());
        Assert.Equal("CONSTRUCTION_REVIEW_FACT_CONTRADICTION", retained["diagnostics"]![0]!["code"]!.ToString());
        finding["fact"]!["equals"] = true;
        runtime.Respond = (request, _) =>
        {
            Assert.NotNull(Context(request)["reportRepair"]);
            return Response(Report(new JsonArray(finding.DeepClone())));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(candidate, state.Construction!.Candidate!.Hash);
        Assert.Empty(state.Construction.Checkpoints); Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(state.Construction.Candidate.Reviews, review => review["reassessment"] is not null);
        Assert.Equal(finding["explanation"]!.ToString(), state.Construction.Candidate.Reviews[^1]["response"]!["findings"]![0]!["explanation"]!.ToString());
        Assert.Equal(2, state.ModelCalls);
    }

    [Fact]
    public async Task AnyOtherValidGroundedFindingPreventsRepairOfAMixedContradictoryReport()
    {
        var state = await State(); var accepted = PlanningConstruction.Hash(state.Plan);
        var contradictory = Finding();
        contradictory["fact"] = new JsonObject { ["pointer"] = "/constructionStage/preflight/scopeOpen", ["equals"] = false };
        var valid = Finding(); valid["kind"] = "omission";
        var report = Report(new JsonArray(contradictory, valid));
        state = await Advance(state, new() { Respond = (_, _) => Response(report.DeepClone()) });
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints); Assert.Empty(state.Construction.Candidate!.Diagnostics);
        Assert.All(state.Construction.Candidate.Reviews, review => Assert.Null(review["reassessment"]));
        Assert.Equal(report.ToJsonString(), Assert.Single(state.Construction.Candidate.Reviews)["response"]!.ToJsonString());
        Assert.Contains("contradicts", Assert.Single(state.Diagnostics).Message, StringComparison.Ordinal);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactIssuedCoverageCoalescesLocalAndIntermediateOnlyForVersionFive(bool intermediate)
    {
        var state = await State();
        state.Construction!.Candidate!.NeedsIntermediateReview = intermediate;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            Assert.Equal(0, request.RequiredReviewCalls);
            var context = Context(request);
            Assert.Equal(intermediate ? "junction" : "draft", context["reviewScope"]!.ToString());
            Assert.Equal(intermediate ? 2 : 1, context["reviewCoverage"]!.AsArray().Count);
            return Response(Report());
        } };
        state = await Advance(state, runtime);
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(intermediate, checkpoint.NeedsIntermediateReview);
        Assert.Equal(intermediate ? 2 : 1, Assert.Single(checkpoint.Reviews)["reviewCoverage"]!.AsArray().Count);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task AcceptedScopeClosureRemainsApprovableWithItsExactIssuedReviewCoverage(int presentation)
    {
        var state = await CompleteReviewedScope(presentation);
        var checkpoint = state.Construction!.Checkpoints[^1];
        Assert.True(checkpoint.NeedsIntermediateReview);
        Assert.Equal(presentation == 5 ? 1 : 2, checkpoint.Reviews.Count);
        Assert.Equal(presentation == 5 ? 2 : 3, state.ModelCalls);
        var artifact = state.ComputeArtifactHash();
        var exact = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        state = PlannerFixture.Clone(state);
        PlanningArtifactApproval.Verify(state);
        Assert.Equal(exact, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(artifact, state.ComputeArtifactHash());
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("narrow_local")]
    [InlineData("extra_coverage")]
    [InlineData("candidate")]
    [InlineData("candidate_identity")]
    [InlineData("closure_claim")]
    [InlineData("baseline")]
    [InlineData("fingerprint")]
    [InlineData("raw_evidence")]
    [InlineData("raw_findings")]
    [InlineData("raw_uncertainty")]
    [InlineData("normalized_inconclusive")]
    public async Task ApprovalRejectsMissingOrMismatchedCombinedReviewEvidence(string defect)
    {
        var state = await CompleteReviewedScope(5);
        PlanningArtifactApproval.Verify(state);
        var checkpoint = state.Construction!.Checkpoints[^1];
        var review = Assert.Single(checkpoint.Reviews);
        switch (defect)
        {
            case "narrow_local": review["reviewCoverage"] = new JsonArray("local"); break;
            case "extra_coverage": review["reviewCoverage"]!.AsArray().Add("final"); break;
            case "candidate": review["candidateHash"] = new string('0', 64); break;
            case "candidate_identity": review["candidateIdentity"] = checkpoint.PlanHash; break;
            case "closure_claim": review["closedRequirementIds"] = new JsonArray(); break;
            case "baseline": checkpoint.Step!.BaseHash = new string('0', 64); break;
            case "fingerprint": review["fingerprint"] = "missing-context-identity"; break;
            case "raw_evidence": review["rawResponse"]!["action"] = new JsonObject { ["kind"] = "evidenceRequests", ["addresses"] = new JsonArray("scope:/root") }; break;
            case "raw_findings": review["rawResponse"]!["action"]!["findings"]!.AsArray().Add((JsonNode)Finding()); break;
            case "raw_uncertainty": review["rawResponse"]!["uncertainty"] = "A different report."; break;
            case "normalized_inconclusive": review["response"]!["inconclusive"] = "Incomplete review."; break;
        }
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(PlannerFixture.Clone(state)));
    }

    [Fact]
    public async Task HistoricalReviewCannotGainIntermediateCoverageFromNewMetadata()
    {
        var state = await CompleteReviewedScope(4);
        PlanningArtifactApproval.Verify(state);
        var checkpoint = state.Construction!.Checkpoints[^1];
        checkpoint.Reviews.RemoveAll(review => review["purpose"]!.ToString() == PlanningConstruction.IntermediatePurpose);
        var local = Assert.Single(checkpoint.Reviews);
        local["reviewCoverage"] = new JsonArray("local", "intermediate");
        local["candidateIdentity"] = "candidate:v1:" + new string('0', 64);
        local["rawResponse"] = Report();
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(PlannerFixture.Clone(state)));
    }

    [Fact]
    public async Task HostPreparedClosureRetainsScopedIdentityThroughReviewRecoveryAndApproval()
    {
        var state = await CompleteReviewedScope(5, automaticClosure: true);
        PlanningArtifactApproval.Verify(PlannerFixture.Clone(state));
        var checkpoint = state.Construction!.Checkpoints[^1];
        var review = Assert.Single(checkpoint.Reviews);
        Assert.StartsWith("candidate:v1:", review["candidateIdentity"]!.ToString(), StringComparison.Ordinal);
        Assert.Equal(new[] { "local", "intermediate" }, review["reviewCoverage"]!.AsArray().Select(item => item!.ToString()));
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.ApprovedHash);
    }

    private static async Task<PlanningSession> CompleteReviewedScope(int presentation, bool automaticClosure = false)
    {
        var state = await State();
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        if (automaticClosure) state.Construction.Compact.CurrentStep!.CloseRequirementIds = ["work"];
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Contains(purpose, new[] { PlanningConstruction.LocalPurpose, PlanningConstruction.IntermediatePurpose });
            if (HybridWorkflowPlanner.ConvergentConstructionReviewIssued(request.StructuredOutputSchema)) return Response(Report());
            var report = new JsonObject { ["findings"] = new JsonArray(), ["evidenceRequests"] = null,
                ["uncertainty"] = "Runtime execution remains unverified." };
            if (request.StructuredOutputSchema?["properties"]?["inconclusive"] is not null) report["inconclusive"] = null;
            return Response(report);
        } };
        state = await Advance(state, runtime);
        Assert.Single(state.Construction!.Checkpoints);
        if (automaticClosure)
        {
            Assert.Null(state.Construction.CandidateFailureIdentityVersion);
            Assert.Equal("closed", Assert.Single(state.Construction.Requirements).Status);
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.NotNull(state.Construction!.Candidate);
            Assert.True(state.Construction.Candidate.NeedsIntermediateReview);
            Assert.Single(runtime.Calls);
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics));
            Assert.Equal(2, state.Construction!.Checkpoints.Count);
            return state;
        }
        var construction = state.Construction;
        construction.Compact!.CurrentStep = new() { Id = "close_work", Kind = "close", Scope = "/root",
            Objective = "Close the completed use of the caller label.", RequirementIds = ["work"], CloseRequirementIds = ["work"],
            DiscoveryComplete = true, BaseHash = PlanningConstruction.Hash(state.Plan) };
        construction.CandidateFailureIdentityVersion = 1;
        construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"],
            CloseScopes = ["/root"], Complete = true });
        Assert.True(construction.Candidate.NeedsIntermediateReview);
        for (var count = 0; count < 3 && state.Status == PlanningStatus.Generating; count++) state = await Advance(state, runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics));
        Assert.Equal(2, state.Construction!.Checkpoints.Count);
        return state;
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("inconclusive")]
    [InlineData("malformed")]
    public async Task OneReceivedParsedReportRepairKeepsFrozenContextAndCannotCreateAChain(string outcome)
    {
        var state = await State();
        var baseline = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (_, _) => Response(new JsonObject { ["action"] = null, ["uncertainty"] = "Unknown." }) };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        var entry = Assert.Single(state.Construction!.Candidate!.Reviews);
        Assert.Equal("ready", entry["reassessment"]!.ToString());
        Assert.True(entry["reportRepair"]!.GetValue<bool>());
        Assert.NotNull(entry["sourceRequestId"]);
        var frozen = state.Construction.PendingReviewContext!.DeepClone().AsObject();
        runtime.Respond = (request, _) =>
        {
            var context = Context(request);
            Assert.NotNull(context["reportRepair"]);
            context.Remove("reportRepair");
            Assert.True(JsonNode.DeepEquals(frozen, context));
            var choices = request.StructuredOutputSchema!["properties"]!["action"]!["anyOf"]!.AsArray();
            Assert.DoesNotContain(choices, choice => choice!["properties"]!["kind"]!["enum"]![0]!.ToString() == "evidenceRequests");
            return Response(outcome == "complete" ? Report() : outcome == "inconclusive" ? Inconclusive("Review remains incomplete.")
                : new JsonObject { ["action"] = new JsonObject { ["kind"] = "unavailable" }, ["uncertainty"] = "Unknown." });
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(outcome == "complete" ? 1 : 0, state.Construction!.Checkpoints.Count);
        var reviews = state.Construction.Candidate?.Reviews ?? state.Construction.Checkpoints.Single().Reviews;
        Assert.Single(reviews, review => review["reassessment"] is not null);
        if (outcome != "complete") Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("mixed_findings")]
    [InlineData("invalid_json")]
    [InlineData("output_limit")]
    public async Task IneligibleReceivedReportsNeverScheduleRepair(string reason)
    {
        var state = await State();
        if (reason == "budget") state.Request.MaxModelCalls = 1;
        var malformed = new JsonObject { ["action"] = null, ["uncertainty"] = "Unknown." };
        if (reason == "mixed_findings")
        {
            malformed = Report(new JsonArray(Finding()));
            malformed["action"]!["unexpected"] = true;
        }
        state = await Advance(state, new() { Respond = (_, _) => reason switch
        {
            "invalid_json" => new() { Text = "{invalid" },
            "output_limit" => new() { Text = "", CompletionStatus = "output_limit" },
            _ => Response(malformed)
        } });
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.All(state.Construction.Candidate!.Reviews, review => Assert.Null(review["reassessment"]));
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task InterruptedReportRepairResumesTheExactIssuedRequestOnce()
    {
        var state = await State();
        var runtime = new TestRuntime { Respond = (_, _) => Response(new JsonObject { ["action"] = null, ["uncertainty"] = "Unknown." }) };
        state = await Advance(state, runtime);
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        state = PlannerFixture.Clone(runtime.Checkpoints.Last(saved => saved.Status == PlanningStatus.Generating && saved.PendingCall is not null));
        var exact = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        Assert.Equal("issued", Assert.Single(state.Construction!.Candidate!.Reviews)["reassessment"]!.ToString());
        var calls = state.ModelCalls;
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return Response(Report());
        };
        state = await Advance(state, runtime);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
    }

    [Fact]
    public async Task PendingVersionFourRequestRetainsItsBytesAndSeparateReviewRequirement()
    {
        var state = await State();
        state.Construction!.Compact!.PresentationVersion = 4;
        state.Request.Options["construction_presentation_version"] = 4;
        state.Construction.Candidate!.NeedsIntermediateReview = true;
        var runtime = new TestRuntime { Respond = (_, _) => throw new OperationCanceledException() };
        _ = await Advance(state, runtime);
        state = PlannerFixture.Clone(runtime.Checkpoints.Last(saved => saved.Status == PlanningStatus.Generating && saved.PendingCall is not null));
        var exact = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.False(HybridWorkflowPlanner.ConvergentConstructionReviewIssued(request.StructuredOutputSchema));
            return Response(new JsonObject { ["findings"] = new JsonArray(), ["evidenceRequests"] = null, ["inconclusive"] = null, ["uncertainty"] = "Execution unverified." });
        };
        state = await Advance(state, runtime);
        Assert.True(state.Construction!.Candidate!.LocalReviewed);
        Assert.False(state.Construction.Candidate.IntermediateReviewed);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(1, state.ModelCalls);
    }

    [Theory]
    [InlineData("obligation", false)]
    [InlineData("implementation", false)]
    [InlineData("obligation", true)]
    [InlineData("implementation", true)]
    public async Task MissingCitationDomainOffersEvidenceOrInconclusiveButNeverCompletion(string missing, bool retainedFinding)
    {
        var state = await State();
        var context = ResolutionContext();
        if (!retainedFinding) context.Remove("defectProgress");
        if (missing == "obligation") context["requirements"] = new JsonArray();
        else { context["draft"] = new JsonObject(); context.Remove("interfaces"); }
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, "Review\n" + context.ToJsonString(),
            HybridWorkflowPlanner.ConvergentConstructionReviewSchema(context));
        Assert.True(HybridWorkflowPlanner.ConvergentConstructionReviewIssued(issued.StructuredOutputSchema));
        Assert.Empty(PlanningContractValidation.ValidateSchema(issued.StructuredOutputSchema!.AsObject(), strict: true));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(Report(), issued.StructuredOutputSchema));
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Inconclusive("Required evidence is absent."), issued.StructuredOutputSchema));
        Assert.DoesNotContain(issued.StructuredOutputSchema["properties"]!["action"]!["anyOf"]!.AsArray(),
            action => action!["properties"]!["kind"]!["enum"]![0]!.ToString() == "findings");
    }

    [Fact]
    public void RequiredBindingSchemaSelectsOnlyExactAvailableSourcesAndOperationArguments()
    {
        var context = ResolutionContext(); context.Remove("defectProgress");
        var schema = HybridWorkflowPlanner.ConvergentConstructionReviewSchema(context);
        var finding = Finding();
        finding["requiredBinding"] = new JsonObject { ["source"] = "/interfaces/0", ["consumer"] = "/draft/tasks/0/inputs/0" };
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Report(new JsonArray(finding.DeepClone())), schema));
        finding["requiredBinding"]!["source"] = "/requirements/0";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(Report(new JsonArray(finding.DeepClone())), schema));
        finding["requiredBinding"]!["source"] = "/interfaces/0";
        finding["requiredBinding"]!["consumer"] = "/draft/tasks/0/outputs/0";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(Report(new JsonArray(finding)), schema));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("still_present")]
    [InlineData("unchanged")]
    [InlineData("invalidated_by_evidence")]
    [InlineData("disconnected")]
    public async Task PriorFindingsCannotBeSilentlyOmittedOrResolvedByUnrelatedChanges(string defect)
    {
        var state = await State(); var context = ResolutionContext();
        var resolution = Resolution();
        var report = new JsonObject { ["findings"] = new JsonArray(), ["resolutions"] = new JsonArray(resolution) };
        switch (defect)
        {
            case "missing": report.Remove("resolutions"); break;
            case "duplicate":
                var other = context["defectProgress"]![0]!.DeepClone(); other["id"] = "other";
                context["defectProgress"]!.AsArray().Add(other);
                report["resolutions"]!.AsArray().Add(resolution.DeepClone()); break;
            case "still_present": resolution["status"] = "still_present"; break;
            case "unchanged": context["defectProgress"]![0]!["status"] = "unchanged"; break;
            case "invalidated_by_evidence": resolution["status"] = "invalidated_by_evidence"; break;
            case "disconnected": context["defectProgress"]![0]!["requiredBinding"] = new JsonObject { ["dependency"] = "disconnected" }; break;
        }
        Assert.Equal("CONSTRUCTION_REVIEW_RESOLUTION_INVALID",
            Assert.Single(HybridWorkflowPlanner.ConstructionResolutionDiagnostics(state, context, report)).Code);
    }

    [Theory]
    [InlineData("established")]
    [InlineData("unknown")]
    public async Task ChangedEvidenceStillRequiresSemanticAssessmentWithoutInventingATypedFailure(string dependency)
    {
        var state = await State(); var context = ResolutionContext();
        context["defectProgress"]![0]!["requiredBinding"] = new JsonObject { ["dependency"] = dependency };
        var report = new JsonObject { ["findings"] = new JsonArray(), ["resolutions"] = new JsonArray(Resolution()) };
        Assert.Empty(HybridWorkflowPlanner.ConstructionResolutionDiagnostics(state, context, report));
        Assert.False(state.Construction!.Candidate!.LocalReviewed);
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task StillPresentAssessmentRetainsAnExplicitGroundedFinding()
    {
        var state = await State(); var context = ResolutionContext();
        var finding = Finding(); finding["evidence"] = new JsonArray("/requirements/0", "/draft/tasks/0");
        var resolution = Resolution(); resolution["status"] = "still_present";
        var report = new JsonObject { ["findings"] = new JsonArray(finding), ["resolutions"] = new JsonArray(resolution) };
        Assert.Empty(HybridWorkflowPlanner.ConstructionResolutionDiagnostics(state, context, report));
    }

    private static JsonObject Resolution() => new()
    {
        ["id"] = "prior", ["status"] = "resolved", ["obligation"] = "/requirements/0", ["implementation"] = "/draft/tasks/0",
        ["explanation"] = "Assessment of the exact changed implementation against its original obligation."
    };

    private static JsonObject ResolutionContext() => new()
    {
        ["requirements"] = new JsonArray(new JsonObject { ["id"] = "work", ["sources"] = new JsonArray() }),
        ["draft"] = new JsonObject { ["tasks"] = new JsonArray(new JsonObject { ["id"] = "use_label", ["kind"] = "operation",
            ["inputs"] = new JsonArray(new JsonObject { ["name"] = "text", ["value"] = new JsonObject { ["kind"] = "string", ["text"] = "literal" } }) }) },
        ["interfaces"] = new JsonArray(new JsonObject { ["binding"] = new JsonObject { ["kind"] = "input", ["source"] = "label" },
            ["schema"] = new JsonObject { ["type"] = "string" } }),
        ["defectProgress"] = new JsonArray(new JsonObject { ["id"] = "prior", ["status"] = "changed" })
    };

    private static JsonObject Finding() => new()
    {
        ["kind"] = "inconsistency", ["location"] = "/draft/tasks/0", ["obligation"] = "/requirements/0",
        ["implementation"] = "/draft/tasks/0", ["explanation"] = "This concrete use does not fulfill the original business obligation.",
        ["uncertainty"] = "Advisory business interpretation; no execution was observed.", ["fact"] = null, ["requiredBinding"] = null
    };
    private static JsonObject Report(JsonArray? findings = null) => new()
    {
        ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = findings ?? new JsonArray() },
        ["uncertainty"] = "Runtime execution remains unverified."
    };
    private static JsonObject Inconclusive(string reason) => new()
    {
        ["action"] = new JsonObject { ["kind"] = "inconclusive", ["reason"] = reason }, ["uncertainty"] = "Unknown."
    };
    private static LLMResponse Response(JsonNode json) => new() { Json = json, Text = json.ToJsonString() };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);

    private static async Task<PlanningSession> State(bool distinctIdentities = false)
    {
        var original = distinctIdentities
            ? "Accept caller strings label (the subject identity) and relatedLabel (a related record identity). Retain the subject label now; later compare its retained value with the requested target."
            : "Accept the caller string label and use it in the requested work.";
        var state = new PlanningSession { Request = new() { TenantId = "convergent-judge", Prompt = original } };
        PlanningSessionCreation.Capture(state);
        state.IntentVersion = 3; state.Revision = 1; state.Status = PlanningStatus.Generating;
        var construction = state.Construction!;
        construction.Compact!.PresentationVersion = 5; state.Request.Options["construction_presentation_version"] = 5;
        construction.CandidateFailureIdentityVersion = 1;
        PlanningCompactConstruction.InitializeSources(state);
        foreach (var page in construction.Compact.Sources) page.Indexed = true;
        var source = new PlanningConstructionSource { Id = construction.Compact.Sources[0].Id + ":clause:0", Kind = "requirement", Text = original,
            Version = PlanningGraphCompiler.Fingerprint(original), Indexed = true, InterfaceKind = "input" };
        construction.Compact.Sources.Add(source);
        construction.Compact.RequirementSources["work"] = [source.Id];
        construction.Compact.InterfaceExtractions = [new() { Id = "extract", SourceIds = [source.Id], Inputs = true, Complete = true,
            Dispositions = [new() { SourceId = source.Id, Version = source.Version, Kind = "interfaces", Inputs = distinctIdentities ? ["label", "relatedLabel"] : ["label"],
                Reason = "Explicit caller input.", Evidence = new() { [source.Id] = source.Version } }] }];
        state.Requirements = new() { Inputs = [new() { Name = "label", Type = new() { Kind = "string" } }], Outputs = [],
            Outcomes = [new("work", original)] };
        if (distinctIdentities) state.Requirements.Inputs!.Add(new() { Name = "relatedLabel", Type = new() { Kind = "string" } });
        construction.Requirements = [new() { Id = "work" }]; construction.OpenScopes = ["/root"];
        state.Plan = new() { Inputs = state.Requirements.Inputs, Root = new() { Tasks = [new()
        {
            Id = "accepted_label", Kind = "value", Objective = "Retain the caller label.",
            Outputs = [new("value", new() { Kind = "input", Source = "label" })]
        }] } };
        construction.Compact.CurrentStep = new() { Id = "use_label", Kind = "value", Scope = "/root", Objective = "Use the caller label.",
            RequirementIds = ["work"], DiscoveryComplete = true, BaseHash = PlanningConstruction.Hash(state.Plan) };
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"], Tasks = [new()
        {
            Id = "use_label", Kind = "value", Objective = "Use the caller label.",
            Outputs = [new("value", new() { Kind = "output", Source = "accepted_label", Port = "value" })]
        }] });
        return state;
    }
}
