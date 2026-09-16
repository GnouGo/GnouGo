using GnOuGo.Flow.Planning.Tests;
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
                    ["member"] = PlanningDeclarations.Assignment(new("member", "modifier_of", PlanningDeclarations.CanonicalId("report", "main", "output"), null, null, "unspecified", null))
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
            PlanningSnapshot DuplicateDeclarations() => new() { Request = new() { TenantId = "smoke", Prompt = "Required output receipt is returned.", MaxRepairsPerWorkflowGate = 1 } };
            await using (var opened = await Factory().OpenAsync(Context("duplicate-declarations"), DuplicateDeclarations(), CancellationToken.None))
            {
                var snapshot = opened.Snapshot;
                var source = PlanningIntentAssessment.IntentSources(snapshot).Single(s => s.Id == "request");
                var parent = PlanningReferences.Register(snapshot, source.Id, source.Kind, source.Text).Single();
                foreach (var id in new[] { "root", "overlap" })
                {
                    var reference = parent with { Id = parent.Id + "_" + id, Kind = parent.Kind + ":selection" };
                    snapshot.References.Add(reference);
                    var obligation = new PlanningObligation(id, [reference.Id], "business_decision", "declaration_candidate", true);
                    snapshot.Obligations.Add(obligation with { Grounding = PlanningSourceGroundingRules.Create(snapshot, obligation) });
                }
                var clauseId = snapshot.Obligations[0].Grounding!.ClauseReference;
                var name = PlanningReferences.Lexical(snapshot, snapshot.References.Single(r => r.Id == clauseId), source.Text)
                    .Single(r => PlanningChoiceEvidence.Text(snapshot, r.Id) == "receipt").Id;
                var initial = new[] { "root", "overlap" }.Select(id => new PlanningDeclarationAssignment(id, "distinct_output", null, name, "main", "required", null)
                    { DeclarationReference = clauseId, PresenceReference = clauseId }).ToList();
                client.DeclarationAnswers = initial.ToDictionary(a => a.CandidateId, PlanningDeclarations.Assignment);
                await PlanningDecisionPages.ResolveAsync(snapshot, opened.Runtime, "intent_declarations", "$plan", PlanningDeclarations.Decisions(snapshot), CancellationToken.None);
                client.DeclarationAnswers["overlap"] = PlanningDeclarations.Assignment(new("overlap", "same_as", PlanningDeclarations.CanonicalId("receipt", "main", "output"), null, null, "unspecified", null));
                await PlanningDecisionPages.ResolveCorrectionsAsync(snapshot, opened.Runtime, "intent_declarations", "$plan", PlanningGates.Response,
                    PlanningDeclarations.RootCorrections(snapshot, initial), CancellationToken.None);
                if (snapshot.Declarations.Count != 0) throw new InvalidOperationException("A correction granted premature declaration authority.");
            }
            var correctedCalls = client.Calls;
            await using (var resumed = await Factory().OpenAsync(Context("duplicate-declarations"), DuplicateDeclarations(), CancellationToken.None))
            {
                await PlanningDeclarations.ResolveAsync(resumed.Snapshot, resumed.Runtime, CancellationToken.None);
                var declaration = resumed.Snapshot.Declarations.Single();
                if (client.Calls != correctedCalls + 1 || declaration.Id != PlanningDeclarations.CanonicalId("receipt", "main", "output") ||
                    declaration.Aliases.Count != 1 || resumed.Snapshot.RepairAllowances.Sum(a => a.Attempts) != 1 || resumed.Snapshot.DecisionCorrections.Count != 1 ||
                    resumed.Snapshot.DecisionPages.Count(p => p.SourceDecisionIds is { Count: 1 }) != 1)
                    throw new InvalidOperationException("Published duplicate-root correction lost its identity, receipt or finite allowance.");
            }
            await using (var resumed = await Factory().OpenAsync(Context("duplicate-declarations"), DuplicateDeclarations(), CancellationToken.None))
            {
                await PlanningDeclarations.ResolveAsync(resumed.Snapshot, resumed.Runtime, CancellationToken.None);
                if (client.Calls != correctedCalls + 1) throw new InvalidOperationException("Published duplicate-root replay dispatched again.");
            }
            PlanningSnapshot Operations() => new() { Request = new() { TenantId = "smoke", Prompt = "Transform the value. Transform according to the rules. This processing is deterministic.", MaxRepairsPerWorkflowGate = 0 } };
            client.OperationAnswers = true;
            await using (var opened = await Factory().OpenAsync(Context("operations"), Operations(), CancellationToken.None))
            {
                var snapshot = opened.Snapshot;
                var scopes = PlanningOperations.SourceScopes(snapshot);
                foreach (var scope in scopes)
                {
                    var answer = new JsonObject { ["role"] = "local_behavior", ["kind"] = "local_processing",
                        ["action"] = new JsonObject { ["start"] = "b0", ["end"] = scope.Boundaries["properties"]!["end"]!["enum"]!.AsArray().Last()!.DeepClone() },
                        ["execution"] = "generated_workflow", ["evidence"] = scope == scopes[^1] ? "governing" : "action", ["necessity"] = new JsonObject { ["state"] = scope == scopes[1] ? "required" : "unspecified", ["evidence"] = scope == scopes[1] ? new JsonObject { ["start"] = "b0", ["end"] = scope.Boundaries["properties"]!["end"]!["enum"]!.AsArray().Last()!.DeepClone() } : null }, ["baseline"] = null };
                    answer["boundary"] = scope == scopes[0] ? new JsonObject { ["kind"] = "invocation", ["owner"] = answer["action"]!.DeepClone(), ["span"] = answer["action"]!.DeepClone() } : null;
                    snapshot.RuntimeEvidence.AddRange(PlanningOperations.ParseRuntime(snapshot, scope.Clause, scope.Select,
                        new JsonArray(answer.DeepClone(), answer.DeepClone(), answer.DeepClone())));
                }
                if (snapshot.RuntimeEvidence.Count != 3) throw new InvalidOperationException("Exact runtime evidence duplicates did not collapse.");
                snapshot.RuntimeEvidenceFingerprint = PlanningOperations.RuntimeFingerprint(snapshot);
                var eligible = PlanningOperations.Scopes(snapshot);
                client.EffectState = snapshot;
                var effectDecisions = eligible.Where(scope => scope.Evidence!.EvidenceRole == "action").Select(scope => PlanningOperations.EffectDecision(snapshot, scope)).ToArray();
                await PlanningDecisionPages.ResolveAsync(snapshot, opened.Runtime, "intent_operations", "$plan", effectDecisions, CancellationToken.None);
                if (snapshot.Obligations.Any(PlanningSourceDecisions.IsOperation)) throw new InvalidOperationException("Staged operation identity granted partial authority.");
            }
            var identityCalls = client.Calls;
            string? operationFingerprint = null;
            for (var restart = 0; restart < 2; restart++)
            {
                await using var resumed = await Factory().OpenAsync(Context("operations"), Operations(), CancellationToken.None);
                client.EffectState = resumed.Snapshot;
                await PlanningOperations.ResolveAsync(resumed.Snapshot, resumed.Runtime, CancellationToken.None);
                if (restart == 0 && client.Calls == identityCalls + 1) identityCalls = client.Calls;
                var operation = resumed.Snapshot.Obligations.Single(PlanningSourceDecisions.IsOperation);
                if (client.Calls != identityCalls || !operation.Required || resumed.Snapshot.RuntimeEvidence.Count != 3 || operation.OperationAdmission!.Assignments.Count != 3 || resumed.Snapshot.RepairAllowances.Count != 0 ||
                    operationFingerprint is not null && operationFingerprint != resumed.Snapshot.OperationAdmissionFingerprint)
                    throw new InvalidOperationException("Published encrypted operation identity/attachment replay failed.");
                operationFingerprint = resumed.Snapshot.OperationAdmissionFingerprint;
            }
            // Synthetic grounded effects are fixture preconditions. Dependency
            // answers pass through the real encrypted journal before admission.
            PlanningSnapshot Dependencies() => new() { Request = new() { TenantId = "smoke", Prompt = "Transform the input. Refine the transformed result.", MaxRepairsPerWorkflowGate = 0 } };
            await using (var opened = await Factory().OpenAsync(Context("dependencies"), Dependencies(), CancellationToken.None))
            {
                var snapshot = opened.Snapshot;
                foreach (var scope in PlanningOperations.SourceScopes(snapshot))
                    snapshot.RuntimeEvidence.AddRange(PlanningOperations.ParseRuntime(snapshot, scope.Clause, scope.Select, new JsonArray(new JsonObject
                    {
                        ["role"] = "local_behavior", ["kind"] = "local_processing", ["execution"] = "generated_workflow", ["evidence"] = "action",
                        ["boundary"] = new JsonObject { ["kind"] = "invocation", ["owner"] = new JsonObject { ["start"] = "b0", ["end"] = scope.Boundaries["properties"]!["end"]!["enum"]!.AsArray().Last()!.DeepClone() }, ["span"] = new JsonObject { ["start"] = "b0", ["end"] = scope.Boundaries["properties"]!["end"]!["enum"]!.AsArray().Last()!.DeepClone() } },
                        ["action"] = new JsonObject { ["start"] = "b0", ["end"] = scope.Boundaries["properties"]!["end"]!["enum"]!.AsArray().Last()!.DeepClone() },
                        ["necessity"] = new JsonObject { ["state"] = "unspecified", ["evidence"] = null }, ["baseline"] = null
                    })));
                snapshot.RuntimeEvidenceFingerprint = PlanningOperations.RuntimeFingerprint(snapshot);
                OperationEffectFixtures.Seed(snapshot, seedDependencies: false);
                client.EffectState = snapshot;
                var domain = PlanningOperations.DependencyDecisions(snapshot, OperationEffectFixtures.Staged(snapshot));
                await PlanningDecisionPages.ResolveAsync(snapshot, opened.Runtime, "intent_operations", "$plan", domain.Decisions, CancellationToken.None);
                if (snapshot.Obligations.Any(PlanningSourceDecisions.IsOperation)) throw new InvalidOperationException("Partial dependency pages granted operation authority.");
            }
            var dependencyCalls = client.Calls; string? dependencyFingerprint = null;
            for (var restart = 0; restart < 2; restart++)
            {
                await using var resumed = await Factory().OpenAsync(Context("dependencies"), Dependencies(), CancellationToken.None);
                client.EffectState = resumed.Snapshot;
                await PlanningOperations.ResolveAsync(resumed.Snapshot, resumed.Runtime, CancellationToken.None);
                var proofs = resumed.Snapshot.Obligations.Where(PlanningSourceDecisions.IsOperation).Select(o => o.OperationAdmission!.Dependencies!).ToArray();
                var edges = proofs.SelectMany(p => p.Assignments).Where(a => a.Disposition == "data").ToArray();
                if (client.Calls != dependencyCalls || proofs.Length != 2 || edges.Length != 1 || edges[0].Origin != PlanningDependencyOrigin.ModelSemanticSelection ||
                    dependencyFingerprint is not null && dependencyFingerprint != resumed.Snapshot.OperationAdmissionFingerprint)
                    throw new InvalidOperationException("Published encrypted dependency proof replay failed.");
                dependencyFingerprint = resumed.Snapshot.OperationAdmissionFingerprint;
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
        public bool OperationAnswers { get; set; }
        public PlanningSnapshot? EffectState { get; set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            var fields = request.StructuredOutputSchema!["properties"]!.AsObject();
            if (OperationAnswers)
            {
                var state = EffectState!;
                if (fields.All(p => p.Key.StartsWith("data_", StringComparison.Ordinal)))
                {
                    var actions = PlanningOperations.ReadRealizations(state);
                    var edge = PlanningOperations.DependencyDecisionId(actions[0].EffectId!, actions[1].EffectId!);
                    return Task.FromResult(new LLMResponse { CompletionStatus = "completed", Usage = new JsonObject { ["total_tokens"] = 2 },
                        Json = new JsonObject(fields.Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                            OperationEffectFixtures.DependencyAnswer(p.Value!.AsObject(), p.Key == edge)))) });
                }
                var first = PlanningOperations.Scopes(state).First(scope => scope.Evidence!.EvidenceRole == "action");
                var target = PlanningOperations.EffectDomain(state, first.Evidence!).Single(p => p.Value.BoundaryReference == first.Evidence!.ActionReference).Key;
                return Task.FromResult(new LLMResponse { CompletionStatus = "completed", Usage = new JsonObject { ["total_tokens"] = 2 },
                    Json = new JsonObject(fields.Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                        OperationEffectFixtures.Answer(state, OperationEffectFixtures.Scope(state, p.Key), [target])))) });
            }
            if (DeclarationAnswers is { } answers)
            {
                var response = new JsonObject();
                foreach (var page in fields)
                {
                    var values = new JsonObject();
                    foreach (var field in page.Value!["properties"]!.AsObject())
                        values[field.Key] = page.Key.StartsWith("declarations_root_conflict_", StringComparison.Ordinal) && answers[field.Key]["disposition"]?.ToString() is "same_as" or "modifier_of"
                            ? new JsonObject { ["disposition"] = "deferred_attachment" } : answers[field.Key].DeepClone();
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
