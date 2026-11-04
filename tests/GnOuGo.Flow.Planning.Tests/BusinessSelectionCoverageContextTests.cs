using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessSelectionCoverageContextTests
{
    [Fact]
    public async Task AbandonedCompletionRetainsCurrentCoverageGapWithoutTechnicalDetailsOrNewAuthority()
    {
        var (state, runtime) = await PreparedGroup();
        var accepted = JsonSerializer.Serialize(state.Construction!.Checkpoints, PlanningJsonContext.Default.ListPlanningConstructionCheckpoint);
        var plan = PlanningConstruction.Hash(state.Plan);
        Assert.False(CompletionEligibilityTests.OffersCompletion(state));
        // An explicitly malformed closure still reaches ordinary final validation.
        state.Construction.Compact!.CurrentStep = new() { Id = "invalid-close", Kind = "close", Scope = "/root",
            RequirementIds = ["later"], CloseRequirementIds = ["later"], CloseScopes = ["/root"],
            BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Contains(state.Diagnostics, finding => finding.Code == PlanningCommitments.MissingOperation);
        PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection
        {
            Disposition = "abandon", Reason = "technical/private/remote_calculation remains unreachable"
        });
        state.Diagnostics.Clear();
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.SelectionContext(state);
        Assert.NotNull(context["currentPlanFindings"]);
        var finding = Assert.Single(context["currentPlanFindings"]!.AsArray())!;
        Assert.Equal("later", finding["requirementId"]!.ToString());
        Assert.Equal("Declared required behavior has no reachable invocation in the current workflow.", finding["finding"]!.ToString());
        foreach (var value in new[] { "remote_calculation", "prepared", "technical/private", "/root", "completion", "operationIds" })
            Assert.DoesNotContain(value, finding.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Null(context["unresolvedNeeds"]);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            var issued = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            Assert.Equal("later", issued["currentPlanFindings"]![0]!["requirementId"]!.ToString());
            return new() { Json = new JsonObject { ["action"] = new JsonObject { ["kind"] = "block", ["block"] = new JsonObject
            {
                ["objective"] = "Deliver the remaining original result", ["requirementIds"] = new JsonArray("later"),
                ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray("The requested result"),
                ["dependencies"] = new JsonArray(new JsonObject { ["resultId"] = runtime.EvidenceId(request, "result:block_2"), ["reason"] = "Reuse the accepted contribution" }),
                ["successCriteria"] = new JsonArray("The remaining original result is supplied")
            } } } };
        };
        var next = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(next.Diagnostics);
        Assert.Equal("later", Assert.Single(PlanningCompactConstruction.CurrentBlock(next)!.RequirementIds));
        Assert.Equal(plan, PlanningConstruction.Hash(next.Plan));
        Assert.Equal(accepted, JsonSerializer.Serialize(next.Construction!.Checkpoints, PlanningJsonContext.Default.ListPlanningConstructionCheckpoint));
        Assert.Null(next.ApprovedHash);
        state.Construction!.Requirements.Single(requirement => requirement.Id == "later").Status = "closed";
        Assert.Null(PlanningCompactConstruction.SelectionContext(state)["currentPlanFindings"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReachableCallsRemoveOnlyTheMissingInvocationFactWithoutClaimingExecution(bool conditional)
    {
        var (state, _) = await PreparedGroup();
        AddCall(state, conditional);
        Assert.DoesNotContain(PlanningCommitments.Assess(state).SelectMany(result => result.Diagnostics),
            diagnostic => diagnostic.Code == PlanningCommitments.MissingOperation);
        Assert.Null(PlanningCompactConstruction.SelectionContext(state)["currentPlanFindings"]);
        Assert.All(PlanningCompactConstruction.SelectionContext(state)["availableResults"]!.AsArray(),
            result => Assert.False(result!["executionObserved"]!.GetValue<bool>()));
        if (conditional) Assert.Contains(PlanningCommitments.Assess(state), result => result.Outcome == "unverified");
    }

    [Fact]
    public async Task DisconnectedPublicResultRemainsVisibleUntilActualProducerLineageIsRestored()
    {
        var (state, _) = await PreparedGroup();
        AddCall(state, conditional: false);
        state.Requirements!.Outputs = [new() { Name = "result", Required = true, Type = new() { Kind = "number" } }];
        state.Requirements.Outcomes[1] = state.Requirements.Outcomes[1] with
        {
            Commitments = [new("remote_calculation") { Result = new("result", "result") }]
        };
        state.Plan!.Root.Outputs = [new("result", new() { Kind = "number", Number = 9 })];
        var context = PlanningCompactConstruction.SelectionContext(state);
        Assert.NotNull(context["currentPlanFindings"]);
        var finding = Assert.Single(context["currentPlanFindings"]!.AsArray())!;
        Assert.Equal("later", finding["requirementId"]!.ToString());
        Assert.Equal("A declared public result is disconnected from its required producer.", finding["finding"]!.ToString());
        state.Plan.Root.Outputs = [new("result", new() { Kind = "output", Source = "invoke", Port = "result" })];
        Assert.Null(PlanningCompactConstruction.SelectionContext(state)["currentPlanFindings"]);
    }

    internal static void AddCall(PlanningSession state, bool conditional)
    {
        PlanTask call = new() { Id = "invoke", Kind = "call", Group = "prepared", Objective = "Use the prepared contribution",
            Inputs = [new("x", new() { Kind = "input", Source = "x" })] };
        state.Plan!.Root.Tasks.Add(conditional ? new() { Id = "guard", Kind = "conditional", Objective = "Conditionally use the prepared contribution", Condition = new() { Kind = "boolean", Boolean = true },
            Body = new() { Tasks = [call] }, Otherwise = new() } : call);
    }

    internal static async Task<(PlanningSession State, TestRuntime Runtime)> PreparedGroup()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        runtime.Respond = (_, _) => new() { Json = BusinessReviewProtocolTests.Report("ok") };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        var capability = new PlanningCapability { Id = "remote_calculation", Kind = "tool", StepType = "mcp.call", Server = "arbitrary", Method = "calculate", EffectKind = "read",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"number"}},"required":["x"],"additionalProperties":false}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"result":{"type":"number"}},"required":["result"],"additionalProperties":false}""")!.AsObject() };
        state.Catalog!.Capabilities.Add(capability); state.Catalog.AllowedStepTypes.Add("mcp.call"); state.Discovery.Resolved.Add(capability);
        state.Requirements!.Outcomes[1] = state.Requirements.Outcomes[1] with { Commitments = [new("remote_calculation")] };
        PlanningCompactConstruction.SelectBlock(state, new JsonObject { ["objective"] = "Supply the later original result", ["requirementIds"] = new JsonArray("later"),
            ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray("The later result"), ["dependencies"] = new JsonArray(),
            ["successCriteria"] = new JsonArray("The later result is supplied") });
        state.Construction!.Compact!.CurrentStep = new() { Id = "prepare", BlockId = state.Construction.Compact.CurrentBlockId,
            Scope = "/root", Kind = "group", BaseHash = PlanningConstruction.Hash(state.Plan), Objective = "Prepare the reusable contribution",
            RequirementIds = ["later"], OperationIds = ["remote_calculation"], DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["later"], CompleteBlock = true,
            Groups = [new() { Id = "prepared", Inputs = [new() { Name = "x", Type = new() { Kind = "number" }, Required = true }],
                Body = new() { Tasks = [new() { Id = "calculate", Kind = "operation", Operation = "remote_calculation", Objective = "Calculate the original numeric result",
                    Inputs = [new("x", new() { Kind = "input", Source = "x" })] }], Outputs = [new("result", new() { Kind = "output", Source = "calculate", Port = "result" })] } }] });
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, state.Construction!.Checkpoints.Count);
        Assert.Equal("business_completion_reviewed", PlanningCompactConstruction.SelectionContext(state)["availableResults"]![1]!["reviewStatus"]!.ToString());
        Assert.Contains(PlanningCommitments.Assess(state).SelectMany(result => result.Diagnostics), diagnostic => diagnostic.Code == PlanningCommitments.MissingOperation);
        return (state, runtime);
    }
}
