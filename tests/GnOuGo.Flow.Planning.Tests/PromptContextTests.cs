using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PromptContextTests
{
    [Fact]
    public void SmallRepeatedContractsShareOnlyWhenTheCompleteContextShrinks()
    {
        var fields = new JsonObject(Enumerable.Range(0, 7).Select(i => new KeyValuePair<string, JsonNode?>("argument" + i, new JsonObject { ["type"] = "string" })));
        Assert.InRange(PlanningPromptContext.Json(fields).Length, 128, 383);
        var original = new JsonArray(Enumerable.Range(0, 6).Select(i => (JsonNode)new JsonObject { ["id"] = i, ["arguments"] = fields.DeepClone() }).ToArray());
        var shared = PlanningPromptContext.Share(original);
        Assert.True(JsonNode.DeepEquals(original, Expand(shared)));
        Assert.True(PlanningJsonTransport.EstimateInputTokens(PlanningPromptContext.Instructions + PlanningPromptContext.Json(shared), new()) <
            PlanningJsonTransport.EstimateInputTokens(PlanningPromptContext.Json(original), new()));
    }

    [Fact]
    public void BehaviorOmitsFixedSelectorDescriptionsAndSharesRequiredConstraintMetadata()
    {
        var preparation = TypedPlannerTests.Preparation();
        var description = string.Concat(Enumerable.Repeat("The complete selector catalog describes several unrelated fixed alternatives. ", 30));
        preparation.Capabilities.Add(new() { Id = "capability", RequestBindings = [new("/selector", JsonValue.Create("chosen"))],
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
                { ["selector"] = new JsonObject { ["type"] = "string", ["description"] = description }, ["value"] = new JsonObject { ["type"] = "string" } } } });
        preparation.LockedContract["constraints"] = new JsonArray(Enumerable.Range(0, 20).Select(i => (JsonNode)new JsonObject
            { ["id"] = "constraint" + i, ["description"] = "Declared obligation " + i, ["required"] = true, ["denied_alternatives"] = new JsonArray() }).ToArray());
        preparation.LockedContract["constraints"]!.AsArray().Add(new JsonObject { ["id"] = "denial", ["description"] = "Declared denial", ["required"] = true, ["denied_alternatives"] = new JsonArray("forbidden") });
        var locked = preparation.LockedContract.DeepClone();
        var text = PlanningBehaviorAssessment.BehaviorContext(preparation);
        var context = Expand(JsonNode.Parse(text[PlanningPromptContext.Instructions.Length..])!)!;
        var capability = context["capabilities"]!.AsArray().Single(c => c!["id"]!.ToString() == "capability")!;
        Assert.False(capability["declaredArguments"]!.AsObject().ContainsKey("selector"));
        Assert.Equal("chosen", capability["requestBindings"]![0]!["value"]!.ToString());
        Assert.False(capability["declaredArguments"]!["value"]!.AsObject().ContainsKey("description"));
        Assert.DoesNotContain(description, text);
        Assert.Equal(20, context["lockedContract"]!["requiredConstraints"]!.AsObject().Count);
        Assert.Equal("denial", Assert.Single(context["lockedContract"]!["constraints"]!.AsArray())!["id"]!.ToString());
        Assert.True(JsonNode.DeepEquals(locked, preparation.LockedContract));
        var before = context.DeepClone(); before["lockedContract"]!.AsObject().Remove("requiredConstraints"); before["lockedContract"]!["constraints"] = locked["constraints"]!.DeepClone();
        before["capabilities"]!.AsArray().Single(c => c!["id"]!.ToString() == "capability")!["declaredArguments"]!["selector"] = new JsonObject { ["type"] = "string", ["description"] = description, ["required"] = false };
        Assert.True(PlanningJsonTransport.EstimateInputTokens(before.ToJsonString(), new()) - PlanningJsonTransport.EstimateInputTokens(PlanningPromptContext.Json(context), new()) > 1000);
    }

    [Fact]
    public void BehaviorReferenceRepairsOmitExecutableArgumentsAndRetainGoverningMetadata()
    {
        var preparation = TypedPlannerTests.Preparation();
        preparation.Capabilities.Add(new()
        {
            Id = "operation", Description = "Declared business obligation", OperationIds = ["obligation"], InputOperationIds = ["source"],
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(Enumerable.Range(0, 12).Select(i =>
                new KeyValuePair<string, JsonNode?>("argument" + i, new JsonObject { ["type"] = "string", ["description"] = new string((char)('a' + i), 400) }))) }
        });
        var full = PlanningBehaviorAssessment.BehaviorCapabilities(preparation);
        var references = PlanningBehaviorAssessment.BehaviorCapabilities(preparation, includeArguments: false);
        var expected = Expand(JsonNode.Parse(full[PlanningPromptContext.Instructions.Length..])!)!.AsArray();
        Assert.Contains(expected, capability => capability!["declaredArguments"] is not null);
        foreach (var capability in expected.OfType<JsonObject>()) { capability.Remove("declaredArguments"); capability.Remove("requiredArguments"); }
        Assert.True(JsonNode.DeepEquals(expected, Expand(JsonNode.Parse(references[PlanningPromptContext.Instructions.Length..])!)));
        Assert.True(PlanningJsonTransport.EstimateInputTokens(full, new()) - PlanningJsonTransport.EstimateInputTokens(references, new()) > 1000);
        Assert.Equal(12, preparation.Capabilities.Single(c => c.Id == "operation").InputSchema["properties"]!.AsObject().Count);
    }

    [Fact]
    public void BehaviorSharesGoverningObligationsWithCapabilityDescriptionsLosslessly()
    {
        var preparation = TypedPlannerTests.Preparation(); preparation.Capabilities.Clear();
        var obligations = new JsonArray();
        for (var i = 0; i < 12; i++)
        {
            var description = "Distinct business obligation " + i + ": " + string.Concat(Enumerable.Repeat("Preserve the declared original artifact, its failure semantics and explicit human decision. ", 8));
            obligations.Add((JsonNode)new JsonObject { ["id"] = "op" + i, ["description"] = description });
            preparation.Capabilities.Add(new() { Id = "cap" + i, Description = description });
        }
        preparation.LockedContract["operations"] = obligations;
        var before = PlanningPromptContext.Json(preparation.LockedContract) + PlanningBehaviorAssessment.BehaviorCapabilities(preparation);
        var after = PlanningBehaviorAssessment.BehaviorContext(preparation);
        Assert.True(PlanningJsonTransport.EstimateInputTokens(after, new()) < PlanningJsonTransport.EstimateInputTokens(before, new()));
        var expanded = Expand(JsonNode.Parse(after[PlanningPromptContext.Instructions.Length..])!)!;
        Assert.True(JsonNode.DeepEquals(obligations, expanded["lockedContract"]!["operations"]));
        Assert.All(expanded["capabilities"]!.AsArray(), c => Assert.Equal(preparation.Capabilities.Single(p => p.Id == c!["id"]!.ToString()).Description, c!["description"]!.ToString()));
    }
    [Fact]
    public void BehaviorCapabilitiesShareRepeatedArgumentContractsBeforeDispatch()
    {
        var preparation = TypedPlannerTests.Preparation(); preparation.Capabilities.Clear();
        var description = string.Concat(Enumerable.Repeat("A required dynamic argument from the declared business input. ", 10));
        for (var i = 0; i < 8; i++) preparation.Capabilities.Add(new()
        {
            Id = "capability_" + i, Description = "Distinct operation " + i,
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["argument"] = new JsonObject { ["type"] = "string", ["description"] = description } }, ["required"] = new JsonArray("argument") }
        });
        var context = PlanningBehaviorAssessment.BehaviorCapabilities(preparation);
        Assert.Equal(1, context.Split(description).Length - 1);
        Assert.Contains("$contextRef", context);
        Assert.All(preparation.Capabilities, capability => Assert.Contains(capability.Id, context));
        Assert.True(context.Length < description.Length * preparation.Capabilities.Count);
    }

    [Fact]
    public void BehaviorArgumentMapsPreserveEvidenceWithoutRepeatedNamesOrAbsentMetadata()
    {
        var preparation = TypedPlannerTests.Preparation();
        preparation.Capabilities.Add(new() { Id = "capability", OperationIds = ["operation"], InputOperationIds = ["producer"], Description = "Declared action",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["dynamic"] = new JsonObject { ["type"] = "string", ["description"] = "The declared business input" } }, ["required"] = new JsonArray("dynamic") } });
        var before = System.Text.Json.JsonSerializer.SerializeToNode(preparation.Capabilities[0], GnOuGo.Flow.Core.Planning.PlanningJsonContext.Default.PlanningCapability)!.AsObject();
        foreach (var field in new[] { "inputSchema", "outputSchema", "declarationFingerprint", "fixedInput", "catalogId" }) before.Remove(field);
        before["declaredArguments"] = new JsonArray(new JsonObject { ["name"] = "dynamic", ["type"] = "string", ["description"] = "The declared business input", ["required"] = true });
        var context = PlanningBehaviorAssessment.BehaviorCapabilities(preparation);
        var after = Assert.Single(Expand(JsonNode.Parse(context[PlanningPromptContext.Instructions.Length..])!)!.AsArray())!;
        Assert.Equal("string", after["declaredArguments"]!["dynamic"]!["type"]!.ToString());
        Assert.Equal("dynamic", Assert.Single(after["requiredArguments"]!.AsArray())!.ToString());
        Assert.Equal("The declared business input", after["declaredArguments"]!["dynamic"]!["description"]!.ToString());
        Assert.Equal("producer", after["inputOperationIds"]![0]!.ToString());
        Assert.True(PlanningJsonTransport.EstimateInputTokens(after.ToJsonString(), new()) < PlanningJsonTransport.EstimateInputTokens(before.ToJsonString(), new()));
    }
    [Fact]
    public void SharedStringsAndUtf8TransportPreserveEveryContractValueWithFewerTokens()
    {
        var description = "The producer's original payload includes café, <bound>, quotes \"and\" exact artifact identity. ";
        var context = new JsonArray(Enumerable.Range(0, 8).Select(i => (JsonNode)new JsonObject { ["owner"] = i, ["contract"] = new JsonObject { ["description"] = description } }).ToArray());
        var shared = PlanningPromptContext.Share(context);
        Assert.True(JsonNode.DeepEquals(context, Expand(shared)));
        var text = PlanningPromptContext.Json(shared);
        Assert.True(JsonNode.DeepEquals(shared, JsonNode.Parse(text)));
        Assert.True(text.Length + PlanningPromptContext.Instructions.Length < context.ToJsonString().Length);
        Assert.DoesNotContain("\\u00E9", text);
    }

    [Fact]
    public void SmallContextsAndReservedLiteralKeysRemainUnchanged()
    {
        var simple = new JsonArray(new JsonObject { ["source"] = "one", ["resultContract"] = new JsonObject { ["type"] = "string" } });
        Assert.True(JsonNode.DeepEquals(simple, PlanningPromptContext.Share(simple)));
        var literal = new JsonObject { ["$contextRef"] = "literal value", ["description"] = new string('x', 2000) };
        var context = new JsonArray(literal, literal.DeepClone());
        Assert.True(JsonNode.DeepEquals(context, PlanningPromptContext.Share(context)));
    }

    internal static JsonNode? Expand(JsonNode pooled)
    {
        if (pooled is not JsonObject envelope || envelope["shared"] is not JsonObject shared) return pooled.DeepClone();
        JsonNode? Read(JsonNode? node)
        {
            if (node is JsonObject obj && obj["$contextRef"] is JsonValue reference) return Read(shared[reference.GetValue<string>()]);
            if (node is JsonObject value) return new JsonObject(value.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Read(p.Value))));
            return node is JsonArray array ? new JsonArray(array.Select(Read).ToArray()) : node?.DeepClone();
        }
        return Read(envelope["context"]);
    }
}
