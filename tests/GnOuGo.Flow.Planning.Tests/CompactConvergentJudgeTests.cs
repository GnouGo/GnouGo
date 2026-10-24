using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactConvergentJudgeTests
{
    [Theory]
    [InlineData("all_null")]
    [InlineData("missing_obligation")]
    [InlineData("navigation_implementation")]
    [InlineData("two_actions")]
    public async Task EmittedSchemaRequiresOneActionAndSeparateOriginalAndConcreteCitations(string defect)
    {
        var state = await State();
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

    private static async Task<PlanningSession> State()
    {
        const string original = "Accept the caller string label and use it in the requested work.";
        var state = new PlanningSession { Request = new() { TenantId = "convergent-judge", Prompt = original } };
        PlanningSessionCreation.Capture(state);
        state.IntentVersion = 3; state.Revision = 1; state.Status = PlanningStatus.Generating;
        var construction = state.Construction!;
        construction.Compact!.PresentationVersion = 5; state.Request.Options["construction_presentation_version"] = 5;
        construction.CandidateFailureIdentityVersion = 1;
        PlanningCompactConstruction.InitializeSources(state);
        foreach (var page in construction.Compact.Sources) page.Indexed = true;
        var source = new PlanningConstructionSource { Id = "label-clause", Kind = "clause", Text = original,
            Version = PlanningGraphCompiler.Fingerprint(original), Indexed = true, InterfaceKind = "input" };
        construction.Compact.Sources.Add(source);
        construction.Compact.RequirementSources["work"] = [source.Id];
        construction.Compact.InterfaceExtractions = [new() { Id = "extract", SourceIds = [source.Id], Inputs = true, Complete = true,
            Dispositions = [new() { SourceId = source.Id, Version = source.Version, Kind = "interfaces", Inputs = ["label"],
                Reason = "Explicit caller input.", Evidence = new() { [source.Id] = source.Version } }] }];
        state.Requirements = new() { Inputs = [new() { Name = "label", Type = new() { Kind = "string" } }], Outputs = [],
            Outcomes = [new("work", "Use the caller label.")] };
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
