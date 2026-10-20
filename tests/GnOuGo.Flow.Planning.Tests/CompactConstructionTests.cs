using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactConstructionTests
{
    [Theory]
    [InlineData("漢")]
    [InlineData("🧠")]
    [InlineData("\"\\\n")]
    public async Task OriginalUnicodeAndEscapedSourcesFitEveryCompleteIndexRequest(string character)
    {
        var original = string.Concat(Enumerable.Repeat(character, 1000));
        var state = Retained(original);
        PlanningSessionCreation.Capture(state);
        PlanningCompactConstruction.InitializeSources(state);
        var pageCount = state.Construction!.Compact!.Sources.Count;
        Assert.InRange(pageCount, 2, 40);
        var runtime = new TestRuntime { Respond = Simple };
        var planner = new HybridWorkflowPlanner();
        for (var page = 0; page < pageCount; page++)
        {
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
            Assert.DoesNotContain(state.Diagnostics, d => d.Required);
            var measurement = state.RequestMeasurements![^1];
            Assert.Equal(PlanningCompactConstruction.IntentPurpose, measurement.Phase);
            Assert.InRange(measurement.EstimatedInputTokens, 1, 3000);
        }
        var pages = state.Construction!.Compact!.Sources.Where(s => s.Kind == "page").ToArray();
        Assert.All(pages, page => Assert.True(page.Indexed));
        Assert.Equal(original, string.Concat(pages.Select(page => page.Text)));
        Assert.Equal(pageCount, state.ModelCalls);
    }

    // Preserve the original combined-index protocol corpus as retained sessions.
    // CompactIntentIndexingTests exercises genuine fresh classification/extraction.
    private static PlanningSession Retained(string prompt = "Return exactly Hello.")
    {
        var state = NewSession(prompt);
        PlanningSessionCreation.Capture(state);
        state.Request.Options.Remove("construction_presentation_version");
        state.Construction!.Compact!.PresentationVersion = null;
        state.Construction.Compact.InterfaceExtractions = null;
        state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        state.Construction.Limits.PhasePolicy = null;
        return PlannerFixture.Clone(state);
    }
    private static PlanningSession NewSession(string prompt = "Return exactly Hello.") => new()
    { Request = new() { TenantId = "compact", Prompt = prompt, Options = new JsonObject { ["automatic_correction"] = true } } };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static JsonObject Reviewed() => new() { ["findings"] = new JsonArray(), ["uncertainty"] = "Execution remains unverified.", ["evidenceRequests"] = null };
    private static JsonObject Index(LLMRequest request, string? text = null, bool two = false)
    {
        var source = Context(request)["sources"]![0]!;
        var original = text ?? source["text"]!.ToString();
        var clauses = two ? new JsonArray(new JsonObject { ["text"] = original[..(original.Length / 2)], ["kind"] = "requirement" },
            new JsonObject { ["text"] = original[(original.Length / 2)..], ["kind"] = "requirement" })
            : new JsonArray(new JsonObject { ["text"] = original, ["kind"] = "requirement" });
        if (request.StructuredOutputSchema!["properties"]!["clauses"]!["items"]!["properties"]!["interfaces"] is not null)
        {
            foreach (var clause in clauses) clause!["interfaces"] = "output";
            return new() { ["sourceId"] = source["id"]!.ToString(), ["clauses"] = clauses };
        }
        return new() { ["sourceId"] = source["id"]!.ToString(), ["clauses"] = clauses, ["inputs"] = new JsonArray(),
            ["outputs"] = new JsonArray(new JsonObject { ["name"] = "message", ["type"] = new JsonObject { ["kind"] = "string", ["nullable"] = false }, ["required"] = true }) };
    }
    private static JsonObject Select(LLMRequest request, string kind = "value", string scope = "/root", bool close = true,
        string[]? inputs = null) => new()
    {
        ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.ToString(),
        ["step"] = new JsonObject { ["scope"] = scope, ["kind"] = kind, ["objective"] = "Produce the exact requested greeting.",
            ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray((inputs ?? []).Select(id => (JsonNode)new JsonObject
                { ["kind"] = "output", ["source"] = id, ["port"] = "message" }).ToArray()),
            ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray("The exact greeting."),
            ["closeRequirementIds"] = close ? new JsonArray("req_0") : new JsonArray(), ["outputNames"] = new JsonArray("message") },
        ["clarifications"] = null, ["evidenceRequests"] = null
    };
    private static PlanTask Value(string id = "greet") => new()
    { Id = id, Kind = "value", Objective = "Produce the exact requested greeting.", Outputs = [new("message", new() { Kind = "string", Text = "Hello" })] };
    private static JsonObject Draft(LLMRequest request, PlanningConstructionExtension? extension = null) => new()
    {
        ["baseHash"] = Context(request)["baseHash"]!.ToString(), ["requirements"] = null, ["commitments"] = null,
        ["extension"] = JsonSerializer.SerializeToNode(extension ?? new() { Tasks = [Value()],
            Outputs = [new("message", new() { Kind = "output", Source = "greet", Port = "message" })],
            RequirementIds = ["req_0"], CloseScopes = ["/root"], Complete = true }, PlanningJsonContext.Default.PlanningConstructionExtension),
        ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null
    };
    private static LLMResponse Simple(LLMRequest request, string purpose) => Wire(request, purpose switch
    {
        PlanningCompactConstruction.IntentPurpose => Index(request),
        PlanningCompactConstruction.SelectPurpose => Select(request),
        PlanningConstruction.DraftPurpose or "replan" => Draft(request),
        _ => Reviewed()
    });

    [Fact]
    public async Task RetainedCombinedIndexSessionSelectsAndReviewsOneDraftWithSmallPhaseRequests()
    {
        var runtime = new TestRuntime { Respond = Simple };
        var state = await PlannerFixture.RunAsync(runtime, Retained());
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(4, runtime.Calls.Count);
        Assert.Equal(2, state.Construction!.Version);
        Assert.Equal(64, state.Request.MaxModelCalls);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Equal("closed", Assert.Single(state.Construction.Requirements).Status);
        PlanningArtifactApproval.Verify(state);
        foreach (var call in runtime.Calls)
        {
            var phase = call.StructuredOutputSchema!["title"]!.ToString();
            Assert.InRange(PlanningRequestMeasurements.Measure(call, phase, 8000).EstimatedInputTokens, 1, PlanningConstructionInputPolicy.EffectiveLimit(state.Request, phase));
        }
        Assert.DoesNotContain("operationDirectory", runtime.Calls[^1].Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"request\":", runtime.Calls[^1].Prompt, StringComparison.Ordinal);
        Assert.Null(runtime.Calls[1].StructuredOutputSchema!["$defs"]?["task"]);
    }

    [Fact]
    public async Task SourceOmissionCannotBecomeAcceptedRequirements()
    {
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, Index(request, "Omitted original text")) };
        var state = await PlannerFixture.RunAsync(runtime, Retained());
        Assert.Null(state.Requirements);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.False(state.Construction.Compact!.Sources.Single().Indexed);
        Assert.Contains(state.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
    }

    [Fact]
    public async Task RequirementContributionDoesNotCloseAnotherOriginalRequirement()
    {
        var runtime = new TestRuntime { Respond = (request, purpose) => purpose == PlanningCompactConstruction.IntentPurpose
            ? Wire(request, Index(request, two: true)) : Simple(request, purpose) };
        var state = await PlannerFixture.RunAsync(runtime, Retained());
        Assert.NotEqual(PlanningStatus.FinalReview, state.Status);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(2, state.Construction.Requirements.Count);
        Assert.Null(state.Construction.Candidate);
        Assert.All(state.Construction.Requirements, requirement => Assert.Equal("remaining", requirement.Status));
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Required);
    }

    [Fact]
    public async Task SetupAndCleanupAreExplicitRequirementsWhoseOmissionBlocksCompletion()
    {
        string[] clauses = ["Prepare a local work area. ", "Return exactly Hello. ", "Always remove the work area."];
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            if (purpose == PlanningCompactConstruction.IntentPurpose)
            {
                Assert.Contains("including setup and cleanup, are business requirements", request.Prompt, StringComparison.Ordinal);
                Assert.Contains("reserve global for invariants applying across the workflow", request.Prompt, StringComparison.Ordinal);
                Assert.Contains("do not automatically declare public return interfaces", request.Prompt, StringComparison.Ordinal);
                var response = Index(request);
                response["clauses"] = new JsonArray(clauses.Select(text => (JsonNode)new JsonObject { ["text"] = text, ["kind"] = "requirement" }).ToArray());
                return Wire(request, response);
            }
            if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                var response = Select(request); response["step"]!["requirementIds"] = new JsonArray("req_1");
                response["step"]!["closeRequirementIds"] = new JsonArray("req_1");
                return Wire(request, response);
            }
            if (purpose is PlanningConstruction.DraftPurpose or "replan")
            {
                var response = Draft(request); response["extension"]!["requirementIds"] = new JsonArray("req_1");
                return Wire(request, response);
            }
            return Wire(request, Reviewed());
        } };
        var state = await PlannerFixture.RunAsync(runtime, Retained(string.Concat(clauses)));
        Assert.Equal(clauses, state.Requirements!.Outcomes.Select(r => r.Description));
        Assert.Empty(state.Construction!.Compact!.GlobalSourceIds);
        Assert.All(state.Construction.Requirements.Where(r => r.Id is "req_0" or "req_2"), requirement => Assert.Equal("remaining", requirement.Status));
        Assert.Null(state.Construction.Candidate);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Required);
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("evidence")]
    public async Task AnnotatedJudgePointersStopWithoutCheckpointOrRetry(string annotated)
    {
        var runtime = new TestRuntime { Respond = (request, purpose) => purpose == PlanningConstruction.LocalPurpose
            ? Wire(request, new() { ["findings"] = new JsonArray(new JsonObject
            {
                ["kind"] = "inconsistency", ["location"] = annotated == "location" ? "/draft/tasks/0: candidate task" : "/draft/tasks/0",
                ["evidence"] = new JsonArray(annotated == "evidence" ? "/step/objective: \"Produce the exact requested greeting.\"" : "/requirements/0/original"),
                ["explanation"] = "A reported discrepancy must use exact evidence pointers.", ["uncertainty"] = "Execution remains unverified."
            }), ["uncertainty"] = "This intentionally malformed report cannot authorize acceptance.", ["evidenceRequests"] = null })
            : Simple(request, purpose) };
        var state = await PlannerFixture.RunAsync(runtime, Retained());
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_REVIEW_INCONCLUSIVE");
        Assert.Empty(state.Construction!.Checkpoints); Assert.Null(state.Plan);
        Assert.Equal(4, runtime.Calls.Count); Assert.Equal(0, state.ReplanAttempts);
        Assert.False(state.Construction.Candidate!.LocalReviewed);
        var request = runtime.Calls[^1];
        Assert.Contains("one exact existing JSON Pointer", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Put all reasoning in explanation and uncertainty", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("No appended quotation, value or prose", request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(2048, request.MaxTokens);
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningConstruction.LocalPurpose, 4000).EstimatedInputTokens, 1, 4000);
    }

    [Theory]
    [InlineData("/constructionStage/requirementCompletionClaimed")]
    [InlineData("/scopeGroup/intent/objective")]
    public void JudgeStageAndGroupEvidenceStillRequiresExactNonemptyPointers(string pointer)
    {
        var context = new JsonObject
        {
            ["constructionStage"] = new JsonObject { ["requirementCompletionClaimed"] = false },
            ["scopeGroup"] = new JsonObject { ["intent"] = new JsonObject { ["objective"] = "Declared local purpose.", ["empty"] = new JsonArray() } }
        };
        Assert.True(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, pointer, compact: true));
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, pointer, compact: false));
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, pointer + ": explanatory prose", compact: true));
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/constructionStage/missing", compact: true));
        Assert.False(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(context, "/scopeGroup/intent/empty", compact: true));
    }

    [Fact]
    public async Task PendingStepSelectionResumesExactIssuedRequest()
    {
        var runtime = new TestRuntime { Respond = (request, purpose) => purpose == PlanningCompactConstruction.SelectPurpose
            ? throw new OperationCanceledException() : Simple(request, purpose) };
        var state = Retained(); var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        _ = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall?.Purpose == PlanningCompactConstruction.SelectPurpose));
        saved.Status = PlanningStatus.Generating; // Recover the issued checkpoint preceding the interrupted call.
        var issued = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var count = saved.ModelCalls;
        var recovery = new TestRuntime { Respond = Simple };
        var resumed = await planner.AdvanceAsync(saved, new() { ExpectedRevision = saved.Revision }, recovery, PlannerFixture.Ct);
        Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(recovery.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(count, resumed.ModelCalls);
        Assert.NotNull(resumed.Construction!.Compact!.CurrentStep);
    }

    [Fact]
    public async Task ClosedSourceCoverageIsRequiredByApprovalAndMeasurementsDoNotChangeArtifact()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        var hash = state.ComputeArtifactHash();
        state.RequestMeasurements ??= [];
        state.RequestMeasurements.Add(new() { Phase = "unrelated-measurement", CompleteRequestBytes = 1 });
        Assert.Equal(hash, state.ComputeArtifactHash());
        state.Construction!.Compact!.Sources[0].Indexed = false;
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    [Fact]
    public async Task RollbackDropsOnlyCurrentStepPresentationAndRetainsOriginalSources()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        var sources = JsonSerializer.Serialize(state.Construction!.Compact!.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource);
        state.Construction.Compact.CurrentStep = new() { Id = "stale", BaseHash = PlanningConstruction.Hash(state.Plan), SchemaPointers = ["stale"] };
        state.Construction.Compact.PendingCommitments["req_0"] = [new("old")];
        PlanningConstruction.Rollback(state, "greet", "Rebuild the exact producer while preserving original intent.");
        Assert.Null(state.Construction.Compact.CurrentStep);
        Assert.Empty(state.Construction.Compact.PendingCommitments);
        Assert.Equal(sources, JsonSerializer.Serialize(state.Construction.Compact.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource));
        Assert.Equal("remaining", Assert.Single(state.Construction.Requirements).Status);
    }

    [Fact]
    public async Task RetainedUnrelatedContractDoesNotEnterNextStepOrJudge()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        state.Construction!.Compact!.CurrentStep = new() { Id = "next", BaseHash = PlanningConstruction.Hash(state.Plan), Scope = "/root", Kind = "close", RequirementIds = ["req_0"] };
        state.Discovery.Resolved.Add(new() { Id = "unrelated", Description = new string('z', 60000), Version = "1", StepType = "set" });
        var context = PlanningCompactConstruction.Context(state, review: true).ToJsonString();
        Assert.DoesNotContain(new string('z', 100), context, StringComparison.Ordinal);
        Assert.Empty(JsonNode.Parse(context)!["operations"]!.AsArray());
    }

    [Fact]
    public async Task CallerCannotOptFreshSessionIntoHistoricalVersion()
    {
        var state = NewSession(); state.Construction = new() { Version = 1 }; state.Request.Options["construction_version"] = 1;
        var runtime = new TestRuntime { Respond = Simple };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        Assert.Equal(2, state.Construction!.Version);
        Assert.Equal(PlanningCompactConstruction.IntentPurpose, Assert.Single(runtime.Calls).StructuredOutputSchema!["title"]!.ToString());
    }
    [Theory]
    [InlineData("sequence")]
    [InlineData("conditional")]
    [InlineData("foreach")]
    [InlineData("parallel")]
    public async Task ControlScopesConstructInOrderWithTypedChildImportsAndExplicitJoins(string kind)
    {
        var container = new PlanTask { Id = "scope_work", Kind = kind, Objective = "Produce the requested scoped result." };
        var children = kind is "conditional" or "parallel" ? 2 : 1;
        if (kind == "parallel") container.Branches = [new(), new()];
        else container.Body = new();
        if (kind == "conditional") { container.Condition = new() { Kind = "boolean", Boolean = true }; container.Otherwise = new(); }
        if (kind == "foreach") container.Items = new() { Kind = "array", Items = [new() { Kind = "string", Text = "Hello" }] };
        var steps = new List<(string Kind, string Scope, PlanningConstructionExtension Extension)>
        { (kind, "/root", new() { Tasks = [container], RequirementIds = ["req_0"] }) };
        for (var i = 0; i < children; i++)
        {
            var scope = kind == "parallel" ? "/tasks/scope_work/branches/" + i : "/tasks/scope_work/" + (i == 0 ? "body" : "otherwise");
            var task = Value("child_" + i);
            if (kind == "foreach") task.Outputs[0] = new("message", new() { Kind = "item" });
            var port = kind == "parallel" && i == 1 ? "other" : "message";
            steps.Add(("value", scope, new() { Scope = scope, Tasks = [task], Outputs = [new(port, new() { Kind = "output", Source = task.Id, Port = "message" })],
                CloseScopes = [scope], RequirementIds = ["req_0"] }));
        }
        steps.Add(("close", "/root", new() { Outputs = [new("message", new() { Kind = "output", Source = "scope_work", Port = "message" })],
            CloseScopes = ["/root"], Complete = true, RequirementIds = ["req_0"] }));
        var index = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            JsonObject response;
            if (purpose == PlanningCompactConstruction.IntentPurpose)
            {
                response = Index(request);
                if (kind == "foreach") response["outputs"]![0]!["type"] = new JsonObject
                    { ["kind"] = "array", ["nullable"] = false, ["items"] = new JsonObject { ["kind"] = "string", ["nullable"] = false } };
            }
            else if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                var next = steps[index];
                response = Select(request, next.Kind, next.Scope, close: index == steps.Count - 1,
                    inputs: index == steps.Count - 1 ? ["scope_work"] : []);
                if (kind == "foreach" && index == 1) response["step"]!["inputs"] = new JsonArray(new JsonObject { ["kind"] = "item" });
            }
            else if (purpose == PlanningConstruction.DraftPurpose) response = Draft(request, steps[index++].Extension);
            else response = Reviewed();
            return Wire(request, response);
        } };
        var state = await PlannerFixture.RunAsync(runtime, Retained());
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(steps.Count, state.Construction!.Checkpoints.Count);
        Assert.Contains(state.Construction.Checkpoints.SelectMany(c => c.Reviews), r => r["purpose"]!.ToString() == PlanningConstruction.IntermediatePurpose);
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task ScopedRevisionRetainsExactSourcesAndUsesExistingTypedPatchAuthority()
    {
        var runtime = new TestRuntime { Respond = Simple };
        var state = await PlannerFixture.RunAsync(runtime, Retained());
        var originalSource = state.Construction!.Compact!.Sources[0].Version;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision,
            PreserveRequirements = true, ArtifactHash = state.ComputeArtifactHash(), Text = "Keep exactly Hello and verify this output binding.",
            EditablePaths = ["/root/outputs/message"] }, runtime, PlannerFixture.Ct);
        Assert.Equal("revision", state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.NotNull(state.Construction.Candidate!.AuthorizedPaths);
        runtime.Respond = (request, purpose) => purpose == PlanningCompactConstruction.IntentPurpose
            ? Wire(request, new JsonObject { ["sourceId"] = Context(request)["sources"]![0]!["id"]!.ToString(),
                ["clauses"] = new JsonArray(new JsonObject { ["text"] = Context(request)["sources"]![0]!["text"]!.ToString(), ["kind"] = "global" }),
                ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray() }) : Simple(request, purpose);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(originalSource, state.Construction!.Compact!.Sources[0].Version);
        Assert.Contains(state.Construction.Compact.Sources, source => source.Kind == "revision_page" && source.Indexed);
        Assert.DoesNotContain(state.Diagnostics, d => d.Code == "PLANNING_HOST_FAILURE");
        Assert.NotNull(state.Construction.Compact.CurrentStep);
    }

    [Fact]
    public async Task InvalidLaterInterfaceDoesNotPartiallyAcceptEarlierInput()
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var response = Index(request);
            response["inputs"] = new JsonArray(new JsonObject { ["name"] = "new_input", ["type"] = new JsonObject { ["kind"] = "string", ["nullable"] = false }, ["required"] = true });
            response["outputs"]![0]!["name"] = "";
            return Wire(request, response);
        } };
        var state = await new HybridWorkflowPlanner().AdvanceAsync(Retained(), new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        Assert.Null(state.Requirements);
        Assert.False(state.Construction!.Compact!.Sources.Single().Indexed);
    }

    [Fact]
    public async Task FreshPhaseLimitsCaptureEffectiveValuesBeforeFirstRequest()
    {
        var state = NewSession();
        state.Request.Generation = new("medium", 3500, 8192);
        state.Construction = new() { Limits = new() { InputLimits = new() { Selection = 5000, Draft = 9000, Review = 7000 } } };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = 0 }, new TestRuntime { Respond = Simple }, PlannerFixture.Ct);
        var captured = state.Request.Options["construction_input_limits"]!.Deserialize(PlanningJsonContext.Default.PlanningConstructionInputLimits)!;
        Assert.Equal(3500, captured.Selection);
        Assert.Equal(3500, captured.Draft);
        Assert.Equal(3500, captured.Review);
        Assert.Equal(captured.Draft, state.Construction!.Limits.InputLimits!.Draft);
        var navigation = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose);
        Assert.Equal(3000, navigation.TargetInputTokens);
        Assert.Equal(3500, navigation.MaxInputTokens);
    }

    [Fact]
    public async Task OriginalRequirementPagesKeepTheirIdentityAfterEarlierRequirementsClose()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        for (var i = 1; i < 6; i++)
        {
            state.Requirements!.Outcomes.Add(new("req_" + i, "Independent original requirement " + i));
            state.Construction!.Requirements.Add(new() { Id = "req_" + i, Status = "remaining" });
        }
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, "page:requirements:3", out var evidence));
        var selected = PlanningCompactConstruction.SelectionContext(state)["requirements"]!.AsArray();
        Assert.Equal("req_3", evidence!["items"]![0]!["id"]!.ToString());
        Assert.Equal("req_1", selected[0]!["id"]!.ToString());
        Assert.Single(selected);
    }

    [Fact]
    public async Task SupplementalRequirementPagePreservesEarlierFocusAndPresentsEachExactClauseOnce()
    {
        string[] clauses = ["Read every supplied label. ", "Normalize each label consistently. ", "Return the ordered labels. ", "Preserve repeated entries."];
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var response = Index(request);
            response["clauses"] = new JsonArray(clauses.Select(text => (JsonNode)new JsonObject { ["text"] = text, ["kind"] = "requirement" }).ToArray());
            return Wire(request, response);
        } };
        var state = await new HybridWorkflowPlanner().AdvanceAsync(Retained(string.Concat(clauses)), new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        var sources = JsonSerializer.Serialize(state.Construction!.Compact!.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource);
        PlanningConstruction.LoadEvidence(state, ["page:requirements:3"]);
        var context = PlanningCompactConstruction.SelectionContext(state);
        Assert.Equal("req_0", Assert.Single(context["requirements"]!.AsArray())!["id"]!.ToString());
        Assert.Equal("req_3", context["evidence"]!["page:requirements:3"]!["items"]![0]!["id"]!.ToString());
        Assert.Equal("request:0:clause:0", context["requirements"]![0]!["sources"]![0]!["id"]!.ToString());
        Assert.Equal(0, state.Construction.Compact.RequirementOffset);
        Assert.False(state.Construction.Compact.RequirementPageRequested);
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectInstructions, context,
            PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, 3000).EstimatedInputTokens, 1, 3000);
        PlanningConstruction.LoadEvidence(state, ["page:requirements:0"]);
        var repeated = PlanningCompactConstruction.SelectionContext(state);
        Assert.Null(repeated["requirements"]);
        Assert.Single(repeated["evidence"]!["page:requirements:0"]!["items"]!.AsArray());
        Assert.Equal("page:requirements:1", repeated["evidence"]!["page:requirements:0"]!["next"]!.ToString());
        Assert.Equal(sources, JsonSerializer.Serialize(state.Construction.Compact.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource));
    }

    [Fact]
    public async Task SelectionKeepsOneOriginalAnchorAndGlobalsWhilePagingLongRequirements()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        state.Requirements!.Outcomes.Clear(); state.Construction!.Requirements.Clear();
        var compact = state.Construction.Compact!;
        compact.Sources.Clear(); compact.RequirementSources.Clear(); compact.GlobalSourceIds.Clear();
        for (var i = 0; i < 12; i++)
        {
            var id = "req_" + i; var sourceId = "original:0:clause:" + i;
            var original = "Preserve the exact business obligation " + i + ": " + new string('x', 160);
            state.Requirements.Outcomes.Add(new(id, original));
            state.Construction.Requirements.Add(new() { Id = id });
            compact.Sources.Add(new() { Id = sourceId, Text = original, Version = PlanningGraphCompiler.Fingerprint(original), Kind = "requirement", Indexed = true });
            compact.RequirementSources[id] = [sourceId];
        }
        for (var i = 0; i < 5; i++)
        {
            var id = "global:0:clause:" + i; var text = "Apply this original invariant throughout the workflow " + i + ": " + new string('y', 80);
            compact.Sources.Add(new() { Id = id, Text = text, Version = PlanningGraphCompiler.Fingerprint(text), Kind = "global", Indexed = true });
            compact.GlobalSourceIds.Add(id);
        }
        var originals = JsonSerializer.Serialize(compact.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource);
        for (var offset = 1; offset < state.Requirements.Outcomes.Count; offset++)
        {
            var address = "page:requirements:" + offset;
            PlanningConstruction.LoadEvidence(state, [address]);
            var context = PlanningCompactConstruction.SelectionContext(state);
            Assert.Equal("req_0", Assert.Single(context["requirements"]!.AsArray())!["id"]!.ToString());
            var page = context["evidence"]![address]!;
            Assert.Equal("req_" + offset, Assert.Single(page["items"]!.AsArray())!["id"]!.ToString());
            Assert.Equal(offset + 1 < state.Requirements.Outcomes.Count ? "page:requirements:" + (offset + 1) : null, page["next"]?.ToString());
            Assert.Equal(5, context["globalConstraints"]!.AsArray().Count);
            var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectInstructions, context,
                PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
            var complete = PlanningGenerationPolicy.Apply(new LLMRequest
            {
                Provider = "OpenAi", Model = "retained-model-revision", Prompt = request.Prompt,
                StructuredOutputSchema = request.StructuredOutputSchema, StructuredOutputStrict = true,
                UseBackgroundMode = true, MaxTokens = state.Construction.Limits.DraftOutputTokens,
                ClientRequestId = new string('s', 64) + ":12:" + new string('h', 64)
            }, state.Request.Generation);
            Assert.InRange(PlanningRequestMeasurements.Measure(complete, PlanningCompactConstruction.SelectPurpose, 3000).EstimatedInputTokens, 1, 3000);
        }
        Assert.Equal(originals, JsonSerializer.Serialize(compact.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource));
        Assert.All(state.Construction.Requirements, requirement => Assert.NotEqual("closed", requirement.Status));
    }

    [Theory]
    [InlineData("1", "page:requirements:2")]
    [InlineData("1,2", "page:requirements:3")]
    [InlineData("1,3", "page:requirements:4")]
    [InlineData("2,3", "page:requirements:4")]
    [InlineData("1,2,3,4", null)]
    public async Task FreshSelectionContinuesAfterFurthestRequirementPageWithoutClosingSkippedRequirements(string supplied, string? next)
    {
        var state = await IndexedNavigationRequirements();
        var originals = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var sources = JsonSerializer.Serialize(state.Construction!.Compact!.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource);
        var planHash = PlanningConstruction.Hash(state.Plan);
        var addresses = supplied.Split(',').Select(offset => "page:requirements:" + offset).ToList();
        PlanningConstruction.LoadEvidence(state, addresses);
        var beforeContext = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.SelectionContext(state);
        Assert.Equal(next, context["nextRequirements"]?.ToString());
        Assert.Equal("page:requirements:0", context["requirementsDirectory"]!.ToString());
        Assert.Equal("req_0", Assert.Single(context["requirements"]!.AsArray())!["id"]!.ToString());
        Assert.Null(context["originalRequirementCount"]);
        Assert.Equal(5, state.Requirements!.Outcomes.Count);
        Assert.Equal(5, context["requirementCount"]!.GetValue<int>());
        Assert.Single(context["globalConstraints"]!.AsArray());
        foreach (var address in addresses)
            Assert.True(JsonNode.DeepEquals(state.Construction.Evidence![address], context["evidence"]![address]));
        Assert.Equal(beforeContext, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(originals, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(sources, JsonSerializer.Serialize(state.Construction.Compact.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource));
        Assert.Equal(planHash, PlanningConstruction.Hash(state.Plan));
        Assert.All(state.Construction.Requirements, requirement => Assert.Equal("remaining", requirement.Status));
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state), context,
            PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
        request = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.SelectPurpose, request.Prompt,
            request.StructuredOutputSchema!.AsObject(), state.Construction.Limits.DraftOutputTokens);
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose).MaxInputTokens;
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, limit).EstimatedInputTokens, 1, limit);
    }

    [Fact]
    public async Task RetainedSelectionKeepsHistoricalRequirementContinuation()
    {
        var state = await IndexedNavigationRequirements();
        state.Construction!.Compact!.PresentationVersion = null;
        state.Request.Options.Remove("construction_presentation_version");
        PlanningConstruction.LoadEvidence(state, ["page:requirements:1"]);
        Assert.Equal("page:requirements:1", PlanningCompactConstruction.SelectionContext(state)["nextRequirements"]!.ToString());
        Assert.False(state.Construction.Compact.RequirementPageRequested);
        Assert.Equal(0, state.Construction.Compact.RequirementOffset);
    }

    [Fact]
    public async Task FreshRequirementNavigationContinuesAcrossReplacementBatchesAndRestart()
    {
        var state = await IndexedNavigationRequirements();
        for (var offset = 1; offset < state.Requirements!.Outcomes.Count; offset++)
        {
            var context = PlanningCompactConstruction.SelectionContext(state);
            var address = "page:requirements:" + offset;
            Assert.Equal(address, context["nextRequirements"]!.ToString());
            Assert.Equal("req_0", Assert.Single(context["requirements"]!.AsArray())!["id"]!.ToString());
            PlanningConstruction.LoadEvidence(state, [address]);
            state = PlannerFixture.Clone(state);
            Assert.Equal(offset, state.Construction!.Compact!.RequirementOffset);
            Assert.True(state.Construction.Compact.RequirementPageRequested);
            Assert.Equal(address, Assert.Single(state.Construction.Evidence!).Key);
        }
        Assert.Null(PlanningCompactConstruction.SelectionContext(state)["nextRequirements"]);
        PlanningConstruction.LoadEvidence(state, ["page:requirements:0"]);
        Assert.Equal(4, state.Construction!.Compact!.RequirementOffset);
        Assert.Null(PlanningCompactConstruction.SelectionContext(state)["nextRequirements"]);
        Assert.All(state.Construction.Requirements, requirement => Assert.Equal("remaining", requirement.Status));
    }

    [Fact]
    public async Task FailedRequirementEvidenceBatchCannotAdvanceCapturedNavigation()
    {
        var state = await IndexedNavigationRequirements();
        PlanningConstruction.LoadEvidence(state, ["page:requirements:1"]);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var failure = Assert.Throws<PlanningResponseException>(() =>
            PlanningConstruction.LoadEvidence(state, ["page:requirements:3", "page:requirements:99"]));
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal("page:requirements:2", PlanningCompactConstruction.SelectionContext(state)["nextRequirements"]!.ToString());
    }

    [Fact]
    public async Task FreshRequirementNavigationStillRejectsRepeatedEvidenceWithoutProgress()
    {
        var state = await IndexedNavigationRequirements();
        PlanningConstruction.LoadEvidence(state, ["page:requirements:1"]);
        Assert.Equal("page:requirements:2", PlanningCompactConstruction.SelectionContext(state)["nextRequirements"]!.ToString());
        var evidence = state.Construction!.Evidence!.ToJsonString();
        var requests = state.Construction.EvidenceRequests.ToArray();
        var failure = Assert.Throws<PlanningResponseException>(() =>
            PlanningConstruction.LoadEvidence(state, ["page:requirements:1"]));
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(evidence, state.Construction.Evidence.ToJsonString());
        Assert.Equal(requests, state.Construction.EvidenceRequests);
        Assert.Empty(state.Construction.Checkpoints);
    }

    private static async Task<PlanningSession> IndexedNavigationRequirements()
    {
        string[] clauses = ["Read each original record. ", "Keep the exact names. ", "Preserve their order. ",
            "Return the retained values. ", "Close acquired resources. ", "Never change the supplied identities."];
        // This corpus tests navigation across distinct original source pages.
        // Same-page clause cohesion has separate fresh-policy coverage.
        clauses = clauses.Select(text => text.TrimEnd().TrimEnd('.') + ": " + new string('x', 540) + ". ").ToArray();
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var source = Context(request)["sources"]![0]!;
            var text = source["text"]!.ToString();
            Assert.Contains(text, clauses);
            return Wire(request, new JsonObject
            {
                ["sourceId"] = source["id"]!.DeepClone(),
                ["clauses"] = new JsonArray(new JsonObject
                { ["text"] = text, ["kind"] = text == clauses[^1] ? "global" : "requirement", ["interfaces"] = "none" })
            });
        } };
        var state = NewSession(string.Concat(clauses));
        for (var index = 0; index < clauses.Length; index++)
            state = await new HybridWorkflowPlanner().AdvanceAsync(state,
                new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, state.Construction!.Compact!.PresentationVersion);
        return state;
    }

    [Fact]
    public async Task RequestedRequirementPagesDisplaceOnlyRepeatedNavigationProse()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        var compact = state.Construction!.Compact!;
        compact.PresentationVersion = 2;
        state.Request.Options["construction_presentation_version"] = 2;
        compact.InterfaceExtractions = [];
        state.Requirements!.Outcomes.Clear(); state.Construction.Requirements.Clear();
        compact.Sources.Clear(); compact.RequirementSources.Clear(); compact.GlobalSourceIds.Clear();
        var originals = new[]
        {
            "Read the supplied records, visit their observed references in order, preserve their complete original descriptions and measured values, then write the results to the requested local artifact. " + new string('a', 180),
            "Preserve the first three records and the exact supplied search term. " + new string('b', 100),
            "Save the result under workflows/isolated-evaluation/original-request/result.xlsx; close acquired resources even on failure. " + new string('c', 100)
        };
        for (var i = 0; i < originals.Length; i++)
        {
            var id = "req_" + i; var source = "source:clause:" + i;
            state.Requirements.Outcomes.Add(new(id, originals[i]));
            state.Construction.Requirements.Add(new() { Id = id });
            compact.Sources.Add(new() { Id = source, Text = originals[i], Version = PlanningGraphCompiler.Fingerprint(originals[i]), Kind = "requirement", Indexed = true });
            compact.RequirementSources[id] = [source];
        }
        var producer = state.Plan!.Root.Tasks[0];
        producer.Objective = "Preserve the supplied internal value. " + new string('x', 150);
        var owner = new PlanTask { Id = "work", Kind = "sequence", Objective = "Collect and preserve original records. " + new string('y', 150),
            Body = new() { Tasks = [producer] } };
        state.Plan.Root.Tasks = [owner];
        state.Construction.OpenScopes = ["/root", "/tasks/work/body"];
        var accepted = PlanningConstruction.Hash(state.Plan);
        var sourceBefore = JsonSerializer.Serialize(compact.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource);
        PlanningConstruction.LoadEvidence(state, ["page:requirements:1", "page:requirements:2"]);
        var evidence = state.Construction.Evidence!.ToJsonString();
        var context = PlanningCompactConstruction.SelectionContext(state);
        Assert.Equal(originals[0], Assert.Single(context["requirements"]!.AsArray())!["original"]!.ToString());
        Assert.Equal(evidence, context["evidence"]!.ToJsonString());
        Assert.All(context["scopes"]!.AsArray().OfType<JsonObject>(), item => Assert.False(item.ContainsKey("objective")));
        Assert.All(context["interfaces"]!.AsArray().OfType<JsonObject>(), item => Assert.False(item.ContainsKey("objective")));
        var scope = context["scopes"]!.AsArray().Single(item => item!["owner"]?.ToString() == owner.Id)!;
        var port = context["interfaces"]!.AsArray().Single(item => item!["task"]?.ToString() == producer.Id)!;
        Assert.NotEmpty(port["ports"]!.AsArray());
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectInstructions, context,
            PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
        request = PlanningGenerationPolicy.Apply(new LLMRequest
        {
            Provider = "OpenAi", Model = "retained-model-revision", Prompt = request.Prompt,
            StructuredOutputSchema = request.StructuredOutputSchema, StructuredOutputStrict = true, UseBackgroundMode = true,
            MaxTokens = state.Construction.Limits.DraftOutputTokens, ClientRequestId = new string('s', 64) + ":14:" + new string('h', 64)
        }, state.Request.Generation);
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, 3000).EstimatedInputTokens, 1, 3000);
        var objectiveAddress = scope["objectiveEvidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [objectiveAddress, port["evidence"]!.ToString()]);
        Assert.Equal(owner.Objective, state.Construction.Evidence![objectiveAddress]!["text"]!.ToString());
        Assert.Equal(producer.Objective, state.Construction.Evidence[port["evidence"]!.ToString()]!["objective"]!.ToString());
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(sourceBefore, JsonSerializer.Serialize(compact.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource));

        compact.PresentationVersion = null;
        var historical = PlanningCompactConstruction.SelectionContext(state);
        Assert.Equal(owner.Objective, historical["scopes"]!.AsArray().Single(item => item!["owner"]?.ToString() == owner.Id)!["objective"]!.ToString());
    }

    [Fact]
    public async Task LongTaskObjectivesAndNumerousPortsRemainBoundedAndExactlyRetrievable()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        var task = state.Plan!.Root.Tasks[0];
        task.Objective = new string('x', 5000) + string.Concat(Enumerable.Repeat("🧠", 200));
        task.Outputs = Enumerable.Range(0, 500).Select(i => new TaskOutput("port_" + i, new() { Kind = "string", Text = "Exact value" })).ToList();
        var original = JsonSerializer.Serialize(task, PlanningJsonContext.Default.PlanTask);
        var entry = PlanningCompactConstruction.InterfaceDirectory(state, null, 0).Single(node => node?["task"]?.ToString() == task.Id)!;
        Assert.False(entry["objectiveComplete"]!.GetValue<bool>());
        Assert.InRange(entry["objective"]!.ToString().Length, 1, 192);
        Assert.Equal(8, entry["ports"]!.AsArray().Count); Assert.Equal(500, entry["portCount"]!.GetValue<int>());
        var reconstructed = ""; string? address = entry["objectiveEvidence"]!.ToString();
        while (address is not null)
        {
            Assert.True(PlanningCompactConstruction.LoadEvidence(state, address, out var page));
            reconstructed += page!["text"]!.ToString(); address = page!["next"]?.ToString();
        }
        Assert.Equal(task.Objective, reconstructed);
        var ports = entry["ports"]!.AsArray().Select(port => port!.ToString()).ToList(); address = entry["nextPorts"]!.ToString();
        while (address is not null)
        {
            Assert.True(PlanningCompactConstruction.LoadEvidence(state, address, out var page));
            ports.AddRange(page!["ports"]!.AsArray().Select(port => port!.ToString())); address = page!["next"]?.ToString();
        }
        Assert.Equal(task.Outputs.Select(output => output.Name), ports);
        Assert.Equal(original, JsonSerializer.Serialize(task, PlanningJsonContext.Default.PlanTask));
        var previous = task.Outputs[8];
        task.Outputs[8] = new("changed_port", previous.Value);
        var stale = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [entry["nextPorts"]!.ToString()]));
        Assert.Contains(stale.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
        task.Outputs[8] = previous;
        for (var i = 1; i < 8; i++) state.Plan.Root.Tasks.Add(Value("independent_" + i));
        var firstPage = PlanningCompactConstruction.InterfaceDirectory(state, null, 0).ToJsonString();
        for (var i = 8; i < 500; i++) state.Plan.Root.Tasks.Add(Value("independent_" + i));
        Assert.Equal(firstPage, PlanningCompactConstruction.InterfaceDirectory(state, null, 0).ToJsonString());
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectInstructions,
            PlanningCompactConstruction.SelectionContext(state), PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
        var complete = PlanningGenerationPolicy.Apply(new LLMRequest
        {
            Provider = state.Request.Options["generator"]?["provider"]?.GetValue<string>(),
            Model = state.Request.Options["generator"]?["model"]?.GetValue<string>() ?? "",
            Prompt = request.Prompt, StructuredOutputSchema = request.StructuredOutputSchema, StructuredOutputStrict = true,
            UseBackgroundMode = true, MaxTokens = state.Construction!.Limits.DraftOutputTokens
        }, state.Request.Generation);
        var hash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(complete, PlanningJsonContext.Default.LLMRequest));
        complete.ClientRequestId = state.Request.SessionId + ":" + (state.ModelCalls + 1) + ":" + hash;
        Assert.Equal(4, PlanningCompactConstruction.InterfaceDirectory(state, null, 0).Count);
        Assert.Equal("page:interfaces:4", PlanningCompactConstruction.SelectionContext(state)["nextInterfaces"]!.ToString());
        Assert.InRange(PlanningRequestMeasurements.Measure(complete, PlanningCompactConstruction.SelectPurpose, 3000).EstimatedInputTokens, 1, 3000);
    }

    [Fact]
    public async Task ClosedCallPortsAndRequestedNavigationPagesDoNotDisappearOrRepeat()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        state.Plan!.Groups.Add(new() { Id = "reusable", Body = new() { Outputs = [new("returned", new() { Kind = "string", Text = "Hello" })] } });
        state.Plan.Root.Tasks.Add(new() { Id = "invoke", Kind = "call", Group = "reusable", Objective = "Return the reusable result." });
        var entry = PlanningCompactConstruction.InterfaceDirectory(state, null, 0).Single(node => node?["task"]?.ToString() == "invoke")!;
        Assert.Equal("returned", Assert.Single(entry["ports"]!.AsArray())!.ToString());
        PlanningConstruction.LoadEvidence(state, ["page:interfaces:0", "page:outputs:0"]);
        var context = PlanningCompactConstruction.SelectionContext(state);
        Assert.Null(context["interfaces"]); Assert.Null(context["publicOutputs"]); Assert.Null(context["frontier"]);
        Assert.Contains(context["evidence"]!["page:interfaces:0"]!.AsArray(), row => row?["task"]?.ToString() == "invoke");
        Assert.Equal("message", context["evidence"]!["page:outputs:0"]!["items"]![0]!["name"]!.ToString());
    }

    [Fact]
    public async Task InternalGroupResultsCannotBeMistakenForDeclaredPublicOutputs()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        state.Construction!.OpenScopes = ["/root"];
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectInstructions,
            PlanningCompactConstruction.SelectionContext(state), PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
        var response = Select(request, "group", close: false);
        response["step"]!["groupId"] = null;
        response["step"]!["outputNames"] = new JsonArray("internalGreeting");
        response["step"]!["expectedOutputs"] = new JsonArray("An internal reusable greeting interface.");
        Assert.Equal("message", Context(request)["publicOutputs"]!["items"]![0]!["name"]!.ToString());
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!.AsObject()));
        var step = response["step"]!.Deserialize(PlanningJsonContext.Default.PlanningConstructionStep)!;
        var error = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, step));
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("CONSTRUCTION_STEP_OUTPUT_INVALID", diagnostic.Code);
        Assert.Equal("/step/outputNames", diagnostic.Location);
        Assert.Contains("message", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("outputNames:[]", diagnostic.Message, StringComparison.Ordinal);
        response["step"]!["outputNames"] = new JsonArray();
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema.AsObject()));
        step.OutputNames.Clear();
        PlanningCompactConstruction.ValidateSelection(state, step);
        Assert.Equal("An internal reusable greeting interface.", Assert.Single(step.ExpectedOutputs));
    }

    [Fact]
    public async Task PublicOutputDirectoryAndSchemaPageWithoutResendingTheCatalog()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        state.Requirements!.Outputs = Enumerable.Range(0, 500).Select(i => new TaskInput
            { Name = "result_" + i, Type = new() { Kind = "string" } }).ToList();
        var context = PlanningCompactConstruction.SelectionContext(state);
        Assert.Equal(8, context["publicOutputs"]!["items"]!.AsArray().Count);
        Assert.Equal("page:outputs:8", context["publicOutputs"]!["next"]!.ToString());
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        Assert.DoesNotContain("result_8\"", schema.ToJsonString(), StringComparison.Ordinal);
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectInstructions, context, schema, PlanningCompactConstruction.SelectPurpose);
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, 3000).EstimatedInputTokens, 1, 3000);
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, context["publicOutputs"]!["next"]!.ToString(), out var page));
        Assert.Equal("result_8", page!["items"]![0]!["name"]!.ToString());
        Assert.Contains("result_8\"", PlanningCompactConstruction.SelectionSchema(state).ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("result_0\"", PlanningCompactConstruction.SelectionSchema(state).ToJsonString(), StringComparison.Ordinal);
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, "output:result_8", out var exact));
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(state.Requirements.Outputs[8], PlanningJsonContext.Default.TaskInput), exact));
        state.Requirements.Outputs.Clear(); state.Construction!.Compact!.OutputOffset = 0;
        var response = Select(request); response["step"]!["groupId"] = null;
        response["step"]!["outputNames"] = new JsonArray();
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, PlanningCompactConstruction.SelectionSchema(state)));
        response["step"]!["outputNames"] = new JsonArray("undeclared");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, PlanningCompactConstruction.SelectionSchema(state)));
    }

    [Fact]
    public async Task SelectionIssuesStableRequirementAndMixedInterfaceContinuationAddresses()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        for (var i = 1; i < 8; i++)
        {
            state.Requirements!.Outcomes.Add(new("req_" + i, "Original obligation " + i));
            state.Construction!.Requirements.Add(new() { Id = "req_" + i });
        }
        state.Plan!.Inputs = Enumerable.Range(0, 10).Select(i => new TaskInput { Name = "input_" + i, Type = new() { Kind = "string" } }).ToList();
        state.Plan.Groups = Enumerable.Range(0, 10).Select(i => new TaskGroup { Id = "group_" + i }).ToList();
        state.Plan.Root.Tasks.AddRange(Enumerable.Range(0, 10).Select(i => new PlanTask { Id = "scope_" + i, Kind = "sequence", Body = new() }));
        state.Construction!.OpenScopes = ["/root", .. Enumerable.Range(0, 10).Select(i => "/tasks/scope_" + i + "/body")];
        var context = PlanningCompactConstruction.SelectionContext(state);
        Assert.Equal("req_1", context["requirements"]![0]!["id"]!.ToString());
        Assert.Equal("page:requirements:2", context["nextRequirements"]!.ToString());
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, context["nextRequirements"]!.ToString(), out var requirements));
        Assert.Equal("req_2", requirements!["items"]![0]!["id"]!.ToString());
        Assert.Equal("page:interfaces:4", context["nextInterfaces"]!.ToString());
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, context["nextInterfaces"]!.ToString(), out var interfaces));
        Assert.Contains(interfaces!.AsArray(), item => item?["group"]?.ToString() == "group_4");
        Assert.Equal("page:interfaces:8", PlanningCompactConstruction.SelectionContext(state)["nextInterfaces"]!.ToString());
        Assert.Equal("page:scopes:8", context["nextScopes"]!.ToString());
        PlanningConstruction.LoadEvidence(state, [context["nextScopes"]!.ToString()]);
        Assert.Equal("/tasks/scope_7/body", state.Construction.Evidence!["page:scopes:8"]![0]!.ToString());
        var later = PlanningCompactConstruction.SelectionContext(state);
        Assert.Null(later["frontier"]);
        Assert.Equal("/tasks/scope_7/body", later["scopes"]![0]!["path"]!.ToString());
        Assert.Null(later["nextScopes"]);
    }

    [Fact]
    public async Task SourcePagesRetainEveryOriginalCharacterAcrossBoundaries()
    {
        var prompt = new string('a', 999) + "🦊" + new string('b', 1000) + " Keep exactly Hello.";
        var state = Retained(prompt);
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, Index(request)) };
        var planner = new HybridWorkflowPlanner();
        for (var i = 0; i < 3; i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(3, runtime.Calls.Count);
        Assert.All(state.Construction!.Compact!.Sources, source => Assert.True(source.Indexed));
        Assert.Equal(prompt, string.Concat(state.Requirements!.Outcomes.Select(r => r.Description)));
        Assert.All(runtime.Calls, call => Assert.InRange(PlanningRequestMeasurements.Measure(call, PlanningCompactConstruction.IntentPurpose, 3000).EstimatedInputTokens, 1, 3000));
    }

    [Fact]
    public async Task ContributionCheckpointCannotForgeReviewedRequirementClosure()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        var checkpoint = Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(["req_0"], checkpoint.ClosedRequirementIds);
        Assert.All(checkpoint.Reviews, review => Assert.Equal("req_0", review["closedRequirementIds"]![0]!.ToString()));
        checkpoint.ClosedRequirementIds = [];
        foreach (var review in checkpoint.Reviews) review["closedRequirementIds"] = new JsonArray();
        Assert.Equal("closed", Assert.Single(state.Construction.Requirements).Status);
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    [Fact]
    public async Task ClosingRootAfterBusinessClosurePreservesClosedRequirementAndReviewedReceipt()
    {
        var step = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            if (purpose == PlanningCompactConstruction.IntentPurpose) return Wire(request, Index(request));
            if (purpose == PlanningCompactConstruction.SelectPurpose) return Wire(request, Select(request, step == 0 ? "value" : "close", close: step == 0,
                inputs: step == 0 ? [] : ["greet"]));
            if (purpose == PlanningConstruction.DraftPurpose) return Wire(request, Draft(request, step++ == 0
                ? new() { Tasks = [Value()], RequirementIds = ["req_0"] }
                : new() { Outputs = [new("message", new() { Kind = "output", Source = "greet", Port = "message" })],
                    RequirementIds = ["req_0"], CloseScopes = ["/root"], Complete = true }));
            return Wire(request, Reviewed());
        } };
        var state = await PlannerFixture.RunAsync(runtime, Retained());
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Message)));
        Assert.Equal("closed", Assert.Single(state.Construction!.Requirements).Status);
        Assert.Equal(["req_0"], state.Construction.Checkpoints[0].ClosedRequirementIds);
        Assert.Empty(state.Construction.Checkpoints[1].ClosedRequirementIds!);
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task ReselectionRetainsAcceptedWorkAndCorrectionAccountingWithoutExpandingAuthority()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        state.Construction!.OpenScopes = ["/root"];
        state.Construction.Compact!.CurrentStep = new() { Id = "tentative", BaseHash = PlanningConstruction.Hash(state.Plan),
            Kind = "value", Scope = "/root", RequirementIds = ["req_0"] };
        var candidate = PlanningConstruction.Apply(state, new() { Tasks = [Value("rejected")], RequirementIds = ["req_0"] });
        candidate.Corrections = 2; state.Construction.Candidate = candidate;
        state.Diagnostics = [new("BUSINESS_OPERATION_REQUIRED", "/draft", "The selected tentative kind cannot perform the required operation.")];
        state.ReplanAttempts = 2;
        var hash = PlanningConstruction.Hash(state.Plan);
        PlanningCompactConstruction.Reselect(state, "Select the required inspected business operation.");
        Assert.Equal(hash, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(2, state.ReplanAttempts);
        Assert.Equal(2, state.Construction.DraftCorrections);
        Assert.Null(state.Construction.Candidate);
        Assert.Null(state.Construction.Compact.CurrentStep);
        Assert.Single(state.Construction.Compact.RejectedSteps);
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state,
            new() { Kind = "operation", Scope = "/root", RequirementIds = ["other"] }));
        state.Construction.Compact.CurrentStep = new() { Scope = "/root", RequirementIds = ["req_0"] };
        state.EditablePaths = ["/root/outputs/message"];
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.Reselect(state, "Cannot widen explicit typed correction."));
    }

    [Theory]
    [InlineData("port")]
    [InlineData("dependency")]
    public async Task CandidateCannotReadUnselectedAcceptedPortOrDependency(string kind)
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        state.Construction!.OpenScopes = ["/root"];
        state.Construction.Compact!.CurrentStep = new() { Id = "next", BaseHash = PlanningConstruction.Hash(state.Plan),
            Kind = "value", Scope = "/root", RequirementIds = ["req_0"] };
        var task = Value("consumer");
        if (kind == "port") task.Outputs = [new("message", new() { Kind = "output", Source = "greet", Port = "message" })];
        else task.DependsOn = ["greet"];
        var candidate = PlanningConstruction.Apply(state, new() { Tasks = [task], RequirementIds = ["req_0"] });
        var error = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, candidate));
        Assert.Contains(error.Diagnostics, d => d.Code == "CONSTRUCTION_STEP_INPUT_INVALID");
        if (kind == "port") state.Construction.Compact.CurrentStep.Inputs = [new() { Kind = "output", Source = "greet", Port = "message" }];
        else state.Construction.Compact.CurrentStep.DependencyTaskIds = ["greet"];
        PlanningCompactConstruction.ValidateExtension(state, candidate);
    }

    [Fact]
    public async Task FiveHundredIndependentTasksAndShallowScopesDoNotGrowSelectedDraftOrJudge()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        state.Construction!.OpenScopes = ["/root"];
        state.Construction.Compact!.CurrentStep = new() { Id = "next", BaseHash = PlanningConstruction.Hash(state.Plan), Kind = "value",
            Scope = "/root", Objective = "Produce another exact value.", RequirementIds = ["req_0"] };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Tasks = [Value("next")], RequirementIds = ["req_0"] });
        var beforeDraft = PlanningConstruction.DraftRequest(state, false);
        var beforeJudge = PlanningCompactConstruction.Context(state, review: true).ToJsonString();
        for (var i = 0; i < 500; i++) state.Plan!.Root.Tasks.Add(Value("independent_" + i));
        for (var i = 0; i < 101; i++)
        {
            state.Plan!.Root.Tasks.Add(new() { Id = "unrelated_scope_" + i, Kind = "sequence", Objective = "Independent accepted scope", Body = new() });
            state.Construction.Checkpoints.Add(new() { Sequence = i + 2, TaskIds = ["unrelated_scope_" + i], RequirementIds = [] });
        }
        state.Construction.Compact.CurrentStep.BaseHash = PlanningConstruction.Hash(state.Plan);
        var afterDraft = PlanningConstruction.DraftRequest(state, false);
        var afterJudge = PlanningCompactConstruction.Context(state, review: true).ToJsonString();
        Assert.Equal(beforeDraft.Prompt.Length, afterDraft.Prompt.Length);
        Assert.Equal(beforeJudge.Length, afterJudge.Length);
        Assert.DoesNotContain("independent_", afterDraft.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated_scope_", afterJudge, StringComparison.Ordinal);
        Assert.InRange(PlanningRequestMeasurements.Measure(afterDraft, PlanningConstruction.DraftPurpose, 8000).EstimatedInputTokens, 1, 8000);
    }

    [Fact]
    public async Task ExactResultCommitmentsRemainTentativeAndVisibleToTheJudge()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        state.Discovery.Resolved.Add(new() { Id = "read", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "test", Method = "read", EffectKind = "read",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["result"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("result"), ["additionalProperties"] = false } });
        state.Construction!.Compact!.CurrentStep = new() { Kind = "operation", Scope = "/root", RequirementIds = ["req_0"], OperationIds = ["read"] };
        PlanningCompactConstruction.StageCommitments(state, [new() { RequirementId = "req_0", Operation = "read", Result = new("result", "message") }]);
        Assert.Empty(state.Requirements!.Outcomes[0].Commitments ?? []);
        Assert.Equal("result", PlanningCompactConstruction.Context(state, review: true)["commitments"]!["req_0"]![0]!["result"]!["port"]!.ToString());
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.StageCommitments(state,
            [new() { RequirementId = "req_0", Operation = "read", Result = new("undeclared", "message") }]));
        Assert.Equal("result", Assert.Single(state.Construction.Compact.PendingCommitments["req_0"]).Result!.Port);
        PlanningCompactConstruction.Accept(state);
        Assert.Equal(new("result", "message"), Assert.Single(state.Requirements.Outcomes[0].Commitments!).Result);
    }

    [Fact]
    public async Task RetainedBaselineKeepsExistingTasksAndCallerDefaultsDuringInitialIndexing()
    {
        var original = Value("baseline");
        var state = Retained("Return exactly the existing greeting as message, preserving the baseline.");
        state.Request.Baseline = new() { Inputs = [new() { Name = "unused", Type = new() { Kind = "string" }, Required = false,
            Default = new() { Kind = "string", Text = "retained" } }], Root = new() { Tasks = [original] } };
        state.Plan = PlanningConstruction.Clone(state.Request.Baseline);
        state.IntentVersion = 3;
        var before = JsonSerializer.Serialize(original, PlanningJsonContext.Default.PlanTask);
        var runtime = new TestRuntime { Respond = (request, purpose) => Wire(request, purpose switch
        {
            PlanningCompactConstruction.IntentPurpose => Index(request),
            PlanningCompactConstruction.SelectPurpose => Select(request, "close", inputs: ["baseline"]),
            PlanningConstruction.DraftPurpose => Draft(request, new() { RequirementIds = ["req_0"], CloseScopes = ["/root"], Complete = true,
                Outputs = [new("message", new() { Kind = "output", Source = "baseline", Port = "message" })] }),
            _ => Reviewed()
        }) };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Message)));
        Assert.Equal(before, JsonSerializer.Serialize(Assert.Single(state.Plan!.Root.Tasks), PlanningJsonContext.Default.PlanTask));
        Assert.Equal("retained", Assert.Single(state.Requirements!.Inputs!).Default!.Text);
        Assert.True(PlanningClarifications.SameInputs(state.Request.Baseline!.Inputs, state.Plan.Inputs));
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task IntentInterfacesRetainLiteralDefaultsEnumsAndArrayCardinality()
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var response = Index(request);
            response["inputs"] = JsonNode.Parse("""
                [{"name":"selection","required":false,"type":{"kind":"string","nullable":false,"enum":["A","B"]},"default":{"kind":"string","text":"B"}},
                 {"name":"labels","required":false,"type":{"kind":"array","nullable":false,"items":{"kind":"string","nullable":false},"minItems":1,"maxItems":2},
                  "default":{"kind":"array","items":[{"kind":"string","text":"A"}]}},
                 {"name":"missing","required":false,"type":{"kind":"string","nullable":true},"default":{"kind":"null"}},
                 {"name":"record","required":false,"type":{"kind":"object","nullable":false,"fields":[{"name":"enabled","required":true,"type":{"kind":"boolean","nullable":false}}]},
                  "default":{"kind":"object","members":[{"name":"enabled","value":{"kind":"boolean","boolean":false}}]}}]
                """);
            return Wire(request, response);
        } };
        var state = await new HybridWorkflowPlanner().AdvanceAsync(Retained("Allow selection A or B with default B; one or two labels defaulting to A; nullable missing defaults to null; record.enabled defaults to false. Return Hello."),
            new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        var inputs = state.Requirements!.Inputs!;
        Assert.Equal(["A", "B"], inputs[0].Type.Enum);
        Assert.Equal("B", inputs[0].Default!.Text);
        Assert.Equal(1, inputs[1].Type.MinItems); Assert.Equal(2, inputs[1].Type.MaxItems);
        Assert.Equal("A", Assert.Single(inputs[1].Default!.Items).Text);
        Assert.Equal("null", inputs[2].Default!.Kind);
        Assert.False(Assert.Single(inputs[3].Default!.Members).Value.Boolean);
        Assert.True(PlanningClarifications.SameInputs(inputs, PlannerFixture.Clone(state).Requirements!.Inputs));
        Assert.InRange(state.RequestMeasurements!.Single().EstimatedInputTokens, 1, 3000);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("bounds")]
    [InlineData("missing_default")]
    public async Task InvalidDeclaredInterfaceConstraintsCannotBecomeAcceptedIntent(string invalid)
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var response = Index(request);
            response["inputs"] = invalid == "missing_default" ? JsonNode.Parse("""
                [{"name":"selection","required":false,"type":{"kind":"string","nullable":false}}]
                """) : invalid == "default" ? JsonNode.Parse("""
                [{"name":"selection","required":false,"type":{"kind":"string","nullable":false,"enum":["A","B"]},"default":{"kind":"string","text":"C"}}]
                """) : JsonNode.Parse("""
                [{"name":"labels","required":true,"type":{"kind":"array","nullable":false,"items":{"kind":"string","nullable":false},"minItems":3,"maxItems":2}}]
                """);
            return Wire(request, response);
        } };
        var state = await new HybridWorkflowPlanner().AdvanceAsync(Retained(), new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        Assert.Null(state.Requirements);
        Assert.Contains(state.Diagnostics, d => d.Code == (invalid switch { "default" => "TASK_DEFAULT_INVALID", "bounds" => "TASK_TYPE_INVALID", _ => "TASK_DEFAULT_REQUIRED" }));
        Assert.False(state.Construction!.Compact!.Sources.Single().Indexed);
    }

    [Fact]
    public async Task PublicOutputCannotReplaceProducedEvidenceWithAnIndexedDefault()
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var response = Index(request);
            response["outputs"]![0]!["default"] = new JsonObject { ["kind"] = "string", ["text"] = "Invented result" };
            return new() { Json = response }; // Exercise exact response-schema rejection without fixture projection.
        } };
        var state = await new HybridWorkflowPlanner().AdvanceAsync(Retained(), new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        Assert.Null(state.Requirements);
        Assert.NotEmpty(state.Diagnostics);
        Assert.False(state.Construction!.Compact!.Sources.Single().Indexed);
    }

    [Fact]
    public async Task RequestedDiscoveryPageReplacesTheInitialMatchingDirectory()
    {
        var state = await PlannerFixture.RunAsync(new TestRuntime { Respond = Simple }, Retained());
        state.Construction!.Compact!.CurrentStep = new() { Kind = "operation", RequirementIds = ["req_0"] };
        state.Construction.Evidence = new() { ["page:operations:4"] = new JsonObject { ["offset"] = 4, ["items"] = new JsonArray("later-operation") },
            ["page:sources:2"] = new JsonObject { ["offset"] = 2, ["items"] = new JsonArray("later-source") } };
        var context = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.Null(context["descriptors"]); Assert.Null(context["sources"]);
        Assert.Equal("later-operation", context["evidence"]!["page:operations:4"]!["items"]![0]!.ToString());
        Assert.Equal("later-source", context["evidence"]!["page:sources:2"]!["items"]![0]!.ToString());
    }

    [Fact]
    public async Task ReusableGroupIsConstructedBeforeItsCallUsingOnlyTheClosedInterface()
    {
        var step = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            if (purpose == PlanningCompactConstruction.IntentPurpose) return Wire(request, Index(request));
            if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                var response = Select(request, step switch { 0 => "group", 1 => "value", _ => "call" },
                    step == 1 ? "/groups/reusable/body" : "/root", close: step == 2);
                if (step < 2) response["step"]!["outputNames"] = new JsonArray();
                if (step == 0)
                {
                    response["step"]!["objective"] = "Build the reusable greeting interface.";
                    response["step"]!["expectedOutputs"] = new JsonArray("Return the exact supplied word as message.");
                }
                if (step == 1)
                {
                    Assert.Contains("\"input\":\"word\"", Context(request)["scopes"]!.ToJsonString(), StringComparison.Ordinal);
                    var scope = Context(request)["scopes"]!.AsArray().Single(s => s!["kind"]?.ToString() == "group")!;
                    Assert.Equal("Build the reusable greeting interface.", scope["objective"]!.ToString());
                    Assert.Equal("req_0", scope["requirementIds"]![0]!.ToString());
                    Assert.Equal("group:reusable", scope["evidence"]!.ToString());
                    Assert.Null(scope["expectedOutputs"]);
                    response["step"]!["inputs"] = new JsonArray(new JsonObject { ["kind"] = "input", ["source"] = "word" });
                }
                if (step == 2)
                {
                    Assert.Contains("\"group\":\"reusable\"", Context(request)["interfaces"]!.ToJsonString(), StringComparison.Ordinal);
                    response["step"]!["groupId"] = "reusable";
                }
                return Wire(request, response);
            }
            if (purpose == PlanningConstruction.DraftPurpose)
            {
                if (step == 1)
                {
                    var scope = Context(request)["scopeGroup"]!;
                    Assert.Equal("Build the reusable greeting interface.", scope["intent"]!["objective"]!.ToString());
                    Assert.Equal("Return the exact supplied word as message.", scope["intent"]!["expectedOutputs"]![0]!.ToString());
                    Assert.Null(scope["body"]);
                }
                var extension = step++ switch
                {
                    0 => new PlanningConstructionExtension { Groups = [new() { Id = "reusable", Inputs = [new() { Name = "word", Type = new() { Kind = "string" } }] }], RequirementIds = ["req_0"] },
                    1 => new() { Scope = "/groups/reusable/body", Tasks = [new() { Id = "assemble", Kind = "value", Objective = "Return the supplied word.",
                        Outputs = [new("message", new() { Kind = "input", Source = "word" })] }],
                        Outputs = [new("message", new() { Kind = "output", Source = "assemble", Port = "message" })], CloseScopes = ["/groups/reusable/body"], RequirementIds = ["req_0"] },
                    _ => new() { Tasks = [new() { Id = "invoke", Kind = "call", Group = "reusable", Objective = "Return the greeting using the requested reusable group.",
                        Inputs = [new("word", new() { Kind = "string", Text = "Hello" })] }],
                        Outputs = [new("message", new() { Kind = "output", Source = "invoke", Port = "message" })], RequirementIds = ["req_0"], CloseScopes = ["/root"], Complete = true }
                };
                if (step == 3)
                {
                    var group = Context(request)["group"]!;
                    Assert.Equal("word", group["inputs"]![0]!["name"]!.ToString());
                    Assert.Equal("string", group["outputs"]![0]!["schema"]!["type"]!.ToString());
                    Assert.Null(group["body"]);
                    Assert.DoesNotContain("assemble", group.ToJsonString(), StringComparison.Ordinal);
                }
                return Wire(request, Draft(request, extension));
            }
            if (purpose is PlanningConstruction.LocalPurpose or PlanningConstruction.IntermediatePurpose)
            {
                var stage = Context(request)["constructionStage"]!;
                Assert.Equal(step == 3, stage["requirementCompletionClaimed"]!.GetValue<bool>());
                Assert.Equal(step == 3, stage["planCompletionClaimed"]!.GetValue<bool>());
                if (step == 1)
                {
                    Assert.Equal(1, stage["openedScopeCount"]!.GetValue<int>());
                    Assert.Equal(0, stage["closedScopeCount"]!.GetValue<int>());
                    Assert.Contains("Newly opened group/container scopes intentionally have empty children", request.Prompt, StringComparison.Ordinal);
                    Assert.Contains("Empty closeRequirementIds makes no requirement-completion claim", request.Prompt, StringComparison.Ordinal);
                    Assert.InRange(PlanningRequestMeasurements.Measure(request, purpose, 4000).EstimatedInputTokens, 1, 4000);
                }
                if (step == 2)
                {
                    Assert.Equal(0, stage["openedScopeCount"]!.GetValue<int>());
                    Assert.Equal(1, stage["closedScopeCount"]!.GetValue<int>());
                }
            }
            return Wire(request, Reviewed());
        } };
        var state = await PlannerFixture.RunAsync(runtime, Retained("Use a reusable group to return exactly Hello as message."));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Single(state.Plan!.Groups); Assert.Equal("call", Assert.Single(state.Plan.Root.Tasks).Kind);
        PlanningArtifactApproval.Verify(state);
        var opening = state.Construction!.Checkpoints[0];
        Assert.Equal("Build the reusable greeting interface.", opening.Step!.Objective);
        Assert.Equal("reusable", opening.Fragment["groups"]![0]!.ToString());
        var saved = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        Assert.Equal("Return the exact supplied word as message.", PlanningCompactConstruction.GroupInterface(saved, "reusable")!["intent"]!["expectedOutputs"]![0]!.ToString());
        PlanningConstruction.LoadEvidence(saved, ["scope:/groups/reusable/body"]);
        Assert.Equal("Build the reusable greeting interface.", saved.Construction!.Evidence!["scope:/groups/reusable/body"]!["group"]!["intent"]!["objective"]!.ToString());
        saved.Plan!.Groups[0].Body.Tasks.Add(new() { Id = "nested", Kind = "foreach", Body = new() });
        saved.Construction.OpenScopes = ["/groups/reusable/body", "/tasks/nested/body"];
        saved.Construction.Compact!.CurrentStep = new() { Kind = "value", Scope = "/tasks/nested/body", RequirementIds = ["req_0"] };
        Assert.Equal("reusable", PlanningCompactConstruction.ScopeGroup(saved, "/tasks/nested/body")!.Id);
        Assert.Equal("Build the reusable greeting interface.", PlanningCompactConstruction.Context(saved)["scopeGroup"]!["intent"]!["objective"]!.ToString());
        saved.Construction.Checkpoints[0].InvalidatedTaskIds.Add("invalidated-original");
        Assert.Null(PlanningCompactConstruction.GroupInterface(saved, "reusable")!["intent"]);
        Assert.DoesNotContain("\"step\"", JsonSerializer.Serialize(new PlanningConstructionCheckpoint(), PlanningJsonContext.Default.PlanningConstructionCheckpoint), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PlanningMode.Auto)]
    [InlineData(PlanningMode.Interactive)]
    public async Task LiteralChoiceCanBeDeclaredThenConsumedWithExistingSelectionBehavior(string mode)
    {
        var step = 0;
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            if (purpose == PlanningCompactConstruction.IntentPurpose) return Wire(request, Index(request));
            if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                var response = Select(request, step == 0 ? "choice" : "value", close: step == 1);
                if (step == 1)
                {
                    Assert.Contains("\"choice\":\"greeting\"", Context(request)["interfaces"]!.ToJsonString(), StringComparison.Ordinal);
                    response["step"]!["inputs"] = new JsonArray(new JsonObject { ["kind"] = "choice", ["source"] = "greeting" });
                }
                return Wire(request, response);
            }
            if (purpose == PlanningConstruction.DraftPurpose)
            {
                var extension = step++ == 0 ? new PlanningConstructionExtension { RequirementIds = ["req_0"], Choices = [new() { Id = "greeting", Question = "Choose the greeting.",
                    Type = new() { Kind = "string" }, Recommended = "hello", Alternatives = [new("hello", "Hello", new() { Kind = "string", Text = "Hello" }), new("hi", "Hi", new() { Kind = "string", Text = "Hi" })] }] }
                    : new() { RequirementIds = ["req_0"], Tasks = [new() { Id = "chosen", Kind = "value", Objective = "Return the selected greeting.",
                        Outputs = [new("message", new() { Kind = "choice", Source = "greeting" })] }],
                        Outputs = [new("message", new() { Kind = "output", Source = "chosen", Port = "message" })], CloseScopes = ["/root"], Complete = true };
                if (step == 2) Assert.Equal("hello", Context(request)["choices"]![0]!["selected"]!.ToString());
                return Wire(request, Draft(request, extension));
            }
            return Wire(request, Reviewed());
        } };
        var state = Retained("Choose Hello or Hi, recommending Hello, and return the choice as message."); state.Request.Mode = mode;
        var planner = new HybridWorkflowPlanner(); var selections = 0;
        for (var i = 0; i < 25 && !PlanningStatus.IsTerminal(state.Status) && state.Status != PlanningStatus.FinalReview; i++)
        {
            var command = state.Status == PlanningStatus.Clarification
                ? new PlanningCommand { Kind = "choose", ExpectedRevision = state.Revision, Selections = new() { ["greeting"] = "hello" } }
                : new() { ExpectedRevision = state.Revision };
            if (command.Kind == "choose") selections++;
            state = await planner.AdvanceAsync(state, command, runtime, PlannerFixture.Ct);
        }
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(mode == PlanningMode.Auto ? 0 : 1, selections);
        Assert.Equal("hello", Assert.Single(state.Plan!.Choices).Selected);
        PlanningArtifactApproval.Verify(state);
        var duplicate = PlanningConstruction.Clone(state.Plan); duplicate.Root.Tasks.Add(new() { Id = "duplicate", Kind = "value", Objective = "Invalid duplicate choice use",
            Outputs = [new("other", new() { Kind = "choice", Source = "greeting" })] });
        Assert.Contains(TaskPlanCompiler.ValidateConstruction(duplicate, state.Catalog!, state.Request, new HashSet<string> { "/root" }).Diagnostics, d => d.Code == "CHOICE_SLOT_INVALID");
        var unbound = PlanningConstruction.Clone(state.Plan); unbound.Root = new();
        Assert.Contains(new TaskPlanCompiler().Compile(unbound, state.Catalog!, state.Request).Diagnostics, d => d.Code == "CHOICE_SLOT_INVALID");
    }

    [Fact]
    public async Task NewBusinessRequirementFromExplicitRevisionCannotDisappearBehindClosedOriginals()
    {
        var runtime = new TestRuntime { Respond = Simple };
        var state = await PlannerFixture.RunAsync(runtime, Retained());
        var original = state.Requirements!.Outcomes[0];
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision, PreserveRequirements = true,
            ArtifactHash = state.ComputeArtifactHash(), Text = "Also produce an independently verified second result.", EditablePaths = ["/root/outputs/message"] }, runtime, PlannerFixture.Ct);
        runtime.Respond = (request, _) =>
        {
            var response = Index(request); response["outputs"] = new JsonArray();
            return Wire(request, response);
        };
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(JsonSerializer.Serialize(original, PlanningJsonContext.Default.PlanningRequirement),
            JsonSerializer.Serialize(state.Requirements!.Outcomes[0], PlanningJsonContext.Default.PlanningRequirement));
        Assert.Equal(2, state.Requirements.Outcomes.Count);
        Assert.Equal("remaining", state.Construction!.Requirements[1].Status);
        Assert.Contains(state.Construction.Candidate!.Diagnostics, d => d.Code == "CONSTRUCTION_REQUIREMENT_REMAINING");
        Assert.DoesNotContain(state.Construction.Compact!.GlobalSourceIds, id => id.StartsWith("revision:", StringComparison.Ordinal));
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    [Fact]
    public async Task DiscoveryAndContinuationRemainBoundToTheSelectedBusinessSubstep()
    {
        const string objective = "Read the exact independently supplied record.";
        var catalog = new ScopedCatalog(); var pages = 0;
        var runtime = new TestRuntime { Capabilities = catalog, Respond = (request, purpose) =>
        {
            if (purpose == PlanningCompactConstruction.IntentPurpose) return Wire(request, Index(request));
            if (purpose == PlanningCompactConstruction.SelectPurpose)
            { var selection = Select(request, "operation", close: false); selection["step"]!["objective"] = objective; return Wire(request, selection); }
            if (pages > 0) Assert.Equal("next", Context(request)["providerRequests"]!["items"]![0]!["cursor"]!.ToString());
            return Wire(request, new() { ["operationIds"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
                ["discoveryRequests"] = new JsonArray(new JsonObject { ["sourceId"] = "source", ["cursor"] = pages++ == 0 ? null : "next", ["query"] = null, ["operationIds"] = null }) });
        } };
        var state = Retained("First read the specified record, then do unrelated later business work."); var planner = new HybridWorkflowPlanner();
        for (var i = 0; i < 4; i++) state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal([objective, objective], catalog.Queries);
        Assert.All(state.Discovery.Pages, page => Assert.Equal(objective, page.Query));
        var invalid = new JsonObject { ["operationIds"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
            ["discoveryRequests"] = new JsonArray(new JsonObject { ["sourceId"] = "source", ["cursor"] = null, ["query"] = state.Request.Prompt, ["operationIds"] = null }) };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, PlanningCompactConstruction.DiscoverySchema(state)));
    }

    private sealed class ScopedCatalog : ICapabilityCatalog
    {
        internal List<string?> Queries { get; } = [];
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([new("source", "Declared scoped reads")]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        { Queries.Add(query); return Task.FromResult(new CapabilityPage(sourceId, cursor, [], cursor is null ? "next" : null, Query: query)); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException("This navigation test resolves no operation.");
    }

}
