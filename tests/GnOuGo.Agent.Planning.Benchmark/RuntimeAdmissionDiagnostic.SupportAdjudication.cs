using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    // This is an immutable-evidence adjudication, not another campaign entrypoint.
    // No transport factory, index, journal, budget scope or persistence writer is created.
    internal static async Task AdjudicateRetainedLocalAsync(string id, IKeyVaultRecordStore store)
    {
        RetainedDiagnosticRecords.RequireCase(id);
        var records = new RetainedDiagnosticRecords(store);
        var ct = CancellationToken.None;
        async Task<string> Integrity()
        {
            var entries = new List<string>();
            foreach (var tenant in new[] { ProgressiveCampaign.Tenant, Tenant })
            foreach (var collection in new[] { ProgressiveCampaign.CampaignCollection, Collection, EfPlanningSessionStore.Collection,
                PlanningModelJournal.RequestCollection, PlanningModelJournal.Collection, PlanningBudgetSink.Collection })
            foreach (var item in (await records.ListAsync(collection, tenant, Author, ct)).OrderBy(r => r.Key, StringComparer.Ordinal))
                entries.Add(tenant + ":" + collection + ":" + item.Key + ":" + item.CreatedAt.ToString("O") + ":" + item.UpdatedAt.ToString("O") + ":" + PlanningGraphCompiler.Fingerprint(item.Value));
            return PlanningGraphCompiler.Fingerprint(string.Join('|', entries));
        }
        var before = await Integrity();
        var checkpoint = await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author, ct) ?? throw new InvalidOperationException("Missing retained checkpoint.");
        var original = await records.GetAsync(Collection, Tenant, id + ":report", Author, ct) ?? throw new InvalidOperationException("Missing original stopped report.");
        var budget = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author, ct) ?? throw new InvalidOperationException("Missing retained accounting.");
        var manifest = JsonNode.Parse((await records.GetAsync(Collection, Tenant, id[..id.LastIndexOf(':')], Author, ct))!.Value)!;
        if (manifest["commit"]!.ToString() != "23bce4bc842e598d19866d92d497772e1f3c0538") throw new InvalidOperationException("Wrong frozen production.");
        foreach (var binary in manifest["binaries"]!.AsObject().Where(p => p.Key != "GnOuGo.Agent.Planning.Benchmark.dll"))
            if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, binary.Key)))) != binary.Value!.ToString())
                throw new InvalidOperationException("Frozen production binary drift.");
        var state = JsonSerializer.Deserialize(JsonNode.Parse(checkpoint.Value)!["snapshot"], PlanningJsonContext.Default.PlanningSnapshot)!;
        if (PlanningGraphCompiler.Fingerprint(checkpoint.Value) != "0653605dfb1bca071c19dfcb490b9b9ee1b762c0ff859ad20a868ecff8b3b530" ||
            state.Request.SessionId != id || state.Request.Prompt != ProgressiveScenarios.Simple ||
            JsonNode.Parse(original.Value)!["firstBlocker"]?.ToString() != "DIAGNOSTIC_APPLICABILITY")
            throw new InvalidOperationException("The reviewed immutable evidence does not match this adjudication.");
        var originalSnapshot = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        var receiptChecks = new JsonArray();
        var answers = new Dictionary<string, (JsonObject Answer, string Request, string Receipt)>(StringComparer.Ordinal);
        foreach (var call in state.RequestAccounting.DistinctBy(c => c.Id))
        {
            var captured = await records.GetAsync(PlanningModelJournal.RequestCollection, Tenant, id + ":" + call.Id, EfPlanningSessionStore.Author, ct)
                ?? throw new InvalidOperationException("Missing original request; no substitute is permitted.");
            var completed = await records.GetAsync(PlanningModelJournal.Collection, Tenant, id + ":" + call.Id, EfPlanningSessionStore.Author, ct)
                ?? throw new InvalidOperationException("Missing receipt; no provider dispatch is permitted.");
            var request = JsonSerializer.Deserialize(captured.Value, PlanningJsonContext.Default.LLMRequest)!;
            var response = JsonSerializer.Deserialize(completed.Value, PlanningJsonContext.Default.LLMResponse)!;
            var candidate = response.Json ?? JsonNode.Parse(response.Text!);
            if (candidate is not JsonObject body || PlanningContractValidation.ValidateInstance(body, request.StructuredOutputSchema!).Count != 0)
                throw new InvalidOperationException("An original qualification/interpretation response is not schema valid.");
            var requestHash = PlanningGraphCompiler.Fingerprint(captured.Value); var receiptHash = PlanningGraphCompiler.Fingerprint(completed.Value);
            foreach (var answer in body)
                if (answer.Key.StartsWith("contribution_", StringComparison.Ordinal))
                    answers[answer.Key] = (answer.Value!.AsObject(), requestHash, receiptHash);
            receiptChecks.Add(new JsonObject { ["requestId"] = call.Id, ["requestFingerprint"] = requestHash,
                ["receiptFingerprint"] = receiptHash, ["schemaFindings"] = 0 });
        }
        PlanningOperations.RequireCurrent(state);
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray();
        CheckEffectFixture("local", state, operations);
        CheckApplicabilitySequence(state, operations);
        var proofs = PlanningOperations.ReadContributions(state);
        foreach (var proof in proofs.Where(p => p.DecisionId is not null))
        {
            var scope = PlanningOperations.Scopes(state).Single(s => s.Evidence!.Id == proof.RuntimeEvidenceId);
            var replayed = PlanningOperations.ParseContributions(state, scope, answers[proof.DecisionId!].Answer);
            if (JsonSerializer.Serialize(replayed, PlanningJsonContext.Default.PlanningExecutionContributionProof) !=
                JsonSerializer.Serialize(proof, PlanningJsonContext.Default.PlanningExecutionContributionProof))
                throw new InvalidOperationException("Current contribution proof differs from its original answer.");
        }
        var reviews = ReviewRetainedSupports(state, operations.Single(), answers);
        var restored = JsonSerializer.Deserialize(originalSnapshot, PlanningJsonContext.Default.PlanningSnapshot)!;
        var restart = await VerifyReadOnlyRestartAsync(state, restored, ct);
        if (JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot) != originalSnapshot)
            throw new InvalidOperationException("Read-only acceptance changed the detached snapshot.");
        var after = await Integrity();
        if (before != after || records.WritesAttempted != 0) throw new InvalidOperationException("Archived evidence or accounting changed.");
        var originalReport = JsonNode.Parse(original.Value)!;
        var report = new JsonObject
        {
            ["outcome"] = "RETAINED LOCAL ACCEPTED", ["classification"] = "HARNESS-ONLY acceptance defect",
            ["identity"] = id, ["productionCommit"] = manifest["commit"]!.DeepClone(),
            ["originalStatus"] = originalReport["status"]!.DeepClone(), ["originalBlocker"] = originalReport["firstBlocker"]!.DeepClone(),
            ["originalReportFingerprint"] = PlanningGraphCompiler.Fingerprint(original.Value), ["checkpointFingerprint"] = PlanningGraphCompiler.Fingerprint(checkpoint.Value),
            ["budgetFingerprint"] = PlanningGraphCompiler.Fingerprint(budget.Value), ["archiveFingerprintBefore"] = before, ["archiveFingerprintAfter"] = after,
            ["providerCalls"] = 0, ["checkpointWrites"] = 0, ["recordWrites"] = records.WritesAttempted, ["newReservations"] = 0,
            ["freshLocalStarts"] = 0, ["mixed"] = "not_run", ["stage1"] = "not_run",
            ["supportReviews"] = reviews, ["receiptChecks"] = receiptChecks, ["readOnlyRestart"] = restart,
            ["snapshotFingerprintBefore"] = PlanningGraphCompiler.Fingerprint(originalSnapshot),
            ["snapshotFingerprintAfter"] = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSnapshot)),
            ["canonicalResult"] = new JsonObject
            {
                ["operationId"] = operations.Single().Id, ["kind"] = operations.Single().Kind, ["required"] = operations.Single().Required,
                ["inputs"] = new JsonArray(operations.Single().OperationAdmission!.Assignments.SelectMany(a => a.Effect!.Inputs)
                    .Distinct().Order(StringComparer.Ordinal).Select(r => (JsonNode?)new JsonObject
                    { ["id"] = r, ["name"] = PlanningDeclarations.Name(state, state.Declarations.Single(d => d.Id == r)) }).ToArray()),
                ["outputs"] = new JsonArray(operations.Single().OperationAdmission!.Assignments.SelectMany(a => a.Effect!.Outputs)
                    .Distinct().Order(StringComparer.Ordinal).Select(r => (JsonNode?)new JsonObject
                    { ["id"] = r, ["name"] = PlanningDeclarations.Name(state, state.Declarations.Single(d => d.Id == r)) }).ToArray()),
                ["operationDependencyEdges"] = operations.Single().OperationAdmission!.Dependencies!.Assignments.Count(a => a.Disposition == "data"),
                ["supportCount"] = reviews.Count,
                ["governingCount"] = operations.Single().OperationAdmission!.Assignments.Count(a => a.Disposition == "attach")
            },
            ["preliminaryEvidenceRoleAuthority"] = "none; frozen current contribution validators grant support authority",
            ["originalLiveDecisionClasses"] = originalReport["decisionClasses"]!.DeepClone(),
            ["originalLiveUsage"] = new JsonObject { ["calls"] = originalReport["modelCalls"]!.DeepClone(),
                ["inputTokens"] = originalReport["inputTokens"]!.DeepClone(), ["outputTokens"] = originalReport["outputTokens"]!.DeepClone(),
                ["reasoningTokens"] = originalReport["reasoningTokens"]!.DeepClone(), ["remainingCallBudget"] = originalReport["remainingCallBudget"]!.DeepClone() },
            ["acceptance"] = "At least one valid positive support for the single classifier; every support is qualified and effect-bound; properties provide no support. Necessity, public dataflow, dependency authority and current proofs validated unchanged.",
            ["nextGate"] = "one fresh MIXED, requiring separate authorization"
        };
        AddContributionReport(report, state);
        Console.WriteLine(report.ToJsonString());
    }

    // A recorded semantic review of this exact retained fixture, not a keyword
    // classifier or new production authority. Different evidence requires review.
    private static JsonArray ReviewRetainedSupports(PlanningSnapshot state, PlanningObligation operation,
        IReadOnlyDictionary<string, (JsonObject Answer, string Request, string Receipt)> answers)
    {
        var reviewed = new Dictionary<int, (int Length, string Text, string Reason)>
        {
            [29] = (28, "classifying a single record.", "The complete request asks for a workflow performing classification of one record; this span specifies that requested work."),
            [325] = (20, "Classify as rejected", "The imperative requests production of the rejected classification under the attached false-approval condition."),
            [370] = (4, "high", "In the complete classification clause, high inherits Classify as and requests the high result under the attached approval and threshold condition; it is not an isolated domain label."),
            [420] = (12, "and standard", "The coordinated phrase inherits Classify as and requests the standard result under the separately attached otherwise fallback.")
        };
        var admission = operation.OperationAdmission!;
        var supports = admission.Assignments.Where(a => a.Disposition == "supports").OrderBy(a => state.References.Single(r => r.Id == a.ActionReference).Start).ToArray();
        if (supports.Length != reviewed.Count) throw new InvalidOperationException("Retained review evidence changed; no automatic semantic acceptance.");
        var result = new JsonArray();
        foreach (var assignment in supports)
        {
            var reference = state.References.Single(r => r.Id == assignment.ActionReference);
            var proof = admission.ExecutionContributions.Single(p => p.RuntimeEvidenceId == assignment.RuntimeEvidenceId);
            var contribution = proof.Contributions.Single(c => c.Id == assignment.ContributionId);
            if (!reviewed.Remove(reference.Start, out var review) || reference.SourceId != "request" || reference.Length != review.Length ||
                PlanningChoiceEvidence.Text(state, reference.Id) != review.Text || contribution.Basis != "requested_result_production" ||
                contribution.EffectId != operation.Id || contribution.Role != "supports" ||
                PlanningChoiceEvidence.Parent(state, reference.Id).Id != assignment.ClauseReference)
                throw new InvalidOperationException("Retained support does not match its reviewed owned result-production evidence.");
            var answer = answers[proof.DecisionId!];
            result.Add(new JsonObject { ["contributionId"] = contribution.Id, ["ownedReference"] = reference.Id,
                ["start"] = reference.Start, ["length"] = reference.Length, ["text"] = review.Text,
                ["completeClause"] = PlanningChoiceEvidence.Text(state, assignment.ClauseReference), ["semanticReview"] = review.Reason,
                ["semanticallyValid"] = true, ["basis"] = contribution.Basis, ["effectId"] = contribution.EffectId,
                ["ownerReference"] = contribution.OwnerReference, ["boundaryReference"] = contribution.BoundaryReference,
                ["origin"] = contribution.Origin.ToString(), ["proofFingerprint"] = proof.ProofFingerprint,
                ["requestFingerprint"] = answer.Request, ["receiptFingerprint"] = answer.Receipt });
        }
        return result;
    }
}
