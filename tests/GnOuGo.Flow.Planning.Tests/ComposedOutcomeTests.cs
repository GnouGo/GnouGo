using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ComposedOutcomeTests
{
    internal static PlanningSession Recorded(string name) => Recording(name)["finalSession"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
    internal static JsonNode Recording(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ContractAwarePlanning", "retained-" + name + ".json")))!;

    [Fact]
    public void RetainedAgentFailureKeepsBothRequiredDiagnosticsAndValidOutputConstraints()
    {
        var state = Recorded("copilot");
        var final = new TaskPlanCompiler().Compile(state.Plan!, state.Catalog!);
        Assert.Equal(new[] { "TASK_INPUT_TYPE", "AGENT_SCOPE_DYNAMIC" }, final.Diagnostics.Select(d => d.Code));
        var repairedSchemaPlan = Recording("copilot")["proposals"]!.AsArray().Single(p => p!["revision"]!.GetValue<int>() == 10)!["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        Assert.Equal(new[] { "TASK_INPUT_TYPE", "AGENT_SCOPE_DYNAMIC" }, new TaskPlanCompiler().Compile(repairedSchemaPlan, state.Catalog!).Diagnostics.Select(d => d.Code));
        var work = state.Plan!.Root.Tasks.Single(t => t.Id == "run_copilot_review_agent");
        var clone = state.Plan.Root.Tasks.Single(t => t.Id == "clone_repository_once");
        work.Inputs.Single(i => i.Name == "workspace").Value.Text = clone.Inputs.Single(i => i.Name == "targetDirectory").Value.Text;
        var workspace = work.Inputs.Single(i => i.Name == "workspace").Value;
        workspace.Kind = "string"; workspace.Source = null; workspace.Port = null;
        Assert.Equal("TASK_INPUT_TYPE", Assert.Single(new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics).Code);
        Assert.Equal(6, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
    }

    internal static async Task<PlanningSession> State()
    {
        var state = PlannerFixture.Session(); state.IntentVersion = 2;
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        var empty = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!.AsObject();
        state.Catalog.Capabilities.Add(new() { Id = "external", Kind = "tool", StepType = "mcp.call", Server = "arbitrary", Method = "omega", Version = "1", EffectKind = "write", InputSchema = empty, OutputSchema = empty });
        state.Requirements = new() { Summary = "Perform requested work", Inputs = [], Outcomes = [PlanningOutcomeTests.Outcome("work") with { Coverage = "once" }] };
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "perform", Objective = "Perform requested work", Operation = "external" }], Outputs = [new("report", PlanningCorpus.String("done"))] } };
        state.OutcomeBindings = [new("work", ["perform"], ["report"])]; return state;
    }

}
