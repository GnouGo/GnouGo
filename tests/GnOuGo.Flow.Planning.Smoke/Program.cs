using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

foreach (var name in PlanningCorpus.Names)
{
    var environment = new PlanningBenchmarkCases.Environment(name); var engine = new WorkflowEngine { McpClientFactory = environment.Factory(), HumanInputProvider = new PlanningCorpus.Human() };
    var runtime = new PlanningCorpus.Runtime(name, engine); var planner = new TypedWorkflowPlanner();
    var state = new PlanningSession { Request = new() { TenantId = "smoke", Prompt = PlanningCorpus.Prompt(name) } };
    for (var i = 0; i < 20 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
    {
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, CancellationToken.None);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
    }
    if (state.Status != PlanningStatus.FinalReview || state.ModelCalls != 1 || environment.Effects.Count != 0) throw new InvalidOperationException(JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
    state = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, CancellationToken.None);
    if (state.Status != PlanningStatus.Approved) throw new InvalidOperationException("Approval failed.");
    var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
    var result = await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], PlanningBenchmarkCases.Inputs(name, "nominal"), CancellationToken.None);
    if (!environment.Verify(result)) throw new InvalidOperationException("Independent result assertion failed: " + result.Error?.Message);
    Console.WriteLine(name + ": passed, calls=1, repairs=0, scenarios=" + state.Scenarios.Count);
}
Console.WriteLine("Planner Native AOT smoke passed.");
