using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityMatchingRequestTests
{
    [Fact]
    public void RepeatedCatalogEncodingMetadataIsSentOnce()
    {
        var description = string.Concat(Enumerable.Repeat("A declared source supplies this exact immutable argument and result contract. ", 20));
        var entries = Enumerable.Range(0, 8).Select(i => new CapabilityCatalogEntry("entry" + i, "mcp", "provider", "tool", "method" + i,
            description, [], description + i, [], [], null, null)).ToArray();
        var compact = CapabilityInventoryContext.MatchingCatalog(new(entries, string.Join('\n', entries.Select(e => e.Id + " " + e.Card))));
        var expanded = PromptContextTests.Expand(JsonNode.Parse(compact[(compact.IndexOf('\n') + 1)..])!)!;
        var groups = expanded["groups"]!.AsArray();
        Assert.Equal(8, groups.Count);
        Assert.All(groups.OfType<JsonObject>(), group => Assert.False(group.ContainsKey("separator")));
        var repeated = groups.DeepClone().AsArray();
        foreach (var group in repeated.OfType<JsonObject>()) group["separator"] = expanded["separator"]!.DeepClone();
        Assert.True(PlanningJsonTransport.EstimateInputTokens(expanded.ToJsonString(), new()) < PlanningJsonTransport.EstimateInputTokens(repeated.ToJsonString(), new()));
        foreach (var group in groups.OfType<JsonObject>())
            foreach (var entry in group["entries"]!.AsObject())
                Assert.Equal(entries.Single(e => e.Id == entry.Key).Card,
                    group["prefix"]!.ToString() + string.Join(expanded["separator"]!.ToString(), entry.Value!.AsArray().Select(p => p!.ToString())) + group["suffix"]);
    }

    [Fact]
    public void CatalogFactoringPreservesUnicodeAndLiteralSeparators()
    {
        var shared = string.Concat(Enumerable.Repeat("Declared contract: 🧪 Unicode é accents; original \"quotes\", commas, newlines\n", 20));
        var entries = new[] { "🧪", "🧬", "literal" }.Select((suffix, index) => new CapabilityCatalogEntry("c" + index, "mcp", "provider", "tool", "method",
            "Declared metadata", [], shared + suffix + " -> tail 🧪", [], [], null, null)).ToArray();
        var original = string.Join('\n', entries.Select(e => e.Id + " " + e.Card));
        var compact = CapabilityInventoryContext.MatchingCatalog(new(entries, original));
        Assert.True(compact.Length < original.Length);
        var expanded = PromptContextTests.Expand(JsonNode.Parse(compact[(compact.IndexOf('\n') + 1)..])!)!;
        foreach (var group in expanded["groups"]!.AsArray().OfType<JsonObject>())
            foreach (var entry in group["entries"]!.AsObject())
                Assert.Equal(entries.Single(e => e.Id == entry.Key).Card,
                    group["prefix"]!.ToString() + string.Join(expanded["separator"]!.ToString(), entry.Value!.AsArray().Select(p => p!.ToString())) + group["suffix"]);
    }
    [Fact]
    public void SavedCodeReviewSelectorCatalogIsLosslesslySharedWithFewerTokens()
    {
        var data = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CodeReviewMatchingCatalog.json")))!.AsArray();
        var entries = data.Select(n => new CapabilityCatalogEntry(n!["id"]!.ToString(), n["resolution"]!.ToString(), n["server"]?.ToString(), n["kind"]?.ToString(), n["method"]!.ToString(),
            "Frozen public metadata", [], n["card"]!.ToString(), [], [], null, null)).ToArray();
        var original = string.Join('\n', entries.Select(e => e.Card));
        var compact = CapabilityInventoryContext.MatchingCatalog(new(entries, original));
        Assert.True(compact.Length < original.Length);
        var expanded = PromptContextTests.Expand(JsonNode.Parse(compact[(compact.IndexOf('\n') + 1)..])!)!;
        var recovered = expanded["groups"]!.AsArray().OfType<JsonObject>().SelectMany(group => group["entries"]!.AsObject().Select(e =>
            new KeyValuePair<string, string>(e.Key, group["prefix"]!.ToString() + string.Join(expanded["separator"]!.ToString(), e.Value!.AsArray().Select(p => p!.ToString())) + group["suffix"]))).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(entries.Length, recovered.Count);
        Assert.All(entries, entry => Assert.Equal(entry.Card, recovered[entry.Id]));
        var schema = PlanningHoleRequests.Object(("result", PlanningHoleRequests.Type("string")));
        Assert.True(PlanningJsonTransport.EstimateInputTokens(compact, schema) < PlanningJsonTransport.EstimateInputTokens(original, schema));
    }
    [Fact]
    public void CompilerOwnedObligationsAreNotCapabilityRequirements()
    {
        const string structural = "Run in finalization after success, failure and cancellation.";
        var operation = new CapabilityInventoryOperation("op", "Release the declared owned resource", true, "external_effect", "lifecycle")
        {
            CoverageRequirementEvidence = [new("primitive", "request", 0, 7, "Release"), new("structure", "request", 8, structural.Length, structural)],
            WorkflowStructureCoverageRequirementIds = ["structure"]
        };
        var inventory = new CapabilityInventory(true, [operation], [], []);
        var catalog = new CapabilityCatalog([new("cap", "mcp", "provider", "tool", "operation", "Declared release", [], "Release a declared resource", [], [], null, null)], "Declared release");
        var request = Assert.Single(CapabilityMatchingRequests.Build(inventory, catalog, new Dictionary<string, IReadOnlySet<string>> { ["op"] = new HashSet<string>(["cap"]) }, 12000));
        var start = request.Prompt.IndexOf("<runtime_inventory>", StringComparison.Ordinal) + "<runtime_inventory>".Length;
        var end = request.Prompt.IndexOf("</runtime_inventory>", StringComparison.Ordinal);
        var context = JsonNode.Parse(request.Prompt[start..end])!;
        Assert.Contains("Release", context["operations"]![0]!["coverage_requirements"]!.ToJsonString());
        Assert.DoesNotContain(structural, context["operations"]![0]!["coverage_requirements"]!.ToJsonString());
        Assert.Equal(structural, context["planner_owned_requirements"]!["op"]![0]!.ToString());
        Assert.Equal("structure", Assert.Single(operation.WorkflowStructureCoverageRequirementIds));
        Assert.Equal(2, operation.CoverageRequirementEvidence.Count); // The governing contract is retained for behavior and compilation.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StructureOnlyEvidenceHasPlannerOwnershipInInitialAndRepairMatching(bool repair)
    {
        const string structure = "Release the owned resource after success, failure or cancellation; retain the original failure.";
        const string primitiveContract = "Deletes the resource supplied through the required original owned_resource argument.";
        var operation = new CapabilityInventoryOperation("effect", structure, true, "external_effect", "lifecycle")
        {
            InputOperationIds = ["producer"],
            CoverageRequirementEvidence = [new("structure", "request", 0, structure.Length, structure)],
            WorkflowStructureCoverageRequirementIds = ["structure"]
        };
        var inventory = new CapabilityInventory(true, [new("producer", "Receive the declared original resource", true, "local_processing", "none"), operation],
            [new("ownership", "Only the original owned artifact is authorized.", true, "workflow_policy")], []);
        var catalog = new CapabilityCatalog([new("cap", "mcp", "provider", "tool", "operation", primitiveContract, [], primitiveContract, [], [], null, null)], primitiveContract);
        var previous = repair ? new CapabilityMatchingEvaluation([new(operation, "unavailable", "Missing workflow scheduling", [], [])], [], [], true) : null;
        var request = Assert.Single(CapabilityMatchingRequests.Build(inventory, catalog,
            new Dictionary<string, IReadOnlySet<string>> { ["effect"] = new HashSet<string>(["cap"]) }, 12000, previous));
        var start = request.Prompt.IndexOf("<runtime_inventory>", StringComparison.Ordinal) + "<runtime_inventory>".Length;
        var end = request.Prompt.IndexOf("</runtime_inventory>", StringComparison.Ordinal);
        var context = JsonNode.Parse(request.Prompt[start..end])!;
        Assert.Equal(structure, context["planner_owned_requirements"]?["effect"]?[0]?.GetValue<string>());
        var matchedOperation = context["operations"]![0]!.AsObject();
        Assert.False(matchedOperation.ContainsKey("workflow_requirements"));
        Assert.False(matchedOperation.ContainsKey("coverage_requirements"));
        Assert.Equal("producer", matchedOperation["input_operation_ids"]![0]!.GetValue<string>());
        Assert.Contains(primitiveContract, request.Prompt);
        Assert.Equal("Only the original owned artifact is authorized.", context["required_workflow_policies"]!["ownership"]!.GetValue<string>());
        Assert.Equal(structure, operation.CoverageRequirementEvidence.Single().Excerpt);
        Assert.Equal("structure", Assert.Single(operation.WorkflowStructureCoverageRequirementIds));
        Assert.Equal("effect", Assert.Single(request.Schema["properties"]!["operation_matches"]!["properties"]!.AsObject()).Key);
    }
    [Fact]
    public void ScopedImplementationPolicySurvivesSelectionAndMatchingWithoutGlobalDenial()
    {
        const string policy = "For the declared analysis, compose the exact preparation and analysis capabilities; another implementation is not authorized.";
        var inventory = new CapabilityInventory(true, [new("op", "Analyze the declared input", true, "external_effect", "execute")],
            [new("scope", policy, true, "workflow_policy")], []);
        var operations = new HashSet<string>(["op"], StringComparer.Ordinal);
        var physical = new PhysicalCapabilityCatalog([new("physical", "provider", "tool", "analyze", "Declared analysis", [])], 50);
        var page = Assert.Single(CapabilitySelectionRequests.Build(inventory, physical, false, operations, new HashSet<string>(), 12000));
        Assert.Contains(policy, page.Prompt);
        var catalog = new CapabilityCatalog([new("cap", "mcp", "provider", "tool", "analyze", "Declared analysis", [], "Declared analysis", [], [], null, null)], "Declared analysis");
        var request = Assert.Single(CapabilityMatchingRequests.Build(inventory, catalog, new Dictionary<string, IReadOnlySet<string>> { ["op"] = new HashSet<string>(["cap"]) }, 12000));
        Assert.Contains(policy, request.Prompt);
        Assert.Empty(request.Schema["properties"]!["constraint_matches"]!["properties"]!.AsObject());
        Assert.Equal("workflow_policy", inventory.Constraints.Single().EnforcementKind);
    }
    [Fact]
    public void ExclusiveConditionalDomainRejectsASingleAlternativeBeforeDispatch()
    {
        var inventory = new CapabilityInventory(true,
            [new("decision", "Business decision", true, "local_processing", "none"),
             new("effect", "Declared conditional effect", true, "external_effect", "write", DecisionSourceOperationId: "decision")], [], []);
        var catalog = new CapabilityCatalog([new("a", "mcp", "provider", "tool", "method", "First choice", [], "First choice", [], [], null, null),
            new("b", "mcp", "provider", "tool", "method", "Second choice", [], "Second choice", [], [], null, null)], "");
        var request = Assert.Single(CapabilityMatchingRequests.Build(inventory, catalog,
            new Dictionary<string, IReadOnlySet<string>> { ["effect"] = new HashSet<string>(["a", "b"], StringComparer.Ordinal) }, 12000));
        var candidate = JsonNode.Parse("""{"operation_matches":{"effect":{"status":"conditional","reason":"Declared outcome","catalog_ids":["a"],"candidate_catalog_ids":[],"decision_operation_id":"decision","conditional_mode":"exactly_one"}},"constraint_matches":{}}""")!;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(candidate, request.Schema));
        candidate["operation_matches"]!["effect"]!["catalog_ids"] = new JsonArray("a", "b");
        Assert.Empty(PlanningContractValidation.ValidateInstance(candidate, request.Schema));
    }

    [Fact]
    public void RequiredPolicyMetadataIsSharedWithoutDroppingIdsOrGoverningText()
    {
        var policies = Enumerable.Range(0, 16).Select(i => new CapabilityInventoryConstraint("p" + i, "Declared required policy " + i, true, "workflow_policy")).ToArray();
        var inventory = new CapabilityInventory(true, [new("op", "Declared operation", true, "external_effect", "read")],
            [.. policies, new("denied", "Declared denial", true, "exact_denial"), new("optional", "Optional policy", false, "workflow_policy")], []);
        var prompt = CapabilityInventoryContext.BuildCapabilityMatchingPrompt(inventory, new([], ""));
        var start = prompt.IndexOf("<runtime_inventory>", StringComparison.Ordinal) + "<runtime_inventory>".Length;
        var end = prompt.IndexOf("</runtime_inventory>", StringComparison.Ordinal);
        var context = JsonNode.Parse(prompt[start..end])!.AsObject();
        Assert.Equal(16, context["required_workflow_policies"]!.AsObject().Count);
        Assert.All(policies, policy => Assert.Equal(policy.Description, context["required_workflow_policies"]![policy.Id]!.ToString()));
        Assert.Equal(2, context["constraints"]!.AsArray().Count);
        var previous = context.DeepClone().AsObject(); previous.Remove("required_workflow_policies");
        foreach (var policy in policies) previous["constraints"]!.AsArray().Add(new JsonObject
        { ["id"] = policy.Id, ["description"] = policy.Description, ["required"] = true, ["enforcement_kind"] = "workflow_policy" });
        Assert.True(PlanningJsonTransport.EstimateInputTokens(previous.ToJsonString(), new()) - PlanningJsonTransport.EstimateInputTokens(context.ToJsonString(), new()) > 300);
    }

    [Fact]
    public async Task ValidatedLocalOperationsAndEmptyAuthorityPoliciesNeedNoMatchingModelCall()
    {
        var inventory = new CapabilityInventory(true, [new("local", "Pure calculation", true, "local_processing", "none")],
            [new("policy", "Declared workflow policy", true, "workflow_policy"), new("denial", "Declared exact denial", true)], []);
        var ctx = new StepExecutionContext { PreparationCheckpoint = new() };
        var client = new LocalMatcher();
        var response = await CapabilityMatchingRequests.CallAsync(ctx, client, inventory, new([], ""), [], null, "fixture", "low", TestContext.Current.CancellationToken);
        Assert.Equal(0, client.Calls);
        var schema = CapabilityMatchAssessment.BuildTypedCapabilityMatchingSchema(inventory, new([], ""));
        Assert.Empty(PlanningContractValidation.ValidateInstance(response.Json, schema));
        Assert.True(CapabilityMatchAssessment.ParseCapabilityMatchingEvaluation(response.Json!.AsObject(), inventory, new([], "")).ContractValid);
    }

    [Fact]
    public void MatchingRepairExposesOnlyFailingDecisions()
    {
        var inventory = new CapabilityInventory(true,
            [new("passed", "Declared read", true, "external_effect", "read"), new("failed", "Declared write", true, "external_effect", "write")], [], []);
        var catalog = new CapabilityCatalog([new("cap", "mcp", "provider", "tool", "method", "Declared operation", [], "Declared operation", [], [], null, null)], "Declared operation");
        var scopes = inventory.Operations.ToDictionary(o => o.Id, _ => (IReadOnlySet<string>)new HashSet<string>(["cap"], StringComparer.Ordinal));
        var previous = new CapabilityMatchingEvaluation(
            [new(inventory.Operations[0], "matched", "Validated", ["cap"], []), new(inventory.Operations[1], "unavailable", "Required argument", [], [])], [],
            [new("failed", "Declared write", true, "unavailable", "Required argument has no proven binding.", [])], true);
        var request = Assert.Single(CapabilityMatchingRequests.Build(inventory, catalog, scopes, 12000, previous));
        Assert.Equal("failed", request.Owner);
        Assert.Equal("failed", Assert.Single(request.Schema["properties"]!["operation_matches"]!["properties"]!.AsObject()).Key);
        Assert.Contains("Required argument has no proven binding", request.Prompt);
        Assert.DoesNotContain("Validated", request.Prompt);
    }

    [Fact]
    public void MissingOrUnknownPhysicalSelectionFailsClosed()
    {
        var inventory = new CapabilityInventory(true, [new("op", "Declared read", true, "external_effect", "read")], [], []);
        var catalog = new CapabilityCatalog([], "");
        Assert.Throws<WorkflowRuntimeException>(() => CapabilityMatchingRequests.CandidateScopes(inventory, catalog, [], null));
        var selection = new PhysicalCandidateSelection(new Dictionary<string, IReadOnlyList<string>> { ["op"] = new[] { "unknown" } }, new Dictionary<string, IReadOnlyList<string>>(), false);
        Assert.Throws<WorkflowRuntimeException>(() => CapabilityMatchingRequests.CandidateScopes(inventory, catalog, [], selection));
    }

    [Fact]
    public async Task CompletedMatchingScopesAreValidatedAndReusedAfterRestart()
    {
        var inventory = new CapabilityInventory(true, [new("op", "Explicit confirmation", true, "human_interaction", "none")], [], []);
        var ctx = new StepExecutionContext
        {
            Engine = new WorkflowEngine(), Step = new() { Source = new() { Id = "planning", Type = "workflow.plan" } },
            PreparationCheckpoint = new(), PlanningGeneration = new()
        };
        var client = new LocalMatcher();
        var catalog = new CapabilityCatalog([new("cap", "native", null, null, "human.input", "Human input", [], "Human input", [], [], null, null)], "Human input");
        var first = await CapabilityMatchingRequests.CallAsync(ctx, client, inventory, catalog, [], null, "fixture", "low", TestContext.Current.CancellationToken);
        ctx.PreparationCheckpoint = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(ctx.PreparationCheckpoint, PlanningJsonContext.Default.PlanningPreparationCheckpoint), PlanningJsonContext.Default.PlanningPreparationCheckpoint)!;
        var replay = await CapabilityMatchingRequests.CallAsync(ctx, client, inventory, catalog, [], null, "fixture", "low", TestContext.Current.CancellationToken);
        Assert.Equal(1, client.Calls);
        Assert.True(JsonNode.DeepEquals(first.Json, replay.Json));
        var retained = ctx.PreparationCheckpoint.ValidatedResults.Single().Value!;
        retained["operation_matches"]!["op"]!["catalog_ids"] = new JsonArray("unknown");
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => CapabilityMatchingRequests.CallAsync(ctx, client, inventory, catalog, [], null, "fixture", "low", TestContext.Current.CancellationToken));
        Assert.Equal(1, client.Calls);
    }

    private sealed class LocalMatcher : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LLMResponse { Json = JsonNode.Parse("""{"operation_matches":{"op":{"status":"matched","catalog_ids":["cap"],"candidate_catalog_ids":[],"decision_operation_id":"","conditional_mode":"","reason":"Declared human interaction"}},"constraint_matches":{}}""") });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedMatchingRepairRetainsDecisionsAndStopsWithoutAnotherRequest(bool hasPartialCandidate)
    {
        var inventory = new CapabilityInventory(true, [new("op", "Explicit confirmation", true, "human_interaction", "none")], [], []);
        var catalog = new CapabilityCatalog([new("cap", "native", null, null, "human.input", "Human input", [], "Human input", [], [], null, null)], "Human input");
        var previous = new CapabilityMatchingEvaluation([new(inventory.Operations[0], "unavailable", "Missing decision", [], [])], [], [], true);
        var retained = CapabilityMatchAssessment.TypedMatchingCandidate(previous);
        var state = new PlanningSnapshot { PreparationCheckpoint = new() };
        state.PreparationCheckpoint.ValidatedResults["matching_candidate"] = retained.DeepClone();
        var partial = await new LocalMatcher().CallAsync(new(), TestContext.Current.CancellationToken);
        var runtime = new TypedPlannerTests.FakeRuntime
        {
            OnCall = (_, _, _) => Task.FromResult(new LLMResponse { CompletionStatus = "output_limit", Json = hasPartialCandidate ? partial.Json : null,
                Usage = new JsonObject { ["output_tokens"] = 8192 } })
        };
        var ctx = new StepExecutionContext
        {
            Engine = new WorkflowEngine(), Step = new() { Source = new() { Id = "planning", Type = "workflow.plan" } },
            PreparationCheckpoint = state.PreparationCheckpoint, PlanningGeneration = new(),
            PlanningModelDispatcher = (request, phase, ct) => PlanningModelCalls.CallAsync(state, runtime, phase, request, ct)
        };
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => CapabilityMatchingRequests.CallAsync(ctx, new LocalMatcher(), inventory, catalog,
            [], null, "fixture", "low", TestContext.Current.CancellationToken, previous));
        Assert.Equal("MODEL_OUTPUT_LIMIT", error.Code);
        Assert.Equal("workflow.plan.capability_matching_repair", Assert.Single(runtime.Phases));
        Assert.True(JsonNode.DeepEquals(retained, state.PreparationCheckpoint.ValidatedResults["matching_candidate"]));
        Assert.Single(state.PreparationCheckpoint.ValidatedResults);
        Assert.Empty(state.Construction.PendingCalls);
        Assert.Equal("receipt", Assert.Single(state.RequestAccounting).Evidence);
        Assert.Equal(8192, state.RequestAccounting[0].OutputTokens);
        Assert.Null(state.BehaviorPlan); Assert.Null(state.Graph);
    }

    [Fact]
    public void MatchingScopesKeepUnrelatedImplementationsOutOfPromptsAndResponseSchemas()
    {
        var inventory = new CapabilityInventory(true,
            [new("first", "First declared operation", true, "external_effect", "read"),
             new("second", "Second declared operation", true, "external_effect", "read") { InputOperationIds = ["first"] }], [], []);
        var entries = Enumerable.Range(0, 40).Select(i => new CapabilityCatalogEntry("cap" + i, "mcp", "provider", "tool", "method" + i,
            "Declared metadata", [], "metadata" + i + ":" + new string('x', 900), [], [], null, null)).ToArray();
        var catalog = new CapabilityCatalog(entries, string.Join('\n', entries.Select(e => e.Card)));
        var scopes = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["first"] = entries.Take(20).Select(e => e.Id).ToHashSet(StringComparer.Ordinal),
            ["second"] = entries.Skip(20).Select(e => e.Id).ToHashSet(StringComparer.Ordinal)
        };
        var requests = CapabilityMatchingRequests.Build(inventory, catalog, scopes, 12000);
        Assert.Equal(2, requests.Count);
        foreach (var request in requests)
        {
            Assert.InRange(PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.Schema), 1, 12000);
            Assert.Empty(PlanningContractValidation.ValidateSchema(request.Schema, true));
            var own = scopes[request.Owner].First();
            var foreign = scopes[request.Owner == "first" ? "second" : "first"].First();
            var response = new JsonObject
            {
                ["operation_matches"] = new JsonObject
                {
                    [request.Owner] = new JsonObject
                    {
                        ["status"] = "matched", ["reason"] = "Declared contract", ["catalog_ids"] = new JsonArray(own),
                        ["candidate_catalog_ids"] = new JsonArray(), ["decision_operation_id"] = "", ["conditional_mode"] = ""
                    }
                },
                ["constraint_matches"] = new JsonObject()
            };
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.Schema));
            response["operation_matches"]![request.Owner]!["catalog_ids"] = new JsonArray(foreign);
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, request.Schema));
            Assert.DoesNotContain(entries.Single(e => e.Id == foreign).Card, request.Prompt);
        }
        Assert.Contains("First declared operation", requests[1].Prompt);
        Assert.Contains("Second declared operation", requests[0].Prompt);
        Assert.Contains("Declared downstream operations, matched separately:", requests[0].Prompt);
    }

    [Fact]
    public void CrossOperationPolicyKeepsConsumerBoundaryWithoutGrantingConsumerAuthority()
    {
        var inventory = new CapabilityInventory(true,
            [new("prepare", "Prepare an immutable comparison", true, "external_effect", "read"),
             new("analyze", "Analyze the prepared comparison", true, "external_effect", "execute") { InputOperationIds = ["prepare"] },
             new("unrelated", "Unrelated external effect", true, "external_effect", "read")],
            [new("chain", "The analysis implementation uses declared preparation followed by declared analysis.", true, "workflow_policy")], []);
        var catalog = new CapabilityCatalog(inventory.Operations.Select(o => new CapabilityCatalogEntry("cap_" + o.Id, "mcp", "provider", "tool", o.Id,
            o.Description, [], "contract_" + o.Id, [], [], null, null)).ToArray(), "");
        var scopes = inventory.Operations.ToDictionary(o => o.Id, o => (IReadOnlySet<string>)new HashSet<string>(["cap_" + o.Id], StringComparer.Ordinal));
        var request = CapabilityMatchingRequests.Build(inventory, catalog, scopes, 12000).Single(r => r.Owner == "prepare");
        Assert.Contains("Analyze the prepared comparison", request.Prompt);
        Assert.DoesNotContain("Unrelated external effect", request.Prompt);
        Assert.DoesNotContain("contract_analyze", request.Prompt);
        Assert.DoesNotContain("cap_analyze", request.Schema.ToJsonString());
        Assert.Equal("prepare", Assert.Single(request.Schema["properties"]!["operation_matches"]!["properties"]!.AsObject()).Key);
        var previous = new CapabilityMatchingEvaluation(
            [new(inventory.Operations[0], "unavailable", "Missing downstream implementation", [], []),
             new(inventory.Operations[1], "matched", "Declared analysis", ["cap_analyze"], [])], [], [], true);
        var repaired = Assert.Single(CapabilityMatchingRequests.Build(inventory, catalog, scopes, 12000, previous));
        Assert.Contains("Retained downstream implementations", repaired.Prompt);
        Assert.Contains("\"method\":\"analyze\"", repaired.Prompt);
        Assert.DoesNotContain("cap_analyze", repaired.Schema.ToJsonString());
        Assert.DoesNotContain("contract_analyze", repaired.Prompt);
    }

    [Fact]
    public void AScopedHumanDecisionKeepsItsBooleanConditionalDomain()
    {
        var inventory = new CapabilityInventory(true,
            [new("permission", "Human permission", true, "human_interaction", "none"),
             new("effect", "Declared write", true, "external_effect", "write", DecisionSourceOperationId: "permission", AllowNoEffectOutcome: true)], [], []);
        var catalog = new CapabilityCatalog([new("cap", "mcp", "provider", "tool", "write", "Declared write", [], "Declared write", [], [], null, null)], "Declared write");
        var requests = CapabilityMatchingRequests.Build(inventory, catalog, new Dictionary<string, IReadOnlySet<string>> { ["effect"] = new HashSet<string>(["cap"], StringComparer.Ordinal) }, 12000);
        var effect = requests.Single(r => r.Owner == "effect");
        Assert.Contains("human_interaction", effect.Prompt);
        Assert.Contains("all_on_value", effect.Schema.ToJsonString());
        Assert.DoesNotContain("exactly_one", effect.Schema.ToJsonString());
    }
}
