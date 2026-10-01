using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RepairPatchTests
{
    internal static PlanningSession State()
    {
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements();
        state.Catalog = new() { AllowedStepTypes = ["mcp.call", "llm.call", "template.render", "set"], Capabilities = [new() { Id = "consume", Version = "1", StepType = "mcp.call", Kind = "tool", Server = "arbitrary", Method = "arbitrary",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"},"position":{"type":"number"},"side":{"type":"string","enum":["A","B"]},"fixed":{"type":"string"}},"required":["text","fixed"],"additionalProperties":false}""")!.AsObject(),
            RequestBindings = [new("/fixed", JsonValue.Create("host"))], OutputSchema = new() { ["type"] = "object" } }] };
        state.Plan = new() { Root = new() { Tasks = [
            new() { Id = "producer", Kind = "transform", Objective = "Interpret evidence; never invent absent values", Inputs = [new("data", new() { Kind = "string", Text = "evidence" })],
                ResultType = new() { Kind = "object", Fields = [new() { Name = "text", Type = new() { Kind = "string", Nullable = true } },
                    new() { Name = "position", Type = new() { Kind = "integer", Nullable = true } }] } },
            new() { Id = "consumer", Kind = "operation", Operation = "consume", Objective = "Publish the supplied evidence", Inputs = [
                new("text", new() { Kind = "output", Source = "producer", Port = "text" }),
                new("position", new() { Kind = "output", Source = "producer", Port = "position" })] },
            new() { Id = "unrelated", Kind = "value", Objective = "Unchanged intent", Outputs = [new("data", new() { Kind = "string", Text = "unrelated" })] }
        ] } };
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        return state;
    }
    internal static IReadOnlyList<PlanningRepairPatch.Slot> Slots(PlanningSession state) => PlanningRepairPatch.Slots(state, PlanningSchemas.FullProposal(state, compact: false)["$defs"]!.AsObject());
    internal static LLMRequest Request(PlanningSession state) => new()
    {
        StructuredOutputSchema = PlanningRepairPatch.Schema(state, PlanningSchemas.FullProposal(state, compact: false, clarifications: false)),
        Prompt = "Repair\n" + new JsonObject { ["repair"] = new JsonObject { ["version"] = 1, ["authority"] = PlanningRepairPatch.Authority(state) } }.ToJsonString()
    };
    internal static JsonObject Edit(PlanningSession state, string path, string action, JsonNode? value = null)
    {
        var edit = new JsonObject { ["slot"] = Slots(state).Single(s => s.Location == path).Id, ["action"] = action };
        if (action is not ("remove" or "remove_owned")) edit["value"] = value;
        return edit;
    }
    internal static TaskPlan Apply(PlanningSession state, params JsonObject[] edits)
    {
        var request = Request(state);
        var response = new JsonObject { ["discoveryRequests"] = null, ["patch"] = new JsonObject { ["edits"] = new JsonArray(edits.Select(e => (JsonNode)e).ToArray()) } };
        var errors = PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!);
        Assert.Empty(errors);
        return PlanningRepairPatch.Apply(state, response.Deserialize(RepairJsonContext.Default.PlanningRepairResponse)!.Patch!, request);
    }

    [Fact]
    public void TypedPatchChangesOnlyExplicitSlotsAndRetainsTheBaseline()
    {
        var state = State(); var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var schema = Request(state).StructuredOutputSchema!;
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.DoesNotContain("#/\u0024defs/plan", schema.ToJsonString());
        Assert.False(schema["properties"]!.AsObject().ContainsKey("plan"));
        Assert.False(schema["$defs"]!.AsObject().ContainsKey("task"));
        var fixedPlan = Apply(state, Edit(state, "/tasks/consumer/inputs/position", "remove"),
            Edit(state, "/tasks/producer/resultType/fields/text/type/nullable", "replace", JsonValue.Create(false)));
        Assert.Empty(new TaskPlanCompiler().Compile(fixedPlan, state.Catalog!).Diagnostics);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(state.Plan!.Root.Tasks[2].Objective, fixedPlan.Root.Tasks[2].Objective);
        Assert.Equal(state.Plan.Root.Tasks[0].Objective, fixedPlan.Root.Tasks[0].Objective);
    }

    [Theory]
    [InlineData("unknown-slot")]
    [InlineData("task-replacement")]
    [InlineData("null-is-not-removal")]
    [InlineData("wrong-type")]
    [InlineData("required-removal")]
    public void IssuedSchemaRejectsUnboundedOrMistypedEdits(string mutation)
    {
        var state = State(); var edit = Edit(state, "/tasks/producer/resultType/fields/text/type/nullable", "replace", JsonValue.Create(false));
        switch (mutation)
        {
            case "unknown-slot": edit["slot"] = "/tasks/unrelated/objective"; break;
            case "task-replacement": edit["plan"] = new JsonObject(); break;
            case "null-is-not-removal": edit["value"] = null; break;
            case "wrong-type": edit["value"] = "false"; break;
            case "required-removal": edit = Edit(state, "/tasks/consumer/inputs/text", "remove"); break;
        }
        var response = new JsonObject { ["discoveryRequests"] = null, ["patch"] = new JsonObject { ["edits"] = new JsonArray((JsonNode)edit) } };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, Request(state).StructuredOutputSchema!));
    }

    [Fact]
    public void DuplicateEditsAndEmptyPatchesAreRejectedWithoutMutation()
    {
        var state = State(); var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var edit = Edit(state, "/tasks/consumer/inputs/position", "remove");
        Assert.Throws<PlanningResponseException>(() => Apply(state, edit, edit.DeepClone().AsObject()));
        Assert.Throws<WorkflowRuntimeException>(() => Apply(state));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("scope")]
    [InlineData("catalog")]
    [InlineData("tenant")]
    [InlineData("policy")]
    [InlineData("ceilings")]
    public void RetainedRequestCannotBeRebased(string changed)
    {
        var state = State(); var request = Request(state);
        switch (changed)
        {
            case "baseline": state.Plan!.Root.Tasks[2].Objective += " changed"; break;
            case "scope": state.RevisionScope.RemoveAt(0); break;
            case "catalog": state.Catalog!.Capabilities[0].Version = "2"; break;
            case "tenant": state.Request.TenantId = "other"; break;
            case "policy": state.Request.Policy.RequireExternalConfirmation = false; break;
            case "ceilings": state.Request.MaxModelCalls++; break;
        }
        Assert.Throws<PlanningConflictException>(() => PlanningRepairPatch.Verify(state, request));
    }

    [Fact]
    public void OwnedWholeBindingOffersOnlyRemoval()
    {
        var state = State(); state.Plan!.Root.Tasks[1].Inputs.Add(new("fixed", new() { Kind = "string", Text = "host" }));
        state.RevisionScope = ["/tasks/consumer/inputs/fixed"];
        Assert.Equal(new[] { "remove" }, Assert.Single(Slots(state)).Actions);
        var plan = Apply(state, Edit(state, state.RevisionScope[0], "remove"));
        Assert.DoesNotContain(plan.Root.Tasks[1].Inputs, i => i.Name == "fixed");
        Assert.Single(state.Catalog!.Capabilities[0].RequestBindings);
    }

    [Fact]
    public void ExportAdditionsRequireTheDiagnosedConsumerChain()
    {
        var state = State(); var producer = state.Plan!.Root.Tasks[0];
        producer.ResultType!.Fields[0].Type.Nullable = false;
        state.Plan.Root.Tasks = [new() { Id = "container", Kind = "sequence", Objective = "Preserve a scope", Body = new() { Tasks = [producer] } }];
        state.Plan.Root.Outputs = [new("text", new() { Kind = "output", Source = "producer", Port = "text" })];
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        JsonObject Export() => Edit(state, "/tasks/container/body/outputs", "add", JsonNode.Parse("""[{"name":"message","value":{"kind":"output","source":"producer","port":"text"}}]"""));
        var fixedPlan = Apply(state, Export(), Edit(state, "/root/outputs/text", "replace", JsonNode.Parse("""{"kind":"output","source":"container","port":"message"}""")));
        Assert.Empty(new TaskPlanCompiler().Compile(fixedPlan, state.Catalog!).Diagnostics);
        Assert.Throws<PlanningResponseException>(() => Apply(state, Export()));
        Assert.Empty(state.Plan.Root.Tasks[0].Body!.Outputs);
    }
    [Fact]
    public void ALocalRepairDoesNotRepeatUnrelatedSubtreesOrSchemas()
    {
        var state = State(); var before = HybridWorkflowPlanner.Prompt(state);
        state.Plan!.Groups.Add(new() { Id = "unrelated_group", Body = new() { Tasks = [new() { Id = "other", Kind = "value", Objective = new string('x', 100000) }] } });
        state.Discovery.Resolved.Add(new() { Id = "unrelated_capability", Description = new string('y', 100000), Version = "1", StepType = "mcp.call", InputSchema = new() { ["type"] = "object" } });
        var after = HybridWorkflowPlanner.Prompt(state);
        Assert.DoesNotContain(new string('x', 50), after); Assert.DoesNotContain(new string('y', 50), after);
        Assert.Equal(before.Length, after.Length); // Only the fixed-length authority fingerprint changes.
        Assert.DoesNotContain("\"taskPlan\"", after);
    }

    [Fact]
    public void PartialCatalogOwnershipAllowsExplicitRemovalOfOwnedDescendantsOnly()
    {
        var state = State(); var cap = state.Catalog!.Capabilities[0];
        cap.InputSchema = JsonNode.Parse("""{"type":"object","properties":{"options":{"type":"object","properties":{"locked":{"type":"string"},"text":{"type":"string"}},"required":["locked","text"]}},"required":["options"]}""")!.AsObject();
        cap.RequestBindings = [new("/options/locked", JsonValue.Create("host"))];
        state.Plan!.Root.Tasks[1].Inputs = [new("options", new() { Kind = "object", Members = [
            new("locked", new() { Kind = "string", Text = "override" }), new("text", new() { Kind = "string", Text = "keep" })] })];
        state.RevisionScope = ["/tasks/consumer/inputs/options"];
        Assert.Equal(new[] { "remove_owned" }, Assert.Single(Slots(state)).Actions);
        var repaired = Apply(state, Edit(state, state.RevisionScope[0], "remove_owned"));
        var remaining = Assert.Single(repaired.Root.Tasks[1].Inputs[0].Value.Members);
        Assert.Equal("text", remaining.Name); Assert.Equal("keep", remaining.Value.Text);
        Assert.Equal(2, state.Plan.Root.Tasks[1].Inputs[0].Value.Members.Count);
    }

    [Fact]
    public void MissingGroupInputAndDependencyRepairsPreserveInterfacesAndOrder()
    {
        var state = State(); state.Plan!.Groups.Add(new() { Id = "reuse", Inputs = [new() { Name = "text" }], Body = new() { Outputs = [new("text", new() { Kind = "input", Source = "text" })] } });
        state.Plan.Root.Tasks.Add(new() { Id = "call_group", Kind = "call", Objective = "Use the declared interface", Group = "reuse", DependsOn = ["unknown"] });
        state.RevisionScope = ["/tasks/call_group/inputs/text", "/tasks/call_group/dependsOn"];
        var repaired = Apply(state, Edit(state, state.RevisionScope[0], "add", JsonNode.Parse("""{"kind":"string","text":"explicit"}""")),
            Edit(state, state.RevisionScope[1], "replace", new JsonArray("consumer")));
        Assert.Equal("explicit", Assert.Single(repaired.Root.Tasks[^1].Inputs).Value.Text);
        Assert.Equal(new[] { "consumer" }, repaired.Root.Tasks[^1].DependsOn);
        Assert.Equal("text", Assert.Single(repaired.Groups[0].Inputs).Name);
        Assert.Equal(state.Plan.Root.Tasks.Select(t => t.Id), repaired.Root.Tasks.Select(t => t.Id));
    }

    [Fact]
    public void MissingTransformTypeIsAClosedBusinessDeclarationNotAReplacementTask()
    {
        var state = State(); state.Plan!.Root.Tasks[0].ResultType = null;
        state.RevisionScope = ["/tasks/producer/resultType"];
        var payload = JsonNode.Parse("""{"kind":"object","fields":[{"name":"text","type":{"kind":"string"}}]}""");
        var repaired = Apply(state, Edit(state, state.RevisionScope[0], "replace", payload));
        Assert.Equal("string", Assert.Single(repaired.Root.Tasks[0].ResultType!.Fields).Type.Kind);
        Assert.Equal(state.Plan.Root.Tasks[0].Objective, repaired.Root.Tasks[0].Objective);
        Assert.Null(state.Plan.Root.Tasks[0].ResultType);
    }

    [Fact]
    public void ChoicesCannotChangeHostSelectionsAndAlternativeValuesRemainLiteral()
    {
        var state = State(); state.Plan!.Choices = [new() { Id = "decision", Question = "Which value?", Type = new() { Kind = "string" },
            Alternatives = [new("a", "First", new() { Kind = "string", Text = "A" }), new("b", "Second", new() { Kind = "string", Text = "B" })], Recommended = "missing" }];
        state.RevisionScope = ["/choices/decision"];
        var payload = JsonNode.Parse("""{"id":"decision","question":"Which value?","type":{"kind":"string"},"alternatives":[{"id":"a","description":"First","value":{"kind":"string","text":"A"}},{"id":"b","description":"Second","value":{"kind":"string","text":"B"}}],"recommended":"a"}""");
        var repaired = Apply(state, Edit(state, state.RevisionScope[0], "replace", payload!.DeepClone()));
        Assert.Equal("a", repaired.Choices[0].Recommended); Assert.Null(repaired.Choices[0].Selected);
        state.Plan.Choices[0].Selected = "b";
        Assert.Throws<PlanningResponseException>(() => Apply(state, Edit(state, state.RevisionScope[0], "replace", payload!.DeepClone())));
        Assert.Equal("b", state.Plan.Choices[0].Selected);
    }

    [Fact]
    public void ConditionalRepairRequiresBothExplicitBranchesAndRejectsUnusedExports()
    {
        var state = State(); var producer = state.Plan!.Root.Tasks[0]; producer.ResultType!.Fields[0].Type.Nullable = false;
        state.Plan.Root.Tasks = [new() { Id = "branch", Kind = "conditional", Objective = "Choose a result", Condition = new() { Kind = "boolean", Boolean = true },
            Body = new() { Tasks = [producer] }, Otherwise = new() }];
        state.Plan.Root.Outputs = [new("text", new() { Kind = "output", Source = "producer", Port = "text" })];
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        JsonObject[] Edits() => [Edit(state, "/tasks/branch/body/outputs", "add", JsonNode.Parse("""[{"name":"text","value":{"kind":"output","source":"producer","port":"text"}}]""")),
            Edit(state, "/tasks/branch/otherwise/outputs", "add", JsonNode.Parse("""[{"name":"text","value":{"kind":"string","text":"explicit fallback"}}]""")),
            Edit(state, "/root/outputs/text", "replace", JsonNode.Parse("""{"kind":"output","source":"branch","port":"text"}"""))];
        Assert.Empty(new TaskPlanCompiler().Compile(Apply(state, Edits()), state.Catalog!).Diagnostics);
        var partial = Edits(); Assert.Throws<PlanningResponseException>(() => Apply(state, partial[0], partial[2]));
        var extra = Edits(); extra[0]["value"]!.AsArray().Add((JsonNode)JsonNode.Parse("""{"name":"unused","value":{"kind":"string","text":"unrelated"}}""")!);
        Assert.Throws<PlanningResponseException>(() => Apply(state, extra));
    }

}
