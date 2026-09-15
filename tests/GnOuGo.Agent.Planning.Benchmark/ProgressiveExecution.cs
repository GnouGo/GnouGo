using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static class ProgressiveExecution
{
    internal static async Task CheckReferenceAsync(JsonObject evidence, CancellationToken ct)
    {
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(evidence["referenceYaml"]!.ToString()));
        var classifier = compiled.Workflows.Values.Single(w => w.Source.Inputs?.ContainsKey("record") == true);
        var batch = compiled.Workflows.Values.Single(w => w.Source.Inputs?.ContainsKey("records") == true);
        var checkedCases = 0;
        foreach (var name in new[] { "accepted", "rejected", "boundary", "omitted_default", "mixed", "empty" })
        {
            var data = Dataset(name);
            foreach (var record in data)
            {
                var result = await new WorkflowEngine().ExecuteAsync(classifier, new JsonObject { ["record"] = record!.DeepClone(), ["threshold"] = 100 }, ct);
                if (!result.Success || !JsonNode.DeepEquals(result.Outputs, new JsonObject { ["classifiedResult"] = Classify(record) })) throw new InvalidOperationException("The independent classifier oracle disagrees with the retained approved artifact.");
            }
            var input = new JsonObject { ["records"] = data.DeepClone() };
            if (name != "omitted_default") input["threshold"] = 100;
            var observed = new Observations();
            var batchResult = await new WorkflowEngine { Telemetry = observed }.ExecuteAsync(batch, input, ct);
            if (!batchResult.Success || !JsonNode.DeepEquals(batchResult.Outputs, new JsonObject { ["summary"] = Summary(data) }) ||
                observed.Steps.Count(s => s.StepId == batch.Finally.Single().Id && s.CallDepth == 0) != 1)
                throw new InvalidOperationException("The independent aggregation/default/finalizer oracle disagrees with the retained approved artifact.");
            checkedCases++;
        }
        Console.WriteLine($"Verified {checkedCases} classifier/batch reference cases; no planning or model calls.");
    }

    internal static async Task RunAsync(PlanningSnapshot state, int stage, JsonArray discovery, IKeyVaultRecordStore records, CancellationToken ct)
    {
        var yaml = new PlanningGraphCompiler().Compile(state.Graph!, state.Preparation!);
        if (state.Status != PlanningStatus.FinalReview || yaml != state.Yaml || PlanningGraphCompiler.Fingerprint(yaml) != state.ArtifactHash ||
            yaml != new PlanningGraphCompiler().Compile(state.Graph!, state.Preparation!)) throw new InvalidOperationException("The execution artifact differs from deterministic lowering.");
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var workflow = compiled.Workflows[compiled.Entrypoint!];
        var fixtureHash = ProgressiveScenarios.FixtureHash(stage); var catalogHash = PlanningGraphCompiler.Fingerprint(discovery.ToJsonString());
        var reports = new JsonArray();
        foreach (var name in ProgressiveScenarios.Cases(stage))
        {
            var report = new JsonObject { ["case"] = name, ["artifactHash"] = state.ArtifactHash, ["fixtureHash"] = fixtureHash, ["catalogHash"] = catalogHash, ["passed"] = false };
            var owner = state.Request.SessionId + ":execution:" + state.ArtifactHash + ":" + fixtureHash + ":" + catalogHash + ":" + name;
            try
            {
                var data = Dataset(name); var reads = 0; var observed = new Observations();
                var input = stage == 1 ? new JsonObject { ["record"] = data[0]!.DeepClone() } : new JsonObject { ["batchId"] = name };
                if (name != "omitted_default") input["threshold"] = name == "null_threshold" ? null : JsonValue.Create(100);
                if (name == "invalid_input") input[stage == 1 ? "record" : "batchId"] = 42;
                var engine = new WorkflowEngine
                {
                    Telemetry = observed,
                    Limits = new() { LogStepContent = false, TenantId = state.Request.TenantId, RunId = owner },
                    McpClientFactory = new FrozenCatalog(discovery, (server, method, arguments) =>
                    {
                        if (server != "ProgressiveFixture" || method != "load_record_batch" || arguments?["batchId"]?.ToString() != name) throw new InvalidOperationException("Unconfigured fixture invocation.");
                        reads++;
                        if (name == "read_failure") throw new WorkflowRuntimeException("FIXTURE_READ_FAILURE", "Injected read failure");
                        return new McpCallResult { Content = new JsonObject { ["records"] = data.DeepClone() } };
                    }),
                    WorkflowCallResolver = name == "callee_failure" ? new FailedCallee() : new DefaultWorkflowCallResolver()
                };
                RunResult result;
                try { result = await engine.ExecuteAsync(workflow, input, ct); }
                catch (WorkflowRuntimeException e) { result = new() { Success = false, Error = new() { Code = e.Code, Message = e.Message } }; }
                var invalid = name is "invalid_input" or "null_threshold";
                var injected = name is "read_failure" or "callee_failure";
                report["success"] = result.Success; report["errorCode"] = result.Error?.Code;
                report["readCalls"] = reads;
                var finalizer = workflow.Finally.SingleOrDefault();
                var finalizerRuns = finalizer is null ? 0 : observed.Steps.Count(s => s.StepId == finalizer.Id && s.CallDepth == 0);
                report["finalizerRuns"] = finalizerRuns;
                if (stage == 2 && (finalizer is null || finalizer.Type != "set" || finalizerRuns != (invalid ? 0 : 1))) throw new InvalidOperationException("The native finalizer did not execute exactly once after execution began.");
                if (invalid)
                {
                    if (result.Success || result.Error?.Code != ErrorCodes.InputValidation || reads != 0) throw new InvalidOperationException("Invalid public input was not rejected before effects.");
                }
                else if (injected)
                {
                    var expected = name == "read_failure" ? "FIXTURE_READ_FAILURE" : "FIXTURE_CALLEE_FAILURE";
                    if (result.Success || result.Error?.Code != expected || reads != 1) throw new InvalidOperationException("The original injected failure was not preserved.");
                }
                else
                {
                    if (!result.Success) throw new InvalidOperationException("Independent execution failed: " + result.Error?.Code);
                    var expected = stage == 1 ? new JsonObject { ["classifiedResult"] = Classify(data[0]!) } : new JsonObject { ["summary"] = Summary(data) };
                    if (!JsonNode.DeepEquals(result.Outputs, expected)) throw new InvalidOperationException("Public results differ from the independent classification oracle.");
                    if (stage == 2)
                    {
                        if (reads != 1 || observed.Classifications.Count != data.Count || observed.Aggregations.Count != 1) throw new InvalidOperationException("Incorrect read/callee invocation cardinality.");
                        for (var i = 0; i < data.Count; i++) if (!JsonNode.DeepEquals(data[i], observed.Classifications[i])) throw new InvalidOperationException("The original record or its order was lost across a call boundary.");
                        if (!JsonNode.DeepEquals(new JsonArray(data.Select(Classify).ToArray()), observed.Aggregations[0])) throw new InvalidOperationException("Aggregation did not consume the validated classifier results.");
                    }
                }
                report["passed"] = true;
                await records.UpsertAsync("agent-planning-benchmark-execution-v5", state.Request.TenantId, owner, report.ToJsonString(), EfPlanningSessionStore.Author, ct);
                reports.Add(report); Console.WriteLine(report.ToJsonString());
            }
            catch (Exception error)
            {
                report["failureType"] = error.GetType().Name;
                await records.UpsertAsync("agent-planning-benchmark-execution-v5", state.Request.TenantId, owner, new JsonObject { ["report"] = report, ["privateFailure"] = error.ToString() }.ToJsonString(), EfPlanningSessionStore.Author, CancellationToken.None);
                throw;
            }
        }
        await records.UpsertAsync("agent-planning-benchmark-validation-v5", state.Request.TenantId, state.Request.SessionId + ":" + state.ArtifactHash, reports.ToJsonString(), EfPlanningSessionStore.Author, ct);
    }
    internal static JsonArray Dataset(string name) => JsonNode.Parse(name switch
    {
        "empty" => "[]",
        "rejected" => """[{"id":"r","amount":900,"approved":false}]""",
        "boundary" => """[{"id":"edge","amount":100,"approved":true}]""",
        "omitted_default" => """[{"id":"below","amount":99,"approved":true},{"id":"edge","amount":100,"approved":true}]""",
        _ => """[{"id":"first","amount":40,"approved":true},{"id":"high","amount":150,"approved":true},{"id":"rejected","amount":900,"approved":false}]"""
    })!.AsArray();
    internal static JsonNode Classify(JsonNode? record) => new JsonObject { ["id"] = record!["id"]!.DeepClone(), ["amount"] = record["amount"]!.DeepClone(),
        ["category"] = !record["approved"]!.GetValue<bool>() ? "rejected" : record["amount"]!.GetValue<decimal>() >= 100 ? "high" : "standard" };
    internal static JsonNode Summary(JsonArray data) => new JsonObject { ["totalCount"] = data.Count,
        ["highCount"] = data.Count(r => r!["approved"]!.GetValue<bool>() && r["amount"]!.GetValue<decimal>() >= 100),
        ["rejectedCount"] = data.Count(r => !r!["approved"]!.GetValue<bool>()),
        ["totalAcceptedAmount"] = data.Where(r => r!["approved"]!.GetValue<bool>()).Sum(r => r!["amount"]!.GetValue<decimal>()) };
    private sealed class FailedCallee : DefaultWorkflowCallResolver
    {
        public override Task<WorkflowCallResolution> ResolveAsync(WorkflowCallResolutionContext context, CancellationToken ct)
            => throw new WorkflowRuntimeException("FIXTURE_CALLEE_FAILURE", "Injected callee failure");
    }
    private sealed class Observations : IWorkflowTelemetry
    {
        internal List<StepTelemetryInfo> Steps { get; } = [];
        internal List<JsonNode?> Classifications { get; } = [];
        internal List<JsonNode?> Aggregations { get; } = [];
        public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info)
        {
            if (info.Inputs is JsonObject input)
            {
                if (input["record"] is { } record) Classifications.Add(record.DeepClone());
                if (input["classifiedResults"] is { } values) Aggregations.Add(values.DeepClone());
            }
            return NullTelemetrySpan.Instance;
        }
        public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info) { Steps.Add(info); return NullTelemetrySpan.Instance; }
        public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result) { }
        public void StepEnd(IStepSpan span, StepResultInfo result) { }
    }
}
