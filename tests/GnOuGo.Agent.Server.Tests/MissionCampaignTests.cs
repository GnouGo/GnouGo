using System.Text.Json.Nodes;
using GnOuGo.Agent.Planning.Benchmark;

namespace GnOuGo.Agent.Server.Tests;

public sealed class MissionCampaignTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void OrphanedCoordinatorOrJournalReservationCannotResume(int journal, int coordinator)
        => Assert.Throws<InvalidOperationException>(() => MissionCampaign.RequireEntry(true, false, true, journal, journal, 0, coordinator));

    [Theory]
    [InlineData("not_run", false, false, "new")]
    [InlineData("starting", false, true, "resume")]
    [InlineData("running", true, true, "resume")]
    [InlineData("behavior_accepted", true, true, "resume")]
    [InlineData("passed", true, true, "completed")]
    [InlineData("blocked", true, true, "completed")]
    [InlineData("not_run", true, true, null)]
    [InlineData("starting", false, false, null)]
    [InlineData("passed", false, true, null)]
    [InlineData("waiting", false, true, null)]
    public void ProgressiveReentryRetainsItsOriginalStart(string status, bool session, bool name, string? expected)
    {
        var stage = new JsonObject { ["status"] = status, ["session"] = session ? "owned" : null, ["name"] = name ? "stage" : null };
        var original = stage.ToJsonString();
        if (expected is null) Assert.Throws<InvalidOperationException>(() => MissionCampaign.StageEntry(stage));
        else Assert.Equal(expected, MissionCampaign.StageEntry(stage));
        Assert.Equal(original, stage.ToJsonString());
    }

    [Theory]
    [InlineData(false, false, false, 0, null, 0, "new")]
    [InlineData(true, false, false, 0, null, 0, "resume")]
    [InlineData(true, false, true, 2, 2L, 0, "resume")]
    [InlineData(true, true, true, 2, 2L, 0, "completed")]
    public void EntryReusesTheDurableStart(bool checkpoint, bool report, bool budget, int calls, long? budgetCalls, int missing, string expected)
        => Assert.Equal(expected, MissionCampaign.RequireEntry(checkpoint, report, budget, calls, budgetCalls, missing));

    [Theory]
    [InlineData(false, true, false, 0, null, 0)]
    [InlineData(false, false, true, 0, 0L, 0)]
    [InlineData(false, false, true, 1, 1L, 0)]
    [InlineData(true, false, false, 1, null, 0)]
    [InlineData(true, false, true, 2, 1L, 0)]
    [InlineData(true, false, true, 2, 2L, 1)]
    [InlineData(true, true, true, 2, 2L, 1)]
    public void AmbiguousEntryCannotAcquireAnotherAllowance(bool checkpoint, bool report, bool budget, int calls, long? budgetCalls, int missing)
        => Assert.Throws<InvalidOperationException>(() => MissionCampaign.RequireEntry(checkpoint, report, budget, calls, budgetCalls, missing));

    private static JsonObject Validation() => new() { ["testedBinaryHashes"] = new JsonObject
    { ["bin/GnOuGo.Flow.Core.dll"] = new string('a', 64), ["bin/GnOuGo.Flow.Planning.dll"] = new string('b', 64),
      ["bin/GnOuGo.Agent.Planning.Benchmark.dll"] = new string('c', 64) } };

    [Theory]
    [InlineData("schema5-fallback-ownership-diagnostics-1")]
    [InlineData("../schema5-autonomous-test")]
    [InlineData("schema5-autonomous-Test")]
    [InlineData("schema5-autonomous-")]
    public void MissionCannotSelectHistoricalOrUnsafeIdentity(string id)
        => Assert.Throws<ArgumentException>(() => new MissionCampaign(id, Validation()));

    [Fact]
    public void FrozenDefinitionRejectsDifferentCampaignOrValidation()
    {
        var mission = new MissionCampaign("schema5-autonomous-test", Validation());
        var manifest = new JsonObject { ["mission"] = mission.Definition() };
        mission.RequireDefinition(manifest);
        Assert.Throws<InvalidOperationException>(() => new MissionCampaign("schema5-autonomous-other", Validation()).RequireDefinition(manifest));
        var changed = Validation(); changed["testedBinaryHashes"]!["bin/GnOuGo.Flow.Core.dll"] = new string('d', 64);
        Assert.Throws<InvalidOperationException>(() => new MissionCampaign(mission.Id, changed).RequireDefinition(manifest));
        Assert.Throws<InvalidOperationException>(() => mission.RequireDefinition(new()));
    }

    [Fact]
    public void HarnessBuildCannotAuthorizeChangedProduction()
    {
        var mission = new MissionCampaign("schema5-autonomous-test", Validation());
        var binaries = mission.ProductionBinaries.DeepClone().AsObject();
        binaries["GnOuGo.Agent.Planning.Benchmark.dll"] = new string('d', 64);
        mission.RequireProduction(binaries);
        binaries["GnOuGo.Flow.Planning.dll"] = new string('e', 64);
        Assert.Throws<InvalidOperationException>(() => mission.RequireProduction(binaries));
        binaries.Remove("GnOuGo.Flow.Planning.dll");
        Assert.Throws<InvalidOperationException>(() => mission.RequireProduction(binaries));
    }

    private static JsonObject Accepted(string name) => new()
    {
        ["case"] = name, ["status"] = "passed", ["admissionCommitted"] = true,
        ["admissionFingerprint"] = "proof", ["journalReservationsWithoutReceipt"] = 0,
        ["coordinatorReservationsWithoutJournalRequest"] = 0, ["relationshipProjectionVerified"] = name == "mixed",
        ["readOnlyRestart"] = new JsonObject { ["passed"] = true, ["providerCalls"] = 0, ["checkpointWrites"] = 0,
            ["admissionFingerprint"] = "proof", ["executionRequestProofVersions"] = new JsonArray(1),
            ["contributionProofVersions"] = new JsonArray(8), ["admissionProofVersions"] = new JsonArray(18) }
    };

    [Theory]
    [InlineData("local")]
    [InlineData("mixed")]
    public void CurrentCompleteIntentCanUnlockTheNextGate(string name)
        => MissionCampaign.RequireAcceptedIntent(Accepted(name), name);

    [Theory]
    [InlineData("stopped")]
    [InlineData("receipt")]
    [InlineData("reservation")]
    [InlineData("calls")]
    [InlineData("writes")]
    [InlineData("fingerprint")]
    [InlineData("proof")]
    [InlineData("relations")]
    public void IncompleteOrStaleIntentCannotUnlockWorkflows(string defect)
    {
        var report = Accepted("mixed");
        if (defect == "stopped") report["status"] = "stopped";
        if (defect == "receipt") report["journalReservationsWithoutReceipt"] = 1;
        if (defect == "reservation") report["coordinatorReservationsWithoutJournalRequest"] = 1;
        if (defect == "calls") report["readOnlyRestart"]!["providerCalls"] = 1;
        if (defect == "writes") report["readOnlyRestart"]!["checkpointWrites"] = 1;
        if (defect == "fingerprint") report["readOnlyRestart"]!["admissionFingerprint"] = "stale";
        if (defect == "proof") report["readOnlyRestart"]!["admissionProofVersions"] = new JsonArray(16);
        if (defect == "relations") report["relationshipProjectionVerified"] = false;
        Assert.Throws<InvalidOperationException>(() => MissionCampaign.RequireAcceptedIntent(report, "mixed"));
        Assert.Throws<InvalidOperationException>(() => MissionCampaign.RequireAcceptedIntent(null, "local"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("hash")]
    [InlineData("writes")]
    [InlineData("calls")]
    [InlineData("unsupported")]
    [InlineData("missing")]
    public void WorkflowStageRequiresExactApprovalAndRestart(string defect)
    {
        var previous = new JsonObject { ["status"] = "passed", ["outcome"] = "valid_workflow", ["approvedArtifactHash"] = "approved",
            ["readOnlyRestart"] = new JsonObject { ["passed"] = true, ["providerCalls"] = 0, ["checkpointWrites"] = 0, ["artifactHash"] = "approved" } };
        if (defect == "hash") previous["readOnlyRestart"]!["artifactHash"] = "changed";
        if (defect == "writes") previous["readOnlyRestart"]!["checkpointWrites"] = 1;
        if (defect == "calls") previous["readOnlyRestart"]!["providerCalls"] = 1;
        if (defect == "unsupported") previous["outcome"] = "unsupported";
        if (defect == "missing") previous.Remove("readOnlyRestart");
        if (defect == "none") MissionCampaign.RequireApprovedWorkflow(previous);
        else Assert.Throws<InvalidOperationException>(() => MissionCampaign.RequireApprovedWorkflow(previous));
    }
}
