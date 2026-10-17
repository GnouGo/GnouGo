using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class GuardCompositionGuidanceTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task DeclaredGuardsPreserveAllThreeAssertionsWithoutInference(bool success, bool available, bool complete)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual); state.Catalog.Policy.RequireExternalConfirmation = false;
        var request = new PlanningPrompt(state).Request();
        Assert.Contains("Use exact contracts and declared guarantees", request.Prompt);
        // This fixture declares its own flags. The shared prompt supplies no
        // conventional field names or prescribed acquisition sequence.
        var guard = new TaskValue { Kind = "predicate", Predicate = "and", Items = [
            new() { Kind = "output", Source = "p", Port = "success" },
            new() { Kind = "predicate", Predicate = "and", Items = [
                new() { Kind = "output", Source = "p", Port = "available" },
                new() { Kind = "output", Source = "p", Port = "complete" }] }] };
        var fields = new[] { "success", "available", "complete" };
        var plan = new TaskPlan { Inputs = fields.Select(f => new TaskInput { Name = f, Type = new() { Kind = "boolean" } }).ToList(), Root = new()
        {
            Tasks = [new() { Id = "p", Kind = "value", Objective = "Retain caller-declared flags without inference",
                Outputs = fields.Select(f => new TaskOutput(f, new() { Kind = "input", Source = f })).ToList() },
                new() { Id = "consume", Kind = "value", Objective = "Publish only after all three ordered assertions", Requires = guard,
                    Outputs = [new("result", new() { Kind = "string", Text = "accepted" })] }],
            Outputs = [new("result", new() { Kind = "output", Source = "consume", Port = "result" })]
        } };
        runtime.Proposal.Plan = plan;
        runtime.Proposal.Requirements = new() { Summary = "Assert all declared flags", Inputs = plan.Inputs,
            Outcomes = [new("checked", "Publish only when all three flags hold")], Outputs = [new() { Name = "result", Type = new() }] };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(PlanningContractValidation.ValidateInstance(TestRuntime.Response(runtime.Calls[0], runtime.Proposal).Json, runtime.Calls[0].StructuredOutputSchema!));
        var result = await LookupCompilationTests.Execute(state.Yaml!, new JsonObject { ["success"] = success, ["available"] = available, ["complete"] = complete }, new WorkflowEngine());
        Assert.Equal(success && available && complete, result.Success);
        if (!result.Success) Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
        Assert.Single(runtime.Calls);
        var hint = Assert.Single(PlanningSchemaReferences.Walk(runtime.Calls[0].StructuredOutputSchema!.AsObject(), "", 0), s => s.Schema.ContainsKey("description")).Schema["description"]!.ToString();
        Assert.Contains("and/or: binary; nest", hint);
        Assert.Contains("field.port: exact property", hint);
    }
}
