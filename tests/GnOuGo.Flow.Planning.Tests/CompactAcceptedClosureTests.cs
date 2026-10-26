using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactAcceptedClosureTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task HistoricalOrIssuedClosureCannotAcquireFreshIdentityMetadata(int presentation)
    {
        var state = await State();
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        if (presentation == 5)
            state.PendingCall = new() { Id = "retained-selection", Purpose = PlanningCompactConstruction.SelectPurpose,
                Request = new() { ClientRequestId = "retained-selection", Prompt = "Retained exact selection request.",
                    StructuredOutputSchema = PlanningCompactConstruction.SelectionSchema(state) } };
        var exact = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Null(state.Construction.CandidateFailureIdentityVersion);
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(state));
        Assert.Equal(exact, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task ReviewedTerminalRootClosesWithoutSelectingOrGeneratingAnotherTask()
    {
        var state = await State();
        var hash = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            Assert.Equal(new[] { "local", "intermediate" }, context["reviewCoverage"]!.AsArray().Select(item => item!.ToString()));
            Assert.Empty(context["draft"]!["tasks"]!.AsArray());
            return new() { Json = new JsonObject { ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray() },
                ["uncertainty"] = "Runtime remains unverified." } };
        } };
        state = await Advance(state, runtime);
        Assert.Empty(runtime.Calls);
        Assert.NotNull(state.Construction!.Candidate);
        Assert.True(state.Construction.Candidate.Extension.Complete);
        Assert.Equal(hash, state.Construction.Candidate.Hash);
        Assert.False(state.Construction.Complete);
        Assert.Single(state.Construction.Checkpoints);
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Single(runtime.Calls);
        Assert.Empty(state.Diagnostics);
        Assert.True(state.Construction!.Complete);
        Assert.Equal(hash, PlanningConstruction.Hash(state.Plan));
        Assert.Single(state.Plan!.Root.Tasks);
        Assert.Equal("closed", Assert.Single(state.Construction.Requirements).Status);
        Assert.NotNull(state.Yaml);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task TerminalSelectionCannotOfferWorkOrAnUnboundKind()
    {
        var state = await State();
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        var step = new JsonObject { ["scope"] = "/root", ["kind"] = "close", ["objective"] = "Close the reviewed root.",
            ["requirementIds"] = new JsonArray("greeting"), ["inputs"] = new JsonArray(), ["groupId"] = null,
            ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray(), ["closeRequirementIds"] = new JsonArray(),
            ["outputNames"] = new JsonArray(), ["prerequisites"] = null };
        var response = new JsonObject { ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["step"] = step,
            ["evidenceRequests"] = null, ["clarifications"] = null };
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, schema));
        foreach (var kind in new string?[] { "value", "transform", "sequence", null })
        {
            step["kind"] = kind;
            Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(response, schema));
        }
    }

    [Theory]
    [InlineData("unreviewed")]
    [InlineData("source")]
    [InlineData("remaining")]
    [InlineData("invalidated")]
    public async Task ClosureReadinessRequiresRetainedReviewedObligations(string missing)
    {
        var state = await State();
        switch (missing)
        {
            case "unreviewed": state.Construction!.Checkpoints[0].Reviews.Clear(); break;
            case "source": state.Construction!.Compact!.Sources[0].Indexed = false; break;
            case "remaining": state.Construction!.Requirements[0].Status = "remaining"; break;
            case "invalidated": state.Construction!.Checkpoints[0].InvalidatedTaskIds.Add("greet"); break;
        }
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(state));
    }

    [Fact]
    public async Task ScopeRelationshipsAndAcceptedExportsRemainDistinctFromPublicRequirements()
    {
        var state = await State();
        state.Plan!.Root.Tasks.Add(new() { Id = "loop", Kind = "foreach", Items = new() { Kind = "array" },
            Body = new() { Outputs = [new("scalar", new() { Kind = "number", Number = 2 })] } });
        state.Construction!.OpenScopes.Add("/tasks/loop/body");
        state.Requirements!.Outputs = [new() { Name = "array", Type = new() { Kind = "array", Items = new() { Kind = "number" } } }];
        state.Plan.Root.Outputs.Clear();
        state.Construction.Requirements[0].Status = "in_progress";
        var context = PlanningCompactConstruction.SelectionContext(state);
        var scopes = context["scopes"]!.AsArray();
        Assert.Equal(1, scopes.Single(scope => scope!["path"]!.ToString() == "/root")!["openDescendantCount"]!.GetValue<int>());
        Assert.Equal("scalar", scopes.Single(scope => scope!["path"]!.ToString() == "/tasks/loop/body")!["acceptedExports"]!["names"]![0]!.ToString());
        Assert.Equal("not_bound", context["publicOutputs"]!["items"]![0]!["bindingStatus"]!.ToString());
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(state));
    }

    [Fact]
    public async Task DependencyInvalidationExplicitlyReopensAnAcceptedRequirement()
    {
        var state = await State();
        PlanningConstruction.Rollback(state, "greet", "The accepted producer requires an explicit repair.");
        Assert.Equal("remaining", state.Construction!.Requirements[0].Status);
        Assert.False(PlanningCompactConstruction.CompletionOnly(state));
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(state));
        Assert.Single(state.Construction.Invalidations);
    }

    [Fact]
    public async Task NestedClosureCarriesOwnerAndCleanupOriginalsInsteadOfUnrelatedClosedRequirements()
    {
        var state = await NestedState();
        var originalHash = PlanningConstruction.Hash(state.Plan);
        var probe = Assert.IsType<PlanningSession>(PlanningCompactConstruction.PrepareReviewedClosure(state));
        Assert.Equal("/tasks/inner/body", probe.Construction!.Compact!.CurrentStep!.Scope);
        Assert.Equal(new[] { "work", "cleanup" }, probe.Construction.Compact.CurrentStep.RequirementIds);
        Assert.Equal(originalHash, PlanningConstruction.Hash(state.Plan));
        Assert.Null(state.Construction!.Candidate);
        Assert.Null(state.Construction.Compact!.CurrentStep);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(state.Plan!, state.Catalog!, state.Request,
            state.Construction.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(probe.Construction.Candidate!.Plan, probe.Catalog!, probe.Request,
            probe.Construction.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);

        // The nonfirst cleanup original is a substantive obligation in the actual
        // issued review, not merely an ID in a checkpoint navigation directory.
        var (_, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(probe, intermediate: true);
        var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        Assert.Equal(new[] { "work", "cleanup" }, context["requirements"]!.AsArray().Select(item => item!["id"]!.ToString()));
        Assert.Equal("Release the owned boundary after success or failure.", context["requirements"]![1]!["original"]!.ToString());
        Assert.Equal("cleanup-original", context["requirements"]![1]!["sources"]![0]!["id"]!.ToString());
        var finding = new JsonObject
        {
            ["kind"] = "omission", ["location"] = "/requirements/1", ["obligation"] = "/requirements/1",
            ["implementation"] = "/scopeInterface", ["explanation"] = "The accepted closure must preserve its cleanup obligation.",
            ["uncertainty"] = "No execution observed.", ["fact"] = null, ["requiredBinding"] = null
        };
        var report = new JsonObject { ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray(finding) },
            ["uncertainty"] = "No execution observed." };
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(report, request.StructuredOutputSchema!));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public async Task RootClosureRetainsEveryOriginalOrDeclinesTheExistingFourRequirementBound(int count, bool prepared)
    {
        var state = await State();
        for (var i = 1; i < count; i++) AddReviewedRequirement(state, "obligation_" + i, "Preserve original obligation " + i + ".");
        var probe = PlanningCompactConstruction.PrepareReviewedClosure(state);
        Assert.Equal(prepared, probe is not null);
        if (probe is not null)
            Assert.Equal(state.Requirements!.Outcomes.Select(item => item.Id), probe.Construction!.Compact!.CurrentStep!.RequirementIds);
        Assert.Null(state.Construction!.Candidate);
        Assert.Null(state.Construction.Compact!.CurrentStep);
    }

    [Theory]
    [InlineData("missing_association")]
    [InlineData("too_many")]
    public async Task NestedClosureDoesNotGuessOrTruncateOriginalAssociations(string defect)
    {
        var state = await NestedState();
        if (defect == "missing_association") state.Construction!.Checkpoints.Single(item => item.TaskIds.Contains("producer")).TaskIds.Clear();
        else
        {
            AddReviewedRequirement(state, "extra_1", "Preserve the first additional boundary obligation.").ImportedTaskIds = ["inner"];
            AddReviewedRequirement(state, "extra_2", "Preserve the second additional boundary obligation.").ImportedTaskIds = ["inner"];
            AddReviewedRequirement(state, "extra_3", "Preserve the third additional boundary obligation.").ImportedTaskIds = ["inner"];
        }
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(state));
    }

    private static async Task<PlanningSession> NestedState()
    {
        var state = await State();
        state.Plan!.Root.Tasks.Add(new() { Id = "outer", Kind = "sequence", Objective = "Own the nested lifecycle boundary.", Body = new()
        {
            Tasks = [new() { Id = "inner", Kind = "sequence", Objective = "Own local work and cleanup.", Body = new()
            {
                Tasks = [new() { Id = "producer", Kind = "value", Objective = "Produce the local numeric record.",
                    Outputs = [new("number", new() { Kind = "number", Number = 2 })] }],
                Always = [new() { Id = "cleanup", Kind = "value", Objective = "Record completion of the local cleanup boundary.",
                    DependsOn = ["producer"], Outputs = [new("done", new() { Kind = "present", Source = "producer" })] }]
            } }]
        } });
        state.Construction!.OpenScopes = ["/root", "/tasks/outer/body", "/tasks/inner/body"];
        var work = AddReviewedRequirement(state, "work", "Produce the numeric result in the owned boundary.");
        work.TaskIds = ["outer", "inner", "producer"];
        work.Step = new() { Scope = "/tasks/inner/body", RequirementIds = ["work"] };
        var cleanup = AddReviewedRequirement(state, "cleanup", "Release the owned boundary after success or failure.");
        cleanup.TaskIds = ["cleanup"]; cleanup.ImportedTaskIds = ["producer"];
        var output = AddReviewedRequirement(state, "public_result", "Return the independent greeting publicly.");
        output.Fragment = new() { ["scope"] = "/root", ["outputs"] = new JsonArray(new JsonObject { ["name"] = "message" }) };
        return state;
    }

    private static PlanningConstructionCheckpoint AddReviewedRequirement(PlanningSession state, string id, string text)
    {
        var compact = state.Construction!.Compact!;
        var source = new PlanningConstructionSource { Id = id + "-original", Kind = "requirement", Text = text, Indexed = true,
            InterfaceKind = "none", Version = PlanningGraphCompiler.Fingerprint(text) };
        compact.Sources.Add(source); compact.RequirementSources[id] = [source.Id];
        state.Requirements!.Outcomes.Add(new(id, text));
        var sequence = state.Construction.Checkpoints.Count + 1;
        var hash = PlanningConstruction.Hash(state.Plan);
        var checkpoint = new PlanningConstructionCheckpoint { Sequence = sequence, PlanHash = hash, RequirementIds = [id], ClosedRequirementIds = [id],
            Reviews = [new JsonObject { ["candidateHash"] = hash, ["outcome"] = "unverified", ["response"] = new JsonObject { ["findings"] = new JsonArray() } }] };
        state.Construction.Checkpoints.Add(checkpoint);
        state.Construction.Requirements.Add(new() { Id = id, Status = "closed", Checkpoints = [sequence] });
        return checkpoint;
    }

    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) => new HybridWorkflowPlanner()
        .AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);

    private static async Task<PlanningSession> State()
    {
        var state = new PlanningSession { Request = new() { TenantId = "accepted-closure", Prompt = "Return the public greeting message." } };
        PlanningSessionCreation.Capture(state);
        // These retained checkpoint records predate per-window review coverage.
        state.Request.Options["construction_presentation_version"] = 5;
        state.Construction!.Compact!.PresentationVersion = 5;
        state.IntentVersion = 3; state.Revision = 1; state.Status = PlanningStatus.Generating;
        PlanningCompactConstruction.InitializeSources(state);
        var compact = state.Construction!.Compact!;
        foreach (var source in compact.Sources) source.Indexed = true;
        var original = new PlanningConstructionSource { Id = "original", Text = state.Request.Prompt, Kind = "requirement", Indexed = true,
            InterfaceKind = "none", Version = PlanningGraphCompiler.Fingerprint(state.Request.Prompt) };
        compact.Sources.Add(original); compact.RequirementSources["greeting"] = [original.Id]; compact.InterfaceExtractions = [];
        state.Requirements = new() { Inputs = [], Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }],
            Outcomes = [new("greeting", state.Request.Prompt)] };
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "greet", Kind = "value", Objective = "Produce the greeting.",
            Outputs = [new("message", new() { Kind = "string", Text = "Hello" })] }],
            Outputs = [new("message", new() { Kind = "output", Source = "greet", Port = "message" })] } };
        state.Construction.Requirements = [new() { Id = "greeting", Status = "closed", Checkpoints = [1] }];
        var hash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Checkpoints = [new() { Sequence = 1, PlanHash = hash, TaskIds = ["greet"], RequirementIds = ["greeting"],
            ClosedRequirementIds = ["greeting"], Step = new() { Scope = "/root", Kind = "value", RequirementIds = ["greeting"] },
            Reviews = [new JsonObject { ["candidateHash"] = hash, ["outcome"] = "unverified",
                ["response"] = new JsonObject { ["findings"] = new JsonArray() } }] }];
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        return state;
    }
}
