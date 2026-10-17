using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FreshContractDomainTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootNamesAreAcceptedNamesWithoutConstrainingNestedOrNewProducerPorts(bool noOutputs)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture("clé},{", "résultat.value");
        runtime.Proposal.Plan!.Root.Tasks.Add(new() { Id = "local", Kind = "sequence", Objective = "Expose a local typed value", Body = new()
        { Outputs = [new("local.unrelated", new() { Kind = "string", Text = "exact" })] } });
        if (noOutputs)
        {
            runtime.Proposal.Requirements!.Outputs = [];
            runtime.Proposal.Requirements.Outcomes[0] = new("deliver", "Perform the requested operation")
            { Commitments = [new(runtime.Proposal.Plan.Root.Tasks[0].Operation!)] };
            runtime.Proposal.Plan.Root.Outputs = [];
        }
        state.Requirements = runtime.Proposal.Requirements;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        var issued = Assert.Single(runtime.Calls); Assert.True(issued.StructuredOutputStrict);
        var valid = TestRuntime.Response(issued, runtime.Proposal).Json!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(valid, issued.StructuredOutputSchema!));
        var invalid = valid.DeepClone();
        invalid["plan"]!["root"]!["outputs"] = new JsonArray(new JsonObject { ["name"] = "foreign", ["value"] = new JsonObject { ["kind"] = "null" } });
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, issued.StructuredOutputSchema!));
        var historical = PlanningSchemas.FullProposal(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(TestRuntime.Response(new() { StructuredOutputSchema = historical },
            new() { Plan = invalid["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan) }).Json, historical));
        Assert.Equal("compact-bindings-v15", state.Request.Options["compilation_profile"]!.ToString());
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("operation", false)]
    [InlineData("port", false)]
    [InlineData("output", false)]
    [InlineData("operation_only", true)]
    [InlineData("literal_unicode", true)]
    public async Task ClosedDiscoveryKeepsOperationAndPortDomainsPaired(string change, bool accepted)
    {
        var (runtime, state) = await Fixture(closed: true);
        var request = new PlanningPrompt(state).Request();
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!.AsObject(), strict: true));
        var response = TestRuntime.Response(request, runtime.Proposal).Json!;
        var commitment = response["requirements"]!["outcomes"]![0]!["commitments"]![0]!;
        switch (change)
        {
            case "operation": commitment["operation"] = "not_inspected"; break;
            case "port": commitment["result"]!["port"] = "other.port"; break;
            case "output": commitment["result"]!["output"] = "not_accepted"; break;
            case "operation_only": commitment["result"] = null; break;
        }
        Assert.Equal(accepted, PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema).Count == 0);
        // The original broad template stays available to historical issued requests.
        var historical = PlanningSchemas.FullProposal(state);
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, historical));
        Assert.InRange(PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema.AsObject()),
            1, state.Request.Generation.MaxInputTokensPerRequest);
    }

    [Fact]
    public async Task OpenDiscoveryAndUndeclaredInterfacesDoNotBecomeInventedDomains()
    {
        var (runtime, state) = await Fixture(closed: false);
        state.Requirements = null;
        var request = new PlanningPrompt(state).Request();
        var response = TestRuntime.Response(request, runtime.Proposal).Json!;
        var commitment = response["requirements"]!["outcomes"]![0]!["commitments"]![0]!;
        commitment["operation"] = "unresolved"; commitment["result"]!["port"] = "unresolved.port";
        commitment["result"]!["output"] = "new.output";
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
        Assert.Contains(PlanningCommitments.Validate(state, response["requirements"]!.Deserialize(PlanningJsonContext.Default.PlanningRequirements)!, true),
            d => d.Code == "REQUIREMENTS_COMMITMENT_INVALID");

        state.ModelCalls = 6; // Closed discovery still cannot enumerate this response's new public interface.
        request = new PlanningPrompt(state).Request(); response = TestRuntime.Response(request, runtime.Proposal).Json!;
        response["requirements"]!["outcomes"]![0]!["commitments"]![0]!["result"]!["output"] = "new.output";
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
    }

    [Fact]
    public async Task OnlyVisibleInspectedContractsAreEligibleAndRepairAuthorityIsUnchanged()
    {
        var (runtime, state) = await Fixture(closed: true);
        var operation = runtime.Proposal.Plan!.Root.Tasks[0].Operation!;
        var request = new PlanningPrompt(state).Request();
        var response = TestRuntime.Response(request, runtime.Proposal).Json!;
        response["requirements"]!["outcomes"]![0]!["commitments"]![0]!["operation"] = "other";
        response["requirements"]!["outcomes"]![0]!["commitments"]![0]!["result"]!["port"] = "other.port";
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
        var limited = PlanningSchemas.Proposal(state, new HashSet<string>(StringComparer.Ordinal) { operation }, commitmentDomains: true);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, limited));

        var repair = RepairPatchTests.State();
        var template = PlanningRepairPatch.Template(repair).ToJsonString();
        var authority = PlanningRepairPatch.Authority(repair);
        var schema = PlanningRepairPatch.Schema(repair, PlanningRepairPatch.Template(repair));
        Assert.True(JsonNode.DeepEquals(schema, new PlanningPrompt(repair).Request().StructuredOutputSchema));
        Assert.Equal(template, PlanningRepairPatch.Template(repair).ToJsonString());
        Assert.Equal(authority, PlanningRepairPatch.Authority(repair));
    }

    [Fact]
    public async Task AdditionalDomainsNeverRaiseTheSupportedSchemaCeilings()
    {
        var (_, state) = await Fixture(closed: true);
        state.Requirements!.Outputs = Enumerable.Range(0, 1001).Select(i => new TaskInput { Name = "result_" + i, Type = new() }).ToList();
        var schema = PlanningSchemas.Proposal(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var original = PlanningSchemas.Compact(PlanningSchemas.FullProposal(state, compact: false), compositionHints: true);
        Assert.True(JsonNode.DeepEquals(original, schema));
        // Presentation fallback never accepts a missing required public result.
        Assert.NotEmpty(PlanningClarifications.OutputFindings(state, PlannerFixture.Greeting()));
    }

    [Fact]
    public async Task PairDomainsUseOnlyAvailableHeadroomWithoutDisplacingContracts()
    {
        var (runtime, state) = await Fixture(closed: true);
        var generous = new PlanningPrompt(state).Request();
        var operations = GenericPlanningPromptTests.Context(generous)["operations"]!.AsArray().Select(o => o!["id"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        var basic = PlanningContractValidation.ProjectStructuredOutputSchema(PlanningSchemas.Proposal(state, operations));
        var minimum = PlanningJsonTransport.EstimateInputTokens(generous.Prompt, basic);
        var maximum = PlanningJsonTransport.EstimateInputTokens(generous.Prompt, generous.StructuredOutputSchema!.AsObject());
        Assert.True(maximum > minimum);
        state.Request.Generation.MaxInputTokensPerRequest = ((minimum + maximum) / 2 * 10 + 8) / 9;
        var bounded = new PlanningPrompt(state).Request();
        Assert.True(JsonNode.DeepEquals(basic, bounded.StructuredOutputSchema));
        Assert.True(JsonNode.DeepEquals(GenericPlanningPromptTests.Context(generous)["operations"], GenericPlanningPromptTests.Context(bounded)["operations"]));
        var response = TestRuntime.Response(bounded, runtime.Proposal).Json!;
        response["requirements"]!["outcomes"]![0]!["commitments"]![0]!["result"]!["port"] = "other.port";
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, bounded.StructuredOutputSchema!));
        Assert.Contains(PlanningCommitments.Validate(state, response["requirements"]!.Deserialize(PlanningJsonContext.Default.PlanningRequirements)!, true),
            d => d.Code == "REQUIREMENTS_COMMITMENT_INVALID");
        Assert.Empty(runtime.Calls); Assert.Equal(6, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    private static async Task<(TestRuntime Runtime, PlanningSession State)> Fixture(bool closed)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture("clé},{", "résultat.value");
        state.Requirements = runtime.Proposal.Requirements;
        state.Catalog!.Capabilities.Add(new() { Id = "other", Version = "1", Kind = "tool", StepType = "mcp.call", Server = "generic", Method = "other",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"other.port":{"type":["string","null"]}},"required":["other.port"],"additionalProperties":false}""")!.AsObject() });
        if (closed) state.ModelCalls = 6;
        Assert.Equal(!closed, PlanningDiscoveryContext.CanDiscover(state));
        return (runtime, state);
    }
}
