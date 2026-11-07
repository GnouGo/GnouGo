using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstructionCursorGuidanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssuedDraftDistinguishesFinishedCursorFromBusinessCompletion(bool diagnostic)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready(diagnostic);
        state.Construction!.Candidate = null;
        var blockId = state.Construction.Compact!.CurrentBlockId;
        var block = JsonSerializer.Serialize(PlanningCompactConstruction.CurrentBlock(state), PlanningJsonContext.Default.PlanningConstructionBlock);
        var step = state.Construction.Compact.CurrentStep!;
        step.Kind = "operation"; step.OperationIds = ["read_number"];
        if (!state.Catalog!.AllowedStepTypes.Contains("mcp.call")) state.Catalog.AllowedStepTypes.Add("mcp.call");
        state.Catalog.Capabilities.Add(new()
        {
            Id = "read_number", Version = "1", Kind = "tool", StepType = "mcp.call", Server = "numbers", Method = "read",
            EffectKind = "read", Description = "Read the exact authorized numeric observation.",
            InputSchema = PlanningSchemas.Object(("index", PlanningSchemas.Type("number"))),
            OutputSchema = PlanningSchemas.Object(("value", PlanningSchemas.Type("number")))
        });
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            Assert.Contains("Null finishes only this implementation cursor.", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("completeBlock:true claims", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("Keep completeBlock:false", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("Selected operationIds belong to this cursor across its continued fragments", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("when completeBlock is true", request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("null finishes the block", request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("when they fit within maxTasksPerDraft", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("null finishes only this cursor", request.StructuredOutputSchema!["description"]!.ToString(), StringComparison.Ordinal);
            Assert.Contains("completeBlock explicitly claims", request.StructuredOutputSchema["description"]!.ToString(), StringComparison.Ordinal);
            var delivered = request.Prompt + request.StructuredOutputSchema.ToJsonString();
            Assert.Equal(diagnostic, delivered.Contains("maxTasksPerDraft is an advisory target", StringComparison.Ordinal));
            Assert.Equal(diagnostic, delivered.Contains("address-count targets only produce host warnings", StringComparison.Ordinal));
            Assert.Equal(!diagnostic, delivered.Contains("up to eight offered exact addresses", StringComparison.Ordinal));
            Assert.Equal(!diagnostic, delivered.Contains("must fit its captured input ceiling", StringComparison.Ordinal));
            var extension = new PlanningConstructionExtension
            {
                Scope = "/root", RequirementIds = ["work"], CompleteBlock = false, ContinueInScope = null,
                Tasks = [new() { Id = "observation", Kind = "operation", Operation = "read_number", Objective = "Obtain the prerequisite number.",
                    Inputs = [new("index", new() { Kind = "number", Number = 0 })] }]
            };
            var wire = PlanningCorpus.Transport(new JsonObject { ["extension"] = JsonSerializer.SerializeToNode(extension,
                PlanningJsonContext.Default.PlanningConstructionExtension) }, request.StructuredOutputSchema.AsObject(), request.StructuredOutputSchema.AsObject());
            Assert.Empty(PlanningContractValidation.ValidateInstance(wire, request.StructuredOutputSchema));
            return new() { Json = wire };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.Null(state.Construction!.Compact!.CurrentStep);
        Assert.Equal(blockId, state.Construction.Compact.CurrentBlockId);
        Assert.Equal(block, JsonSerializer.Serialize(PlanningCompactConstruction.CurrentBlock(state), PlanningJsonContext.Default.PlanningConstructionBlock));
        Assert.Equal("in_progress", state.Construction.Requirements.Single(requirement => requirement.Id == "work").Status);
        Assert.True(Assert.Single(state.Construction.Checkpoints).Fragment["businessReviewPending"]!.GetValue<bool>());
        Assert.Empty(state.Construction.Checkpoints[0].ClosedRequirementIds!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssuedDiscoveryAddressGuidanceAgreesWithItsCapturedSchema(bool diagnostic)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready(diagnostic);
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = null;
        state.Construction.Compact.CurrentStep.DiscoveryComplete = false;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.DiscoveryPurpose, purpose);
            Assert.Contains("current implementation cursor within its selected scope", request.Prompt, StringComparison.Ordinal);
            Assert.Equal(diagnostic, request.Prompt.Contains("complete needed set of offered exact addresses", StringComparison.Ordinal));
            Assert.Equal(!diagnostic, request.Prompt.Contains("up to eight offered exact addresses", StringComparison.Ordinal));
            var arrays = Objects(request.StructuredOutputSchema!["properties"]!["evidenceRequests"])
                .Where(node => node["type"]?.ToString() == "array").ToArray();
            Assert.NotEmpty(arrays);
            Assert.All(arrays, array => Assert.Equal(diagnostic ? null : 8, array["maxItems"]?.GetValue<int>()));
            var response = PlanningCorpus.Transport(new JsonObject { ["binding"] = new JsonObject
                { ["kind"] = "value", ["operationIds"] = new JsonArray(), ["groupId"] = null, ["closeScopes"] = null } },
                request.StructuredOutputSchema.AsObject(), request.StructuredOutputSchema.AsObject());
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema));
            return new() { Json = response };
        };
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.True(state.Construction!.Compact!.CurrentStep!.DiscoveryComplete);
        Assert.Equal("value", state.Construction.Compact.CurrentStep.Kind);
        Assert.NotNull(state.Construction.Compact.CurrentBlockId);
        Assert.Null(state.Construction.Candidate);
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var child in obj.SelectMany(pair => Objects(pair.Value))) yield return child;
        }
        else if (node is JsonArray array)
            foreach (var child in array.SelectMany(Objects)) yield return child;
    }
}
