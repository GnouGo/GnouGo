using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstructionRequestAdmissionTests
{
    [Fact]
    public async Task OptionalWholeContractsCannotBlockAnOtherwiseAdmissibleDraft()
    {
        var state = State();
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Contains("required-port-description", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("optional-port-description", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("optional_operation", request.Prompt, StringComparison.Ordinal); // Retained discovery identity.
        Assert.DoesNotContain("optional_operation", request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("required_operation", request.StructuredOutputSchema.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var runtime = new TestRuntime { Respond = (_, _) => throw new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningConstruction.DraftPurpose, request.Prompt, request.StructuredOutputSchema.AsObject(), PlannerFixture.Ct, 8192));
        Assert.InRange(Estimate(Assert.Single(runtime.Calls)), 1, 32000);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(32000, state.Request.Generation.MaxInputTokensPerRequest);
        Assert.Equal(8192, state.PendingCall!.Request.MaxTokens);
        PlanningConstruction.LoadEvidence(state, ["contract:optional_operation"]);
        Assert.Contains("optional-port-description", state.Construction!.Evidence!.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("inspection")]
    [InlineData("evidence")]
    [InlineData("task")]
    [InlineData("nested_candidate")]
    [InlineData("commitment")]
    public async Task MandatoryContractsStillRejectBeforeDispatchWithoutTruncation(string mandatory)
    {
        var state = State();
        switch (mandatory)
        {
            case "inspection": state.Discovery.Inspections![0].OperationIds!.Add("optional_operation"); break;
            case "evidence": PlanningConstruction.LoadEvidence(state, ["contract:optional_operation"]); break;
            case "task": state.Plan = new() { Root = new() { Tasks = [new() { Id = "use", Operation = "optional_operation", Objective = "Perform the requested operation" }] } }; break;
            case "nested_candidate":
                var nested = new PlanTask { Id = "use", Operation = "optional_operation", Objective = "Perform the requested operation" };
                var container = new PlanTask { Id = "branch", Kind = "conditional", Objective = "Select the requested result", Condition = new() { Kind = "boolean", Boolean = true },
                    Body = new() { Tasks = [nested] }, Otherwise = new() };
                state.Construction!.Candidate = new() { Plan = new() { Root = new() { Tasks = [container] } },
                    TaskIds = ["branch", "use"], OpenScopes = ["/root"], Extension = new() { Tasks = [container] } };
                break;
            case "commitment": state.Requirements = new() { Outcomes = [new("required", "Perform the requested operation")
                { Commitments = [new("optional_operation")] }] }; break;
        }
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Contains("optional-port-description", request.Prompt, StringComparison.Ordinal);
        var runtime = new TestRuntime();
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningConstruction.DraftPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, 8192));
        Assert.Equal("MODEL_INPUT_LIMIT", error.Code);
        Assert.Empty(runtime.Calls);
        Assert.Null(state.PendingCall);
        Assert.Equal(0, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(32000, state.Request.Generation.MaxInputTokensPerRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewAndEvidencePresentationsIncludeTheWholePhaseEnvelope(bool intermediate)
    {
        var state = State();
        var (context, request) = PlanningConstruction.Request(state, "Review the exact original evidence.",
            _ => PlanningSchemas.Object(("findings", PlanningSchemas.Array(PlanningSchemas.String(), 0, 3))),
            2048, review: true, intermediate);
        Assert.DoesNotContain("optional-port-description", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("required-port-description", request.Prompt, StringComparison.Ordinal);
        Assert.Equal("required_operation", Assert.Single(context["operations"]!.AsArray())!["id"]!.ToString());
        Assert.InRange(Estimate(state, request, 2048), 1, 32000);
        Assert.Null(state.Construction!.PendingReviewContext);
    }

    [Fact]
    public void OptionalIdentityDirectoryCannotMakeMandatoryContextExceedTheBoundary()
    {
        var state = State();
        var instructions = "Review the exact evidence.";
        JsonObject Schema(IReadOnlySet<string> _) => PlanningSchemas.Object(("findings", PlanningSchemas.Array(PlanningSchemas.String(), 0, 3)));
        var context = PlanningConstruction.Context(state);
        context["operations"]!.AsArray().RemoveAt(1);
        var mandatory = new LLMRequest { Prompt = instructions + "\n" + PlanningJsonTransport.Prompt(context), StructuredOutputSchema = Schema(new HashSet<string>()) };
        state.Request.Generation.MaxInputTokensPerRequest = checked((int)Estimate(state, mandatory, 2048));
        var (selected, request) = PlanningConstruction.Request(state, instructions, Schema, 2048);
        Assert.Null(selected["optionalOperationContracts"]);
        Assert.InRange(Estimate(state, request, 2048), 1, state.Request.Generation.MaxInputTokensPerRequest);
        Assert.Contains("required-port-description", request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalAdmissionCountsItsOperationDependentResponseSchema()
    {
        var state = State();
        state.Discovery.Resolved.Single(c => c.Id == "optional_operation").InputSchema["properties"]!["value"]!["description"] = "Small optional contract.";
        var context = PlanningConstruction.Context(state);
        var promptOnly = new LLMRequest { Prompt = "Review.\n" + PlanningJsonTransport.Prompt(context) };
        Assert.True(Estimate(state, promptOnly, 2048) < 32000);
        var (selected, request) = PlanningConstruction.Request(state, "Review.",
            ids => PlanningSchemas.Object(("value", PlanningSchemas.Enum(ids.Contains("optional_operation") ? new string('x', 100000) : "required"))), 2048);
        Assert.Equal("required_operation", Assert.Single(selected["operations"]!.AsArray())!["id"]!.ToString());
        Assert.InRange(Estimate(state, request, 2048), 1, 32000);
    }

    [Fact]
    public async Task PendingDraftRetainsItsOriginalOptionalContractsAndExactIdentity()
    {
        var state = State();
        var optional = state.Discovery.Resolved.Single(c => c.Id == "optional_operation");
        optional.InputSchema["properties"]!["value"]!["description"] = "previously-small-contract";
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Contains("previously-small-contract", request.Prompt, StringComparison.Ordinal);
        var runtime = new TestRuntime { Respond = (_, _) => throw new OperationCanceledException() };
        state.Construction!.IssuedBaseHash = PlanningConstruction.Hash(state.Plan);
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningConstruction.DraftPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject(), PlannerFixture.Ct, 8192));
        var saved = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        optional.InputSchema["properties"]!["value"]!["description"] = new string('x', 150000);
        state = PlannerFixture.Clone(state);
        await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(saved, JsonSerializer.Serialize(runtime.Calls[1], PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(1, state.ModelCalls);
    }

    private static long Estimate(LLMRequest request) => (System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)) + 2L) / 3L + 256;
    private static long Estimate(PlanningSession state, LLMRequest request, int output) => Estimate(PlanningGenerationPolicy.Apply(new()
    {
        Prompt = request.Prompt, StructuredOutputSchema = request.StructuredOutputSchema, StructuredOutputStrict = true,
        UseBackgroundMode = true, Model = "", MaxTokens = output,
        ClientRequestId = state.Request.SessionId + ":" + (state.ModelCalls + 1) + ":" + new string('0', 64)
    }, state.Request.Generation));

    private static PlanningSession State()
    {
        var required = Capability("required_operation", "required-port-description");
        var optional = Capability("optional_operation", "optional-port-description" + new string('x', 150000));
        return new()
        {
            IntentVersion = 3,
            Request = new() { TenantId = "construction-admission", Prompt = "Return the exact requested value.", Generation = new("medium", 32000, 8192) },
            Construction = new(), Catalog = new() { AllowedStepTypes = ["mcp.call", "set"] },
            Discovery = new()
            {
                Sources = [new("source", "Declared operations")], Resolved = [required, optional],
                Pages = [new("source", null, [Summary(required), Summary(optional)], null)],
                Inspections = [new("source", OperationIds: [required.Id])]
            }
        };
        static CapabilitySummary Summary(PlanningCapability capability) => new(capability.Id, "source", capability.Method!, capability.Description,
            capability.StepType, capability.EffectKind, capability.Version, Operation: TaskOperations.Describe(capability));
        static PlanningCapability Capability(string id, string description) => new()
        {
            Id = id, Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = id, Description = "Read an exact declared value.", EffectKind = "read",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string", ["description"] = description } },
                ["required"] = new JsonArray("value"), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["result"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("result"), ["additionalProperties"] = false }
        };
    }
}
