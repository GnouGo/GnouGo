using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
using static GnOuGo.Planning.Examples.ProductTransformationPlan;
using static GnOuGo.Flow.Planning.Tests.LookupCompilationTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class MappingStatusCompositionTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("missing_fact")]
    [InlineData("failure")]
    [InlineData("absent")]
    [InlineData("capture_truncated")]
    [InlineData("manifest_truncated")]
    [InlineData("complete", true)]
    [InlineData("missing_fact", true)]
    [InlineData("failure", true)]
    [InlineData("absent", true)]
    [InlineData("capture_truncated", true)]
    [InlineData("manifest_truncated", true)]
    public async Task ExplicitRevisionSeparatesFactsAndProducerOutcomeWithNoStatusInference(string variant, bool automatic = false)
    {
        var rowType = Obj(("name", new() { Nullable = true }), ("description", new() { Nullable = true }), ("price", new() { Nullable = true }));
        var snapshotType = Obj(("record", rowType), ("captureTruncated", new() { Kind = "boolean" }), ("manifestTruncated", new() { Kind = "boolean" }));
        snapshotType.Nullable = true;
        var producerType = Obj(("success", new() { Kind = "boolean" }), ("observationSnapshot", snapshotType), ("error_message", new() { Nullable = true }));
        var statusType = Obj(("success", new() { Kind = "boolean" }), ("availability", new() { Kind = "boolean" }),
            ("completeness", new() { Kind = "boolean" }), ("message", new() { Nullable = true }));
        var row = new JsonObject { ["name"] = " Observed\tname ", ["description"] = variant == "missing_fact" ? null : "Whole observed block\n二ième ligne", ["price"] = "001.20 €" };
        var response = new JsonObject { ["success"] = variant != "failure", ["error_message"] = variant == "failure" ? "Producer failure" : null,
            ["observationSnapshot"] = variant == "absent" ? null : new JsonObject { ["record"] = row.DeepClone(), ["captureTruncated"] = variant == "capture_truncated", ["manifestTruncated"] = variant == "manifest_truncated" } };
        var reads = 0;
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("generic", new() { Tools = [new() { Name = "acquire", InputSchema = TaskPlanCompiler.TypeSchema(new() { Kind = "object" }), OutputSchema = TaskPlanCompiler.TypeSchema(producerType), EffectKind = "none" }],
            ToolHandlers = new() { ["acquire"] = _ => { reads++; return new() { Content = response.DeepClone() }; } } });
        var model = new Facts();
        var engine = new WorkflowEngine { LLMUsageBudget = new(new() { MaxCalls = 2 }), McpClientFactory = factory, LLMClient = model, LlmDefaults = new() { Model = "offline" } };
        var runtime = new TestRuntime(engine);
        var state = PlannerFixture.Session(); state.Catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual); state.Catalog.Policy.RequireExternalConfirmation = false;
        var operation = TaskOperations.Describe(state.Catalog.Capabilities.Single(c => c.Method == "acquire")).Id;
        var snapshot = Field(Ref("acquire_source"), "observationSnapshot");
        var plan = new TaskPlan { Root = new() { Tasks = [
            new() { Id = "acquire_source", Operation = operation, Objective = "Acquire observed data" },
            new() { Id = "adapt_facts", Kind = "transform", Mode = "extract", Objective = "Extract row and outcome", Inputs = [new("record", snapshot)],
                Requires = new() { Kind = "predicate", Predicate = "and", Items = [Field(Ref("acquire_source"), "success"), new() { Kind = "predicate", Predicate = "not_equal", Items = [snapshot, new() { Kind = "null" }] }] },
                ResultType = Obj(("row", rowType), ("status", statusType)) }
        ], Outputs = [new("row", Ref("adapt_facts", "row")), new("status", Ref("adapt_facts", "status"))] } };
        runtime.Proposal = new() { Plan = plan, Requirements = new() { Summary = "Extract exact facts and report the producer outcome", Inputs = [],
            Outputs = [new() { Name = "row", Type = rowType }, new() { Name = "status", Type = statusType }], Outcomes = [new("observed", "Return observed facts and their acquisition outcome")] } };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); var baseline = PlannerFixture.Clone(state); var oldYaml = state.Yaml;
        var revisedPlan = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        MappingStatusComposition.Separate(revisedPlan.Root, "acquire_source", "adapt_facts", "assemble_outcome");
        runtime.Proposal.Plan = revisedPlan;
        var planner = new HybridWorkflowPlanner();
        if (automatic)
            state = new() { IntentVersion = 2, Catalog = baseline.Catalog, Requirements = baseline.Requirements, Request = new()
            {
                TenantId = "test", Prompt = baseline.Request.Prompt, Baseline = baseline.Plan,
                Options = new() { ["automatic_correction"] = true }, FailureEvidence = new()
                {
                    ["error_code"] = "CONTRACT_UNSATISFIED", ["details"] = new JsonObject
                    { ["mapping_result_path"] = "$/status/success", ["mapping_failure_kind"] = "program", ["mapping_failure_reason"] = "ungrounded_scalar", ["mapping_attempts"] = 2 }
                }
            } };
        else state = await planner.AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
            PreserveRequirements = true, Text = "Separate facts extraction and deterministic producer status. Preserve outputs and operations." }, runtime, PlannerFixture.Ct);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Null(state.ApprovedHash); Assert.Equal(automatic ? 1 : 0, state.ReplanAttempts); Assert.Equal(2, runtime.Calls.Count);
        if (automatic) Assert.Contains("$/status/success", runtime.Calls[^1].Prompt);
        Assert.Contains("Bind typed copies/projections/assembly", runtime.Calls[^1].Prompt);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(baseline.Requirements, PlanningJsonContext.Default.PlanningRequirements), JsonSerializer.SerializeToNode(state.Requirements, PlanningJsonContext.Default.PlanningRequirements)));
        Assert.Equal(oldYaml, baseline.Yaml); Assert.Equal(0, reads); Assert.Equal(0, model.Calls);
        var result = await Execute(state.Yaml!, new(), engine);
        var success = variant is "complete" or "missing_fact";
        Assert.Equal(success, result.Success); Assert.Equal(1, reads); Assert.Equal(success ? 1 : 0, model.Calls);
        if (success)
        {
            Assert.True(JsonNode.DeepEquals(row, result.Outputs!["row"]));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"success":true,"availability":true,"completeness":true,"message":null}"""), result.Outputs["status"]));
        }
        else Assert.Equal(variant == "failure" ? "MCP_CALL_ERROR" : "INPUT_VALIDATION", result.Error!.Code);
        Assert.InRange(PlanningJsonTransport.EstimateInputTokens(runtime.Calls[^1].Prompt, runtime.Calls[^1].StructuredOutputSchema!.AsObject()), 1, state.Request.Generation.MaxInputTokensPerRequest);
    }
    private static TaskValue Field(TaskValue value, string field) => new() { Kind = "field", Port = field, Items = [value] };
    private sealed class Facts : ILLMClient
    {
        public int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = "({row:source.record.record})" } });
        }
    }
}
