// Compiled only inside the pinned schema-4 audit checkout, never into production.
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;

internal static class ProgressiveEvidenceExport
{
    internal static async Task RunAsync(string root, IKeyVaultRecordStore records)
    {
        const string collection = "agent-planning-progressive-evidence-v5", author = "GnOuGo.Agent.Planning.Benchmark", tenant = "planner-progressive";
        var approvedStore = new EfPlanningSessionStore(new Contexts(GnOuGoWorkspace.ResolveDatabasePath(null, root, ".GnOuGo/data/gnougo-planning-v4.db"), true), records);
        var catalogStore = new EfPlanningSessionStore(new Contexts(GnOuGoWorkspace.ResolveDatabasePath(null, root, ".GnOuGo/data/planner-benchmark/gnougo-planning-v4.db"), true), records);
        var approved = await approvedStore.LoadAsync("default", "a653ccf16f9f47fb9d043a54c3918991", default) ?? throw new InvalidOperationException("Approved reference missing.");
        var catalog = await catalogStore.LoadAsync("planner-benchmark", "codereview-bc72dd6", default) ?? throw new InvalidOperationException("Frozen catalog missing.");
        string Hash(PlanningSnapshot value) => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(value, PlanningJsonContext.Default.PlanningSnapshot));
        var approvedHash = Hash(approved); var catalogHash = Hash(catalog);
        if (approved.Status != PlanningStatus.Approved || approved.ArtifactHash != approved.ApprovedHash || PlanningGraphCompiler.Fingerprint(approved.Yaml!) != approved.ApprovedHash)
            throw new InvalidOperationException("The historical reference lacks exact artifact approval.");
        var inputs = new JsonObject
        {
            ["sourceCommit"] = "2536c277232995d9d9fac06c9fae9cfe058817a0",
            ["approvedSourceHash"] = approvedHash, ["catalogSourceHash"] = catalogHash,
            ["approvedArtifactHash"] = approved.ApprovedHash, ["approvedRevision"] = approved.Revision,
            ["referencePrompt"] = approved.Request.Prompt, ["referenceYaml"] = approved.Yaml,
            ["referenceGraph"] = JsonSerializer.SerializeToNode(approved.Graph, PlanningJsonContext.Default.PlanningGraph),
            ["referenceBehavior"] = JsonSerializer.SerializeToNode(approved.BehaviorPlan, PlanningJsonContext.Default.PlanningBehaviorPlan),
            ["codeReviewPrompt"] = catalog.Request.Prompt,
            ["discovery"] = catalog.PreparationCheckpoint!.ValidatedResults["discovery"]!.DeepClone(),
            ["generator"] = catalog.Request.Options["generator"]!.DeepClone()
        };
        var fingerprint = PlanningGraphCompiler.Fingerprint(inputs.ToJsonString());
        var existing = await records.GetAsync(collection, tenant, "frozen", author, default);
        if (existing is not null && existing.Value != inputs.ToJsonString()) throw new InvalidOperationException("Frozen evidence changed.");
        if (existing is null) await records.UpsertAsync(collection, tenant, "frozen", inputs.ToJsonString(), author, default);
        if (Hash((await approvedStore.LoadAsync("default", approved.Request.SessionId, default))!) != approvedHash ||
            Hash((await catalogStore.LoadAsync("planner-benchmark", catalog.Request.SessionId, default))!) != catalogHash)
            throw new InvalidOperationException("Historical source changed during evidence export.");
        Console.WriteLine(new JsonObject { ["evidenceHash"] = fingerprint, ["approvedArtifactHash"] = approved.ApprovedHash,
            ["catalogHash"] = PlanningGraphCompiler.Fingerprint(inputs["discovery"]!.ToJsonString()), ["sourceUnchanged"] = true,
            ["providerDispatches"] = 0, ["sessionsMigrated"] = 0 }.ToJsonString());
    }
}
