using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningRepeatedTaskPresentationTests
{
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
