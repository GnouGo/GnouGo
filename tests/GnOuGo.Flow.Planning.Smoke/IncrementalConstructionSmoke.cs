using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

internal static class IncrementalConstructionSmoke
{
    internal static async Task RunAsync()
    {
        var planner = new HybridWorkflowPlanner();
        var runtime = new Runtime();
        // Keep this exact issued version-1 recovery fixture alongside the fresh
        // compact smoke; new creation defaults must never migrate it.
        var initial = new PlanningSession
        {
            IntentVersion = 3, Construction = new() { Version = 1 },
            Request = new()
            {
                TenantId = "smoke", Prompt = "Return exactly Hello as message.", MaxModelCalls = 12,
                Options = new()
                {
                    ["construction_version"] = 1, ["compilation_profile"] = TaskPlanCompiler.CompactProfile,
                    ["mapping_profile"] = TaskPlanCompiler.AdaptiveMappingProfile,
                    ["planning_validation_profile"] = TaskPlanCompiler.AssertionValidationProfile,
                    ["planning_diagnostics_version"] = 1, ["planning_input_review_version"] = 2,
                    ["business_judge_review_version"] = 2, ["clarification_policy"] = 1
                }
            }
        };
        initial = JsonSerializer.Deserialize(JsonSerializer.Serialize(initial, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        var candidate = await planner.AdvanceAsync(initial, new(), runtime, CancellationToken.None);
        if (candidate.Plan is not null || candidate.Construction?.Candidate is null || candidate.Construction.Checkpoints.Count != 0 || runtime.PhysicalCalls != 1)
            throw new InvalidOperationException("Unexpected initial construction state: " + candidate.Status + "; calls=" + runtime.PhysicalCalls +
                "; accepted=" + (candidate.Plan is not null) + "; candidate=" + (candidate.Construction?.Candidate is not null) + "; " +
                JsonSerializer.Serialize(candidate.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));

        // Interrupt after the model completion but before the candidate checkpoint.
        // The retained pending request and its completed receipt survive independently.
        var restarted = JsonSerializer.Deserialize(runtime.BeforeDispatch!, PlanningJsonContext.Default.PlanningSession)!;
        var receipt = JsonSerializer.Deserialize(JsonSerializer.Serialize(runtime.Receipts.Single().Value,
            PlanningJsonContext.Default.LLMResponse), PlanningJsonContext.Default.LLMResponse)!;
        var recoveredRuntime = new Runtime();
        recoveredRuntime.Receipts.Add(restarted.PendingCall!.Id, receipt);
        restarted = await planner.AdvanceAsync(restarted, new() { ExpectedRevision = restarted.Revision }, recoveredRuntime, CancellationToken.None);
        restarted = JsonSerializer.Deserialize(JsonSerializer.Serialize(restarted, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        if (recoveredRuntime.PhysicalCalls != 0 || restarted.ModelCalls != 1 || restarted.Plan is not null)
            throw new InvalidOperationException("Receipt recovery redispatched or accepted an unreviewed draft.");
        for (var i = 0; i < 4 && !PlanningStatus.IsWaiting(restarted.Status) && !PlanningStatus.IsTerminal(restarted.Status); i++)
            restarted = await planner.AdvanceAsync(restarted, new() { ExpectedRevision = restarted.Revision }, recoveredRuntime, CancellationToken.None);
        PlanningArtifactApproval.Verify(restarted);
        if (restarted.Status != PlanningStatus.FinalReview || restarted.ModelCalls != 2 || recoveredRuntime.PhysicalCalls != 1 ||
            restarted.Construction is not { Complete: true, Candidate: null, Checkpoints.Count: 1 } || restarted.ApprovedHash is not null)
            throw new InvalidOperationException("Construction restart lost checkpoint, local-review or approval boundaries.");
        Console.WriteLine("incremental construction: source-generated pending-request recovery, one draft/local Judge, no duplicate dispatch");
    }

    private sealed class Runtime : IPlanningRuntime
    {
        private readonly WorkflowPlanningRuntime _inner = new(new(), (_, _) => Task.CompletedTask);
        internal Dictionary<string, LLMResponse> Receipts { get; } = new(StringComparer.Ordinal);
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
            if (Receipts.TryGetValue(request.ClientRequestId!, out var receipt)) return Task.FromResult(receipt);
            PhysicalCalls++;
            JsonObject response;
            if (purpose == "construction_local_judge")
                response = new() { ["findings"] = new JsonArray(), ["uncertainty"] = "Execution remains unverified.", ["evidenceRequests"] = null };
            else if (purpose == "construction_draft")
            {
                var requirements = new PlanningRequirements { Summary = "Return exactly Hello as message.", Inputs = [],
                    Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }], Outcomes = [new("message", "Return exactly Hello as message.")] };
                var extension = new PlanningConstructionExtension
                {
                    Tasks = [new() { Id = "greeting", Kind = "value", Objective = "Return the exact greeting", Outputs = [new("message", new() { Kind = "string", Text = "Hello" })] }],
                    Outputs = [new("message", new() { Kind = "output", Source = "greeting", Port = "message" })],
                    CloseScopes = ["/root"], Complete = true, RequirementIds = ["message"]
                };
                var issuedSchema = request.StructuredOutputSchema!;
                var issuedHash = issuedSchema["properties"]!["baseHash"]!;
                while (issuedHash["$ref"]?.ToString() is { } reference) issuedHash = issuedSchema["$defs"]![reference[8..]]!;
                response = new() { ["baseHash"] = issuedHash["enum"]![0]!.DeepClone(),
                    ["requirements"] = JsonSerializer.SerializeToNode(requirements, PlanningJsonContext.Default.PlanningRequirements),
                    ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension),
                    ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null };
            }
            else throw new InvalidOperationException("Unexpected construction model phase: " + purpose);
            var result = new LLMResponse { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
            Receipts.Add(request.ClientRequestId!, result);
            return Task.FromResult(result);
        }
    }
}
