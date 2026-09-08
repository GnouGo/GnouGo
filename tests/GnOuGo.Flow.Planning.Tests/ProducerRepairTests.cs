using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ProducerRepairTests
{
    [Theory]
    [InlineData("source", "Read resource metadata")]
    [InlineData("renamed-capability", "Lire les metadonnees de la ressource")]
    public void MissingDependencyRevisitsOnlyContributingSynthesizedContracts(string source, string purpose)
    {
        var state = Fixture(source, purpose); var approval = state.ApprovedBehaviorHash;
        var unrelated = state.ConstructionUnits.Single(u => u.Key == "unrelated").Candidate!.ToJsonString();
        Assert.True(PlanningProducerRepair.Schedule(state));
        var producer = state.ConstructionUnits.Single(u => u.Key == "producer-contract");
        Assert.NotNull(producer.ProducerReviewBaseline); Assert.Equal("invalid", producer.Status);
        Assert.Equal(PlanningProducerRepair.DiagnosticCode, Assert.Single(producer.Diagnostics).Code);
        Assert.Equal("validated", state.ConstructionUnits.Single(u => u.Key == "unrelated").Status);
        Assert.Equal(unrelated, state.ConstructionUnits.Single(u => u.Key == "unrelated").Candidate!.ToJsonString());
        Assert.Equal(approval, state.ApprovedBehaviorHash); Assert.Single(state.Answers); Assert.Equal(9, state.Usage!.Calls);
        Assert.False(PlanningProducerRepair.Schedule(state));
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        Assert.NotNull(state.ConstructionUnits.Single(u => u.Key == "producer-contract").ProducerReviewBaseline);
        PlanningProducerRepair.ResumeConsumers(state);
        Assert.True(state.ConstructionUnits.Single(u => u.Key == "consumer").WaitingForProducerReview);
        state.ConstructionUnits.Single(u => u.Key == "producer-contract").Status = "validated";
        PlanningProducerRepair.ResumeConsumers(state);
        var consumer = state.ConstructionUnits.Single(u => u.Key == "consumer");
        Assert.False(consumer.WaitingForProducerReview); Assert.Equal(consumer.RepairCalls, consumer.RepairCallsAtRetry);
        Assert.False(PlanningProducerRepair.Schedule(state)); // No identical review loop after restart.
    }

    [Fact]
    public void OriginalCapabilitySchemasAreNeverRepairTargets()
    {
        var state = Fixture(); var producer = state.Graph!.Workflows[0].Steps[0].Branches[0].Steps[0];
        producer.StructuredOutput = null;
        state.Preparation!.Capabilities[0].OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" } } };
        Assert.False(PlanningProducerRepair.Schedule(state));
    }

    [Fact]
    public void PatchCanAddARequiredFieldWithoutRegeneratingOrWeakeningExistingFields()
    {
        var state = Fixture(); Assert.True(PlanningProducerRepair.Schedule(state));
        var unit = state.ConstructionUnits.Single(u => u.Key == "producer-contract");
        var workflow = state.Graph!.Workflows[0];
        var schema = PlanningConstruction.Schema(workflow, unit, state.Preparation!, state.Graph);
        var patch = PlanningUnitPatches.Create(state.Graph, unit, schema, state.Preparation);
        var coordinate = Assert.Single(patch.Context(unit.Candidate));
        Assert.Equal("nodes/metadata/structuredOutput/schema", coordinate.Key);
        var changed = coordinate.Value!.DeepClone();
        changed["properties"]!.AsArray().Add(Field("revision"));
        var result = patch.Apply(unit.Candidate, new() { ["changes"] = new JsonObject { [coordinate.Key] = changed }, ["remove"] = new JsonArray() });
        PlanningProducerRepair.Preserve(unit.ProducerReviewBaseline!, result);
        Assert.Equal("name", result["nodes"]!["metadata"]!["structuredOutput"]!["schema"]!["properties"]![0]!["name"]!.GetValue<string>());
        var compiled = PlanningConstruction.Apply(state.Graph, unit, result, state.Preparation!);
        var consumer = state.ConstructionUnits.Single(u => u.Key == "consumer");
        var binding = PlanningDataflow.Index(compiled.Workflows[0], state.Preparation!, compiled, "consume").Values.Single(b =>
            b.Value.Source == "observation" && b.Value.Path.SequenceEqual(new[] { "branches", "0", "metadata", "json", "revision" }));
        var implementation = consumer.Candidate!.DeepClone().AsObject();
        implementation["nodes"]!["consume"]!["arguments"]!["revision"] = new JsonObject { ["kind"] = "binding", ["reference"] = binding.Id };
        var applied = PlanningConstruction.Apply(compiled, consumer, implementation, state.Preparation!);
        Assert.DoesNotContain(PlanningDataflow.OperationInputFindings(applied, state.Preparation!), d => d.Code == "OPERATION_INPUT_BINDING_MISSING");
        Assert.Equal("unrelated", applied.Workflows[0].Steps[1].Input.Text);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("type")]
    [InlineData("nullable")]
    [InlineData("required")]
    [InlineData("duplicate")]
    public void ProducerRepairRejectsChangedOrInventedExistingContracts(string mutation)
    {
        var state = Fixture(); var before = state.ConstructionUnits.Single(u => u.Key == "producer-contract").Candidate!;
        var after = before.DeepClone().AsObject(); var fields = after["nodes"]!["metadata"]!["structuredOutput"]!["schema"]!["properties"]!.AsArray();
        if (mutation == "remove") fields.Clear();
        else if (mutation == "duplicate") fields.Add(fields[0]!.DeepClone());
        else if (mutation == "required") fields[0]!["required"] = false;
        else if (mutation == "nullable") fields[0]!["schema"]!["nullable"] = true;
        else fields[0]!["schema"]!["type"] = "number";
        Assert.Throws<InvalidOperationException>(() => PlanningProducerRepair.Preserve(before, after));
        PlanningProducerRepair.Preserve(before, before.DeepClone().AsObject()); // Unrelated contributors need not invent fields.
    }

    private static JsonObject Field(string name) => new()
    {
        ["name"] = name, ["required"] = true, ["default"] = null, ["schema"] = new JsonObject { ["kind"] = "inline", ["type"] = "string", ["nullable"] = false, ["description"] = null, ["enum"] = new JsonArray() }
    };

    internal static PlanningSnapshot Fixture(string capability = "source", string purpose = "Read metadata")
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); state.Answers.Add(new("Retained answer", new() { ["choice"] = true })); state.Usage = new() { Calls = 9 };
        var schema = new JsonObject { ["kind"] = "inline", ["type"] = "object", ["nullable"] = false, ["description"] = null,
            ["properties"] = new JsonArray(Field("name")), ["additionalProperties"] = null };
        var metadata = new PlanningNode { Key = "metadata", Type = "mcp.call", CapabilityId = capability, Purpose = purpose,
            StructuredOutput = new(JsonSerializer.Deserialize(schema, PlanningJsonContext.Default.PlanningSchema)!) };
        var workflow = state.Graph!.Workflows[0]; workflow.Outputs.Clear();
        workflow.Steps = [new() { Key = "observation", Type = "parallel", OperationIds = ["observe"], Branches = [new([metadata])] },
            new() { Key = "unrelated", Type = "set", Input = Str("unrelated") },
            new() { Key = "consume", Type = "mcp.call", CapabilityId = "destination", OperationIds = ["act"] }];
        state.Preparation!.Capabilities = [new() { Id = capability, StepType = "mcp.call" }, new() { Id = "destination", StepType = "mcp.call", OperationIds = ["act"], InputOperationIds = ["observe"],
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["revision"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("revision") } }];
        state.ConstructionUnits = [new() { Key = "producer-contract", WorkflowKey = "main", Kind = "contracts", NodeKeys = ["metadata"], Status = "validated", ContractVersion = PlanningDataflow.ContractVersion,
            Candidate = new() { ["nodes"] = new JsonObject { ["metadata"] = new JsonObject { ["structuredOutput"] = new JsonObject { ["schema"] = schema } } } } },
            new() { Key = "unrelated", WorkflowKey = "main", Kind = "contracts", NodeKeys = ["unrelated"], Status = "validated", Candidate = new() },
            new() { Key = "consumer", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["consume"], Status = "invalid", ContractVersion = PlanningDataflow.ContractVersion, Calls = 4, RepairCalls = 3,
                Dependencies = ["producer-contract"], Diagnostics = [new("OPERATION_INPUT_BINDING_MISSING", "/workflows/0/steps/2/input", "Missing upstream input.")],
                Candidate = new() { ["functions"] = "", ["nodes"] = new JsonObject { ["consume"] = new JsonObject { ["arguments"] = new JsonObject { ["revision"] = new JsonObject { ["kind"] = "string", ["text"] = "example" } }, ["onError"] = new JsonArray() } } } }];
        return state;
    }
}
