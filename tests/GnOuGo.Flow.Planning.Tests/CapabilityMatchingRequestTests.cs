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
