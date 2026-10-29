using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactIntentIndexingTests
{
    private const string Objective = "Accept required text named query without a default. Write the local report. Return its path as reportPath.";
    private static PlanningSession Fresh(string prompt = Objective) => PlannerFixture.Capture(new()
    { Request = new() { TenantId = "combined-intent", Prompt = prompt, Options = new() { ["automatic_correction"] = true } } });
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static JsonArray Declarations(params TaskInput[] declarations) => JsonSerializer.SerializeToNode(declarations.ToList(), PlanningJsonContext.Default.ListTaskInput)!.AsArray();
    private static JsonObject Index(LLMRequest request) => CurrentPlanningFixture.Intent(Context(request),
        Declarations(new TaskInput() { Name = "query", Type = new() { Kind = "string" } }),
        Declarations(new TaskInput() { Name = "reportPath", Type = new() { Kind = "string" } }));
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);

    [Fact]
    public async Task ShortOriginalCoverageAndBothInterfacesCommitInOneCall()
    {
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, Index(request)) };
        var state = await Advance(Fresh(), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(0, runtime.Discoveries);
        Assert.Equal(Objective, Assert.Single(state.Requirements!.Outcomes).Description);
        Assert.Equal("query", Assert.Single(state.Requirements.Inputs!).Name);
        Assert.Equal("reportPath", Assert.Single(state.Requirements.Outputs!).Name);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Single(state.Construction!.Compact!.Sources, source => source.Kind == "page");
        Assert.Null(state.Construction.Compact.CurrentStep);
    }

    [Theory]
    [InlineData("omission")]
    [InlineData("default")]
    [InlineData("type")]
    [InlineData("two_actions")]
    public async Task InvalidCombinedIndexCannotPartiallyAcceptCoverageOrAnOtherwiseValidInput(string failure)
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var response = Index(request);
            if (failure == "omission") response["clauses"]![0]!["text"] = "Write the local report.";
            if (failure == "default") response["clauses"]![0]!["inputs"]![0]!["required"] = false;
            if (failure == "type") response["clauses"]![0]!["outputs"]![0]!["type"]!["kind"] = "missing";
            if (failure == "two_actions") response["clarifications"] = new JsonArray(new JsonObject
                { ["id"] = "missing", ["question"] = "Which target?", ["alternatives"] = new JsonArray(), ["recommended"] = null });
            return Wire(request, response);
        } };
        var state = await Advance(Fresh(), runtime);
        Assert.NotEmpty(state.Diagnostics);
        Assert.Null(state.Requirements);
        Assert.All(state.Construction!.Compact!.Sources, source => Assert.False(source.Indexed));
        Assert.False(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Null(state.Construction.Candidate);
        Assert.Equal(0, runtime.Discoveries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactIssuedIntentRecoversWithoutAnotherReservation(bool savedReceipt)
    {
        var runtime = new TestRuntime { Respond = (_, _) => throw new OperationCanceledException() };
        await Advance(Fresh(), runtime);
        var pending = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall is not null));
        pending.Status = PlanningStatus.Generating;
        var exact = JsonSerializer.Serialize(pending.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var calls = pending.ModelCalls;
        var receipt = Wire(pending.PendingCall.Request, Index(pending.PendingCall.Request));
        var physical = 0;
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            if (savedReceipt) return receipt;
            physical++;
            return Wire(request, Index(request));
        } };
        var state = await Advance(pending, recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(savedReceipt ? 0 : 1, physical);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task ReceivedReasoningOnlyExhaustionCannotIndexSourcesOrRetry()
    {
        var runtime = new TestRuntime { Respond = (_, _) => new() { CompletionStatus = "output_limit",
            Usage = new JsonObject { ["output_tokens"] = 8192, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 8192 } } } };
        var state = await Advance(Fresh(), runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Null(state.Requirements);
        Assert.All(state.Construction!.Compact!.Sources, source => Assert.False(source.Indexed));
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(runtime.Calls);
        Assert.Same(state, await Advance(state, runtime));
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task RemovingADeclarationBlocksGenerationBeforeDiscovery()
    {
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, Index(request)) };
        var state = await Advance(Fresh(), runtime);
        state.Construction!.Compact!.Sources.Single(source => source.Kind == "requirement").Inputs = null;
        await Assert.ThrowsAsync<PlanningConflictException>(() => Advance(PlannerFixture.Clone(state), runtime));
        Assert.Equal(0, runtime.Discoveries);
        Assert.Single(runtime.Calls);
    }

    [Theory]
    [InlineData("漢")]
    [InlineData("🧠")]
    [InlineData("\"\\\n")]
    public async Task LargeOriginalUsesMeasuredPagesAndRetainsUnicodeContinuations(string token)
    {
        var original = string.Concat(Enumerable.Repeat(token, 4000));
        var state = Fresh(original);
        PlanningCompactConstruction.InitializeSources(state);
        var pages = state.Construction!.Compact!.Sources.ToArray();
        Assert.True(pages.Length > 1);
        Assert.Equal(original, string.Concat(pages.Select(page => page.Text)));
        Assert.All(pages, page => Assert.False(char.IsLowSurrogate(page.Text[0])));
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, CurrentPlanningFixture.Intent(Context(request))) };
        foreach (var page in pages)
        {
            state = await Advance(state, runtime);
            Assert.Empty(state.Diagnostics);
        }
        Assert.Equal(pages.Length, state.ModelCalls);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
    }

    [Fact]
    public async Task MissingCrossPageTypeUsesExactOriginalEvidenceWithoutAcceptingAPartialDeclaration()
    {
        var original = "Accept exactly one required caller input named query; its type is specified below. " +
            new string('x', 15_000) + ". The declared type of query is text, without a default.";
        var state = Fresh(original);
        PlanningCompactConstruction.InitializeSources(state);
        var last = state.Construction!.Compact!.Sources.Last();
        Assert.Contains("declared type", last.Text, StringComparison.Ordinal);
        var address = "source:" + last.Id;
        var calls = 0;
        var runtime = new TestRuntime();
        runtime.Respond = (request, _) =>
        {
            if (calls == 0) Assert.Contains(address, runtime.Checkpoints.Last().PendingCall!.EvidenceAddresses!.Addresses.Values);
            return Wire(request, calls++ == 0 ? new JsonObject { ["evidenceRequests"] = new JsonArray(address) }
                : CurrentPlanningFixture.Intent(Context(request), Declarations(new TaskInput() { Name = "query", Type = new() { Kind = "string" } })));
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.Requirements);
        Assert.All(state.Construction!.Compact!.Sources, source => Assert.False(source.Indexed));
        Assert.NotNull(state.Construction.Evidence![address]);
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("query", Assert.Single(state.Requirements!.Inputs!).Name);
        Assert.False(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task FileContentsAreAnObligationWithoutInventingAPublicReturn()
    {
        const string prompt = "Write a local file containing decision, findings and exact command results, then remove the disposable workspace.";
        var state = await Advance(Fresh(prompt), new() { Respond = (request, _) => Wire(request, CurrentPlanningFixture.Intent(Context(request))) });
        Assert.Empty(state.Diagnostics);
        Assert.Empty(state.Requirements!.Outputs!);
        Assert.Equal(prompt, Assert.Single(state.Requirements.Outcomes).Description);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
    }

    [Fact]
    public async Task RecursiveDefaultsAndExplicitNullRemainDistinctFromNoDefault()
    {
        var inputs = Declarations(new TaskInput() { Name = "query", Type = new() { Kind = "string", Enum = ["all", "available"] } },
            new() { Name = "fallback", Required = false, Type = new() { Kind = "string", Nullable = true }, Default = new() { Kind = "null" } },
            new() { Name = "values", Required = false, Type = new() { Kind = "array", Items = new() { Kind = "number" } },
                Default = new() { Kind = "array", Items = [new() { Kind = "number", Number = 2 }] } });
        var state = await Advance(Fresh(), new() { Respond = (request, _) => Wire(request, CurrentPlanningFixture.Intent(Context(request), inputs)) });
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.Requirements!.Inputs![0].Default);
        Assert.Equal("null", state.Requirements.Inputs[1].Default!.Kind);
        Assert.Equal(2, Assert.Single(state.Requirements.Inputs[2].Default!.Items).Number);
        Assert.Null(state.Requirements.Inputs[2].Type.MaxItems);
    }

    [Fact]
    public async Task RetainedFullFrenchRequestKeepsAllCloneRestrictionsBeforeSelectionAndDrafting()
    {
        var retained = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Intent", "retained-review-pr-9c1d9303.json"), PlannerFixture.Ct))!;
        var original = retained["prompt"]!.ToString();
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(original) > 1800);
        var state = await Advance(Fresh(original), new() { Respond = (request, _) => Wire(request, CurrentPlanningFixture.Intent(Context(request),
            Declarations(new TaskInput() { Name = "pullRequestUrl", Type = new() { Kind = "string" } }, new() { Name = "reviewText", Type = new() { Kind = "string" } }))) });
        Assert.Empty(state.Diagnostics);
        Assert.Single(state.Construction!.Compact!.Sources, source => source.Kind == "page");
        var selection = PlanningCompactConstruction.SelectionContext(state).ToJsonString();
        foreach (var required in new[] { "branch=", "targetDirectory=", "review.json", "LOCAUX", "unique", "même en cas" })
            Assert.Contains(required, System.Text.RegularExpressions.Regex.Unescape(selection), StringComparison.Ordinal);
        var contract = retained["cloneContract"]!.Deserialize(PlanningJsonContext.Default.PlanningCapability)!;
        var faulty = retained["faultyClone"]!.Deserialize(PlanningJsonContext.Default.PlanTask)!;
        state.Catalog = new() { Capabilities = [contract], AllowedStepTypes = ["mcp.call"] };
        state.Plan = new() { Inputs = state.Requirements!.Inputs!.ToList() };
        var faultyPlan = PlanningConstruction.Clone(state.Plan);
        faultyPlan.Root.Tasks.Add(faulty);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(faultyPlan, state.Catalog, state.Request, new HashSet<string> { "/root" }).Diagnostics);
        Assert.False(CloneMatchesOriginal(faulty, retained["firstClone"]!));
        state.Construction.Compact.CurrentStep = new() { Id = "clone", Kind = "operation", Scope = "/root", Objective = "Clone the exact repository, revision and directory.",
            RequirementIds = ["req_0"], OperationIds = [contract.Id], BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        var operation = TaskOperations.Describe(contract);
        PlanningConstruction.LoadEvidence(state, ["schema:" + operation.Id + "@" + operation.Version + ":/input/properties/branch"]);
        var draft = PlanningConstruction.DraftRequest(state, false);
        var context = Context(draft);
        var originals = string.Concat(context["requirements"]!.AsArray().Select(row => row!["original"]!.ToString()));
        Assert.Equal(original, originals);
        Assert.Contains(retained["firstClone"]!["branch"]!.ToString(), originals, StringComparison.Ordinal);
        Assert.Contains(retained["firstClone"]!["targetDirectory"]!.ToString(), originals, StringComparison.Ordinal);
        var corrected = faultyPlan.Root.Tasks[0];
        corrected.Inputs = new[] { "remoteUrl", "branch", "targetDirectory" }.Select(name =>
            new TaskOutput(name, new() { Kind = "string", Text = retained["firstClone"]![name]!.ToString() })).ToList();
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            return Wire(request, new JsonObject { ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension
                { Scope = "/root", Tasks = [corrected], RequirementIds = ["req_0"] }, PlanningJsonContext.Default.PlanningConstructionExtension) });
        } };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var candidate = state.Construction!.Candidate!;
        Assert.True(CloneMatchesOriginal(Assert.Single(candidate.Plan.Root.Tasks), retained["firstClone"]!));
        Assert.False(candidate.BusinessReviewed);
        Assert.Null(state.ApprovedHash);
        Assert.Equal(2, state.ModelCalls);
    }

    [Theory]
    [InlineData("remoteUrl")]
    [InlineData("branch")]
    [InlineData("targetDirectory")]
    public async Task IndependentCloneExpectationRejectsWrongMeaningMissingRevisionAndWrongDirectory(string wrong)
    {
        var retained = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Intent", "retained-review-pr-9c1d9303.json"), PlannerFixture.Ct))!;
        var task = retained["faultyClone"]!.Deserialize(PlanningJsonContext.Default.PlanTask)!;
        task.Inputs = new[] { "remoteUrl", "branch", "targetDirectory" }.Select(name =>
            new TaskOutput(name, new() { Kind = "string", Text = retained["firstClone"]![name]!.ToString() })).ToList();
        Assert.True(CloneMatchesOriginal(task, retained["firstClone"]!));
        if (wrong == "branch") task.Inputs.RemoveAll(input => input.Name == wrong);
        else task.Inputs[task.Inputs.FindIndex(input => input.Name == wrong)] = new(wrong, wrong == "remoteUrl"
            ? new() { Kind = "input", Source = "pullRequestUrl" } : new() { Kind = "string", Text = "workflows/smartguide-pr-610" });
        Assert.False(CloneMatchesOriginal(task, retained["firstClone"]!));

    }

    private static bool CloneMatchesOriginal(PlanTask task, JsonNode expected) =>
        new[] { "remoteUrl", "branch", "targetDirectory" }.All(name => task.Inputs.Count(input => input.Name == name &&
            input.Value.Kind == "string" && input.Value.Text == expected[name]!.ToString()) == 1);
}
