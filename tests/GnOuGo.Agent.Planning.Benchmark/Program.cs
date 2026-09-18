using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

ILLMClient? live = args.Length == 2 && args[0] == "--live-command" ? new CommandModel(args[1]) : null;
if (args.Length > 0 && live is null) throw new ArgumentException("Usage: [--live-command /absolute/path/to/model-adapter]");
foreach (var name in PlanningCorpus.Names)
{
    var effects = new List<string>(); var engine = new WorkflowEngine { McpClientFactory = PlanningCorpus.Factory(effects), HumanInputProvider = new PlanningCorpus.Human() };
    var runtime = new PlanningCorpus.Runtime(name, engine, live); var planner = new TypedWorkflowPlanner();
    var state = new PlanningSession { Request = new() { TenantId = "smoke", Prompt = PlanningCorpus.Prompt(name) } };
    for (var i = 0; i < 20 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); i++)
    {
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, CancellationToken.None);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
    }
    if (state.Status != PlanningStatus.FinalReview || live is null && state.ModelCalls != 1 || effects.Count != 0) throw new InvalidOperationException(JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
    state = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, CancellationToken.None);
    if (state.Status != PlanningStatus.Approved) throw new InvalidOperationException("Approval failed.");
    var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
    var result = await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), CancellationToken.None);
    if (!result.Success || result.Outputs?["result"]?.ToJsonString() != "42") throw new InvalidOperationException("Independent result assertion failed: " + result.Error?.Message);
    var expected = name == "protected_cleanup" ? new[] { "write", "cleanup" } : name == "read_transform" ? ["read"] : [];
    if (!effects.SequenceEqual(expected)) throw new InvalidOperationException("Effect assertion failed.");
    Console.WriteLine(new JsonObject { ["case"] = name, ["passed"] = true, ["model_calls"] = state.ModelCalls, ["repair_attempts"] = state.RepairAttempts, ["scenarios"] = state.Scenarios.Count }.ToJsonString());
}
sealed class CommandModel(string executable) : ILLMClient
{
    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })
            ?? throw new InvalidOperationException("Model adapter did not start.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token); var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest).AsMemory(), deadline.Token);
            process.StandardInput.Close(); await process.WaitForExitAsync(deadline.Token); await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException("Model adapter exited with code " + process.ExitCode);
            return JsonSerializer.Deserialize(await output, PlanningJsonContext.Default.LLMResponse) ?? throw new InvalidOperationException("Empty model receipt.");
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
