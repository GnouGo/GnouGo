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
    public static async Task<IReadOnlyList<PlanningScenarioResult>> ValidateAsync(WorkflowDocument document, IMcpClientFactory? fakeFactory, CancellationToken ct, JsonObject? validationInputs = null, JsonObject? loopItemSchemas = null, JsonObject? observations = null)
    {
        var definitions = new List<Scenario> { new("nominal", null, null, "normal") };
        var observationLoops = new Dictionary<string, (string Workflow, StepDef Loop)>(StringComparer.Ordinal);
        foreach (var (key, _) in observations ?? [])
        {
            var separator = key.IndexOf(':');
            if (separator < 1 || !document.Workflows.TryGetValue(key[..separator], out var workflow)) continue;
            var owner = Enumerate(workflow.Steps.Concat(workflow.Finally)).LastOrDefault(s => s.Type == "loop.sequential" &&
                Enumerate(s.Steps ?? []).Any(child => child.Id == key[(separator + 1)..]));
            if (owner is null) continue;
            observationLoops[key] = (key[..separator], owner);
        }
        foreach (var group in observationLoops.Values.DistinctBy(v => (v.Workflow, v.Loop.Id)))
            definitions.Add(new("observations:" + group.Workflow + ":" + group.Loop.Id, group.Workflow, group.Loop.Id, "observations"));
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
                ForcePath(doc, scenario.Workflow!, scenario.Step, new HashSet<string>(StringComparer.Ordinal), loopItemSchemas);
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
                LlmDefaults = new() { Model = "planning-scenario" },
                HumanInputProvider = new ScenarioHuman(),
                Telemetry = telemetry,
                Limits = new ExecutionLimits { MaxTotalStepsExecuted = 1000, MaxLoopIterations = 10, MaxCallDepth = 10, MaxParallelBranches = 10, LogStepContent = false, RunId = "planning-scenario" }
            };
            var fault = new Injection();
            var observed = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            engine.Registry.Register(new FailureExecutor(new McpCallExecutor(), scenario, cancellation, fault, observations, observed, telemetry));
            engine.Registry.Register(new FailureExecutor(new LlmCallExecutor(), scenario, cancellation, fault, observations, observed, telemetry));
            var diagnostics = new List<PlanningDiagnostic>();
            string outcome;
            try
            {
                var compiled = new WorkflowCompiler().Compile(doc);
                var main = compiled.Workflows[compiled.Entrypoint!];
                var inputs = validationInputs?.DeepClone().AsObject() ?? new JsonObject();
                if (validationInputs is null)
                    foreach (var (name, input) in main.Source.Inputs ?? []) inputs[name] = Sample(input);
                var run = await engine.ExecuteAsync(main, inputs, cancellation.Token);
                if (scenario.Kind is "normal" or "observations")
                {
                    diagnostics.AddRange(telemetry.RecoveredErrors.Values);
                    foreach (var (key, fixture) in observations ?? [])
                    {
                        var hasOwner = observationLoops.TryGetValue(key, out var owner);
                        var required = scenario.Kind == "observations" ? hasOwner && owner.Workflow == scenario.Workflow && owner.Loop.Id == scenario.Step
                            : !hasOwner || telemetry.Statuses.TryGetValue(owner.Workflow + ":" + owner.Loop.Id, out var loopStatus) && loopStatus != StepStatus.Skipped;
                        if (required && fixture?["responses"] is JsonArray samples && observed.GetValueOrDefault(key) != samples.Count)
                            diagnostics.Add(new("SCENARIO_OBSERVATIONS_UNCONSUMED", "workflow:" + key.Replace(":", "/step:", StringComparison.Ordinal), "Execution of the observation-driven loop did not consume its declared sequence; early termination is not successful coverage."));
                    }
                }
                var reached = scenario.Step is null || fault.Injected || telemetry.Statuses.ContainsKey(scenario.Workflow + ":" + scenario.Step);
                var expectedFailure = fault.Injected && (run.Success || run.Error?.Code is "SCENARIO_INJECTED_FAILURE" or "CANCELLED");
                outcome = reached && (run.Success || expectedFailure) ? "passed" : "inconclusive";
                if (diagnostics.Count != 0) outcome = "inconclusive";
                if (!reached) diagnostics.Add(new("SCENARIO_UNREACHED", "workflow:" + scenario.Workflow + "/step:" + scenario.Step, "The synthetic input did not reach this required scenario."));
                if (!run.Success && !expectedFailure)
                {
                    diagnostics.AddRange(telemetry.Failures.Where(p => !telemetry.RequestFailures.ContainsKey(p.Key)).Select(p => p.Value));
                    diagnostics.AddRange(telemetry.RequestFailures.Values.SelectMany(p => p));
                    if (telemetry.Failures.IsEmpty) diagnostics.Add(new("SCENARIO_INCONCLUSIVE", "$", run.Error?.Code + ": " + run.Error?.Message));
                }
                foreach (var visitedWorkflow in telemetry.Workflows)
                {
                    if (!doc.Workflows.TryGetValue(visitedWorkflow, out var wf)) continue;
                    foreach (var finalizer in wf.Finally.Where(s => s.If is null))
                        if (!telemetry.Statuses.TryGetValue(visitedWorkflow + ":" + finalizer.Id, out var status) || status != StepStatus.Succeeded)
                        {
                            diagnostics.Add(new("FINALIZATION_NOT_EXECUTED", "workflow:" + visitedWorkflow + "/step:" + finalizer.Id, "An unconditional finalizer did not complete successfully."));
                            outcome = "failed";
                        }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                outcome = "inconclusive";
                diagnostics.Add(new("SCENARIO_INCONCLUSIVE", scenario.Step is null ? "$" : "workflow:" + scenario.Workflow + "/step:" + scenario.Step, "Synthetic execution could not establish this scenario: " + ex.Message));
            }
            results.Add(new(scenario.Id, outcome, "Synthetic " + scenario.Kind + " coverage with explicit path fixtures; does not establish live external behavior.", diagnostics));
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
    private sealed class FailureExecutor(IStepExecutor inner, Scenario scenario, CancellationTokenSource cancellation, Injection fault, JsonObject? observations, System.Collections.Concurrent.ConcurrentDictionary<string, int> observed, CoverageTelemetry telemetry) : IStepExecutor
    {
        public string StepType => inner.StepType;
        public string? DslSnippet => inner.DslSnippet;
        public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
        {
            if (ctx.Step.Id == scenario.Step && ctx.ExecutionScope?.Workflow?.Name == scenario.Workflow)
            {
                if (scenario.Kind == "cancellation") { fault.Injected = true; cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
                if (scenario.Kind == "failure") { fault.Injected = true; throw new WorkflowRuntimeException("SCENARIO_INJECTED_FAILURE", "Synthetic integration failure."); }
            }
            var key = ctx.ExecutionScope?.Workflow?.Name + ":" + ctx.Step.Id;
            if (observations?[key] is JsonObject fixture)
            {
                var index = observed.GetValueOrDefault(key);
                if (fixture["responses"] is not JsonArray samples || index >= samples.Count)
                    throw new WorkflowRuntimeException("SCENARIO_OBSERVATIONS_EXHAUSTED", "Execution requested another observation after the explicit terminal fixture; check the loop continuation or provide a valid longer fixture.");
                if (fixture["schema"] is not JsonObject schema || PlanningContractValidation.ValidateInstance(samples[index], schema).Count != 0)
                    throw new WorkflowRuntimeException("SCENARIO_OBSERVATION_INVALID", "The synthetic observation does not satisfy its declared producer contract.");
                // Fixtures replace observations, not executable request validation. The inner
                // executor uses the scenario's fake integrations and must evaluate arguments,
                // validate native contracts, and complete before this sample is consumed.
                await ExecuteInnerAsync(ctx, ct).ConfigureAwait(false);
                observed.AddOrUpdate(key, 1, (_, current) => current + 1);
                return samples[index]?.DeepClone();
            }
            return await ExecuteInnerAsync(ctx, ct).ConfigureAwait(false);
        }

        private async Task<JsonNode?> ExecuteInnerAsync(StepExecutionContext ctx, CancellationToken ct)
        {
            try { return await inner.ExecuteAsync(ctx, ct).ConfigureAwait(false); }
            catch (WorkflowRuntimeException ex) when (ex.Code == ErrorCodes.InputValidation && ex.Details?["validation_findings"] is JsonArray findings)
            {
                var workflow = ctx.ExecutionScope?.Workflow?.Name;
                telemetry.RequestFailures[workflow + ":" + ctx.Step.Id] = findings.Select(f => new PlanningDiagnostic(
                    "SCENARIO_EXECUTION_FAILED", "workflow:" + workflow + "/step:" + ctx.Step.Id + "/input/request" + f!["instance_pointer"]!.GetValue<string>(),
                    ex.Code + ": " + f["message"]!.GetValue<string>())).ToArray();
                throw;
            }
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
        public System.Collections.Concurrent.ConcurrentDictionary<string, PlanningDiagnostic> Failures { get; } = new(StringComparer.Ordinal);
        public System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<PlanningDiagnostic>> RequestFailures { get; } = new(StringComparer.Ordinal);
        public System.Collections.Concurrent.ConcurrentDictionary<string, PlanningDiagnostic> RecoveredErrors { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Workflows { get; } = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info) { lock (_gate) Workflows.Add(info.WorkflowName); return new CoverageSpan(info.WorkflowName); }
        public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result) { }
        public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info) => new CoverageSpan(((CoverageSpan)parentSpan).Workflow, info.StepId);
        public void StepEnd(IStepSpan span, StepResultInfo result)
        {
            var step = (CoverageSpan)span; var key = step.Workflow + ":" + step.Step; Statuses[key] = result.Status;
            if (result.Status == StepStatus.Failed) Failures[key] = new("SCENARIO_EXECUTION_FAILED", "workflow:" + step.Workflow + "/step:" + step.Step, result.ErrorCode + ": " + result.ErrorMessage);
            else if (result.ErrorCode is not null) RecoveredErrors[key] = new("SCENARIO_RECOVERED_ERROR", "workflow:" + step.Workflow + "/step:" + step.Step,
                "Nominal execution required error recovery: " + result.ErrorCode + ": " + result.ErrorMessage + ". A successful fallback does not establish the normal path.");
        }
    }
    private sealed class CoverageSpan(string workflow, string? step = null) : IWorkflowSpan, IStepSpan
    {
        public string Workflow { get; } = workflow;
        public string? Step { get; } = step;
        public void Dispose() { }
    }

    private static void ForcePath(WorkflowDocument doc, string workflow, string target, HashSet<string> visited, JsonObject? loopItemSchemas)
    {
        if (!visited.Add(workflow)) return;
        ForceIn(doc.Workflows[workflow].Steps.Concat(doc.Workflows[workflow].Finally), target, workflow, loopItemSchemas);
        foreach (var (callerName, caller) in doc.Workflows)
            foreach (var call in Enumerate(caller.Steps.Concat(caller.Finally)).Where(s => s.Type == "workflow.call" && s.Input?["ref"]?["name"]?.GetValue<string>() == workflow))
                ForcePath(doc, callerName, call.Id, visited, loopItemSchemas);
    }

    private static bool ForceIn(IEnumerable<StepDef> nodes, string target, string workflow, JsonObject? loopItemSchemas)
    {
        foreach (var node in nodes)
        {
            if (node.Id == target) return true;
            var found = ForceIn(node.Steps ?? [], target, workflow, loopItemSchemas) || (node.Branches ?? []).Any(b => ForceIn(b.Steps, target, workflow, loopItemSchemas));
            for (var i = 0; i < (node.Cases?.Count ?? 0); i++)
                if (ForceIn(node.Cases![i].Steps, target, workflow, loopItemSchemas))
                {
                    node.Expr = null;
                    for (var j = 0; j < node.Cases.Count; j++) { node.Cases[j].Value = null; node.Cases[j].When = i == j ? "${true}" : "${false}"; }
                    found = true;
                    break;
                }
            if (ForceIn(node.Default ?? [], target, workflow, loopItemSchemas))
            {
                node.Expr = null;
                foreach (var branch in node.Cases ?? []) { branch.Value = null; branch.When = "${false}"; }
                found = true;
            }
            if (found)
            {
                if (node.If is not null) node.If = "${true}";
                // Like forced branch outcomes, loop entry is explicit synthetic path
                // coverage. Never change the original artifact or its nominal execution.
                if (node.Type == "loop.sequential" && node.Input is JsonObject loop)
                {
                    if ((loop.ContainsKey("items") || loop.ContainsKey("over")) && loopItemSchemas?[workflow + ":" + node.Id] is JsonObject schema)
                    {
                        var item = WorkflowPlanDryRunValidator.CreateSampleFromJsonSchema(schema);
                        if (PlanningContractValidation.ValidateInstance(item, schema).Count == 0)
                        {
                            loop.Remove("over"); loop.Remove("times"); loop.Remove("while");
                            loop["items"] = new JsonArray(item); loop["max_times"] = 1;
                        }
                    }
                    else if (!loop.ContainsKey("items") && !loop.ContainsKey("over"))
                    { loop.Remove("while"); loop["times"] = 1; loop["max_times"] = 1; }
                }
                return true;
            }
        }
        return false;
    }
}
