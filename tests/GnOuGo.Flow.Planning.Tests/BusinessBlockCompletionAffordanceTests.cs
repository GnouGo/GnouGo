using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessBlockCompletionAffordanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedPlanRemembersWhetherItsFailureDependedOnTheOperationSelection(bool independentFailure)
    {
        var (state, _) = await Partial();
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "sequence"; step.OperationIds = ["refresh"];
        var candidate = state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = step.Scope, RequirementIds = step.RequirementIds, CompleteBlock = true,
            Tasks = [new() { Id = "unrelated", Kind = "value", Objective = "Prepare a literal result.",
                Outputs = [new("number", new() { Kind = "number", Number = 2 })] }]
        });
        candidate.Diagnostics = PlanningCompactConstruction.CandidateFindings(state).ToList();
        Assert.Equal(PlanningCommitments.MissingOperation, Assert.Single(candidate.Diagnostics).Code);
        if (independentFailure) candidate.Diagnostics.Add(new("CONSTRUCTION_BUSINESS_REVIEW", "/construction/candidate", "The result is incomplete."));
        PlanningConstruction.RecordRejection(state, candidate);
        Assert.True(PlanningConstruction.PreviouslyRejected(state, candidate));
        var restored = PlannerFixture.Clone(state);
        var replacement = restored.Construction!.Compact!.CurrentStep!;
        replacement.Id = "renamed";
        Assert.True(PlanningConstruction.PreviouslyRejected(restored, restored.Construction.Candidate!));
        replacement.OperationIds.Clear();
        Assert.Equal(independentFailure, PlanningConstruction.PreviouslyRejected(restored, restored.Construction.Candidate!));
        Assert.True(HybridWorkflowPlanner.NeedsBusinessReview(restored, restored.Construction.Candidate!));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task PublishingAnAcceptedResultCanCloseItsBlockUnderOrdinaryReview(bool acceptedResult, bool closeRequirement)
    {
        var (state, _) = await Partial();
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "sequence"; step.DiscoveryComplete = true; step.OperationIds = ["refresh"];
        step.CloseRequirementIds = closeRequirement ? ["work"] : [];
        step.OutputNames = ["result"];
        state.Requirements!.Outputs = [new() { Name = "result", Type = new() { Kind = "number" } }];
        var binding = acceptedResult ? new TaskValue { Kind = "output", Source = "compute", Port = "result" }
            : new TaskValue { Kind = "number", Number = 1 };
        var candidate = state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = step.Scope, RequirementIds = step.RequirementIds, CompleteBlock = true, Outputs = [new("result", binding)]
        });
        var diagnostics = PlanningCompactConstruction.CandidateFindings(state).ToList();
        Assert.Equal(!acceptedResult, diagnostics.Any(d => d.Code == PlanningCommitments.MissingOperation));
        Assert.True(HybridWorkflowPlanner.NeedsBusinessReview(state, candidate));
        Assert.Empty(state.Plan!.Root.Outputs);
        state.Construction.Compact.PendingCommitments["work"] = [new("refresh")];
        Assert.Contains(PlanningCompactConstruction.CandidateFindings(state), d => d.Code == PlanningCommitments.MissingOperation);
    }

    [Theory]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "declared")]
    [InlineData(false, "unknown")]
    public async Task ContinuedDraftCanSubmitTheAcceptedBlockForReviewWithoutAddingAnUnneededOperation(bool missingPublicOutput, string publicInterface)
    {
        var (state, runtime) = await Partial();
        var step = state.Construction!.Compact!.CurrentStep!;
        var capability = new PlanningCapability
        {
            Id = "refresh", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "records", Method = "refresh",
            Description = "Acquire a new observation when the prior observation is unavailable.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject() },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject() }
        };
        state.Catalog!.Capabilities.Add(capability);
        step.Kind = "sequence"; step.DiscoveryComplete = true; step.OperationIds = [capability.Id];
        if (missingPublicOutput) step.OutputNames = ["missing"];
        if (publicInterface == "declared") state.Requirements!.Outputs = [new() { Name = "later", Type = new() { Kind = "number" } }];
        if (publicInterface == "unknown") state.Requirements!.Outputs = null;
        var baseline = PlanningConstruction.Hash(state.Plan);
        var extension = new PlanningConstructionExtension { CompleteBlock = true, Scope = "/root", RequirementIds = ["work"] };
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            var response = new JsonObject { ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension) };
            var schema = request.StructuredOutputSchema!.AsObject();
            Assert.Equal(!missingPublicOutput && publicInterface == "empty", schema["description"]!.ToString().Contains("zero public outputs", StringComparison.Ordinal));
            var wire = PlanningCorpus.Transport(response, schema, schema)!;
            Assert.Empty(PlanningContractValidation.ValidateInstance(wire, request.StructuredOutputSchema!));
            return new() { Json = wire };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        if (publicInterface == "unknown")
        {
            Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REQUIREMENTS_INVALID" && diagnostic.Required);
            return;
        }
        if (missingPublicOutput)
        {
            Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Required);
            return;
        }
        Assert.Empty(state.Diagnostics);
        var candidate = Assert.IsType<PlanningConstructionCandidate>(state.Construction!.Candidate);
        Assert.Empty(candidate.TaskIds); Assert.Equal(baseline, candidate.Hash);
        Assert.True(HybridWorkflowPlanner.NeedsBusinessReview(state, candidate));
        var committed = PlannerFixture.Clone(state);
        committed.Construction!.Compact!.PendingCommitments["work"] = [new(capability.Id)];
        Assert.Contains(PlanningCompactConstruction.CandidateFindings(committed), finding =>
            finding.Code == PlanningCommitments.MissingOperation && finding.Message.Contains(capability.Id, StringComparison.Ordinal));
        runtime.Respond = (_, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.Construction!.Compact!.CurrentBlockId);
        Assert.Contains("/root", state.Construction.OpenScopes);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedBusinessContributionCanClaimReviewWithoutAddingWorkOrClosingRoot(bool selectedBoundOutput)
    {
        var (state, runtime) = await Partial(selectedBoundOutput);
        var plan = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var block = state.Construction!.Compact!.CurrentBlockId;
        Assert.True(OffersClose(PlanningCompactConstruction.DiscoverySchema(state)));
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.DiscoveryPurpose, purpose);
            return new() { Json = new JsonObject
            {
                ["discoveryRequests"] = null, ["evidenceRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null,
                ["binding"] = new JsonObject { ["kind"] = "close", ["operationIds"] = new JsonArray(), ["groupId"] = null, ["closeScopes"] = new JsonArray() }
            } };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("close", state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.Empty(state.Construction.Compact.CurrentStep.CloseScopes!);
        var calls = runtime.Calls.Count;
        runtime.Respond = (_, _) => throw new InvalidOperationException("Selected unchanged-plan closure needs no drafting echo.");
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(calls, runtime.Calls.Count);
        var candidate = Assert.IsType<PlanningConstructionCandidate>(state.Construction!.Candidate);
        Assert.True(candidate.Extension.CompleteBlock);
        Assert.False(candidate.Extension.Complete);
        Assert.Empty(candidate.TaskIds);
        Assert.Empty(candidate.Extension.CloseScopes);
        Assert.True(HybridWorkflowPlanner.NeedsBusinessReview(state, candidate));
        Assert.Contains(PlanningBusinessProjection.Build(state, global: false)["actions"]!.AsArray(), action => action!["id"]?.ToString() == "compute");
        Assert.Equal(plan, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(plan, JsonSerializer.Serialize(candidate.Plan, PlanningJsonContext.Default.TaskPlan));
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            Assert.Contains("multiply", request.Prompt, StringComparison.Ordinal);
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.Construction!.Compact!.CurrentBlockId);
        Assert.NotNull(state.Construction.Compact.Blocks!.Single(row => row.Id == block).CompletionCheckpoint);
        Assert.Contains("/root", state.Construction.OpenScopes);
        Assert.All(state.Construction.Requirements, requirement => Assert.False(PlanningCompactConstruction.RequirementCompletionClaimed(state, requirement.Id)));
        Assert.Null(state.ApprovedHash);
        Assert.Equal(plan, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("invalidated")]
    [InlineData("empty")]
    [InlineData("wrong_owner")]
    [InlineData("already_complete")]
    [InlineData("stale")]
    public async Task CompletionChoiceRequiresAnUnreviewedOwnedAcceptedContribution(string defect)
    {
        var (state, _) = await Partial();
        var compact = state.Construction!.Compact!;
        var checkpoint = Assert.Single(state.Construction.Checkpoints);
        switch (defect)
        {
            case "unrelated": checkpoint.Step!.BlockId = "other"; break;
            case "invalidated": checkpoint.InvalidatedTaskIds.Add("compute"); break;
            case "empty": checkpoint.TaskIds.Clear(); break;
            case "wrong_owner": compact.CurrentStep!.BlockId = "other"; break;
            case "already_complete": compact.Blocks![0].CompletionCheckpoint = checkpoint.Sequence; break;
            case "stale": compact.CurrentStep!.BaseHash = "stale"; break;
        }
        Assert.False(OffersClose(PlanningCompactConstruction.DiscoverySchema(state)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    public async Task PublicOutputSelectionCannotManufactureAnUnchangedBinding(string defect)
    {
        var (state, _) = await Partial(selectedBoundOutput: true);
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "close"; step.DiscoveryComplete = true; step.CloseScopes = [];
        if (defect == "missing") step.OutputNames = ["not_bound"];
        else state.Plan!.Root.Outputs.Add(new("result", new() { Kind = "number", Number = 123 }));
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        var before = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        Assert.Null(PlanningCompactConstruction.PrepareSelectedClosure(state));
        Assert.Equal(before, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Null(state.Construction.Candidate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalResultChainCanFinishAcceptedWorkWithoutAnExploratoryOperation(bool consumesAcceptedResult)
    {
        var (state, _) = await Partial();
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "sequence"; step.DiscoveryComplete = true; step.OperationIds = ["refresh"];
        var extension = new PlanningConstructionExtension
        {
            Scope = step.Scope, RequirementIds = step.RequirementIds, CompleteBlock = true,
            Tasks = [
                new() { Id = "normalize", Kind = "value", Objective = "Normalize the doubled result.", DependsOn = ["compute"],
                    Outputs = [new("result", consumesAcceptedResult ? new() { Kind = "output", Source = "compute", Port = "result" }
                        : new() { Kind = "number", Number = 1 })] },
                new() { Id = "prepared", Kind = "value", Objective = "Return the normalized result.",
                    Outputs = [new("result", new() { Kind = "output", Source = "normalize", Port = "result" })] }
            ]
        };
        var candidate = state.Construction.Candidate = PlanningConstruction.Apply(state, extension);
        var findings = PlanningCompactConstruction.CandidateFindings(state).ToList();
        Assert.Equal(!consumesAcceptedResult, findings.Any(finding => finding.Code == PlanningCommitments.MissingOperation));
        Assert.True(HybridWorkflowPlanner.NeedsBusinessReview(state, candidate));
        state.Construction.Compact.PendingCommitments["work"] = [new("refresh")];
        Assert.Contains(PlanningCompactConstruction.CandidateFindings(state), finding => finding.Code == PlanningCommitments.MissingOperation);
    }

    [Theory]
    [InlineData("total", true, true)]
    [InlineData("unrelated", false, true)]
    [InlineData(null, false, true)]
    [InlineData("total", true, false)]
    [InlineData("unrelated", false, false)]
    [InlineData(null, false, false)]
    public async Task DerivedLocalResultsCanProposeTheirPublicExportInTheSameReview(string? port, bool derivedExport, bool deferred)
    {
        var (state, _) = await Partial();
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "sequence"; step.DiscoveryComplete = true; step.OperationIds = ["refresh"];
        if (!deferred) step.OutputNames = ["report"];
        state.Requirements!.Outputs = [new() { Name = "report", Type = new() { Kind = "number" } }];
        var export = port is not null ? new TaskValue { Kind = "output", Source = "projected", Port = port }
            : new TaskValue { Kind = "number", Number = 1 };
        var candidate = state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = step.RequirementIds, CompleteBlock = true,
            Tasks = [new() { Id = "prepared", Kind = "value", Objective = "Prepare the observed total.",
                Outputs = [new("total", new() { Kind = "output", Source = "compute", Port = "result" }),
                    new("unrelated", new() { Kind = "number", Number = 1 })] },
                new() { Id = "projected", Kind = "value", Objective = "Project both fields.",
                    Outputs = [new("total", new() { Kind = "output", Source = "prepared", Port = "total" }),
                        new("unrelated", new() { Kind = "output", Source = "prepared", Port = "unrelated" })] }],
            Outputs = deferred ? [] : [new("report", export)],
            BoundaryTransitions = deferred ? [new() { Scope = "/root", RequirementIds = step.RequirementIds,
                Outputs = [new("report", export)] }] : null
        });
        Assert.Empty(PlanningCompactConstruction.ExtensionFindings(state, candidate));
        Assert.Equal(!derivedExport, PlanningCompactConstruction.CandidateFindings(state)
            .Any(diagnostic => diagnostic.Code == PlanningCommitments.MissingOperation));
        Assert.True(HybridWorkflowPlanner.NeedsBusinessReview(state, candidate));
        Assert.Empty(state.Plan!.Root.Outputs);
        Assert.Equal(!deferred, candidate.Plan.Root.Outputs.Any(output => output.Name == "report"));
        state.Construction.Compact.PendingCommitments["work"] = [new("refresh")];
        Assert.Contains(PlanningCompactConstruction.CandidateFindings(state), diagnostic => diagnostic.Code == PlanningCommitments.MissingOperation);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Partial(bool selectedBoundOutput = false)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate!.Extension.CompleteBlock = false;
        if (selectedBoundOutput)
        {
            state.Requirements!.Outputs = [new() { Name = "result", Type = new() { Kind = "number" } }];
            state.Construction.Compact!.CurrentStep!.OutputNames = ["result"];
            var extension = state.Construction.Candidate.Extension;
            extension.Outputs = [new("result", new() { Kind = "output", Source = "compute", Port = "result" })];
            state.Construction.Candidate = PlanningConstruction.Apply(state, extension);
        }
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Single(state.Construction!.Checkpoints);
        state.Construction.Compact!.CurrentStep = new()
        {
            Id = "finish_business_block", BlockId = state.Construction.Compact.CurrentBlockId, Scope = "/root",
            BaseHash = PlanningConstruction.Hash(state.Plan), Objective = "Request review of the accepted doubled result.",
            RequirementIds = ["work"], ExpectedOutputs = ["The accepted doubled result"], CloseRequirementIds = [],
            OutputNames = selectedBoundOutput ? ["result"] : []
        };
        return (state, runtime);
    }

    private static bool OffersClose(JsonNode schema)
    {
        bool Visit(JsonNode? node) => node switch
        {
            JsonArray array => array.Any(Visit),
            JsonObject obj => obj["properties"]?["kind"]?["enum"] is JsonArray kinds && kinds.Any(kind => kind?.ToString() == "close") || obj.Any(pair => Visit(pair.Value)),
            _ => false
        };
        return Visit(schema["properties"]!["binding"]);
    }
}
