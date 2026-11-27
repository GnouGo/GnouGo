using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Models;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkPermanentRetentionTests
{
    [Theory]
    [InlineData("not_started", true)]
    [InlineData("unknown", false)]
    [InlineData(null, false)]
    [InlineData(null, true, "KeyVaultAccessException")]
    [InlineData(null, false, "IOException")]
    [InlineData(null, false, "duplicate")]
    [InlineData(null, false, "different-request")]
    public async Task RetentionBeforeCampaignEnvelopePreservesOnlyTheExactServerHold(string? dispatch, bool eligible, string? storageError = null)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "GnOuGo.ServerRetention", Guid.NewGuid().ToString("N"));
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(Path.Combine(workspace, ".GnOuGo/data/live-server", InterruptedServerRun));
        try
        {
            var campaign = await SeedInterruptedServerAsync(fixture);
            var state = (await fixture.Store.LoadAsync("benchmark", Session, Ct))!;
            state.Status = PlanningStatus.Stopped;
            state.Revision++;
            state.Diagnostics = storageError is null ? [new(ErrorCodes.LlmBudgetUnverifiable, "/", "Admission refused.")
            { BudgetFailure = new(null, null, null, null, null, null, dispatch) }] : [new("MODEL_DISPATCH_UNVERIFIABLE", "/", "No retained completion.")];
            Assert.True(await fixture.Store.TrySaveAsync(state, state.Revision - 1, Ct));
            var saved = (await campaign.LoadAsync("planning-evaluation-runs", InterruptedServerRun, Ct))!;
            saved["result"]!["status"] = PlanningStatus.Stopped;
            saved["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession);
            await campaign.SaveAsync("planning-evaluation-runs", InterruptedServerRun, saved, Ct);
            foreach (var collection in new[] { "planning-evaluation-requests", BenchmarkHttpJournal.Collection, "planning-evaluation-failures" })
                Assert.True(await fixture.Records.DeleteAsync(collection, "benchmark", "retention:" + RequestId, "test", Ct));
            var kind = storageError is null ? "admission-refused-before-campaign-envelope" : "storage-failure-before-campaign-envelope";
            if (storageError is not null)
            {
                var telemetry = Path.Combine(workspace, ".GnOuGo/data/live-server", InterruptedServerRun, "telemetry.db");
                await using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    { DataSource = telemetry, Pooling = false }.ToString());
                await db.OpenAsync(Ct);
                await using var sql = db.CreateCommand();
                sql.CommandText = "CREATE TABLE span_records (name TEXT, trace_id BLOB, span_id BLOB, start_unix_ns INTEGER, end_unix_ns INTEGER, status_code INTEGER, attributes_json TEXT)";
                await sql.ExecuteNonQueryAsync(Ct);
                sql.CommandText = "INSERT INTO span_records VALUES ('gen_ai.generate', randomblob(16), randomblob(8), 1, 2, 2, $attrs)";
                sql.Parameters.AddWithValue("$attrs", new JsonObject
                {
                    ["tenant.id"] = "benchmark", ["gnougo.planning.session_id"] = Session, ["gnougo.llm.status"] = "failed",
                    ["error.type"] = storageError == "IOException" ? storageError : "KeyVaultAccessException",
                    ["gnougo.llm.request_id"] = storageError == "different-request" ? "different" : RequestId
                }.ToJsonString());
                await sql.ExecuteNonQueryAsync(Ct);
                if (storageError == "duplicate") await sql.ExecuteNonQueryAsync(Ct);
            }
            Task<JsonObject> Inspect() => campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", InterruptedServerRun, RequestId, Ct, kind, workspace);
            if (!eligible)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(Inspect);
                Assert.Null(await campaign.LoadAsync("planning-evaluation-closures", RequestId, Ct));
                return;
            }
            var preview = await Inspect();
            Assert.True(preview["request_replay_forbidden"]!.GetValue<bool>());
            var original = await fixture.Records.GetAsync("agent-planning-budgets-v11", "benchmark", Session, "test", Ct);
            await campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", InterruptedServerRun, RequestId, preview["evidence_hash"]!.ToString(), Operator, Authorization, Ct, kind, workspace);
            var cold = new BenchmarkCampaign(new KeyVaultRecordStore(Path.Combine(fixture.Root, "vault.db")), "retention");
            Assert.Equal(.95m, await cold.RetainedApplicationReservationsAsync(Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => cold.RequireRunNotRetainedAsync(Session, Ct));
            Assert.Equal(original, await fixture.Records.GetAsync("agent-planning-budgets-v11", "benchmark", Session, "test", Ct));
        }
        finally { await fixture.DisposeAsync(); Directory.Delete(workspace, true); }
    }
}
