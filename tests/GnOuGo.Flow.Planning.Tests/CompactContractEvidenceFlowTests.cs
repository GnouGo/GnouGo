using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactContractEvidenceFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerationAndCorrectionExposeOptionalPortsBeforeReviewingExactOriginals(bool correction)
    {
        var (state, runtime) = await Ready();
        var baseline = PlanningConstruction.Hash(state.Plan);
        var originalContract = JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability);
        if (correction) state.Diagnostics = [new("CONSTRUCTION_STEP_INPUT_INVALID", "/extension", "Retrieve the missing original input contract.")];
        var phase = correction ? "replan" : PlanningConstruction.DraftPurpose;
        var drafts = 0; var reviews = 0; var retrievedText = "";
        runtime.Respond = (request, purpose) =>
        {
            var context = Context(request);
            var view = context["operations"]![0]!;
            var measurement = PlanningRequestMeasurements.Measure(request, purpose,
                purpose == PlanningConstruction.LocalPurpose ? 4000 : 8000);
            Assert.InRange(measurement.EstimatedInputTokens, 1, purpose == PlanningConstruction.LocalPurpose ? 4000 : 8000);
            if (purpose == phase)
            {
                drafts++;
                if (drafts == 1)
                {
                    Assert.Empty(view["editableInputs"]!.AsArray());
                    Assert.Contains("Empty editableInputs does not mean", request.StructuredOutputSchema!["description"]!.ToString(), StringComparison.Ordinal);
                    return Wire(request, DraftAction(context, evidence: "schema:read@v1:/input/properties/target"));
                }
                Assert.Equal(2, drafts);
                Assert.Equal("target", Assert.Single(view["editableInputs"]!.AsArray())!["name"]!.ToString());
                Assert.True(JsonNode.DeepEquals(state.Discovery.Resolved[0].InputSchema["properties"]!["target"],
                    context["evidence"]!["schema:read@v1:/input/properties/target"]!["schema"]));
                Assert.Null(view["outputs"]);
                Assert.Null(view["omittedOptionalInputs"]);
                Assert.Equal("port-page:read@v1:/output:0", view["outputDirectory"]!.ToString());
                return Wire(request, DraftAction(context, task: new() { Id = "read_exact", Kind = "operation",
                    Objective = "Read the caller-authorized record EXACT.", Operation = "read",
                    Inputs = [new("target", new() { Kind = "string", Text = "EXACT" })] }));
            }
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            reviews++;
            Assert.Contains("Partial descriptions do not prove absent functionality", request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("Newly opened group/container scopes", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("Future work is not an omission", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("closeRequirementIds claims completion with accepted work", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("scopeInterface shows accepted exports", request.Prompt, StringComparison.Ordinal);
            var supplied = context["evidence"]?.AsObject().FirstOrDefault(pair => pair.Key.StartsWith("description:", StringComparison.Ordinal));
            if (supplied?.Value is JsonObject original)
            {
                Assert.Null(view["description"]);
                Assert.Null(view["deterministicValidation"]);
                Assert.Equal("target", Assert.Single(view["boundInputs"]!.AsArray())!["name"]!.ToString());
                retrievedText += original["text"]!.ToString();
                return Wire(request, original["next"] is { } next ? Review(next.ToString()) : Review());
            }
            Assert.False(view["descriptionComplete"]!.GetValue<bool>());
            return Wire(request, Review(view["descriptionEvidence"]!.ToString()));
        };
        for (var i = 0; i < 9 && state.Construction!.Checkpoints.Count == 0; i++)
        {
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.DoesNotContain(state.Diagnostics, finding => finding.Required && finding.Code != "CONSTRUCTION_STEP_INPUT_INVALID");
            Assert.NotNull(state.Construction);
            if (state.Construction.Checkpoints.Count == 0) Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        }
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(2, drafts);
        Assert.Equal(3, reviews);
        Assert.Equal(state.Discovery.Resolved[0].Description, retrievedText);
        Assert.Equal("EXACT", Assert.Single(Assert.Single(state.Plan!.Root.Tasks).Inputs).Value.Text);
        Assert.Equal(correction ? 2 : 0, state.ReplanAttempts);
        Assert.Equal(originalContract, JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidencePresentationKeepsSelectedOutputsAndEveryOriginalPortRetrievable(bool historical)
    {
        var (state, _) = await Ready();
        if (historical) state.Construction!.Compact!.PresentationVersion = null;
        var contract = state.Discovery.Resolved[0];
        var before = JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningCapability);
        PlanningConstruction.LoadEvidence(state, ["schema:read@v1:/input/properties/target", "schema:read@v1:/output/properties/result11"]);
        var exactEvidence = state.Construction!.Evidence!.DeepClone();
        var view = Assert.Single(PlanningCompactConstruction.ContractViews(state))!;
        Assert.True(JsonNode.DeepEquals(contract.InputSchema["properties"]!["target"], view["inputSchema"]!["properties"]!["target"]));
        Assert.NotNull(view["selectedOutputs"]);
        Assert.Equal(historical, view["outputs"] is JsonArray);
        Assert.Equal(historical, view["omittedOptionalInputs"] is JsonArray);
        var names = historical ? view["outputs"]!.AsArray().Select(port => port!["name"]!.ToString()).ToList() : [];
        string? address = view["outputDirectory"]!.ToString();
        while (address is not null)
        {
            var page = PlanningCompactConstruction.LoadContractEvidence(state, address)!;
            names.AddRange(page["items"]!.AsArray().Select(port => port!["name"]!.ToString()));
            address = page["next"]?.ToString();
        }
        Assert.Equal(12, names.Distinct(StringComparer.Ordinal).Count());
        Assert.True(JsonNode.DeepEquals(exactEvidence, state.Construction.Evidence));
        Assert.Equal(before, JsonSerializer.Serialize(contract, PlanningJsonContext.Default.PlanningCapability));
    }

    [Fact]
    public async Task SavedGenerationRequestReplaysItsCapturedPresentationBeforeExpandingEvidence()
    {
        var (state, runtime) = await Ready();
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint =>
            checkpoint.Status == PlanningStatus.Generating && checkpoint.PendingCall?.Purpose == PlanningConstruction.DraftPurpose));
        var issued = saved.PendingCall!.Request;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls;
        var baseline = PlanningConstruction.Hash(saved.Plan);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return Wire(request, DraftAction(Context(request), evidence: "schema:read@v1:/input/properties/target"));
        };
        var resumed = await Advance(saved, runtime);
        Assert.Equal(calls, resumed.ModelCalls);
        Assert.Equal(0, resumed.ReplanAttempts);
        Assert.Equal(baseline, PlanningConstruction.Hash(resumed.Plan));
        Assert.Null(resumed.PendingCall);
        Assert.Contains("schema:read@v1:/input/properties/target", resumed.Construction!.Compact!.CurrentStep!.SchemaPointers);
        Assert.NotNull(resumed.Construction.Evidence!["schema:read@v1:/input/properties/target"]);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready()
    {
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var source = Context(request)["sources"]![0]!;
            return Wire(request, new() { ["sourceId"] = source["id"]!.ToString(),
                ["clauses"] = new JsonArray(new JsonObject { ["text"] = source["text"]!.ToString(), ["kind"] = "requirement", ["interfaces"] = "none" }) });
        } };
        var state = await Advance(new() { Request = new() { TenantId = "contract-evidence-flow",
            Prompt = "Read the caller-authorized record EXACT.", Options = new JsonObject { ["automatic_correction"] = true } } }, runtime);
        Assert.Empty(state.Diagnostics);
        var capability = new PlanningCapability
        {
            Id = "read", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = "read", EffectKind = "read",
            Description = "Read only authorized original records. " + new string('x', 2100) + " If target is supplied, read that exact record without replacing missing observations.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
                { ["target"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Z]+$", ["description"] = "Optional exact authorized record identity." },
                  ["unrelated"] = new JsonObject { ["type"] = "string" } }, ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }
        };
        for (var i = 0; i < 12; i++) capability.OutputSchema["properties"]!["result" + i] = new JsonObject { ["type"] = "string" };
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"] };
        state.Discovery.Resolved = [capability];
        state.Construction!.Compact!.CurrentStep = new() { Id = "s1", Scope = "/root", Kind = "operation",
            Objective = state.Request.Prompt, OperationIds = ["read"], RequirementIds = ["req_0"],
            BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        return (state, runtime);
    }
    private static JsonObject DraftAction(JsonObject context, string? evidence = null, PlanTask? task = null) => new()
    {
        ["baseHash"] = context["baseHash"]!.ToString(), ["requirements"] = null, ["commitments"] = null,
        ["extension"] = task is null ? null : JsonSerializer.SerializeToNode(new PlanningConstructionExtension
            { Tasks = [task], RequirementIds = ["req_0"] }, PlanningJsonContext.Default.PlanningConstructionExtension),
        ["evidenceRequests"] = evidence is null ? null : new JsonArray(evidence), ["discoveryRequests"] = null,
        ["clarifications"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
    };
    private static JsonObject Review(string? evidence = null) => new()
    { ["findings"] = evidence is null ? new JsonArray() : null, ["uncertainty"] = "Execution remains unverified.",
        ["evidenceRequests"] = evidence is null ? null : new JsonArray(evidence) };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response)
    {
        var transported = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject());
        Assert.NotNull(transported);
        return new() { Json = transported, Text = transported.ToJsonString() };
    }
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
