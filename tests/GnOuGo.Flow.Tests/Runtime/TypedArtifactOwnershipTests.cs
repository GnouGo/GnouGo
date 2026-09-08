using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class TypedArtifactOwnershipTests
{
    [Fact]
    public async Task LocalOperationOwnershipAllowsValidatedNativeControlFlow()
    {
        var (runtime, request, preparation, _, _) = await Prepare("provider", "apply");
        preparation.RuntimeState["capabilities"]!.AsArray().Add(new JsonObject { ["id"] = "local", ["description"] = "Local condition", ["required"] = true,
            ["resolution"] = "local", ["requestBindings"] = new JsonArray(), ["operationId"] = "local", ["executionKind"] = "local_processing", ["externalEffectKind"] = "none" });
        preparation.Capabilities.Add(new() { Id = "local_cap", Description = "Local condition", StepType = "set", Resolution = "local", OperationIds = ["local"] });
        var bindings = Bindings(preparation); bindings.Add(new("main", "local_condition", "local_cap"));
        var yaml = Yaml("provider", "apply") + "\n      - id: local_condition\n        type: switch\n        expr: \"${'done'}\"\n        cases: [{value: done, steps: []}]\n        default: []\n";
        Assert.Empty(await runtime.ValidateAsync(yaml, request, preparation, bindings, Ct));
    }

    [Theory]
    [InlineData("provider", "apply")]
    [InlineData("renamed", "appliquer")]
    public async Task SharedConsentRetainsDistinctOwnersOfIdenticalPhysicalCalls(string server, string method)
    {
        var (runtime, request, preparation, factory, writes) = await Prepare(server, method);
        var yaml = Yaml(server, method);
        var bindings = Bindings(preparation);
        Assert.Contains(await runtime.ValidateAsync(yaml, request, preparation, Ct), d => d.ValidationStage == PlanningValidationStage.ConditionalActivation);
        Assert.Empty(await runtime.ValidateAsync(yaml, request, preparation, bindings, Ct));
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        foreach (var response in new JsonNode?[] { JsonValue.Create(true), JsonValue.Create(false), null, JsonValue.Create("invalid"), new JsonObject { ["_action"] = "abandon" } })
        {
            writes.Clear();
            var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new Human(response) };
            await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), Ct);
            Assert.Equal(response is JsonValue v && v.TryGetValue<bool>(out var accepted) && accepted ? 2 : 0, writes.Count);
        }
    }

    [Theory]
    [InlineData("missing_binding")]
    [InlineData("unknown_binding")]
    [InlineData("duplicate_binding")]
    [InlineData("wrong_owner")]
    [InlineData("outside_gate")]
    [InlineData("unbound_extra_call")]
    [InlineData("different_permission")]
    public async Task OwnershipCannotHideUnapprovedOrUnboundCalls(string variation)
    {
        var (runtime, request, preparation, _, _) = await Prepare("provider", "apply");
        var yaml = Yaml("provider", "apply");
        var bindings = Bindings(preparation);
        switch (variation)
        {
            case "missing_binding": bindings.RemoveAt(0); break;
            case "unknown_binding": bindings[0] = bindings[0] with { CapabilityId = "unknown" }; break;
            case "duplicate_binding": bindings.Add(bindings[0]); break;
            case "wrong_owner": bindings[1] = bindings[1] with { CapabilityId = bindings[0].CapabilityId }; break;
            case "outside_gate":
            case "unbound_extra_call":
                // Move the same call outside the consent boundary using YAML text.
                var block = "              - id: second\n                type: mcp.call\n                input: {server: provider, method: apply, request: {}}\n";
                Assert.Contains(block, yaml);
                if (variation == "outside_gate") yaml = yaml.Replace(block, "", StringComparison.Ordinal);
                else bindings.Add(new("main", "outside", bindings[1].CapabilityId));
                yaml += "\n      - id: " + (variation == "outside_gate" ? "second" : "outside") + "\n        type: mcp.call\n        input: {server: provider, method: apply, request: {}}\n";
                if (variation == "unbound_extra_call") bindings.RemoveAt(bindings.Count - 1);
                break;
            case "different_permission":
                yaml = yaml.Replace("  main:\n    steps:\n", "  main:\n    steps:\n      - id: unrelated\n        type: human.input\n        input: {mode: confirm, prompt: Separate action?, choices: [yes, no]}\n", StringComparison.Ordinal)
                    .Replace("data.steps.consent.response", "data.steps.unrelated.response", StringComparison.Ordinal);
                break;
        }
        var findings = await runtime.ValidateAsync(yaml, request, preparation, bindings, Ct);
        if (variation is "wrong_owner" or "outside_gate" or "different_permission")
            Assert.Contains(findings, d => d.ValidationStage == PlanningValidationStage.ConditionalActivation);
        else Assert.Contains(findings, d => d.Code == "ARTIFACT_OWNERSHIP_INVALID");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<PlanningArtifactBinding> Bindings(PlanningPreparation preparation) =>
        [new("main", "first", preparation.Capabilities[0].Id), new("main", "second", preparation.Capabilities[1].Id), new("main", "consent", "permission_capability")];

    private static async Task<(WorkflowPlanningRuntime, PlanningRequest, PlanningPreparation, InMemoryMcpClientFactory, List<JsonNode?>)> Prepare(string server, string method)
    {
        var writes = new List<JsonNode?>();
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer(server, new() { Tools = [new() { Name = method, InputSchema = new JsonObject { ["type"] = "object" } }],
            ToolHandlers = new() { [method] = input => { writes.Add(input); return new() { Content = new JsonObject() }; } } });
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = factory });
        var request = new PlanningRequest { TenantId = "tenant", Prompt = "Perform both actions only after confirmation.", Options = new()
        {
            ["generator"] = new JsonObject { ["model"] = "fake" },
            ["capability_preflight"] = new JsonObject { ["mode"] = "explicit", ["requirements"] = new JsonArray(new[] { "first", "second" }.Select(id => (JsonNode)new JsonObject
            { ["id"] = id, ["description"] = id, ["required"] = true, ["alternatives"] = new JsonArray(new JsonObject { ["server"] = server, ["kind"] = "tool", ["method"] = method }) }).ToArray()) }
        } };
        var preparation = await runtime.PrepareAsync(request, Ct);
        var resolved = preparation.RuntimeState["capabilities"]!.AsArray();
        for (var i = 0; i < 2; i++)
        {
            var activation = new McpCapabilityActivation("all_on_value", "group" + i, "permission", "EFFECT")
            { DecisionOutputPath = "/response", AllowedValues = ["EFFECT", "NO_EFFECT"], NoEffectValues = ["NO_EFFECT"], DecisionContractSource = "human_confirmation", DecisionProducerCatalogId = "human_catalog" };
            preparation.Capabilities[i].Activation = activation;
            resolved[i]!["activation"] = JsonSerializer.SerializeToNode(activation, PlanningJsonContext.Default.McpCapabilityActivation);
        }
        resolved.Add(new JsonObject { ["id"] = "permission", ["description"] = "Confirm", ["required"] = true, ["resolution"] = "native", ["method"] = "human.input",
            ["requestBindings"] = new JsonArray(), ["operationId"] = "permission", ["catalogId"] = "human_catalog", ["executionKind"] = "human_interaction", ["externalEffectKind"] = "none" });
        preparation.Capabilities.Add(new() { Id = "permission_capability", Description = "Confirm", StepType = "human.input", Method = "human.input", Resolution = "native", CatalogId = "human_catalog", OperationIds = ["permission"] });
        return (runtime, request, preparation, factory, writes);
    }

    private static string Yaml(string server, string method) => $$$"""
        version: 1
        skill: {description: Perform the confirmed actions., tags: [fixture], inputs: {}, outputs: {}}
        workflows:
          main:
            steps:
              - id: consent
                type: human.input
                input: {mode: confirm, prompt: Proceed?, choices: [yes, no], allow_abandon: true}
              - id: route
                type: switch
                expr: '${DECISION_EXPRESSION}'
                cases:
                  - value: EFFECT
                    steps:
                      - id: first
                        type: mcp.call
                        input: {server: {{{server}}}, method: {{{method}}}, request: {}}
                      - id: second
                        type: mcp.call
                        input: {server: {{{server}}}, method: {{{method}}}, request: {}}
                  - value: NO_EFFECT
                    steps: []
                default: []
        """.Replace("DECISION_EXPRESSION", ConfirmationDecisionExpression.Build("data.steps.consent.response", "EFFECT", "NO_EFFECT"), StringComparison.Ordinal);

    private sealed class Human(JsonNode? response) : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => Task.FromResult(response?.DeepClone());
    }
}
