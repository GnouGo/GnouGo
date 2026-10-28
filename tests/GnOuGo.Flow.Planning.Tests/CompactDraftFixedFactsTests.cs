using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactDraftFixedFactsTests
{
    [Fact]
    public void DraftOmitsOnlyHostFactsAndUnavailableActions()
    {
        var state = State();
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var properties = schema["properties"]!.AsObject();
        Assert.DoesNotContain("baseHash", properties.Select(pair => pair.Key));
        Assert.DoesNotContain("requirements", properties.Select(pair => pair.Key));
        Assert.DoesNotContain("discoveryRequests", properties.Select(pair => pair.Key));
        Assert.DoesNotContain("commitments", properties.Select(pair => pair.Key));
        var extension = Extension(schema);
        foreach (var fixedField in new[] { "scope", "groups", "choices", "closeScopes", "complete", "continuationBinding" })
            Assert.Null(extension[fixedField]);
        Assert.NotNull(extension["tasks"]);
        Assert.NotNull(extension["always"]);
        Assert.NotNull(extension["outputs"]);
        Assert.NotNull(extension["requirementIds"]);
        Assert.NotNull(properties["clarifications"]);
        Assert.NotNull(properties["reselectStep"]);
        Assert.DoesNotContain("dischargedContinuationIds", schema["description"]!.ToString(), StringComparison.Ordinal);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
    }

    [Fact]
    public void NestedDraftRestoresExactRetainedScopeAndBaselineAfterSerialization()
    {
        var state = State();
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var raw = Response(schema);
        var issued = Issued(state, schema);
        var bytes = raw.ToJsonString();
        state = PlannerFixture.Clone(state);
        var normalized = PlanningConstruction.NormalizeDraftResponse(state, raw, issued);
        Assert.Equal(bytes, raw.ToJsonString());
        Assert.Equal(state.Construction!.IssuedBaseHash, normalized["baseHash"]!.ToString());
        Assert.Equal("/tasks/outer/body", normalized["extension"]!["scope"]!.ToString());
        var extension = normalized["extension"]!.Deserialize(PlanningJsonContext.Default.PlanningConstructionExtension)!;
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Equal("/tasks/outer/body", candidate.Extension.Scope);
        Assert.False(candidate.Extension.Complete);
        Assert.Empty(candidate.Extension.Groups);
        Assert.Empty(candidate.Extension.CloseScopes);
    }

    [Fact]
    public void ReturnedHostFactsOrStaleRetainedAuthorityCannotChooseAnotherScope()
    {
        var state = State();
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var issued = Issued(state, schema);
        var forged = Response(schema);
        forged["baseHash"] = "forged";
        forged["extension"]!["scope"] = "/root";
        Assert.NotEmpty(PlanningModelCalls.ValidateResponseFindings(forged, schema));
        Assert.Throws<PlanningConflictException>(() => PlanningConstruction.NormalizeDraftResponse(state, forged, issued));
        var raw = Response(schema);
        state.Construction!.IssuedBaseHash = "stale";
        Assert.Throws<PlanningConflictException>(() => PlanningConstruction.NormalizeDraftResponse(state, raw, issued));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("step")]
    public void RecoveryCannotReplaceIssuedStepAuthorityUnderTheSameBaseline(string changed)
    {
        var state = State();
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var issued = Issued(state, schema);
        var raw = Response(schema);
        state = PlannerFixture.Clone(state);
        if (changed == "scope") state.Construction!.Compact!.CurrentStep!.Scope = "/root";
        else state.Construction!.Compact!.CurrentStep!.Id = "different_step";
        Assert.Throws<PlanningConflictException>(() => PlanningConstruction.NormalizeDraftResponse(state, raw, issued));
        Assert.Null(raw["baseHash"]);
        Assert.Null(raw["extension"]!["scope"]);
    }

    [Fact]
    public void AlreadyIssuedRequiredEchoesRemainRequiredAndAreNotReinterpreted()
    {
        var state = State();
        var schema = PlanningCompactConstruction.DraftSchema(state);
        schema["properties"]!["baseHash"] = PlanningSchemas.Enum(state.Construction!.IssuedBaseHash!);
        schema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("baseHash"));
        var extension = Extension(schema);
        extension["scope"] = PlanningSchemas.Enum("/tasks/outer/body");
        var shape = schema["properties"]!["extension"]!["anyOf"]!.AsArray().OfType<JsonObject>().Single(item => item["properties"] is not null);
        shape["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("scope"));
        var raw = Response(schema);
        raw["baseHash"] = state.Construction.IssuedBaseHash;
        raw["extension"]!["scope"] = "/tasks/outer/body";
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(raw, schema));
        Assert.Equal(raw.ToJsonString(), PlanningConstruction.NormalizeDraftResponse(state, raw, Issued(state, schema)).ToJsonString());
        raw.AsObject().Remove("baseHash");
        Assert.NotEmpty(PlanningModelCalls.ValidateResponseFindings(raw, schema));
        Assert.Null(PlanningConstruction.NormalizeDraftResponse(state, raw, Issued(state, schema))["baseHash"]);
    }

    [Fact]
    public void FinalExistingFactoringPreservesGuidanceAndResponseDomains()
    {
        var state = State();
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var response = Response(schema);
        var compact = PlanningSchemas.Compact(schema.DeepClone().AsObject(), preserveDescriptions: true);
        Assert.Equal(schema["description"]!.ToString(), compact["description"]!.ToString());
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(response, compact));
        response["extension"]!["tasks"]![0]!["kind"] = "operation";
        Assert.NotEmpty(PlanningModelCalls.ValidateResponseFindings(response, schema));
        Assert.NotEmpty(PlanningModelCalls.ValidateResponseFindings(response, compact));
        // Reselection still declares a typed prerequisite: recursive/default/null/
        // enum semantics remain reachable rather than replacing it with any.
        Assert.Contains("expectedType", compact.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("default", compact.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("nullable", compact.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty(PlanningContractValidation.ValidateSchema(compact, strict: true));
    }

    private static JsonObject Extension(JsonObject schema) => schema["properties"]!["extension"]!["anyOf"]!
        .AsArray().OfType<JsonObject>().Single(item => item["properties"] is not null)["properties"]!.AsObject();

    private static LLMRequest Issued(PlanningSession state, JsonObject schema) => new()
    {
        StructuredOutputSchema = schema,
        Prompt = "Draft the selected fragment.\n" + new JsonObject
        {
            ["baseHash"] = state.Construction!.IssuedBaseHash,
            ["step"] = new JsonObject { ["id"] = state.Construction.Compact!.CurrentStep!.Id,
                ["scope"] = state.Construction.Compact.CurrentStep.Scope }
        }.ToJsonString()
    };

    private static JsonNode Response(JsonObject schema) => PlanningCorpus.Transport(new JsonObject
    {
        ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension
        {
            Scope = "/tasks/outer/body", RequirementIds = ["req"], Tasks = [new()
            {
                Id = "result", Kind = "value", Objective = "Produce the requested exact result.",
                Outputs = [new("text", new() { Kind = "string", Text = "retained" })]
            }]
        }, PlanningJsonContext.Default.PlanningConstructionExtension)
    }, schema, schema)!;

    private static PlanningSession State()
    {
        var state = new PlanningSession
        {
            Request = new() { TenantId = "fixed-facts", Prompt = "Preserve the exact declared result.", Generation = new("medium", 32000, 8192) },
            Catalog = new(), Plan = new() { Root = new() { Tasks = [new() { Id = "outer", Kind = "sequence", Objective = "Own the requested result.",
                Body = new() { Tasks = [new() { Id = "inner", Kind = "sequence", Objective = "Retain the unfinished independent child.", Body = new() }] } }] } },
            Requirements = new() { Inputs = [], Outputs = [], Outcomes = [new("req", "Produce the requested exact result.")] },
            Construction = new()
            {
                OpenScopes = ["/root", "/tasks/outer/body", "/tasks/inner/body"], Requirements = [new() { Id = "req" }], Compact = new()
                {
                    Sources = [new() { Id = "request:0", Kind = "page", Text = "Produce the requested exact result.", Indexed = true }],
                    CurrentStep = new() { Id = "selected", Scope = "/tasks/outer/body", Kind = "value", DiscoveryComplete = true,
                        RequirementIds = ["req"], OutputNames = ["text"] }
                }
            }
        };
        PlanningSessionCreation.Capture(state);
        state.Construction!.IssuedBaseHash = state.Construction.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        return state;
    }
}
