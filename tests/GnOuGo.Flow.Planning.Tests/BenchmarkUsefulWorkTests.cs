using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BenchmarkUsefulWorkTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void FreshCohortConfigurationMatchesSessionCreationWhileRetainedPresentationIsUnchanged(int retainedVersion)
    {
        var state = new PlanningSession();
        PlanningBenchmarkMeasurements.ConfigureRequest(state.Request, [], retained: false);
        var frozen = PlanningBenchmarkMeasurements.Configuration(state.Request);
        PlanningBenchmarkMeasurements.CaptureConstructionSettings(state);
        PlanningSessionCreation.Capture(state);
        Assert.Equal(8, state.Construction!.Compact!.PresentationVersion);
        Assert.True(JsonNode.DeepEquals(frozen, PlanningBenchmarkMeasurements.Configuration(state.Request)));

        frozen["construction_presentation_version"] = retainedVersion;
        var keys = frozen.Select(item => item.Key).ToArray();
        var retained = PlanningBenchmarkMeasurements.RestoreConfiguration(frozen);
        PlanningBenchmarkMeasurements.ConfigureRequest(retained, [], retained: true);
        Assert.True(JsonNode.DeepEquals(frozen, PlanningBenchmarkMeasurements.Configuration(retained)));
        var restored = new PlanningSession { Request = retained };
        PlanningBenchmarkMeasurements.CaptureConstructionSettings(restored);
        Assert.Equal(retainedVersion, restored.Construction!.Compact!.PresentationVersion);
        Assert.Equal(keys, PlanningBenchmarkMeasurements.Configuration(restored.Request).Select(item => item.Key));
    }

    [Fact]
    public void DistinctFrozenObligationsIgnorePlannerGranularityAndDoNotMutateEvidence()
    {
        var measured = new JsonObject
        {
            ["logical_planning_calls"] = 18, ["physical_attempts"] = 20, ["input_tokens"] = 1200, ["output_tokens"] = 800,
            ["reasoning_tokens"] = 600, ["estimated_cost_eur"] = 0.1234567890123456789012345678m,
            ["planning_ms"] = 4000, ["tasks"] = 500, ["requirements"] = 100, ["checkpoints"] = 128
        };
        var original = measured.ToJsonString();
        var report = PlanningBenchmarkMeasurements.UsefulWork(measured, ["read", "write", "read"],
            ["write", "read", "write"], "artifact", runtimeAccepted: false);
        Assert.Equal(2, report["accepted_obligations"]!.GetValue<int>());
        Assert.Equal(9m, report["per_accepted_obligation"]!["logical_calls"]!.GetValue<decimal>());
        Assert.Equal(10m, report["per_accepted_obligation"]!["physical_attempts"]!.GetValue<decimal>());
        Assert.Equal(1000m, report["per_accepted_obligation"]!["provider_total_tokens"]!.GetValue<decimal>());
        Assert.Equal(0.0617283945061728394506172839m, report["per_accepted_obligation"]!["cost_eur"]!.GetValue<decimal>());
        Assert.Equal(2000m, report["per_accepted_obligation"]!["planning_ms"]!.GetValue<decimal>());
        Assert.False(report["runtime_accepted"]!.GetValue<bool>());
        Assert.Equal(original, measured.ToJsonString());
        var same = PlanningBenchmarkMeasurements.UsefulWork(measured, ["write", "read"], ["read", "write"], "artifact", true);
        Assert.Equal(report["obligation_set_hash"]!.ToString(), same["obligation_set_hash"]!.ToString());
        Assert.True(JsonNode.DeepEquals(report["per_accepted_obligation"], same["per_accepted_obligation"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroOrUnknownArtifactCoverageNeverProducesEfficiency(bool unknown)
    {
        var report = PlanningBenchmarkMeasurements.UsefulWork(new() { ["calls"] = 1, ["estimated_cost_eur"] = 1m },
            ["read"], unknown ? null : [], null, runtimeAccepted: null);
        Assert.Equal(unknown ? null : 0, report["accepted_obligations"]?.GetValue<int>());
        Assert.All(report["per_accepted_obligation"]!.AsObject(), value => Assert.Null(value.Value));
        Assert.Null(report["runtime_accepted"]);
    }

    [Fact]
    public void UnknownUsageRemainsUnknownAfterArtifactAcceptance()
    {
        var report = PlanningBenchmarkMeasurements.UsefulWork(new() { ["logical_planning_calls"] = 3, ["known_cost_eur"] = 0.5m,
            ["known_input_tokens"] = 20, ["output_tokens"] = 10 }, ["read", "write"], ["read"], "artifact", null);
        Assert.Equal(1, report["accepted_obligations"]!.GetValue<int>());
        Assert.Equal(3m, report["per_accepted_obligation"]!["logical_calls"]!.GetValue<decimal>());
        Assert.Null(report["per_accepted_obligation"]!["physical_attempts"]);
        Assert.Equal(10m, report["per_accepted_obligation"]!["provider_output_tokens"]!.GetValue<decimal>());
        Assert.Null(report["per_accepted_obligation"]!["provider_input_tokens"]);
        Assert.Null(report["per_accepted_obligation"]!["provider_total_tokens"]);
        Assert.Null(report["per_accepted_obligation"]!["cost_eur"]);
        Assert.Null(report["per_accepted_obligation"]!["planning_ms"]);
    }

    [Fact]
    public void PhysicalAttemptsRequireCompleteTransportEvidenceRatherThanLogicalReservations()
    {
        var run = new JsonObject { ["usage_receipts"] = new JsonObject
        {
            ["first"] = new JsonObject { ["transport_attempts"] = 2 },
            ["second"] = new JsonObject { ["transport_attempts"] = 0 }
        } };
        Assert.Equal(2L, PlanningBenchmarkMeasurements.PhysicalAttempts(run, 2));
        Assert.Null(PlanningBenchmarkMeasurements.PhysicalAttempts(run, 3));
        run["usage_receipts"]!["first"]!.AsObject().Remove("transport_attempts");
        Assert.Null(PlanningBenchmarkMeasurements.PhysicalAttempts(run, 2));
    }

    [Fact]
    public void UnfrozenOrUnboundCoverageCannotReceiveCredit()
    {
        Assert.Throws<ArgumentException>(() => PlanningBenchmarkMeasurements.UsefulWork(new(), ["read"], ["invented"], "artifact", true));
        Assert.Throws<ArgumentException>(() => PlanningBenchmarkMeasurements.UsefulWork(new(), ["read"], ["read"], null, true));
        Assert.Throws<ArgumentException>(() => PlanningBenchmarkMeasurements.UsefulWork(new(), [""], [], null, null));
    }
}
