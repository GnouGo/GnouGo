using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TypedBindingSchemaTests
{
    [Theory]
    [InlineData("[\"encoded\"]")]
    [InlineData("A description of checks")]
    public async Task StructuredListsRejectProseAndEncodedJsonBeforeSemanticCompilation(string text)
    {
        var state = await ComposedOutcomeTests.State();
        var definitions = PlanningSchemas.FullProposal(state, compact: false)["$defs"]!.DeepClone().AsObject();
        var contract = JsonNode.Parse("""{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,"properties":{"category":{"type":"string","enum":["observed"]},"limit":{"type":"integer","minimum":1}},"required":["category","limit"]}}""")!.AsObject();
        var binding = PlanningBindingSchemas.For(contract, definitions, literalOnly: true);
        var schema = definitions[binding["$ref"]!.ToString()[8..]]!.DeepClone().AsObject(); schema["$defs"] = definitions;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonSerializer.SerializeToNode(PlanningCorpus.String(text), PlanningJsonContext.Default.TaskValue), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""{"kind":"array","items":[{"kind":"object","members":[{"name":"category","value":{"kind":"string","text":"observed"}},{"name":"limit","value":{"kind":"number","number":1}}]}]}"""), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""{"kind":"array","items":[{"kind":"object","members":[{"name":"category","value":{"kind":"string","text":"invented"}}]}]}"""), schema));
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
    }

    [Fact]
    public void RetainedAgentRepairPinsScopeTypesAndExposesFixedCreationArgumentsReadOnly()
    {
        var state = ComposedOutcomeTests.Recorded("copilot"); state.OutcomeVersion = 2;
        var request = new PlanningPrompt(state).Request(); var context = PlanningRepairPatch.RequestContext(request);
        var source = context["repair"]!["tasks"]!.AsArray().Single(t => t!["id"]!.ToString() == "clone_repository_once")!;
        Assert.Null(source["inputs"]); Assert.Contains(source["fixedInputs"]!.AsArray(), i => i!["name"]!.ToString() == "targetDirectory");
        var slots = context["repair"]!["slots"]!.AsArray();
        Assert.DoesNotContain(slots, s => s!["location"]!.ToString().Contains("clone_repository_once", StringComparison.Ordinal));
        var verification = slots.Single(s => s!["location"]!.ToString().EndsWith("/verification", StringComparison.Ordinal))!["id"]!.ToString();
        var response = new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null, ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
            { ["slot"] = verification, ["action"] = "replace", ["value"] = new JsonObject { ["kind"] = "string", ["text"] = "still prose" } }) } };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
        var hash = PlanningRepairPatch.Authority(state, 5);
        state.Catalog!.Capabilities.Single(c => c.StepType == "agent.run").InputSchema["properties"]!["verification"]!["minItems"] = 2;
        Assert.NotEqual(hash, PlanningRepairPatch.Authority(state, 5));
        Assert.Throws<PlanningConflictException>(() => PlanningRepairPatch.Verify(state, request));
    }

    [Fact]
    public async Task NewRequestSelectsExactlyItsDetailedContractsAndLegacyRequestsKeepTheirBroadSchema()
    {
        var state = await ComposedOutcomeTests.State(); state.Plan = null; state.Requirements = null;
        var indexed = new PlanningOperation { Id = "index_only", Description = "Inspection is required", Version = "1" };
        state.Discovery.Sources = [new("directory", "Other operations")];
        state.Discovery.Pages = [new("directory", null, [new("index_only", "directory", "opaque", "", "mcp.call", "read", "1", Operation: indexed)], null)];
        var request = new PlanningPrompt(state).Request();
        Assert.DoesNotContain("index_only", request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("index_only", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("external", request.StructuredOutputSchema.ToJsonString(), StringComparison.Ordinal);
        state.Catalog!.Policy.DeniedCapabilityIds.Add("external");
        Assert.DoesNotContain("\"external\"", new PlanningPrompt(state).Request().StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
        state.Catalog.Policy.DeniedCapabilityIds.Clear();
        state.OutcomeVersion = 1;
        Assert.Contains("index_only", new PlanningPrompt(state).Request().StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
    }
}
