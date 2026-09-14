using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;

internal static class RuntimePersistenceSmoke
{
    internal static async Task RunAsync()
    {
        var metadata = new LLMOptions
        {
            DefaultProvider = "configured", DefaultModel = "declared",
            Models = { ["configured"] = new() { Type = "neutral" } },
            ModelOverrides =
            {
                ["neutral/declared"] = new() { Capabilities = new() { SupportsStructuredOutput = true, SupportsReasoningEffort = true, SupportedReasoningEfforts = ["low"] } },
                ["neutral/partial"] = new() { Capabilities = new() { SupportsReasoningEffort = true } }
            }
        };
        var capabilities = new RoutingLLMClientAdapter(new RoutingLLMClient(metadata, []));
        if (await capabilities.SupportsStructuredOutputAsync(null, "", CancellationToken.None) != true ||
            await capabilities.SupportedReasoningLevelsAsync(null, "", CancellationToken.None) is not { Count: 1 } levels || levels[0] != "low" ||
            await capabilities.SupportedReasoningLevelsAsync(null, "partial", CancellationToken.None) is not null ||
            await capabilities.SupportsStructuredOutputAsync(null, "unknown", CancellationToken.None) is not null)
            throw new InvalidOperationException("Published declared capability resolution failed.");
        var directory = Path.Combine(Path.GetTempPath(), "gnougo-planning-native-" + Guid.NewGuid().ToString("N"));
        try
        {
            var client = new Client();
            WorkflowPlanningRuntimeFactory Factory() => WorkflowPlanningRuntimeFactory.CreateWorkspace(Path.Combine(directory, "keyvault.db"), Path.Combine(directory, "leases"));
            StepExecutionContext Context(string runId = "native") => new()
            {
                Engine = new WorkflowEngine { LLMClient = client },
                Data = new(),
                Step = new() { Source = new StepDef { Id = "plan", Type = "workflow.plan" } },
                Limits = new() { RunId = runId, TenantId = "smoke" }
            };
            PlanningSnapshot Initial() => new() { Request = new() { TenantId = "smoke", Prompt = "private native intent" } };
            var decisions = new[] { "a", "b", "c", "d", "e" }.Select(id => new PlanningDecisionPages.Decision(id,
                new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("yes", "no") }, new() { ["evidence"] = "private native intent" }, "frozen")).ToArray();
            LLMRequest request;
            await using (var session = await Factory().OpenAsync(Context(), Initial(), CancellationToken.None))
            {
                request = PlanningGenerationPolicy.Apply(new()
                {
                    Model = "smoke", Reasoning = "low",
                    Prompt = "private native intent",
                    StructuredOutputStrict = true,
                    StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{},"required":[],"additionalProperties":false}""")
                }, new());
                request.ClientRequestId = session.Snapshot.Request.SessionId + ":1:intent:workflow:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                session.Snapshot.Construction.PendingCalls.Add(new() { Id = request.ClientRequestId, Request = request, Phase = "intent" });
                await session.Runtime.CheckpointAsync(session.Snapshot, CancellationToken.None);
                await session.Runtime.CallAsync(request, "intent", CancellationToken.None);
            }
            await using (var resumed = await Factory().OpenAsync(Context(), Initial(), CancellationToken.None))
            {
                if (resumed.Snapshot.Construction.PendingCalls.Count != 1 || resumed.Snapshot.Usage?.Calls != 1)
                    throw new InvalidOperationException("Published session or budget persistence failed.");
                await resumed.Runtime.CallAsync(request, "intent", CancellationToken.None);
                if (client.Calls != 1) throw new InvalidOperationException("Published receipt replay dispatched another model request.");
                resumed.Snapshot.Construction.PendingCalls.Clear();
                resumed.Snapshot.Request.MaxRepairsPerWorkflowGate = 0;
                for (var index = 0; index < 3; index++)
                {
                    var dispatch = PlanningDecisionPages.Next(resumed.Snapshot, "intent", "$plan", decisions)!;
                    await resumed.Runtime.CheckpointAsync(resumed.Snapshot, CancellationToken.None);
                    PlanningDecisionPages.Accept(resumed.Snapshot, dispatch, await PlanningModelCalls.DispatchAsync(resumed.Snapshot, resumed.Runtime, dispatch.Call, CancellationToken.None));
                    await resumed.Runtime.CheckpointAsync(resumed.Snapshot, CancellationToken.None);
                }
            }
            await using (var resumed = await Factory().OpenAsync(Context(), Initial(), CancellationToken.None))
            {
                var pages = resumed.Snapshot.DecisionPages;
                if (pages.Count != 3 || pages[1].Status != "completed" || pages[2].Status != "split" ||
                    pages[1].Origin != PlanningDecisionPageOrigin.OutputPartition || pages[1].Gate != PlanningGates.Response || pages[0].PartitionChildren.Count != 2)
                    throw new InvalidOperationException("Published encrypted partition tree recovery failed.");
                var assignments = await PlanningDecisionPages.ResolveAsync(resumed.Snapshot, resumed.Runtime, "intent", "$plan", decisions, CancellationToken.None);
                if (assignments.Count != 5 || client.Calls != 7 || resumed.Snapshot.RequestAccounting.Count != 6 ||
                    resumed.Snapshot.DecisionCorrections.Count != 0 || resumed.Snapshot.RepairAllowances.Count != 0)
                    throw new InvalidOperationException("Published recursive partition replay or accounting failed.");
                var escalated = resumed.Snapshot.DecisionPages.Single(p => p.Origin == PlanningDecisionPageOrigin.OutputBudgetEscalation);
                if (escalated.EffectiveOutputTokens != 16384 || escalated.OutputBudgetEscalation?.Level != 1)
                    throw new InvalidOperationException("Published output escalation metadata was lost.");
            }
            await using (var resumed = await Factory().OpenAsync(Context(), Initial(), CancellationToken.None))
            {
                await PlanningDecisionPages.ResolveAsync(resumed.Snapshot, resumed.Runtime, "intent", "$plan", decisions, CancellationToken.None);
                if (client.Calls != 7) throw new InvalidOperationException("Published escalated receipt replay dispatched again.");
            }
            // Freeze only semantic roots, persist through the real encrypted runtime,
            // then recover the dependent attachment domain without dispatching roots again.
            PlanningSnapshot Declarations() => new() { Request = new() { TenantId = "smoke", Prompt = "Required output report has a status member. Its status is a string.", MaxRepairsPerWorkflowGate = 0 } };
            await using (var opened = await Factory().OpenAsync(Context("declarations"), Declarations(), CancellationToken.None))
            {
                var snapshot = opened.Snapshot;
                var source = PlanningIntentAssessment.IntentSources(snapshot).Single(s => s.Id == "request");
                var references = PlanningReferences.Register(snapshot, source.Id, source.Kind, source.Text);
                void Candidate(string id, string fragment, string kind)
                {
                    var parent = references.Single(r => source.Text.Substring(r.Start, r.Length).Contains(fragment, StringComparison.Ordinal));
                    var reference = parent with { Id = parent.Id + "_" + id, Kind = parent.Kind + ":selection", Start = source.Text.IndexOf(fragment, StringComparison.Ordinal), Length = fragment.Length };
                    snapshot.References.Add(reference);
                    var obligation = new PlanningObligation(id, [reference.Id], "business_decision", kind, true);
                    snapshot.Obligations.Add(obligation with { Grounding = PlanningSourceGroundingRules.Create(snapshot, obligation) });
                }
                Candidate("output", "Required output report", "declaration_candidate"); Candidate("member", "Its status is a string.", "declaration_constraint");
                var clauseId = snapshot.Obligations.Single(o => o.Id == "output").Grounding!.ClauseReference;
                var name = PlanningReferences.Lexical(snapshot, snapshot.References.Single(r => r.Id == clauseId), source.Text)
                    .Single(r => PlanningChoiceEvidence.Text(snapshot, r.Id) == "report").Id;
                client.DeclarationAnswers = new()
                {
                    ["output"] = PlanningDeclarations.Assignment(new("output", "distinct_output", null, name, "main", "required", null)
                        { DeclarationReference = clauseId, PresenceReference = clauseId }),
                    ["member"] = PlanningDeclarations.Assignment(new("member", "modifier_of", PlanningDeclarations.CanonicalId("output", name, "main", "output"), null, null, "unspecified", null))
                };
                await PlanningDecisionPages.ResolveAsync(snapshot, opened.Runtime, "intent_declarations", "$plan", PlanningDeclarations.Decisions(snapshot), CancellationToken.None);
                if (snapshot.Declarations.Count != 0 || snapshot.DeclarationFingerprint is not null) throw new InvalidOperationException("Staged roots granted premature port authority.");
            }
            var rootCalls = client.Calls;
            await using (var resumed = await Factory().OpenAsync(Context("declarations"), Declarations(), CancellationToken.None))
            {
                await PlanningDeclarations.ResolveAsync(resumed.Snapshot, resumed.Runtime, CancellationToken.None);
                var declaration = resumed.Snapshot.Declarations.Single();
                if (client.Calls != rootCalls + 1 || declaration.Direction != "output" || declaration.ModifierReferences.Count != 1 ||
                    resumed.Snapshot.RepairAllowances.Count != 0 || resumed.Snapshot.DecisionCorrections.Count != 0)
                    throw new InvalidOperationException("Published encrypted declaration attachment recovery failed.");
            }
            await using (var resumed = await Factory().OpenAsync(Context("declarations"), Declarations(), CancellationToken.None))
            {
                await PlanningDeclarations.ResolveAsync(resumed.Snapshot, resumed.Runtime, CancellationToken.None);
                if (client.Calls != rootCalls + 1) throw new InvalidOperationException("Published declaration replay dispatched again.");
            }
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                if (System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains("private native intent", StringComparison.Ordinal))
                    throw new InvalidOperationException("Published planning content was stored in plaintext.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class Client : ILLMClient, ILLMCapabilityResolver
    {
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["low", "medium"]);
        public int Calls { get; private set; }
        public Dictionary<string, JsonObject>? DeclarationAnswers { get; set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            var fields = request.StructuredOutputSchema!["properties"]!.AsObject();
            if (DeclarationAnswers is { } answers)
            {
                var response = new JsonObject();
                foreach (var page in fields)
                {
                    var values = new JsonObject();
                    foreach (var field in page.Value!["properties"]!.AsObject())
                        values[field.Key] = answers[field.Key].DeepClone();
                    response[page.Key] = values;
                }
                return Task.FromResult(new LLMResponse { CompletionStatus = "completed", Json = response, Usage = new JsonObject { ["total_tokens"] = 2 } });
            }
            return Task.FromResult(new LLMResponse
            {
                CompletionStatus = fields.Count is 5 or 3 || fields.Count == 1 && fields.ContainsKey("c") && request.MaxTokens == 8192 ? "output_limit" : "completed",
                Json = new JsonObject(fields.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create("yes")))),
                Usage = new JsonObject { ["total_tokens"] = 2 }
            });
        }
    }
}
