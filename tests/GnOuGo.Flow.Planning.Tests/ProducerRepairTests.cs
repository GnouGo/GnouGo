using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ProducerRepairTests
{
    [Theory]
    [InlineData("instructions", "Parse the resource and retain instructions for the review")]
    [InlineData("consignes", "Lire la ressource et conserver les consignes de revision")]
    public void MissingBusinessInputRevisitsTheNativeResultContractWithoutChangingApproval(string input, string purpose)
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); var workflow = state.Graph!.Workflows[0]; var node = workflow.Steps[0];
        node.Purpose = purpose; node.Input = Obj(("owner", Str("example")));
        node.OutputSchema = new() { Type = "object", Properties = [new() { Name = "owner", Schema = new() { Type = "string" } }] };
        workflow.Inputs = [new() { Name = input, Schema = new() { Type = "string" } }];
        state.BehaviorPlan!.Workflows[0].Steps[0].InputDependencies = [input];
        state.BehaviorPlan.Workflows[0].Inputs = [new(input, "Runtime instructions", true)];
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        var contract = new PlanningConstructionUnit { Key = "contract", WorkflowKey = workflow.Key, Kind = "contracts", NodeKeys = [node.Key], Status = "validated", ContractVersion = PlanningDataflow.ContractVersion };
        contract.Candidate = PlanningConstruction.Values(workflow, contract);
        var implementation = new PlanningConstructionUnit { Key = "implementation", WorkflowKey = workflow.Key, Kind = "implementation", NodeKeys = [node.Key], Status = "invalid", ContractVersion = PlanningDataflow.ContractVersion,
            Calls = 2, RepairCalls = 1, Dependencies = [contract.Key], Diagnostics = [new("BUSINESS_INPUT_BINDING_MISSING", "/workflows/0/steps/0/input", "Missing input")] };
        implementation.Candidate = PlanningConstruction.UpgradeCandidate(state.Graph, implementation, PlanningConstruction.Values(workflow, implementation), state.Preparation!);
        state.ConstructionUnits = [contract, implementation]; var approval = state.ApprovedBehaviorHash;
        Assert.True(PlanningProducerRepair.Schedule(state));
        Assert.Equal("/workflows/0/steps/0/outputSchema", Assert.Single(contract.Diagnostics).Location);
        Assert.Contains(input, contract.Diagnostics[0].Message); Assert.Equal(approval, state.ApprovedBehaviorHash);
        var patch = PlanningUnitPatches.Create(state.Graph, contract, PlanningConstruction.Schema(workflow, contract, state.Preparation!, state.Graph), state.Preparation);
        var field = Assert.Single(patch.Context(contract.Candidate));
        Assert.Equal("nodes/greeting/outputSchema", field.Key);
        var changed = field.Value!.DeepClone(); changed["properties"]!.AsArray().Add(Field(input));
        var candidate = patch.Apply(contract.Candidate, new() { ["changes"] = new JsonObject { [field.Key] = changed }, ["remove"] = new JsonArray() });
        PlanningProducerRepair.Preserve(contract.ProducerReviewBaseline!, candidate);
        Assert.False(PlanningProducerRepair.Schedule(state));
        state.Graph = PlanningConstruction.Apply(state.Graph, contract, candidate, state.Preparation!);
        contract.Status = "validated";
        PlanningProducerRepair.ResumeConsumers(state);
        var implementationPatch = PlanningUnitPatches.Create(state.Graph, implementation,
            PlanningConstruction.Schema(state.Graph.Workflows[0], implementation, state.Preparation!, state.Graph), state.Preparation);
        Assert.Equal("nodes/greeting/values/" + input, Assert.Single(implementationPatch.Context(implementation.Candidate)).Key);
        var context = TypedWorkflowPlanner.ContractPrompt(state, workflow, contract, state.Preparation!);
        Assert.Contains("ownedInputDependencies", context); Assert.Contains(input, context);
    }

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
