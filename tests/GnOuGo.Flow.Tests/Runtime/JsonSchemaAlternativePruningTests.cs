using System.Diagnostics;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class JsonSchemaAlternativePruningTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("anyOf", false)]
    [InlineData("anyOf", true)]
    [InlineData("oneOf", false)]
    [InlineData("oneOf", true)]
    public void DeepTypedAlternativesDoNotTraverseIncompatibleRecursiveChildren(string composition, bool tagFirst)
    {
        var variants = new JsonArray();
        for (var i = 0; i < 8; i++)
        {
            var fields = new JsonObject();
            var tag = new JsonObject { ["enum"] = new JsonArray("case-" + i) };
            if (tagFirst) fields["discriminant"] = tag;
            fields["child"] = new JsonObject { ["$ref"] = "#/$defs/node" };
            if (!tagFirst) fields["discriminant"] = tag;
            variants.Add(new JsonObject { ["type"] = "object", ["properties"] = fields,
                ["required"] = new JsonArray("discriminant"), ["additionalProperties"] = false });
        }
        var schema = new JsonObject { ["$ref"] = "#/$defs/node",
            ["$defs"] = new JsonObject { ["node"] = new JsonObject { [composition] = variants } } };
        foreach (var depth in new[] { 8, 16, 32 })
        {
            JsonNode value = new JsonObject { ["discriminant"] = "case-0" };
            for (var i = 0; i < depth; i++) value = tagFirst
                ? new JsonObject { ["discriminant"] = "case-" + i % 8, ["child"] = value }
                : new JsonObject { ["child"] = value, ["discriminant"] = "case-" + i % 8 };
            var before = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(value, schema));
            output.WriteLine($"{composition}, depth={depth}, tagFirst={tagFirst}: {clock.Elapsed.TotalMilliseconds:F3} ms, {GC.GetAllocatedBytesForCurrentThread() - before} bytes");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Typed alternatives must not expand exponentially.");
        }
    }

    [Theory]
    [InlineData("{\"oneOf\":[{\"type\":\"number\"},{\"type\":\"integer\"}]}", "1", "oneOf")]
    [InlineData("{\"anyOf\":[{\"type\":\"number\"},{\"type\":\"integer\"}]}", "1", null)]
    [InlineData("{\"oneOf\":[{\"type\":[\"integer\",\"null\"]},{\"type\":\"string\"}]}", "null", null)]
    [InlineData("{\"oneOf\":[false,true]}", "{}", null)]
    [InlineData("{\"oneOf\":[true,true]}", "{}", "oneOf")]
    [InlineData("{\"oneOf\":[{\"properties\":{\"tag\":{\"const\":\"a\"}}},{\"properties\":{\"tag\":{\"const\":\"b\"}}}]}", "{}", "oneOf")]
    [InlineData("{\"oneOf\":[{\"discriminator\":{\"propertyName\":\"tag\"}},{\"type\":\"object\"}]}", "{\"tag\":\"a\"}", "oneOf")]
    [InlineData("{\"anyOf\":[{\"type\":\"string\",\"properties\":{\"tag\":false}},{\"type\":\"integer\"}]}", "\"text\"", null)]
    [InlineData("{\"oneOf\":[{\"$ref\":\"#/$defs/a\"},{\"type\":\"integer\"}],\"$defs\":{\"a\":{\"$ref\":\"#/$defs/a\",\"type\":\"string\"}}}", "\"text\"", null)]
    [InlineData("{\"oneOf\":[{\"$ref\":\"#\",\"const\":false},{\"type\":\"integer\"}]}", "1", null)]
    [InlineData("{\"anyOf\":[{\"$ref\":\"#/$defs/tag\",\"enum\":[\"b\"]},{\"const\":\"b\"}],\"$defs\":{\"tag\":{\"const\":\"a\"}}}", "\"b\"", null)]
    public void PruningPreservesAlternativeSemantics(string schema, string instance, string? rule)
    {
        var findings = PlanningContractValidation.ValidateInstanceFindings(JsonNode.Parse(instance), JsonNode.Parse(schema)!);
        if (rule is null) Assert.Empty(findings);
        else Assert.Equal(rule, Assert.Single(findings).Rule);
    }

    [Fact]
    public void PropertyReferenceAssertionsAndInvalidNestedFieldsRetainExactDiagnostics()
    {
        var schema = JsonNode.Parse("""
            {"anyOf":[
              {"type":"object","properties":{"tag":{"const":"a"},"a/~é":{"type":"integer"}},"required":["a/~é"]},
              {"type":"object","properties":{"tag":{"const":"b"},"a/~é":{"type":"string"}}}
            ]}
            """)!;
        Assert.Equal(new[] { new PlanningInstanceFinding("/a~1~0é", "$.a/~é: expected integer", "type") },
            PlanningContractValidation.ValidateInstanceFindings(JsonNode.Parse("""{"a/~é":false,"tag":"a"}"""), schema));
        // A referenced property tag can prune matching work, but must not change the
        // historical (ambiguous) diagnostic selector, which only reads inline tags.
        schema["$defs"] = JsonNode.Parse("""{"b":{"const":"b"}}""");
        schema["anyOf"]![1]!["properties"]!["tag"] = JsonNode.Parse("""{"$ref":"#/$defs/b"}""");
        Assert.Equal(new[] { new PlanningInstanceFinding("", "$: value does not match any allowed schema variant", "anyOf") },
            PlanningContractValidation.ValidateInstanceFindings(JsonNode.Parse("""{"a/~é":false,"tag":"a"}"""), schema));
        Assert.Equal(new[] { new PlanningInstanceFinding("", "$: unresolved schema reference '#/$defs/missing'", "$ref") },
            PlanningContractValidation.ValidateInstanceFindings(JsonValue.Create(1), JsonNode.Parse("""{"anyOf":[{"$ref":"#/$defs/missing"}]}""")!));
    }

    [Fact]
    public void OriginalIssuedSchemaValidatesCommittedFourthResponseWithoutInference()
    {
        static JsonNode Read(string name)
        {
            using var stream = typeof(JsonSchemaAlternativePruningTests).Assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return JsonNode.Parse(reader.ReadToEnd())!;
        }
        var schema = Read("RetainedPlanningSchema");
        var response = Read("RetainedPlanningResponse")["response"]!;
        var schemaBefore = schema.ToJsonString(); var responseBefore = response.ToJsonString();
        var before = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
        var findings = PlanningContractValidation.ValidateInstanceFindings(response, schema);
        output.WriteLine($"Original fourth response: {clock.Elapsed.TotalMilliseconds:F3} ms; {GC.GetAllocatedBytesForCurrentThread() - before} allocated bytes; {findings.Count} findings");
        Assert.Empty(findings);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "The retained response must validate without exponential alternative traversal.");
        Assert.Equal(schemaBefore, schema.ToJsonString()); Assert.Equal(responseBefore, response.ToJsonString());
    }
}
