using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SemanticReviewTests
{
    [Theory]
    [InlineData("read", "group", "findings")]
    [InlineData("lecture", "ensemble", "résultats")]
    public void SemanticContractsDistinguishRawReferencesFromContainerEnvelopes(string producer, string container, string field)
    {
        var graph = Graph(); var prep = Preparation(); var workflow = graph.Workflows[0];
        prep.Capabilities.Add(new() { Id = "external", StepType = "mcp.call", OutputSchema = new JsonObject { ["type"] = "object",
            ["properties"] = new JsonObject { [field] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
            ["required"] = new JsonArray(field) } });
        workflow.Steps.Insert(0, new() { Key = container, Type = "sequence", Steps = [new() { Key = producer, Type = "mcp.call", CapabilityId = "external",
            StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = "summary", Schema = new() { Type = "string" } }] }) }] });
        workflow.Steps[1].Input = Obj(("container", new() { Kind = "output", Source = container }),
            ("raw", new() { Kind = "output", Source = producer }), ("structured", new() { Kind = "output", Source = producer, ResultChannel = "structured" }),
            ("invalid", new() { Kind = "output", Source = "missing" }));
        workflow.Outputs.Clear();
        var context = TypedWorkflowPlanner.SemanticValueContracts(graph, prep);
        JsonObject Entry(string source, string? channel = null) => Assert.Single(context["references"]!.AsArray().OfType<JsonObject>(),
            p => p["source"]?.ToString() == source && p["resultChannel"]?.ToString() == channel);
        JsonObject Schema(JsonObject entry) => context["schemas"]![entry["schema"]!["$ref"]!.ToString()["#/schemas/".Length..]]!.AsObject();
        Assert.Equal("array", Schema(Entry(producer))["properties"]![field]!["type"]!.ToString());
        Assert.Null(Schema(Entry(producer))["properties"]!["response"]);
        var children = Schema(Entry(container))["properties"]![producer]!["properties"]!;
        Assert.Equal("array", children["response"]!["properties"]![field]!["type"]!.ToString());
        Assert.Equal("string", children["json"]!["properties"]!["summary"]!["type"]!.ToString());
        Assert.Equal("string", Schema(Entry(producer, "structured"))["properties"]!["summary"]!["type"]!.ToString());
        Assert.NotNull(Entry("missing")["unresolved"]); Assert.Null(Entry("missing")["schema"]);
    }

    [Theory]
    [InlineData("expr")]
    public async Task SelectorFindingsRemainExecutableRepairs(string field)
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.Graph.Workflows[0].Steps.Add(new() { Key = "decision", Type = "switch", Purpose = "Select the requested outcome",
            Expr = Str("selected"), Cases = [new("selected", null, [])] });
        state.BehaviorPlan = PlanningBehaviorRevisions.Inspect(state.Graph);
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        var approval = state.ApprovedBehaviorHash;
        state.ConstructionUnits = [new() { Key = "selector", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["decision"],
            ContractVersion = PlanningDataflow.ContractVersion, Status = "validated" }];
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            if (phase == "repair_unit")
            {
                var coordinate = Assert.Single(request.StructuredOutputSchema!["properties"]!["changes"]!["properties"]!.AsObject()).Key;
                Assert.Equal("nodes/decision/expr", coordinate);
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["changes"] = new JsonObject { [coordinate] = new JsonObject { ["kind"] = "string", ["text"] = "selected" } }, ["remove"] = new JsonArray() } });
            }
            Assert.Equal("semantic_review", phase);
            var target = "/workflows/0/steps/1/" + field;
            Assert.Contains(request.StructuredOutputSchema!["properties"]!["findings"]!["items"]!["properties"]!["location"]!["enum"]!.AsArray(), value => value!.ToString() == target);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray(new JsonObject
            { ["code"] = "SELECTOR_VALUE", ["workflow"] = "main", ["location"] = target, ["evidence"] = "Return a greeting",
                ["message"] = "Correct the computed selector without changing the accepted branches.", ["blocking"] = true }) } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Generating, state.Status); Assert.Equal(approval, state.ApprovedBehaviorHash);
        Assert.NotNull(state.Graph); Assert.Null(state.BehaviorRevisionSource);
        Assert.Contains(PlanningPatches.Scope(state.Graph, state.Diagnostics), coordinate => coordinate == PlanningPatches.Coordinate("main", "decision", field));
        Assert.DoesNotContain(state.Events, e => e.Kind == "behavior_revision_required");
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Validating, state.Status); Assert.Equal(approval, state.ApprovedBehaviorHash);
        Assert.Equal("selected", state.Graph!.Workflows[0].Steps[1].Expr!.Text);
        Assert.Equal(new[] { "semantic_review", "repair_unit" }, runtime.Phases);
    }

    [Theory]
    [InlineData("producer", "consumer")]
    [InlineData("renamed-source", "renamed-destination")]
    public void SemanticReviewReceivesAuthoritativeSchemasWithExactBindingsAndSharedDefinitions(string first, string second)
    {
        var graph = Graph(); var preparation = Preparation();
        graph.Workflows[0].Steps[0].CapabilityId = first;
        graph.Workflows[0].Steps.Add(new() { Key = "other", Type = "mcp.call", CapabilityId = second });
        var input = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            { ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("allocate", "finalize") }, ["body"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("mode", "body"), ["additionalProperties"] = false };
        preparation.Capabilities = [new() { Id = first, InputSchema = input, RequestBindings = [new("/mode", JsonValue.Create("allocate"))] },
            new() { Id = second, InputSchema = input.DeepClone().AsObject(), RequestBindings = [new("/mode", JsonValue.Create("finalize"))] },
            new() { Id = "unselected" }];
        var context = TypedWorkflowPlanner.SemanticCapabilities(graph, preparation);
        Assert.Equal(2, context["capabilities"]!.AsArray().Count); Assert.Equal(2, context["schemas"]!.AsObject().Count);
        var source = context["capabilities"]![0]!; var consumer = context["capabilities"]![1]!;
        Assert.True(JsonNode.DeepEquals(source["inputSchema"], consumer["inputSchema"]));
        Assert.Equal("allocate", source["requestBindings"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("finalize", consumer["requestBindings"]![0]!["value"]!.GetValue<string>());
        var inputId = source["inputSchema"]!["$ref"]!.GetValue<string>()["#/schemas/".Length..];
        Assert.True(JsonNode.DeepEquals(input, context["schemas"]![inputId]));
        Assert.False(context["schemas"]![inputId]!["properties"]!.AsObject().ContainsKey("inventedAssociationId"));
        Assert.Empty(context["schemas"]![source["outputSchema"]!["$ref"]!.GetValue<string>()["#/schemas/".Length..]]!.AsObject());
        context["schemas"]![inputId]!["properties"]!["body"]!["type"] = "integer";
        Assert.Equal("string", input["properties"]!["body"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Restore each affected component", "Remove all temporary resources")]
    [InlineData("Restaurer chaque composant concerné", "Supprimer toutes les ressources temporaires")]
    public async Task EvidenceRepairCannotDropFindingsOrTreatGeneratedQuestionsAsIntent(string requirement, string cleanup)
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.Request.Prompt = requirement + "\n" + cleanup;
        state.Answers.Add(new("Invented materialized context", new JsonObject { ["answer"] = "yes" }));
        var calls = 0; var runtime = new FakeRuntime { OnCall = (_, request, _) =>
        {
            calls++;
            if (calls == 2)
            {
                Assert.Single(request.StructuredOutputSchema!["properties"]!.AsObject());
                var allowed = request.StructuredOutputSchema["properties"]!["finding_1_evidence"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>());
                Assert.DoesNotContain("Invented materialized context", allowed); Assert.Contains(requirement, allowed);
                Assert.DoesNotContain("Graph:", request.Prompt);
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["finding_1_evidence"] = requirement } });
            }
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray(
                Finding("CLEANUP", cleanup), Finding("OBSERVATION", "Invented materialized context")) } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Contains(state.Diagnostics, d => d.Code == "CLEANUP"); Assert.Contains(state.Diagnostics, d => d.Code == "OBSERVATION");
        Assert.DoesNotContain(state.Diagnostics, d => d.Code.StartsWith("SEMANTIC_REVIEW", StringComparison.Ordinal));
        JsonObject Finding(string code, string evidence) => new() { ["code"] = code, ["workflow"] = "main", ["location"] = "/workflows/0/steps/0/input",
            ["message"] = code + " must preserve the requirement.", ["evidence"] = evidence, ["blocking"] = true };
    }

    [Fact]
    public async Task CollectionCleanupAndEnumFindingsSurviveBehaviorReassessmentAndRestart()
    {
        // Sanitized mixed findings from live final review: changing iteration must
        // retain the independent implementation defects and the answered intent.
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.Request.Prompt = "Read the complete collection, remove every created directory, and preserve the chosen enum value.";
        state.BehaviorPlan = BehaviorPlan(); state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Answers.Add(new("Require confirmation?", new JsonObject { ["answer"] = "yes" }));
        state.ClarificationForms = 1; state.ClarificationQuestions = 1;
        var findings = new JsonArray(new[]
        {
            (Code: "COLLECTION_INCOMPLETE", Field: "behavior", Evidence: "complete collection", Message: "Only the first page is read; repeat until the complete collection is observed."),
            (Code: "CLEANUP_INCOMPLETE", Field: "input", Evidence: "every created directory", Message: "Cleanup must cover every created directory, including the parent allocation."),
            (Code: "ENUM_MEANING_CHANGED", Field: "input", Evidence: "chosen enum value", Message: "The mapping replaces a valid source outcome with the default instead of preserving its meaning.")
        }.Select(f => (JsonNode)new JsonObject
        { ["code"] = f.Code, ["workflow"] = "main", ["location"] = "/workflows/0/steps/0/" + f.Field, ["evidence"] = f.Evidence, ["message"] = f.Message, ["blocking"] = true }).ToArray());
        var runtime = new FakeRuntime { OnCall = (phase, _, _) =>
        {
            Assert.Equal("semantic_review", phase);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = findings.DeepClone() } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        state = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        Assert.Equal(PlanningPhase.Behavior, state.CurrentPhase); Assert.Null(state.ApprovedBehaviorHash); Assert.Null(state.ApprovedHash);
        Assert.Equal(3, state.Diagnostics.Count); Assert.Equal(3, state.Attempts.Last().Diagnostics.Count);
        foreach (var finding in state.Diagnostics) Assert.Contains(finding.Message, state.Feedback);
        Assert.NotNull(state.PreviousGraph); Assert.Single(state.Answers); Assert.Equal(1, state.ClarificationQuestions);
    }

    [Fact]
    public async Task IterationCorrectionRequiresNewBehaviorReviewAndRetainsAnswers()
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.BehaviorPlan = BehaviorPlan(); state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Answers.Add(new("Keep the greeting?", new JsonObject { ["answer"] = "yes" })); state.ClarificationForms = 1; state.ClarificationQuestions = 1;
        var runtime = new FakeRuntime { OnCall = (phase, _, _) =>
        {
            Assert.Equal("semantic_review", phase);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray(new JsonObject
            {
                ["code"] = "COVERAGE", ["workflow"] = "main", ["location"] = "/workflows/0/steps/0/behavior",
                ["evidence"] = "Return a greeting", ["message"] = "Make the intended repeated operation explicit in the reviewed behavior.", ["blocking"] = true
            }) } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Created, state.Status); Assert.Equal(PlanningPhase.Behavior, state.CurrentPhase);
        Assert.Null(state.ApprovedBehaviorHash); Assert.Null(state.ApprovedHash); Assert.Null(state.Graph);
        Assert.NotNull(state.PreviousGraph); Assert.NotNull(state.Preparation); Assert.Single(state.Answers);
        Assert.Equal(1, state.ClarificationForms); Assert.Contains("repeated operation", state.Feedback);
        Assert.DoesNotContain(runtime.Phases, phase => phase is "repair_unit" or "repair_fragment");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InvalidAssessmentRepairsItsEvidenceOrPausesWithoutEditingTheExecutable(bool invalidWorkflow, bool exhausted)
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        var original = PlanningGraphCompiler.Fingerprint(state.Graph); var calls = 0;
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("semantic_review", phase); calls++;
            if (calls == 2) Assert.Contains("assessment contract only", request.Prompt);
            if (calls == 2 && !invalidWorkflow)
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["finding_0_evidence"] = exhausted ? "invented request evidence" : "Return a greeting" } });
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray(new JsonObject
            {
                ["code"] = "SEMANTIC_NOTE", ["workflow"] = invalidWorkflow && calls == 1 ? "invented" : "main",
                ["location"] = "/workflows/0/steps/0/input",
                ["evidence"] = !invalidWorkflow && (calls == 1 || exhausted) ? "invented request evidence" : "Return a greeting",
                ["message"] = "The greeting requirement is explicitly retained.", ["blocking"] = false
            }) } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls); Assert.Equal(0, state.RepairAttempt);
        Assert.Equal(original, PlanningGraphCompiler.Fingerprint(state.Graph!));
        Assert.Equal(exhausted ? PlanningStatus.Recovery : PlanningStatus.FinalReview, state.Status);
        if (exhausted)
        {
            Assert.Equal("semantic_review", state.CurrentPhase);
            Assert.Contains(state.Diagnostics, d => d.Code == "SEMANTIC_REVIEW_EVIDENCE_INVALID");
            Assert.DoesNotContain(state.Diagnostics, d => d.Code == "GRAPH_VALIDATION");
        }
    }
}
