using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Planning.Tests;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    // Explicit synthetic corrected mappings on detached archive input. No provider,
    // durable budget writer, checkpoint writer, or session advancement is installed.
    internal static async Task CoverageFixtureAsync(string id, IKeyVaultRecordStore records, bool contractEligibility = false, bool jointClause = false, bool nestedQualifiers = false)
    {
        var archiveBefore = await ArchiveAsync(records, CancellationToken.None);
        var captured = await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author) ?? throw new InvalidOperationException("Missing retained checkpoint.");
        var budget = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author);
        var state = JsonSerializer.Deserialize(JsonNode.Parse(captured.Value)!["snapshot"], PlanningJsonContext.Default.PlanningSnapshot)!;
        var name = id.EndsWith(":mixed", StringComparison.Ordinal) ? "mixed" : "local";
        var originalAccounting = state.RequestAccounting.Count;
        state.Obligations.RemoveAll(o => o.OperationAdmission is not null); state.OperationAdmissionFingerprint = null;
        // Explicit current-proof reassessment of detached fixture facts. Historical receipts remain unchanged.
        state.RuntimeEvidence = state.RuntimeEvidence.Select(e => PlanningOperations.SealRuntime(state, e with { EvidenceRole = e.Origin == PlanningRuntimeEvidenceOrigin.EngineBaseline ? e.EvidenceRole : null })).ToList();
        state.RuntimeEvidenceFingerprint = PlanningOperations.RuntimeFingerprint(state);
        var qualifications = PlanningOperations.ContributionDecisions(state);
        var interpretation = PlanningSourceDecisions.InterpretationDecisions(state);
        var semanticFixtures = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (contractEligibility && !jointClause)
        {
            // These become explicit synthetic answers for new domains, never
            // journal receipts or historical replay of a changed request.
            foreach (var call in state.RequestAccounting.DistinctBy(c => c.Id))
            {
                if (!call.Phase.StartsWith("intent_operations", StringComparison.Ordinal)) continue;
                var requestRecord = await records.GetAsync(PlanningModelJournal.RequestCollection, Tenant, id + ":" + call.Id, EfPlanningSessionStore.Author);
                var receiptRecord = await records.GetAsync(PlanningModelJournal.Collection, Tenant, id + ":" + call.Id, EfPlanningSessionStore.Author);
                if (requestRecord is null || receiptRecord is null) throw new InvalidOperationException("Synthetic retained-shape measurement requires complete original evidence.");
                var request = JsonSerializer.Deserialize(requestRecord.Value, PlanningJsonContext.Default.LLMRequest)!;
                var receipt = JsonSerializer.Deserialize(receiptRecord.Value, PlanningJsonContext.Default.LLMResponse)!;
                var answer = receipt.Json ?? JsonNode.Parse(receipt.Text!);
                if (PlanningContractValidation.ValidateInstance(answer, request.StructuredOutputSchema!).Count != 0)
                    throw new InvalidOperationException("Original-schema evidence failed audit.");
                foreach (var field in answer!.AsObject().Where(p => p.Key.StartsWith("contribution_", StringComparison.Ordinal)))
                    semanticFixtures.Add(field.Key, field.Value!.DeepClone().AsObject());
            }
            var excluded = PlanningOperations.DeclarationExclusions(state);
            if (name == "mixed" && (qualifications.Length != 2 || !excluded.ContainsKey("runtime_30713b159af7b4abb37970db") ||
                !excluded.ContainsKey("runtime_ff2313d52c4641cbbe61879a")))
                throw new InvalidOperationException("Retained MIXED contract exclusions did not precede qualification.");
            foreach (var decision in qualifications)
                if (!semanticFixtures.TryGetValue(decision.Id, out var fixture) || PlanningContractValidation.ValidateInstance(fixture, decision.Schema).Count != 0)
                    throw new InvalidOperationException("An eligible retained-shape semantic fixture no longer fits its explicitly new domain.");
        }
        var client = new CoverageFixtureClient(state, semanticFixtures, nestedQualifiers);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = client, LLMCapabilities = client }, (_, _) => Task.CompletedTask);
        await PlanningOperations.ResolveAsync(state, runtime, CancellationToken.None);
        var groups = PlanningOperations.CoverageGroups(state);
        if (groups.Length != (name == "mixed" ? 2 : 1)) throw new InvalidOperationException("Captured coverage groups changed.");
        PlanningOperations.RequireCurrent(state); PlanningOperations.RequireExecutableIntent(state);
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray();
        // A corrected qualification is not authority to fill missing historical
        // runtime spans. Report further fixture gaps without relabelling the archive.
        JsonObject? acceptanceFinding = null;
        try { CheckEffectFixture(name, state, operations); }
        catch (GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException error) when (nestedQualifiers)
        { acceptanceFinding = new() { ["code"] = error.Code, ["message"] = error.Message }; }
        var inputs = state.Declarations.ToDictionary(d => d.Id, d => PlanningDeclarations.Name(state, d));
        var local = operations.Single(o => o.Kind == "local_processing");
        var localInputs = local.OperationAdmission!.Assignments.SelectMany(a => a.Effect!.Inputs).Distinct().Select(r => inputs[r]).Order().ToArray();
        if (!localInputs.SequenceEqual(name == "mixed" ? ["threshold"] : new[] { "record", "threshold" })) throw new InvalidOperationException("Local public input evidence changed.");
        var before = client.Requests.Count; var restart = await VerifyReadOnlyRestartAsync(state, PlanningContext.Clone(state), CancellationToken.None);
        if (before != client.Requests.Count) throw new InvalidOperationException("Restart dispatched.");
        var admissionCalls = client.Requests.Count;
        if (contractEligibility && name == "mixed")
        {
            await PlanningSourceDecisions.RelateAsync(state, runtime, CancellationToken.None);
            RuntimeAdmissionDiagnosticRules.RequireProjectedData(operations, state.ObligationRelations);
            foreach (var request in client.Requests.Skip(admissionCalls)) RuntimeAdmissionDiagnosticRules.RequireRelationDomain(request.StructuredOutputSchema);
        }
        var nominalCalls = PlanningDecisionPages.PackedPageCount(state, interpretation) + client.Requests.Count;
        if (contractEligibility && nominalCalls > RuntimeAdmissionDiagnosticRules.MaxCalls)
            throw new InvalidOperationException("Architecture review required: the mandatory nominal path exceeds the unchanged sixteen-call budget.");
        if ((await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author))?.Value != captured.Value ||
            (await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author))?.Value != budget?.Value)
            throw new InvalidOperationException("Archive changed.");
        var archiveAfter = await ArchiveAsync(records, CancellationToken.None);
        if (archiveBefore != archiveAfter) throw new InvalidOperationException("Other archived evidence changed.");
        var fields = client.Requests.SelectMany(r => r.StructuredOutputSchema!["properties"]!.AsObject().Select(p => p.Key)).ToArray();
        Console.WriteLine(new JsonObject
        {
            ["evidence"] = "Explicit synthetic complete coverage and dependency answers on retained runtime/declaration fixtures; not historical receipts or live convergence.",
            ["sourceIdentity"] = id, ["archiveUnchanged"] = true, ["archiveFingerprint"] = archiveAfter, ["providerDispatches"] = 0,
            ["qualificationFixtureOrigin"] = contractEligibility ? "Explicit synthetic current-domain qualification answers retaining eligible historical semantic shape; no receipt substitution" : "Explicit synthetic fixture",
            ["admissionSyntheticCalls"] = admissionCalls, ["nominalCallsThroughAdmission"] = PlanningDecisionPages.PackedPageCount(state, interpretation) + admissionCalls,
            ["downstreamRelationshipCalls"] = client.Requests.Count - admissionCalls,
            ["downstreamRelationshipDecisions"] = fields.Count(k => k.StartsWith("relation_", StringComparison.Ordinal)),
            ["completeNominalCalls"] = nominalCalls, ["nominalHeadroom"] = RuntimeAdmissionDiagnosticRules.MaxCalls - nominalCalls,
            ["exclusions"] = new JsonObject(PlanningOperations.DeclarationExclusions(state).Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                new JsonArray(p.Value.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())))),
            ["checkpointFingerprint"] = PlanningGraphCompiler.Fingerprint(captured.Value),
            ["interpretationDecisions"] = interpretation.Length,
            ["interpretationInitialPages"] = PlanningDecisionPages.PackedPageCount(state, interpretation),
            ["independentContributionStatusDecisions"] = 0,
            ["canonicalContributionDecisions"] = qualifications.Length,
            ["fixtureAcceptancePassed"] = acceptanceFinding is null, ["fixtureAcceptanceFinding"] = acceptanceFinding,
            ["jointClauseQualification"] = jointClause, ["nestedQualifierFixture"] = nestedQualifiers,
            ["nestedQualifiers"] = PlanningOperations.ReadContributions(state).Sum(p => p.Units.Count(u => u.ParentRequestUnitId is not null)),
            ["eligibleRuntimeRecords"] = PlanningOperations.Scopes(state).Length,
            ["clauseMembership"] = new JsonArray(qualifications.Select(d => (JsonNode)new JsonObject { ["decision"] = d.Id, ["members"] = d.Context["provenance"]!.AsObject().Count }).ToArray()),
            ["semanticUnits"] = PlanningOperations.ReadContributions(state).Where(p => p.DecisionId is not null).Sum(p => p.Units.Count),
            ["executionRequestUnits"] = PlanningOperations.ReadContributions(state).Where(p => p.DecisionId is not null).Sum(p => p.Units.Count(u => u.Role == "requested_execution")),
            ["contributionInitialPages"] = PlanningDecisionPages.PackedPageCount(state, qualifications),
            ["contributionSchemas"] = new JsonArray(qualifications.Select(d => (JsonNode)new JsonObject { ["decision"] = d.Id, ["schemaBytes"] = System.Text.Encoding.UTF8.GetByteCount(d.Schema.ToJsonString()) }).ToArray()),
            ["jointCoverageDecisions"] = groups.Length,
            ["coverageInitialPages"] = PlanningDecisionPages.PackedPageCount(state, groups.Select(g => g.Decision).ToArray()),
            ["assessedActionContributions"] = groups.Sum(g => g.Scopes.Count(s => s.Contribution!.Role == "supports")),
            ["jointGoverningContributions"] = groups.Sum(g => g.Scopes.Count(s => s.Contribution!.Role == "governs")),
            ["governingApplicabilityDecisions"] = fields.Count(k => k.StartsWith("applicability_", StringComparison.Ordinal)),
            ["applicabilityInitialPages"] = PlanningDecisionPages.PackedPageCount(state, PlanningOperations.ApplicabilityDecisions(state)),
            ["applicabilitySchemas"] = new JsonArray(PlanningOperations.ApplicabilityDecisions(state).Select(d => (JsonNode)new JsonObject
                { ["decision"] = d.Id, ["schemaBytes"] = System.Text.Encoding.UTF8.GetByteCount(d.Schema.ToJsonString()) }).ToArray()),
            ["contributions"] = JsonSerializer.SerializeToNode(PlanningOperations.ReadContributions(state).ToList(), PlanningJsonContext.Default.ListPlanningExecutionContributionProof),
            ["applicability"] = JsonSerializer.SerializeToNode(PlanningOperations.ReadApplicability(state).ToList(), PlanningJsonContext.Default.ListPlanningGoverningApplicabilityProof),
            ["identityDecisions"] = fields.Count(k => k.StartsWith("operation_", StringComparison.Ordinal)),
            ["dependencyDecisions"] = fields.Count(k => k.StartsWith("data_", StringComparison.Ordinal)),
            ["syntheticRequests"] = client.Requests.Count, ["newSyntheticReservations"] = state.RequestAccounting.Count - originalAccounting,
            ["actualTokens"] = null, ["readOnlyRestart"] = restart,
            ["coverageSchemas"] = new JsonArray(groups.Select(g => (JsonNode)new JsonObject { ["decision"] = g.Id,
                ["schemaBytes"] = System.Text.Encoding.UTF8.GetByteCount(g.Decision.Schema.ToJsonString()), ["mappingAlternatives"] = g.Plans.Count,
                ["selectedProof"] = PlanningOperations.ReadCoverage(state).Single(p => p.DecisionId == g.Id).ProofFingerprint }).ToArray()),
            ["requests"] = new JsonArray(client.Requests.Select(r => (JsonNode)new JsonObject { ["estimatedInput"] = PlanningJsonTransport.EstimateInputTokens(r.Prompt, r.StructuredOutputSchema!.AsObject()),
                ["decisions"] = new JsonArray(r.StructuredOutputSchema!["properties"]!.AsObject().Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray()) }).ToArray()),
            ["operations"] = new JsonArray(operations.Select(o => (JsonNode)new JsonObject { ["id"] = o.Id, ["kind"] = o.Kind, ["required"] = o.Required,
                ["admissionProof"] = o.OperationAdmission!.ProofFingerprint, ["coverageProof"] = o.OperationAdmission.RealizationCoverage!.ProofFingerprint,
                ["dependencyProof"] = o.OperationAdmission.Dependencies!.ProofFingerprint,
                ["inputs"] = new JsonArray(o.OperationAdmission.Assignments.SelectMany(a => a.Effect!.Inputs).Distinct().Select(r => (JsonNode)JsonValue.Create(inputs[r])!).ToArray()),
                ["outputs"] = new JsonArray(o.OperationAdmission.Assignments.SelectMany(a => a.Effect!.Outputs).Distinct().Select(r => (JsonNode)JsonValue.Create(inputs[r])!).ToArray()),
                ["supports"] = o.OperationAdmission.Assignments.Count(a => a.Disposition == "supports"), ["governing"] = o.OperationAdmission.Assignments.Count(a => a.Disposition == "attach"),
                ["producers"] = new JsonArray(o.OperationAdmission.Dependencies.Assignments.Where(a => a.Disposition == "data").Select(a => (JsonNode)new JsonObject { ["id"] = a.Producer, ["origin"] = a.Origin.ToString() }).ToArray()) }).ToArray())
        }.ToJsonString());
    }

    private sealed class CoverageFixtureClient(PlanningSnapshot state, IReadOnlyDictionary<string, JsonObject> semanticFixtures, bool nestedQualifiers) : ILLMClient, ILLMCapabilityResolver
    {
        public List<LLMRequest> Requests { get; } = [];
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["low"]);
        private static JsonObject SyntheticMapping(PlanningSnapshot snapshot, PlanningOperations.Scope scope)
        {
            // Frozen semantic expectations belong only to this labelled synthetic fixture.
            var e = scope.Evidence!;
            var description = PlanningChoiceEvidence.Text(snapshot, e.ClauseReference).Trim() == "This is deterministic, local, in-memory business processing.";
            var role = description || e.Kind == "external_read" && e.OccurrenceBoundary is null ? "governs" : "realizes";
            var names = e.Kind == "external_read" ? new[] { "sourceId" } : snapshot.Declarations.Any(d => PlanningDeclarations.Name(snapshot, d) == "sourceId") ? ["threshold"] : new[] { "record", "threshold" };
            return OperationEffectFixtures.Answer(snapshot, scope, contribution: role,
                inputs: snapshot.Declarations.Where(d => d.Direction == "input" && names.Contains(PlanningDeclarations.Name(snapshot, d))).Select(d => d.Id));
        }
        private JsonObject SyntheticQualification(PlanningSnapshot snapshot, PlanningOperations.Scope[] members)
        {
            // Complete-clause semantic answers are explicitly synthetic; no historical
            // fragment answer is interpreted as a clause-level request proof.
            var scope = members.OrderByDescending(s => snapshot.References.Single(r => r.Id == s.Evidence!.ActionReference).Length)
                .ThenBy(s => s.Evidence!.Id, StringComparer.Ordinal).First();
            var text = PlanningChoiceEvidence.Text(snapshot, scope.Clause.Id).Trim();
            if (text == "This is deterministic, local, in-memory business processing.")
                return new() { ["status"] = "qualified", ["units"] = new JsonArray((JsonNode)new JsonObject
                    { ["role"] = "governing_property", ["scope"] = scope.Clause.Id, ["evidence"] = scope.Evidence!.ActionReference }) };
            var answer = OperationEffectFixtures.ContributionAnswer(snapshot, scope, SyntheticMapping(snapshot, scope));
            if (text.StartsWith("Classify as rejected", StringComparison.Ordinal))
                answer["units"]![0]!["request"]!["predicate"] = new JsonObject { ["start"] = "b0", ["end"] = "b2" };
            if (nestedQualifiers && text == "Read the record identified by sourceId once from the external record store.")
                answer["units"]![0]!["qualifiers"] = new JsonArray((JsonNode)new JsonObject
                    { ["evidence"] = new JsonObject { ["start"] = "b6", ["end"] = "b7" } });
            return answer;
        }

        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request); var result = new JsonObject();
            foreach (var field in request.StructuredOutputSchema!["properties"]!.AsObject())
            {
                if (field.Key.StartsWith("contribution_", StringComparison.Ordinal))
                {
                    var members = PlanningOperations.Scopes(state).Where(s => PlanningOperations.ContributionDecisionId(s.Evidence!) == field.Key).ToArray();
                    result[field.Key] = semanticFixtures.TryGetValue(field.Key, out var retainedShape) ? retainedShape.DeepClone() : SyntheticQualification(state, members);
                }
                else if (field.Key.StartsWith("applicability_", StringComparison.Ordinal))
                {
                    var decision = PlanningOperations.ApplicabilityDecisions(state).Single(d => d.Id == field.Key);
                    var candidates = PlanningOperations.MaterializeCoverage(state);
                    var scope = PlanningOperations.QualifiedScopes(state).Single(s => PlanningOperations.ApplicabilityDecisionId(s) == field.Key);
                    var kind = scope.Evidence!.Kind == "external_read" ? "external_read" : "local_processing";
                    result[field.Key] = OperationEffectFixtures.ApplicabilityAnswer(decision, [candidates.Single(o => o.Kind == kind).Id]);
                }
                else if (field.Key.StartsWith("coverage_", StringComparison.Ordinal))
                {
                    var group = PlanningOperations.CoverageGroups(state).Single(g => g.Id == field.Key);
                    result[field.Key] = OperationEffectFixtures.CoverageAnswer(state, group, scope => SyntheticMapping(state, scope));
                }
                else if (field.Key.StartsWith("data_", StringComparison.Ordinal))
                {
                    var domain = PlanningOperations.DependencyDecisions(state, OperationEffectFixtures.Staged(state));
                    var decision = domain.Decisions.Single(d => d.Id == field.Key);
                    var producer = OperationEffectFixtures.Staged(state).Single(o => o.Id == decision.Context["producer"]!.ToString());
                    result[field.Key] = OperationEffectFixtures.DependencyAnswer(field.Value!.AsObject(), producer.Kind == "external_read");
                }
                else if (field.Key.StartsWith("relation_", StringComparison.Ordinal))
                {
                    RuntimeAdmissionDiagnosticRules.RequireRelationDomain(field.Value);
                    result[field.Key] = field.Value!["enum"]!.AsArray().Any(v => v!.ToString() == "policy") ? "policy" : "none";
                }
                else throw new InvalidOperationException("Unexpected synthetic decision: " + field.Key);
                var fieldSchema = field.Value!.DeepClone().AsObject();
                if (request.StructuredOutputSchema["$defs"] is { } definitions) fieldSchema["$defs"] = definitions.DeepClone();
                if (PlanningContractValidation.ValidateInstance(result[field.Key], fieldSchema).Count != 0)
                    throw new InvalidOperationException("Synthetic coverage did not satisfy the issued schema.");
            }
            return Task.FromResult(new LLMResponse { Json = result, CompletionStatus = "completed" });
        }
    }
}
