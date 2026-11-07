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
    [InlineData("null_href")]
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("unknown")]
    [InlineData("unoffered")]
    [InlineData("incomplete")]
    [InlineData("denied")]
    public Task DesignerApprovalCompositionWritesExactThreeColumnWorkbook(string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v15", approval: true);

    [Fact]
    public async Task DesignerApprovalRevisionCorrectsBusinessCompositionWithoutChangingHistoricalArtifact()
    {
        var factory = new RealProductContracts.Factory(CaptureIsolatedProductContracts());
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken); catalog.Policy.RequireExternalConfirmation = false;
        var retained = ApprovalRetained(catalog); var corrected = ApprovalCorrected(catalog);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ApprovalRetained(catalog, "corrected"), PlanningFixtureJsonContext.Default.TaskPlanFixture),
            JsonSerializer.SerializeToNode(corrected, PlanningFixtureJsonContext.Default.TaskPlanFixture)));
        var before = BusinessCompilation(retained.Plan!, catalog, "compact-bindings-v15");
        // The original was structurally valid: static validation is not a business oracle.
        Assert.Empty(new TaskPlanCompiler().Compile(retained.Plan!, catalog).Diagnostics);
        Assert.Equal("object", retained.Plan!.Root.Tasks.Single(t => t.Id == "extract_choices").ResultType!.Fields.Single().Type.Items!.Kind);
        Assert.DoesNotContain(RetainedTasks(retained.Plan.Root), t => t.Kind == "operation" && t.Id == "release");
        AssertApprovalBusinessPreserved(retained, corrected);
        var after = BusinessCompilation(corrected.Plan!, catalog, "compact-bindings-v15");
        var session = await DesignerRevision(catalog, runtime, corrected, TestContext.Current.CancellationToken, approval: true);
        Assert.Equal(PlanningStatus.FinalReview, session.Status); Assert.Null(session.ApprovedHash);
        Assert.Equal(0, factory.InvocationAttempts);
        Assert.Equal(new[] { "prepare_url", "choose", "choose_details" }, RetainedTasks(corrected.Plan!.Root).Where(t => t.Mode == "interpret").Select(t => t.Id));
        var outputFields = corrected.Requirements!.Outputs!.Single(o => o.Name == "status").Type.Fields;
        Assert.Equal(new[] { "saved", "success" }, outputFields.Select(f => f.Name));
        output.WriteLine($"APPROVAL before tasks={RetainedTasks(retained.Plan.Root).Count()}; steps={before.Steps}; sets={before.Sets}; yamlBytes={System.Text.Encoding.UTF8.GetByteCount(before.Yaml)}; after tasks={RetainedTasks(corrected.Plan.Root).Count()}; steps={after.Steps}; sets={after.Sets}; yamlBytes={System.Text.Encoding.UTF8.GetByteCount(after.Yaml)}; revisionCalls=3 (2 cleanup discovery,1 proposal); repairs=0; effects=0");
        if (Environment.GetEnvironmentVariable("GNOU_GO_APPROVAL_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "corrected-proposal.json"), JsonSerializer.Serialize(corrected, PlanningFixtureJsonContext.Default.TaskPlanFixture));
            File.WriteAllText(Path.Combine(folder, "corrected-taskplan.json"), JsonSerializer.Serialize(corrected.Plan, PlanningJsonContext.Default.TaskPlan));
            File.WriteAllText(Path.Combine(folder, "corrected.yaml"), session.Yaml);
            File.WriteAllText(Path.Combine(folder, "retained.yaml"), before.Yaml);
        }
    }

    [Theory]
    [InlineData("requirement")]
    [InlineData("output")]
    [InlineData("column")]
    [InlineData("operation")]
    public async Task DesignerApprovalReviewRejectsChangesBeyondTheAuthorizedDelta(string change)
    {
        var factory = CaptureIsolatedProductContracts();
        // Use the actual captured contracts through the same existing discovery helper.
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = new RealProductContracts.Factory(factory) }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken);
        var retained = ApprovalRetained(catalog); var corrected = ApprovalCorrected(catalog);
        switch (change)
        {
            case "requirement": corrected.Requirements!.Outcomes.RemoveAt(0); break;
            case "output": corrected.Requirements!.Outputs!.Single(o => o.Name == "filePath").Type.Nullable = true; break;
            case "column": corrected.Plan!.Root.Tasks.Single(t => t.Id == "checked_header").Inputs[0].Value.Items[0].Text = "Invented header"; break;
            case "operation": corrected.Plan!.Root.Tasks.Single(t => t.Id == "read_results").Inputs.Single(i => i.Name == "timeoutMs").Value.Number = 46000; break;
        }
        Assert.ThrowsAny<Exception>(() => AssertApprovalBusinessPreserved(retained, corrected));
    }

    private static TaskPlanFixture ApprovalRetained(PlanningCatalog catalog, string name = "retained")
    {
        var proposal = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "DesignerApproval", name + ".json")), PlanningFixtureJsonContext.Default.TaskPlanFixture)!;
        foreach (var task in RetainedTasks(proposal.Plan!.Root).Where(t => t.Kind == "operation"))
            task.Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == task.Operation)).Id;
        return proposal;
    }

    private static TaskPlanFixture ApprovalCorrected(PlanningCatalog catalog, string? origin = null)
    {
        var retained = ApprovalRetained(catalog);
        // Reuse the independently executed exact-ID/null-branch composition, while
        // preserving this session's direct search and three-column business interface.
        var proposal = DesignerCorrected(catalog, origin);
        var cleanup = proposal.Requirements!.Outcomes.Single(o => o.Id == "cleanup_browser");
        proposal.Requirements = retained.Requirements;
        proposal.Requirements!.Outcomes.Add(cleanup);
        proposal.Requirements.Outputs!.Single(o => o.Name == "status").Type.Fields.RemoveAll(f => f.Name is "rowCountIncludingHeader" or "dataRowCount");
        proposal.Plan!.Inputs = retained.Plan!.Inputs;
        var originals = RetainedTasks(retained.Plan.Root).ToDictionary(t => t.Id);
        var prepare = new PlanTask { Id = "prepare_url", Kind = "transform", Mode = "interpret",
            Objective = "Convert the exact caller query into the requested catalogue search URL. This bounded conversion neither chooses products nor supplies paths, headers, copies or status flags.",
            Inputs = [new("query", new() { Kind = "input", Source = "article" })],
            ResultType = new() { Kind = "object", Fields = [new() { Name = "url", Type = new() }] } };
        var search = originals["read_results"]; search.DependsOn.Clear(); search.Requires = V10PresentValue(V10Output(prepare.Id, "url"));
        search.Inputs[search.Inputs.FindIndex(i => i.Name == "url")] = new("url", V10Output(prepare.Id, "url"));
        var tasks = proposal.Plan.Root.Tasks;
        tasks.RemoveAll(t => t.Id is "open_catalogue" or "home_records" or "search_control" or "choose_control" or "fill_control" or "read_results");
        tasks.InsertRange(0, [prepare, search]);
        var visit = tasks.Single(t => t.Id == "visit");
        var branch = visit.Body!.Tasks.Single();
        branch.Body!.Tasks[0] = originals["read_product"]; branch.Body.Tasks[0].DependsOn.Clear();
        branch.Otherwise!.Outputs.Single().Value.Items.Single().Members.RemoveAll(m => m.Name == "error");
        var productAssembly = RetainedTasks(branch.Body).Single(t => t.Id == "detail_price_row").Body!;
        productAssembly.Outputs.Single(o => o.Name == "product").Value.Members.RemoveAll(m => m.Name == "error");
        productAssembly.Outputs.Single(o => o.Name == "row").Value.Items.RemoveAt(3);
        var row = tasks.Single(t => t.Id == "rows");
        row.Body!.Tasks.Single(t => t.Id == "assembled_cells").Outputs.Single().Value.Items.RemoveAt(3);
        var cells = proposal.Plan.Groups.Single(g => g.Id == "cell_contract").Inputs.Single().Type;
        cells.MinItems = cells.MaxItems = 3;
        var workbook = proposal.Plan.Groups.Single(g => g.Id == "workbook_contract").Inputs.Single().Type;
        workbook.MinItems = 2; workbook.MaxItems = 11; workbook.Items = cells;
        tasks.Single(t => t.Id == "checked_header").Inputs.Single().Value.Items.RemoveAt(3);
        tasks.Single(t => t.Id == "checked_header").Objective = "Validate the fixed three-cell header without inference.";
        row.Body.Tasks.Single(t => t.Id == "check_cells").Objective = "Validate exactly three nullable string cells without conversion.";
        tasks.Single(t => t.Id == "checked_rows").Objective = "Validate the header and one to ten exact three-cell product rows before writing.";
        var write = originals["write"];
        write.DependsOn = ["checked_rows"]; write.Requires = new() { Kind = "present", Source = "checked_rows" };
        write.Inputs[write.Inputs.FindIndex(i => i.Name == "rows")] = new("rows", V10Output("checked_rows", "rows"));
        write.Inputs[write.Inputs.FindIndex(i => i.Name == "filePath")] = new("filePath", new() { Kind = "string", Text = "workflows/observed-product-extraction/products.xlsx" });
        tasks[tasks.FindIndex(t => t.Id == "write")] = write;
        tasks.Single(t => t.Id == "checked_writer").Outputs = [new("status", new() { Kind = "object", Members = [
            new("saved", V10Output(write.Id, "success")), new("success", V10Output(write.Id, "success"))] })];
        proposal.Plan.Root.Outputs = [new("filePath", V10Output(write.Id, "filePath")), new("status", V10Output("checked_writer", "status"))];
        return proposal;
    }

    private static void AssertApprovalBusinessPreserved(TaskPlanFixture retained, TaskPlanFixture corrected)
    {
        var expected = JsonSerializer.SerializeToNode(retained.Requirements, PlanningJsonContext.Default.PlanningRequirements)!.AsObject();
        var actual = JsonSerializer.SerializeToNode(corrected.Requirements, PlanningJsonContext.Default.PlanningRequirements)!.AsObject();
        var added = actual["outcomes"]!.AsArray().Single(r => r!["id"]!.ToString() == "cleanup_browser"); actual["outcomes"]!.AsArray().Remove(added);
        var status = expected["outputs"]!.AsArray().Single(o => o!["name"]!.ToString() == "status")!["type"]!["fields"]!.AsArray();
        foreach (var removed in status.Where(f => f!["name"]!.ToString() is "rowCountIncludingHeader" or "dataRowCount").ToArray()) status.Remove(removed);
        Assert.True(JsonNode.DeepEquals(expected, actual), "Only cleanup and the two explicitly authorized status-field removals may change requirements.");
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(retained.Plan!.Inputs), JsonSerializer.SerializeToNode(corrected.Plan!.Inputs)));
        var operations = RetainedTasks(corrected.Plan.Root).Where(t => t.Kind == "operation").ToArray();
        var old = RetainedTasks(retained.Plan.Root).Where(t => t.Kind == "operation").ToArray();
        Assert.Equal(old.Select(t => (t.Id, t.Operation)), operations.Where(t => t.Id != "release").Select(t => (t.Id, t.Operation)));
        foreach (var producer in old)
            foreach (var input in producer.Inputs.Where(i => !(producer.Id == "read_results" && i.Name == "url") && !(producer.Id == "write" && i.Name is "rows" or "filePath")))
                Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(input.Value, PlanningJsonContext.Default.TaskValue),
                    JsonSerializer.SerializeToNode(operations.Single(t => t.Id == producer.Id).Inputs.Single(i => i.Name == input.Name).Value, PlanningJsonContext.Default.TaskValue)));
        Assert.Equal("workflows/observed-product-extraction/products.xlsx", operations.Single(t => t.Id == "write").Inputs.Single(i => i.Name == "filePath").Value.Text);
        Assert.Equal(new[] { "Nom", "Description", "Prix" }, corrected.Plan.Root.Tasks.Single(t => t.Id == "checked_header").Inputs.Single().Value.Items.Select(i => i.Text));
    }
}
