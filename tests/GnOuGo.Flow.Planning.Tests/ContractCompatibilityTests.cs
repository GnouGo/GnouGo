using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ContractCompatibilityTests
{
    private static JsonObject S(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void IdenticalExclusiveUnionsPreserveProducerExclusionsWithoutAnIdentityCall()
    {
        var contract = S("""{"oneOf":[{"type":"number"},{"type":"integer"}]}""");
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonValue.Create(0.5), contract));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonValue.Create(1), contract));
        var (state, workflow, hole) = ConvergenceDomainTests.Input();
        var capability = state.Preparation!.Capabilities.Single();
        capability.InputSchema["properties"]!["argument"] = contract.DeepClone();
        capability.OutputSchema = contract.DeepClone().AsObject();
        workflow.Inputs[0].Schema = new() { CapabilityId = capability.Id, SchemaPointer = "/output" };
        // Expanding the source oneOf into an inclusive union used to discard its
        // integer exclusion, remove the sole binding and require a computation.
        PlanningBindingResolution.Resolve(state, workflow);
        Assert.True(hole.Resolved);
        Assert.Equal("deterministic", hole.ResolutionOrigin);
        Assert.Empty(state.Construction.PendingCalls);
        Assert.True(JsonNode.DeepEquals(contract, capability.OutputSchema));
    }

    [Fact]
    public void IdenticalReferenceTextDoesNotMeanIdenticalReferencedContracts()
    {
        var source = S("""{"type":"object","properties":{"value":{"$ref":"#/$defs/item"}},"required":["value"],"$defs":{"item":{"type":"string"}}}""");
        var target = S("""{"type":"object","properties":{"value":{"$ref":"#/$defs/item"}},"required":["value"],"$defs":{"item":{"type":"number"}}}""");
        Assert.False(PlanningContractCompatibility.Fits(source, target));
        Assert.True(PlanningContractCompatibility.Fits(source, source.DeepClone().AsObject()));
    }

    [Theory]
    [InlineData("""{"anyOf":[false,{"type":"string"}]}""", """{"type":"string"}""", true)]
    [InlineData("""{"allOf":[true,{"type":"string"}]}""", """{"type":"string"}""", true)]
    [InlineData("""{"type":"string"}""", """{"oneOf":[false,{"type":"string"}]}""", true)]
    [InlineData("""{"type":"string"}""", """{"oneOf":[true,{"type":"string"}]}""", false)]
    [InlineData("""{"type":"string"}""", """{"allOf":[false,{"type":"string"}]}""", false)]
    [InlineData("""{"type":"string"}""", """{"anyOf":[true,{"type":"integer"}]}""", true)]
    [InlineData("""{"type":"string"}""", """{"anyOf":[{"type":"string"}],"oneOf":[{"type":"integer"}]}""", false)]
    [InlineData("""{"type":"string"}""", """{"anyOf":[{"type":"string"},{"type":"number"}],"oneOf":[{"type":"string"},{"type":"boolean"}]}""", true)]
    [InlineData("""{"type":"object"}""", """{"type":"object","properties":{"unconstrained":{}}}""", true)]
    [InlineData("""{"type":"array"}""", """{"type":"array","items":{}}""", true)]
    [InlineData("""{"type":"object","patternProperties":{"^x":{"type":"string"}},"required":["x"],"additionalProperties":false}""", """{"oneOf":[{"type":"object"},{"type":"object","properties":{"x":{"type":"string"}},"required":["x"]}]}""", false)]
    [InlineData("""{"type":"string","enum":["a","b"]}""", """{"oneOf":[{"const":"a"},{"const":"b"}]}""", true)]
    [InlineData("""{"const":"a"}""", """{"oneOf":[{"type":"string"},{"const":"a"}]}""", false)]
    [InlineData("""{"const":"a"}""", """{"oneOf":[true,{"type":"string"}]}""", false)]
    [InlineData("""{"const":"a"}""", """{"type":"string","not":{"const":"a"}}""", false)]
    [InlineData("""{"const":"not-an-email"}""", """{"type":"string","format":"email"}""", false)]
    [InlineData("""{"type":["string","null"],"enum":["a",null]}""", """{"oneOf":[{"type":"string","enum":["a","b"]},{"type":"null"}]}""", true)]
    [InlineData("""{"type":["string","null"]}""", """{"type":"string","default":"a"}""", false)]
    [InlineData("""{"allOf":[{"type":"number","minimum":0},{"maximum":10}]}""", """{"type":"number","minimum":0,"maximum":10}""", true)]
    [InlineData("""{"$ref":"#/$defs/n","minimum":10,"$defs":{"n":{"type":"number"}}}""", """{"type":"number","minimum":10}""", true)]
    [InlineData("""{"type":"number","minimum":10}""", """{"$ref":"#/$defs/n","minimum":11,"$defs":{"n":{"type":"number"}}}""", false)]
    [InlineData("""{"anyOf":[{"type":"number"},{"type":"integer"}],"minimum":10}""", """{"type":"number","minimum":10}""", true)]
    [InlineData("""{"type":"number","minimum":0}""", """{"oneOf":[{"type":"number","minimum":0},{"type":"number","exclusiveMaximum":0}]}""", true)]
    [InlineData("""{"type":"number","minimum":0}""", """{"oneOf":[{"type":"number","minimum":0},{"type":"number","maximum":0}]}""", false)]
    [InlineData("""{"type":"array","prefixItems":[{"type":"string"},{"type":"integer"}],"items":false}""", """{"type":"array","prefixItems":[{"type":"string"},{"type":"number"}],"items":false}""", true)]
    [InlineData("""{"type":"array","items":false}""", """{"type":"array","items":{"type":"string"}}""", true)]
    [InlineData("""{"type":"array","items":false}""", """{"type":"array","maxItems":0,"uniqueItems":true}""", true)]
    [InlineData("""{"type":"object","additionalProperties":false}""", """{"type":"object","maxProperties":0}""", true)]
    [InlineData("""{"type":"object","required":["a","b"]}""", """{"type":"object","minProperties":2}""", true)]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}},"required":["a"]}""", """{"type":"object","minProperties":2}""", false)]
    [InlineData("""{"type":"object"}""", """{"type":"object","properties":{"forbidden":false}}""", false)]
    [InlineData("""{"type":"object","patternProperties":{"^x":{"type":"string"}},"additionalProperties":false}""", """{"type":"object","additionalProperties":false}""", false)]
    [InlineData("""{"type":"integer"}""", """{"type":"number","multipleOf":1}""", true)]
    [InlineData("""{"type":"array","items":{"type":"string"}}""", """{"type":"array","items":false}""", false)]
    [InlineData("""{"type":"array","maxItems":0}""", """{"type":"array","items":{"type":"number"},"uniqueItems":true}""", true)]
    [InlineData("""{"type":"array","items":{"type":["string","null"]}}""", """{"type":"array","items":{"type":"string"}}""", false)]
    [InlineData("""{"type":"array","items":{"type":"integer"},"minItems":1,"maxItems":2}""", """{"type":"array","items":{"type":"number"},"minItems":1,"maxItems":3}""", true)]
    [InlineData("""{"type":"array","items":{"type":"string"}}""", """{"type":"array","items":{"type":"string"},"uniqueItems":true}""", false)]
    [InlineData("""{"type":"boolean"}""", """{"type":"boolean","minimum":1,"minLength":10,"minItems":1}""", true)]
    [InlineData("""{"type":"object","additionalProperties":{"type":"string"}}""", """{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":{"type":"string"}}""", true)]
    [InlineData("""{"type":"object","additionalProperties":true}""", """{"type":"object","properties":{"a":{"type":"string"}}}""", false)]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":false}""", """{"type":"object","properties":{"a":{"type":"string","default":"value"}},"required":["a"]}""", false)]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}""", """{"type":"object","additionalProperties":{"type":"string"}}""", true)]
    public void InclusionHonorsDeclaredConstraints(string actual, string expected, bool fits)
        => Assert.Equal(fits, PlanningContractCompatibility.Fits(S(actual), S(expected)));

    [Fact]
    public void DiscriminatedObjectsAreCompatibleWithoutAnIdentityTransformationCall()
    {
        var a = """{"type":"object","properties":{"kind":{"const":"a"},"value":{"type":"number"}},"required":["kind","value"],"additionalProperties":false}""";
        var b = """{"type":"object","properties":{"kind":{"const":"b"},"value":{"type":"string"}},"required":["kind","value"],"additionalProperties":false}""";
        var expected = new JsonObject { ["oneOf"] = new JsonArray(S(a), S(b)) };
        Assert.True(PlanningContractCompatibility.Fits(S(a), expected));
        PlanningGraphValidation.RequireTyped(S(a), 0);
        Assert.True(PlanningContractCompatibility.Fits(expected, expected));
        var (state, workflow, hole) = ConvergenceDomainTests.Input();
        var capability = state.Preparation!.Capabilities.Single();
        capability.InputSchema["properties"]!["argument"] = expected;
        capability.OutputSchema = S(a);
        workflow.Inputs[0].Schema = new() { CapabilityId = capability.Id, SchemaPointer = "/output" };
        PlanningBindingResolution.Resolve(state, workflow);
        Assert.True(hole.Resolved);
        Assert.Equal("deterministic", hole.ResolutionOrigin);
        Assert.Empty(state.Construction.PendingCalls);
    }

    [Fact]
    public void BooleanUnionBranchesDoNotRequireAnIdentityComputation()
    {
        var (state, workflow, hole) = ConvergenceDomainTests.Input();
        var capability = state.Preparation!.Capabilities.Single();
        capability.InputSchema["properties"]!["argument"] = S("""{"oneOf":[false,{"type":"string"}]}""");
        var locked = capability.InputSchema.DeepClone();
        // This runtime-supported union previously removed the only direct binding
        // from the response domain, leaving an unnecessary model computation.
        PlanningBindingResolution.Resolve(state, workflow);
        Assert.True(hole.Resolved);
        Assert.Equal("deterministic", hole.ResolutionOrigin);
        Assert.Empty(state.Construction.PendingCalls);
        Assert.True(JsonNode.DeepEquals(locked, capability.InputSchema));
    }

    [Fact]
    public void AllOfPreservesClosedObjectSemanticsAndReferenceConstraints()
    {
        var actual = S("""{"allOf":[{"type":"object","properties":{"kind":{"type":"string"},"value":{"type":"number"}},"required":["kind","value"],"additionalProperties":false},{"properties":{"kind":{"const":"a"}},"required":["kind"]}]}""");
        var expected = S("""{"type":"object","properties":{"kind":{"const":"a"},"value":{"type":"number"}},"required":["kind","value"],"additionalProperties":false}""");
        Assert.True(PlanningContractCompatibility.Fits(actual, expected));
        Assert.False(PlanningContractCompatibility.Fits(expected, S("""{"allOf":[{"type":"object","properties":{"kind":{"const":"a"}},"required":["kind"],"additionalProperties":false},{"properties":{"value":{"type":"number"}},"required":["value"]}]}""")));
    }

    [Fact]
    public void UnsupportedProofIsDistinctFromProvenMismatch()
    {
        Assert.Equal(PlanningContractCompatibility.Proof.Incompatible, PlanningContractCompatibility.Analyze(S("""{"type":"string"}"""), S("""{"type":"number"}""")));
        Assert.Equal(PlanningContractCompatibility.Proof.Unknown, PlanningContractCompatibility.Analyze(S("""{"type":"string","pattern":"^a+$"}"""), S("""{"type":"string","pattern":"^a*$"}""")));
    }

    [Fact]
    public void TransportAnnotationsDoNotRequireAnIdentityComputation()
    {
        var (state, workflow, hole) = ConvergenceDomainTests.Input();
        var field = state.Preparation!.Capabilities.Single().InputSchema["properties"]!["argument"]!.AsObject();
        field["x-transport-selector"] = "declared-routing-metadata";
        field["readOnly"] = true;
        var retained = field.DeepClone();
        PlanningBindingResolution.Resolve(state, workflow);
        Assert.True(hole.Resolved);
        Assert.Equal("deterministic", hole.ResolutionOrigin);
        Assert.Equal("input", PlanningGraphValidation.Member(PlanningGraphValidation.Member(state.Graph!.Workflows[0].Steps[0].Input, "request")!, "argument")!.Kind);
        Assert.True(JsonNode.DeepEquals(retained, field));
        Assert.Empty(state.Construction.PendingCalls);
    }

    [Theory]
    [InlineData("{\"type\":\"string\",\"x-selector\":\"a\"}", true)]
    [InlineData("{\"type\":\"string\",\"deprecated\":true}", true)]
    [InlineData("{\"type\":\"string\",\"pattern\":\"^a\"}", false)]
    [InlineData("{\"type\":\"string\",\"not\":{\"const\":\"a\"}}", false)]
    [InlineData("{\"type\":\"string\",\"$vocabulary\":{\"urn:custom:assertions\":true},\"customAssertion\":true}", false)]
    public void AnnotationHandlingDoesNotDiscardAssertionsOrDeclaredVocabularies(string target, bool compatible)
        => Assert.Equal(compatible, PlanningContractCompatibility.Fits(S("{\"type\":\"string\"}"), S(target)));

    [Fact]
    public void RepeatedReferenceAlternativesHaveABoundedProofWorkload()
    {
        var definitions = new JsonObject { ["leaf"] = S("""{"type":"string"}""") };
        var previous = "leaf";
        for (var i = 0; i < 14; i++)
        {
            var name = "level" + i;
            definitions[name] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["$ref"] = "#/$defs/" + previous }, new JsonObject { ["$ref"] = "#/$defs/" + previous }) };
            previous = name;
        }
        var source = new JsonObject { ["$ref"] = "#/$defs/" + previous, ["$defs"] = definitions };
        Assert.False(PlanningContractCompatibility.Fits(source, S("""{"type":"string"}""")));
    }

    [Fact]
    public void EveryAcceptedProofAgreesWithValidatorOnFiniteDomain()
    {
        string[] schemas =
        [
            "{}", """{"type":"string"}""", """{"type":"number"}""", """{"type":"integer"}""", """{"type":["string","null"]}""",
            """{"anyOf":[false,{"type":"string"}]}""", """{"allOf":[true,{"type":"number"}]}""", """{"oneOf":[true,{"type":"string"}]}""",
            """{"enum":[null,"a",0]}""", """{"type":"number","minimum":0}""", """{"oneOf":[{"type":"number"},{"type":"integer"}]}""",
            """{"type":"array","items":false}""", """{"type":"array","items":{"type":"number"}}""",
            """{"type":"array","prefixItems":[{"type":"string"}],"items":false}""", """{"type":"array","maxItems":0}""",
            """{"type":"object","additionalProperties":false}""", """{"type":"object","additionalProperties":{"type":"number"}}""", """{"type":"object","minProperties":1}""",
            """{"type":"object","properties":{"kind":{"const":"a"}},"required":["kind"]}""",
            """{"oneOf":[{"type":"object","properties":{"kind":{"const":"a"}},"required":["kind"]},{"type":"object","properties":{"kind":{"const":"b"}},"required":["kind"]}]}""",
            """{"allOf":[{"type":"object","additionalProperties":false},{"properties":{"kind":{"const":"a"}}}]}"""
        ];
        string[] instances = ["null", "true", "false", "-1", "0", "0.5", "1", "\"a\"", "\"b\"", "[]", "[0]", "[0,1]", "[\"a\"]", "[\"a\",0]", "{}", "{\"kind\":\"a\"}", "{\"kind\":\"b\"}", "{\"kind\":\"a\",\"extra\":0}", "{\"value\":0}"];
        foreach (var source in schemas.Select(S))
            foreach (var target in schemas.Select(S))
                if (PlanningContractCompatibility.Fits(source, target))
                    foreach (var value in instances.Select(s => JsonNode.Parse(s)).Where(v => PlanningContractValidation.ValidateInstance(v, source).Count == 0))
                        Assert.True(PlanningContractValidation.ValidateInstance(value, target).Count == 0, $"Unsound inclusion {source} -> {target} for {value}");
    }
}
