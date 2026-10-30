using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactReviewedTransitionTests
{
    private static JsonObject Selection(LLMRequest request, string scope, string kind, bool close, bool export)
    {
        var response = new JsonObject
        {
        ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(),
        ["step"] = new JsonObject { ["scope"] = scope, ["kind"] = kind,
            ["inputs"] = new JsonArray(), ["operationIds"] = new JsonArray(),
            ["closeScopes"] = close ? export ? new JsonArray() : new JsonArray(scope) : null,
            ["dependencyTaskIds"] = new JsonArray(),
            ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray(), ["groupId"] = null, ["prerequisites"] = null },
        ["evidenceRequests"] = null, ["clarifications"] = null
        };
        if (request.StructuredOutputSchema["properties"]!["resumeStep"] is not null) response["resumeStep"] = null;
        return response;
    }

    private static JsonObject BusinessSelection(LLMRequest request) => new()
    {
        ["block"] = new JsonObject
        {
            ["objective"] = string.Join(" ", Context(request)["requirements"]!.AsArray().Select(row => row!["original"]!.ToString())),
            ["requirementIds"] = Context(request)["remainingObligations"]!.DeepClone(),
            ["informationNeeds"] = new JsonArray("The exact original numeric prerequisites."),
            ["expectedResults"] = new JsonArray("The requested numeric results with the original dependencies and boundaries."),
            ["dependencies"] = new JsonArray(), ["successCriteria"] = new JsonArray("Every requested result uses its exact required predecessor.")
        }, ["evidenceRequests"] = null, ["clarifications"] = null
    };

    private static JsonObject CleanReview(JsonObject _) => new()
    {
        ["decision"] = new JsonObject { ["verdict"] = "ok", ["reason"] = "The present fragment fulfills its current business contribution; future obligations remain open." }
    };

    private static PlanTask Container(string id) => new() { Id = id, Kind = "sequence", Objective = "Preserve the required local boundary.", Body = new() };
    private static PlanTask Number(string id) => new() { Id = id, Kind = "value", Objective = "Produce exactly seven.", Outputs = [new("number", new() { Kind = "number", Number = 7 })] };
    private static TaskValue Output(string id) => new() { Kind = "output", Source = id, Port = "number" };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response, TestRuntime runtime)
    {
        // Every scripted response is validated against the ordinary issued schema.
        var schema = request.StructuredOutputSchema ?? throw new InvalidOperationException("The scripted response requires its exact issued schema.");
        var value = response.ContainsKey("step") ? response.DeepClone() :
            PlanningCorpus.Transport(response, schema.AsObject(), schema.AsObject())!;
        var issued = runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall?.Id == request.ClientRequestId).PendingCall!;
        value = TestRuntime.WireEvidence(value, issued.EvidenceAddresses);
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(value, schema));
        return new() { Json = value, Text = value.ToJsonString() };
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) => new HybridWorkflowPlanner()
        .AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
