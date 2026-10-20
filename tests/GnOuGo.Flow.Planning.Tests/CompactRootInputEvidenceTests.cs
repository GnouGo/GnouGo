using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactRootInputEvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclarationClosureReviewsExactRootInputsWithoutInventingImportedOperands(bool recover)
    {
        var state = await State();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var inputs = JsonSerializer.SerializeToNode(state.Plan!.Inputs, PlanningJsonContext.Default.ListTaskInput);
        var runtime = new TestRuntime { Respond = (_, _) => Review("scope:/root") };
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
                return Review("scope:/root");
            } };
            state = await Advance(state, runtime);
            Assert.Equal(issuedCalls, state.ModelCalls);
        }
        else state = await Advance(state, runtime);

        Assert.Empty(state.Diagnostics);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.True(JsonNode.DeepEquals(inputs, state.Construction.Evidence!["scope:/root"]!["inputs"]));
        var calls = state.ModelCalls;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            Assert.Empty(context["interfaces"]!.AsArray());
            Assert.True(context["constructionStage"]!["requirementCompletionClaimed"]!.GetValue<bool>());
            Assert.False(context["constructionStage"]!["planCompletionClaimed"]!.GetValue<bool>());
            var declared = context["evidence"]!["scope:/root"]!["inputs"]!;
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
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.True(JsonNode.DeepEquals(inputs, JsonSerializer.SerializeToNode(state.Plan!.Inputs, PlanningJsonContext.Default.ListTaskInput)));
        Assert.Equal("closed", state.Construction!.Requirements.Single(requirement => requirement.Id == "declare").Status);
        Assert.Equal("remaining", state.Construction.Requirements.Single(requirement => requirement.Id == "work").Status);
        Assert.False(state.Construction.Complete);
        Assert.Empty(Assert.Single(state.Construction.Checkpoints).TaskIds);
        Assert.Empty(state.Plan!.Root.Tasks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootDeclarationsAreFreshEvidenceOnlyAndDoNotEnterIsolatedGroupInputs(bool historical)
    {
        var state = await State();
        if (historical)
        {
            state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
            state.Construction!.Limits.PhasePolicy = null;
        }
        var accepted = PlanningConstruction.Hash(state.Plan);
        PlanningConstruction.LoadEvidence(state, ["scope:/root", "scope:/groups/isolated/body"]);
        var root = state.Construction!.Evidence!["scope:/root"]!;
        var group = state.Construction.Evidence["scope:/groups/isolated/body"]!;
        Assert.Equal(!historical, root.AsObject().ContainsKey("inputs"));
        Assert.Null(group["inputs"]);
        Assert.Equal("local", Assert.Single(group["group"]!["inputs"]!.AsArray())!["name"]!.ToString());
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(PlanningCompactConstruction.Context(state, review: true)["interfaces"]!.AsArray());
    }

    private static async Task<PlanningSession> State()
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
        construction.OpenScopes = ["/root", "/groups/isolated/body"];
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        compact.CurrentStep = new() { Id = "close_declaration", Kind = "close", Scope = "/root", BaseHash = PlanningConstruction.Hash(state.Plan),
            Objective = "Confirm the declared caller input interface.", RequirementIds = ["declare"], CloseRequirementIds = ["declare"], DiscoveryComplete = true };
        construction.Candidate = PlanningConstruction.Apply(state, new() { RequirementIds = ["declare"] });
        return state;
    }

    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Review(string? address = null)
    {
        var response = new JsonObject { ["findings"] = address is null ? new JsonArray() : null,
            ["evidenceRequests"] = address is null ? null : new JsonArray(address), ["uncertainty"] = "Execution remains unverified." };
        return new() { Json = response, Text = response.ToJsonString() };
    }
}
