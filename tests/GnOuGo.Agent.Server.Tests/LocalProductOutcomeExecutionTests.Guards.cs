using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("one")]
    [InlineData("empty")]
    [InlineData("empty_selection")]
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("null_href")]
    [InlineData("unknown")]
    [InlineData("unoffered")]
    [InlineData("incomplete")]
    [InlineData("denied")]
    public Task GuardCompositionPreservesOriginalDataAndWritesExactWorkbook(string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v15", approval: true, guards: true);

    [Fact]
    public async Task RetainedGuardsAndExplicitCompositionRevisionStaySeparate()
    {
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new(), AppContext.BaseDirectory)));
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken); catalog.Policy.RequireExternalConfirmation = false;
        var retained = GuardRetained(catalog); var original = JsonSerializer.Serialize(retained, PlanningJsonContext.Default.PlanningProposal);
        var compiler = new TaskPlanCompiler(); var findings = compiler.Compile(retained.Plan!, catalog).Diagnostics;
        Assert.Equal(new[] { "/tasks/extract_choices/requires", "/tasks/extract_record/requires" }, findings.Select(d => d.Location));
        Assert.All(findings, d => { Assert.Equal("TASK_FIELD_UNKNOWN", d.Code); Assert.Contains("available},{", d.Message); });
        var referenceOnly = GuardRetained(catalog);
        foreach (var t in RetainedTasks(referenceOnly.Plan!.Root).Where(t => t.Id is "extract_choices" or "extract_record"))
        {
            Assert.Equal("and", t.Requires!.Predicate); Assert.Equal(2, t.Requires.Items.Count);
            t.Requires.Items[1].Port = "available";
        }
        Assert.Empty(compiler.Compile(referenceOnly.Plan!, catalog).Diagnostics);
        var before = BusinessCompilation(referenceOnly.Plan!, catalog, "compact-bindings-v15");
        // Compiling the typo-only correction does not certify the business composition.
        Assert.Equal(2, RetainedTasks(referenceOnly.Plan!.Root).Count(t => t.Id.EndsWith("_flags", StringComparison.Ordinal) && t.Mode == "interpret"));
        var corrected = GuardCorrected(catalog);
        AssertGuardBusinessPreserved(retained, corrected);
        var session = await GuardRevision(catalog, runtime, corrected, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.FinalReview, session.Status); Assert.Null(session.ApprovedHash);
        Assert.Equal(0, factory.InvocationAttempts); Assert.Equal(original, JsonSerializer.Serialize(retained, PlanningJsonContext.Default.PlanningProposal));
        Assert.Equal(new[] { "prepare_url", "choose", "choose_details" }, RetainedTasks(corrected.Plan!.Root).Where(t => t.Mode == "interpret").Select(t => t.Id));
        var after = BusinessCompilation(corrected.Plan!, catalog, "compact-bindings-v15");
        output.WriteLine($"GUARDS typo-only tasks={RetainedTasks(referenceOnly.Plan.Root).Count()},steps={before.Steps},sets={before.Sets},yaml={System.Text.Encoding.UTF8.GetByteCount(before.Yaml)}; corrected tasks={RetainedTasks(corrected.Plan.Root).Count()},steps={after.Steps},sets={after.Sets},yaml={System.Text.Encoding.UTF8.GetByteCount(after.Yaml)}; cleanup only added requirement; repairs=0");
        if (Environment.GetEnvironmentVariable("GNOU_GO_GUARD_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "retained-proposal.json"), original);
            File.WriteAllText(Path.Combine(folder, "reference-only-taskplan.json"), JsonSerializer.Serialize(referenceOnly.Plan, PlanningJsonContext.Default.TaskPlan));
            File.WriteAllText(Path.Combine(folder, "reference-only.yaml"), before.Yaml);
            File.WriteAllText(Path.Combine(folder, "corrected-proposal.json"), JsonSerializer.Serialize(corrected, PlanningJsonContext.Default.PlanningProposal));
            File.WriteAllText(Path.Combine(folder, "corrected-taskplan.json"), JsonSerializer.Serialize(corrected.Plan, PlanningJsonContext.Default.TaskPlan));
            File.WriteAllText(Path.Combine(folder, "corrected.yaml"), session.Yaml);
        }
    }

    [Theory]
    [InlineData("requirement")]
    [InlineData("output")]
    [InlineData("input")]
    [InlineData("argument")]
    [InlineData("destination")]
    [InlineData("header")]
    public async Task GuardRevisionReviewRejectsUnrelatedBusinessChanges(string change)
    {
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new(), AppContext.BaseDirectory))) }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken);
        var retained = GuardRetained(catalog); var proposal = GuardCorrected(catalog);
        switch (change)
        {
            case "requirement": proposal.Requirements!.Outcomes.RemoveAt(0); break;
            case "output": proposal.Requirements!.Outputs!.Single().Type.Nullable = true; break;
            case "input": proposal.Plan!.Inputs.Single().Name = "invented"; break;
            case "argument": proposal.Plan!.Root.Tasks.Single(t => t.Id == "read_results").Inputs.Single(i => i.Name == "timeoutMs").Value.Number = 1234; break;
            case "destination": proposal.Plan!.Root.Tasks.Single(t => t.Id == "write").Inputs.Single(i => i.Name == "filePath").Value.Text = "other.xlsx"; break;
            case "header": proposal.Plan!.Root.Tasks.Single(t => t.Id == "write").Inputs.Single(i => i.Name == "rows").Value.Items[0].Items[0].Items[0].Items[0].Text = "Invented"; break;
        }
        Assert.ThrowsAny<Exception>(() => AssertGuardBusinessPreserved(retained, proposal));
    }

    private static PlanningProposal GuardRetained(PlanningCatalog catalog)
    {
        var proposal = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GuardComposition", "retained.json")), PlanningJsonContext.Default.PlanningProposal)!;
        foreach (var task in RetainedTasks(proposal.Plan!.Root).Where(t => t.Kind == "operation"))
            task.Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == task.Operation)).Id;
        return proposal;
    }

    private static PlanningProposal GuardCorrected(PlanningCatalog catalog, PlanningProposal? retained = null)
    {
        retained = JsonSerializer.Deserialize(JsonSerializer.Serialize(retained ?? GuardRetained(catalog), PlanningJsonContext.Default.PlanningProposal), PlanningJsonContext.Default.PlanningProposal)!;
        var proposal = ApprovalCorrected(catalog);
        var cleanup = proposal.Requirements!.Outcomes.Single(o => o.Id == "cleanup_browser");
        proposal.Requirements = JsonSerializer.Deserialize(JsonSerializer.Serialize(retained.Requirements, PlanningJsonContext.Default.PlanningRequirements), PlanningJsonContext.Default.PlanningRequirements)!;
        proposal.Requirements.Outcomes.Add(cleanup);
        proposal.Plan!.Inputs = retained.Plan!.Inputs;
        var tasks = proposal.Plan.Root.Tasks; var originals = RetainedTasks(retained.Plan.Root).ToDictionary(t => t.Id);
        // Preserve every literal external argument; only replace adaptation bindings.
        var read = originals["read_results"]; read.DependsOn.Clear(); read.Requires = V10PresentValue(V10Output("prepare_url", "url"));
        read.Inputs[read.Inputs.FindIndex(i => i.Name == "url")] = new("url", V10Output("prepare_url", "url"));
        tasks[tasks.FindIndex(t => t.Id == read.Id)] = read;
        var visit = tasks.Single(t => t.Id == "visit");
        // No collection-length binding exists. The existing json conversion has
        // an exact empty-array representation; only bounded compact values enter
        // this assertion, never raw observations. Empty selection cannot conceal
        // an unresolved decision over offered candidates.
        TaskValue Json(TaskValue value) => new() { Kind = "json", Items = [value] };
        TaskValue Empty(TaskValue value) => new() { Kind = "predicate", Predicate = "equal", Items = [Json(value), new() { Kind = "string", Text = "[]" }] };
        var choose = tasks.Single(t => t.Id == "choose");
        visit.Requires = new() { Kind = "predicate", Predicate = "and", Items = [visit.Requires!,
            new() { Kind = "predicate", Predicate = "or", Items = [
                new() { Kind = "predicate", Predicate = "not", Items = [Empty(V10Output(choose.Id, "selectedProductReferenceIds"))] },
                Empty(choose.Inputs.Single().Value)] }] };
        choose.Objective += " An empty selection is allowed only when no candidates were offered after complete acquisition; uncertainty is not an empty business result.";
        var branch = visit.Body!.Tasks.Single();
        visit.Body = branch.Body!;
        var product = originals["read_product"]; product.DependsOn.Clear();
        product.Requires = V10PresentValue(V10Field(new() { Kind = "item" }, "href"));
        product.Inputs[product.Inputs.FindIndex(i => i.Name == "url")] = new("url", V10Field(new() { Kind = "item" }, "href"));
        visit.Body.Tasks[0] = product;
        // This retained interface exports only the writer path, not the other
        // fixture's independently bounded rows. Three typed literal cells already
        // establish the column contract; keep the ten-record projection boundary.
        var rows = tasks.Single(t => t.Id == "rows");
        rows.Body = new() { Outputs = [new("cells", V10Array(new[] { "name", "description", "price" }.Select(f => V10Field(new() { Kind = "item" }, f)).ToArray()))] };
        var header = tasks.Single(t => t.Id == "checked_header").Inputs.Single().Value;
        tasks.RemoveAll(t => t.Id is "checked_header" or "checked_rows");
        proposal.Plan.Groups.RemoveAll(g => g.Id is "cell_contract" or "workbook_contract");
        var writer = originals["write"]; writer.DependsOn = [rows.Id]; writer.Requires = new() { Kind = "present", Source = rows.Id };
        writer.Inputs[writer.Inputs.FindIndex(i => i.Name == "rows")] = new("rows", V10Flatten(V10Array(V10Array(header), V10Output(rows.Id, "cells"))));
        tasks[tasks.FindIndex(t => t.Id == "write")] = writer;
        var checkedWriter = tasks.Single(t => t.Id == "checked_writer");
        checkedWriter.Outputs = [new("filePath", V10Output("write", "filePath"))];
        proposal.Plan.Root.Outputs = [new("filePath", V10Output(checkedWriter.Id, "filePath"))];
        return proposal;
    }

    private static void AssertGuardBusinessPreserved(PlanningProposal retained, PlanningProposal corrected)
    {
        var expected = JsonSerializer.SerializeToNode(retained.Requirements, PlanningJsonContext.Default.PlanningRequirements)!;
        var actual = JsonSerializer.SerializeToNode(corrected.Requirements, PlanningJsonContext.Default.PlanningRequirements)!;
        var added = Assert.Single(actual["outcomes"]!.AsArray(), o => o!["id"]!.ToString() == "cleanup_browser"); actual["outcomes"]!.AsArray().Remove(added);
        Assert.True(JsonNode.DeepEquals(expected, actual), "Cleanup is the sole added requirement; all accepted interfaces remain exact.");
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(retained.Plan!.Inputs), JsonSerializer.SerializeToNode(corrected.Plan!.Inputs)));
        var previous = RetainedTasks(retained.Plan.Root).Where(t => t.Kind == "operation").ToArray();
        var current = RetainedTasks(corrected.Plan.Root).Where(t => t.Kind == "operation").ToArray();
        // The two original branches write through the same capability with the
        // same literals. Only their row binding differs; one writer can serve both.
        var oldWriter = previous.Single(t => t.Id == "write");
        var emptyWriter = previous.Single(t => t.Id == "write_empty");
        Assert.Equal(oldWriter.Operation, emptyWriter.Operation);
        foreach (var input in oldWriter.Inputs.Where(i => i.Name != "rows"))
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(input, PlanningJsonContext.Default.TaskOutput),
                JsonSerializer.SerializeToNode(emptyWriter.Inputs.Single(i => i.Name == input.Name), PlanningJsonContext.Default.TaskOutput)));
        Assert.Equal(previous.Where(t => t.Id != "write_empty").Select(t => (t.Id, t.Operation)), current.Where(t => t.Id != "release").Select(t => (t.Id, t.Operation)));
        foreach (var task in previous.Where(t => t.Id != "write_empty"))
            foreach (var input in task.Inputs.Where(i => !(task.Id is "read_results" or "read_product" && i.Name == "url") && !(task.Id == "write" && i.Name == "rows")))
                Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(input.Value, PlanningJsonContext.Default.TaskValue),
                    JsonSerializer.SerializeToNode(current.Single(t => t.Id == task.Id).Inputs.Single(i => i.Name == input.Name).Value, PlanningJsonContext.Default.TaskValue)));
        Assert.Equal(new[] { "Nom", "Description", "Prix" }, corrected.Plan.Root.Tasks.Single(t => t.Id == "write").Inputs.Single(i => i.Name == "rows").Value.Items[0].Items[0].Items[0].Items.Select(i => i.Text));
        Assert.Equal(new[] { "filePath" }, corrected.Plan.Root.Outputs.Select(o => o.Name));
        Assert.Equal(10, corrected.Plan.Root.Tasks.Single(t => t.Id == "visit").MaxItems);
    }

    private static async Task<PlanningSession> GuardRevision(PlanningCatalog catalog, WorkflowPlanningRuntime runtime, PlanningProposal corrected, CancellationToken ct)
    {
        var retained = GuardRetained(catalog); AssertGuardBusinessPreserved(retained, corrected);
        var planner = new HybridWorkflowPlanner(); var initial = new ProposalRuntime(runtime, retained);
        var seed = GnOuGo.Planning.Examples.PlanningCorpus.RetainedSession(new PlanningSession { Catalog = catalog, Request = new() { TenantId = "local", Name = "guard-offline",
            Prompt = retained.Requirements!.Summary, Policy = new() { RequireExternalConfirmation = false }, Generation = new() { MaxInputTokensPerRequest = 96000, MaxOutputTokens = 32768 } } });
        seed.Discovery.Sources = (await runtime.Capabilities.ListSourcesAsync(ct)).ToList();
        foreach (var source in seed.Discovery.Sources) seed.Discovery.Pages.Add(await runtime.Capabilities.ListAsync(source.Id, null, ct));
        var state = await planner.AdvanceAsync(seed, new(), initial, ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(initial.Requests); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(2, state.Diagnostics.Count(d => d.Code == "REVISION_REQUIRED"));
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var copy = JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSession)!;
        var adapter = new ProposalRuntime(runtime, corrected);
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(copy, new() { Kind = "revise", ExpectedRevision = copy.Revision - 1, Text = "Stale" }, adapter, ct));
        Assert.Empty(adapter.Requests);
        copy = await planner.AdvanceAsync(copy, new() { Kind = "revise", ExpectedRevision = copy.Revision, ArtifactHash = copy.ComputeArtifactHash(), PreserveRequirements = false,
            Text = "Correct adaptation and producer assertions. Preserve all four requirements, article input, filePath output, ordered first ten visits, three exact columns, destination and external literals. Add only Browser cleanup. Use complete pages, compact ID decisions, offered/original lookup and deterministic rows. A missing required URL fails before dispatch; incomplete acquisition cannot produce an empty successful workbook." }, adapter, ct);
        Assert.Null(copy.EditablePaths); Assert.Null(copy.ApprovedHash); Assert.Equal(state.ModelCalls, copy.ModelCalls);
        var close = catalog.Capabilities.Single(c => c.Method == "browser_close");
        var sourceId = copy.Discovery.Pages.First(p => p.Capabilities.Any(c => c.Name == "browser_get_content")).SourceId;
        adapter.Patch = _ => JsonSerializer.SerializeToNode(new PlanningProposal { Requirements = corrected.Requirements, DiscoveryRequests = [new(sourceId, Query: "browser_close")] }, PlanningJsonContext.Default.PlanningProposal)!.AsObject();
        copy = await planner.AdvanceAsync(copy, new() { ExpectedRevision = copy.Revision }, adapter, ct);
        var summary = copy.Discovery.Pages.SelectMany(p => p.Capabilities).First(c => c.Id == close.Id);
        adapter.Patch = _ => JsonSerializer.SerializeToNode(new PlanningProposal { Requirements = corrected.Requirements, DiscoveryRequests = [new(summary.SourceId, OperationIds: [summary.Operation!.Id])] }, PlanningJsonContext.Default.PlanningProposal)!.AsObject();
        copy = await planner.AdvanceAsync(copy, new() { ExpectedRevision = copy.Revision }, adapter, ct);
        adapter.Patch = null;
        copy = await planner.AdvanceAsync(copy, new() { ExpectedRevision = copy.Revision }, adapter, ct);
        Assert.True(copy.Status == PlanningStatus.FinalReview, string.Join(';', copy.Diagnostics));
        Assert.Equal(state.ModelCalls + 3, copy.ModelCalls); Assert.Equal(3, adapter.Requests.Count); Assert.Equal(0, copy.ReplanAttempts);
        Assert.True(copy.ActiveMilliseconds >= state.ActiveMilliseconds); Assert.Null(copy.ApprovedHash);
        AssertGuardBusinessPreserved(retained, new() { Requirements = copy.Requirements, Plan = copy.Plan });
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(copy, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        restored = await planner.AdvanceAsync(restored, new() { ExpectedRevision = restored.Revision }, adapter, ct);
        Assert.Equal(3, adapter.Requests.Count); Assert.Equal(copy.Yaml, restored.Yaml);
        Assert.Equal(saved, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        return copy;
    }
}
