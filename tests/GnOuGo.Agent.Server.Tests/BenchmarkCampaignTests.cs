using System.Text.Json.Nodes;
using System.Net;
using GnOuGo.AI.Core;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;
public sealed class BenchmarkCampaignTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("none")] [InlineData("permission")] [InlineData("endpoint")]
    [InlineData("price")] [InlineData("retry")] [InlineData("metadata")] [InlineData("other-server")]
    public async Task SdkWireAmendmentPreservesFundingAndRejectsUnrelatedChanges(string defect)
    {
        var records = new Records(); var old = new BenchmarkCampaign(records, "old"); var campaign = new BenchmarkCampaign(records, "sdk-funded");
        var options = new LLMOptions { DefaultProvider = "OpenAi", DefaultModel = "model",
            Models = new() { ["OpenAi"] = new() { Type = "openai", Url = "https://gateway.invalid/v1" } },
            ModelOverrides = new() { ["openai/model"] = new() { Id = "model", ProviderType = "openai", MaxOutputTokens = 32768,
                Pricing = new() { Currency = "EUR", InputPer1MTokens = 30, OutputPer1MTokens = 180 },
                Capabilities = new() { SupportsReasoningEffort = true, SupportedReasoningEfforts = ["medium", "high"] } } } };
        options.McpServers[BenchmarkCampaign.SdkServer] = new() { Type = "stdio", Command = "dotnet",
            EnvironmentVariables = new() { ["Code__AllowWrites"] = "false" } };
        using var original = new KeyVaultBenchmarkModel(campaign, options);
        var config = original.CampaignConfiguration(); await old.PinAsync(config, Ct); await campaign.PinAsync(config, Ct);
        string Metadata() => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(new RoutingLLMClient(options, []).ResolveDeclaredMetadata("OpenAi", "model")));
        var allocation = await campaign.AuthorizeFreshAllocationAsync("old", 400m, "same-grant", config, original.ConfigurationFingerprint, Metadata(), Ct);
        var parent = allocation["configuration_id"]!.ToString();
        var funding = (await campaign.LoadAsync("planning-evaluation-configuration", "independent-funding-scope", Ct))!.ToJsonString();
        var uncertain = new BenchmarkHttpJournal(campaign, "retained:1", 10, 10, 3m);
        await uncertain.PrepareAsync(Ct); await uncertain.SaveAsync(new() { Attempts = [new() { Id = "unknown" }] }, Ct);
        options.McpServers[BenchmarkCampaign.SdkServer].EnvironmentVariables![BenchmarkCampaign.SdkTransportKey] = BenchmarkCampaign.SdkStructuredTransport;
        if (defect == "permission") options.McpServers[BenchmarkCampaign.SdkServer].EnvironmentVariables!["Code__AllowWrites"] = "true";
        if (defect == "endpoint") options.Models["OpenAi"].Url = "https://other.invalid/v1";
        if (defect == "price") options.ModelOverrides["openai/model"].Pricing!.InputPer1MTokens++;
        if (defect == "retry") options.Models["OpenAi"].RetryPolicy.MaxAttempts++;
        if (defect == "metadata") options.ModelOverrides["openai/model"].MaxOutputTokens++;
        if (defect == "other-server") options.McpServers["other"] = new() { Type = "http", Url = "https://unapproved.invalid/mcp" };
        using var changed = new KeyVaultBenchmarkModel(campaign, options);
        if (defect != "none")
        {
            await Assert.ThrowsAsync<PlanningConflictException>(() => changed.SdkTransportProofAsync(parent, "absent", Ct));
            return;
        }
        var proof = await changed.SdkTransportProofAsync(parent, "absent", Ct);
        var preview = await campaign.RegisterConfigurationAsync(config, changed.ConfigurationFingerprint, Metadata(),
            PlanningGraphCompiler.Fingerprint(config.ToJsonString()), "explicit-sdk-wire", false, Ct, captureCurrentSettings: true, sdkTransportProof: proof);
        var saved = await campaign.RegisterConfigurationAsync(config, changed.ConfigurationFingerprint, Metadata(),
            PlanningGraphCompiler.Fingerprint(config.ToJsonString()), "explicit-sdk-wire", true, Ct, preview["configuration_id"]!.ToString(),
            captureCurrentSettings: true, sdkTransportProof: proof);
        await campaign.SelectConfigurationAsync(saved["configuration_id"]!.ToString(), config, changed.ConfigurationFingerprint, Ct);
        Assert.Equal(400m, (await campaign.FreshAllocationAsync(Ct))!["allowance_eur"]!.GetValue<decimal>());
        Assert.Equal(funding, (await campaign.LoadAsync("planning-evaluation-configuration", "independent-funding-scope", Ct))!.ToJsonString());
        Assert.Equal(3m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        var forged = proof.DeepClone().AsObject(); forged["environment_key"] = "Code__AllowWrites";
        await Assert.ThrowsAsync<PlanningConflictException>(() => campaign.RegisterConfigurationAsync(config, changed.ConfigurationFingerprint, Metadata(),
            PlanningGraphCompiler.Fingerprint(config.ToJsonString()), "forged-sdk-wire", true, Ct, captureCurrentSettings: true, sdkTransportProof: forged));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("price")]
    [InlineData("endpoint")]
    [InlineData("output")]
    [InlineData("retry")]
    public async Task ReasoningCapabilityCorrectionPreservesFundedAuthorityAndRejectsEveryOtherChange(string defect)
    {
        var records = new Records(); var old = new BenchmarkCampaign(records, "old"); var campaign = new BenchmarkCampaign(records, "funded");
        var options = new LLMOptions { DefaultProvider = "OpenAi", DefaultModel = "model",
            Models = new() { ["OpenAi"] = new() { Type = "openai", Url = "https://gateway.invalid/v1" } },
            ModelOverrides = new() { ["openai/model"] = new() { Id = "model", ProviderType = "openai",
                Pricing = new() { Currency = "EUR", InputPer1MTokens = 30, OutputPer1MTokens = 180 }, MaxOutputTokens = 32768,
                Capabilities = new() { SupportsReasoningEffort = true, SupportedReasoningEfforts = ["medium", "high"] } } } };
        using var before = new KeyVaultBenchmarkModel(campaign, options);
        var config = before.CampaignConfiguration(); await old.PinAsync(config, Ct); await campaign.PinAsync(config, Ct);
        var oldMetadata = new RoutingLLMClient(options, []).ResolveDeclaredMetadata("OpenAi", "model");
        var authority = await campaign.AuthorizeFreshAllocationAsync("old", 400, "one-existing-grant", config,
            before.ConfigurationFingerprint, PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(oldMetadata)), Ct);
        var parent = authority["configuration_id"]!.ToString();
        var funding = (await campaign.LoadAsync("planning-evaluation-configuration", "independent-funding-scope", Ct))!.ToJsonString();
        var retained = new BenchmarkHttpJournal(campaign, "old:1:hash", 10, 10, 3m);
        await retained.PrepareAsync(Ct); await retained.SaveAsync(new() { Attempts = [new() { Id = "old:1:hash:attempt" }] }, Ct);
        options.ModelOverrides["openai/model"].Capabilities.SupportedReasoningEfforts = ["low", "medium", "high"];
        if (defect == "price") options.ModelOverrides["openai/model"].Pricing!.InputPer1MTokens = 1;
        if (defect == "endpoint") options.Models["OpenAi"].Url = "https://different.invalid/v1";
        if (defect == "output") options.ModelOverrides["openai/model"].MaxOutputTokens = 65536;
        if (defect == "retry") options.Models["OpenAi"].RetryPolicy.MaxAttempts++;
        using var current = new KeyVaultBenchmarkModel(campaign, options);
        if (defect != "none")
        {
            await Assert.ThrowsAsync<PlanningConflictException>(() => current.ReasoningCapabilityProofAsync(parent, ["medium", "high"], Ct));
            return;
        }
        var proof = await current.ReasoningCapabilityProofAsync(parent, ["medium", "high"], Ct);
        var metadata = new RoutingLLMClient(options, []).ResolveDeclaredMetadata("OpenAi", "model");
        var amendment = await campaign.RegisterConfigurationAsync(config, current.ConfigurationFingerprint,
            PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(metadata)), PlanningGraphCompiler.Fingerprint(config.ToJsonString()),
            "explicit capability-only authorization", true, Ct, reasoningCapabilityProof: proof);
        await campaign.SelectConfigurationAsync(amendment["configuration_id"]!.ToString(), config, current.ConfigurationFingerprint, Ct);
        Assert.Equal(400m, (await campaign.FreshAllocationAsync(Ct))!["allowance_eur"]!.GetValue<decimal>());
        Assert.Equal(funding, (await campaign.LoadAsync("planning-evaluation-configuration", "independent-funding-scope", Ct))!.ToJsonString());
        Assert.Equal(3m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        var next = new BenchmarkHttpJournal(campaign, "independent:1:hash", 10, 10, 1m);
        await next.PrepareAsync(Ct); await next.SaveAsync(new() { Attempts = [new() { Id = "new-attempt" }] }, Ct);
        Assert.Equal(4m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(funding, (await campaign.LoadAsync("planning-evaluation-configuration", "independent-funding-scope", Ct))!.ToJsonString());
    }

    [Theory]
    [InlineData("none")] [InlineData("price")] [InlineData("endpoint")]
    [InlineData("output")] [InlineData("retry")] [InlineData("other-model")]
    public async Task StreamingAmendmentChainsToTheSameGrantWithoutChangingAnyHold(string defect)
    {
        var records = new Records(); var previousCampaign = new BenchmarkCampaign(records, "old"); var campaign = new BenchmarkCampaign(records, "funded");
        var options = new LLMOptions { DefaultProvider = "OpenAi", DefaultModel = "model", Models = new() { ["OpenAi"] = new() { Type = "openai", Url = "https://gateway.invalid/v1" } },
            ModelOverrides = new() { ["openai/model"] = new() { Id = "model", ProviderType = "openai", MaxOutputTokens = 32768,
                Pricing = new() { Currency = "EUR", InputPer1MTokens = 30, OutputPer1MTokens = 180 },
                Capabilities = new() { SupportsReasoningEffort = true, SupportedReasoningEfforts = ["medium", "high"] } } } };
        using var original = new KeyVaultBenchmarkModel(campaign, options);
        var configuration = original.CampaignConfiguration(); await previousCampaign.PinAsync(configuration, Ct); await campaign.PinAsync(configuration, Ct);
        string MetadataHash() => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(new RoutingLLMClient(options, []).ResolveDeclaredMetadata("OpenAi", "model")));
        var grant = await campaign.AuthorizeFreshAllocationAsync("old", 400, "same-400-grant", configuration, original.ConfigurationFingerprint, MetadataHash(), Ct);
        var rootId = grant["configuration_id"]!.ToString();
        var funding = (await campaign.LoadAsync("planning-evaluation-configuration", "independent-funding-scope", Ct))!.ToJsonString();
        var retained = new BenchmarkHttpJournal(campaign, "earlier:1", 10, 10, 3m);
        await retained.PrepareAsync(Ct); await retained.SaveAsync(new() { Attempts = [new() { Id = "uncertain" }] }, Ct);
        options.ModelOverrides["openai/model"].Capabilities.SupportedReasoningEfforts = ["low", "medium", "high"];
        using var reasoning = new KeyVaultBenchmarkModel(campaign, options);
        var reasoningProof = await reasoning.ReasoningCapabilityProofAsync(rootId, ["medium", "high"], Ct);
        var amended = await campaign.RegisterConfigurationAsync(configuration, reasoning.ConfigurationFingerprint, MetadataHash(), PlanningGraphCompiler.Fingerprint(configuration.ToJsonString()),
            "reasoning-only", true, Ct, reasoningCapabilityProof: reasoningProof);
        var parentId = amended["configuration_id"]!.ToString();
        options.Models["OpenAi"].ModelTransportOverrides = new() { ["model"] = new() { StreamingMode = LLMStreamingMode.Streaming,
            StreamingSupport = new() { [LLMTransportResolver.ChatCompletions] = true } } };
        if (defect == "price") options.ModelOverrides["openai/model"].Pricing!.InputPer1MTokens = 1;
        if (defect == "endpoint") options.Models["OpenAi"].Url = "https://other.invalid/v1";
        if (defect == "output") options.ModelOverrides["openai/model"].MaxOutputTokens++;
        if (defect == "retry") options.Models["OpenAi"].RetryPolicy.MaxAttempts++;
        if (defect == "other-model") options.Models["OpenAi"].ModelTransportOverrides!["other"] = new() { StreamingMode = LLMStreamingMode.Streaming };
        using var streaming = new KeyVaultBenchmarkModel(campaign, options);
        if (defect != "none")
        {
            await Assert.ThrowsAsync<PlanningConflictException>(() => streaming.StreamingTransportProofAsync(parentId, Ct));
            return;
        }
        var proof = await streaming.StreamingTransportProofAsync(parentId, Ct);
        var preview = await campaign.RegisterConfigurationAsync(streaming.CampaignConfiguration(), streaming.ConfigurationFingerprint, MetadataHash(),
            PlanningGraphCompiler.Fingerprint(configuration.ToJsonString()), "transport-only", false, Ct, captureCurrentSettings: true, streamingTransportProof: proof);
        var committed = await campaign.RegisterConfigurationAsync(streaming.CampaignConfiguration(), streaming.ConfigurationFingerprint, MetadataHash(),
            PlanningGraphCompiler.Fingerprint(configuration.ToJsonString()), "transport-only", true, Ct, preview["configuration_id"]!.ToString(), captureCurrentSettings: true, streamingTransportProof: proof);
        await campaign.SelectConfigurationAsync(committed["configuration_id"]!.ToString(), streaming.CampaignConfiguration(), streaming.ConfigurationFingerprint, Ct);
        Assert.Equal(400m, (await campaign.FreshAllocationAsync(Ct))!["allowance_eur"]!.GetValue<decimal>());
        Assert.Equal(funding, (await campaign.LoadAsync("planning-evaluation-configuration", "independent-funding-scope", Ct))!.ToJsonString());
        Assert.Equal(3m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        var forged = proof.DeepClone().AsObject(); forged["previous_host_configuration_hash"] = new string('f', 64);
        await Assert.ThrowsAsync<PlanningConflictException>(() => campaign.RegisterConfigurationAsync(streaming.CampaignConfiguration(), streaming.ConfigurationFingerprint, MetadataHash(),
            PlanningGraphCompiler.Fingerprint(configuration.ToJsonString()), "forged-transport", true, Ct, captureCurrentSettings: true, streamingTransportProof: forged));
    }

    [Theory]
    [InlineData("none")] [InlineData("receipt")] [InlineData("usage")] [InlineData("artifact")]
    [InlineData("tools")] [InlineData("empty-attempts")] [InlineData("body")]
    [InlineData("policy")] [InlineData("owner")] [InlineData("configuration")]
    public async Task IndependentProbeRetentionPreservesEveryOriginalAndReservation(string defect)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "probe-campaign");
        const string runId = "independent-probe";
        var request = ServerLiveDiagnostics.IndependentProbe(null, "OpenAi", "model", runId, new string('a', 32), "medium", 64, true);
        var requestNode = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject();
        var id = request.ClientRequestId!;
        var config = new JsonObject { ["provider"] = "OpenAi", ["model"] = "model", ["endpoint"] = "https://gateway.invalid/v1" };
        await campaign.PinAsync(config, Ct);
        var registration = await campaign.RegisterConfigurationAsync(config, new string('a', 64), new string('b', 64),
            PlanningGraphCompiler.Fingerprint(config.ToJsonString()), "fixture exact deployment", true, Ct);
        var policy = new JsonObject { ["version"] = 2, ["mode"] = "runtime_single_attempt", ["disable_transport_retries"] = true,
            ["allow_http_recovery"] = true, ["request_hash"] = PlanningGraphCompiler.Fingerprint(requestNode.ToJsonString()),
            ["effective_retry_policy"] = JsonSerializer.SerializeToNode(new LLMProviderRetryPolicyOptions { MaxAttempts = 1, MaxUncertainRetries = 0 }, LLMHttpRetryJsonContext.Default.LLMProviderRetryPolicyOptions) };
        var body = System.Text.Encoding.UTF8.GetString(ChatRequestBuilder.OpenAiFull("model", request.Prompt, structuredOutputSchema: request.StructuredOutputSchema, reasoning: "medium", maxOutputTokens: 64));
        var journal = new BenchmarkHttpJournal(campaign, id, 4096, 64, 3m, effectiveTransportPolicy: policy);
        await journal.PrepareAsync(Ct, new JsonObject { ["source"] = "fixture" });
        await journal.SaveAsync(new() { Fingerprint = new string('c', 64), RequestMethod = "POST", Endpoint = "https://gateway.invalid/v1/chat/completions", RequestBody = body,
            Attempts = [new() { Id = id + ":attempt", StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), CompletedAtUtc = DateTimeOffset.UtcNow, Failure = "transport", ResponseBodyComplete = false }] }, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", id, requestNode, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", id, new() { ["stage"] = "dispatch", ["kind"] = "Transport", ["attempt_count"] = 1 }, Ct);
        var run = new JsonObject { ["source"] = new string('a', 40), ["host"] = "GnOuGo.Agent.Server", ["phase"] = "provider_probe", ["run"] = runId,
            ["campaign"] = campaign.Id, ["budget_mode"] = "limited", ["campaign_configuration"] = registration["configuration_id"]!.DeepClone(), ["request_id"] = id,
            ["passed"] = false, ["frontend_ready"] = true, ["finished_utc"] = DateTimeOffset.UtcNow, ["durable_receipt"] = false, ["physical_http_attempts"] = 1,
            ["provider"] = "OpenAi", ["model"] = "model", ["independent_probe"] = new JsonObject { ["applies_candidate"] = false, ["executes_business_tools"] = false,
                ["prompt_hash"] = PlanningGraphCompiler.Fingerprint(request.Prompt), ["schema_hash"] = PlanningGraphCompiler.Fingerprint(request.StructuredOutputSchema!.ToJsonString()) },
            ["effective_http"] = new JsonObject { ["body_sha256"] = PlanningGraphCompiler.Fingerprint(body), ["endpoint_sha256"] = PlanningGraphCompiler.Fingerprint("https://gateway.invalid/v1/chat/completions") } };
        if (defect == "receipt") await campaign.SaveAsync("planning-evaluation-receipts", id, new(), Ct);
        if (defect == "artifact") run["artifact_hash"] = "approved";
        if (defect == "owner") run["campaign"] = "other";
        if (defect == "configuration") run["campaign_configuration"] = new string('f', 64);
        if (defect == "tools") { requestNode["tools"] = new JsonArray(new JsonObject { ["name"] = "business_effect" }); await campaign.SaveAsync("planning-evaluation-requests", id, requestNode, Ct); }
        if (defect is "usage" or "body" or "empty-attempts" or "policy")
        {
            var changed = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
            if (defect == "usage") changed["usage"] = new JsonObject { ["input_tokens"] = 1 };
            if (defect == "body") changed["transport"]!["Attempts"]![0]!["Body"] = "received output";
            if (defect == "empty-attempts") changed["transport"]!["Attempts"] = new JsonArray();
            if (defect == "policy") changed["effective_transport_policy"]!["effective_retry_policy"]!["MaxAttempts"] = 2;
            await campaign.SaveAsync(BenchmarkHttpJournal.Collection, id, changed, Ct);
        }
        await campaign.SaveAsync("planning-evaluation-runs", runId, run, Ct);
        if (defect != "none")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", runId, id, Ct, BenchmarkCampaign.ProviderProbeRetention));
            return;
        }
        var accounting = (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)).ToJsonString();
        var originals = new Dictionary<string, string>();
        foreach (var collection in new[] { "planning-evaluation-runs", "planning-evaluation-requests", "planning-evaluation-failures", BenchmarkHttpJournal.Collection })
            originals[collection] = (await campaign.LoadAsync(collection, collection == "planning-evaluation-runs" ? runId : id, Ct))!.ToJsonString();
        var preview = await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", runId, id, Ct, BenchmarkCampaign.ProviderProbeRetention);
        var closure = await campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", runId, id, preview["evidence_hash"]!.ToString(),
            "operator", "explicit eligible generation retention", Ct, BenchmarkCampaign.ProviderProbeRetention);
        Assert.Equal("inconclusive", closure["outcome"]!.ToString()); Assert.True(closure["reservation_retained"]!.GetValue<bool>());
        Assert.True(closure["request_replay_forbidden"]!.GetValue<bool>());
        Assert.False(await campaign.HasUncertainRequestAsync(Ct));
        Assert.Equal(accounting, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)).ToJsonString());
        foreach (var original in originals)
            Assert.Equal(original.Value, (await campaign.LoadAsync(original.Key, original.Key == "planning-evaluation-runs" ? runId : id, Ct))!.ToJsonString());
        Assert.Equal(3m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task NewRunPreflightRequiresReceiptOrExistingRetentionEvenWithAnEmptyHttpJournal()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "current");
        await campaign.SaveAsync("planning-evaluation-requests", "session:1:hash", new(), Ct);
        Assert.True(await campaign.HasUncertainRequestAsync(Ct));
        await new BenchmarkHttpJournal(campaign, "session:1:hash", 10, 10, 1m).PrepareAsync(Ct);
        Assert.True(await campaign.HasUncertainRequestAsync(Ct)); // Empty transport alone is not a no-dispatch proof.
        await campaign.SaveAsync("planning-evaluation-receipts", "session:1:hash", new(), Ct);
        Assert.False(await campaign.HasUncertainRequestAsync(Ct));
        await new BenchmarkCampaign(records, "other").SaveAsync("planning-evaluation-requests", "unresolved:1:hash", new(), Ct);
        Assert.False(await campaign.HasUncertainRequestAsync(Ct));
    }

    [Fact]
    public async Task CampaignInspectionPagesOwnedEvidenceAndReadsItsExactBudgetOnly()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "current");
        for (var i = 0; i < 203; i++) await campaign.SaveAsync("planning-evaluation-requests", $"session:{i}:hash", new(), Ct);
        await records.UpsertAsync("planning-evaluation-requests", "benchmark", "other:broken", "not JSON", BenchmarkCampaign.Author, Ct);
        await records.UpsertAsync("planning-evaluation-budgets", "benchmark", "current", JsonSerializer.Serialize(new LLMUsageBudgetSnapshot { EstimatedCostCurrency = "EUR" }, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), BenchmarkCampaign.Author, Ct);
        await records.UpsertAsync("planning-evaluation-budgets", "benchmark", "other", "not JSON", BenchmarkCampaign.Author, Ct);
        var expected = await campaign.InspectAsync(Ct);
        records.ReadKeys.Clear(); records.MetadataQueries = 0; records.ForbidList = true;
        var actual = await campaign.InspectAsync(Ct);
        Assert.True(JsonNode.DeepEquals(expected, actual));
        Assert.Equal(203, actual["reserved_requests"]!.GetValue<int>());
        Assert.True(records.MetadataQueries >= 3);
        Assert.DoesNotContain(records.ReadKeys, key => key == "other" || key.StartsWith("other:", StringComparison.Ordinal));
        Assert.Contains("current", records.ReadKeys);
    }

    [Fact]
    public async Task CampaignInspectionRetainsTheCanonicalHashAcrossMixedCollectionsAndCaseSensitiveKeys()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "current");
        var names = new[] { "requests", "receipts", "failures", "runs", "summaries", "configuration", "http-attempts", "closures" };
        foreach (var name in names.Except(["http-attempts"]))
            foreach (var key in new[] { "z", "A", "a" })
                await campaign.SaveAsync("planning-evaluation-" + name, key, new() { ["value"] = "quote\"\\\n<>é🚀" }, Ct);
        await records.UpsertAsync("planning-evaluation-budgets", "benchmark", "current",
            JsonSerializer.Serialize(new LLMUsageBudgetSnapshot { EstimatedCostCurrency = "EUR" }, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), BenchmarkCampaign.Author, Ct);
        await records.UpsertAsync("planning-evaluation-runs", "benchmark", "another:private", "malformed unrelated payload", BenchmarkCampaign.Author, Ct);
        var evidence = new List<KeyVaultRecordValue>();
        foreach (var name in names) evidence.AddRange(await campaign.OwnedRecordsAsync("planning-evaluation-" + name, Ct));
        evidence.Add((await records.GetAsync("planning-evaluation-budgets", "benchmark", "current", BenchmarkCampaign.Author, Ct))!);
        var expected = PlanningGraphCompiler.Fingerprint(string.Join('\n', evidence.OrderBy(r => r.Collection, StringComparer.Ordinal)
            .ThenBy(r => r.Key, StringComparer.Ordinal).Select(r => new JsonArray(r.Collection, r.Key, r.Value).ToJsonString())));
        records.ForbidList = true; records.ReadKeys.Clear(); var writes = records.Writes;
        var actual = await campaign.InspectAsync(Ct);
        Assert.Equal(expected, actual["evidence_hash"]!.ToString());
        Assert.Equal(3, actual["reserved_requests"]!.GetValue<int>());
        Assert.Equal(3, actual["completed_receipts"]!.GetValue<int>());
        Assert.True(actual["completion_receipts_complete"]!.GetValue<bool>());
        Assert.Equal(writes, records.Writes);
        Assert.DoesNotContain("another:private", records.ReadKeys);
    }

    [Fact]
    public async Task CampaignAdmissionPagesOnlyOwnedRecordsWithoutDecryptingOtherCampaignHistory()
    {
        var records = new Records(); var current = new BenchmarkCampaign(records, "current"); var old = new BenchmarkCampaign(records, "old");
        var empty = new JsonObject { ["transport"] = new JsonObject { ["Attempts"] = new JsonArray() }, ["input_ceiling"] = 10,
            ["output_ceiling"] = 10, ["cost_ceiling_eur"] = 1m };
        for (var i = 0; i < 203; i++) await current.SaveAsync(BenchmarkHttpJournal.Collection, $"session:{i}:hash", empty, Ct);
        await old.SaveAsync(BenchmarkHttpJournal.Collection, "pending:1:hash", empty, Ct);
        await old.SaveAsync("planning-evaluation-requests", "pending:1:hash", new(), Ct);
        records.ForbidList = true;
        var accounting = await BenchmarkHttpJournal.AccountingAsync(current, ct: Ct);
        Assert.Equal(0m, accounting["cost_upper_bound_eur"]!.GetValue<decimal>());
        Assert.True(records.MetadataQueries >= 3);
        await current.CallAsync(new() { ClientRequestId = "new:1:hash" }, _ => Task.CompletedTask,
            _ => Task.FromResult(new LLMResponse { Text = "received" }), Ct);
        Assert.DoesNotContain(records.ReadKeys, key => key.StartsWith("old:", StringComparison.Ordinal));
        Assert.NotNull(await current.LoadAsync("planning-evaluation-receipts", "new:1:hash", Ct));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("missing")]
    [InlineData("cursor")]
    public async Task TargetedFinancialReadsFailClosedOnUnverifiableEvidence(string defect)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "current");
        await new BenchmarkHttpJournal(campaign, "one:1:hash", 10, 10, 1m).PrepareAsync(Ct);
        if (defect == "missing") records.HideKey = "current:one:1:hash";
        if (defect == "owner") records.MutateMetadata = page => page with { Records = [page.Records[0] with { Key = "another:one:1:hash" }] };
        if (defect == "cursor") records.MutateMetadata = page => page with { HasMore = true, Next = null };
        var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct));
        Assert.Equal("LLM_BUDGET_UNVERIFIABLE", error.Code);
    }

    [Fact]
    public async Task ExplicitFreshFundingRetainsOldLiabilitiesAndCannotResetOrDuplicateANewGrant()
    {
        var records = new Records(); var previous = new BenchmarkCampaign(records, "retained");
        var config = new JsonObject { ["provider"] = "OpenAi", ["model"] = "configured-model" };
        await previous.PinAsync(config, Ct);
        var old = new BenchmarkHttpJournal(previous, "old:1:hash", 10, 10, 30m);
        await old.PrepareAsync(Ct); await old.SaveAsync(new() { Attempts = [new() { Id = "old:1:hash:1" }] }, Ct);
        var before = (await previous.LoadAsync(BenchmarkHttpJournal.Collection, "old:1:hash", Ct))!.ToJsonString();
        var fresh = new BenchmarkCampaign(records, "new-allocation"); await fresh.PinAsync(config, Ct);
        async Task<JsonObject> Authorize(BenchmarkCampaign target, decimal amount = 400) => await target.AuthorizeFreshAllocationAsync(
            previous.Id, amount, "explicit-new-400", config, new string('a',64), new string('b',64), Ct);
        var funding = await Authorize(fresh); Assert.Equal(400m, await fresh.CostCeilingAsync(Ct));
        Assert.True(funding["historical_liabilities_unreconciled"]!.GetValue<bool>()); Assert.Null(funding["globally_available_funding"]);
        Assert.Equal(before, (await previous.LoadAsync(BenchmarkHttpJournal.Collection, "old:1:hash", Ct))!.ToJsonString());
        var current = new BenchmarkHttpJournal(fresh, "new:1:hash", 10, 10, 5m);
        await current.PrepareAsync(Ct); await current.SaveAsync(new() { Attempts = [new() { Id = "new:1:hash:1" }] }, Ct);
        await Authorize(fresh); // Idempotent authorization never clears subsequent consumption.
        Assert.Equal(5m, (await BenchmarkHttpJournal.AccountingAsync(fresh, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        await Assert.ThrowsAsync<PlanningConflictException>(() => Authorize(fresh, 401));
        var second = new BenchmarkCampaign(records, "second"); await second.PinAsync(config, Ct);
        await Assert.ThrowsAsync<PlanningConflictException>(() => Authorize(second));
        await Assert.ThrowsAsync<PlanningConflictException>(() => fresh.AuthorizeFreshAllocationAsync(previous.Id, 400,
            "another-reference", config, new string('a',64), new string('b',64), Ct));
        var wrong = new BenchmarkCampaign(records, "already-used"); await wrong.PinAsync(config, Ct);
        var issued = new BenchmarkHttpJournal(wrong, "issued:1:hash", 10, 10, 1m); await issued.PrepareAsync(Ct);
        await Assert.ThrowsAsync<PlanningConflictException>(() => wrong.AuthorizeFreshAllocationAsync(previous.Id, 400,
            "distinct-reference", config, new string('a',64), new string('b',64), Ct));
    }

    [Fact]
    public async Task ExistingAllocationAcceptsOneAuthorizedSupplementWithoutResettingGrantOrLiabilities()
    {
        var records = new Records(); var previous = new BenchmarkCampaign(records, "previous");
        var campaign = new BenchmarkCampaign(records, "supplemented");
        var config = new JsonObject { ["model"] = "model" };
        await previous.PinAsync(config, Ct); await campaign.PinAsync(config, Ct);
        Task<JsonObject> OriginalGrant(BenchmarkCampaign owner) => owner.AuthorizeFreshAllocationAsync(previous.Id, 400,
            "original-grant", config, new string('a', 64), new string('b', 64), Ct);
        await OriginalGrant(campaign);
        var grant = (await campaign.LoadAsync("planning-evaluation-configuration", "independent-funding-scope", Ct))!.ToJsonString();
        var old = new BenchmarkHttpJournal(campaign, "retained:1:hash", 10, 10, 5m);
        await old.PrepareAsync(Ct); await old.SaveAsync(new() { Attempts = [new() { Id = "uncertain" }] }, Ct);
        var before = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "retained:1:hash", Ct))!.ToJsonString();
        var accounting = (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)).ToJsonString();
        var extension = await campaign.ExtendBudgetAsync(400, 800, "one-supplement", Ct);
        var restarted = new BenchmarkCampaign(records, campaign.Id);
        Assert.Equal(extension.ToJsonString(), (await restarted.ExtendBudgetAsync(400, 800, "one-supplement", Ct)).ToJsonString());
        var authority = (await restarted.FreshAllocationAsync(Ct))!;
        Assert.Equal(400m, authority["allowance_eur"]!.GetValue<decimal>());
        Assert.Equal(800m, authority["current_authorized_ceiling_eur"]!.GetValue<decimal>());
        await OriginalGrant(restarted); // Re-reading either authorization does not credit or reset anything.
        Assert.Equal(2, (await restarted.LoadAsync("planning-evaluation-configuration", "spending-authorization", Ct))!["history"]!.AsArray().Count);
        Assert.Equal(grant, (await restarted.LoadAsync("planning-evaluation-configuration", "independent-funding-scope", Ct))!.ToJsonString());
        Assert.Equal(before, (await restarted.LoadAsync(BenchmarkHttpJournal.Collection, "retained:1:hash", Ct))!.ToJsonString());
        Assert.Equal(accounting, (await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct)).ToJsonString());
        var next = new BenchmarkHttpJournal(restarted, "new:1:hash", 10, 10, 500m);
        await next.PrepareAsync(Ct); await next.SaveAsync(new() { Attempts = [new() { Id = "new-attempt" }] }, Ct);
        var over = new BenchmarkHttpJournal(restarted, "over:1:hash", 10, 10, 296m);
        await over.PrepareAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => over.SaveAsync(new() { Attempts = [new() { Id = "over-attempt" }] }, Ct));
        Assert.Equal(505m, (await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ExtendBudgetAsync(800, 1200, "one-supplement", Ct));
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("duplicate")]
    [InlineData("decrease")]
    [InlineData("missing-accounting")]
    [InlineData("grant")]
    public async Task SupplementAdmissionRejectsBrokenAuthorizationChainsWithoutAnotherAttempt(string defect)
    {
        var records = new Records(); var previous = new BenchmarkCampaign(records, "previous");
        var campaign = new BenchmarkCampaign(records, "supplemented");
        var config = new JsonObject { ["model"] = "model" };
        await previous.PinAsync(config, Ct); await campaign.PinAsync(config, Ct);
        await campaign.AuthorizeFreshAllocationAsync(previous.Id, 400, "original-grant", config, new string('a', 64), new string('b', 64), Ct);
        var spending = await campaign.ExtendBudgetAsync(400, 800, "one-supplement", Ct);
        var history = spending["history"]!.AsArray();
        switch (defect)
        {
            case "gap": history[1]!["previous_ceiling_eur"] = 399; break;
            case "duplicate": history[1]!["authorization"] = "original-grant"; break;
            case "decrease": history[1]!["ceiling_eur"] = 399; spending["ceiling_eur"] = 399; break;
            case "missing-accounting": history[1]!.AsObject().Remove("accounting_before"); break;
            case "grant": history[0]!["ceiling_eur"] = 401; history[1]!["previous_ceiling_eur"] = 401; break;
        }
        await campaign.SaveAsync("planning-evaluation-configuration", "spending-authorization", spending, Ct);
        var journal = new BenchmarkHttpJournal(new BenchmarkCampaign(records, campaign.Id), "next:1:hash", 10, 10, 1m);
        await journal.PrepareAsync(Ct);
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() =>
            journal.SaveAsync(new() { Attempts = [new() { Id = "forbidden" }] }, Ct));
        Assert.Empty((await journal.LoadAsync(Ct))!.Attempts);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("binding")]
    [InlineData("ceiling")]
    [InlineData("configuration")]
    public async Task FreshFundingRejectsIncompleteOrSubstitutedAuthority(string defect)
    {
        var records = new Records(); var old = new BenchmarkCampaign(records, "old"); var campaign = new BenchmarkCampaign(records, "fresh");
        var config = new JsonObject { ["model"] = "model" }; await old.PinAsync(config, Ct); await campaign.PinAsync(config, Ct);
        await campaign.AuthorizeFreshAllocationAsync(old.Id, 25, "new-funds", config, new string('a',64), new string('b',64), Ct);
        var scope = (await campaign.LoadAsync("planning-evaluation-configuration", "independent-funding-scope", Ct))!;
        if (defect == "pending") scope["status"] = "pending";
        if (defect == "binding") scope["material"]!["authorization_reference"] = "substituted";
        if (defect == "ceiling")
        {
            var spending = (await campaign.LoadAsync("planning-evaluation-configuration", "spending-authorization", Ct))!;
            spending["ceiling_eur"] = 26;
            await campaign.SaveAsync("planning-evaluation-configuration", "spending-authorization", spending, Ct);
        }
        if (defect == "configuration") scope["configuration_id"] = "missing";
        await campaign.SaveAsync("planning-evaluation-configuration", "independent-funding-scope", scope, Ct);
        var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => campaign.FreshAllocationAsync(Ct));
        Assert.Equal("LLM_BUDGET_UNVERIFIABLE", error.Code);
        var dispatched = false;
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => campaign.CallAsync(new() { ClientRequestId = "new:1:hash" },
            _ => Task.CompletedTask, _ => { dispatched = true; return Task.FromResult(new LLMResponse()); }, Ct));
        Assert.False(dispatched);
        var journal = new BenchmarkHttpJournal(campaign, "new:1:hash", 10, 10, 1m); await journal.PrepareAsync(Ct);
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => journal.SaveAsync(new() { Attempts = [new() { Id = "attempt" }] }, Ct));
        Assert.Empty((await journal.LoadAsync(Ct))!.Attempts);
    }

    [Fact]
    public async Task InterruptedFreshAllocationRequiresExactCompletionAndNeverDiscardsReceivedEvidence()
    {
        var records = new Records(); var old = new BenchmarkCampaign(records, "old"); var campaign = new BenchmarkCampaign(records, "fresh");
        var config = new JsonObject { ["model"] = "model" }; await old.PinAsync(config, Ct); await campaign.PinAsync(config, Ct);
        Task<JsonObject> Authorize() => campaign.AuthorizeFreshAllocationAsync(old.Id, 400, "fresh-grant", config, new string('a',64), new string('b',64), Ct);
        records.FailKey = "fresh:spending-authorization";
        await Assert.ThrowsAsync<IOException>(Authorize);
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => campaign.FreshAllocationAsync(Ct));
        records.FailKey = null; await Authorize();
        var journal = new BenchmarkHttpJournal(campaign, "new:1:hash", 10, 10, 1m);
        var state = new LLMHttpRetryState { Attempts = [new() { Id = "attempt" }] }; await journal.SaveAsync(state, Ct);
        var scope = (await campaign.LoadAsync("planning-evaluation-configuration", "independent-funding-scope", Ct))!;
        scope["status"] = "pending"; await campaign.SaveAsync("planning-evaluation-configuration", "independent-funding-scope", scope, Ct);
        state.Attempts[0].Status = 200; state.Attempts[0].Body = "received";
        await journal.SaveAsync(state, Ct); // Persist actual outcomes even if admission authority later becomes invalid.
        Assert.Equal("received", (await journal.LoadAsync(Ct))!.Attempts[0].Body);
        await campaign.SaveAsync("planning-evaluation-receipts", "new:1:hash", JsonSerializer.SerializeToNode(new LLMResponse { Text = "exact" }, PlanningJsonContext.Default.LLMResponse)!.AsObject(), Ct);
        var recovered = await campaign.CallAsync(new() { ClientRequestId = "new:1:hash" }, _ => throw new Exception("No admission"), _ => throw new Exception("No dispatch"), Ct);
        Assert.Equal("exact", recovered.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CampaignFinancialForecastWarningsKeepCurrentAndUnknownExposureHard(bool experimental)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "forecast");
        JsonObject Policy() => new() { ["financial_forecast_mode"] = experimental ? "experimental_warning" : "strict" };
        var first = new BenchmarkHttpJournal(campaign, "one:1:hash", 10, 10, 30m, reviewCostHeadroom: 30m, reviewAttemptHeadroom: 1, effectiveTransportPolicy: Policy());
        await first.PrepareAsync(Ct);
        var attempt = new LLMHttpRetryState { Attempts = [new() { Id = "one:1:hash:1" }] };
        if (!experimental)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => first.SaveAsync(attempt, Ct));
            Assert.Empty((await first.LoadAsync(Ct))!.Attempts); return;
        }
        await first.SaveAsync(attempt, Ct);
        var record = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "one:1:hash", Ct))!;
        Assert.NotNull(record["financial_forecast_warning"]);
        var current = new BenchmarkHttpJournal(campaign, "two:1:hash", 10, 10, 30m, effectiveTransportPolicy: Policy());
        await current.PrepareAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => current.SaveAsync(new() { Attempts = [new() { Id = "two:1:hash:1" }] }, Ct));
        var unverified = Policy(); unverified["external_accounting_status"] = "unverified_external_holds";
        var missing = new BenchmarkHttpJournal(campaign, "three:1:hash", 10, 10, 1m, effectiveTransportPolicy: unverified);
        await missing.PrepareAsync(Ct);
        var failure = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => missing.SaveAsync(new() { Attempts = [new() { Id = "three:1:hash:1" }] }, Ct));
        Assert.Equal("LLM_BUDGET_UNVERIFIABLE", failure.Code);
        var before = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var audit = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct, includeSessions: true);
        Assert.Equal(before["reserved_cost_eur"]!.GetValue<decimal>(), audit["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(30m, audit["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(3, audit["sessions"]!.AsArray().Count);
        Assert.Null(audit["providerInvoicedCost"]);
        Assert.False(audit["legacyAggregate"]!["present"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("material")]
    [InlineData("configuration")]
    [InlineData("host")]
    [InlineData("authorization")]
    public async Task SelectionDiagnosisUsesEveryOriginalGuardWithoutWrites(string defect)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "selection");
        var original = new JsonObject { ["provider"] = "OpenAi", ["model"] = "original" };
        await campaign.PinAsync(original, Ct);
        var current = original.DeepClone().AsObject(); current["model"] = "configured-private-deployment";
        var preview = await campaign.RegisterConfigurationAsync(current, new string('a', 64), new string('b', 64), PlanningGraphCompiler.Fingerprint(original.ToJsonString()),
            "selection-authorization", false, Ct);
        var id = preview["configuration_id"]!.ToString();
        await campaign.RegisterConfigurationAsync(current, new string('a', 64), new string('b', 64), PlanningGraphCompiler.Fingerprint(original.ToJsonString()),
            "selection-authorization", true, Ct, id);
        var saved = (await campaign.LoadAsync("planning-evaluation-configuration", "configuration:" + id, Ct))!;
        var savedBytes = saved.ToJsonString();
        if (defect == "material") { saved["extra"] = true; await campaign.SaveAsync("planning-evaluation-configuration", "configuration:" + id, saved, Ct); }
        if (defect == "authorization") await campaign.SaveAsync("planning-evaluation-configuration", "configuration-authorization:" + PlanningGraphCompiler.Fingerprint("selection-authorization"), new() { ["configuration_id"] = "other" }, Ct);
        if (defect == "configuration") current["model"] = "stale";
        if (defect == "missing") id = "missing";
        var writes = records.Writes;
        var result = await campaign.CheckConfigurationSelectionAsync(id, current, defect == "host" ? "changed" : new string('a', 64), Ct);
        Assert.Equal(defect == "valid", result.Accepted);
        if (defect == "valid") await campaign.SelectConfigurationAsync(id, current, new string('a', 64), Ct);
        else
        {
            var error = await Assert.ThrowsAsync<PlanningConflictException>(() => campaign.SelectConfigurationAsync(id, current, defect == "host" ? "changed" : new string('a', 64), Ct));
            Assert.Equal(result.ToJson().ToJsonString(), Assert.IsType<JsonObject>(error.Data["gnougo.campaign_configuration_selection"]).ToJsonString());
            Assert.Null(campaign.ConfigurationId);
            Assert.Single(result.Failures);
        }
        Assert.Equal(writes, records.Writes);
        Assert.Equal(original.ToJsonString(), (await campaign.LoadAsync("planning-evaluation-configuration", "configuration", Ct))!.ToJsonString());
        if (defect == "missing") { Assert.Null(result.MaterialHashMatches); Assert.Null(result.AuthorizationBindingMatches); }
        else if (defect != "material") Assert.Equal(savedBytes, (await campaign.LoadAsync("planning-evaluation-configuration", "configuration:" + id, Ct))!.ToJsonString());
    }

    [Fact]
    public void BillingDefaultDifferencesAreExactAndSecretsAreWithheld()
    {
        var previous = new JsonObject { ["model"] = "private-deployment", ["endpoint"] = "https://secret-host/?key=old-secret", ["request_policy"] = new JsonObject() };
        var current = previous.DeepClone().AsObject();
        current["endpoint"] = "https://other-secret/?key=new-secret";
        current["credential"] = "credential-secret";
        current["request_policy"] = new JsonObject { ["BillingServiceTier"] = "standard", ["BillingRegionalProcessing"] = false, ["BillingCacheRetention"] = "30m" };
        var differences = BenchmarkCampaign.ConfigurationDifferences(previous, current);
        Assert.Equal(5, differences.Count);
        var billing = differences.OfType<JsonObject>().Where(p => p["field"]!.ToString().StartsWith("request_policy.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, billing.Length);
        Assert.All(billing, field => Assert.False(field["previous_present"]!.GetValue<bool>()));
        Assert.Contains("standard", differences.ToJsonString()); Assert.Contains("30m", differences.ToJsonString());
        Assert.DoesNotContain("secret", differences.ToJsonString());
        Assert.Empty(BenchmarkCampaign.ConfigurationDifferences(null, current));
        var sources = KeyVaultBenchmarkModel.DescribeConfigurationSources(new JsonObject(), "OpenAi", new() { ["model"] = "keyvault_provider_configuration" });
        Assert.Equal("code_default", sources["request_policy.BillingServiceTier"]!.ToString());
        Assert.Equal("keyvault_provider_configuration", sources["model"]!.ToString());
    }

    [Theory]
    [InlineData("{\"result\":42}", true)]
    [InlineData("{\"result\":42.0}", true)]
    [InlineData("{\"result\":4.2e1}", true)]
    [InlineData("{\"result\":\"42\"}", false)]
    [InlineData("{\"result\":41}", false)]
    [InlineData("{\"result\":42,\"extra\":1}", false)]
    public void NativeLiveOracleRequiresTheNumericPublicResultAndSuccessfulExecution(string json, bool expected)
    {
        Assert.Equal(expected, ServerLiveDiagnostics.NativeOutputOracle(true, JsonNode.Parse(json)));
        Assert.False(ServerLiveDiagnostics.NativeOutputOracle(false, JsonNode.Parse(json)));
        Assert.True(ServerLiveDiagnostics.NativeOutputOracle(true, new JsonObject { ["result"] = 42d }));
    }

    [Theory]
    [InlineData("49", true)]
    [InlineData("49.01", false)]
    public async Task RetainedExternalReservationsRemainInsideTheUnchangedGlobalCeiling(string held, bool allowed)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "external-reservation");
        var policy = new JsonObject { ["retained_external_reservations_eur"] = decimal.Parse(held, System.Globalization.CultureInfo.InvariantCulture) };
        var journal = new BenchmarkHttpJournal(campaign, "fresh:1:hash", 10, 10, 1m, effectiveTransportPolicy: policy);
        await journal.PrepareAsync(Ct);
        var state = new LLMHttpRetryState { Attempts = [new() { Id = "fresh:1:hash:1" }] };
        if (allowed) await journal.SaveAsync(state, Ct);
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveAsync(state, Ct));
            var refusal = Assert.IsType<JsonObject>(error.Data[BenchmarkCampaign.BudgetRefusalData]);
            Assert.Equal("campaign_llm_budget", refusal["scope"]!.ToString());
            Assert.Equal(50m, refusal["allowance"]!.GetValue<decimal>());
            Assert.Equal("not_started", refusal["dispatch_status"]!.ToString());
            Assert.Equal("not_started", refusal["attempt_dispatch_status"]!.ToString());
        }
        Assert.Equal(50m, await campaign.CostCeilingAsync(Ct));
        Assert.Equal(allowed ? 1 : 0, (await journal.LoadAsync(Ct))!.Attempts.Count);
    }

    [Fact]
    public async Task RetryFundingRefusalRetainsTheReceivedAttemptAndReportsItsScopeWithoutSendingAnother()
    {
        var campaign = new BenchmarkCampaign(new Records(), "retry-funding");
        var journal = new BenchmarkHttpJournal(campaign, "retry:1:hash", 10, 10, 30m,
            reviewCostHeadroom: 10m, reviewAttemptHeadroom: 1, effectiveTransportPolicy: StrictTransport());
        await journal.PrepareAsync(Ct);
        var state = new LLMHttpRetryState { Attempts = [new() { Id = "retry:1:hash:1" }] };
        await journal.SaveAsync(state, Ct);
        state.Attempts[0].Status = 500; state.Attempts[0].Body = "{\"error\":{\"code\":\"upstream_failure\"}}";
        await journal.SaveAsync(state, Ct);
        state.Attempts.Add(new() { Id = "retry:1:hash:2" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveAsync(state, Ct));
        var details = Assert.IsType<JsonObject>(error.Data[BenchmarkCampaign.BudgetRefusalData]);
        var failure = PlanningBudgetFailure.FromDetails(details)!;
        Assert.Equal("campaign_llm_budget", failure.Scope);
        Assert.Equal("captured_campaign_ceiling", failure.Origin);
        Assert.Equal(50m, failure.Allowance); Assert.Equal(70m, failure.RequiredReservation);
        Assert.Equal("EUR", failure.Currency); Assert.Equal("started", failure.DispatchStatus);
        Assert.Equal("not_started", details["attempt_dispatch_status"]!.ToString());
        Assert.Equal(2, details["denied_attempt_number"]!.GetValue<int>());
        Assert.Equal(500, Assert.Single((await journal.LoadAsync(Ct))!.Attempts).Status);
        Assert.Equal(30m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task ServerCaptureUsesItsRetainedElapsedBudgetWithoutAddingBenchmarkAuthorityToTheRequest()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "server-duration");
        using var model = new KeyVaultBenchmarkModel(campaign, new LLMOptions());
        var request = LiveWorkflowEvaluation.FreshPlanningRequest([], "server", "amazon", "Requested work", "provider", "model");
        request.Options.Remove("benchmark_planning_elapsed_ms");
        var original = JsonSerializer.Serialize(request, PlanningJsonContext.Default.PlanningRequest);
        await model.CapturePlanningPolicyAsync(request, Ct, capturedElapsedLimit: TimeSpan.FromMinutes(7));
        Assert.Equal(original, JsonSerializer.Serialize(request, PlanningJsonContext.Default.PlanningRequest));
        var captured = (await campaign.LoadAsync("planning-evaluation-session-policy", "server", Ct))!;
        Assert.Equal(420000, captured["benchmark_planning_elapsed_ms"]!.GetValue<int>());
        Assert.Equal("captured_server_session_budget", captured["elapsed_limit_origin"]!.ToString());
        var writes = records.Writes;
        await model.CapturePlanningPolicyAsync(request, Ct, capturedElapsedLimit: TimeSpan.FromMinutes(7));
        Assert.Equal(writes, records.Writes);
        await Assert.ThrowsAsync<PlanningConflictException>(() => model.CapturePlanningPolicyAsync(request, Ct, capturedElapsedLimit: TimeSpan.FromMinutes(8)));
        Assert.Equal(writes, records.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cost_ceiling_eur")]
    [InlineData("max_calls")]
    public async Task CurrentSettingsAmendmentRequiresExplicitScopeAndPreservesFinancialGuards(string? forbidden)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "current-settings");
        var original = new JsonObject { ["provider"] = "openai", ["model"] = "old", ["endpoint"] = "old-route",
            ["request_policy"] = new JsonObject(), ["max_calls"] = 8, ["cost_ceiling_eur"] = 50 };
        await campaign.PinAsync(original, Ct);
        var next = original.DeepClone().AsObject(); next["model"] = "configured-deployment";
        next["request_policy"] = new JsonObject { ["BillingServiceTier"] = "standard" };
        var previous = PlanningGraphCompiler.Fingerprint(original.ToJsonString()); var writes = records.Writes;
        await Assert.ThrowsAsync<PlanningConflictException>(() => campaign.RegisterConfigurationAsync(next, new string('a', 64),
            new string('b', 64), previous, "current-settings-authorization", true, Ct));
        Assert.Equal(writes, records.Writes);
        if (forbidden is not null)
        {
            next[forbidden] = 100;
            await Assert.ThrowsAsync<PlanningConflictException>(() => campaign.RegisterConfigurationAsync(next, new string('a', 64),
                new string('b', 64), previous, "current-settings-authorization", true, Ct, captureCurrentSettings: true));
            Assert.Equal(writes, records.Writes); return;
        }
        var preview = await campaign.RegisterConfigurationAsync(next, new string('a', 64), new string('b', 64), previous,
            "current-settings-authorization", false, Ct, captureCurrentSettings: true);
        var applied = await campaign.RegisterConfigurationAsync(next, new string('a', 64), new string('b', 64), previous,
            "current-settings-authorization", true, Ct, preview["configuration_id"]!.ToString(), captureCurrentSettings: true);
        Assert.Equal("current_deployment_settings", applied["material"]!["amendment_scope"]!.ToString());
        Assert.True(JsonNode.DeepEquals(original, await campaign.LoadAsync("planning-evaluation-configuration", "configuration", Ct)));
        Assert.Equal(50m, await campaign.CostCeilingAsync(Ct));
    }

    [Fact]
    public async Task ModelAmendmentPreservesOriginalFinancialStateAndRegistersExactlyOnce()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "model-amendment");
        var original = new JsonObject { ["provider"] = "deployment", ["model"] = "previous-snapshot", ["endpoint"] = "old-route", ["max_calls"] = 8 };
        await campaign.PinAsync(original, Ct);
        var next = original.DeepClone().AsObject(); next["model"] = "new-snapshot";
        next["endpoint"] = "new-deployment-route";
        var previous = PlanningGraphCompiler.Fingerprint(original.ToJsonString());
        var host = new string('a', 64); var metadata = new string('b', 64);
        var journal = new BenchmarkHttpJournal(campaign, "unknown:1:hash", 100, 20, .1m, 64, .1m, 1, StrictTransport());
        await journal.SaveAsync(new() { Attempts = [new() { Id = "unknown:1:hash:1" }] }, Ct);
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var writes = records.Writes;
        var preview = await campaign.RegisterConfigurationAsync(next, host, metadata, previous, "exact-model-authorization", false, Ct);
        Assert.Equal(writes, records.Writes);
        Assert.False(preview["applied"]!.GetValue<bool>());
        await Assert.ThrowsAsync<PlanningConflictException>(() => campaign.RegisterConfigurationAsync(next, host, metadata, previous,
            "exact-model-authorization", true, Ct, new string('c', 64)));
        Assert.Equal(writes, records.Writes);
        var applied = await campaign.RegisterConfigurationAsync(next, host, metadata, previous, "exact-model-authorization", true, Ct);
        var id = applied["configuration_id"]!.ToString(); writes = records.Writes;
        await campaign.RegisterConfigurationAsync(next, host, metadata, previous, "exact-model-authorization", true, Ct);
        Assert.Equal(writes, records.Writes);
        Assert.Equal(preview["configuration_id"]!.ToString(), id);
        Assert.True(JsonNode.DeepEquals(original, await campaign.LoadAsync("planning-evaluation-configuration", "configuration", Ct)));
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        Assert.Equal(50m, await campaign.CostCeilingAsync(Ct));
        await campaign.SelectConfigurationAsync(id, next, host, Ct);
        Assert.Equal(id, campaign.ConfigurationId);
        await Assert.ThrowsAsync<PlanningConflictException>(() => campaign.SelectConfigurationAsync(id, original, host, Ct));
        await Assert.ThrowsAsync<PlanningConflictException>(() => campaign.SelectConfigurationAsync(id, next, new string('c', 64), Ct));
        next["model"] = "conflicting-snapshot";
        await Assert.ThrowsAsync<PlanningConflictException>(() => campaign.RegisterConfigurationAsync(next, host, metadata, previous,
            "exact-model-authorization", true, Ct));
        Assert.Equal(writes, records.Writes);
    }

    [Theory]
    [InlineData("previous-pin")]
    [InlineData("provider")]
    [InlineData("metadata")]
    public async Task ModelAmendmentRejectsChangedAuthorityBeforeWriting(string defect)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "model-conflict");
        var original = new JsonObject { ["provider"] = "deployment", ["model"] = "previous", ["endpoint"] = "original" };
        await campaign.PinAsync(original, Ct);
        var next = original.DeepClone().AsObject(); next["model"] = "new";
        if (defect == "provider") next["provider"] = "changed";
        var previous = defect == "previous-pin" ? new string('c', 64) : PlanningGraphCompiler.Fingerprint(original.ToJsonString());
        var writes = records.Writes;
        await Assert.ThrowsAsync<PlanningConflictException>(() => campaign.RegisterConfigurationAsync(next, new string('a', 64),
            defect == "metadata" ? "unknown" : new string('b', 64), previous, "authorization", true, Ct));
        Assert.Equal(writes, records.Writes);
    }

    [Fact]
    public void OptimizationDiagnosticsAreFreshCapturedAuthoritySeparateFromTokenAdvisory()
    {
        var request = new PlanningRequest();
        PlanningBenchmarkMeasurements.ConfigureRequest(request, ["--diagnostic-optimization-thresholds"], retained: false);
        Assert.True(PlanningTokenThresholds.OptimizationAdvisory(request));
        var frozen = PlanningBenchmarkMeasurements.Configuration(request);
        var restored = PlanningBenchmarkMeasurements.RestoreConfiguration(frozen);
        Assert.True(PlanningTokenThresholds.OptimizationAdvisory(restored));
        Assert.True(JsonNode.DeepEquals(frozen, PlanningBenchmarkMeasurements.Configuration(restored)));
        PlanningBenchmarkMeasurements.ConfigureRequest(restored, ["--diagnostic-optimization-thresholds"], retained: true);
        Assert.Throws<PlanningConflictException>(() => PlanningTokenThresholds.RejectReserved(restored.Options));

        var old = new PlanningRequest();
        PlanningBenchmarkMeasurements.ConfigureRequest(old, ["--advisory-token-thresholds"], retained: false);
        Assert.False(PlanningTokenThresholds.OptimizationAdvisory(old));
        Assert.Throws<InvalidOperationException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(old,
            ["--diagnostic-optimization-thresholds"], retained: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticJournalCrossesCallAndElapsedTargetsButRetainsReservationsAndMoney(bool diagnostics)
    {
        var campaign = new BenchmarkCampaign(new Records(), "optimization-journal");
        var policy = StrictTransport();
        policy["token_threshold_policy"] = "advisory";
        if (diagnostics) policy[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        policy["deadline_utc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        policy["max_elapsed_ms"] = 1L;
        var journal = new BenchmarkHttpJournal(campaign, "session:1:hash", 100, 20, 1m, 1, 1m, 1, policy);
        var attempts = new LLMHttpRetryState { Attempts = [new() { Id = "session:1:hash:1" }] };
        if (!diagnostics)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveAsync(attempts, Ct));
            Assert.Null(await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "session:1:hash", Ct));
            return;
        }
        await journal.SaveAsync(attempts, Ct);
        var recorded = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "session:1:hash", Ct))!;
        var warnings = recorded["optimization_threshold_warnings"]!.AsArray();
        Assert.Contains(warnings, warning => warning?["kind"]?.ToString() == "physical_calls");
        Assert.Contains(warnings, warning => warning?["kind"]?.ToString() == "elapsed_ms");
        var restarted = new BenchmarkHttpJournal(campaign, "session:1:hash", 100, 20, 1m, 1, 1m, 1, policy);
        Assert.Single((await restarted.LoadAsync(Ct))!.Attempts);
        await restarted.SaveAsync(attempts, Ct);
        Assert.True(JsonNode.DeepEquals(recorded, await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "session:1:hash", Ct)));
        var totals = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(1m, totals["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(1L, totals["unknown_attempts"]!.GetValue<long>());
        var reported = (await LiveCampaignEvidence.AccountingAsync(campaign, "session"))["planning"]!;
        Assert.Equal(2, reported["warnings"]!.AsArray().Count);

        // An optimization warning cannot grant a larger retry policy or spend beyond money.
        var retryOverflow = new LLMHttpRetryState { Attempts = Enumerable.Range(1, 4).Select(i =>
            new LLMHttpAttempt { Id = "overflow:" + i }).ToList() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.SaveAsync(retryOverflow, Ct));
        var denied = new BenchmarkHttpJournal(campaign, "session:2:hash", 100, 20, 50m, 1, 1m, 1, policy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => denied.SaveAsync(new()
            { Attempts = [new() { Id = "session:2:hash:1" }] }, Ct));
        Assert.Null(await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "session:2:hash", Ct));
        Assert.True(JsonNode.DeepEquals(totals, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
    }

    [Fact]
    public async Task AdvisoryJournalRetainsTokenOverrunsAndReservationsAcrossRestart()
    {
        var campaign = new BenchmarkCampaign(new Records(), "advisory-journal");
        var policy = StrictTransport(); policy["token_threshold_policy"] = "advisory";
        policy["max_total_tokens"] = 1L;
        var journal = new BenchmarkHttpJournal(campaign, "session:1:hash", 100, 20, .1m, 64, .1m, 1, policy);
        var state = new LLMHttpRetryState { Attempts = [new() { Id = "session:1:hash:1" }] };
        await journal.SaveAsync(state, Ct);
        state.Attempts[0].Status = 500; state.Attempts[0].Body = "received failure";
        await journal.SaveAsync(state, Ct);
        var restarted = new BenchmarkHttpJournal(campaign, "session:1:hash", 100, 20, .1m, 64, .1m, 1, policy);
        state = (await restarted.LoadAsync(Ct))!;
        state.Attempts.Add(new() { Id = "session:1:hash:2" });
        await restarted.SaveAsync(state, Ct);
        var row = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "session:1:hash", Ct))!;
        Assert.NotNull(row["token_threshold_warning"]);
        var totals = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(2L, totals["calls"]!.GetValue<long>());
        Assert.Equal(.2m, totals["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(240L, totals["reserved_input_tokens"]!.GetValue<long>() + totals["reserved_output_tokens"]!.GetValue<long>());
    }

    [Fact]
    public void AdvisoryModeIsCapturedOnceAndCannotBecomeProductionAuthority()
    {
        var request = new PlanningRequest();
        PlanningBenchmarkMeasurements.ConfigureRequest(request, ["--advisory-token-thresholds"], retained: false);
        var saved = PlanningBenchmarkMeasurements.Configuration(request);
        var restored = PlanningBenchmarkMeasurements.RestoreConfiguration(saved);
        Assert.Equal("advisory", restored.Options["token_threshold_policy"]!.ToString());
        Assert.Throws<PlanningConflictException>(() => PlanningTokenThresholds.RejectReserved(restored.Options));
        var strict = new PlanningRequest(); PlanningBenchmarkMeasurements.ConfigureRequest(strict, [], retained: false);
        Assert.Throws<InvalidOperationException>(() => PlanningBenchmarkMeasurements.ConfigureRequest(strict,
            ["--advisory-token-thresholds"], retained: true));
    }

    [Fact]
    public async Task CapturedRetriesReserveEveryFailedPhysicalAttemptIncludingRateLimits()
    {
        var campaign = new BenchmarkCampaign(new Records(), "confirmed-retries");
        const string id = "session:1:hash";
        var policy = StrictTransport();
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m, 64, .2m, 1, policy);
        var identities = new List<string>();
        using var http = new HttpClient(new Handler(message =>
        {
            identities.Add(message.Headers.GetValues("X-Client-Request-Id").Single());
            return identities.Count < 3
                ? new(identities.Count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable)
                    { Content = new StringContent("{\"error\":{\"message\":\"temporarily unavailable\",\"code\":\"rate_limit_exceeded\"}}") }
                : new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}") };
        }));
        using (new LLMHttpRetryContext(id, journal, new() { MaxAttempts = 3, MaxUncertainRetries = 0,
            BaseDelayMilliseconds = 1, MaxDelayMilliseconds = 1 }).Activate())
            await new OpenAiLLMProvider(http).CallAsync("test", new() { Url = "https://provider.example/v1" },
                new() { Prompt = "fixed", MaxOutputTokens = 20 }, Ct);
        var usage = await journal.CompleteAsync(new() { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m }, Ct);
        Assert.Equal(3, identities.Distinct().Count());
        Assert.Equal(2, usage["uncertain_attempts"]!.GetValue<int>());
        Assert.Equal(2m, usage["reserved_cost_eur"]!.GetValue<decimal>());
        var record = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var reported = (await LiveCampaignEvidence.AccountingAsync(campaign, "session"))["planning"]!;
        var closed = PlanningBenchmarkMeasurements.ClosedHttpUsage([record], 3)!;
        foreach (var result in new JsonNode[] { accounting, reported, closed })
            Assert.Equal(2m, result["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(.1m, accounting["known_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(200L, accounting["reserved_input_tokens"]!.GetValue<long>());
        Assert.Equal(40L, accounting["reserved_output_tokens"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(503, 2)]
    [InlineData(200, 1)]
    public async Task CapturedRetryRecoveryUsesOnlyExactCompleteOutcomes(int? interruptedStatus, int expectedSends)
    {
        var campaign = new BenchmarkCampaign(new Records(), "strict-recovery");
        const string id = "session:1:hash";
        var request = new LLMRequest { ClientRequestId = id, Prompt = "same exact prompt" };
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m, 64, effectiveTransportPolicy: StrictTransport());
        var sends = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            sends++;
            return sends == 1 && interruptedStatus == 503
                ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"message\":\"unavailable\"}}") }
                : new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}") };
        }));
        async Task<LLMResponse> Dispatch(ILLMHttpRetryJournal active, CancellationToken ct)
        {
            using var retry = new LLMHttpRetryContext(id, active, new() { MaxAttempts = 3, MaxUncertainRetries = 0,
                BaseDelayMilliseconds = 1, MaxDelayMilliseconds = 1 }).Activate();
            var response = await new OpenAiLLMProvider(http).CallAsync("test", new() { Url = "https://provider.example/v1" },
                new() { Prompt = "fixed", MaxOutputTokens = 20 }, ct);
            return new() { Text = response.Text, Json = response.Json, Usage = await journal.CompleteAsync(new()
                { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m }, ct) };
        }
        await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(request, _ => Task.CompletedTask,
            ct => Dispatch(new InterruptingJournal(journal, interruptedStatus), ct), Ct, allowHttpRecovery: true));
        var before = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString();
        if (interruptedStatus is null)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(request, _ => Task.CompletedTask,
                ct => Dispatch(journal, ct), Ct, allowHttpRecovery: true));
            Assert.Equal(before, (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString());
        }
        else
        {
            var response = await campaign.CallAsync(request, _ => Task.CompletedTask, ct => Dispatch(journal, ct), Ct, allowHttpRecovery: true);
            Assert.Equal("{}", response.Text);
            Assert.Equal(interruptedStatus == 503 ? 1 : 0, response.Usage!["uncertain_attempts"]!.GetValue<int>());
            await campaign.CallAsync(request, _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), Ct, allowHttpRecovery: true);
        }
        Assert.Equal(expectedSends, sends);
    }

    private sealed class InterruptingJournal(ILLMHttpRetryJournal inner, int? status) : ILLMHttpRetryJournal
    {
        private bool _failed;
        public Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct) => inner.LoadAsync(ct);
        public async Task SaveAsync(LLMHttpRetryState state, CancellationToken ct)
        {
            await inner.SaveAsync(state, ct);
            if (!_failed && state.Attempts.Count > 0 && state.Attempts[^1].Status == status)
            { _failed = true; throw new IOException("Interrupted after durable attempt evidence."); }
        }
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("tokens")]
    [InlineData("time")]
    [InlineData("money")]
    public async Task EveryRetryKeepsRequiredReviewHeadroomAndPriorReservations(string limit)
    {
        var campaign = new BenchmarkCampaign(new Records(), "strict-" + limit);
        var clock = new BenchmarkClock();
        var policy = StrictTransport(clock.GetUtcNow().AddSeconds(1));
        policy["max_total_tokens"] = limit == "tokens" ? 350L : 15_000_000L;
        policy["review_token_headroom"] = 120L;
        var journal = new BenchmarkHttpJournal(campaign, "session:1:hash", 100, 20, limit == "money" ? 24m : .1m,
            limit == "calls" ? 2 : 64, limit == "money" ? 3m : .1m, 1, policy, clock);
        var state = new LLMHttpRetryState { Attempts = [new() { Id = "session:1:hash:1" }] };
        await journal.SaveAsync(state, Ct);
        state.Attempts[0].Status = 429;
        await journal.SaveAsync(state, Ct);
        var before = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "session:1:hash", Ct))!.ToJsonString();
        if (limit == "time") clock.Advance();
        state.Attempts.Add(new() { Id = "session:1:hash:2" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveAsync(state, Ct));
        Assert.Equal(before, (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, "session:1:hash", Ct))!.ToJsonString());
        Assert.Single((await journal.LoadAsync(Ct))!.Attempts);
    }

    private static JsonObject StrictTransport(DateTimeOffset? deadline = null) => new()
    {
        ["version"] = 2, ["allow_http_recovery"] = true, ["disable_transport_retries"] = false,
        ["effective_retry_policy"] = new JsonObject { ["MaxAttempts"] = 3 },
        ["max_total_tokens"] = 15_000_000L, ["review_token_headroom"] = 120L,
        ["deadline_utc"] = (deadline ?? DateTimeOffset.UtcNow.AddHours(5)).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
    };

    private sealed class BenchmarkClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance() => _now = _now.AddSeconds(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\n")]
    [InlineData("No structured completion.")]
    [InlineData("{\"partial\":")]
    public void ReceiptInspectionRetainsEmptyAndNonJsonTextWithoutChangingEvidence(string text)
    {
        var receipt = new JsonObject { ["json"] = null, ["text"] = text, ["completionStatus"] = "output_limit",
            ["usage"] = new JsonObject { ["output_tokens"] = 16384, ["reasoning_tokens"] = 16384 } };
        var original = receipt.ToJsonString();
        var response = Assert.IsAssignableFrom<JsonValue>(SchemaPortabilityCampaign.InspectResponse(receipt));
        Assert.Equal(text, response.GetValue<string>());
        Assert.Null(response.Parent);
        Assert.Equal(original, receipt.ToJsonString());
    }

    [Theory]
    [InlineData("{\"answer\":42}")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData(null)]
    public void ReceiptInspectionPreservesExistingJsonAndAbsentResponsePresentation(string? text)
    {
        var receipt = new JsonObject { ["json"] = null, ["text"] = text };
        var original = receipt.ToJsonString();
        Assert.True(JsonNode.DeepEquals(text is null ? null : JsonNode.Parse(text), SchemaPortabilityCampaign.InspectResponse(receipt)));
        Assert.Equal(original, receipt.ToJsonString());
    }

    [Fact]
    public void ReceiptInspectionPrefersStructuredResponseAndReturnsAnIndependentProjection()
    {
        var receipt = new JsonObject { ["json"] = new JsonObject { ["answer"] = 42 }, ["text"] = "not JSON" };
        var original = receipt.ToJsonString();
        var response = Assert.IsType<JsonObject>(SchemaPortabilityCampaign.InspectResponse(receipt));
        Assert.Equal(42, response["answer"]!.GetValue<int>());
        response["answer"] = 0;
        Assert.Equal(original, receipt.ToJsonString());
    }

    [Fact]
    public async Task CompactTransportFreezesPhaseCeilingsAndCannotFallBackToGeneralAllowance()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "compact-policy");
        using var model = new KeyVaultBenchmarkModel(campaign, new LLMOptions());
        var request = LiveWorkflowEvaluation.FreshPlanningRequest([], "compact", "amazon", "Requested work", "provider", "model");
        Assert.Equal(64, request.MaxModelCalls);
        await model.CapturePlanningPolicyAsync(request, Ct);
        var captured = (await campaign.LoadAsync("planning-evaluation-session-policy", "compact", Ct))!;
        Assert.Equal(11, captured["planning_format"]!.GetValue<int>());
        foreach (var (phase, limit) in new[] { ("construction_select", 6000), ("construction_draft", 16000), ("construction_local_judge", 8000) })
            Assert.Equal(limit, KeyVaultBenchmarkModel.PlanningInputLimit(captured, new() { StructuredOutputSchema = new JsonObject { ["title"] = phase } }));
        Assert.Throws<PlanningConflictException>(() => KeyVaultBenchmarkModel.PlanningInputLimit(captured, new()));
        request.Options["construction_limits"]!["phasePolicy"]!["draft"]!["maxInputTokens"] = 7000;
        await Assert.ThrowsAsync<PlanningConflictException>(() => model.CapturePlanningPolicyAsync(request, Ct));
        Assert.Equal(16000, (await campaign.LoadAsync("planning-evaluation-session-policy", "compact", Ct))!["construction_limits"]!["phasePolicy"]!["draft"]!["maxInputTokens"]!.GetValue<int>());
        Assert.Throws<PlanningConflictException>(() => KeyVaultBenchmarkModel.PlanningInputLimit(new JsonObject { ["max_input_tokens"] = 32000 }, new()));
    }

    [Fact]
    public async Task DraftHeadroomPreservesMandatoryReviewsWithoutInventingChargesOrResettingUnknowns()
    {
        var campaign = new BenchmarkCampaign(new Records(), "review-headroom");
        await new BenchmarkHttpJournal(campaign, "old:1:unknown", 100, 20, 45m)
            .SaveAsync(new() { Attempts = [new() { Id = "old" }] }, Ct);
        var before = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var draft = new BenchmarkHttpJournal(campaign, "fresh:1:draft", 100, 20, 3m,
            reviewCostHeadroom: 3m, reviewAttemptHeadroom: 2);
        await draft.PrepareAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => draft.SaveAsync(new() { Attempts = [new() { Id = "draft" }] }, Ct));
        Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        Assert.Empty((await draft.LoadAsync(Ct))!.Attempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BenchmarkHttpJournal(campaign, "fresh:1:draft", 100, 20, 3m)
            .SaveAsync(new(), Ct));
        var allowed = new BenchmarkHttpJournal(campaign, "fresh:2:smaller", 100, 20, 1m,
            reviewCostHeadroom: 3m, reviewAttemptHeadroom: 2);
        await allowed.SaveAsync(new() { Attempts = [new() { Id = "allowed" }] }, Ct);
        var after = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(46m, after["cost_upper_bound_eur"]!.GetValue<decimal>());
        Assert.Equal(2L, after["unknown_attempts"]!.GetValue<long>());
        var exhausted = new BenchmarkHttpJournal(campaign, "bounded:1:draft", 100, 20, .1m,
            sessionAttemptLimit: 2, reviewCostHeadroom: .1m, reviewAttemptHeadroom: 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => exhausted.SaveAsync(new() { Attempts = [new() { Id = "no-review-room" }] }, Ct));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("retired")]
    [InlineData("limit")]
    [InlineData("transport")]
    [InlineData("unbounded")]
    [InlineData("excess")]
    [InlineData("runtime")]
    public async Task MandatoryReviewHeadroomRequiresExactCapturedSessionPolicy(string defect)
    {
        var campaign = new BenchmarkCampaign(new Records(), "window-headroom-" + defect);
        using var model = new KeyVaultBenchmarkModel(campaign, new LLMOptions());
        var request = LiveWorkflowEvaluation.FreshPlanningRequest([], "window", "amazon", "Requested work", "provider", "model");
        request.Options["llm_budget"]!["max_calls"] = 5;
        await model.CapturePlanningPolicyAsync(request, Ct);
        var captured = (await campaign.LoadAsync("planning-evaluation-session-policy", "window", Ct))!;
        var policy = StrictTransport();
        policy["planning_transport_policy"] = captured[PlanningTransportPolicies.OptionName]!.DeepClone();
        if (defect == "retired") captured["planning_format"] = 10;
        if (defect == "limit") captured["max_physical_attempts"] = 4;
        if (defect == "transport") policy["planning_transport_policy"] = new JsonObject();
        if (defect == "runtime") policy["mode"] = "runtime_single_attempt";
        await campaign.SaveAsync("planning-evaluation-session-policy", "window", captured, Ct);
        var id = defect == "missing" ? "absent:1:hash" : "window:1:hash";
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, .1m,
            sessionAttemptLimit: defect == "unbounded" ? null : 5,
            reviewAttemptHeadroom: defect == "excess" ? 6 : 3, effectiveTransportPolicy: policy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.PrepareAsync(Ct));
        Assert.Null(await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct));
        Assert.Equal(0L, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["calls"]!.GetValue<long>());
    }

    [Fact]
    public async Task MandatoryReviewHeadroomResumesExactlyAndPreservesRequiredCalls()
    {
        var campaign = new BenchmarkCampaign(new Records(), "window-headroom-recovery");
        using var model = new KeyVaultBenchmarkModel(campaign, new LLMOptions());
        var request = LiveWorkflowEvaluation.FreshPlanningRequest([], "window", "amazon", "Requested work", "provider", "model");
        request.Options["llm_budget"]!["max_calls"] = 5;
        await model.CapturePlanningPolicyAsync(request, Ct);
        var captured = (await campaign.LoadAsync("planning-evaluation-session-policy", "window", Ct))!;
        Assert.Equal(11, captured["planning_format"]!.GetValue<int>());
        var policy = StrictTransport();
        policy["planning_transport_policy"] = captured[PlanningTransportPolicies.OptionName]!.DeepClone();
        const string id = "window:1:hash";
        BenchmarkHttpJournal Journal(int reviews = 4, int limit = 5) => new(campaign, id, 100, 20, .1m,
            limit, .4m, reviews, policy);
        await Journal().PrepareAsync(Ct);
        var empty = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString();
        await Journal().SaveAsync(new(), Ct);
        Assert.Equal(empty, (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Journal(3).SaveAsync(new(), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Journal(limit: 6).SaveAsync(new(), Ct));
        var state = new LLMHttpRetryState { Attempts = [new() { Id = "first", Status = null }] };
        await Journal().SaveAsync(state, Ct);
        state.Attempts[0].Status = 500;
        await Journal().SaveAsync(state, Ct);
        var before = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString();
        state.Attempts.Add(new() { Id = "no-room-for-four-reviews" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Journal().SaveAsync(state, Ct));
        Assert.Equal(before, (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString());
        Assert.Single((await Journal().LoadAsync(Ct))!.Attempts);
        captured["planning_format"] = 10;
        await campaign.SaveAsync("planning-evaluation-session-policy", "window", captured, Ct);
        state.Attempts.RemoveAt(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Journal().SaveAsync(state, Ct));
        Assert.Equal(before, (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString());
    }

    [Fact]
    public async Task LargeSessionTransportPolicyIsCapturedOnceAndCannotAcquireMissingIssuedAuthority()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "capacity");
        using var model = new KeyVaultBenchmarkModel(campaign, new LLMOptions());
        var request = LiveWorkflowEvaluation.FreshPlanningRequest(["--construction-preset", "large"], "new-large", "amazon", "Requested work", "provider", "model");
        Assert.Equal(384, request.MaxModelCalls); Assert.Equal(64, request.MaxReplanAttempts);
        Assert.Equal(32_000, request.Generation.MaxInputTokensPerRequest);
        await model.CapturePlanningPolicyAsync(request, Ct);
        var captured = (await campaign.LoadAsync("planning-evaluation-session-policy", "new-large", Ct))!;
        Assert.Equal(384, captured["max_physical_attempts"]!.GetValue<int>());
        var writes = records.Writes;
        await model.CapturePlanningPolicyAsync(request, Ct); Assert.Equal(writes, records.Writes);
        request.MaxModelCalls++;
        await Assert.ThrowsAsync<PlanningConflictException>(() => model.CapturePlanningPolicyAsync(request, Ct));
        Assert.True(JsonNode.DeepEquals(captured, await campaign.LoadAsync("planning-evaluation-session-policy", "new-large", Ct)));
        request.SessionId = "historical";
        await campaign.SaveAsync("planning-evaluation-requests", "historical:1:issued", new JsonObject(), Ct);
        writes = records.Writes;
        await Assert.ThrowsAsync<PlanningConflictException>(() => model.CapturePlanningPolicyAsync(request, Ct));
        Assert.Null(await campaign.LoadAsync("planning-evaluation-session-policy", "historical", Ct));
        Assert.Equal(writes, records.Writes);
    }


    [Fact]
    public async Task ExplicitBudgetExtensionPreservesReservationsConfigurationAndIsolationAcrossRestart()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "extension");
        var configuration = new JsonObject { ["cost_ceiling_eur"] = 50 };
        await campaign.PinAsync(configuration, Ct);
        var unknown = new BenchmarkHttpJournal(campaign, "unknown:1:hash", 100, 20, 49m);
        await unknown.SaveAsync(new() { Attempts = [new() { Id = "unknown" }] }, Ct);
        var request = new BenchmarkHttpJournal(campaign, "fresh:1:hash", 100, 20, 2m);
        var attempt = new LLMHttpRetryState { Attempts = [new() { Id = "fresh" }] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => request.SaveAsync(attempt, Ct));
        var before = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var authorization = await campaign.ExtendBudgetAsync(50m, 100m, "Explicit test operator authorization", Ct);
        Assert.True(JsonNode.DeepEquals(authorization,
            await campaign.ExtendBudgetAsync(50m, 100m, "Explicit test operator authorization", Ct)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.ExtendBudgetAsync(100m, 150m, "Explicit test operator authorization", Ct));
        var restarted = new BenchmarkCampaign(records, "extension");
        Assert.Equal(100m, await restarted.CostCeilingAsync(Ct));
        Assert.Equal(50m, await new BenchmarkCampaign(records, "other").CostCeilingAsync(Ct));
        Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct)));
        await restarted.PinAsync(configuration, Ct);
        Assert.Single(authorization["history"]!.AsArray());
        await request.SaveAsync(attempt, Ct);
        var totals = await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct);
        Assert.Equal(51m, totals["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(2L, totals["unknown_attempts"]!.GetValue<long>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BenchmarkHttpJournal(restarted, "over:1:hash", 100, 20, 50m)
            .SaveAsync(new() { Attempts = [new() { Id = "over" }] }, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ExtendBudgetAsync(50m, 100m, "Stale approval", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ExtendBudgetAsync(100m, 100m, "No increase", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.ExtendBudgetAsync(100m, 150m, " ", Ct));
        Assert.True(JsonNode.DeepEquals(authorization, await restarted.LoadAsync("planning-evaluation-configuration", "spending-authorization", Ct)));
    }

    [Fact]
    public void DurableMappingIdentitiesRemainStableAndBelongToExecutionAccounting()
    {
        var original = new LLMRequest { ClientRequestId = "durable-mapping-receipt", Prompt = "Observed data", MaxTokens = 8192 };
        var dispatched = LiveWorkflowEvaluation.ExecutionRequest(original, "cohort-case-1", 1);
        Assert.StartsWith("cohort-case-1-execution:", dispatched.ClientRequestId);
        Assert.Equal(dispatched.ClientRequestId, LiveWorkflowEvaluation.ExecutionRequest(original, "cohort-case-1", 99).ClientRequestId);
        Assert.NotEqual(dispatched.ClientRequestId, LiveWorkflowEvaluation.ExecutionRequest(original, "cohort-case-2", 1).ClientRequestId);
        Assert.Equal("durable-mapping-receipt", original.ClientRequestId);
        Assert.Equal(8192, dispatched.MaxTokens);
    }

    [Fact]
    public async Task LiveReportsRejectMissingManifestsAndRetainInterruptedRuns()
    {
        var campaign = new BenchmarkCampaign(new Records(), "interrupted-live");
        var run = new JsonObject { ["phase"] = "final" };
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:final-amazon-1", run, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LiveCampaignEvidence.ReportAsync(campaign));
        var manifest = new JsonObject { ["production_sha"] = "frozen", ["harness_sha"] = "frozen" };
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "final-manifest", manifest, Ct);
        run["manifest"] = manifest.DeepClone();
        run["source"] = "frozen"; run["label"] = "final-amazon-1"; run["scenario"] = "amazon";
        run["session"] = new JsonObject { ["request"] = new JsonObject { ["sessionId"] = "final-amazon-1" } };
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:final-amazon-1", run, Ct);
        var report = await LiveCampaignEvidence.ReportAsync(campaign);
        Assert.Equal("interrupted", report["runs"]![0]!["status"]!.ToString());
        Assert.Equal(6, report["runs"]!.AsArray().Count);
        Assert.Equal(0, report["passed"]!.GetValue<int>());
        run["result"] = new JsonObject { ["status"] = "final_review", ["execution_oracle"] = true };
        run["execution_started"] = "retained-before-dispatch";
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:final-amazon-1", run, Ct);
        report = await LiveCampaignEvidence.ReportAsync(campaign);
        Assert.Equal("interrupted", report["runs"]![0]!["execution_status"]!.ToString());
        Assert.Equal(0, report["passed"]!.GetValue<int>());
    }

    [Fact]
    public void ExecutionInputLimitCountsTokensInsteadOfJsonBytes()
    {
        var body = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = string.Concat(Enumerable.Repeat("A local repository file.\n", 6000)) }) }.ToJsonString();
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(body) > 96000);
        Assert.InRange(KeyVaultBenchmarkModel.ExecutionInputEstimate(body), 4097, 96000);
        Assert.True(KeyVaultBenchmarkModel.ExecutionInputEstimate(string.Concat(Enumerable.Repeat(body, 10))) > 96000);
    }
    [Fact]
    public void OrdinaryRuntimeInferenceUsesTheSameCampaignInputAllowanceAsTheProxy()
    {
        var request = new LLMRequest { Prompt = string.Concat(Enumerable.Repeat("<span data-item=\"observed\">An observed product</span>", 30000)), MaxTokens = 8192 };
        var snapshot = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        Assert.True(KeyVaultBenchmarkModel.ExecutionInputEstimate(snapshot) > 96000);
        var error = Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() =>
            KeyVaultBenchmarkModel.CreateDispatchRequest(request, "pinned", "pinned", execution: true));
        Assert.Equal(GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetExceeded, error.Code);
        Assert.Equal("not_started", error.Details!["dispatch_status"]!.ToString());
        Assert.Equal("request_input_allowance", error.Details["classification"]!.ToString());
        Assert.Equal("input_tokens", error.Details["limit_kind"]!.ToString());
        Assert.Equal("conservative_token_estimate", error.Details["measurement"]!.ToString());
        Assert.Equal(96000, error.Details["limit"]!.GetValue<int>());
        var dispatched = JsonSerializer.Deserialize(snapshot, PlanningJsonContext.Default.LLMRequest)!;
        dispatched.Provider = "pinned"; dispatched.Model = "pinned"; dispatched.UseBackgroundMode = false;
        Assert.Equal(KeyVaultBenchmarkModel.ExecutionInputEstimate(JsonSerializer.Serialize(dispatched, PlanningJsonContext.Default.LLMRequest)),
            error.Details["current"]!.GetValue<int>());
        Assert.DoesNotContain("observed product", error.Details.ToJsonString());
        Assert.Equal(snapshot, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        request.Prompt = "Observed input";
        Assert.Equal(8192, KeyVaultBenchmarkModel.CreateDispatchRequest(request, "pinned", "pinned", execution: true).MaxTokens);
        request.MaxTokens = 32769;
        Assert.Throws<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() =>
            KeyVaultBenchmarkModel.CreateDispatchRequest(request, "pinned", "pinned", execution: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CampaignAdmissionFailureAllowsDurableCleanupWithoutInference(bool mapping)
    {
        var definition = GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: model
                    type: llm.call
                    input: {model: pinned, prompt: "${data.inputs.observation}", max_tokens: 8192}
                finally:
                  - {id: cleanup, type: set, input: {value: cleaned}}
            """);
        JsonNode observation = JsonValue.Create(string.Concat(Enumerable.Repeat("<span>A</span>", 60000)))!;
        if (mapping)
        {
            var step = definition.Workflows["main"].Steps[0]; step.Type = "mapping.dynamic";
            step.Input = new JsonObject { ["sources"] = new JsonObject { ["snapshot"] = "${data.inputs.observation}" },
                ["objective"] = "Extract the first ten observed entries", ["binding"] = "facts", ["producer_contract"] = "fixture" };
            step.OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"object","properties":{"items":{"type":"array","items":{"type":"string"},"maxItems":10}},"required":["items"]}},"required":["value"]}""")!.AsObject();
            observation = new JsonObject { ["pages"] = new JsonArray(Enumerable.Range(0, 53).Select(page => (JsonNode)new JsonObject
            {
                ["records"] = new JsonArray(Enumerable.Range(0, page == 52 ? 21 : 30).Select(index => (JsonNode)new JsonObject
                { ["text"] = "observed/" + page + "/" + index, ["metadata"] = string.Concat(Enumerable.Repeat("<span>Original</span>", 100)) }).ToArray())
            }).ToArray()) };
        }
        var document = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(definition);
        var store = new InMemoryWorkflowRunStore(); var client = new AdmissionClient();
        var engine = new WorkflowEngine { LLMClient = client, LlmDefaults = new() { Model = "pinned" }, RunStore = store,
            Limits = new() { TenantId = "tenant", RunId = "run", MaxMappingInputTokens = 96000 } };
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!],
            new JsonObject { ["observation"] = observation }, Ct);
        Assert.Equal("LLM_BUDGET_EXCEEDED", result.Error!.Code);
        Assert.Equal("request_input_allowance", result.Error.Details!["classification"]!.ToString());
        Assert.Equal("model", result.Error.Details["failed_step_id"]!.ToString());
        Assert.Equal(0, client.Dispatches);
        var run = (await store.ReadAsync("tenant", "run", Ct))!;
        Assert.Equal("failed", run.Invocations["/workflow/main/step/model"].Status);
        var receipt = run.Invocations["/workflow/main/step/model"];
        if (mapping)
        {
            Assert.False(receipt.ExternalCompletionObserved); // Admission failed before any model child existed.
            Assert.NotNull(receipt.CompletedAt);
            Assert.Equal("request_input_allowance", receipt.Error!.Details!["classification"]!.ToString());
            Assert.DoesNotContain("/workflow/main/step/model/mapping/0", run.Invocations.Keys);
            Assert.Equal(96000, result.Error.Details["limit"]!.GetValue<int>());
            Assert.Equal("not_started", result.Error.Details["dispatch_status"]!.ToString());
        }
        else Assert.True(receipt.ExternalCompletionObserved);
        Assert.Equal("completed", Assert.Single(run.Invocations.Values, i => i.IsFinalization && i.StepType == "set").Status);
        var originalError = result.Error.Details.ToJsonString();
        var recovered = await new WorkflowEngine { RunStore = store, LLMClient = client }
            .ResumeAsync("tenant", "run", run.Revision, document.Workflows[document.Entrypoint!], Ct);
        Assert.False(recovered.Success);
        Assert.Equal(originalError, recovered.Error!.Details!.ToJsonString());
        Assert.Equal(0, client.Dispatches);
    }
    private sealed class AdmissionClient : ILLMClient, ILLMCapabilityResolver
    {
        public int Dispatches;
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(96000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            _ = KeyVaultBenchmarkModel.CreateDispatchRequest(request, "pinned", "pinned", execution: true);
            Dispatches++; return Task.FromResult(new LLMResponse());
        }
    }

    [Fact]
    public async Task ExecutionHasItsOwnAttemptPolicyAndStillSharesTheSpendingCeiling()
    {
        var campaign = new BenchmarkCampaign(new Records(), "execution-limit");
        var journal = new BenchmarkHttpJournal(campaign, "execution:1:hash", 100, 20, 5m, sessionAttemptLimit: null);
        var state = new LLMHttpRetryState();
        for (var i = 0; i < 10; i++) { state.Attempts.Add(new() { Id = i.ToString() }); await journal.SaveAsync(state, Ct); }
        Assert.Equal(10, (await journal.LoadAsync(Ct))!.Attempts.Count);
        Assert.Equal(50m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["cost_upper_bound_eur"]!.GetValue<decimal>());
        state.Attempts.Add(new() { Id = "over-budget" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveAsync(state, Ct));
        Assert.Equal(10, (await journal.LoadAsync(Ct))!.Attempts.Count);
        var old = new BenchmarkHttpJournal(campaign, "planning:1:hash", 100, 20, .01m);
        await old.PrepareAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BenchmarkHttpJournal(campaign, "planning:1:hash", 100, 20, .01m, sessionAttemptLimit: null).SaveAsync(new(), Ct));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProviderStageIsEvidenceOnlyAndCannotReleaseReservationsOrAuthorizeReplay(bool authentication, bool priorAttempt)
    {
        var campaign = new BenchmarkCampaign(new Records(), "provider-stage");
        const string id = "one:1:hash";
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m);
        await journal.PrepareAsync(Ct);
        JsonObject? accountingBeforeFailure = null;
        var dispatches = 0;
        var request = new LLMRequest { ClientRequestId = id };
        await Assert.ThrowsAsync<LLMClientException>(() => campaign.CallAsync(request, _ => Task.CompletedTask, async ct =>
        {
            dispatches++;
            if (priorAttempt) await journal.SaveAsync(new() { Attempts = [new() { Id = id + ":1" }] }, ct);
            accountingBeforeFailure = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: ct);
            throw new LLMClientException(LLMClientFailureKind.Transport, "redacted failure", retryable: true)
            { Stage = authentication ? LLMClientFailureStage.Authentication : null };
        }, Ct));

        var failure = (await campaign.LoadAsync("planning-evaluation-failures", id, Ct))!;
        Assert.Equal("dispatch", failure["stage"]!.ToString());
        Assert.Equal(authentication ? "authentication" : null, failure["provider_stage"]?.ToString());
        Assert.Equal("uncertain_dispatch", campaign.StopReason);
        Assert.True(await campaign.HasUncertainRequestAsync(Ct));
        Assert.Equal(priorAttempt ? 1 : 0, (await journal.LoadAsync(Ct))!.Attempts.Count);
        Assert.True(JsonNode.DeepEquals(accountingBeforeFailure, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(request, _ => Task.CompletedTask,
            _ => { dispatches++; return Task.FromResult(new LLMResponse()); }, Ct));
        Assert.Equal(1, dispatches);
    }

    [Fact]
    public async Task FailedPreflightRetainsEvidenceWithoutInventingDispatch()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "preflight");
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = "one:1:hash" },
            _ => throw new InvalidOperationException("No currency quote."), _ => throw new Exception("Must not dispatch"), Ct));
        Assert.Null(await campaign.LoadAsync("planning-evaluation-requests", "one:1:hash", Ct));
        var failure = (await campaign.LoadAsync("planning-evaluation-failures", "one:1:hash", Ct))!;
        Assert.Equal("preflight", failure["stage"]!.ToString()); Assert.Equal("currency_quote_unavailable", failure["reason"]!.ToString());
        Assert.False(await campaign.HasUncertainRequestAsync(Ct));
        await new BenchmarkCampaign(records, "preflight").CallAsync(new() { ClientRequestId = "two:1:hash" }, _ => Task.CompletedTask,
            _ => Task.FromResult(new LLMResponse()), Ct);
        Assert.NotNull(await campaign.LoadAsync("planning-evaluation-failures", "one:1:hash", Ct));
    }
    [Fact]
    public async Task LiveCohortKeepsExecutionAccountingSeparateAndCountsMissingRuns()
    {
        var campaign = new BenchmarkCampaign(new Records(), "live-accounting");
        async Task Record(string id, int? status, bool usage)
        {
            var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m);
            var state = new LLMHttpRetryState { Attempts = [new() { Id = id }] };
            await journal.SaveAsync(state, Ct); state.Attempts[0].Status = status; await journal.SaveAsync(state, Ct);
            if (usage) await journal.CompleteAsync(new() { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m }, Ct);
        }
        await Record("final-amazon-1:1:hash", 200, true);
        await Record("final-amazon-1-execution:1:hash", null, false);
        await Record("diagnostic-other:1:hash", 200, true);
        var accounting = await LiveCampaignEvidence.AccountingAsync(campaign, "final-amazon-1");
        Assert.Equal(1L, accounting["planning"]!["physical_attempts"]!.GetValue<long>());
        Assert.Equal(10L, accounting["planning"]!["known_input_tokens"]!.GetValue<long>());
        Assert.Equal(1L, accounting["execution"]!["unknown_attempts"]!.GetValue<long>());
        Assert.Equal(1m, accounting["execution"]!["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(3L, accounting["campaign"]!["calls"]!.GetValue<long>());
        var report = await LiveCampaignEvidence.ReportAsync(campaign);
        Assert.Equal(6, report["runs"]!.AsArray().Count); Assert.False(report["complete"]!.GetValue<bool>());
        var manifest = new JsonObject { ["production_sha"] = "one" };
        LiveCampaignEvidence.RequireMatch(manifest, manifest.DeepClone().AsObject());
        Assert.Throws<InvalidOperationException>(() => LiveCampaignEvidence.RequireMatch(manifest, new() { ["production_sha"] = "two" }));
    }
    [Theory]
    [InlineData("/v1/responses", "max_output_tokens")]
    [InlineData("/v1/chat/completions", "max_completion_tokens")]
    public void ExecutionInferenceUsesBoundedVisibleInputAndPinnedReasoning(string path, string field)
    {
        var original = JsonNode.Parse("""{"model":"pinned","messages":[{"role":"user","content":"Review"}],"max_tokens":65536,"stream":true,"reasoning_effort":"high"}""")!.AsObject();
        var before = original.ToJsonString(); var prepared = KeyVaultBenchmarkModel.PrepareProxyPayload(original, "pinned", path);
        Assert.Equal(before, original.ToJsonString()); Assert.Equal(32768, prepared[field]!.GetValue<int>()); Assert.Null(prepared["max_tokens"]);
        Assert.Equal("medium", (path.EndsWith("responses", StringComparison.Ordinal) ? prepared["reasoning"]!["effort"] : prepared["reasoning_effort"])!.ToString());
        foreach (var invalid in new[] { "{\"model\":\"other\"}", "{\"model\":\"pinned\",\"n\":2}", "{\"model\":\"pinned\",\"conversation\":\"hidden\"}", "{\"model\":\"pinned\",\"messages\":[{\"type\":\"input_image\"}]}" })
            Assert.Throws<InvalidOperationException>(() => KeyVaultBenchmarkModel.PrepareProxyPayload(JsonNode.Parse(invalid)!.AsObject(), "pinned", path));
    }
    [Fact]
    public void ExplicitStructuredSdkTransportRetainsReasoningAndItsActualStructuredWireBody()
    {
        var original = JsonNode.Parse("""{"model":"pinned","messages":[{"role":"system","content":"exact tool declarations"}],"reasoning_effort":"high","max_completion_tokens":8192,"stream":false,"response_format":{"type":"json_schema","json_schema":{"name":"decision","strict":true,"schema":{"type":"object"}}}}""")!.AsObject();
        var prepared = KeyVaultBenchmarkModel.PrepareProxyPayload(original, "pinned", "/v1/chat/completions", preserveSdkReasoning: true);
        Assert.True(JsonNode.DeepEquals(original, prepared));
        Assert.Equal("medium", KeyVaultBenchmarkModel.PrepareProxyPayload(original, "pinned", "/v1/chat/completions")["reasoning_effort"]!.ToString());
    }
    [Theory]
    [InlineData("/v1/chat/completions", true, "none")]
    [InlineData("/v1/chat/completions", false, "medium")]
    [InlineData("/v1/responses", true, "medium")]
    public void ExplicitSdkToolReasoningChangesOnlyTheSelectedChatToolField(string path, bool tools, string expected)
    {
        var original = JsonNode.Parse("""{"model":"pinned","messages":[{"role":"user","content":"Inspect repository"}],"max_tokens":8192,"reasoning_effort":"high"}""")!.AsObject();
        if (tools) original["tools"] = JsonNode.Parse("""[{"type":"function","function":{"name":"inspect","parameters":{"type":"object"}}}]""");
        var before = original.ToJsonString();
        var legacy = KeyVaultBenchmarkModel.PrepareProxyPayload(original, "pinned", path);
        var selected = KeyVaultBenchmarkModel.PrepareProxyPayload(original, "pinned", path, "none");
        Assert.Equal(before, original.ToJsonString());
        Assert.Equal(expected, (path.EndsWith("responses", StringComparison.Ordinal) ? selected["reasoning"]!["effort"] : selected["reasoning_effort"])!.ToString());
        if (!path.EndsWith("responses", StringComparison.Ordinal) && tools) selected["reasoning_effort"] = "medium";
        Assert.True(JsonNode.DeepEquals(legacy, selected));
    }

    [Theory]
    [InlineData("/v1/chat/completions", 0)]
    [InlineData("/v1/chat/completions", 1)]
    [InlineData("/v1/chat/completions", 2)]
    [InlineData("/v1/responses", 2)]
    public void ExplicitProviderDefaultOmitsOnlyChatToolReasoning(string path, int tools)
    {
        var original = JsonNode.Parse("""{"model":"pinned","messages":[{"role":"user","content":"Inspect repository"}],"max_tokens":8192,"stream":true,"reasoning_effort":"high"}""")!.AsObject();
        if (tools > 0) original["tools"] = tools == 1 ? new JsonArray() : JsonNode.Parse("""[{"type":"function","function":{"name":"inspect","parameters":{"type":"object"}}}]""");
        var before = original.ToJsonString();
        var legacy = KeyVaultBenchmarkModel.PrepareProxyPayload(original, "pinned", path);
        var selected = KeyVaultBenchmarkModel.PrepareProxyPayload(original, "pinned", path, "provider-default");
        Assert.Equal(before, original.ToJsonString());
        if (path.EndsWith("/chat/completions", StringComparison.Ordinal) && tools == 2)
        {
            Assert.False(selected.ContainsKey("reasoning_effort"));
            selected["reasoning_effort"] = "medium";
        }
        Assert.True(JsonNode.DeepEquals(legacy, selected));
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("none;ignore-policy")]
    public void InvalidSdkToolReasoningIsRejectedBeforePayloadPreparation(string effort)
        => Assert.Throws<ArgumentException>(() => KeyVaultBenchmarkModel.PrepareProxyPayload(
            new JsonObject { ["model"] = "pinned" }, "pinned", "/v1/chat/completions", effort));

    [Theory]
    [InlineData(null)]
    [InlineData("completions")]
    [InlineData("responses")]
    public void ExplicitSdkWireApiUsesExistingConfigurationWithoutChangingOtherSettings(string? wireApi)
    {
        var settings = new McpServerOptions { Type = "stdio", Command = "dotnet", Args = ["tool.dll"],
            EnvironmentVariables = new() { ["Code__AllowWrites"] = "false", ["Code__Copilot__Providers__other__WireApi"] = "completions" } };
        var before = JsonSerializer.SerializeToNode(settings)!.AsObject();
        CampaignInferenceProxy.ConfigureWireApi(settings, "selected", wireApi);
        if (wireApi is not null)
        {
            Assert.Equal(wireApi, settings.EnvironmentVariables!["Code__Copilot__Providers__selected__WireApi"]);
            settings.EnvironmentVariables.Remove("Code__Copilot__Providers__selected__WireApi");
        }
        Assert.True(JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(settings)));
        Assert.Throws<ArgumentException>(() => CampaignInferenceProxy.ConfigureWireApi(settings, "selected", "automatic"));
        Assert.Throws<ArgumentException>(() => CampaignInferenceProxy.ConfigureWireApi(settings, "selected:other", "responses"));
        Assert.True(JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(settings)));
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(200, false)]
    [InlineData(null, false)]
    public async Task OnlyVerifiedSchemaRejectionsCanCloseWithoutInventingCompletion(int? status, bool canClose)
    {
        var campaign = new BenchmarkCampaign(new Records(), "schema-rejection"); const string id = "session:1:hash";
        await campaign.SaveAsync("planning-evaluation-requests", id, new() { ["clientRequestId"] = id }, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", id, new() { ["status_code"] = 400, ["safe_provider_code"] = "invalid_json_schema" }, Ct);
        var journal = new BenchmarkHttpJournal(campaign, id, 96000, 32768, 1m);
        var transport = new LLMHttpRetryState { Attempts = [new() { Id = "one" }] };
        await journal.SaveAsync(transport, Ct); transport.Attempts[0].Status = status; await journal.SaveAsync(transport, Ct);
        if (!canClose)
        { await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainSchemaRejectionAsync(id, Ct)); return; }
        await campaign.RetainSchemaRejectionAsync(id, Ct);
        Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = id }, _ => Task.CompletedTask, _ => Task.FromResult(new LLMResponse()), Ct));
        await campaign.CallAsync(new() { ClientRequestId = "next:1:hash" }, _ => Task.CompletedTask, _ => Task.FromResult(new LLMResponse()), Ct);
        Assert.Equal(1, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["calls"]!.GetValue<long>());
    }
    [Theory]
    [InlineData("exact", true)]
    [InlineData("other_schema_constraint", true)]
    [InlineData("ordinary400", false)]
    [InlineData("malformed_body", false)]
    [InlineData("nonobject_body", false)]
    [InlineData("missing_body", false)]
    [InlineData("wrong_param", false)]
    [InlineData("wrong_type", false)]
    [InlineData("wrong_format_name", false)]
    [InlineData("non_null_code", false)]
    [InlineData("missing_code", false)]
    [InlineData("empty_schema_detail", false)]
    [InlineData("success", false)]
    [InlineData("unknown", false)]
    [InlineData("usage", false)]
    [InlineData("receipt", false)]
    [InlineData("retryable", false)]
    [InlineData("wrong_count", false)]
    [InlineData("wrong_stage", false)]
    [InlineData("wrong_failure_kind", false)]
    [InlineData("wrong_request", false)]
    [InlineData("unstructured", false)]
    public async Task CodeLessSchemaRejectionRequiresTheExactOriginalTransportEnvelope(string evidence, bool canClose)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "code-less-schema");
        const string id = "session:2:hash";
        var request = new JsonObject { ["clientRequestId"] = evidence == "wrong_request" ? "other:2:hash" : id,
            ["structuredOutputStrict"] = true, ["structuredOutputSchema"] = new JsonObject { ["type"] = "object" } };
        if (evidence == "unstructured") request.Remove("structuredOutputSchema");
        var failure = new JsonObject { ["stage"] = evidence == "wrong_stage" ? "preflight" : "dispatch",
            ["kind"] = evidence == "wrong_failure_kind" ? "Transport" : "InvalidRequest", ["status_code"] = 400,
            ["safe_provider_code"] = null, ["attempt_count"] = evidence == "wrong_count" ? 2 : 1,
            ["retryable"] = evidence == "retryable" };
        var error = new JsonObject
        {
            ["type"] = evidence == "wrong_type" ? "server_error" : "invalid_request_error",
            ["param"] = evidence == "wrong_param" ? "messages" : "response_format",
            ["code"] = evidence == "non_null_code" ? "another_error" : null,
            ["message"] = evidence switch
            {
                "ordinary400" => "Invalid value for response_format.",
                "wrong_format_name" => "Invalid schema for response_format 'unrelated': invalid schema.",
                "empty_schema_detail" => "Invalid schema for response_format 'output': ",
                "other_schema_constraint" => "Invalid schema for response_format 'output': required must list every declared property.",
                _ => "Invalid schema for response_format 'output': context=('anyof', '2', 'properties', 'minItems'), $ref cannot have keywords {'description'}."
            }
        };
        if (evidence == "missing_code") error.Remove("code");
        var body = evidence switch { "malformed_body" => "{", "nonobject_body" => "[]", "missing_body" => null,
            _ => new JsonObject { ["error"] = error }.ToJsonString() };
        await campaign.SaveAsync("planning-evaluation-requests", id, request, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", id, failure, Ct);
        var journal = new BenchmarkHttpJournal(campaign, id, 3000, 8192, 1m);
        var transport = new LLMHttpRetryState { Attempts = [new() { Id = "one" }] };
        await journal.SaveAsync(transport, Ct);
        transport.Attempts[0].Status = evidence switch { "success" => 200, "unknown" => null, _ => 400 };
        transport.Attempts[0].Body = body;
        await journal.SaveAsync(transport, Ct);
        if (evidence == "usage")
        {
            var row = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
            row["usage"] = new JsonObject(); await campaign.SaveAsync(BenchmarkHttpJournal.Collection, id, row, Ct);
        }
        if (evidence == "receipt") await campaign.SaveAsync("planning-evaluation-receipts", id, new(), Ct);
        var collections = new[] { "planning-evaluation-requests", "planning-evaluation-failures", BenchmarkHttpJournal.Collection,
            "planning-evaluation-receipts" };
        var originals = new Dictionary<string, string?>();
        foreach (var collection in collections) originals[collection] = (await campaign.LoadAsync(collection, id, Ct))?.ToJsonString();
        var writes = records.Writes;
        if (!canClose)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainSchemaRejectionAsync(id, Ct));
            Assert.Null(await campaign.LoadAsync("planning-evaluation-closures", id, Ct));
            Assert.Equal(writes, records.Writes);
        }
        else
        {
            var before = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
            await campaign.RetainSchemaRejectionAsync(id, Ct);
            var closure = (await campaign.LoadAsync("planning-evaluation-closures", id, Ct))!;
            Assert.Equal("provider_schema_rejected", closure["reason"]!.ToString());
            Assert.Equal("retained_response_format_schema_error", closure["verification"]!.ToString());
            Assert.Equal(writes + 1, records.Writes);
            await campaign.RetainSchemaRejectionAsync(id, Ct);
            Assert.Equal(writes + 1, records.Writes);
            Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", id, Ct));
            Assert.Null((await campaign.LoadAsync("planning-evaluation-failures", id, Ct))!["safe_provider_code"]);
            Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = id },
                _ => Task.CompletedTask, _ => throw new Exception("A rejected request must never dispatch again."), Ct));
            await campaign.CallAsync(new() { ClientRequestId = "fresh:1:hash" }, _ => Task.CompletedTask,
                _ => Task.FromResult(new LLMResponse()), Ct);
        }
        foreach (var collection in collections)
            Assert.Equal(originals[collection], (await campaign.LoadAsync(collection, id, Ct))?.ToJsonString());
    }
    [Fact]
    public void LiveAdapterUsesSynchronousRecoveryWithoutChangingReservedRequest()
    {
        var request = new LLMRequest
        {
            ClientRequestId = "session:1:hash", Prompt = "business request", UseBackgroundMode = true,
            DisableTransportRetries = true, MaxTokens = 32768,
            StructuredOutputSchema = new JsonObject { ["type"] = "object" }, StructuredOutputStrict = true
        };
        var original = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var dispatched = KeyVaultBenchmarkModel.CreateDispatchRequest(request, "configured-provider", "configured-model");
        Assert.False(dispatched.UseBackgroundMode); Assert.False(dispatched.DisableTransportRetries);
        Assert.Equal("configured-provider", dispatched.Provider); Assert.Equal("configured-model", dispatched.Model);
        Assert.Equal(request.ClientRequestId, dispatched.ClientRequestId); Assert.Equal(32768, dispatched.MaxTokens);
        Assert.True(dispatched.StructuredOutputStrict);
        Assert.True(JsonNode.DeepEquals(request.StructuredOutputSchema, dispatched.StructuredOutputSchema));
        var boundedRuntime = KeyVaultBenchmarkModel.CreateDispatchRequest(request, "configured-provider", "configured-model", execution: true);
        Assert.True(boundedRuntime.DisableTransportRetries);
        dispatched.StructuredOutputSchema!["type"] = "string";
        Assert.Equal(original, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
    }

    [Fact]
    public void LiveAdapterRejectsToolsBeforeDispatch()
    {
        var request = new LLMRequest { Tools = [new()] };
        Assert.Throws<InvalidOperationException>(() => KeyVaultBenchmarkModel.CreateDispatchRequest(request, "provider", "model"));
    }

    [Theory]
    [InlineData("Allow once")]
    [InlineData("Refuse")]
    public async Task LiveHumanInputRejectsWrongWrappersWithoutAbortingOrChoosingAnAnswer(string choice)
    {
        var payload = new JsonObject { ["mode"] = "choice", ["choices"] = new JsonArray("Allow once", "Refuse"),
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "answer", ["type"] = "select" }) };
        using var input = new StringReader("invalid JSON\n{\"answer\":\"Allow once\"}\n" + new JsonObject { ["response"] = choice }.ToJsonString() + "\n");
        using var output = new StringWriter();
        var response = await LiveWorkflowEvaluation.ReadHumanAnswerAsync(payload, input, output, Ct);
        Assert.Equal(choice, response!["response"]!.GetValue<string>());
        Assert.Contains("Required response schema", output.ToString());
        Assert.Contains("no approval submitted", output.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => LiveWorkflowEvaluation.ReadHumanAnswerAsync(payload,
            new StringReader("{\"answer\":\"Allow once\"}\n"), output, Ct));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LiveWorkflowEvaluation.ReadHumanAnswerAsync(payload,
            new StringReader("{\"response\":\"Allow once\"}\n"), output, cancelled.Token));
    }

    [Fact]
    public async Task LiveConsoleCancellationFinishesWithoutInputAndNeverAcceptsALateApproval()
    {
        using var cancelled = new CancellationTokenSource();
        using var reader = new BlockingConsoleReader();
        using var input = TextReader.Synchronized(reader); // Console.In uses this synchronous reader.
        using var output = new StringWriter();
        var payload = new JsonObject { ["mode"] = "confirm", ["choices"] = new JsonArray("approve", "reject") };
        var answer = Task.Run(() => LiveWorkflowEvaluation.ReadHumanAnswerAsync(payload, input, output, cancelled.Token), Ct);
        try
        {
            await reader.Started.Task.WaitAsync(Ct);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer.WaitAsync(TimeSpan.FromSeconds(2), Ct));
        }
        finally { reader.Release.Set(); }
        await reader.Finished.Task.WaitAsync(Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer);
    }

    private sealed class BlockingConsoleReader : TextReader
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        public override string ReadLine()
        {
            Started.TrySetResult();
            Release.Wait();
            Finished.TrySetResult();
            return "{\"response\":true}";
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Release.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task HttpRecoveryPreservesUnknownAllowanceAndReplaysAfterReceiptWriteFailure()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "recovery");
        var request = new LLMRequest { ClientRequestId = "session:1:hash", Prompt = "immutable", StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        var ids = new List<string>();
        using var http = new HttpClient(new Handler(message =>
        {
            ids.Add(message.Headers.GetValues("X-Client-Request-Id").Single());
            if (ids.Count == 1) throw new TaskCanceledException();
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}") };
        }));
        Task<LLMResponse> Dispatch(CancellationToken ct) => DispatchAsync(campaign, request.ClientRequestId!, http, ct);
        records.FailCollection = "planning-evaluation-receipts";
        await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(request, _ => Task.CompletedTask, Dispatch, Ct, allowHttpRecovery: true));
        Assert.Equal(2, ids.Distinct().Count());
        var before = await BenchmarkHttpJournal.AccountingAsync(campaign, request.ClientRequestId, ct: Ct);
        Assert.Equal(2, before["calls"]!.GetValue<long>()); Assert.Equal(.1m, before["known_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(1m, before["reserved_cost_eur"]!.GetValue<decimal>()); Assert.Equal(1.1m, before["cost_upper_bound_eur"]!.GetValue<decimal>());
        Assert.Equal(100L, before["reserved_input_tokens"]!.GetValue<long>()); Assert.Equal(20L, before["reserved_output_tokens"]!.GetValue<long>());
        Assert.NotNull(await campaign.LoadAsync("planning-evaluation-failures", request.ClientRequestId, Ct));
        records.FailCollection = null;
        campaign = new(records, "recovery");
        var response = await campaign.CallAsync(request, _ => Task.CompletedTask, Dispatch, Ct, allowHttpRecovery: true);
        Assert.Equal(1, response.Usage!["uncertain_attempts"]!.GetValue<int>());
        var replay = await campaign.CallAsync(request, _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), Ct, allowHttpRecovery: true);
        Assert.True(JsonNode.DeepEquals(response.Usage, replay.Usage)); Assert.Equal(2, ids.Count);
        Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(campaign, request.ClientRequestId, ct: Ct)));
        Assert.False(await campaign.HasUncertainRequestAsync(Ct)); // The original uncertain attempt still exists in its HTTP journal.
        Assert.Equal("immutable", (await campaign.LoadAsync("planning-evaluation-requests", request.ClientRequestId, Ct))!["prompt"]!.ToString());
        // Recovery does not poison the rest of the campaign.
        await campaign.CallAsync(new() { ClientRequestId = "next:1:hash" }, _ => Task.CompletedTask, _ => Task.FromResult(new LLMResponse()), Ct);
        Assert.Null(campaign.StopReason);
    }
    [Theory]
    [InlineData(null)]
    [InlineData(502)]
    public async Task ConservativeAdmissionRejectsCostAndCallExhaustionWithoutChangingLedger(int? status)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "limits");
        var journal = new BenchmarkHttpJournal(campaign, "session:1:hash", 100, 20, 26m);
        var state = new LLMHttpRetryState { Fingerprint = "fixed", Attempts = [new() { Id = "first", Failure = "timeout" }] };
        await journal.SaveAsync(state, Ct);
        if (status is not null) { state.Attempts[0].Status = status; await journal.SaveAsync(state, Ct); }
        state.Attempts.Add(new() { Id = "second" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveAsync(state, Ct));
        Assert.Single((await journal.LoadAsync(Ct))!.Attempts);
        Assert.Equal(26m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["cost_upper_bound_eur"]!.GetValue<decimal>());
        var other = new BenchmarkCampaign(records, "calls"); var limited = new BenchmarkHttpJournal(other, "session:1:hash", 100, 20, .1m);
        state = new() { Fingerprint = "fixed" };
        for (var i = 0; i < 8; i++) { state.Attempts.Add(new() { Id = i.ToString() }); await limited.SaveAsync(state, Ct); state.Attempts[^1].Status = 503; await limited.SaveAsync(state, Ct); }
        state.Attempts.Add(new() { Id = "ninth" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => limited.SaveAsync(state, Ct));
        Assert.Equal(8, (await limited.LoadAsync(Ct))!.Attempts.Count);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(200, true)]
    [InlineData(400, false)]
    [InlineData(429, false)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(599, true)]
    public async Task UnsettledHttpAttemptsUseTheSameConservativeReservationInEveryReport(int? status, bool reserved)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "http-reserves");
        const string id = "run:1:hash";
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 3m);
        var transport = new LLMHttpRetryState { Fingerprint = "fixed", Attempts = [new() { Id = "attempt" }] };
        await journal.SaveAsync(transport, Ct);
        if (status is not null) { transport.Attempts[0].Status = status; await journal.SaveAsync(transport, Ct); }
        var original = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
        var hash = PlanningGraphCompiler.Fingerprint(original.ToJsonString());
        var writes = records.Writes;
        var restarted = new BenchmarkCampaign(records, campaign.Id);
        using var model = new KeyVaultBenchmarkModel(restarted, new LLMOptions());
        var campaignUsage = await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct);
        var liveUsage = (await LiveCampaignEvidence.AccountingAsync(restarted, "run"))["planning"]!;
        var partialUsage = (await model.PartialUsageAsync(id, Ct))!;
        var closedUsage = PlanningBenchmarkMeasurements.ClosedHttpUsage([original], 1)!;
        foreach (var usage in new JsonNode[] { campaignUsage, liveUsage, partialUsage, closedUsage })
        {
            Assert.Equal(reserved ? 3m : 0m, usage["reserved_cost_eur"]!.GetValue<decimal>());
            Assert.Equal(reserved ? 100L : 0L, usage["reserved_input_tokens"]!.GetValue<long>());
            Assert.Equal(reserved ? 20L : 0L, usage["reserved_output_tokens"]!.GetValue<long>());
        }
        Assert.Equal(reserved ? 1L : 0L, campaignUsage["unknown_attempts"]!.GetValue<long>());
        Assert.Equal(reserved ? 1 : 0, partialUsage["uncertain_attempts"]!.GetValue<int>());
        Assert.Equal(!reserved, closedUsage["usage_complete"]!.GetValue<bool>());
        Assert.Equal(0L, closedUsage["known_input_tokens"]!.GetValue<long>());
        Assert.Equal(0m, campaignUsage["known_cost_eur"]!.GetValue<decimal>());
        if (status is >= 500 and < 600)
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.CompleteAsync(new() { ["input_tokens"] = 0L, ["output_tokens"] = 0L, ["benchmark_cost_eur"] = 0m }, Ct));
        Assert.Equal(writes, records.Writes);
        Assert.Equal(hash, PlanningGraphCompiler.Fingerprint((await restarted.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString()));
        Assert.Null(await restarted.LoadAsync("planning-evaluation-receipts", id, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterVerifiedUsageKeepsEarlierServerFailureAndTimeoutReservedWithoutRewritingHistoricalUsage(bool historicalUsage)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "later-receipt");
        const string id = "run:1:hash";
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 3m);
        var transport = new LLMHttpRetryState { Fingerprint = "fixed" };
        foreach (var status in new int?[] { null, 502, 200 })
        {
            transport.Attempts.Add(new() { Id = "attempt-" + transport.Attempts.Count });
            await journal.SaveAsync(transport, Ct);
            if (status is not null) { transport.Attempts[^1].Status = status; await journal.SaveAsync(transport, Ct); }
        }
        var known = new JsonObject { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m };
        if (historicalUsage)
        {
            var old = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
            old["usage"] = known.DeepClone();
            old["usage"]!["transport_attempts"] = 3; old["usage"]!["uncertain_attempts"] = 1;
            old["usage"]!["reserved_input_tokens"] = 100L; old["usage"]!["reserved_output_tokens"] = 20L;
            old["usage"]!["reserved_cost_eur"] = 3m; old["usage"]!["benchmark_usage_bounded"] = true;
            await campaign.SaveAsync(BenchmarkHttpJournal.Collection, id, old, Ct);
        }
        else await journal.CompleteAsync(known, Ct);
        var original = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
        var hash = PlanningGraphCompiler.Fingerprint(original.ToJsonString());
        var writes = records.Writes;
        var restarted = new BenchmarkCampaign(records, campaign.Id);
        using var model = new KeyVaultBenchmarkModel(restarted, new LLMOptions());
        var completed = await new BenchmarkHttpJournal(restarted, id, 100, 20, 3m).CompleteAsync(known, Ct);
        var partial = (await model.PartialUsageAsync(id, Ct))!;
        var accounting = await BenchmarkHttpJournal.AccountingAsync(restarted, ct: Ct);
        var live = (await LiveCampaignEvidence.AccountingAsync(restarted, "run"))["planning"]!;
        var closed = PlanningBenchmarkMeasurements.ClosedHttpUsage([original], 3)!;
        foreach (var usage in new JsonNode[] { completed, partial, accounting, live, closed })
        {
            Assert.Equal(6m, usage["reserved_cost_eur"]!.GetValue<decimal>());
            Assert.Equal(200L, usage["reserved_input_tokens"]!.GetValue<long>());
            Assert.Equal(40L, usage["reserved_output_tokens"]!.GetValue<long>());
        }
        Assert.Equal(2, completed["uncertain_attempts"]!.GetValue<int>());
        Assert.Equal(10L, partial["input_tokens"]!.GetValue<long>());
        Assert.Equal(.1m, accounting["known_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(6.1m, accounting["cost_upper_bound_eur"]!.GetValue<decimal>());
        Assert.Equal(10L, closed["known_input_tokens"]!.GetValue<long>());
        Assert.False(closed["usage_complete"]!.GetValue<bool>());
        Assert.Null(closed["input_tokens"]);
        Assert.Equal(writes, records.Writes);
        Assert.Equal(hash, PlanningGraphCompiler.Fingerprint((await restarted.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!.ToJsonString()));
    }
    [Fact]
    public async Task HttpExhaustionAndCancellationCannotDispatchOnRestart()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "exhaustion"); var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; throw new HttpRequestException("unknown delivery"); }));
        var request = new LLMRequest { ClientRequestId = "session:1:hash" };
        for (var restart = 0; restart < 2; restart++)
        {
            campaign = new(records, "exhaustion");
            await Assert.ThrowsAsync<HttpRequestException>(() => campaign.CallAsync(request, _ => Task.CompletedTask,
                ct => DispatchAsync(campaign, request.ClientRequestId!, http, ct), Ct, allowHttpRecovery: true));
        }
        Assert.Equal(2, calls); Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", request.ClientRequestId, Ct));
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(2L, accounting["calls"]!.GetValue<long>()); Assert.Equal(2m, accounting["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal("uncertain_dispatch", campaign.StopReason);
    }
    [Fact]
    public async Task PreparedJournalResumesBeforeFirstSendAndOriginalSchemaCannotChange()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "prepared");
        var request = new LLMRequest { ClientRequestId = "session:1:hash", StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        await new BenchmarkHttpJournal(campaign, request.ClientRequestId, 100, 20, 1m).PrepareAsync(Ct);
        await campaign.SaveAsync("planning-evaluation-requests", request.ClientRequestId, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}") }; }));
        request.StructuredOutputSchema["type"] = "string";
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(request, _ => Task.CompletedTask, ct => DispatchAsync(campaign, request.ClientRequestId, http, ct), Ct, allowHttpRecovery: true));
        Assert.Equal(0, calls);
        request.StructuredOutputSchema["type"] = "object";
        await campaign.CallAsync(request, _ => Task.CompletedTask, ct => DispatchAsync(campaign, request.ClientRequestId, http, ct), Ct, allowHttpRecovery: true);
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task RecoveryPreservesChangedFailureEvidenceWithoutDuplicatingIdenticalFailures()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "history");
        var request = new LLMRequest { ClientRequestId = "session:1:hash" };
        await new BenchmarkHttpJournal(campaign, request.ClientRequestId, 100, 20, 1m).PrepareAsync(Ct);
        await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(request, _ => Task.CompletedTask, _ => throw new IOException(), Ct, allowHttpRecovery: true));
        for (var restart = 0; restart < 2; restart++)
            await Assert.ThrowsAsync<TimeoutException>(() => new BenchmarkCampaign(records, "history").CallAsync(request, _ => Task.CompletedTask, _ => throw new TimeoutException(), Ct, allowHttpRecovery: true));
        var failure = (await campaign.LoadAsync("planning-evaluation-failures", request.ClientRequestId, Ct))!;
        Assert.Equal("TimeoutException", failure["exception_type"]!.ToString());
        Assert.Equal("IOException", Assert.Single(failure["previous_failures"]!.AsArray())!["exception_type"]!.ToString());
    }
    private static async Task<LLMResponse> DispatchAsync(BenchmarkCampaign campaign, string id, HttpClient http, CancellationToken ct)
    {
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m);
        using var context = new LLMHttpRetryContext(id, journal).Activate();
        var response = await new OpenAiLLMProvider(http).CallAsync("test", new() { Url = "https://provider.example/v1", RetryPolicy = new() { BaseDelayMilliseconds = 1, MaxDelayMilliseconds = 1 } }, new() { Prompt = "fixed", MaxOutputTokens = 20 }, ct);
        var usage = await journal.CompleteAsync(new() { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m }, ct);
        return new() { Json = response.Json, Text = response.Text, Usage = usage };
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(action(request)); }


    private static async Task<(Records Records, BenchmarkCampaign Campaign, string RequestId)> PendingRecoveryAsync(bool receipt = true, string tenant = "benchmark", bool valid = false)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "recovery");
        var state = await CurrentPlanningFixture.PendingLiteralReviewAsync(tenant, "pending", Ct);
        state.Revision = 5;
        var request = state.PendingCall!.Request; var requestId = state.PendingCall.Id;
        await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:pending", new()
        {
            ["source"] = "original-build", ["manifest"] = new JsonObject { ["frozen"] = true },
            ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession),
            ["result"] = new JsonObject { ["status"] = "generating", ["planning_ms"] = 1500L, ["calls"] = state.ModelCalls,
                ["execution_status"] = "not_started", ["accounting"] = new JsonObject { ["retained"] = 42 } }
        }, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", requestId,
            JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        if (receipt) await campaign.SaveAsync("planning-evaluation-receipts", requestId,
            JsonSerializer.SerializeToNode(new LLMResponse { Json = valid
                ? JsonNode.Parse("""{"decision":{"verdict":"ok","reason":"The original numeric public result is present without additional effects."}}""")
                : new JsonObject { ["malformed"] = true } }, PlanningJsonContext.Default.LLMResponse)!.AsObject(), Ct);
        return (records, campaign, requestId);
    }

    [Fact]
    public async Task ValidPendingReviewReceiptRestoresReviewWithoutApprovingOrExecuting()
    {
        var (_, campaign, requestId) = await PendingRecoveryAsync(valid: true);
        var run = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct))!;
        var calls = run["session"]!["modelCalls"]!.GetValue<int>();
        var result = await campaign.RecoverPendingReceiptAsync("pending", 5, requestId, 600, "validator-build", true, Ct);
        Assert.Equal(PlanningStatus.FinalReview, result["status"]!.ToString());
        Assert.Empty(result["diagnostics"]!.AsArray());
        var saved = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct))!;
        Assert.True(saved["session"]!["construction"]!["complete"]!.GetValue<bool>());
        Assert.NotNull(saved["artifact_hash"]);
        Assert.Null(saved["session"]!["approvedHash"]); Assert.Null(saved["session"]!["pendingCall"]);
        Assert.Null(saved["execution_started"]); Assert.Equal(calls, saved["result"]!["calls"]!.GetValue<int>());
    }

    [Fact]
    public async Task PendingReceiptRecoveryCommitsOnceWithoutChangingFrozenEvidenceOrAccounting()
    {
        var (records, campaign, requestId) = await PendingRecoveryAsync();
        var original = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct))!;
        var before = await campaign.InspectAsync(Ct); var writes = records.Writes;
        var dry = await campaign.RecoverPendingReceiptAsync("pending", 5, requestId, 600, "validator-build", false, Ct);
        Assert.False(dry["applied"]!.GetValue<bool>()); Assert.Equal(writes, records.Writes);
        var applied = await campaign.RecoverPendingReceiptAsync("pending", 5, requestId, 600, "validator-build", true, Ct);
        Assert.Equal(6L, applied["revision"]!.GetValue<long>()); Assert.Equal(0, applied["new_model_calls"]!.GetValue<int>());
        Assert.NotEmpty(applied["diagnostics"]!.AsArray());
        Assert.Equal(writes + 1, records.Writes);
        var restarted = new BenchmarkCampaign(records, "recovery");
        var recovered = (await restarted.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct))!;
        Assert.Null(recovered["session"]!["pendingCall"]);
        Assert.Equal(original["session"]!["modelCalls"]!.GetValue<int>(), recovered["session"]!["modelCalls"]!.GetValue<int>());
        Assert.True(recovered["session"]!["activeMilliseconds"]!.GetValue<double>() >= original["session"]!["activeMilliseconds"]!.GetValue<double>() + 600);
        Assert.True(recovered["result"]!["planning_ms"]!.GetValue<long>() >= 2100);
        foreach (var key in new[] { "source", "manifest" }) Assert.True(JsonNode.DeepEquals(original[key], recovered[key]));
        Assert.True(JsonNode.DeepEquals(await LiveCampaignEvidence.AccountingAsync(restarted, "pending"), recovered["result"]!["accounting"]));
        Assert.True(JsonNode.DeepEquals(original["result"], recovered["receipt_recoveries"]![0]!["result_before"]));
        Assert.True(JsonNode.DeepEquals(original["session"], Assert.Single(recovered["receipt_recoveries"]!.AsArray())!["session_before"]));
        Assert.True(JsonNode.DeepEquals(before, await restarted.InspectAsync(Ct)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RecoverPendingReceiptAsync("pending", 5, requestId, 600, "validator-build", true, Ct));
        Assert.Equal(writes + 1, records.Writes);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("tenant")]
    [InlineData("receipt")]
    [InlineData("schema")]
    [InlineData("identity")]
    [InlineData("executed")]
    [InlineData("closed")]
    [InlineData("elapsed")]
    public async Task PendingReceiptRecoveryRejectsChangedOrUncertainAuthorityWithoutWrites(string failure)
    {
        var (records, campaign, requestId) = await PendingRecoveryAsync(receipt: failure != "receipt", tenant: failure == "tenant" ? "other" : "benchmark");
        if (failure == "schema")
        {
            var request = (await campaign.LoadAsync("planning-evaluation-requests", requestId, Ct))!;
            request["structuredOutputSchema"]!["type"] = "string";
            await campaign.SaveAsync("planning-evaluation-requests", requestId, request, Ct);
        }
        if (failure == "executed")
        {
            var run = (await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct))!;
            run["execution_started"] = "retained";
            await campaign.SaveAsync(SchemaPortabilityCampaign.Collection, "run:pending", run, Ct);
        }
        if (failure == "closed") await campaign.SaveAsync("planning-evaluation-closures", requestId, new(), Ct);
        var writes = records.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RecoverPendingReceiptAsync("pending", failure == "stale" ? 4 : 5,
            failure == "identity" ? "pending:3:hash" : requestId, failure == "elapsed" ? 18_000_001 : 600, "validator-build", true, Ct));
        Assert.Equal(writes, records.Writes);
    }

    [Fact]
    public async Task InterruptedRecoveryPublicationLeavesOriginalReceiptAvailableWithoutDuplicateCharging()
    {
        var (records, campaign, requestId) = await PendingRecoveryAsync();
        var original = await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct);
        var before = await campaign.InspectAsync(Ct);
        records.FailCollection = SchemaPortabilityCampaign.Collection;
        await Assert.ThrowsAsync<IOException>(() => campaign.RecoverPendingReceiptAsync("pending", 5, requestId, 600, "validator-build", true, Ct));
        Assert.True(JsonNode.DeepEquals(original, await campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:pending", Ct)));
        records.FailCollection = null;
        await new BenchmarkCampaign(records, "recovery").RecoverPendingReceiptAsync("pending", 5, requestId, 600, "validator-build", true, Ct);
        Assert.True(JsonNode.DeepEquals(before, await campaign.InspectAsync(Ct)));
    }

    [Fact]
    public async Task UncertainFailureRetainsSafeMetadataWithoutInventingAReceipt()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "recorded");
        await Assert.ThrowsAsync<LLMClientException>(() => campaign.CallAsync(new() { ClientRequestId = "session:1:hash", Prompt = "private prompt" }, _ => Task.CompletedTask,
            _ => throw new LLMClientException(LLMClientFailureKind.ServiceUnavailable, "raw body containing private data", true, 503, "overloaded", 1, false, 1000), Ct));
        var failure = (await campaign.LoadAsync("planning-evaluation-failures", "session:1:hash", Ct))!;
        Assert.Equal("ServiceUnavailable", failure["kind"]!.ToString()); Assert.Equal(503, failure["status_code"]!.GetValue<int>());
        Assert.Equal("dispatch", failure["stage"]!.ToString()); Assert.Equal(1, failure["attempt_count"]!.GetValue<int>());
        Assert.DoesNotContain("private", failure.ToJsonString()); Assert.DoesNotContain("raw body", failure.ToJsonString());
        Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", "session:1:hash", Ct));
        var writes = records.Writes; var inspection = await campaign.InspectAsync(Ct);
        Assert.Equal(1, inspection["reserved_requests"]!.GetValue<int>()); Assert.Equal(0, inspection["completed_receipts"]!.GetValue<int>());
        Assert.False(inspection["completion_receipts_complete"]!.GetValue<bool>()); Assert.Null(inspection["known_budget_cost"]);
        Assert.Equal("session:1:hash", Assert.Single(inspection["uncertain_requests"]!.AsArray())!.ToString());
        Assert.Equal(writes, records.Writes);
    }
    [Fact]
    public async Task NewCampaignIgnoresOldUncertaintyAndReplaysOwnReceiptsWithoutDispatch()
    {
        var records = new Records(); var old = new BenchmarkCampaign(records, "old"); var fresh = new BenchmarkCampaign(records, "fresh");
        await old.SaveAsync("planning-evaluation-requests", "pending", new() { ["private"] = "unchanged" }, Ct);
        var request = new LLMRequest { ClientRequestId = "session:1", StructuredOutputSchema = new JsonObject { ["type"] = "object" } }; var calls = 0;
        Task<LLMResponse> Dispatch(CancellationToken _) { calls++; return Task.FromResult(new LLMResponse { Text = "receipt" }); }
        Assert.Equal("receipt", (await fresh.CallAsync(request, _ => Task.CompletedTask, Dispatch, Ct)).Text);
        var reopened = new BenchmarkCampaign(records, "fresh");
        Assert.Equal("receipt", (await reopened.CallAsync(request, _ => throw new InvalidOperationException("Preflight must not rerun"), Dispatch, Ct)).Text);
        Assert.Equal(1, calls); Assert.True(await old.HasUncertainRequestAsync(Ct)); Assert.False(await fresh.HasUncertainRequestAsync(Ct));
        Assert.Equal("unchanged", (await old.LoadAsync("planning-evaluation-requests", "pending", Ct))!["private"]!.ToString());
    }
    [Fact]
    public async Task UncertainDispatchBlocksFurtherRequestsWithoutResend()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "fresh"); var calls = 0;
        Task<LLMResponse> Dispatch(CancellationToken _) { calls++; throw new IOException("Uncertain transport"); }
        await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(new() { ClientRequestId = "one" }, _ => Task.CompletedTask, Dispatch, Ct));
        var reopened = new BenchmarkCampaign(records, "fresh");
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.CallAsync(new() { ClientRequestId = "two" }, _ => Task.CompletedTask, Dispatch, Ct));
        Assert.Equal(1, calls); Assert.Equal("uncertain_dispatch", reopened.StopReason);
    }
    [Fact]
    public async Task CompletedReceiptSurvivesCancellationAndPinnedConfigurationCannotDrift()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "fresh");
        await campaign.PinAsync(new() { ["model"] = "one" }, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.PinAsync(new() { ["model"] = "two" }, Ct));
        using var cancelled = new CancellationTokenSource();
        await campaign.CallAsync(new() { ClientRequestId = "one" }, _ => Task.CompletedTask, _ => { cancelled.Cancel(); return Task.FromResult(new LLMResponse { Text = "durable" }); }, cancelled.Token);
        Assert.NotNull(await campaign.LoadAsync("planning-evaluation-receipts", "one", Ct));
    }
    [Fact]
    public async Task CheckpointsRetainReservedSchemaAndBudgetRefusalDoesNotReserveDispatch()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "fresh");
        var evidence = new JsonObject { ["session"] = new JsonObject { ["modelCalls"] = 1, ["pendingCall"] = new JsonObject { ["responseSchema"] = new JsonObject { ["type"] = "string" } } } };
        await campaign.SaveAsync("planning-evaluation-runs", "pilot:local:1", evidence, Ct);
        Assert.True(JsonNode.DeepEquals(evidence, await new BenchmarkCampaign(records, "fresh").LoadAsync("planning-evaluation-runs", "pilot:local:1", Ct)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = "one" }, _ => throw new InvalidOperationException("budget"), _ => throw new InvalidOperationException("must not dispatch"), Ct));
        Assert.False(await campaign.HasUncertainRequestAsync(Ct));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("missing_closure")]
    [InlineData("changed_run")]
    [InlineData("changed_request")]
    [InlineData("attempted")]
    [InlineData("receipt")]
    [InlineData("failure")]
    [InlineData("wrapped")]
    public async Task AdmissionAuditRequiresImmutableClosedZeroDispatchEvidenceAndNeverWrites(string? defect)
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "audit");
        var first = new BenchmarkHttpJournal(campaign, "session:1:hash", 100, 20, 1m);
        var state = new LLMHttpRetryState { Fingerprint = "first" };
        for (var i = 0; i < 8; i++)
        {
            state.Attempts.Add(new() { Id = i.ToString() }); await first.SaveAsync(state, Ct);
            state.Attempts[^1].Status = i == 7 ? 200 : 503; await first.SaveAsync(state, Ct);
        }
        var known = await first.CompleteAsync(new() { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m }, Ct);
        const string id = "session:2:hash", key = "source:fixture:case:1";
        var denied = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m); await denied.PrepareAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = id }, _ => Task.CompletedTask,
            async ct => { await denied.SaveAsync(new() { Attempts = [new() { Id = "denied" }] }, ct); throw new Exception("Must not dispatch"); }, Ct));
        var run = new JsonObject { ["session"] = new JsonObject { ["modelCalls"] = 2, ["pendingCall"] = new JsonObject { ["id"] = id } },
            ["result"] = new JsonObject { ["execution_correct"] = false, ["termination_reason"] = "session_http_budget", ["calls"] = 9 },
            ["usage_receipts"] = new JsonObject { ["session:1:hash"] = known, [id] = new JsonObject { ["transport_attempts"] = 0 } } };
        await campaign.SaveAsync("planning-evaluation-runs", key, run, Ct);
        if (defect == "wrapped")
            await campaign.SaveAsync("planning-evaluation-failures", id, new()
            { ["stage"] = "dispatch", ["exception_type"] = "LLMClientException", ["kind"] = "Unknown", ["retryable"] = false }, Ct);
        // Immutable historical data fixture: obsolete closure creators are removed.
        if (defect != "missing_closure") await campaign.SaveAsync("planning-evaluation-closures", id, new JsonObject
        {
            ["run_key"] = key, ["request_id"] = id, ["reason"] = "session_http_attempts_exhausted", ["outcome"] = "inconclusive",
            ["run_hash"] = PlanningGraphCompiler.Fingerprint(run.ToJsonString()),
            ["request_hash"] = PlanningGraphCompiler.Fingerprint((await campaign.LoadAsync("planning-evaluation-requests", id, Ct))!.ToJsonString()),
            ["accounting_at_closure"] = await BenchmarkHttpJournal.AccountingAsync(campaign, id, ct: Ct)
        }, Ct);
        switch (defect)
        {
            case "changed_run": run["result"]!["calls"] = 10; break;
            case "changed_request": await campaign.SaveAsync("planning-evaluation-requests", id, new() { ["prompt"] = "changed" }, Ct); break;
            case "attempted":
                var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
                journal["transport"]!["Attempts"]!.AsArray().Add((JsonNode)new JsonObject { ["Id"] = "unknown", ["Status"] = null });
                await campaign.SaveAsync(BenchmarkHttpJournal.Collection, id, journal, Ct); break;
            case "receipt": await campaign.SaveAsync("planning-evaluation-receipts", id, new(), Ct); break;
            case "failure": await campaign.SaveAsync("planning-evaluation-failures", id, new() { ["exception_type"] = "IOException" }, Ct); break;
        }
        var writes = records.Writes; var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var audit = await campaign.AuditAdmissionDenialAsync(key, run, Ct);
        Assert.Equal(defect is null or "wrapped", audit is not null); Assert.Equal(writes, records.Writes);
        Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct)));
        if (audit is not null)
        {
            Assert.Equal(0, audit["admitted_request_attempts"]!.GetValue<int>());
            Assert.Equal(8, audit["session_attempts"]!.GetValue<int>());
            Assert.Equal(9, audit["original_result"]!["calls"]!.GetValue<int>());
            Assert.False(audit["original_result"]!["execution_correct"]!.GetValue<bool>());
        }
    }
    private sealed class Records : IKeyVaultRecordStore, IKeyVaultRecordReader
    {
        internal int Writes;
        internal string? FailCollection;
        internal string? FailKey;
        internal bool ForbidList;
        internal int MetadataQueries;
        internal string? HideKey;
        internal Func<KeyVaultRecordMetadataPage, KeyVaultRecordMetadataPage>? MutateMetadata;
        internal readonly List<string> ReadKeys = [];
        private readonly Dictionary<string, KeyVaultRecordValue> _rows = [];
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); ReadKeys.Add(k); return Task.FromResult(k == HideKey ? null : _rows.GetValueOrDefault(c + t + k)); }
        public Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); if (c == FailCollection || k == FailKey) throw new IOException("Injected store failure"); Writes++; var row = new KeyVaultRecordValue(c, t, k, v, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); _rows[c + t + k] = row; return Task.FromResult(row); }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct = default) => ForbidList
            ? throw new InvalidOperationException("Tenant-wide history hydration is forbidden.")
            : Task.FromResult<IReadOnlyList<KeyVaultRecordValue>>(_rows.Values.Where(r => r.Collection == c && r.TenantId == t).ToArray());
        public Task<KeyVaultRecordMetadataPage> QueryMetadataAsync(string c, string t, string prefix, string a, KeyVaultRecordCursor? after = null, int count = 100, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); MetadataQueries++;
            var rows = _rows.Values.Where(r => r.Collection == c && r.TenantId == t && r.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Where(r => after is null || r.UpdatedAt.UtcTicks > after.UpdatedAtTicks || r.UpdatedAt.UtcTicks == after.UpdatedAtTicks && string.CompareOrdinal(r.Key, after.Key) > 0)
                .OrderBy(r => r.UpdatedAt).ThenBy(r => r.Key, StringComparer.Ordinal).Take(count + 1).ToArray();
            var selected = rows.Take(count).Select(r => new KeyVaultRecordMetadata(r.Key, r.CreatedAt, r.UpdatedAt, null, null)).ToArray();
            var page = new KeyVaultRecordMetadataPage(selected, selected.Length == 0 ? after : new(selected[^1].UpdatedAt.UtcTicks, selected[^1].Key), rows.Length > count);
            return Task.FromResult(MutateMetadata?.Invoke(page) ?? page);
        }
        public Task<KeyVaultRecordMetadata?> GetMetadataAsync(string c, string t, string k, string a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KeyVaultRecordRange?> ReadRangeAsync(string c, string t, string k, string a, string? version, int offset, int count, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
