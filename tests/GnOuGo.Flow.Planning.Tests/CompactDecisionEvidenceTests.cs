using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactConvergentJudgeTests
{
    [Fact]
    public async Task ReviewCompactionMovesOnlyCheckpointNavigationBehindExactTaskEvidence()
    {
        var state = await DecisionState();
        var context = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false).Context;
        var requirement = context["requirements"]!.AsArray()[0]!;
        var progress = new JsonObject
        {
            ["basis"] = "accepted_checkpoint_associations_not_fulfillment", ["checkpointCount"] = 4,
            ["items"] = new JsonArray(Enumerable.Range(1, 4).Select(index => (JsonNode)new JsonObject
            {
                ["sequence"] = index, ["evidence"] = "page:checkpoints:" + (index - 1),
                ["tasks"] = new JsonArray(new JsonObject { ["task"] = "accepted_label", ["evidence"] = "task:accepted_label",
                    ["scope"] = "/root", ["kind"] = "value", ["objective"] = new string('x', 220),
                    ["ports"] = new JsonArray("value"), ["portCount"] = 1 })
            }).ToArray())
        };
        requirement["acceptedProgress"] = progress;
        var before = context.DeepClone().AsObject();
        var bytes = System.Text.Encoding.UTF8.GetByteCount(context.ToJsonString());
        PlanningCompactConstruction.CompactReviewNavigation(context);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(context.ToJsonString()) < bytes - 800);
        foreach (var field in new[] { "original", "sources", "commitments" })
            Assert.True(JsonNode.DeepEquals(before["requirements"]![0]![field], requirement[field]));
        foreach (var field in new[] { "draft", "interfaces", "operations", "inputDeclarations", "ancestorControls", "sourceCoverage", "reviewPropositions", "evidence" })
            Assert.True(JsonNode.DeepEquals(before[field], context[field]), field);
        foreach (var item in progress["items"]!.AsArray())
        {
            var task = item!["tasks"]![0]!;
            Assert.Null(task["objective"]); Assert.True(task["detailsOmitted"]!.GetValue<bool>());
            Assert.Equal("task:accepted_label", task["evidence"]!.ToString());
        }
        var once = context.ToJsonString();
        PlanningCompactConstruction.CompactReviewNavigation(context);
        Assert.Equal(once, context.ToJsonString());
        PlanningConstruction.LoadEvidence(state, ["task:accepted_label"]);
        Assert.Equal("accepted_label", state.Construction!.Evidence!["task:accepted_label"]!["id"]!.ToString());
        Assert.False(state.Construction.Candidate!.LocalReviewed);
        Assert.False(PlanningConstructionReviewCoverage.Complete(state, state.Construction.Candidate));
    }

    [Fact]
    public async Task DecisionReviewOffersExactTaskEvidenceAndLoadsItWithoutCorrection()
    {
        var state = await DecisionState();
        var baseline = PlanningConstruction.Hash(state.Plan);
        const string address = "task:accepted_label";
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var context = Context(request);
            if (context["evidence"]?[address] is null)
            {
                var evidence = EvidenceAction(address);
                Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(evidence, request.StructuredOutputSchema!));
                Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(
                    EvidenceAction(address + "__final_Response? copy this prose"), request.StructuredOutputSchema!));
                return Response(evidence);
            }
            Assert.Equal("accepted_label", context["evidence"]![address]!["id"]!.ToString());
            Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction(address), request.StructuredOutputSchema!));
            return Response(Report());
        } };
        state = await Advance(state, runtime);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.NotNull(state.Construction.Evidence?[address]);
        Assert.Equal(0, state.ReplanAttempts);
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task ContaminatedJudgeAddressUsesOnlyTheExistingReceivedReportFollowup()
    {
        var state = await DecisionState();
        var runtime = new TestRuntime { Respond = (_, _) => Response(EvidenceAction("task:accepted_label__final_Response? prose")) };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Null(state.Construction!.Evidence);
        Assert.Empty(state.Construction.EvidenceRequests);
        var repair = Assert.Single(state.Construction.Candidate!.Reviews, row => row["reportRepair"]?.GetValue<bool>() == true);
        Assert.Equal("ready", repair["reassessment"]!.ToString());
        runtime.Respond = (request, _) =>
        {
            Assert.NotNull(Context(request)["reportRepair"]);
            Assert.DoesNotContain(request.StructuredOutputSchema!["properties"]!["action"]!["anyOf"]!.AsArray(),
                action => action!["properties"]!["kind"]!["enum"]![0]!.ToString() == "evidenceRequests");
            return Response(EvidenceAction("task:accepted_label"));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Null(state.Construction.Evidence);
        Assert.Single(state.Construction.Candidate!.Reviews, row => row["reassessment"] is not null);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task HistoricalJudgeSchemasDoNotAcquireDecisionEvidenceEnums(int presentation)
    {
        var state = await State();
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        var request = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false).Request;
        var original = request.StructuredOutputSchema!.ToJsonString();
        PlanningCompactConstruction.ConstrainReviewEvidence(state, Context(request), request.StructuredOutputSchema.AsObject());
        Assert.Equal(original, request.StructuredOutputSchema.ToJsonString());
        var response = presentation == 5 ? EvidenceAction("task:accepted_label__prose") : new JsonObject
        {
            ["findings"] = null, ["evidenceRequests"] = new JsonArray("task:accepted_label__prose"), ["uncertainty"] = "Unknown."
        };
        if (presentation == 4) response["inconclusive"] = null;
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema));
    }

    [Fact]
    public async Task DecisionEvidenceWalkerDoesNotPromoteSchemaOrTaskContentIntoLinks()
    {
        var state = await DecisionState();
        var context = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false).Context;
        context["interfaces"] = new JsonArray(new JsonObject
        {
            ["schema"] = new JsonObject { ["description"] = "task:invented", ["evidence"] = "task:invented", ["next"] = "page:tasks:999" },
            ["binding"] = new JsonObject { ["evidence"] = "task:invented" }
        });
        var schema = HybridWorkflowPlanner.ConvergentConstructionReviewSchema(context);
        PlanningCompactConstruction.ConstrainReviewEvidence(state, context, schema);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction("task:invented"), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction("page:tasks:999"), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction("task:accepted_label"), schema));
    }

    [Fact]
    public async Task DecisionJudgeTaskDirectoryOffersOnlyItsCurrentExactContinuation()
    {
        var state = await DecisionState();
        for (var index = 0; index < 16; index++)
            state.Plan!.Root.Tasks.Add(new()
            {
                Id = "retained_" + index, Kind = "value", Objective = "Retain the independent label.",
                Outputs = [new("value", new() { Kind = "input", Source = "label" })]
            });
        var extension = state.Construction!.Candidate!.Extension;
        state.Construction.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, extension);
        var accepted = PlanningConstruction.Hash(state.Plan);
        foreach (var offset in new[] { 0, 8, 16 })
        {
            var address = "page:tasks:" + offset;
            var request = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false).Request;
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction(address), request.StructuredOutputSchema!));
            Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction("page:tasks:999"), request.StructuredOutputSchema!));
            PlanningConstruction.LoadEvidence(state, [address], followup: probe =>
            {
                var next = HybridWorkflowPlanner.CreateConstructionReviewRequest(probe, intermediate: false).Request;
                return PlanningModelCalls.PrepareRequest(probe, PlanningConstruction.LocalPurpose, next.Prompt,
                    next.StructuredOutputSchema!.AsObject(), probe.Construction!.Limits.ReviewOutputTokens);
            }, purpose: PlanningConstruction.LocalPurpose);
            var page = state.Construction.Evidence![address]!;
            Assert.InRange(page["items"]!.AsArray().Count, 1, 8);
            Assert.Equal(offset == 16 ? null : "page:tasks:" + (offset + 8), page["next"]?.ToString());
            request = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false).Request;
            Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction(address), request.StructuredOutputSchema!));
        }
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(0, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(state.Construction.Checkpoints);
    }

    private static JsonObject EvidenceAction(string address) => new()
    {
        ["action"] = new JsonObject { ["kind"] = "evidenceRequests", ["addresses"] = new JsonArray(address) },
        ["uncertainty"] = "The exact supplied original is needed."
    };

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task ReusedOriginalOffersOnlyItsExactExistingAddressWithoutGrantingRepeatProgress(int presentation)
    {
        var state = await DecisionState();
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        const string id = "independent_original";
        const string address = "requirement:" + id;
        const string original = "Retain the caller label without changing its spelling.";
        state.Requirements!.Outcomes.Add(new(id, original));
        state.Construction.Requirements.Add(new() { Id = id });
        var source = new PlanningConstructionSource { Id = "independent_source", Kind = "requirement", Text = original,
            Version = PlanningGraphCompiler.Fingerprint(original), Indexed = true, InterfaceKind = "none" };
        state.Construction.Compact.Sources.Add(source);
        state.Construction.Compact.RequirementSources[id] = [source.Id];
        var context = PlanningCompactConstruction.Context(state, review: true);
        context["requirements"] = new JsonArray(context["requirements"]!.AsArray()
            .Single(row => row!["id"]!.ToString() == "work")!.DeepClone());
        var baseline = Schema();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction(address), baseline));
        context["reusedPropositions"] = new JsonArray(new JsonObject
        {
            ["sourceKey"] = address, ["propositionId"] = "task:invented",
            ["subject"] = new JsonObject { ["evidence"] = "task:invented" }
        }, new JsonObject { ["sourceKey"] = address + " copy this prose" },
            new JsonObject { ["sourceKey"] = "requirement:unknown" }, new JsonObject { ["sourceKey"] = "task:invented" });
        var schema = Schema();
        if (presentation < 9)
        {
            Assert.Equal(baseline.ToJsonString(), schema.ToJsonString());
            return;
        }
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction(address), schema));
        foreach (var invalid in new[] { address + " copy this prose", "requirement:unknown", "task:invented", "source:independent_source" })
            Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction(invalid), schema));
        var planHash = PlanningConstruction.Hash(state.Plan);
        PlanningConstruction.LoadEvidence(state, [address]);
        Assert.Equal(original, state.Construction.Evidence![address]![0]!["original"]!.ToString());
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction(address), Schema()));
        state.Construction.Evidence = null;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(EvidenceAction(address), Schema()));
        Assert.Equal(planHash, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(0, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);

        JsonObject Schema()
        {
            var result = HybridWorkflowPlanner.ConvergentConstructionReviewSchema(context);
            PlanningCompactConstruction.ConstrainReviewEvidence(state, context, result);
            return result;
        }
    }

    [Theory]
    [InlineData(6, "/requirements/0", "source_a,source_b")]
    [InlineData(6, "/requirements/0/original", "source_a,source_b")]
    [InlineData(6, "/globalConstraints", "global_a,global_b")]
    [InlineData(6, "/globalConstraints/1", "global_b")]
    [InlineData(6, "/evidence/requirement:work", "source_a,source_b")]
    [InlineData(6, "/evidence/source:source_b", "source_b")]
    [InlineData(6, "/evidence/source:global_a", "global_a")]
    [InlineData(5, "/requirements/0", "")]
    [InlineData(5, "/globalConstraints", "")]
    public async Task DefectWitnessRetainsOnlyExactCitedOriginalSourceIdentities(int presentation, string obligation, string expected)
    {
        var state = await DecisionState();
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        var context = PlanningCompactConstruction.Context(state, review: true);
        context["requirements"]![0]!["sources"] = new JsonArray(new JsonObject { ["id"] = "source_a", ["version"] = "a" },
            new JsonObject { ["id"] = "source_b", ["version"] = "b" });
        context["globalConstraints"] = new JsonArray(new JsonObject { ["id"] = "global_a", ["version"] = "ga", ["text"] = "First exact global." },
            new JsonObject { ["id"] = "global_b", ["version"] = "gb", ["text"] = "Second exact global." });
        context["evidence"] = new JsonObject
        {
            ["requirement:work"] = context["requirements"]![0]!.DeepClone(),
            ["source:source_b"] = context["requirements"]![0]!["sources"]![1]!.DeepClone(),
            ["source:global_a"] = context["globalConstraints"]![0]!.DeepClone()
        };
        var finding = Finding(); finding["evidence"] = new JsonArray(obligation, "/draft/tasks/0");
        var witness = PlanningConstruction.DefectWitness(state, context, new JsonArray(finding));
        Assert.NotNull(witness);
        var entry = Assert.Single(witness["findings"]!.AsArray())!;
        var candidate = state.Construction.Candidate!;
        candidate.Reviews.Add(new() { ["defectWitness"] = witness });
        Assert.True(PlanningConstruction.UnresolvedDefects(candidate));
        var progress = Assert.Single(PlanningConstruction.DefectProgressContext(state, context)!)!;
        if (presentation == 6)
        {
            Assert.Equal(expected.Split(','), entry["obligationSourceIds"]!.AsArray().Select(id => id!.ToString()));
            Assert.True(JsonNode.DeepEquals(entry["obligationSourceIds"], progress["obligationSourceIds"]));
        }
        else
        {
            Assert.Null(entry["obligationSourceIds"]);
            Assert.Null(progress["obligationSourceIds"]);
        }
        candidate.Reviews.Add(new() { ["response"] = new JsonObject { ["resolutions"] = new JsonArray(new JsonObject
            { ["id"] = entry["id"]!.DeepClone(), ["status"] = "resolved" }) } });
        Assert.False(PlanningConstruction.UnresolvedDefects(candidate));
    }

    private static async Task<PlanningSession> DecisionState()
    {
        var state = await State();
        state.Construction!.Compact!.PresentationVersion = 6;
        state.Request.Options["construction_presentation_version"] = 6;
        return state;
    }
}
