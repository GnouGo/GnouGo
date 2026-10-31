using System.IO.Compression;
using System.Text;
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
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("unoffered")]
    [InlineData("incomplete")]
    [InlineData("null_href")]
    [InlineData("denied")]
    public Task CorrectedNullableGuardCompositionWritesOriginalCells(string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v15", approval: true, guards: true, nullableGuards: true);

    [Fact]
    public async Task RetainedNullableGuardsAndDisposableCompositionHaveSeparateProofs()
    {
        var factory = new RealProductContracts.Factory(CaptureIsolatedProductContracts());
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken); catalog.Policy.RequireExternalConfirmation = false;
        var retained = NullableGuardRetained(catalog); var corrected = NullableGuardComposition(catalog);
        var diagnostics = new TaskPlanCompiler().Compile(retained.Plan!, catalog).Diagnostics;
        Assert.Equal(2, diagnostics.Count(d => d.Code == "TASK_CONDITION_TYPE"));
        var minimal = NullableGuardRetained(catalog);
        foreach (var task in RetainedTasks(minimal.Plan!.Root).Where(t => t.Id is "accept_cookies" or "extract_candidates_by_page"))
            task.Requires = new() { Kind = "predicate", Predicate = "and", Items = [task.Id == "accept_cookies"
                ? V10Field(new() { Kind = "output", Source = "cookie_decision" }, "hasConsentButton")
                : V10Output("accept_cookies_if_present", "search_read_success"), V10PresentValue(task.Requires!)] };
        Assert.Empty(new TaskPlanCompiler().Compile(minimal.Plan!, catalog).Diagnostics);
        var before = BusinessCompilation(minimal.Plan!, catalog, "compact-bindings-v15");
        var after = BusinessCompilation(corrected.Plan!, catalog, "compact-bindings-v15");
        var state = await NullableGuardRevision(catalog, runtime, corrected, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Null(state.ApprovedHash); Assert.Equal(0, factory.InvocationAttempts);
        Assert.Equal(new[] { "prepare_url", "choose", "choose_details" }, RetainedTasks(corrected.Plan!.Root).Where(t => t.Mode == "interpret").Select(t => t.Id));
        Assert.Equal(2, RetainedTasks(corrected.Plan.Root).Count(t => t.Mode == "extract"));
        output.WriteLine($"NULLABLE GUARDS minimal tasks={RetainedTasks(minimal.Plan.Root).Count()},steps={before.Steps},sets={before.Sets},yaml={Encoding.UTF8.GetByteCount(before.Yaml)}; " +
            $"corrected tasks={RetainedTasks(corrected.Plan.Root).Count()},steps={after.Steps},sets={after.Sets},yaml={Encoding.UTF8.GetByteCount(after.Yaml)}; requirements=6 unchanged; explicit revision=1; repairs=0");
        if (Environment.GetEnvironmentVariable("GNOU_GO_NULLABLE_GUARD_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "corrected-taskplan.json"), JsonSerializer.Serialize(corrected.Plan, PlanningJsonContext.Default.TaskPlan));
            File.WriteAllText(Path.Combine(folder, "corrected-proposal.json"), JsonSerializer.Serialize(corrected, PlanningFixtureJsonContext.Default.TaskPlanFixture));
            File.WriteAllText(Path.Combine(folder, "corrected.yaml"), state.Yaml);
            File.WriteAllText(Path.Combine(folder, "guard-only.yaml"), before.Yaml);
        }
    }

    private static TaskPlanFixture NullableGuardRetained(PlanningCatalog catalog)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NullableGuards", "initial-proposal.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var proposal = JsonSerializer.Deserialize(gzip, PlanningFixtureJsonContext.Default.TaskPlanFixture)!;
        foreach (var task in RetainedTasks(proposal.Plan!.Root).Where(t => t.Kind == "operation"))
        {
            var method = task.Id switch { "close_browser" => "browser_close", "write_products_xlsx" => "document_write", "accept_cookies" => "browser_click", _ => "browser_get_content" };
            task.Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == method)).Id;
        }
        return proposal;
    }

    private static TaskPlanFixture NullableGuardComposition(PlanningCatalog catalog)
    {
        var retained = NullableGuardRetained(catalog);
        var corrected = GuardCorrected(catalog);
        corrected.Requirements = retained.Requirements;
        corrected.Plan!.Inputs = retained.Plan!.Inputs;
        var tasks = corrected.Plan.Root.Tasks;
        var originals = RetainedTasks(retained.Plan.Root).ToDictionary(t => t.Id);
        tasks.Single(t => t.Id == "prepare_url").Inputs = [new("query", new() { Kind = "input", Source = "query" })];
        tasks.Single(t => t.Id == "prepare_url").Objective = originals["build_search_url"].Objective;
        var read = originals["open_search"]; read.Id = "read_results"; read.DependsOn.Clear(); read.Requires = V10PresentValue(V10Output("prepare_url", "url"));
        read.Inputs[read.Inputs.FindIndex(i => i.Name == "url")] = new("url", V10Output("prepare_url", "url"));
        tasks[tasks.FindIndex(t => t.Id == read.Id)] = read;
        var product = originals["open_product"]; product.Id = "read_product"; product.DependsOn.Clear(); product.Requires = V10PresentValue(V10Field(new() { Kind = "item" }, "href"));
        product.Inputs[product.Inputs.FindIndex(i => i.Name == "url")] = new("url", V10Field(new() { Kind = "item" }, "href"));
        tasks.Single(t => t.Id == "visit").Body!.Tasks[0] = product;
        var write = tasks.Single(t => t.Id == "write");
        var rows = write.Inputs.Single(i => i.Name == "rows").Value;
        write.Inputs = originals["write_products_xlsx"].Inputs;
        write.Inputs[write.Inputs.FindIndex(i => i.Name == "rows")] = new("rows", rows);
        var check = tasks.Single(t => t.Id == "checked_writer");
        check.Outputs = [new("success", V10Output("write", "success")), new("filePath", V10Output("write", "filePath")),
            new("status", new() { Kind = "string", Text = "completed" }), new("errors", V10Array())];
        corrected.Plan.Root.Outputs = check.Outputs.Select(o => new TaskOutput(o.Name, V10Output(check.Id, o.Name))).ToList();
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(retained.Requirements, PlanningJsonContext.Default.PlanningRequirements),
            JsonSerializer.SerializeToNode(corrected.Requirements, PlanningJsonContext.Default.PlanningRequirements)));
        Assert.Equal(6, corrected.Requirements!.Outcomes.Count);
        Assert.Equal(new[] { "browser_get_content", "browser_get_content", "document_write", "browser_close" },
            RetainedTasks(corrected.Plan.Root).Where(t => t.Kind == "operation").Select(t => catalog.Capabilities.Single(c => TaskOperations.Describe(c).Id == t.Operation).Method));
        return corrected;
    }

    private static async Task<PlanningSession> NullableGuardRevision(PlanningCatalog catalog, WorkflowPlanningRuntime runtime, TaskPlanFixture corrected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var result = RecordedPlanCompilation.CompileStatic(corrected, catalog, new()
        { TenantId = "local", Prompt = corrected.Requirements!.Summary, Policy = new() { RequireExternalConfirmation = false },
            Options = new() { ["compilation_profile"] = "compact-bindings-v15", ["mapping_profile"] = "adaptive-each-v1" } });
        Assert.True(result.Status == PlanningStatus.FinalReview, string.Join(';', result.Diagnostics));
        Assert.Null(result.ApprovedHash); Assert.Equal(0, result.ModelCalls);
        return result;
    }

}
