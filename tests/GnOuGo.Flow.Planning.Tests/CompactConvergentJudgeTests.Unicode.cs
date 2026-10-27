using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactConvergentJudgeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnicodeBoundaryEvidenceReportDoesNotConsumeReportFollowup(bool recover)
    {
        var state = await State();
        state.Construction!.Compact!.PresentationVersion = 10;
        state.Request.Options["construction_presentation_version"] = 10;
        var accepted = PlanningConstruction.Hash(state.Plan);
        var calls = state.ModelCalls;
        var runtime = new TestRuntime();
        string? issued = null;
        if (recover)
        {
            runtime.Respond = (_, _) => throw new OperationCanceledException();
            _ = await Advance(state, runtime);
            state = PlannerFixture.Clone(runtime.Checkpoints.Last(saved => saved.Status == PlanningStatus.Generating && saved.PendingCall is not null));
            issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        }
        runtime.Respond = (request, _) =>
        {
            if (issued is not null)
                Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(PlanningModelCalls.UnicodeStringLengths, request.StructuredOutputSchema!["$comment"]!.ToString());
            return Response(new JsonObject
            {
                ["action"] = new JsonObject { ["kind"] = "evidenceRequests", ["addresses"] = new JsonArray("task:accepted_label") },
                ["uncertainty"] = new string('a', 255) + "\U0001F4A1"
            });
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.NotNull(state.Construction!.Evidence!["task:accepted_label"]);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(calls + 1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
        Assert.All(state.Construction.Candidate!.Reviews, review => Assert.Null(review["reassessment"]));
        Assert.False(state.Construction.Candidate.LocalReviewed);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task IssuedStringLengthInterpretationSurvivesChangedSessionAndRequestReconstruction(int presentation)
    {
        var state = await State();
        state.Construction!.Compact!.PresentationVersion = presentation;
        var schema = PlanningSchemas.Object(("text", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1 }));
        var request = PlanningModelCalls.PrepareRequest(state, "construction_select", "Inspect one character.", schema);
        Assert.Null(schema["$comment"]);
        Assert.Equal(presentation is 10 or 11, request.StructuredOutputSchema!["$comment"] is not null);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema, strict: true));
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = "construction_select", Request = request };
        state.ModelCalls++;
        state = PlannerFixture.Clone(state);
        var exact = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        // Recovery must use the issued annotation, never a newer/current version.
        state.Construction!.Compact!.PresentationVersion = presentation is 10 or 11 ? 9 : 10;
        var runtime = new TestRuntime { Respond = (issuedRequest, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(issuedRequest, PlanningJsonContext.Default.LLMRequest));
            return Response(new JsonObject { ["text"] = "\U0001F4A1" });
        } };
        var call = PlanningModelCalls.CallAsync(state, runtime, "construction_select", "New context must not replace an issued request.", schema, PlannerFixture.Ct);
        if (presentation is 10 or 11) Assert.Equal("\U0001F4A1", (await call)["text"]!.ToString());
        else Assert.Contains((await Assert.ThrowsAsync<PlanningResponseException>(() => call)).Diagnostics,
            diagnostic => diagnostic.Location == "/text");
        Assert.Equal(1, state.ModelCalls);
        Assert.Single(runtime.Calls);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task PreviouslyIssuedUnmarkedVersionTenRequestKeepsHistoricalLengthValidation()
    {
        var state = await State();
        state.Construction!.Compact!.PresentationVersion = 10;
        var schema = PlanningSchemas.Object(("text", new JsonObject { ["type"] = "string", ["maxLength"] = 1 }));
        var request = PlanningModelCalls.PrepareRequest(state, "construction_select", "Historical issued request.", schema);
        request.StructuredOutputSchema!.AsObject().Remove("$comment");
        request.ClientRequestId = "retained-request";
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = "construction_select", Request = request };
        state.ModelCalls = 1;
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        state = PlannerFixture.Clone(state);
        var runtime = new TestRuntime { Respond = (issued, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest));
            return Response(new JsonObject { ["text"] = "\U0001F4A1" });
        } };
        await Assert.ThrowsAsync<PlanningResponseException>(() => PlanningModelCalls.CallAsync(state, runtime,
            "construction_select", "Current context.", schema, PlannerFixture.Ct));
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
    }
}
