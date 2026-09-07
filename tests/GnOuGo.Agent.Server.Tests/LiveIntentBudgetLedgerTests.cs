using System.Reflection;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LiveIntentBudgetLedgerTests
{
    [Fact]
    public async Task AuthorizedAmendmentPreservesUsageReservationsAndHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-live-amend-{Guid.NewGuid():N}.json");
        var definition = new LiveIntentAgentGenerationTests.LiveBudgetDefinition(new(100, "EUR"), new(0, "EUR"), MaxCalls: 120, ExistingConfiguration: true, PriorCostReserve: 50);
        try
        {
            var ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, definition);
            ledger.ReserveCall(30, "pending");
            await ledger.PersistAsync(ledger.Snapshot with { Calls = 120, EstimatedCost = 68 }, TestContext.Current.CancellationToken);
            var amended = definition with { AuthorizedBudget = new(150, "EUR"), MaxCalls = 240 };
            Assert.Throws<InvalidOperationException>(() => LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, amended));
            ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, amended, authorizeLimitAmendment: true);
            Assert.Equal(120, ledger.Snapshot.Calls); Assert.Equal(68, ledger.Snapshot.EstimatedCost); Assert.Equal(30, ledger.UnverifiedCostReserve);
            await ledger.PersistAsync(ledger.Snapshot, TestContext.Current.CancellationToken);
            Assert.Single(JsonNode.Parse(File.ReadAllText(path))!["budget_amendments"]!.AsArray());
            ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, amended, authorizeLimitAmendment: true);
            Assert.Single(JsonNode.Parse(File.ReadAllText(path))!["budget_amendments"]!.AsArray());
            Assert.Throws<InvalidOperationException>(() => LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, amended with { PriorCostReserve = 0 }, true));
            Assert.Throws<InvalidOperationException>(() => LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, definition, true));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TextReservationIncludesUnicodeSchemasAndToolContracts()
    {
        var request = new LLMRequest { Prompt = "é😀", StructuredOutputSchema = new JsonObject { ["description"] = "résultat" },
            Tools = [new() { Name = "renamed", Description = new string('x', 10_000) }] };
        var reservation = LiveIntentAgentGenerationTests.ConservativeTextInputReservation(request);
        Assert.True(reservation > 14_096);
        request.Tools = null;
        Assert.True(reservation - LiveIntentAgentGenerationTests.ConservativeTextInputReservation(request) >= 10_000);
    }

    [Fact]
    public async Task UnverifiedDispatchRetainsItsMaximumCostAcrossRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-live-budget-{Guid.NewGuid():N}.json");
        var definition = new LiveIntentAgentGenerationTests.LiveBudgetDefinition(new(100, "EUR"), new(0, "EUR"), ExistingConfiguration: true, PriorCostReserve: 50);
        try
        {
            var ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, definition);
            var reservation = ledger.ReserveCall(30);
            await ledger.PersistAsync(ledger.Snapshot with { Calls = 1 }, TestContext.Current.CancellationToken);
            ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, definition);
            Assert.Equal(30, ledger.UnverifiedCostReserve);
            Assert.Throws<InvalidOperationException>(() => ledger.ReserveCall(21));
            await ledger.PersistAsync(ledger.Snapshot with { EstimatedCost = 55 }, TestContext.Current.CancellationToken);
            ledger.CompleteCall(reservation);
            ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, definition);
            Assert.Equal(55, ledger.Snapshot.EstimatedCost); Assert.Equal(0, ledger.UnverifiedCostReserve);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ExistingConfigurationReserve_IsCumulativeAndCannotBeResetOnReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-live-budget-{Guid.NewGuid():N}.json");
        var definition = new LiveIntentAgentGenerationTests.LiveBudgetDefinition(new(100, "EUR"), new(0, "EUR"), ExistingConfiguration: true, PriorCostReserve: 50);
        try
        {
            var ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, definition);
            Assert.Equal(50, ledger.Snapshot.EstimatedCost);
            await ledger.PersistAsync(ledger.Snapshot with { Calls = 1, EstimatedCost = 51 }, TestContext.Current.CancellationToken);
            Assert.Equal(51, LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, definition).Snapshot.EstimatedCost);
            Assert.Throws<InvalidOperationException>(() => LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, definition with { PriorCostReserve = 0 }));
            Assert.Throws<InvalidOperationException>(() => LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, definition with { ExistingConfiguration = false }));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static readonly LiveIntentAgentGenerationTests.LiveBudgetDefinition BudgetDefinition = new(
        new MonetaryAmount(50m, "EUR"),
        new MonetaryAmount(50m, "EUR"));

    [Fact]
    public void ProgressTelemetry_PersistsOnlyExplicitPlannerSafeFields()
    {
        var entry = new JsonObject();
        var flowEvent = new SmartFlowEvent(
            "telemetry.span.event",
            new JsonObject
            {
                ["event.name"] = "gnougo-flow.plan.pipeline.main_assembly_candidate_rejected",
                ["attributes"] = new JsonObject
                {
                    ["gnougo-flow.plan.pipeline.candidate.fingerprint"] = "candidate-hash",
                    ["gnougo-flow.plan.pipeline.candidate.validation_progress"] = 50,
                    ["gen_ai.prompt"] = "sensitive prompt",
                    ["gnougo-flow.plan.pipeline.main_workflow_prompt"] = "sensitive workflow"
                }
            }.ToJsonString());
        var method = typeof(LiveIntentAgentGenerationTests).GetMethod(
            "AppendSanitizedPlannerTelemetry",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        method.Invoke(null, [entry, flowEvent]);

        Assert.Equal(
            "gnougo-flow.plan.pipeline.main_assembly_candidate_rejected",
            entry["planner_event"]!.GetValue<string>());
        Assert.Equal("candidate-hash", entry["gnougo-flow.plan.pipeline.candidate.fingerprint"]!.GetValue<string>());
        Assert.Equal(50, entry["gnougo-flow.plan.pipeline.candidate.validation_progress"]!.GetValue<int>());
        Assert.DoesNotContain("sensitive", entry.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ledger_PersistsOnlyRedactedBudgetAndPhaseState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-live-budget-{Guid.NewGuid():N}.json");
        try
        {
            var ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, BudgetDefinition);
            Assert.False(ledger.Exists);

            var snapshot = new LLMUsageBudgetSnapshot
            {
                StartedAtUtc = DateTimeOffset.UtcNow,
                Calls = 7,
                InputTokens = 101,
                OutputTokens = 23,
                TotalTokens = 124,
                EstimatedCost = 1.10m,
                EstimatedCostCurrency = "EUR",
                EstimatedCostUsd = 1.25m
            };
            await ledger.PersistAsync(snapshot, TestContext.Current.CancellationToken);
            ledger.MarkProbeCompleted(snapshot);
            ledger.MarkDiagnosticGenerationCompleted(snapshot);
            ledger.MarkFinalAcceptanceCompleted(snapshot);

            var reloaded = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, BudgetDefinition);
            Assert.True(reloaded.Exists);
            Assert.True(reloaded.ProbeCompleted);
            Assert.True(reloaded.DiagnosticGenerationCompleted);
            Assert.True(reloaded.FinalAcceptanceCompleted);
            Assert.Equal(snapshot, reloaded.Snapshot);

            var raw = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("prompt", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("model", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credential", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("response", raw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("provider_hard_limit_amount", raw, StringComparison.Ordinal);
            Assert.Contains("max_calls", raw, StringComparison.Ordinal);
            Assert.Contains("max_total_tokens", raw, StringComparison.Ordinal);

            reloaded.Delete();
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Ledger_MalformedContentFailsClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-live-budget-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{malformed", TestContext.Current.CancellationToken);

            var failure = Assert.Throws<InvalidOperationException>(() =>
                LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, BudgetDefinition));

            Assert.Contains("unreadable or malformed", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Ledger_RejectsChangedBudgetDefinition()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-live-budget-{Guid.NewGuid():N}.json");
        try
        {
            var ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, BudgetDefinition);
            await ledger.PersistAsync(ledger.Snapshot, TestContext.Current.CancellationToken);
            var changed = new LiveIntentAgentGenerationTests.LiveBudgetDefinition(
                new MonetaryAmount(49m, "EUR"),
                BudgetDefinition.ProviderHardLimit);

            var failure = Assert.Throws<InvalidOperationException>(() =>
                LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, changed));

            Assert.Contains("does not match", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Ledger_RejectsChangedCallOrTokenCeilings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-live-budget-{Guid.NewGuid():N}.json");
        try
        {
            var ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, BudgetDefinition);
            await ledger.PersistAsync(ledger.Snapshot, TestContext.Current.CancellationToken);

            var changedCalls = BudgetDefinition with { MaxCalls = BudgetDefinition.MaxCalls + 1 };
            var callsFailure = Assert.Throws<InvalidOperationException>(() =>
                LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, changedCalls));
            Assert.Contains("does not match", callsFailure.Message, StringComparison.Ordinal);

            var changedTokens = BudgetDefinition with { MaxTotalTokens = BudgetDefinition.MaxTotalTokens + 1 };
            var tokensFailure = Assert.Throws<InvalidOperationException>(() =>
                LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, changedTokens));
            Assert.Contains("does not match", tokensFailure.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Ledger_RejectsLegacyVersionOneCycle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-live-budget-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"version\":1}", TestContext.Current.CancellationToken);

            var failure = Assert.Throws<InvalidOperationException>(() =>
                LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, BudgetDefinition));

            Assert.Contains("newly attested provider project", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Ledger_PinsOneImmutableQuotePerCurrencyPair()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-live-budget-{Guid.NewGuid():N}.json");
        try
        {
            var ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, BudgetDefinition);
            var quote = new CurrencyExchangeQuote(
                "USD",
                "EUR",
                0.9m,
                DateTimeOffset.UtcNow.AddHours(-1),
                "test_reference");
            ledger.PinExchangeRate(quote);
            ledger.PinExchangeRate(quote);

            var changedQuote = quote with { Rate = 0.8m };
            var failure = Assert.Throws<InvalidOperationException>(() => ledger.PinExchangeRate(changedQuote));

            Assert.Contains("different exchange-rate quote", failure.Message, StringComparison.Ordinal);
            Assert.Equal(quote, Assert.Single(ledger.Snapshot.ExchangeRates));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task LivePhase_AllowsFailedDiagnosticRetryWithoutResettingSharedLedger()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gnougo-live-budget-{Guid.NewGuid():N}.json");
        try
        {
            var ledger = LiveIntentAgentGenerationTests.LiveBudgetLedger.Open(path, BudgetDefinition);
            var snapshot = new LLMUsageBudgetSnapshot
            {
                StartedAtUtc = DateTimeOffset.UtcNow,
                Calls = 7,
                InputTokens = 101,
                OutputTokens = 23,
                TotalTokens = 124,
                EstimatedCost = 1.10m,
                EstimatedCostCurrency = "EUR",
                EstimatedCostUsd = 1.25m
            };
            await ledger.PersistAsync(snapshot, TestContext.Current.CancellationToken);
            ledger.MarkProbeCompleted(snapshot);
            var method = typeof(LiveIntentAgentGenerationTests).GetMethod(
                "ValidateLivePhase",
                BindingFlags.Static | BindingFlags.NonPublic)!;

            method.Invoke(null, [ledger, 1]);

            ledger.MarkDiagnosticGenerationCompleted(snapshot);
            var repeatedDiagnostic = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [ledger, 1]));
            Assert.IsType<InvalidOperationException>(repeatedDiagnostic.InnerException);
            method.Invoke(null, [ledger, 3]);

            ledger.MarkFinalAcceptanceCompleted(snapshot);
            var repeatedFinal = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [ledger, 3]));
            Assert.IsType<InvalidOperationException>(repeatedFinal.InnerException);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
