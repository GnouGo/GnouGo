using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class SemanticGroundingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public void ReplanningFeedbackSharesMessagesWithoutDroppingLocationsOrSeverity()
    {
        var diagnostics = new List<PlanningDiagnostic> { new("INVALID", "/operations/first", "Preserve the complete diagnostic.", ValidationStage: "grounded", Rule: "opaque"),
            new("INVALID", "/operations/second", "Preserve the complete diagnostic.", ValidationStage: "grounded", Rule: "opaque"),
            new("INVALID", "/operations/optional", "Preserve the complete diagnostic.", Required: false, ValidationStage: "grounded", Rule: "opaque") };
        var prompt = PlanningJsonTransport.Diagnostics(diagnostics);
        Assert.Equal(2, prompt.Count);
        Assert.Equal(new[] { "/operations/first", "/operations/second" }, prompt[0]!["locations"]!.AsArray().Select(n => n!.ToString()));
        Assert.Equal(diagnostics[0].Message, prompt[0]!["message"]!.ToString()); Assert.Equal("opaque", prompt[0]!["rule"]!.ToString());
        Assert.True(prompt[0]!["required"]!.GetValue<bool>()); Assert.False(prompt[1]!["required"]!.GetValue<bool>());
        Assert.True(PlanningJsonTransport.Prompt(prompt).Length < JsonSerializer.Serialize(diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic).Length);
    }
    [Fact]
    public async Task UnambiguousActionsRemainAccountedForWithoutModelReselection()
    {
        var state = State(); state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages)
            state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "matched", page.CapabilityIds.Where(id => a == "collect" || id == "cap_1")
                .Select(id => new GroundingMatch(id, "Declared behavior")).ToList(), "Covered")).ToList()));
        var runtime = new TestRuntime { Respond = request =>
        {
            Assert.Equal(new[] { "collect" }, request.StructuredOutputSchema!["properties"]!["selections"]!["properties"]!.AsObject().Select(p => p.Key));
            Assert.Contains("establishedSelections", request.Prompt);
            return new() { Json = JsonNode.Parse("""{"selections":{"collect":{"capabilities":{"cap_0":true,"cap_1":false},"reason":"Direct declared reader"}}}""") };
        } };
        await CapabilitySelection.ApplyAsync(state, runtime, Ct);
        Assert.Equal(2, state.Grounding.Selections!.Count);
        Assert.Equal(new[] { "cap_1" }, state.Grounding.Selections.Single(s => s.ActionId == "release").CapabilityIds);
        Assert.Equal(1, state.ModelCalls);
    }
    [Fact]
    public void SelectionSchemaPreventsCrossActionIdentitiesAndDuplicateChoices()
    {
        var schema = CapabilitySelection.Schema([new("left", "matched", [new("renamed_a", "Reads the left source")], "Covered"),
            new("right", "matched", [new("renamed_b", "Releases the right resource")], "Covered")]);
        var response = JsonNode.Parse("""{"selections":{"left":{"capabilities":{"renamed_a":true},"reason":"Declared reader"},"right":{"capabilities":{"renamed_b":true},"reason":"Declared release"}}}""")!;
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, schema));
        response["selections"]!["left"]!["capabilities"]!["renamed_b"] = true;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, schema));
        response["selections"]!["left"]!["capabilities"] = new JsonArray("renamed_a", "renamed_a");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, schema));
    }
    [Fact]
    public async Task InvalidSelectionGetsLocatedFeedbackAndUnchangedRetryStops()
    {
        var state = State(); state.Grounding = CapabilityGrounder.Create(state);
        var explanation = 0;
        foreach (var page in state.Grounding.Pages)
            state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "matched", page.CapabilityIds.Select(id => new GroundingMatch(id, "Declared behavior")).ToList(), "Covered")).ToList()));
        var runtime = new TestRuntime { Respond = _ => new() { Json = new JsonObject { ["selections"] = new JsonObject(state.SemanticPlan!.Actions.Select(a =>
            new KeyValuePair<string, JsonNode?>(a.Id, new JsonObject { ["capabilities"] = new JsonObject { ["cap_0"] = false, ["cap_1"] = false }, ["reason"] = "Changed explanation " + ++explanation }))) } } };
        var invalid = await Assert.ThrowsAsync<PlanningResponseException>(() => CapabilitySelection.ApplyAsync(state, runtime, Ct));
        Assert.Contains(invalid.Diagnostics, d => d.Location == "/actions/collect" && d.Message.Contains("requires at least one", StringComparison.Ordinal));
        state.Diagnostics = invalid.Diagnostics;
        var repeated = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => CapabilitySelection.ApplyAsync(state, runtime, Ct, "replan"));
        Assert.Equal("REPLAN_NO_PROGRESS", repeated.Code);
        Assert.Contains("requires at least one", runtime.Calls[1].Prompt); Assert.Null(state.Grounding.Selections);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
    }
    [Theory]
    [InlineData("collect", false, "SEMANTIC_BINDING_BLOCKED")]
    [InlineData("invented", false, "BINDING_BLOCKER_INVALID")]
    [InlineData("collect", true, "BINDING_BLOCKER_INVALID")]
    public async Task MissingBindingPrerequisitesAreExplicitAndCannotHideExecutableWork(string action, bool executable, string code)
    {
        var state = State();
        var proposal = executable ? PlanningJsonTransport.Grounded(PlannerFixture.Greeting()) : PlanningJsonTransport.Grounded(new());
        proposal["blockedActions"] = new JsonArray(new JsonObject { ["actionId"] = action, ["reason"] = "The selected consumer requires original evidence that the producer does not expose." });
        var schema = PlanningSchemas.Grounded(["cap_0"]);
        var runtime = new TestRuntime { Respond = _ => new() { Json = PlanningJsonTransport.ModelGrounded(proposal, schema) } };
        var rejected = await Assert.ThrowsAsync<PlanningResponseException>(() => PlanningModelCalls.CallAsync(state, runtime, "binding", "Bind", schema, Ct));
        Assert.Equal(code, Assert.Single(rejected.Diagnostics).Code);
        Assert.Null(state.GroundedPlan); Assert.Null(state.PendingCall); Assert.Equal(1, state.ModelCalls);
        if (code == "SEMANTIC_BINDING_BLOCKED")
        {
            state.Diagnostics = rejected.Diagnostics; state.BindingProgress = new();
            runtime.Respond = request =>
            {
                Assert.Contains("Replan this business action/subgraph atomically", request.Prompt);
                return new() { Json = new JsonObject { ["actions"] = SemanticPlanning.Json(new() { Actions = [state.SemanticPlan!.Actions[0]] })["actions"]!.DeepClone(), ["questions"] = new JsonArray() } };
            };
            var result = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
            Assert.Equal(PlanningStatus.Stopped, result.Status);
            Assert.Contains(result.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
            Assert.Equal(1, result.ReplanAttempts);
        }
    }
    internal static SemanticPlan Request() => new() { Summary = "Collect evidence and release the resource", Actions = [
        new() { Id = "collect", Purpose = "Read the observation", Outputs = [new("evidence", "Observed data")] },
        new() { Id = "release", Purpose = "Release the acquired resource", Outputs = [new("released", "Resource released")] }] };
    internal static PlanningSession State(int count = 2)
    {
        var state = PlannerFixture.Session(); state.SemanticPlan = Request();
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"], Capabilities = Enumerable.Range(0, count).Select(i => new PlanningCapability
        { Id = "cap_" + i, Method = "renamed_" + i, StepType = "mcp.call", Description = "Declared operation " + i, EffectKind = i == 0 ? "read" : "lifecycle" }).ToList() };
        return state;
    }
    [Fact]
    public void SemanticResponseCannotSelectCapabilitiesOrExecutableFields()
    {
        var state = State(); var schema = SemanticPlanning.Schema(); var prompt = SemanticPlanning.Prompt(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.DoesNotContain("cap_0", prompt); Assert.DoesNotContain("renamed_0", prompt);
        foreach (var field in new[] { "capability", "arguments", "outputSchema", "retry", "expression", "stepType" })
        {
            var json = SemanticPlanning.Json(Request()); json["actions"]![0]![field] = "forbidden";
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(json, schema));
        }
    }
    [Fact]
    public void CoverageIncludesEveryCapabilityAndUnabridgedDescription()
    {
        var state = State(14); state.Request.Generation.MaxInputTokensPerRequest = 3500;
        foreach (var capability in state.Catalog!.Capabilities) capability.Description = new string('x', 1400) + "\nDeclared release operation at the end.";
        var coverage = CapabilityGrounder.Create(state);
        Assert.InRange(coverage.Pages.Count, 2, 7);
        foreach (var action in state.SemanticPlan!.Actions)
            Assert.Equal(state.Catalog.Capabilities.Select(c => c.Id).Order(), coverage.Pages.Where(p => p.ActionIds.Contains(action.Id)).SelectMany(p => p.CapabilityIds).Order());
        foreach (var page in coverage.Pages)
        {
            var prompt = CapabilityGrounder.Prompt(state, page);
            Assert.Contains("Declared release operation at the end.", prompt);
            Assert.True(PlanningJsonTransport.EstimateInputTokens(prompt, CapabilityGrounder.Schema(page)) <= 3500);
            var rows = JsonNode.Parse(prompt[prompt.IndexOf("\n{", StringComparison.Ordinal)..])!["capabilities"]!.AsArray();
            foreach (var row in rows)
            {
                var capability = state.Catalog.Capabilities.Single(c => c.Id == row![0]!.ToString());
                Assert.Equal(capability.Method, row![1]!.ToString()); Assert.Equal(capability.EffectKind, row[2]!.ToString());
                Assert.Equal(capability.Description, row[3]!.ToString()); Assert.True(JsonNode.DeepEquals(capability.Metadata, row[4]));
            }
        }
    }
    [Fact]
    public void CompleteMissStaysNoneOfTheAboveEvenWithCompatibleArguments()
    {
        var state = State(); state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages)
            state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "none_of_the_above", [], "No declared capability performs the required action.")).ToList()));
        Assert.All(CapabilityGrounder.Decisions(state), d => { Assert.Equal("none_of_the_above", d.Outcome); Assert.Empty(d.Matches); });
    }
    [Fact]
    public void ReaderOnlyCleanupCandidatesRemainACompleteCatalogMiss()
    {
        var state = State(1); state.SemanticPlan!.Actions = [new() { Id = "release", Kind = "cleanup", Purpose = "Release the acquired resource" }];
        state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages) state.Grounding.Results.Add(new(page.Id, [new("release", "matched", [new("cap_0", "Has compatible arguments")], "Proposed cleanup")]));
        var decision = Assert.Single(CapabilityGrounder.Decisions(state));
        Assert.Equal("none_of_the_above", decision.Outcome); Assert.Empty(decision.Matches);
    }
    [Fact]
    public void PartialCoverageAndChangedCatalogCannotAuthorizeBinding()
    {
        var state = State(); state.Grounding = CapabilityGrounder.Create(state);
        Assert.Throws<PlanningConflictException>(() => CapabilityGrounder.Decisions(state));
        foreach (var page in state.Grounding.Pages)
            state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "none_of_the_above", [], "No match.")).ToList()));
        state.Catalog!.Capabilities[0].EffectKind = "write";
        Assert.Throws<PlanningConflictException>(() => CapabilityGrounder.Decisions(state));
    }

    [Fact]
    public void AtomicSemanticReplacementReusesOnlyUnchangedActionCoverage()
    {
        var state = State(14); state.Request.Generation.MaxInputTokensPerRequest = 3500;
        foreach (var capability in state.Catalog!.Capabilities) capability.Description = new string('x', 1400);
        var before = state.SemanticPlan!; var previous = CapabilityGrounder.Create(state);
        foreach (var page in previous.Pages)
            previous.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "none_of_the_above", [], "No match in this catalog page.")).ToList()));
        state.SemanticPlan = JsonSerializer.Deserialize(JsonSerializer.Serialize(before, PlanningJsonContext.Default.SemanticPlan), PlanningJsonContext.Default.SemanticPlan)!;
        state.SemanticPlan.Actions[0].Purpose = "Read an alternative observation";
        state.Grounding = CapabilityGrounder.Reground(state, before, previous);
        Assert.All(state.Grounding.Results.SelectMany(r => r.Decisions), d => Assert.Equal("release", d.ActionId));
        var pending = state.Grounding.Pages.Where(p => state.Grounding.Results.All(r => r.PageId != p.Id)).ToArray();
        Assert.All(pending, p => Assert.Equal(new[] { "collect" }, p.ActionIds));
        foreach (var page in pending) state.Grounding.Results.Add(new(page.Id, [new("collect", "none_of_the_above", [], "The replacement still has no match.")]));
        Assert.Equal(2, CapabilityGrounder.Decisions(state).Count);
        state.Catalog.Capabilities[0].Description = "Changed catalog semantics";
        Assert.Throws<PlanningConflictException>(() => CapabilityGrounder.Decisions(state));
    }
    [Theory]
    [InlineData("unissued")]
    [InlineData("duplicate")]
    [InlineData("forced")]
    public void InvalidGroundingClassificationsFailDeterministically(string defect)
    {
        var page = new GroundingPage("page", ["collect"], ["cap_0"]);
        var decision = new JsonObject { ["actionId"] = "collect", ["outcome"] = "matched", ["reason"] = "Declared reader.",
            ["matches"] = new JsonArray(new JsonObject { ["capabilityId"] = defect == "unissued" ? "tool_name" : "cap_0", ["reason"] = "Declared behavior." }) };
        if (defect == "forced") decision["outcome"] = "none_of_the_above";
        var json = new JsonObject { ["decisions"] = new JsonArray(decision) };
        if (defect == "duplicate") json["decisions"]!.AsArray().Add(decision.DeepClone());
        Assert.Throws<PlanningResponseException>(() => CapabilityGrounder.Read(page, json));
    }
    [Fact]
    public void AnOversizedCatalogStopsBeforeReservingOrCharging()
    {
        var state = State(1); state.Catalog!.Capabilities[0].Description = new string('z', 60000);
        Assert.Throws<WorkflowRuntimeException>(() => CapabilityGrounder.Create(state));
        Assert.Null(state.PendingCall); Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }
    [Fact]
    public async Task NativePlanUsesSeparateSemanticAndBindingCallsAndSurvivesRestart()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        Assert.Equal(2, state.ModelCalls); Assert.NotNull(state.SemanticPlan); Assert.NotNull(state.GroundedPlan);
        Assert.Equal(PlanningPhase.Review, state.Phase); Assert.All(state.Scenarios, s => Assert.Equal("passed", s.Outcome));
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        var second = new TestRuntime(); var next = await new TypedWorkflowPlanner().AdvanceAsync(restored, new() { ExpectedRevision = restored.Revision }, second, Ct);
        Assert.Empty(second.Calls); Assert.Equal(state.ComputeArtifactHash(), next.ComputeArtifactHash());
    }
    [Theory]
    [InlineData("opaque")]
    [InlineData("conditional")]
    [InlineData("expression")]
    [InlineData("cycle")]
    public async Task RecordedInvalidDataflowNeverReachesTheBuilder(string defect)
    {
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, Ct);
        var plan = PlannerFixture.Greeting();
        switch (defect)
        {
            case "opaque":
                catalog.Capabilities.Add(new() { Id = "source", StepType = "mcp.call", InputSchema = new() { ["type"] = "object" } });
                plan.Operations.Insert(0, new InvokeGroundedOperation { Id = "source", Capability = "source" });
                ((CalculateGroundedOperation)plan.Operations[1]).Value = new() { Kind = "result", Source = "source", Path = ["invented"] }; break;
            case "conditional": plan.Operations[0].When = new() { Kind = "boolean", Boolean = false }; plan.Operations.Add(new CalculateGroundedOperation { Id = "consume", Value = new() { Kind = "result", Source = "greet" } }); break;
            case "expression": ((CalculateGroundedOperation)plan.Operations[0]).Value = new() { Kind = "compute", Text = "undeclaredDecision === true" }; break;
            case "cycle": plan.Operations[0].After = ["other"]; plan.Operations.Add(new CalculateGroundedOperation { Id = "other", After = ["greet"], Value = new() { Kind = "number", Number = 1 } }); break;
        }
        var result = GroundedPlanValidator.Validate(plan, catalog);
        Assert.Null(result.Plan); Assert.NotEmpty(result.Diagnostics);
    }
    [Fact]
    public async Task BusinessDefaultsAreLiteralAndMustSatisfyTheirContracts()
    {
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, Ct);
        foreach (var value in new GroundedValue[] { new(), new() { Kind = "input", Source = "a" }, new() { Kind = "string", Text = "wrong" } })
        {
            var plan = PlannerFixture.Greeting(); plan.Inputs.Add(new("a", new() { Type = "number" }, true, value));
            Assert.Null(GroundedPlanValidator.Validate(plan, catalog).Plan);
        }
        var valid = PlannerFixture.Greeting(); valid.Inputs.Add(new("a", new() { Type = "number", Nullable = true }, true, new()));
        Assert.NotNull(GroundedPlanValidator.Validate(valid, catalog).Plan);
    }
    [Fact]
    public void PromptContractsRetainEveryConstraintAndLiteralAnnotationNamedProperty()
    {
        var schema = JsonNode.Parse("""{"type":"object","description":"Documentation","properties":{"description":{"type":"string","description":"Label","enum":["a","b"],"default":"a"},"value":{"const":{"description":"Literal data","examples":[1]}}},"required":["description"],"additionalProperties":false}""")!.AsObject();
        var compact = PlanningJsonTransport.ContractPrompt(schema);
        Assert.Null(compact["description"]); Assert.NotNull(compact["properties"]!["description"]);
        Assert.True(JsonNode.DeepEquals(schema["properties"]!["value"]!["const"], compact["properties"]!["value"]!["const"]));
        foreach (var sample in new[] { "{}", "{\"description\":\"a\"}", "{\"description\":\"wrong\"}", "{\"description\":\"b\",\"extra\":1}" })
            Assert.Equal(PlanningContractValidation.ValidateInstance(JsonNode.Parse(sample), schema).Count, PlanningContractValidation.ValidateInstance(JsonNode.Parse(sample), compact).Count);
        var data = new JsonObject { ["text"] = "évaluer <not HTML>" };
        Assert.True(JsonNode.DeepEquals(data, JsonNode.Parse(PlanningJsonTransport.Prompt(data))));
    }

    [Fact]
    public void RepeatedContractFragmentsAreFactoredWithoutChangingAcceptedValues()
    {
        var item = JsonNode.Parse("""{"type":"object","properties":{"name":{"type":"string","minLength":2,"maxLength":20},"quantity":{"type":"integer","minimum":2,"maximum":8},"enabled":{"type":"boolean"}},"required":["name","quantity","enabled"],"additionalProperties":false}""")!;
        var schema = new JsonObject { ["type"] = "array", ["prefixItems"] = new JsonArray(item.DeepClone(), item.DeepClone(), item.DeepClone()), ["minItems"] = 3, ["maxItems"] = 3 };
        var compact = PlanningJsonTransport.ContractPrompt(schema);
        Assert.NotNull(compact["$defs"]); Assert.True(compact.ToJsonString().Length < schema.ToJsonString().Length);
        foreach (var sample in new[] { "[]", "[{}, {}, {}]", "[{\"name\":\"ab\",\"quantity\":2,\"enabled\":true},{\"name\":\"cd\",\"quantity\":8,\"enabled\":false},{\"name\":\"ef\",\"quantity\":4,\"enabled\":true}]" })
            Assert.Equal(PlanningContractValidation.ValidateInstance(JsonNode.Parse(sample), schema).Count == 0, PlanningContractValidation.ValidateInstance(JsonNode.Parse(sample), compact).Count == 0);
        Assert.True(JsonNode.DeepEquals(compact, PlanningJsonTransport.ContractPrompt(compact)));
    }
    [Fact]
    public async Task CompleteMatchesAreSelectedBeforeLargeBindingContractsAreLoaded()
    {
        var state = State(12);
        state.Catalog!.Capabilities[0].InputSchema = JsonNode.Parse("""{"type":"object","properties":{"evidence":{"type":"string","description":"Original comparison evidence from its declared producer; synthetic text is invalid."}},"required":["evidence"]}""")!.AsObject();
        state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages)
            state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "matched", page.CapabilityIds.Select(id => new GroundingMatch(id, "Declared viable implementation")).ToList(), "Viable implementations")).ToList()));
        var runtime = new TestRuntime { Respond = _ => new() { Json = new JsonObject { ["selections"] = new JsonObject(state.SemanticPlan!.Actions.Select(a => new KeyValuePair<string, JsonNode?>(a.Id, new JsonObject {
            ["capabilities"] = new JsonObject(state.Catalog.Capabilities.Select(c => new KeyValuePair<string, JsonNode?>(c.Id, JsonValue.Create(c.Id == "cap_0")))), ["reason"] = "Direct declared implementation" }))) } } };
        await CapabilitySelection.ApplyAsync(state, runtime, Ct);
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.DoesNotContain("Original comparison evidence", runtime.Calls[0].Prompt);
        Assert.Equal(12, CapabilityGrounder.Decisions(state)[0].Matches.Count);
        var prompt = CapabilityGrounder.BindingPrompt(state);
        Assert.Contains("cap_0", prompt); Assert.DoesNotContain("cap_11", prompt);
        Assert.Contains("Original comparison evidence", prompt);
        Assert.Throws<PlanningResponseException>(() => CapabilitySelection.Validate(state, [new("collect", ["unissued"], "Guess")]));
    }
    [Fact]
    public void GroundingCanRealizeBusinessEvaluationAndLeafCleanupWithDeclaredCapabilities()
    {
        var state = State(); state.SemanticPlan!.Actions[0].Kind = "calculate"; state.SemanticPlan.Actions[1].Kind = "cleanup";
        var pages = CapabilityGrounder.Create(state).Pages;
        Assert.All(state.SemanticPlan.Actions, a => Assert.Contains(pages, p => p.ActionIds.Contains(a.Id)));
        Assert.False(SemanticPlanning.External(state.SemanticPlan.Actions[0])); Assert.True(SemanticPlanning.External(state.SemanticPlan.Actions[1]));
    }
    [Fact]
    public async Task AnUnchangedSemanticReplanStopsAndPreservesUnrelatedActions()
    {
        var state = State(); var original = SemanticPlanning.Hash(state.SemanticPlan!);
        state.Diagnostics = [new("NONE_OF_THE_ABOVE", "/actions/collect", "No semantic match")];
        var runtime = new TestRuntime { Respond = _ => new() { Json = new JsonObject { ["actions"] = new JsonArray(SemanticPlanning.Json(new() { Actions = [state.SemanticPlan!.Actions[0]] })["actions"]![0]!.DeepClone()), ["questions"] = new JsonArray() } } };
        await SemanticReplanning.ApplyAsync(state, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(original, SemanticPlanning.Hash(state.SemanticPlan!));
        Assert.Equal(2, state.SemanticPlan!.Actions.Count); Assert.Equal(1, state.ReplanAttempts);
    }
    [Fact]
    public void FactoredBindingTransportPreservesNestedOperationsAndRejectsUnissuedFields()
    {
        var plan = PlannerFixture.Greeting();
        plan.Operations.Add(new ParallelGroundedOperation { Id = "branches", Branches = [new("one", new([new CalculateGroundedOperation { Id = "inner", Value = new() { Kind = "number", Number = 3 } }], new() { Kind = "result", Source = "inner" }))] });
        var flat = PlanningJsonTransport.Grounded(plan); var schema = PlanningSchemas.Grounded([]);
        var wire = PlanningJsonTransport.ModelGrounded(flat, schema);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(wire, schema));
        Assert.True(JsonNode.DeepEquals(flat, PlanningJsonTransport.ModelGrounded(wire, schema, unpack: true)));
        wire["operations"]![0]!["implementation"]!["policy"] = "bypass";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(wire, schema));
        Assert.True(schema.ToJsonString().Length < PlanningSchemas.Grounded().ToJsonString().Length);
    }
}
