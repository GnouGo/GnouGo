using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

// Harness authority only. A new identity never changes a planner allowance or
// reopens a consumed historical campaign. The accepted report is build-bound.
internal sealed class MissionCampaign
{
    internal const string ValidatedProductionCommit = "e86ce7802f454577bb8c5f052f6d64f87ae12501";
    internal static MissionCampaign? Current { get; private set; }
    internal string Id { get; }
    internal string DiagnosticIdentity => Id + "-intent";
    internal string WorkflowIdentity => Id + "-workflows";
    internal string ProductionFingerprint { get; }
    internal string ValidationFingerprint { get; }
    internal JsonObject ProductionBinaries { get; }
    internal JsonArray AuthorizedCases => new("local", "mixed");

    internal MissionCampaign(string id, JsonObject validation)
    {
        if (!Regex.IsMatch(id, "^schema5-autonomous-[a-z0-9][a-z0-9-]{0,79}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Mission identities must use the schema5-autonomous- prefix and lowercase letters, digits or hyphens.");
        Id = id;
        ValidationFingerprint = PlanningGraphCompiler.Fingerprint(validation.ToJsonString());
        var hashes = validation["testedBinaryHashes"]?.AsObject() ?? throw new InvalidOperationException("Accepted binary evidence is missing.");
        ProductionBinaries = new JsonObject(hashes.Where(p => Path.GetFileName(p.Key) != "GnOuGo.Agent.Planning.Benchmark.dll")
            .OrderBy(p => Path.GetFileName(p.Key), StringComparer.Ordinal)
            .Select(p => new KeyValuePair<string, JsonNode?>(Path.GetFileName(p.Key), p.Value?.DeepClone())));
        if (ProductionBinaries.Count == 0 || ProductionBinaries.Any(p => p.Value is null || !Regex.IsMatch(p.Value.ToString(), "^[a-f0-9]{64}$")))
            throw new InvalidOperationException("Accepted production hashes are incomplete.");
        ProductionFingerprint = PlanningGraphCompiler.Fingerprint(ProductionBinaries.ToJsonString());
    }

    internal static string[] Select(string[] args)
    {
        var index = Array.IndexOf(args, "--campaign");
        if (index < 0) return args;
        if (index != args.Length - 2 || args.Count(a => a == "--campaign") != 1 ||
            args[0] is not ("diagnose-runtime-admission" or "campaign"))
            throw new ArgumentException("Append --campaign ID to a diagnostic or progressive campaign command.");
        using var stream = typeof(MissionCampaign).Assembly.GetManifestResourceStream("MissionProductionValidation")
            ?? throw new InvalidOperationException("The accepted production validation resource is missing.");
        using var reader = new StreamReader(stream);
        Current = new(args[index + 1], JsonNode.Parse(reader.ReadToEnd())!.AsObject());
        return args[..index];
    }

    internal JsonObject Definition() => new()
    {
        ["identity"] = Id, ["productionCommit"] = ValidatedProductionCommit,
        ["productionBinariesFingerprint"] = ProductionFingerprint,
        ["validationFingerprint"] = ValidationFingerprint,
        ["gates"] = new JsonArray("local", "mixed", "stage1", "stage2", "stage3")
    };

    internal void RequireDefinition(JsonObject manifest)
    {
        if (!JsonNode.DeepEquals(manifest["mission"], Definition()))
            throw new InvalidOperationException("The frozen mission identity, production or validation evidence changed.");
    }

    internal void RequireProduction(JsonObject binaries)
    {
        var actual = new JsonObject(binaries.Where(p => p.Key != "GnOuGo.Agent.Planning.Benchmark.dll")
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())));
        if (!JsonNode.DeepEquals(actual, ProductionBinaries))
            throw new InvalidOperationException("Production binaries differ from the accepted mission implementation.");
    }

    internal static void RequireProductionSources()
    {
        using var process = Process.Start(new ProcessStartInfo("git")
        {
            ArgumentList = { "diff", "--quiet", ValidatedProductionCommit, "--", "src", ":(exclude)src/**/README.md" },
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Cannot verify the production checkout.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Production sources differ from the accepted mission implementation.");
    }

    internal static void RequireAcceptedIntent(JsonObject? report, string name)
    {
        if (report?["case"]?.ToString() != name || report["status"]?.ToString() != "passed" ||
            report["firstBlocker"] is not null || report["admissionCommitted"]?.GetValue<bool>() != true ||
            string.IsNullOrEmpty(report["admissionFingerprint"]?.ToString()) ||
            report["journalReservationsWithoutReceipt"]?.GetValue<int>() != 0 ||
            report["coordinatorReservationsWithoutJournalRequest"]?.GetValue<int>() != 0 ||
            report["readOnlyRestart"] is not JsonObject restart || restart["passed"]?.GetValue<bool>() != true ||
            restart["providerCalls"]?.GetValue<int>() != 0 || restart["checkpointWrites"]?.GetValue<int>() != 0 ||
            restart["admissionFingerprint"]?.ToString() != report["admissionFingerprint"]?.ToString() ||
            !JsonNode.DeepEquals(restart["executionRequestProofVersions"], new JsonArray(1)) ||
            !JsonNode.DeepEquals(restart["contributionProofVersions"], new JsonArray(8)) ||
            !JsonNode.DeepEquals(restart["admissionProofVersions"], new JsonArray(18)) ||
            name == "mixed" && report["relationshipProjectionVerified"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("The previous Intent gate lacks current, complete live acceptance and read-only restart evidence.");
    }

    internal static void RequireApprovedWorkflow(JsonObject previous)
    {
        if (previous["status"]?.ToString() != "passed" || previous["outcome"]?.ToString() != "valid_workflow" ||
            string.IsNullOrEmpty(previous["approvedArtifactHash"]?.ToString()) ||
            previous["readOnlyRestart"] is not JsonObject restart || restart["passed"]?.GetValue<bool>() != true ||
            restart["providerCalls"]?.GetValue<int>() != 0 || restart["checkpointWrites"]?.GetValue<int>() != 0 ||
            restart["artifactHash"]?.ToString() != previous["approvedArtifactHash"]?.ToString())
            throw new InvalidOperationException("The previous workflow lacks exact approval and read-only encrypted restart evidence.");
    }

    // A checkpoint is the durable gate start. Only an entirely empty identity
    // may create one; interrupted work keeps its existing request allowances.
    internal static string RequireEntry(bool checkpoint, bool report, bool budget, int journalCalls, long? budgetCalls, int missingReceipts, int? coordinatorCalls = null)
    {
        if (!checkpoint && (report || budget || journalCalls != 0) || journalCalls != (budgetCalls ?? 0) ||
            journalCalls > 0 && !budget || missingReceipts != 0 || coordinatorCalls is not null && coordinatorCalls != journalCalls)
            throw new InvalidOperationException("Mission gate state is incomplete or dispatch accounting is ambiguous; its start allowance cannot be reacquired.");
        return report ? "completed" : checkpoint ? "resume" : "new";
    }

    internal static string StageEntry(JsonObject stage) => stage["status"]?.ToString() switch
    {
        "not_run" when stage["session"] is null && stage["name"] is null => "new",
        "passed" or "blocked" when stage["session"] is not null => "completed",
        "starting" when stage["name"] is not null => "resume",
        "running" or "waiting" or "behavior_accepted" when stage["session"] is not null => "resume",
        _ => throw new InvalidOperationException("The progressive gate has inconsistent start ownership.")
    };
}
