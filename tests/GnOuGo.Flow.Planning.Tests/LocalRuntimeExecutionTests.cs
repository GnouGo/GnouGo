using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LocalRuntimeExecutionTests
{
    [Theory]
    [InlineData("accepted", 150, true, true, "high")]
    [InlineData("rejected", 150, false, true, "rejected")]
    [InlineData("boundary", 100, true, true, "high")]
    [InlineData("omitted_default", 99, true, true, "standard")]
    [InlineData("invalid_input", 100, true, false, null)]
    [InlineData("null_threshold", 100, true, false, null)]
    public async Task EvidencedLocalSkeletonExecutesWithoutExternalCapabilities(string name, int amount, bool approved, bool valid, string? category)
    {
        var ct = TestContext.Current.CancellationToken;
        var state = OperationAdmissionTests.State("Classify the supplied record using the threshold and approval rule.");
        var recordSchema = new PlanningSchema { Type = "object", Properties = [new() { Name = "id", Schema = new() { Type = "string" } },
            new() { Name = "amount", Schema = new() { Type = "number" } }, new() { Name = "approved", Schema = new() { Type = "boolean" } }] };
        var resultSchema = new PlanningSchema { Type = "object", Properties = [new() { Name = "id", Schema = new() { Type = "string" } },
            new() { Name = "amount", Schema = new() { Type = "number" } }, new() { Name = "category", Schema = new() { Type = "string", Enum = ["rejected", "high", "standard"] } }] };
        state.Request.Baseline = new() { Workflows = [new() { Key = "main", Inputs = [new() { Name = "record", Schema = recordSchema },
            new() { Name = "threshold", Required = false, Default = new() { Kind = "number", Number = 100 }, Schema = new() { Type = "number" } }],
            Outputs = [new() { Name = "classifiedResult", Schema = resultSchema }] }] };
        PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request").Clause);
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, _, _) => throw new InvalidOperationException("Unexpected model call.") };
        OperationEffectFixtures.Seed(state);
        await PlanningOperations.ResolveAsync(state, runtime, ct);
        var operation = Assert.Single(state.Obligations);
        state.Preparation = TypedPlannerTests.Preparation(); state.Preparation.Capabilities = [new() { Id = "local", Resolution = "local", StepType = "set", Required = true, OperationIds = [operation.Id] }];
        state.BehaviorPlan = PlanningBehaviorDecisions.Assemble(state, new()); PlanningGraphSkeleton.Create(state);
        var workflow = Assert.Single(state.Graph!.Workflows); var node = Assert.Single(workflow.Steps); Assert.Null(node.CapabilityId);
        // Synthetic executable-hole fixture; no live generation or acceptance is claimed.
        node.Input = new() { Kind = "expression", Text = "({id:data.inputs.record.id,amount:data.inputs.record.amount,category:!data.inputs.record.approved?'rejected':data.inputs.record.amount>=data.inputs.threshold?'high':'standard'})" };
        node.OutputSchema = resultSchema; workflow.Outputs[0].Value = new() { Kind = "output", Source = node.Key };
        var yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Preparation);
        Assert.Equal(yaml, new PlanningGraphCompiler().Compile(state.Graph, state.Preparation)); Assert.Empty(runtime.Requests);
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var input = new JsonObject { ["record"] = new JsonObject { ["id"] = "item-1", ["amount"] = amount, ["approved"] = approved } };
        if (name != "omitted_default") input["threshold"] = name == "null_threshold" ? null : JsonValue.Create(100);
        if (name == "invalid_input") input["record"] = "invalid";
        try
        {
            var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], input, ct);
            Assert.Equal(valid, result.Success);
            if (valid) Assert.True(JsonNode.DeepEquals(new JsonObject { ["classifiedResult"] = new JsonObject { ["id"] = "item-1", ["amount"] = amount, ["category"] = category } }, result.Outputs));
        }
        catch (WorkflowRuntimeException) when (!valid) { }
    }
}
