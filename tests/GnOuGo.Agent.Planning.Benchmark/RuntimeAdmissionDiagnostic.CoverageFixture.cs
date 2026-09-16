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
    internal static async Task CoverageFixtureAsync(string id, IKeyVaultRecordStore records)
    {
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
        var client = new CoverageFixtureClient(state);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = client, LLMCapabilities = client }, (_, _) => Task.CompletedTask);
        await PlanningOperations.ResolveAsync(state, runtime, CancellationToken.None);
        var groups = PlanningOperations.CoverageGroups(state);
        if (groups.Length != (name == "mixed" ? 2 : 1)) throw new InvalidOperationException("Captured coverage groups changed.");
        PlanningOperations.RequireCurrent(state); PlanningOperations.RequireExecutableIntent(state);
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray();
        CheckEffectFixture(name, state, operations);
        var inputs = state.Declarations.ToDictionary(d => d.Id, d => PlanningDeclarations.Name(state, d));
        var local = operations.Single(o => o.Kind == "local_processing");
        var localInputs = local.OperationAdmission!.Assignments.SelectMany(a => a.Effect!.Inputs).Distinct().Select(r => inputs[r]).Order().ToArray();
        if (!localInputs.SequenceEqual(name == "mixed" ? ["threshold"] : new[] { "record", "threshold" })) throw new InvalidOperationException("Local public input evidence changed.");
        var before = client.Requests.Count; var restart = await VerifyReadOnlyRestartAsync(state, PlanningContext.Clone(state), CancellationToken.None);
        if (before != client.Requests.Count) throw new InvalidOperationException("Restart dispatched.");
        if ((await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author))?.Value != captured.Value ||
            (await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author))?.Value != budget?.Value)
            throw new InvalidOperationException("Archive changed.");
        var fields = client.Requests.SelectMany(r => r.StructuredOutputSchema!["properties"]!.AsObject().Select(p => p.Key)).ToArray();
        Console.WriteLine(new JsonObject
        {
            ["evidence"] = "Explicit synthetic complete coverage and dependency answers on retained runtime/declaration fixtures; not historical receipts or live convergence.",
            ["sourceIdentity"] = id, ["archiveUnchanged"] = true, ["providerDispatches"] = 0,
            ["checkpointFingerprint"] = PlanningGraphCompiler.Fingerprint(captured.Value),
            ["interpretationDecisions"] = interpretation.Length,
            ["interpretationInitialPages"] = PlanningDecisionPages.PackedPageCount(state, interpretation),
            ["independentContributionStatusDecisions"] = 0,
            ["canonicalContributionDecisions"] = qualifications.Length,
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

    private sealed class CoverageFixtureClient(PlanningSnapshot state) : ILLMClient, ILLMCapabilityResolver
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
        private static JsonObject SyntheticQualification(PlanningSnapshot snapshot, PlanningOperations.Scope scope)
        {
            var local = !snapshot.Declarations.Any(d => PlanningDeclarations.Name(snapshot, d) == "sourceId");
            var text = PlanningChoiceEvidence.Text(snapshot, scope.Clause.Id);
            JsonObject Property(JsonNode reference) => new() { ["role"] = "governing_property", ["evidence"] = reference };
            JsonObject Qualified(params JsonObject[] items) => new() { ["status"] = "qualified", ["contributions"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()) };
            if (text.Trim() == "This is deterministic, local, in-memory business processing." || local && text.StartsWith("Classify as rejected", StringComparison.Ordinal))
                return Qualified(Property(JsonValue.Create(scope.Evidence!.ActionReference)!));
            var answer = OperationEffectFixtures.ContributionAnswer(snapshot, scope, SyntheticMapping(snapshot, scope));
            if (local && PlanningChoiceEvidence.Text(snapshot, scope.Evidence!.ActionReference!) == "classifying a single record.")
            {
                JsonObject Range(int start, int end)
                {
                    var starts = scope.Boundaries["properties"]!["start"]!["enum"]!.AsArray().Select(v => v!.ToString()).ToArray();
                    var ends = scope.Boundaries["properties"]!["end"]!["enum"]!.AsArray().Select(v => v!.ToString()).ToArray();
                    return new() { ["start"] = starts.Single(b => scope.Select(b, ends[^1]).Start == start),
                        ["end"] = ends.Single(b => { var r = scope.Select(starts[0], b); return r.Start + r.Length == end; }) };
                }
                var reference = snapshot.References.Single(r => r.Id == scope.Evidence!.ActionReference);
                var support = answer["contributions"]![0]!.AsObject();
                support["evidence"] = Range(reference.Start, reference.Start + "classifying".Length);
                answer["contributions"]!.AsArray().Add((JsonNode)Property(Range(reference.Start + "classifying ".Length, reference.Start + reference.Length)));
            }
            return answer;
        }

        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request); var result = new JsonObject();
            foreach (var field in request.StructuredOutputSchema!["properties"]!.AsObject())
            {
                if (field.Key.StartsWith("contribution_", StringComparison.Ordinal))
                {
                    var scope = PlanningOperations.Scopes(state).Single(s => PlanningOperations.ContributionDecisionId(s.Evidence!) == field.Key);
                    result[field.Key] = SyntheticQualification(state, scope);
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
                else throw new InvalidOperationException("Unexpected synthetic decision: " + field.Key);
                if (PlanningContractValidation.ValidateInstance(result[field.Key], field.Value!.AsObject()).Count != 0)
                    throw new InvalidOperationException("Synthetic coverage did not satisfy the issued schema.");
            }
            return Task.FromResult(new LLMResponse { Json = result, CompletionStatus = "completed" });
        }
    }
}
