using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningRepeatedTaskPresentationTests
{
    [Fact]
    public void RejectedStepSharesOnlyItsUnchangedEvidenceAtTheSameIssuedAddress()
    {
        var current = new JsonObject { ["path"] = "/root", ["detail"] = new string('x', 1200) };
        var previous = new JsonObject { ["detail"] = new string('y', 1200) };
        var archive = new JsonObject
        {
            ["step"] = new JsonObject { ["scope"] = "/root" }, ["reason"] = "Keep the original requirements open.",
            ["evidence"] = new JsonObject { ["scope:/root"] = current.DeepClone(), ["page:tasks:0"] = previous.DeepClone(),
                ["scope:/other"] = current.DeepClone() }
        };
        var evidence = new JsonObject { ["scope:/root"] = current.DeepClone(), ["page:tasks:0"] = new JsonObject(),
            ["rejected:0"] = archive.DeepClone() };
        var state = PlannerFixture.Session();
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        var request = PlanningModelCalls.PrepareRequest(state, "construction_implementation", "Choose.\n" + new JsonObject { ["evidence"] = evidence }.ToJsonString(),
            schema, null, null, out var map);
        var call = new PlanningModelCall { Id = request.ClientRequestId!, Purpose = "construction_implementation", Request = request, EvidenceAddresses = map };
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(call, PlanningJsonContext.Default.PlanningModelCall),
            PlanningJsonContext.Default.PlanningModelCall)!;
        PlanningModelCalls.ValidateEvidenceAddresses(restored);
        foreach (var prompt in new[] { request.Prompt, PlanningModelCalls.CanonicalEvidenceRequest(restored).Prompt })
        {
            var rows = Read(prompt)["evidence"]!.AsObject();
            var rejection = rows.Single(p => p.Value?["reason"] is not null).Value!;
            var references = Assert.IsType<JsonArray>(rejection["unchangedEvidence"]);
            var address = Assert.Single(references)!.ToString();
            Assert.True(JsonNode.DeepEquals(current, rows[address]));
            Assert.Equal(prompt == request.Prompt, address != "scope:/root");
            Assert.Null(rejection["evidence"]!["scope:/root"]);
            Assert.True(JsonNode.DeepEquals(previous, rejection["evidence"]!["page:tasks:0"]));
            Assert.True(JsonNode.DeepEquals(current, rejection["evidence"]!["scope:/other"]));
            Assert.Equal(archive["reason"]!.ToString(), rejection["reason"]!.ToString());
        }
        Assert.True(JsonNode.DeepEquals(current, archive["evidence"]!["scope:/root"]));
        Assert.Equal(0, state.ModelCalls);
    }

    [Theory]
    [InlineData("tasks", true)]
    [InlineData("always", true)]
    [InlineData("tasks", false)]
    public void IssuedTaskPageReusesOnlyTheIdenticalOrderedScopeArray(string phase, bool sameOrder)
    {
        var context = Context(phase);
        var evidence = context["evidence"]!.AsObject();
        var items = evidence["scope:/root~view"]![phase]!.AsArray();
        items.Add(new JsonObject { ["id"] = "second", ["kind"] = "value", ["objective"] = "Another accepted result." });
        var originals = new JsonArray((sameOrder ? items.AsEnumerable() : items.Reverse()).Select(item => item!.DeepClone()).ToArray());
        evidence["page:tasks:8"] = new JsonObject { ["offset"] = 8, ["total"] = 10, ["items"] = originals.DeepClone() };

        var state = PlannerFixture.Session();
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        var request = PlanningModelCalls.PrepareRequest(state, "construction_draft", "Originals.\n" + context.ToJsonString(),
            schema, null, null, out var map);
        var issued = new PlanningModelCall { Id = request.ClientRequestId!, Purpose = "construction_draft", Request = request, EvidenceAddresses = map };
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(issued, PlanningJsonContext.Default.PlanningModelCall),
            PlanningJsonContext.Default.PlanningModelCall)!;
        PlanningModelCalls.ValidateEvidenceAddresses(restored);
        foreach (var prompt in new[] { request.Prompt, PlanningModelCalls.CanonicalEvidenceRequest(restored).Prompt })
        {
            var view = Read(prompt);
            var page = view["evidence"]!.AsObject().Single(pair => pair.Value?["offset"]?.GetValue<int>() == 8).Value!;
            Assert.Equal(10, page["total"]!.GetValue<int>());
            if (sameOrder)
            {
                Assert.Null(page["items"]);
                var pointer = Assert.IsAssignableFrom<JsonValue>(page["itemsPresentedAt"]).ToString();
                Assert.True(JsonNode.DeepEquals(originals, PlanningSchemaReferences.Read(view, pointer)));
            }
            else
            {
                Assert.Null(page["itemsPresentedAt"]);
                Assert.True(JsonNode.DeepEquals(originals, page["items"]));
            }
        }
    }

    [Theory]
    [InlineData("tasks")]
    [InlineData("always")]
    public void RepeatedTaskViewsResolveToTheirExactDeclarationWithLocationAndLiteralDataPreserved(string phase)
    {
        var context = Context(phase); var frozen = context.ToJsonString();
        var prompt = "All original obligations.\n" + frozen;
        var compact = PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt);
        Assert.True(compact.Length < prompt.Length - 1000);
        var shown = Read(compact);
        var producer = shown["interfaces"]![0]!["producers"]![0]!;
        Assert.Null(producer["task"]);
        var originalTask = context["interfaces"]![0]!["producers"]![0]!["task"]!;
        Assert.True(JsonNode.DeepEquals(originalTask, PlanningSchemaReferences.Read(shown, producer["taskPresentedAt"]!.ToString())));
        var row = shown["evidence"]!["task:producer"]!;
        Assert.Equal("/root", row["scope"]!.ToString());
        Assert.Equal(phase, row["phase"]!.ToString());
        Assert.Equal("producer", row["id"]!.ToString());
        Assert.Equal(producer["taskPresentedAt"]!.ToString(), row["taskPresentedAt"]!.ToString());
        Assert.True(JsonNode.DeepEquals(context["requirements"], shown["requirements"]));
        Assert.True(JsonNode.DeepEquals(context["diagnostics"], shown["diagnostics"]));
        Assert.True(JsonNode.DeepEquals(context["interfaces"]![0]!["value"], shown["interfaces"]![0]!["value"]));
        Assert.Equal(frozen, context.ToJsonString());
        Assert.Equal(compact, PlanningPromptPresentation.ShareDuplicateContractSchemas(compact));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("phase")]
    [InlineData("id")]
    [InlineData("argument")]
    public void DifferentLocationIdentityOrTaskArgumentsCannotBorrowTheOtherDeclaration(string difference)
    {
        var context = Context(); context["evidence"]!.AsObject().Remove("task:producer");
        var producer = context["interfaces"]![0]!["producers"]![0]!;
        if (difference == "argument") producer["task"]!["inputs"]![0]!["value"]!["text"] = "different original argument";
        else producer[difference] = "different";
        var prompt = "Originals.\n" + context.ToJsonString();
        Assert.Equal(prompt, PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt));
    }

    [Fact]
    public void IssuedEvidencePointersSurviveShortAddressMappingColdRestoreAndCanonicalRecovery()
    {
        var state = PlannerFixture.Session();
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        var request = PlanningModelCalls.PrepareRequest(state, "construction_draft", "Originals.\n" + Context().ToJsonString(), schema, null, null, out var map);
        var issued = new PlanningModelCall { Id = request.ClientRequestId!, Purpose = "construction_draft", Request = request, EvidenceAddresses = map };
        PlanningModelCalls.ValidateEvidenceAddresses(issued);
        var shown = Read(request.Prompt);
        var producer = shown["interfaces"]![0]!["producers"]![0]!;
        Assert.NotNull(PlanningSchemaReferences.Read(shown, producer["taskPresentedAt"]!.ToString()));
        Assert.DoesNotContain("scope:~1root~0view", producer["taskPresentedAt"]!.ToString(), StringComparison.Ordinal);
        var bytes = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.PlanningModelCall);
        var restored = JsonSerializer.Deserialize(bytes, PlanningJsonContext.Default.PlanningModelCall)!;
        PlanningModelCalls.ValidateEvidenceAddresses(restored);
        Assert.Equal(bytes, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningModelCall));
        var canonical = Read(PlanningModelCalls.CanonicalEvidenceRequest(restored).Prompt);
        var pointer = canonical["interfaces"]![0]!["producers"]![0]!["taskPresentedAt"]!.ToString();
        Assert.Contains("scope:~1root~0view", pointer, StringComparison.Ordinal);
        var declaration = PlanningSchemaReferences.Read(canonical, pointer)!;
        Assert.True(JsonNode.DeepEquals(Context()["interfaces"]![0]!["producers"]![0]!["task"], declaration));
        Assert.Equal("/evidence/scope:~1root~0view/tasks/0", declaration["inputs"]![1]!["value"]!["text"]!.ToString());
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(0, state.ModelCalls);
    }

    [Theory]
    [InlineData("page:checkpoint:0")]
    [InlineData("page:checkpoints:0")]
    public void CheckpointKeepsExactNestedDeclarationsAtTheirOriginalLocations(string address)
    {
        var leaf = new JsonObject { ["id"] = "inspect", ["kind"] = "operation", ["objective"] = new string('x', 820) };
        var different = leaf.DeepClone().AsObject();
        different["objective"] = "different target";
        var loop = new JsonObject { ["id"] = "visit", ["kind"] = "foreach", ["body"] = new JsonObject { ["always"] = new JsonArray(leaf.DeepClone()) } };
        var guard = new JsonObject { ["id"] = "guard", ["kind"] = "if", ["otherwise"] = new JsonObject { ["tasks"] = new JsonArray(loop.DeepClone()) } };
        var original = new JsonObject { ["tasks"] = new JsonArray(leaf.DeepClone(), guard, different, loop) };
        var page = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["sequence"] = 1, ["hash"] = "original", ["fragment"] = original.DeepClone() }) };
        var context = new JsonObject { ["evidence"] = new JsonObject { [address] = page } };
        var request = PlanningModelCalls.PrepareRequest(PlannerFixture.Session(), "construction_implementation", "Choose.\n" + context.ToJsonString(),
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, null, null, out var map);
        var call = new PlanningModelCall { Id = request.ClientRequestId!, Purpose = "construction_implementation", Request = request, EvidenceAddresses = map };
        PlanningModelCalls.ValidateEvidenceAddresses(call);
        foreach (var prompt in new[] { request.Prompt, PlanningModelCalls.CanonicalEvidenceRequest(call).Prompt })
        {
            var evidence = Read(prompt)["evidence"]!.AsObject().Single().Value!;
            var row = evidence["items"]![0]!;
            Assert.Equal("original", row["hash"]!.ToString());
            var fragment = row["fragment"]!;
            var tasks = fragment["tasks"]!.AsArray();
            foreach (var index in new[] { 0, 3 })
            {
                var link = tasks[index]!;
                Assert.Equal(original["tasks"]![index]!["id"]!.ToString(), link["id"]!.ToString());
                var full = PlanningSchemaReferences.Read(fragment, link["declarationAt"]!.ToString());
                Assert.True(JsonNode.DeepEquals(original["tasks"]![index], full));
            }
            Assert.True(JsonNode.DeepEquals(guard, tasks[1]));
            Assert.True(JsonNode.DeepEquals(different, tasks[2]));
            Assert.Equal(prompt, PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt));
        }
    }

    private static JsonObject Read(string prompt) => JsonNode.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..])!.AsObject();

    private static JsonObject Context(string phase = "tasks")
    {
        var task = new JsonObject { ["id"] = "producer", ["kind"] = "operation", ["objective"] = "Preserve original behavior",
            ["inputs"] = new JsonArray(new JsonObject { ["name"] = "instruction", ["value"] = new JsonObject { ["kind"] = "string", ["text"] = new string('x', 2000) } },
                new JsonObject { ["name"] = "literal", ["value"] = new JsonObject { ["kind"] = "string", ["text"] = "/evidence/scope:~1root~0view/tasks/0" } }) };
        var row = task.DeepClone().AsObject(); row["scope"] = "/root"; row["phase"] = phase;
        return new JsonObject
        {
            ["evidence"] = new JsonObject
            {
                ["scope:/root~view"] = new JsonObject { ["path"] = "/root", ["tasks"] = phase == "tasks" ? new JsonArray(task.DeepClone()) : new JsonArray(),
                    ["always"] = phase == "always" ? new JsonArray(task.DeepClone()) : new JsonArray() },
                ["task:producer"] = row
            },
            ["interfaces"] = new JsonArray(new JsonObject { ["value"] = new JsonObject { ["kind"] = "output", ["source"] = "producer" },
                ["producers"] = new JsonArray(new JsonObject { ["id"] = "producer", ["scope"] = "/root", ["phase"] = phase, ["task"] = task.DeepClone() }) }),
            ["requirements"] = new JsonArray("Exact original obligation"),
            ["diagnostics"] = new JsonArray(new JsonObject { ["code"] = "EXACT_FINDING", ["message"] = "Retained finding" })
        };
    }
}
