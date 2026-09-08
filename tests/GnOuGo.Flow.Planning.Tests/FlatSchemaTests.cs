using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FlatSchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public async Task UnreceivedContractPausesAndExplicitRetryUsesEquivalentFlatTransport(int status)
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); var planner = new TypedWorkflowPlanner(); var runtime = new FakeRuntime();
        for (var i = 0; i < 6 && !state.ConstructionUnits.Any(u => u.Kind == "inputs" && u.Status == "validated"); i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        var calls = 0; var approval = state.ApprovedBehaviorHash;
        runtime = new FakeRuntime { OnCall = (_, request, _) =>
        {
            if (++calls == 1) throw new LLMClientException(LLMClientFailureKind.Transport, "Upstream unavailable.", true, status);
            Assert.Contains("flat list", request.Prompt); Assert.Equal(8192, request.MaxTokens);
            Assert.True(request.DisableTransportRetries);
            return Task.FromResult(new LLMResponse { Json = Candidate(Row("", "object"), Row("/properties/message", "string")) });
        } };
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Recovery, state.Status); Assert.Equal(1, calls);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(1, calls);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        state = await planner.AdvanceAsync(state, new() { Kind = "retry", ExpectedRevision = state.Revision }, runtime, Ct);
        if (calls == 1) state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        var unit = state.ConstructionUnits.Single(u => u.Kind == "contracts");
        Assert.Equal(2, calls); Assert.Equal(2, unit.Calls); Assert.Equal(0, unit.RepairCalls);
        Assert.True(unit.FlatSchemaGeneration); Assert.Equal("validated", unit.Status); Assert.Equal(approval, state.ApprovedBehaviorHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputLimitedContractSwitchesTransportOnceWithoutAUserRetry(bool secondResponseLimited)
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); var planner = new TypedWorkflowPlanner(); var runtime = new FakeRuntime();
        for (var i = 0; i < 6 && !state.ConstructionUnits.Any(u => u.Kind == "inputs" && u.Status == "validated"); i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        var calls = 0;
        runtime = new FakeRuntime { OnCall = (_, request, _) =>
        {
            calls++;
            if (calls == 2) Assert.Contains("flat list", request.Prompt);
            return Task.FromResult(calls == 1 || secondResponseLimited ? new LLMResponse { CompletionStatus = "output_limit" }
                : new LLMResponse { Json = Candidate(Row("", "object"), Row("/properties/message", "string")) });
        } };
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        var unit = state.ConstructionUnits.Single(u => u.Kind == "contracts");
        Assert.True(unit.FlatSchemaGeneration); Assert.Equal("pending", unit.Status);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        unit = state.ConstructionUnits.Single(u => u.Kind == "contracts");
        Assert.Equal(2, calls); Assert.Equal(0, unit.RepairCalls);
        Assert.Equal(secondResponseLimited ? PlanningStatus.Recovery : PlanningStatus.Generating, state.Status);
        Assert.Equal(secondResponseLimited ? "recovery" : "validated", unit.Status);
        Assert.Equal(secondResponseLimited ? 2 : 1, state.Attempts.Count(a => a.Diagnostics.Any(d => d.Code == "MODEL_OUTPUT_LIMIT")));
    }
    [Theory]
    [InlineData("entries/name", "choice~value")]
    [InlineData("entrees/nom", "choix~valeur")]
    public void FlatDeclarationsPreserveNestedTypesEscapesNullabilityAndEnumConstraints(string name, string field)
    {
        var prep = Preparation(); var graph = Graph(); var unit = Unit();
        var original = PlanningConstruction.Schema(graph.Workflows[0], unit, prep, graph);
        var flat = new PlanningFlatSchemas(original);
        Assert.Empty(PlanningContractValidation.ValidateSchema(flat.Schema, strict: true));
        var pointer = "/properties/" + PlanningSchemaReferences.Escape(name);
        var candidate = Candidate(Row("", "object"), Row(pointer, "array"), Row(pointer + "/items", "object"),
            Row(pointer + "/items/properties/" + PlanningSchemaReferences.Escape(field), "string", nullable: true, values: ["one", "two"]));
        var expanded = flat.Expand(candidate, out var findings);
        Assert.Empty(findings); Assert.NotNull(expanded);
        Assert.Empty(PlanningConstruction.ShapeFindings(expanded, original, unit));
        var schema = JsonSerializer.Deserialize(expanded["nodes"]!["greeting"]!["outputSchema"]!, PlanningJsonContext.Default.PlanningSchema)!;
        var json = PlanningGraphCompiler.ToJsonSchema(schema, prep);
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { [name] = new JsonArray(new JsonObject { [field] = null }) }, json));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { [name] = new JsonArray(new JsonObject { [field] = "invented" }) }, json));
        Assert.Equal("array", schema.Properties[0].Schema.Type);
        Assert.True(schema.Properties[0].Schema.Items!.Properties[0].Required);
        Assert.NotNull(candidate["nodes"]!["greeting"]!["outputSchema"]!["fields"]);
    }

    [Fact]
    public void AuthoritativeReferencesRemainIndivisibleAndKeepTheirConstraints()
    {
        var prep = Preparation(); var graph = Graph(); graph.Workflows[0].Steps[0].CapabilityId = "contract";
        prep.Capabilities.Add(new() { Id = "contract", StepType = "set", OutputSchema = new JsonObject
            { ["type"] = "string", ["minLength"] = 5 } });
        var flat = new PlanningFlatSchemas(PlanningConstruction.Schema(graph.Workflows[0], Unit(), prep, graph));
        var root = new JsonObject { ["path"] = "", ["required"] = true, ["schema"] = new JsonObject
            { ["kind"] = "reference", ["capabilityId"] = "contract", ["schemaPointer"] = "/output" } };
        var expanded = flat.Expand(Candidate(root), out var diagnostics);
        Assert.Empty(diagnostics);
        var schema = JsonSerializer.Deserialize(expanded!["nodes"]!["greeting"]!["outputSchema"]!, PlanningJsonContext.Default.PlanningSchema)!;
        Assert.Equal(5, PlanningGraphCompiler.ToJsonSchema(schema, prep)["minLength"]!.GetValue<int>());
        Assert.Null(flat.Expand(Candidate(root.DeepClone(), Row("/properties/invented", "string")), out diagnostics));
        Assert.Contains(diagnostics, d => d.Message.Contains("Authoritative references", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/properties/bad~2escape")]
    [InlineData("/unknown")]
    [InlineData("/properties/missing/properties/leaf")]
    public void InvalidDeclarationsHaveStableLocationsAndRepairsPreserveOtherFields(string path)
    {
        var graph = Graph(); var flat = new PlanningFlatSchemas(PlanningConstruction.Schema(graph.Workflows[0], Unit(), Preparation(), graph));
        var original = Candidate(Row("", "object"), Row("/properties/valid", "string"), Row(path, "string"));
        Assert.Null(flat.Expand(original, out var diagnostics));
        Assert.Contains(diagnostics, d => d.Location == "/declarations/nodes/greeting/outputSchema/fields/2");
        var changed = Candidate(Row("", "object"), Row("/properties/valid", "boolean"));
        Assert.Contains(flat.PreservationFindings(original, changed), d => d.Code == "SCHEMA_DECLARATION_PRESERVATION");
    }

    [Fact]
    public async Task MalformedFlatResponsesConsumeOnlyTheConfiguredRepairAllowance()
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); var planner = new TypedWorkflowPlanner(); var runtime = new FakeRuntime();
        for (var i = 0; i < 6 && !state.ConstructionUnits.Any(u => u.Kind == "inputs" && u.Status == "validated"); i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        var unit = state.ConstructionUnits.Single(u => u.Kind == "contracts");
        unit.DispatchOutcome = "output_limit"; unit.Calls = 1;
        var calls = 0;
        runtime = new FakeRuntime { OnCall = (_, request, _) =>
        {
            if (++calls > 1) Assert.Contains("exact diagnostics", request.Prompt);
            return Task.FromResult(new LLMResponse { Text = "{" });
        } };
        for (var i = 0; i < 6 && state.Status != PlanningStatus.Recovery; i++)
        {
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        }
        unit = state.ConstructionUnits.Single(u => u.Kind == "contracts");
        Assert.Equal(PlanningStatus.Recovery, state.Status); Assert.Equal(1 + state.Request.MaxRepairs, calls);
        Assert.Equal(state.Request.MaxRepairs, unit.RepairCalls); Assert.Null(unit.Candidate);
        Assert.Contains(unit.Diagnostics, d => d.Code == "SCHEMA_DECLARATION_SHAPE_INVALID" && d.Location.StartsWith("/units/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OutputLimitedContractResumesWithFlatTransportAndPersistsItsRepairAcrossRestart()
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); var planner = new TypedWorkflowPlanner(); var runtime = new FakeRuntime();
        for (var i = 0; i < 6 && !state.ConstructionUnits.Any(u => u.Kind == "inputs" && u.Status == "validated"); i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        var unit = state.ConstructionUnits.Single(u => u.Kind == "contracts");
        unit.DispatchOutcome = "output_limit"; unit.Calls = 1;
        var calls = 0;
        runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            calls++; Assert.Contains("flat list", request.Prompt);
            Assert.Equal(8192, request.MaxTokens);
            Assert.InRange(PlanningConstruction.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject()), 1, 12000);
            return Task.FromResult(new LLMResponse { Json = calls == 1
                ? Candidate(Row("", "object"), Row("/properties/message", "array"))
                : Candidate(Row("", "object"), Row("/properties/message", "array"), Row("/properties/message/items", "string")) });
        } };
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        unit = state.ConstructionUnits.Single(u => u.Kind == "contracts");
        Assert.Equal("invalid", unit.Status); Assert.NotNull(unit.SchemaDeclarations); Assert.Null(unit.Candidate);
        Assert.Equal(0, unit.RepairCalls);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        unit = state.ConstructionUnits.Single(u => u.Kind == "contracts");
        Assert.Equal(2, calls); Assert.Equal(1, unit.RepairCalls); Assert.Equal("validated", unit.Status);
        Assert.NotNull(unit.Candidate); Assert.NotNull(unit.SchemaDeclarations);
    }

    private static PlanningConstructionUnit Unit() => new() { Kind = "contracts", WorkflowKey = "main", NodeKeys = ["greeting"], ContractVersion = PlanningDataflow.ContractVersion };
    private static JsonObject Candidate(params JsonNode[] fields) => new() { ["nodes"] = new JsonObject
        { ["greeting"] = new JsonObject { ["outputSchema"] = new JsonObject { ["fields"] = new JsonArray(fields) } } } };
    private static JsonObject Row(string path, string type, bool nullable = false, string[]? values = null)
    {
        var schema = new JsonObject { ["kind"] = "inline", ["type"] = type, ["nullable"] = nullable, ["description"] = null };
        if (type == "string") schema["enum"] = new JsonArray((values ?? []).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        return new() { ["path"] = path, ["required"] = true, ["schema"] = schema };
    }
}
