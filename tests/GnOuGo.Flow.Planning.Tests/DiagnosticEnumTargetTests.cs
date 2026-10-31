using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DiagnosticEnumTargetTests
{
    private static List<string> Domain() => Enumerable.Range(0, 257).Select(index => "value_" + index).ToList();
    private static TaskType DomainType() => new() { Kind = "string", Enum = Domain() };
    private static PlanningSession Session() => PlannerFixture.Capture(new() { Request = new()
    {
        TenantId = "enum-target", Prompt = "Return the caller's choice unchanged as result. The exact allowed choices are: " + string.Join(",", Domain()),
        Options = new() { [PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1" }
    } });

    [Fact]
    public async Task OriginalLargeDomainSurvivesIndexingDraftFinalReviewAndRestartWithoutWideningMembership()
    {
        var state = Session();
        var runtime = new TestRuntime { Respond = (request, purpose) => Respond(request, purpose) };
        var planner = new HybridWorkflowPlanner();
        for (var advance = 0; advance < 20 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); advance++)
        {
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
            state = PlannerFixture.Clone(state);
        }
        Assert.True(state.Status == PlanningStatus.FinalReview, state.Status + ": " + string.Join(";", state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(5, state.ModelCalls);
        Assert.Equal(Domain(), Assert.Single(state.Plan!.Inputs).Type.Enum);
        Assert.Equal(Domain(), Assert.Single(state.Requirements!.Outputs!).Type.Enum);
        Assert.Null(state.ApprovedHash);
        PlanningArtifactApproval.Verify(state);
        Assert.Contains(state.ThresholdWarnings!, warning => warning.Kind == "enum_values" && warning.Current == 257 && warning.Threshold == 256);
        var output = state.Graph!.Workflows.Single(w => w.Key == state.Graph.Entrypoint).Outputs.Single();
        var schema = PlanningGraphCompiler.ToJsonSchema(output.Schema, state.Catalog!);
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonValue.Create("value_256"), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonValue.Create("not_original"), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(null, schema));
        var changed = PlannerFixture.Clone(state);
        changed.Plan!.Root.Tasks[0].Outputs[0] = new("choice", new() { Kind = "string", Text = "not_original" });
        var changedGraph = new TaskPlanCompiler().Compile(changed.Plan, changed.Catalog!, changed.Request).Graph!;
        Assert.Contains(PlanningClarifications.OutputFindings(changed, changedGraph), finding => finding.Code == "REQUIREMENTS_OUTPUTS_CHANGED");
        Assert.Throws<ArgumentException>(() => TaskPlanCompiler.TypeSchema(DomainType()));
        var reusedCompiler = new TaskPlanCompiler();
        Assert.Empty(reusedCompiler.Compile(state.Plan, state.Catalog!, state.Request).Diagnostics);
        Assert.NotEmpty(reusedCompiler.Compile(state.Plan, state.Catalog!).Diagnostics);
        state.Request.Options.Remove(PlanningTokenThresholds.OptimizationOptionName);
        state.Request.Options[PlanningTokenThresholds.OptionName] = "advisory";
        Assert.NotEmpty(reusedCompiler.Compile(state.Plan, state.Catalog!, state.Request).Diagnostics);
    }

    [Fact]
    public void ScopedEnumRepairKeepsAllMembersAcrossRestartAndRejectsMalformedEnums()
    {
        var state = Session(); state.Catalog = new() { AllowedStepTypes = ["set", "llm.call", "template.render"] };
        state.Plan = new() { Root = new() { Tasks = [new()
        {
            Id = "interpret", Kind = "transform", Objective = "Retain an exact observed choice.",
            Inputs = [new("source", new() { Kind = "string", Text = "value_256" })],
            ResultType = new() { Kind = "object", Fields = [new() { Name = "choice", Type = new() { Kind = "string", Enum = [] } }] }
        }, new() { Id = "independent", Kind = "value", Objective = "Retain independent work.", Outputs = [new("number", new() { Kind = "number", Number = 42 })] }] } };
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog, state.Request).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        const string path = "/tasks/interpret/resultType/fields/choice/type/enum";
        Assert.Contains(path, state.RevisionScope);
        state = PlannerFixture.Clone(state);
        var before = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var repaired = RepairPatchTests.Apply(state, RepairPatchTests.Edit(state, path, "replace", Strings(Domain())));
        Assert.Empty(new TaskPlanCompiler().Compile(repaired, state.Catalog!, state.Request).Diagnostics);
        Assert.Equal(Domain(), repaired.Root.Tasks[0].ResultType!.Fields[0].Type.Enum);
        Assert.Equal(before, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(JsonSerializer.Serialize(state.Plan!.Root.Tasks[1], PlanningJsonContext.Default.PlanTask),
            JsonSerializer.Serialize(repaired.Root.Tasks[1], PlanningJsonContext.Default.PlanTask));
        foreach (var defect in new[] { "empty", "duplicate", "null", "wrong_kind" })
        {
            var probe = JsonSerializer.SerializeToNode(repaired, PlanningJsonContext.Default.TaskPlan)!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
            var type = probe.Root.Tasks[0].ResultType!.Fields[0].Type;
            if (defect == "empty") type.Enum = [];
            if (defect == "duplicate") type.Enum!.Add(type.Enum[0]);
            if (defect == "null") type.Enum!.Add(null!);
            if (defect == "wrong_kind") type.Kind = "integer";
            Assert.Contains(new TaskPlanCompiler().Compile(probe, state.Catalog!, state.Request).Diagnostics,
                finding => finding.Code == "TASK_TRANSFORM_TYPE" && finding.Location == path);
        }
    }

    [Fact]
    public void PublicOutputRepairRetainsItsLargeExactDomainInsteadOfFallingBackToUnrestrictedStrings()
    {
        var state = Session(); state.Catalog = new() { AllowedStepTypes = ["set"] };
        state.Requirements = new() { Outputs = [new() { Name = "result", Type = DomainType() }] };
        state.Plan = new() { Root = new() { Outputs = [new("result", new() { Kind = "string", Text = "not_original" })] } };
        state.RevisionScope = ["/root/outputs/result"];
        state.Diagnostics = [new("REQUIREMENTS_OUTPUTS_CHANGED", "/root/outputs/result", "Return an original allowed value.")];
        var slot = Assert.Single(RepairPatchTests.Slots(state));
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["kind"] = "string", ["text"] = "value_256" }, slot.ValueSchema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["kind"] = "string", ["text"] = "not_original" }, slot.ValueSchema));
        var repaired = RepairPatchTests.Apply(state, RepairPatchTests.Edit(state, slot.Location, "replace",
            new JsonObject { ["kind"] = "string", ["text"] = "value_256" }));
        Assert.Equal("value_256", repaired.Root.Outputs[0].Value.Text);
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static LLMResponse Respond(LLMRequest request, string purpose)
    {
        var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
        var schema = request.StructuredOutputSchema!.AsObject();
        var response = purpose switch
        {
            "construction_intent" => CurrentPlanningFixture.Intent(context,
                JsonSerializer.SerializeToNode(new List<TaskInput> { new() { Name = "choice", Type = DomainType() } }, PlanningJsonContext.Default.ListTaskInput)!.AsArray(),
                JsonSerializer.SerializeToNode(new List<TaskInput> { new() { Name = "result", Type = DomainType() } }, PlanningJsonContext.Default.ListTaskInput)!.AsArray()),
            "construction_select" => new JsonObject { ["block"] = new JsonObject
            {
                ["objective"] = "Return the caller's exact allowed choice.", ["requirementIds"] = Strings(["req_0"]),
                ["informationNeeds"] = new JsonArray(), ["expectedResults"] = Strings(["The public result equals the caller's choice."]),
                ["dependencies"] = new JsonArray(), ["successCriteria"] = Strings(["Preserve the exact allowed domain and selected value."])
            } },
            "construction_implementation" => new JsonObject { ["baseHash"] = schema["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(), ["step"] = new JsonObject
            {
                ["scope"] = "/root", ["kind"] = "value", ["closeRequirementIds"] = Strings(["req_0"]),
                ["inputs"] = new JsonArray(), ["dependencyTaskIds"] = new JsonArray(), ["operationIds"] = new JsonArray(),
                ["outputNames"] = Strings(["result"]), ["groupId"] = null, ["closeScopes"] = null, ["prerequisites"] = null
            } },
            "construction_draft" => new JsonObject { ["baseHash"] = context["baseHash"]!.DeepClone(),
                ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension
                {
                    Scope = "/root", RequirementIds = ["req_0"], CloseScopes = ["/root"], CompleteBlock = true, Complete = true,
                    Tasks = [new() { Id = "echo", Kind = "value", Objective = "Preserve the original caller choice.",
                        Outputs = [new("choice", new() { Kind = "input", Source = "choice" })] }],
                    Outputs = [new("result", new() { Kind = "output", Source = "echo", Port = "choice" })]
                }, PlanningJsonContext.Default.PlanningConstructionExtension) },
            "construction_global_judge" => new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "ok", ["reason"] = "The result preserves the original caller choice unchanged." } },
            _ => throw new InvalidOperationException("Unexpected enum fixture phase: " + purpose)
        };
        var wire = PlanningCorpus.Transport(response, schema, schema)!;
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(wire, schema));
        if (purpose == "construction_implementation")
            Assert.True(JsonNode.DeepEquals(response["step"], wire["step"]), "The current implementation response must not discard or fabricate authored fields.");
        return new() { Json = wire };
    }
}
