using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class WorkflowProvenanceTests
{
    [Theory]
    [InlineData("value.trim()", true)]
    [InlineData("value ? null : value", false)]
    public void RuntimeCheckedComputedArgumentsRetainExplicitProducerEvidence(string expression, bool valid)
    {
        var (graph, prep, _, _, call) = Fixture();
        var argument = new PlanningValue { Kind = "compute", Text = expression,
            Members = [new("value", new() { Kind = "output", Source = "source" })] };
        call.Input = Obj(("ref", new() { Kind = "workflow", Source = "callee" }), ("args", Obj(("value", argument))));
        Assert.Equal(!valid, PlanningDataflow.OperationInputFindings(graph, prep).Any(d => d.Code == "WORKFLOW_INPUT_PROVENANCE_MISSING"));
        argument.Members[0] = new("value", Str("unrelated"));
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "OPERATION_INPUT_BINDING_MISSING");
    }

    [Fact]
    public void CaseConditionsContributeOnlyToTheirOwnAndLaterBranches()
    {
        var first = new PlanningNode { Key = "first", Input = Str("first") };
        var second = new PlanningNode { Key = "second", Input = Str("second") };
        var fallback = new PlanningNode { Key = "fallback", Input = Str("fallback") };
        var decision = new PlanningNode { Key = "choose", Type = "switch", Cases =
        [
            new("first", new() { Kind = "input", Source = "firstCondition" }, [first]),
            new("second", new() { Kind = "input", Source = "threshold" }, [second]),
            new("third", new() { Kind = "input", Source = "laterCondition" }, [new() { Key = "unrelated", Input = new() { Kind = "input", Source = "unrelated" } }])
        ], Default = [fallback] };
        var workflow = new PlanningWorkflow { Key = "main", Steps = [decision] };
        Assert.Contains("threshold", PlanningDataflow.BusinessInputs(workflow, decision));
        Assert.DoesNotContain("threshold", PlanningDataflow.BusinessInputs(workflow, first));
        var dependencies = PlanningDataflow.BusinessInputs(workflow, second);
        Assert.Contains("firstCondition", dependencies); Assert.Contains("threshold", dependencies);
        Assert.DoesNotContain("laterCondition", dependencies); Assert.DoesNotContain("unrelated", dependencies);
        Assert.Contains("laterCondition", PlanningDataflow.BusinessInputs(workflow, fallback));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaseConditionsProveOperationDependenciesOnlyWhenEvaluated(bool ignored)
    {
        var read = new PlanningNode { Key = "observe", OperationIds = ["observation"], Input = Obj(("allow", new() { Kind = "boolean", Boolean = true })) };
        var decision = new PlanningNode { Key = "choose", Type = "switch", Expr = ignored ? Str("go") : null,
            Cases = [new("go", new() { Kind = "output", Source = "observe", Path = ["allow"] }, [])] };
        var workflow = new PlanningWorkflow { Key = "main", Steps = [read, decision] };
        var dependencies = PlanningDataflow.OperationDependencies(workflow, decision, Preparation(), new() { Workflows = [workflow] }).Operations;
        Assert.Equal(!ignored, dependencies.Contains("observation"));
    }

    [Fact]
    public void BranchAndLoopInputsCountWithoutBorrowingUnrelatedSiblingInputs()
    {
        var leaf = new PlanningNode { Key = "leaf", Input = Obj(("category", new() { Kind = "string", Text = "high" })) };
        var unrelated = new PlanningNode { Key = "unrelated", Input = new() { Kind = "input", Source = "unrelated" } };
        var branch = new PlanningNode { Key = "choose", Type = "switch", Expr = new() { Kind = "input", Source = "threshold" }, Cases = [new("high", null, [leaf]), new("other", null, [unrelated])] };
        var loop = new PlanningNode { Key = "each", Type = "loop.sequential", Input = Obj(("items", new() { Kind = "input", Source = "records" })), Steps = [branch] };
        var workflow = new PlanningWorkflow { Key = "main", Steps = [loop] };
        var dependencies = PlanningDataflow.BusinessInputs(workflow, leaf);
        Assert.Contains("threshold", dependencies);
        Assert.Contains("records", dependencies);
        Assert.DoesNotContain("unrelated", dependencies);
    }

    [Fact]
    public void ModelLoopReferencesUseDeclaredNodeKeysInsteadOfRuntimeVariableAliases()
    {
        var workflow = new PlanningWorkflow { Key = "main", Inputs = [new() { Name = "records" }], Steps = [new() { Key = "each", Type = "loop.sequential", ItemVar = "item" }] };
        var schema = PlanningSchemas.WholeWorkflow(Preparation());
        PlanningSchemas.ScopeValues(schema, workflow);
        var variant = schema["$defs"]!["value"]!["anyOf"]!.AsArray().Single(v => v!["properties"]!["kind"]!["enum"]![0]!.ToString() == "loop_item")!;
        Assert.Equal("[\"each\"]", variant["properties"]!["source"]!["enum"]!.ToJsonString());
    }

    [Fact]
    public void AnUnrelatedReturnedFieldCannotClaimAnotherOutputsProducer()
    {
        var (graph, prep, caller, callee, _) = Fixture();
        callee.Outputs.Add(new() { Name = "unrelated", Schema = new() { Type = "string" }, Value = new() { Kind = "input", Source = "value" } });
        caller.Steps[^1].Input.Path = ["unrelated"];
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "OPERATION_INPUT_BINDING_MISSING" && d.Location == "/workflows/0/steps/2/input");
    }

    [Fact]
    public void InvocationInputsAndReturnedOperationsCrossTheWorkflowBoundary()
    {
        var (graph, prep, caller, callee, call) = Fixture();
        Assert.Empty(PlanningDataflow.OperationInputFindings(graph, prep));
        // Returning an unrelated input cannot claim the callee's unused processing operation.
        callee.Outputs[0].Value = new() { Kind = "input", Source = "value" };
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "OPERATION_INPUT_BINDING_MISSING" && d.Location == "/workflows/0/steps/2/input");
    }

    [Fact]
    public void AConstantCalleeCannotClaimAnInvocationDependency()
    {
        var (graph, prep, _, callee, _) = Fixture();
        callee.Steps[0].Input = new() { Kind = "string", Text = "invented" };
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "OPERATION_INPUT_BINDING_MISSING" && d.Location == "/workflows/1/steps/0/input");
    }

    [Fact]
    public void MissingOrMistypedCallerArgumentsAreLocatedOnTheCaller()
    {
        var (graph, prep, _, _, call) = Fixture();
        foreach (var argument in new[] { Obj(), Obj(("value", new PlanningValue { Kind = "number", Number = 7 })) })
        {
            call.Input = Obj(("ref", new() { Kind = "workflow", Source = "callee" }), ("args", argument));
            Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "WORKFLOW_INPUT_PROVENANCE_MISSING" && d.Location == "/workflows/0/steps/1/input");
        }
    }

    [Fact]
    public void EveryCallSiteMustEstablishTheConsumedOperation()
    {
        var (graph, prep, caller, _, _) = Fixture();
        caller.Finally.Add(new() { Key = "other", Type = "workflow.call", Input = Obj(("ref", new() { Kind = "workflow", Source = "callee" }), ("args", Obj(("value", new() { Kind = "string", Text = "unrelated" })))) });
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "WORKFLOW_INPUT_PROVENANCE_MISSING" && d.Location == "/workflows/0/finally/0/input");
    }

    [Fact]
    public void InvocationsRetainTheirOwnLockedProducerDependencies()
    {
        var (graph, prep, _, _, call) = Fixture();
        call.Input = Obj(("ref", new() { Kind = "workflow", Source = "callee" }), ("args", Obj(("value", new() { Kind = "string", Text = "substitute" }))));
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "OPERATION_INPUT_BINDING_MISSING" && d.Location == "/workflows/0/steps/1/input");
    }

    private static (PlanningGraph Graph, PlanningPreparation Prep, PlanningWorkflow Caller, PlanningWorkflow Callee, PlanningNode Call) Fixture()
    {
        var prep = Preparation();
        prep.Capabilities = [new() { Id = "source", OperationIds = ["read"] },
            new() { Id = "invoke", OperationIds = ["invoke"], InputOperationIds = ["read"] },
            new() { Id = "process", OperationIds = ["process"], InputOperationIds = ["invoke"] },
            new() { Id = "finish", OperationIds = ["finish"], InputOperationIds = ["process"] }];
        var caller = new PlanningWorkflow { Key = "caller", Inputs = [new() { Name = "value", Schema = new() { Type = "string" } }] };
        var callee = new PlanningWorkflow { Key = "callee", Inputs = [new() { Name = "value", Schema = new() { Type = "string" } }],
            Steps = [new() { Key = "process", CapabilityId = "process", OperationIds = ["process"], Input = new() { Kind = "input", Source = "value" }, OutputSchema = new() { Type = "string" } }],
            Outputs = [new() { Name = "result", Schema = new() { Type = "string" }, Value = new() { Kind = "output", Source = "process" } }] };
        var call = new PlanningNode { Key = "invoke", Type = "workflow.call", OperationIds = ["invoke"], Input = Obj(("ref", new() { Kind = "workflow", Source = "callee" }), ("args", Obj(("value", new() { Kind = "output", Source = "source" })))) };
        caller.Steps = [new() { Key = "source", CapabilityId = "source", OperationIds = ["read"], Input = new() { Kind = "input", Source = "value" }, OutputSchema = new() { Type = "string" } }, call,
            new() { Key = "finish", CapabilityId = "finish", OperationIds = ["finish"], Input = new() { Kind = "output", Source = "invoke", Path = ["result"] } }];
        return (new() { Entrypoint = "caller", Workflows = [caller, callee] }, prep, caller, callee, call);
    }
}
