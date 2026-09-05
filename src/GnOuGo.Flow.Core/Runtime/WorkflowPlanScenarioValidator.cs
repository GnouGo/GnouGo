using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime.Executors;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Bounded synthetic path testing. No production integration is executed.</summary>
internal static class WorkflowPlanScenarioValidator
{
    public static async Task<IReadOnlyList<PlanningScenarioResult>> ValidateAsync(WorkflowDocument document, IMcpClientFactory? fakeFactory, CancellationToken ct)
    {
        var definitions = new List<Scenario> { new("nominal", null, null, "normal") };
        foreach (var (workflowName, workflow) in document.Workflows)
            foreach (var step in Enumerate(workflow.Steps).Concat(Enumerate(workflow.Finally)))
            {
                if (step.Type == "switch")
                {
                    for (var i = 0; i < (step.Cases?.Count ?? 0); i++) definitions.Add(new($"branch:{workflowName}:{step.Id}:{i}", workflowName, step.Id, "branch", i));
                    definitions.Add(new($"default:{workflowName}:{step.Id}", workflowName, step.Id, "branch", -1));
                }
                if (step.If is not null)
                {
                    definitions.Add(new($"guard:true:{workflowName}:{step.Id}", workflowName, step.Id, "guard_true"));
                    definitions.Add(new($"guard:false:{workflowName}:{step.Id}", workflowName, step.Id, "guard_false"));
                }
                if (step.Type is "mcp.call" or "llm.call" && Enumerate(workflow.Steps).Contains(step))
                {
                    definitions.Add(new($"failure:{workflowName}:{step.Id}", workflowName, step.Id, "failure"));
                    definitions.Add(new($"cancellation:{workflowName}:{step.Id}", workflowName, step.Id, "cancellation"));
                }
            }
        if (definitions.Count > 100)
            return [new("coverage", "inconclusive", "Synthetic scenario limit exceeded.", [new("SCENARIO_LIMIT", "$", "More than 100 scenarios are required; reduce the workflow or increase explicit coverage support.")])];
        var results = new List<PlanningScenarioResult>();
        foreach (var scenario in definitions)
        {
            ct.ThrowIfCancellationRequested();
            var doc = WorkflowParser.Parse(document.RawYaml ?? throw new InvalidOperationException("Scenario validation requires the exported artifact."));
            var telemetry = new CoverageTelemetry();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (scenario.Step is not null)
            {
                ForcePath(doc, scenario.Workflow!, scenario.Step, new HashSet<string>(StringComparer.Ordinal));
                var target = Enumerate(doc.Workflows[scenario.Workflow!].Steps.Concat(doc.Workflows[scenario.Workflow!].Finally)).Single(s => s.Id == scenario.Step);
                if (target.If is not null) target.If = scenario.Kind == "guard_false" ? "${false}" : "${true}";
            }
            if (scenario.Kind == "branch")
            {
                var target = Enumerate(doc.Workflows[scenario.Workflow!].Steps).Concat(Enumerate(doc.Workflows[scenario.Workflow!].Finally)).Single(s => s.Id == scenario.Step);
                target.Expr = null;
                for (var index = 0; index < (target.Cases?.Count ?? 0); index++)
                {
                    target.Cases![index].Value = null;
                    target.Cases[index].When = index == scenario.CaseIndex ? "${true}" : "${false}";
                }
            }
            var engine = new WorkflowEngine
            {
                McpClientFactory = fakeFactory,
                LLMClient = new ScenarioLlm(),
                HumanInputProvider = new ScenarioHuman(),
                Telemetry = telemetry,
                Limits = new ExecutionLimits { MaxTotalStepsExecuted = 1000, MaxLoopIterations = 10, MaxCallDepth = 10, MaxParallelBranches = 10, LogStepContent = false, RunId = "planning-scenario" }
            };
            var fault = new Injection();
            engine.Registry.Register(new FailureExecutor(new McpCallExecutor(), scenario, cancellation, fault));
            engine.Registry.Register(new FailureExecutor(new LlmCallExecutor(), scenario, cancellation, fault));
            var diagnostics = new List<PlanningDiagnostic>();
            string outcome;
            try
            {
                var compiled = new WorkflowCompiler().Compile(doc);
                var main = compiled.Workflows[compiled.Entrypoint!];
                var inputs = new JsonObject();
                foreach (var (name, input) in main.Source.Inputs ?? [])
                    inputs[name] = Sample(input);
                var run = await engine.ExecuteAsync(main, inputs, cancellation.Token);
                var reached = scenario.Step is null || fault.Injected || telemetry.Statuses.ContainsKey(scenario.Workflow + ":" + scenario.Step);
                var expectedFailure = fault.Injected && (run.Success || run.Error?.Code is "SCENARIO_INJECTED_FAILURE" or "CANCELLED");
                outcome = reached && (run.Success || expectedFailure) ? "passed" : "inconclusive";
                if (!reached) diagnostics.Add(new("SCENARIO_UNREACHED", scenario.Step!, "The synthetic input did not reach this required scenario."));
                else if (!run.Success && !expectedFailure) diagnostics.Add(new("SCENARIO_INCONCLUSIVE", run.Error?.Code ?? "$", "Synthetic execution did not establish successful behavior."));
                foreach (var visitedWorkflow in telemetry.Workflows)
                {
                    if (!doc.Workflows.TryGetValue(visitedWorkflow, out var wf)) continue;
                    foreach (var finalizer in wf.Finally.Where(s => s.If is null))
                        if (!telemetry.Statuses.TryGetValue(visitedWorkflow + ":" + finalizer.Id, out var status) || status != StepStatus.Succeeded)
                        {
                            diagnostics.Add(new("FINALIZATION_NOT_EXECUTED", finalizer.Id, "An unconditional finalizer did not complete successfully."));
                            outcome = "failed";
                        }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                outcome = "inconclusive";
                diagnostics.Add(new("SCENARIO_INCONCLUSIVE", scenario.Step ?? "$", "Synthetic execution could not establish this scenario."));
            }
            results.Add(new(scenario.Id, outcome, "Synthetic " + scenario.Kind + " coverage; does not execute external effects.", diagnostics));
        }
        return results;
    }

    private static JsonNode? Sample(InputDef input)
    {
        if (input.Default is not null) return InputDefaultValueConverter.ConvertToNode(input.Default, input);
        if (input.Enum is { Count: > 0 }) return JsonValue.Create(input.Enum[0]);
        return input.Type switch
        {
            "boolean" => JsonValue.Create(true),
            "number" or "integer" => JsonValue.Create(1),
            "array" => new JsonArray(Sample(input.Items ?? new InputDef { Type = "string" })),
            "object" => new JsonObject((input.Properties ?? []).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Sample(p.Value)))),
            _ => JsonValue.Create("sample")
        };
    }

    private static IEnumerable<StepDef> Enumerate(IEnumerable<StepDef> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in Enumerate((step.Steps ?? []).Concat(step.Default ?? []).Concat((step.Cases ?? []).SelectMany(c => c.Steps)).Concat((step.Branches ?? []).SelectMany(b => b.Steps)))) yield return child;
        }
    }

    private sealed record Scenario(string Id, string? Workflow, string? Step, string Kind, int CaseIndex = -1);
    private sealed class Injection { public bool Injected { get; set; } }
    private sealed class FailureExecutor(IStepExecutor inner, Scenario scenario, CancellationTokenSource cancellation, Injection fault) : IStepExecutor
    {
        public string StepType => inner.StepType;
        public string? DslSnippet => inner.DslSnippet;
        public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
        {
            if (ctx.Step.Id == scenario.Step && ctx.ExecutionScope?.Workflow?.Name == scenario.Workflow)
            {
                if (scenario.Kind == "cancellation") { fault.Injected = true; cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
                if (scenario.Kind == "failure") { fault.Injected = true; throw new WorkflowRuntimeException("SCENARIO_INJECTED_FAILURE", "Synthetic integration failure."); }
            }
            return inner.ExecuteAsync(ctx, ct);
        }
    }
    private sealed class ScenarioLlm : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var json = request.StructuredOutputSchema is null ? null : WorkflowPlanDryRunValidator.CreateSampleFromJsonSchema(request.StructuredOutputSchema);
            return Task.FromResult(new LLMResponse { Text = json?.ToJsonString() ?? "sample", Json = json });
        }
    }
    private sealed class ScenarioHuman : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Fields is { Count: > 0 })
                return Task.FromResult<JsonNode?>(new JsonObject(request.Fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, JsonValue.Create(f.Options?.FirstOrDefault() ?? f.Default ?? "sample")))));
            return Task.FromResult<JsonNode?>(new JsonObject { ["response"] = request.Mode == HumanInputContract.ModeConfirm ? JsonValue.Create(true) : JsonValue.Create(request.Choices?.FirstOrDefault() ?? "sample") });
        }
    }
    private sealed class CoverageTelemetry : IWorkflowTelemetry
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, StepStatus> Statuses { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Workflows { get; } = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info) { lock (_gate) Workflows.Add(info.WorkflowName); return new CoverageSpan(info.WorkflowName); }
        public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result) { }
        public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info) => new CoverageSpan(((CoverageSpan)parentSpan).Workflow, info.StepId);
        public void StepEnd(IStepSpan span, StepResultInfo result) { var step = (CoverageSpan)span; Statuses[step.Workflow + ":" + step.Step] = result.Status; }
    }
    private sealed class CoverageSpan(string workflow, string? step = null) : IWorkflowSpan, IStepSpan
    {
        public string Workflow { get; } = workflow;
        public string? Step { get; } = step;
        public void Dispose() { }
    }

    private static void ForcePath(WorkflowDocument doc, string workflow, string target, HashSet<string> visited)
    {
        if (!visited.Add(workflow)) return;
        ForceIn(doc.Workflows[workflow].Steps.Concat(doc.Workflows[workflow].Finally), target);
        foreach (var (callerName, caller) in doc.Workflows)
            foreach (var call in Enumerate(caller.Steps.Concat(caller.Finally)).Where(s => s.Type == "workflow.call" && s.Input?["ref"]?["name"]?.GetValue<string>() == workflow))
                ForcePath(doc, callerName, call.Id, visited);
    }

    private static bool ForceIn(IEnumerable<StepDef> nodes, string target)
    {
        foreach (var node in nodes)
        {
            if (node.Id == target) return true;
            var found = ForceIn(node.Steps ?? [], target) || (node.Branches ?? []).Any(b => ForceIn(b.Steps, target));
            for (var i = 0; i < (node.Cases?.Count ?? 0); i++)
                if (ForceIn(node.Cases![i].Steps, target))
                {
                    node.Expr = null;
                    for (var j = 0; j < node.Cases.Count; j++) { node.Cases[j].Value = null; node.Cases[j].When = i == j ? "${true}" : "${false}"; }
                    found = true;
                    break;
                }
            if (ForceIn(node.Default ?? [], target))
            {
                node.Expr = null;
                foreach (var branch in node.Cases ?? []) { branch.Value = null; branch.When = "${false}"; }
                found = true;
            }
            if (found) { if (node.If is not null) node.If = "${true}"; return true; }
        }
        return false;
    }
}
