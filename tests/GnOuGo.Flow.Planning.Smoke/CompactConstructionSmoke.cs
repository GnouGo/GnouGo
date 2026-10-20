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
    private const string ResultRequirement = "Return exactly Hello as message. ";
    private const string InterfaceRequirement = "The public output message is a required string.";
    private const string Objective = ResultRequirement + InterfaceRequirement;

    internal static async Task RunAsync()
    {
        var planner = new HybridWorkflowPlanner();
        var runtime = new Runtime();
        var recoveredIntentActions = new HashSet<string>(StringComparer.Ordinal);
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
                var issued = JsonSerializer.Serialize(interrupted.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
                if (interrupted.PendingCall.Purpose == "construction_intent")
                    recoveredIntentActions.Add(interrupted.PendingCall.Request.StructuredOutputSchema!["properties"]!["clauses"] is null ? "interfaces" : "classification");
                var recoveredRuntime = new Runtime();
                foreach (var (id, receipt) in runtime.Receipts)
                    recoveredRuntime.Receipts.Add(id, JsonSerializer.Deserialize(JsonSerializer.Serialize(receipt,
                        PlanningJsonContext.Default.LLMResponse), PlanningJsonContext.Default.LLMResponse)!);
                var recovered = await planner.AdvanceAsync(interrupted, new() { ExpectedRevision = interrupted.Revision }, recoveredRuntime, CancellationToken.None);
                if (recoveredRuntime.PhysicalCalls != 0 || recovered.ModelCalls != state.ModelCalls || recovered.Status != state.Status ||
                    recovered.Phase != state.Phase || recovered.Diagnostics.Count != state.Diagnostics.Count ||
                    recoveredRuntime.LastRequest != issued ||
                    JsonSerializer.Serialize(recovered.Construction?.Compact, PlanningJsonContext.Default.PlanningCompactConstructionState) !=
                    JsonSerializer.Serialize(state.Construction?.Compact, PlanningJsonContext.Default.PlanningCompactConstructionState) ||
                    PlanningConstruction.Hash(recovered.Plan) != PlanningConstruction.Hash(state.Plan) ||
                    recovered.Construction?.Checkpoints.Count != state.Construction?.Checkpoints.Count)
                    throw new InvalidOperationException("Compact phase recovery changed accepted work or dispatched a duplicate call.");
            }
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        }
        if (state.Status != PlanningStatus.FinalReview || state.Construction is not { Version: 2, Complete: true, Candidate: null } ||
            state.Construction.Compact is not { PresentationVersion: 2 } ||
            state.Construction.Requirements.Count != 2 || state.Construction.Requirements.Any(r => r.Status != "closed") ||
            state.ApprovedHash is not null || state.Yaml is null)
            throw new InvalidOperationException("Compact construction did not complete with explicit requirement closure: " +
                state.Status + "; " + JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        if (state.Request.Options["construction_presentation_version"]?.ToString() != "2" ||
            state.Construction.Compact.InterfaceExtractions is not [{ Complete: true, SourceIds.Count: 2 }] ||
            !runtime.IntentActions.SequenceEqual(["classification", "interfaces"]) ||
            !recoveredIntentActions.SetEquals(["classification", "interfaces"]) ||
            runtime.PhysicalCalls != 5 || state.ModelCalls != 5 || state.ReplanAttempts != 0)
            throw new InvalidOperationException("Fresh classified declarations, grouped interface extraction or exact recovery accounting changed.");
        var historical = JsonSerializer.Serialize(new PlanningCompactConstructionState(), PlanningJsonContext.Default.PlanningCompactConstructionState);
        if (historical.Contains("presentationVersion", StringComparison.Ordinal) || historical.Contains("interfaceExtractions", StringComparison.Ordinal))
            throw new InvalidOperationException("Fresh presentation metadata changed historical omitted fields.");
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
        Console.WriteLine("compact construction: classified originals, grouped interface extraction, five exact recovered calls, narrow draft/Judge and independently executed output");
    }

    private sealed class Runtime : IPlanningRuntime
    {
        private readonly WorkflowPlanningRuntime _inner = new(new(), (_, _) => Task.CompletedTask);
        internal Dictionary<string, LLMResponse> Receipts { get; } = new(StringComparer.Ordinal);
        internal List<string> Purposes { get; } = [];
        internal List<string> IntentActions { get; } = [];
        internal string? BeforeDispatch;
        internal string? LastRequest;
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
            LastRequest = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
            if (Receipts.TryGetValue(request.ClientRequestId!, out var retained)) return Task.FromResult(retained);
            PhysicalCalls++; Purposes.Add(purpose);
            var context = JsonNode.Parse(request.Prompt![(request.Prompt.LastIndexOf('\n') + 1)..])!;
            JsonObject response;
            switch (purpose)
            {
                case "construction_intent":
                    var classification = request.StructuredOutputSchema!["properties"]!["clauses"] is not null;
                    IntentActions.Add(classification ? "classification" : "interfaces");
                    response = classification
                        ? new() { ["sourceId"] = Issued(request, "sourceId"),
                            ["clauses"] = new JsonArray(
                                new JsonObject { ["text"] = ResultRequirement, ["kind"] = "requirement", ["interfaces"] = "output" },
                                new JsonObject { ["text"] = InterfaceRequirement, ["kind"] = "requirement", ["interfaces"] = "output" }) }
                        : new() { ["sourceId"] = Issued(request, "sourceId"), ["inputs"] = new JsonArray(),
                            ["outputs"] = new JsonArray(new JsonObject { ["name"] = "message", ["required"] = true,
                                ["type"] = new JsonObject { ["kind"] = "string", ["nullable"] = false } }) };
                    break;
                case "construction_select":
                    var requirements = new JsonArray("req_0", "req_1");
                    response = new()
                    {
                        ["baseHash"] = Issued(request, "baseHash"), ["evidenceRequests"] = null, ["clarifications"] = null,
                        ["step"] = new JsonObject
                        {
                            ["scope"] = "/root", ["kind"] = "value", ["objective"] = Objective,
                            ["requirementIds"] = requirements.DeepClone(), ["inputs"] = new JsonArray(),
                            ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray("The exact requested greeting"),
                            ["closeRequirementIds"] = requirements.DeepClone(), ["outputNames"] = new JsonArray("message")
                        }
                    };
                    break;
                case "construction_draft":
                    var requirementIds = context["step"]!["requirementIds"]!.AsArray().Select(id => id!.ToString()).ToList();
                    var extension = new PlanningConstructionExtension
                    {
                        Tasks = [new() { Id = "greeting", Kind = "value", Objective = Objective,
                            Outputs = [new("message", new() { Kind = "string", Text = "Hello" })] }],
                        Outputs = [new("message", new() { Kind = "output", Source = "greeting", Port = "message" })],
                        CloseScopes = ["/root"], RequirementIds = requirementIds, Complete = true
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
