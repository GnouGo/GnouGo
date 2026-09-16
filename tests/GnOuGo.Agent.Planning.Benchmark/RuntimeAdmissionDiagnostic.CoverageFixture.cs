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
        var groups = PlanningOperations.CoverageGroups(state);
        if (groups.Length != (name == "mixed" ? 2 : 1)) throw new InvalidOperationException("Captured coverage groups changed.");
        var interpretation = PlanningSourceDecisions.InterpretationDecisions(state);
        var client = new CoverageFixtureClient(state);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = client, LLMCapabilities = client }, (_, _) => Task.CompletedTask);
        await PlanningOperations.ResolveAsync(state, runtime, CancellationToken.None);
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
            ["jointCoverageDecisions"] = groups.Length,
            ["coverageInitialPages"] = PlanningDecisionPages.PackedPageCount(state, groups.Select(g => g.Decision).ToArray()),
            ["assessedActionContributions"] = groups.Sum(g => g.Scopes.Count(s => s.Evidence!.EvidenceRole == "action")),
            ["jointGoverningContributions"] = groups.Sum(g => g.Scopes.Count(s => s.Evidence!.EvidenceRole == "governing")),
            ["additionalGoverningDecisions"] = fields.Count(k => k.StartsWith("effect_governing_", StringComparison.Ordinal)),
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
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request); var result = new JsonObject();
            foreach (var field in request.StructuredOutputSchema!["properties"]!.AsObject())
            {
                if (field.Key.StartsWith("coverage_", StringComparison.Ordinal))
                {
                    var group = PlanningOperations.CoverageGroups(state).Single(g => g.Id == field.Key);
                    result[field.Key] = OperationEffectFixtures.CoverageAnswer(state, group, scope =>
                    {
                        var e = scope.Evidence!;
                        var description = PlanningChoiceEvidence.Text(state, e.ClauseReference).Trim() == "This is deterministic, local, in-memory business processing.";
                        var role = e.EvidenceRole == "governing" || description || e.Kind == "external_read" && e.OccurrenceBoundary is null ? "governs" : "realizes";
                        var names = e.Kind == "external_read" ? new[] { "sourceId" } : state.Declarations.Any(d => PlanningDeclarations.Name(state, d) == "sourceId") ? ["threshold"] : new[] { "record", "threshold" };
                        return OperationEffectFixtures.Answer(state, scope, contribution: role,
                            inputs: state.Declarations.Where(d => d.Direction == "input" && names.Contains(PlanningDeclarations.Name(state, d))).Select(d => d.Id));
                    });
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
