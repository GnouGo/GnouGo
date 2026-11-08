using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkPermanentRetentionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("tools")]
    [InlineData("background")]
    [InlineData("identity")]
    [InlineData("status")]
    [InlineData("retry")]
    [InlineData("code")]
    [InlineData("parameter")]
    [InlineData("partial")]
    [InlineData("usage")]
    [InlineData("receipt")]
    [InlineData("success")]
    public async Task TerminalContextRejectionKeepsWholeHoldAndOriginalsAndForbidsDispatch(string? defect)
    {
        var records = new Records(); var campaign = await SeedAsync(records);
        const string id = "runtime-execution:9:hash";
        var request = new JsonObject { ["clientRequestId"] = id, ["maxTokens"] = 8192, ["tools"] = null, ["useBackgroundMode"] = false };
        var failure = new JsonObject { ["stage"] = "dispatch", ["kind"] = "InvalidRequest", ["status_code"] = 400,
            ["retryable"] = false, ["attempt_count"] = 1, ["retry_exhausted"] = false };
        var error = new JsonObject { ["message"] = "Input tokens exceed the configured limit.", ["type"] = "invalid_request_error",
            ["param"] = "messages", ["code"] = "context_length_exceeded" };
        if (defect == "tools") request["tools"] = new JsonArray(new JsonObject { ["name"] = "effect" });
        if (defect == "background") request["useBackgroundMode"] = true;
        if (defect == "identity") request["clientRequestId"] = "other";
        if (defect == "status") failure["status_code"] = 500;
        if (defect == "retry") failure["retryable"] = true;
        if (defect == "code") error["code"] = "other";
        if (defect == "parameter") error["param"] = "other";
        var body = new JsonObject { ["error"] = error };
        if (defect == "success") body["choices"] = new JsonArray();
        await campaign.SaveAsync("planning-evaluation-requests", id, request, Ct);
        await campaign.SaveAsync("planning-evaluation-failures", id, failure, Ct);
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 8192, 1m);
        await journal.SaveAsync(new() { Attempts = [new() { Id = id + ":attempt" }] }, Ct);
        await journal.SaveAsync(new() { Attempts = [new() { Id = id + ":attempt", Status = 400,
            Body = defect == "partial" ? "{\"error\":" : body.ToJsonString() }] }, Ct);
        if (defect == "usage")
        {
            var record = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct))!;
            record["usage"] = new JsonObject { ["benchmark_cost_eur"] = .5m, ["input_tokens"] = 1, ["output_tokens"] = 1, ["total_tokens"] = 2 };
            await campaign.SaveAsync(BenchmarkHttpJournal.Collection, id, record, Ct);
        }
        if (defect == "receipt") await campaign.SaveAsync("planning-evaluation-receipts", id, new(), Ct);
        var before = records.Snapshot(excludeClosures: true);
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        if (defect is not null) await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RetainContextRejectionAsync(id, Ct));
        else
        {
            await campaign.RetainContextRejectionAsync(id, Ct);
            var cold = new BenchmarkCampaign(records, "retention");
            await cold.RetainContextRejectionAsync(id, Ct);
            Assert.True(JsonNode.DeepEquals(accounting, await BenchmarkHttpJournal.AccountingAsync(cold, ct: Ct)));
            Assert.True((await cold.LoadAsync("planning-evaluation-closures", id, Ct))!["evidence"]!["reservation_retained"]!.GetValue<bool>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => cold.CallAsync(new LLMRequest { ClientRequestId = id },
                _ => Task.CompletedTask, _ => throw new InvalidOperationException("Must not dispatch"), Ct));
        }
        Assert.Equal(before, records.Snapshot(excludeClosures: true));
    }
}
