using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Planning.Examples;

/// <summary>Explicit responses for a current-protocol literal workflow, using ordinary indexing, drafting and review.</summary>
public static class CurrentPlanningFixture
{
    public static async Task<PlanningSession> LiteralAsync(string tenantId, string? sessionId, PlanningPolicy? policy, CancellationToken ct)
        => await RunAsync(tenantId, sessionId, policy, interruptReview: false, ct);

    public static async Task<PlanningSession> PendingLiteralReviewAsync(string tenantId, string sessionId, CancellationToken ct)
        => await RunAsync(tenantId, sessionId, null, interruptReview: true, ct);

    private static async Task<PlanningSession> RunAsync(string tenantId, string? sessionId, PlanningPolicy? policy, bool interruptReview, CancellationToken ct)
    {
        var state = new PlanningSession { Request = new()
        {
            TenantId = tenantId, SessionId = sessionId ?? Guid.NewGuid().ToString("N"), Name = "literal-result",
            Prompt = "Return the required public numeric result named result with value 42.",
            Policy = policy ?? new(), Options = new() { ["automatic_correction"] = true, ["benchmark_planning_elapsed_ms"] = 18_000_000L }
        } };
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = new LiteralModel(interruptReview) }, (_, _) => Task.CompletedTask);
        var planner = new HybridWorkflowPlanner();
        for (var advance = 0; advance < 40 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); advance++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, ct);
        if (interruptReview && state.PendingCall?.Purpose == "construction_global_judge")
        { state.Status = PlanningStatus.Generating; return state; }
        if (state.Status != PlanningStatus.FinalReview)
            throw new InvalidOperationException("Current literal fixture failed: " + state.Status + "; " +
                string.Join("; ", state.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
        PlanningArtifactApproval.Verify(state);
        return state;
    }

    public sealed class LiteralModel(bool interruptReview = false, Action<LLMRequest>? onRequest = null,
        IReadOnlyList<PlanningQuestion>? questions = null) : ILLMClient, ILLMCapabilityResolver
    {
        private bool _asked;
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>(["medium"]);
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            onRequest?.Invoke(request);
            var schema = request.StructuredOutputSchema!.AsObject();
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
            var purpose = schema["title"]!.ToString();
            if (interruptReview && purpose == "construction_global_judge") throw new IOException("Interrupted at the exact issued review request.");
            if (!_asked && questions is not null && purpose == "construction_intent" && schema["properties"]?["interfaces"] is not null)
            {
                _asked = true;
                var clarification = new JsonObject { ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(),
                    ["clarifications"] = JsonSerializer.SerializeToNode(questions.ToList(), PlanningJsonContext.Default.ListPlanningQuestion) };
                return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(clarification, schema, schema),
                    Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
            }
            var response = purpose switch
            {
                "construction_intent" when schema["properties"]!["clauses"] is not null => Classify(context),
                "construction_intent" => Interfaces(context),
                "construction_select" => new JsonObject
                {
                    ["baseHash"] = schema["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(),
                    ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = "value", ["objective"] = "Return numeric result 42.",
                        ["operationIds"] = new JsonArray(),
                        ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(), ["dependencyTaskIds"] = new JsonArray(),
                        ["expectedOutputs"] = new JsonArray("result = 42"), ["closeRequirementIds"] = new JsonArray("req_0"), ["outputNames"] = new JsonArray("result") }
                },
                "construction_draft" => new JsonObject
                {
                    ["baseHash"] = context["baseHash"]!.DeepClone(),
                    ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension
                    {
                        Scope = "/root", RequirementIds = ["req_0"], CloseScopes = ["/root"], Complete = true,
                        Tasks = [new() { Id = "result", Kind = "value", Objective = "Return the original required number.",
                            Outputs = [new("value", new() { Kind = "number", Number = 42 })] }],
                        Outputs = [new("result", new() { Kind = "output", Source = "result", Port = "value" })]
                    }, PlanningJsonContext.Default.PlanningConstructionExtension)
                },
                "construction_local_judge" or "construction_global_judge" => new JsonObject
                    { ["decision"] = new JsonObject { ["verdict"] = "ok", ["reason"] = "The fragment returns the exact original required number and makes no external effects." } },
                _ => throw new InvalidOperationException("Unexpected fixture request phase: " + purpose)
            };
            return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(response, schema, schema),
                Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }

        private static JsonObject Classify(JsonObject context)
        {
            var source = context["sources"]![0]!;
            var answer = source["id"]!.ToString().StartsWith("answer:", StringComparison.Ordinal);
            return new() { ["sourceId"] = source["id"]!.DeepClone(), ["clauses"] = new JsonArray(new JsonObject
                { ["text"] = source["text"]!.DeepClone(), ["kind"] = answer ? "global" : "requirement", ["interfaces"] = answer ? "none" : "output" }) };
        }

        private static JsonObject Interfaces(JsonObject context) => new()
        {
            ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(),
            ["interfaces"] = new JsonObject
            {
                ["inputs"] = new JsonArray(), ["outputs"] = JsonSerializer.SerializeToNode(new List<TaskInput>
                    { new() { Name = "result", Type = new() { Kind = "number" } } }, PlanningJsonContext.Default.ListTaskInput),
                ["dispositions"] = new JsonArray(context["sources"]!.AsArray().Select(source => (JsonNode)new JsonObject
                {
                    ["sourceId"] = source!["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone(), ["kind"] = "interfaces",
                    ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray("result"),
                    ["reason"] = "The exact original explicitly declares the required public numeric result.",
                    ["evidence"] = new JsonArray(new JsonObject { ["sourceId"] = source["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone() })
                }).ToArray())
            }
        };
    }
}
