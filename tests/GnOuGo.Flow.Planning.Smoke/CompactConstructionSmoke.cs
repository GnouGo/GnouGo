using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

internal static class CompactConstructionSmoke
{
    private const string Objective = "Return exactly Hello as message.";

    internal static async Task RunAsync()
    {
        var planner = new HybridWorkflowPlanner();
        var runtime = new Runtime();
        var state = new PlanningSession { Request = new() { TenantId = "compact-smoke", Prompt = Objective } };
        for (var advance = 0; advance < 24 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); advance++)
        {
            var previousCalls = runtime.PhysicalCalls;
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, CancellationToken.None);
            if (runtime.PhysicalCalls > previousCalls && runtime.BeforeDispatch is { } saved)
            {
                // Rehydrate the exact pending request separately from its receipt
                // at every model phase, including intent and step selection.
                var interrupted = JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSession)!;
                var recoveredRuntime = new Runtime();
                foreach (var (id, receipt) in runtime.Receipts)
                    recoveredRuntime.Receipts.Add(id, JsonSerializer.Deserialize(JsonSerializer.Serialize(receipt,
                        PlanningJsonContext.Default.LLMResponse), PlanningJsonContext.Default.LLMResponse)!);
                var recovered = await planner.AdvanceAsync(interrupted, new() { ExpectedRevision = interrupted.Revision }, recoveredRuntime, CancellationToken.None);
                if (recoveredRuntime.PhysicalCalls != 0 || recovered.ModelCalls != state.ModelCalls || recovered.Status != state.Status ||
                    recovered.Phase != state.Phase || recovered.Diagnostics.Count != state.Diagnostics.Count ||
                    PlanningConstruction.Hash(recovered.Plan) != PlanningConstruction.Hash(state.Plan) ||
                    recovered.Construction?.Checkpoints.Count != state.Construction?.Checkpoints.Count)
                    throw new InvalidOperationException("Compact phase recovery changed accepted work or dispatched a duplicate call.");
            }
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        }
        if (state.Status != PlanningStatus.FinalReview || state.Construction is not { Version: 2, Complete: true, Candidate: null } ||
            state.Construction.Compact is null || state.Construction.Requirements.Any(r => r.Status != "closed") ||
            state.ApprovedHash is not null || state.Yaml is null)
            throw new InvalidOperationException("Compact construction did not complete with explicit requirement closure: " +
                state.Status + "; " + JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        foreach (var phase in new[] { "construction_intent", "construction_select", "construction_draft", "construction_local_judge" })
            if (!runtime.Purposes.Contains(phase, StringComparer.Ordinal)) throw new InvalidOperationException("Missing compact model phase: " + phase);
        if (state.RequestMeasurements is null || state.RequestMeasurements.Count != runtime.PhysicalCalls ||
            state.RequestMeasurements.Any(m => m.EstimatedInputTokens > m.InputLimit || m.Components.Sum(c => c.SerializedBytes) != m.CompleteRequestBytes))
            throw new InvalidOperationException("Compact request measurements did not survive source-generated recovery.");
        PlanningArtifactApproval.Verify(state);
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml)).Workflows["main"];
        var result = await new WorkflowEngine().ExecuteAsync(workflow, new JsonObject(), CancellationToken.None);
        if (!result.Success || result.Outputs?["message"]?.GetValue<string>() != "Hello")
            throw new InvalidOperationException("Compact v15 artifact changed the independently expected value.");
        Console.WriteLine("compact construction: indexed originals, selected step, narrow draft/Judge, exact phase recovery and independently executed output");
    }

    private sealed class Runtime : IPlanningRuntime
    {
        private readonly WorkflowPlanningRuntime _inner = new(new(), (_, _) => Task.CompletedTask);
        internal Dictionary<string, LLMResponse> Receipts { get; } = new(StringComparer.Ordinal);
        internal List<string> Purposes { get; } = [];
        internal string? BeforeDispatch;
        internal int PhysicalCalls;
        public ICapabilityCatalog Capabilities => _inner.Capabilities;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => _inner.DiscoverAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => _inner.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => _inner.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession state, CancellationToken ct)
        {
            if (state.PendingCall is not null) BeforeDispatch = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
            return Task.CompletedTask;
        }
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            if (Receipts.TryGetValue(request.ClientRequestId!, out var retained)) return Task.FromResult(retained);
            PhysicalCalls++; Purposes.Add(purpose);
            var context = JsonNode.Parse(request.Prompt![(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
            JsonObject response;
            switch (purpose)
            {
                case "construction_intent":
                    response = new()
                    {
                        ["sourceId"] = Issued(request, "sourceId"),
                        ["clauses"] = new JsonArray(new JsonObject { ["text"] = Objective, ["kind"] = "requirement" }),
                        ["inputs"] = new JsonArray(),
                        ["outputs"] = new JsonArray(new JsonObject { ["name"] = "message", ["required"] = true,
                            ["type"] = new JsonObject { ["kind"] = "string", ["nullable"] = false } })
                    };
                    break;
                case "construction_select":
                    var requirement = context["requirements"]![0]!["id"]!.ToString();
                    response = new()
                    {
                        ["baseHash"] = Issued(request, "baseHash"), ["evidenceRequests"] = null, ["clarifications"] = null,
                        ["step"] = new JsonObject
                        {
                            ["scope"] = "/root", ["kind"] = "value", ["objective"] = Objective,
                            ["requirementIds"] = new JsonArray(requirement), ["inputs"] = new JsonArray(),
                            ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray("The exact requested greeting"),
                            ["closeRequirementIds"] = new JsonArray(requirement), ["outputNames"] = new JsonArray("message")
                        }
                    };
                    break;
                case "construction_draft":
                    var requirementId = context["step"]!["requirementIds"]![0]!.ToString();
                    var extension = new PlanningConstructionExtension
                    {
                        Tasks = [new() { Id = "greeting", Kind = "value", Objective = Objective,
                            Outputs = [new("message", new() { Kind = "string", Text = "Hello" })] }],
                        Outputs = [new("message", new() { Kind = "output", Source = "greeting", Port = "message" })],
                        CloseScopes = ["/root"], RequirementIds = [requirementId], Complete = true
                    };
                    response = new()
                    {
                        ["baseHash"] = Issued(request, "baseHash"), ["requirements"] = null,
                        ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension),
                        ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
                        ["rollbackTask"] = null, ["rollbackReason"] = null
                    };
                    break;
                case "construction_local_judge":
                case "construction_intermediate_judge":
                    response = new() { ["findings"] = new JsonArray(), ["uncertainty"] = "Execution remains unverified.", ["evidenceRequests"] = null };
                    break;
                default: throw new InvalidOperationException("Unexpected compact phase: " + purpose);
            }
            var result = new LLMResponse { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
            Receipts.Add(request.ClientRequestId!, result);
            return Task.FromResult(result);
        }

        private static JsonNode Issued(LLMRequest request, string name)
        {
            var schema = request.StructuredOutputSchema!;
            var value = schema["properties"]![name]!;
            while (value["$ref"]?.ToString() is { } reference) value = schema["$defs"]![reference[8..]]!;
            return value["enum"]![0]!.DeepClone();
        }
    }
}
