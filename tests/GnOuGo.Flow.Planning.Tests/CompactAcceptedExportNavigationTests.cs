using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactAcceptedExportNavigationTests
{
    private const string Scope = "/tasks/container/body";

    [Fact]
    public async Task ConsumedScalarInterfacesRetainExactBindingsAndNullableTypes()
    {
        var state = await Ready(30000);
        var imports = PlanningCompactConstruction.ActualImportedInterfaces(state);
        Assert.Equal(3, imports.Count);
        Assert.All(imports, imported => Assert.DoesNotContain("unused", imported!["schema"]!.ToJsonString(), StringComparison.Ordinal));
        Assert.Equal(new[] { "content", "format", "error" }, imports.Select(imported => imported!["binding"]!["port"]!.ToString()));
        Assert.All(imports, imported => Assert.Equal("first", imported!["binding"]!["items"]![0]!["source"]!.ToString()));
        Assert.Contains("null", imports[2]!["schema"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(state.Construction!.Candidate!.Plan, state.Catalog!, state.Request,
            state.Construction.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
    }

    [Fact]
    public async Task CompactBusinessReviewDoesNotRepeatUnconsumedGiantTypeMetadata()
    {
        var state = await Ready(30000);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var (context, proposal) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        Assert.DoesNotContain(new string('z', 200), context.ToJsonString(), StringComparison.Ordinal);
        var consumer = context["actions"]!.AsArray().Single(action => action!["id"]!.ToString() == "consumer")!;
        Assert.Equal(new[] { "content", "format", "error" }, consumer["arguments"]!.AsArray().Select(argument => argument!["name"]!.ToString()));
        Assert.Contains("first", consumer["arguments"]!.ToJsonString(), StringComparison.Ordinal);
        var producer = context["actions"]!.AsArray().Single(action => action!["id"]!.ToString() == "first")!;
        Assert.Equal("record", producer["outputs"]![0]!["value"]!["source"]!.ToString());
        Assert.DoesNotContain("inputSchema", context.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty(PlanningContractValidation.ValidateSchema(proposal.StructuredOutputSchema!.AsObject(), strict: true));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        var request = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, proposal.Prompt, proposal.StructuredOutputSchema.AsObject());
        var measurement = PlanningRequestMeasurements.Measure(request, PlanningConstruction.LocalPurpose, 8000);
        Assert.InRange(measurement.EstimatedInputTokens, 1, 8000);
        Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(component => component.SerializedBytes));
    }

    [Fact]
    public async Task OriginalExportTypesRemainExactlyRetrievableAndRepeatedEvidenceIsNotProgress()
    {
        var state = await Ready(32);
        var original = PlanningCompactConstruction.ScopeInterface(state, candidate: false)!;
        var address = PlanningCompactConstruction.ScopeInterface(state, candidate: false, navigationOnly: true)!["evidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        Assert.True(JsonNode.DeepEquals(original, state.Construction!.Evidence![address]));
        var evidence = state.Construction.Evidence.ToJsonString();
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, [address]));
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(evidence, state.Construction.Evidence.ToJsonString());
    }

    [Fact]
    public async Task WholeObjectConsumptionRetainsMandatoryOriginalTypeForCompilerAndDraft()
    {
        var state = await Ready(30000, wholeObject: true);
        var imported = Assert.Single(PlanningCompactConstruction.ActualImportedInterfaces(state));
        Assert.Equal("object", imported!["schema"]!["type"]!.ToString());
        Assert.Equal(30000, imported["schema"]!["properties"]!["unused"]!["enum"]![0]!.ToString().Length);
        var interfaceView = PlanningCompactConstruction.ScopeInterface(state, candidate: false)!;
        Assert.Equal(2, interfaceView["outputs"]!.AsArray().Count);
        Assert.All(interfaceView["outputs"]!.AsArray(), item => Assert.NotNull(item!["schema"]));
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(state.Construction!.Candidate!.Plan, state.Catalog!, state.Request,
            state.Construction.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
    }

    private static async Task<PlanningSession> Ready(int length, bool wholeObject = false)
    {
        const string requirement = "Extract the supplied content, format and error without changing either accepted record export.";
        var state = PlannerFixture.Capture(new() { Request = new() { TenantId = "accepted-export-navigation", Prompt = requirement } });
        state.Status = PlanningStatus.Generating;
        var construction = state.Construction!;
        PlanningCompactConstruction.InitializeSources(state);
        foreach (var source in construction.Compact!.Sources) source.Indexed = true;
        construction.Compact.RequirementSources["req_0"] = [construction.Compact.Sources[0].Id];
        var input = new TaskInput { Name = "record", Type = new() { Kind = "object", Fields =
        [new() { Name = "content", Type = new() { Kind = "string" } }, new() { Name = "format", Type = new() { Kind = "string" } },
            new() { Name = "error", Type = new() { Kind = "string", Nullable = true } },
            new() { Name = "unused", Type = new() { Kind = "string", Enum = [new string('z', length)] } }] } };
        state.Requirements = new() { Outcomes = [new("req_0", requirement)], Inputs = [input], Outputs = [] };
        construction.Requirements = [new() { Id = "req_0" }];
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new() { Inputs = [input], Root = new() { Tasks = [new()
        {
            Id = "container", Kind = "sequence", Objective = "Process the supplied records.", Body = new()
            {
                Tasks = [Producer("first"), Producer("second")],
                Outputs = [new("first_record", Record("first")), new("second_record", Record("second"))]
            }
        }] } };
        construction.OpenScopes = ["/root", Scope];
        PlanningCompactConstruction.SelectBlock(state, new JsonObject
        {
            ["objective"] = requirement, ["requirementIds"] = new JsonArray("req_0"),
            ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray(requirement),
            ["dependencies"] = new JsonArray(), ["successCriteria"] = new JsonArray("Preserve the exact original values and declared result.")
        });
        construction.Compact.CurrentStep = new() { BlockId = state.Construction!.Compact!.CurrentBlockId, Id = "selected", Scope = Scope, Kind = "transform", RequirementIds = ["req_0"],
            Objective = requirement, BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true,
            Inputs = wholeObject ? [Record("first")] : [Field("content"), Field("format"), Field("error")] };
        construction.Candidate = PlanningConstruction.Apply(state, new() { CompleteBlock = false, Scope = Scope, RequirementIds = ["req_0"], Tasks =
        [new() { Id = "consumer", Kind = "transform", Mode = "extract", Objective = requirement,
            Inputs = wholeObject ? [new("record", Record("first"))] : [new("content", Field("content")), new("format", Field("format")), new("error", Field("error"))],
            ResultType = new() { Kind = "object", Fields = [new() { Name = "content", Type = new() { Kind = "string" } }] } }] });
        PlanningCompactConstruction.ValidateExtension(state, construction.Candidate);
        return state;
    }

    private static PlanTask Producer(string id) => new() { Id = id, Kind = "value", Objective = "Preserve the supplied record.", Outputs = [new("record", new() { Kind = "input", Source = "record" })] };
    private static TaskValue Record(string id) => new() { Kind = "output", Source = id, Port = "record" };
    private static TaskValue Field(string name) => new() { Kind = "field", Port = name, Items = [Record("first")] };
}
