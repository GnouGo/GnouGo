using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    private static async Task<JsonObject> VerifyReadOnlyRestartAsync(PlanningSnapshot expected, PlanningSnapshot restored, CancellationToken ct)
    {
        static string Fingerprint(PlanningSnapshot state) => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot));
        var before = Fingerprint(restored);
        if (expected.OperationAdmissionFingerprint is null || before != Fingerprint(expected))
            throw new InvalidOperationException("The durable checkpoint does not contain the complete committed admission.");
        var transport = new RejectRestartDispatch();
        var checkpoints = 0;
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = transport }, (_, _) =>
        { checkpoints++; throw new InvalidOperationException("Read-only restart attempted a checkpoint write."); });
        PlanningOperations.RequireCurrent(restored);
        await PlanningOperations.ResolveAsync(restored, runtime, ct);
        if (transport.Calls != 0 || checkpoints != 0 || before != Fingerprint(restored))
            throw new InvalidOperationException("Read-only restart changed proof or accounting.");
        return new() { ["passed"] = true, ["providerCalls"] = transport.Calls, ["checkpointWrites"] = checkpoints,
            ["snapshotFingerprint"] = before, ["admissionFingerprint"] = restored.OperationAdmissionFingerprint,
            ["executionRequestProofVersions"] = new JsonArray(PlanningOperations.ReadExecutionRequests(restored).Select(p => p.Version)
                .Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["executionRequestFingerprints"] = new JsonArray(PlanningOperations.ReadExecutionRequests(restored)
                .Select(p => (JsonNode?)JsonValue.Create(p.ProofFingerprint)).ToArray()),
            ["operationIds"] = new JsonArray(restored.Obligations.Where(o => o.OperationAdmission is not null)
                .OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => (JsonNode?)JsonValue.Create(o.Id)).ToArray()),
            ["contributionProofVersions"] = new JsonArray(PlanningOperations.ReadContributions(restored).Select(p => p.Version)
                .Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["effectProofVersions"] = new JsonArray(restored.Obligations.Where(o => o.OperationAdmission is not null)
                .SelectMany(o => o.OperationAdmission!.Assignments).Select(a => a.Effect!.Version).Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["admissionProofVersions"] = new JsonArray(restored.Obligations.Where(o => o.OperationAdmission is not null)
                .Select(o => o.OperationAdmission!.Version).Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["applicabilityProofVersions"] = new JsonArray(PlanningOperations.ReadApplicability(restored).Select(p => p.Version).Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["applicabilityFingerprints"] = new JsonArray(PlanningOperations.ReadApplicability(restored).Select(p => (JsonNode?)JsonValue.Create(p.ProofFingerprint)).ToArray()),
            ["contributionFingerprints"] = new JsonArray(PlanningOperations.ReadContributions(restored).Select(p => (JsonNode?)JsonValue.Create(p.ProofFingerprint)).ToArray()),
            ["coverageProofVersions"] = new JsonArray(restored.Obligations.Where(o => o.OperationAdmission is not null)
                .Select(o => o.OperationAdmission!.RealizationCoverage!.Version).Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["coverageFingerprints"] = new JsonArray(restored.Obligations.Where(o => o.OperationAdmission is not null)
                .OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => (JsonNode?)JsonValue.Create(o.OperationAdmission!.RealizationCoverage!.ProofFingerprint)).ToArray()),
            ["dependencyFingerprints"] = new JsonArray(restored.Obligations.Where(o => o.OperationAdmission is not null)
                .OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => (JsonNode?)JsonValue.Create(o.OperationAdmission!.Dependencies!.ProofFingerprint)).ToArray()),
            ["dependencyProofVersions"] = new JsonArray(restored.Obligations.Where(o => o.OperationAdmission is not null)
                .Select(o => o.OperationAdmission!.Dependencies!.Version).Distinct().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    }

    private sealed class RejectRestartDispatch : ILLMClient
    {
        internal int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("Read-only restart attempted provider dispatch."); }
    }
}
