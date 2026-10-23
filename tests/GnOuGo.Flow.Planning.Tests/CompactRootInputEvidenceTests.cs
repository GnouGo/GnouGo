using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactRootInputEvidenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeclarationClosureReviewsExactRootInputsWithoutInventingImportedOperands(bool recover, bool acceptedPlan)
    {
        var state = await State(acceptedPlan);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var candidate = PlanningConstruction.Hash(state.Construction!.Candidate!.Plan);
        var inputs = JsonSerializer.SerializeToNode(state.Requirements!.Inputs!, PlanningJsonContext.Default.ListTaskInput);
        foreach (var intermediate in new[] { false, true })
        {
            var review = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate);
            AssertDeclarationNavigation(review.Context);
            Assert.Contains("exact input or scope evidence", review.Request.Prompt, StringComparison.Ordinal);
            Assert.True(HybridWorkflowPlanner.ConstructionReviewEvidenceGrounded(
                review.Context, "/inputDeclarations", compact: true));
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(
                DeclaredEvidence(review.Context).Json!, review.Request.StructuredOutputSchema!));
        }
        var runtime = new TestRuntime { Respond = (request, _) => DeclaredEvidence(Context(request)) };
        if (recover)
        {
            runtime.Respond = (_, _) => throw new OperationCanceledException();
            _ = await Advance(state, runtime);
            state = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint =>
                checkpoint.Status == PlanningStatus.Generating && checkpoint.PendingCall is not null));
            var exact = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
            var pendingHash = state.Construction!.PendingReviewHash;
            var issuedCalls = state.ModelCalls;
            runtime = new TestRuntime { Respond = (request, _) =>
            {
                Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                Assert.Equal(pendingHash, state.Construction.PendingReviewHash);
                return DeclaredEvidence(Context(request));
            } };
            state = await Advance(state, runtime);
            Assert.Equal(issuedCalls, state.ModelCalls);
        }
        else state = await Advance(state, runtime);

        Assert.Empty(state.Diagnostics);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.True(JsonNode.DeepEquals(inputs![0], state.Construction.Evidence!["input:values"]));
        Assert.True(JsonNode.DeepEquals(inputs[1], state.Construction.Evidence["input:label"]));
        var calls = state.ModelCalls;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            Assert.Empty(context["interfaces"]!.AsArray());
            Assert.True(context["constructionStage"]!["requirementCompletionClaimed"]!.GetValue<bool>());
            Assert.False(context["constructionStage"]!["planCompletionClaimed"]!.GetValue<bool>());
            var declared = new JsonArray(context["evidence"]!["input:values"]!.DeepClone(),
                context["evidence"]!["input:label"]!.DeepClone());
            Assert.True(JsonNode.DeepEquals(inputs, declared));
            Assert.True(declared[0]!["required"]!.GetValue<bool>());
            Assert.Equal("number", declared[0]!["type"]!["items"]!["kind"]!.ToString());
            Assert.Equal(0, declared[0]!["type"]!["minItems"]!.GetValue<int>());
            Assert.Equal(32, declared[0]!["type"]!["maxItems"]!.GetValue<int>());
            Assert.True(declared[1]!["type"]!["nullable"]!.GetValue<bool>());
            Assert.False(declared[1]!["required"]!.GetValue<bool>());
            Assert.Equal("null", declared[1]!["default"]!["kind"]!.ToString());
            Assert.Empty(context["draft"]!["tasks"]!.AsArray());
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(
                Review().Json!, request.StructuredOutputSchema!));
            Assert.InRange(PlanningRequestMeasurements.Measure(request, purpose, 8000).EstimatedInputTokens, 1, 8000);
            return Review();
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(candidate, PlanningConstruction.Hash(state.Plan));
        Assert.True(JsonNode.DeepEquals(inputs, JsonSerializer.SerializeToNode(state.Plan!.Inputs, PlanningJsonContext.Default.ListTaskInput)));
        Assert.Equal("closed", state.Construction!.Requirements.Single(requirement => requirement.Id == "declare").Status);
        Assert.Equal("remaining", state.Construction.Requirements.Single(requirement => requirement.Id == "work").Status);
        Assert.False(state.Construction.Complete);
        Assert.Empty(Assert.Single(state.Construction.Checkpoints).TaskIds);
        Assert.Empty(state.Plan!.Root.Tasks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RootDeclarationsAreFreshEvidenceOnlyAndDoNotEnterIsolatedGroupInputs(int? presentation)
    {
        var state = await State();
        if (presentation is { } version) state.Construction!.Compact!.PresentationVersion = version;
        else
        {
            state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
            state.Construction!.Limits.PhasePolicy = null;
        }
        var accepted = PlanningConstruction.Hash(state.Plan);
        PlanningConstruction.LoadEvidence(state, ["scope:/root", "scope:/groups/isolated/body"]);
        var root = state.Construction!.Evidence!["scope:/root"]!;
        var group = state.Construction.Evidence["scope:/groups/isolated/body"]!;
        Assert.Equal(presentation is not null, root.AsObject().ContainsKey("inputs"));
        Assert.Null(group["inputs"]);
        Assert.Equal("local", Assert.Single(group["group"]!["inputs"]!.AsArray())!["name"]!.ToString());
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        var context = PlanningCompactConstruction.Context(state, review: true);
        Assert.Empty(context["interfaces"]!.AsArray());
        Assert.Equal(presentation == 3, context.ContainsKey("inputDeclarations"));
        Assert.Equal(presentation == 3, context.ContainsKey("scopeEvidence"));
        Assert.Null(PlanningCompactConstruction.Context(state)["inputDeclarations"]);
        state.Construction.Compact!.CurrentStep!.Scope = "/groups/isolated/body";
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/groups/isolated/body", RequirementIds = ["declare"] });
        var isolated = PlanningCompactConstruction.Context(state, review: true);
        Assert.Null(isolated["inputDeclarations"]);
        Assert.Empty(isolated["interfaces"]!.AsArray());
        if (presentation == 3) Assert.Equal("scope:/groups/isolated/body", isolated["scopeEvidence"]!.ToString());
        Assert.Equal("local", Assert.Single(isolated["evidence"]!["scope:/groups/isolated/body"]!["group"]!["inputs"]!.AsArray())!["name"]!.ToString());
    }

    [Fact]
    public async Task ClosureDeclarationNavigationStaysBoundedAndRetrievesExactLaterInputs()
    {
        var state = await State();
        var declarations = Enumerable.Range(0, 20).Select(index => new TaskInput
        {
            Name = "parameter_" + index,
            Type = new() { Kind = "string", Enum = ["exact value " + index] }
        }).ToList();
        state.Plan!.Inputs = declarations;
        state.Requirements!.Inputs = declarations;
        var step = state.Construction!.Compact!.CurrentStep!;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { RequirementIds = ["declare"] });
        var request = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false).Request;
        declarations[1].Type.Enum = [new string('x', 16000)];
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { RequirementIds = ["declare"] });
        var giant = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false).Request;
        var context = Context(giant);
        Assert.True(JsonNode.DeepEquals(Context(request)["inputDeclarations"], context["inputDeclarations"]));
        Assert.DoesNotContain(new string('x', 100), giant.Prompt, StringComparison.Ordinal);
        Assert.Empty(context["interfaces"]!.AsArray());
        Assert.Equal(8, context["inputDeclarations"]!["inputs"]!.AsArray().Count);
        Assert.Equal(20, context["inputDeclarations"]!["count"]!.GetValue<int>());
        Assert.Equal("page:interfaces:8", context["inputDeclarations"]!["next"]!.ToString());
        var measurement = PlanningRequestMeasurements.Measure(giant, PlanningConstruction.LocalPurpose, 8000);
        Assert.InRange(measurement.EstimatedInputTokens, 1, 8000);
        Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(component => component.SerializedBytes));
        Assert.True(measurement.Components.Single(component => component.Component == "interfaces").SerializedBytes >
            context["inputDeclarations"]!.ToJsonString().Length);
        PlanningConstruction.LoadEvidence(state, [context["inputDeclarations"]!["next"]!.ToString()]);
        context = PlanningCompactConstruction.Context(state, review: true);
        Assert.Equal(8, context["inputDeclarations"]!["offset"]!.GetValue<int>());
        var entry = context["inputDeclarations"]!["inputs"]![0]!;
        Assert.Equal("parameter_8", entry["input"]!.ToString());
        var address = entry["evidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(declarations[8], PlanningJsonContext.Default.TaskInput),
            state.Construction.Evidence![address]));
        var firstPage = context["inputDeclarations"]!["directory"]!.ToString();
        Assert.Equal("page:interfaces:0", firstPage);
        PlanningConstruction.LoadEvidence(state, [firstPage]);
        context = PlanningCompactConstruction.Context(state, review: true);
        Assert.Equal(0, context["inputDeclarations"]!["offset"]!.GetValue<int>());
        Assert.Null(context["inputDeclarations"]!["directory"]);
        var first = context["inputDeclarations"]!["inputs"]![0]!["evidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [first]);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(declarations[0], PlanningJsonContext.Default.TaskInput),
            state.Construction.Evidence![first]));
        state.Construction.Compact!.CurrentStep!.CloseRequirementIds.Clear();
        Assert.Null(PlanningCompactConstruction.Context(state, review: true)["inputDeclarations"]);
    }

    private static LLMResponse DeclaredEvidence(JsonObject context)
    {
        AssertDeclarationNavigation(context);
        return Review(context["inputDeclarations"]!["inputs"]!.AsArray().Select(input => input!["evidence"]!.ToString()).ToArray());
    }

    private static void AssertDeclarationNavigation(JsonObject context)
    {
        Assert.Empty(context["interfaces"]!.AsArray());
        Assert.Equal("scope:/root", context["scopeEvidence"]!.ToString());
        var declarations = context["inputDeclarations"]!;
        Assert.Equal("/root", declarations["scope"]!.ToString());
        Assert.Equal(2, declarations["count"]!.GetValue<int>());
        Assert.Null(declarations["next"]);
        Assert.Equal(new[] { "values", "label" }, declarations["inputs"]!.AsArray().Select(input => input!["input"]!.ToString()));
        Assert.Equal("array", declarations["inputs"]![0]!["typeKind"]!.ToString());
        Assert.Equal("input:values", declarations["inputs"]![0]!["evidence"]!.ToString());
    }

    private static async Task<PlanningSession> State(bool acceptedPlan = true)
    {
        const string requirement = "Accept a required numeric array and an optional nullable label; process the values afterward.";
        var state = new PlanningSession { Request = new() { TenantId = "root-input-evidence", Prompt = requirement } };
        PlanningSessionCreation.Capture(state);
        state.IntentVersion = 3; state.Revision = 1; state.Status = PlanningStatus.Generating;
        var construction = state.Construction!;
        var compact = construction.Compact!;
        PlanningCompactConstruction.InitializeSources(state);
        foreach (var source in compact.Sources) source.Indexed = true;
        compact.RequirementSources["declare"] = [compact.Sources[0].Id];
        compact.RequirementSources["work"] = [compact.Sources[0].Id];
        state.Plan = new()
        {
            Inputs =
            [
                new() { Name = "values", Type = new() { Kind = "array", MinItems = 0, MaxItems = 32, Items = new() { Kind = "number" } } },
                new() { Name = "label", Required = false, Type = new() { Kind = "string", Nullable = true }, Default = new() { Kind = "null" } }
            ],
            Groups = [new() { Id = "isolated", Inputs = [new() { Name = "local", Type = new() { Kind = "number" } }] }]
        };
        state.Requirements = new() { Inputs = state.Plan.Inputs, Outputs = [],
            Outcomes = [new("declare", "Accept the typed caller inputs."), new("work", "Process the values afterward.")] };
        construction.Requirements = [new() { Id = "declare" }, new() { Id = "work" }];
        if (!acceptedPlan) state.Plan = null;
        construction.OpenScopes = acceptedPlan ? ["/root", "/groups/isolated/body"] : ["/root"];
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        compact.CurrentStep = new() { Id = "close_declaration", Kind = "close", Scope = "/root", BaseHash = PlanningConstruction.Hash(state.Plan),
            Objective = "Confirm the declared caller input interface.", RequirementIds = ["declare"], CloseRequirementIds = ["declare"], DiscoveryComplete = true };
        construction.Candidate = PlanningConstruction.Apply(state, new() { RequirementIds = ["declare"] });
        return state;
    }

    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Review(params string[] addresses)
    {
        var response = new JsonObject { ["findings"] = addresses.Length == 0 ? new JsonArray() : null,
            ["evidenceRequests"] = addresses.Length == 0 ? null : new JsonArray(addresses.Select(address => (JsonNode?)JsonValue.Create(address)).ToArray()),
            ["uncertainty"] = "Execution remains unverified." };
        return new() { Json = response, Text = response.ToJsonString() };
    }
}
