using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Files.Core;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Persistence;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Planning;

internal static class RetentionInspection
{
    internal static async Task RunAsync(string[] arguments)
    {
        var asOf = DateTimeOffset.UtcNow;
        if (arguments.Length > 1 || arguments.Length == 1 &&
            (!DateTimeOffset.TryParse(arguments[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out asOf) ||
             asOf.Offset != TimeSpan.Zero))
            throw new ArgumentException("Usage: --inspect-retention [UTC timestamp]");
        await using var app = GnOuGoAgentWebHost.BuildForRetentionInspection();
        var services = app.Services;
        var tenant = WorkflowExecutionTenant.Resolve(services.GetService<IOptions<OpenTelemetrySettings>>());
        var runs = (EncryptedWorkflowRunStore)services.GetRequiredService<IWorkflowRunStore>();
        var designer = (EfPlanningSessionStore)services.GetRequiredService<IPlanningSessionStore>();
        var nested = services.GetRequiredService<WorkflowPlanningRuntimeFactory>();
        var files = services.GetRequiredService<DurableFileStore>();
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, args) => { args.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            await ReportAsync(FileOwnerKind.Execution, runs.EnumerateOwnerIdsAsync(tenant, cancel.Token), runs.InspectRetentionAsync);
            await ReportAsync(FileOwnerKind.Planning, designer.EnumerateOwnerIdsAsync(tenant, cancel.Token), designer.InspectRetentionAsync);
            await ReportAsync(FileOwnerKind.WorkflowPlanning, nested.EnumerateOwnerIdsAsync(tenant, cancel.Token), nested.InspectRetentionAsync);
        }
        finally { Console.CancelKeyPress -= onCancel; }

        async Task ReportAsync(FileOwnerKind kind, IAsyncEnumerable<string> ids,
            Func<string, string, DateTimeOffset, CancellationToken, Task<OwnerRetentionInspection>> inspect)
        {
            await foreach (var id in ids)
            {
                var row = new JsonObject { ["tenant"] = tenant, ["kind"] = kind.ToString(), ["id"] = id, ["asOfUtc"] = asOf };
                try
                {
                    var entry = await inspect(tenant, id, asOf, cancel.Token);
                    row["revision"] = entry.Revision;
                    row["deadlineUtc"] = entry.DeadlineUtc;
                    row["reason"] = entry.Reason;
                    var usage = await files.InspectRetentionAsync(tenant, new(kind, id), asOf, cancel.Token);
                    row["fileCount"] = usage.Objects;
                    row["sharedFileBytes"] = usage.SharedBytes;
                    row["temporaryGrantBytes"] = usage.TemporaryGrantBytes;
                    row["exclusiveFileBytes"] = usage.ExclusiveBytes;
                    row["reclaimableFileBytes"] = entry.Reason == "eligible" ? usage.ExclusiveBytes : 0L;
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or
                    PlanningConflictException or WorkflowRunConflictException)
                {
                    row["reason"] = "unreadable_root";
                    row["reclaimableFileBytes"] = null;
                }
                Console.WriteLine(row.ToJsonString());
            }
        }
    }
}
