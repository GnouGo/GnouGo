using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("null_href")]
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("empty")]
    [InlineData("one")]
    [InlineData("incomplete")]
    [InlineData("unknown")]
    public Task DesignerCompositionExecutesExactWorkbookAndNullableVisits(string variant)
        => ExecuteTypedComposition(variant, "compact-bindings-v15", designer: true);

    [Fact]
    public async Task DesignerCompositionRevisionPreservesBusinessWorkAndReachesReview()
    {
        var factory = new RealProductContracts.Factory(CaptureIsolatedProductContracts());
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken); catalog.Policy.RequireExternalConfirmation = false;
        var retained = DesignerRetained(catalog); var corrected = DesignerCorrected(catalog);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(DesignerFixture(catalog, "corrected"), PlanningFixtureJsonContext.Default.TaskPlanFixture),
            JsonSerializer.SerializeToNode(corrected, PlanningFixtureJsonContext.Default.TaskPlanFixture)));
        var diagnostics = new TaskPlanCompiler().Compile(retained.Plan!, catalog).Diagnostics;
        Assert.Contains(diagnostics, d => d.Code == "TASK_TRANSFORM_EACH" && d.Location == "/tasks/extract_choices/each");
        Assert.Contains(diagnostics, d => d.Code == "TASK_LOOKUP_INVALID" && d.Location == "/tasks/visit/body/outputs/record");
        var compiled = BusinessCompilation(corrected.Plan!, catalog, "compact-bindings-v15");
        var session = await DesignerRevision(catalog, runtime, corrected, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.FinalReview, session.Status); Assert.Null(session.ApprovedHash);
        Assert.Equal(0, factory.InvocationAttempts);
        Assert.DoesNotContain(RetainedTasks(corrected.Plan!.Root), t => t.Mode == "interpret" && t.Id is "check_home" or "check_results" or "check_product" or "resolve_choices" or "rows");
        Assert.Equal("candidatesPerPage", RetainedTasks(corrected.Plan.Root).Single(t => t.Id == "extract_choices").Each!.Output);
        output.WriteLine($"DESIGNER corrected tasks={RetainedTasks(corrected.Plan.Root).Count()}; steps={compiled.Steps}; sets={compiled.Sets}; yamlBytes={System.Text.Encoding.UTF8.GetByteCount(session.Yaml!)}; revisionProposalCalls=1; cleanupDiscoveryCalls=2; automaticRepairs=0; externalCalls=0");
        if (Environment.GetEnvironmentVariable("GNOU_GO_DESIGNER_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "corrected-proposal.json"), JsonSerializer.Serialize(corrected, PlanningFixtureJsonContext.Default.TaskPlanFixture));
            File.WriteAllText(Path.Combine(folder, "corrected.yaml"), session.Yaml);
        }
    }

    [Theory]
    [InlineData("requirement")]
    [InlineData("public_output")]
    [InlineData("search_value")]
    [InlineData("operation")]
    public async Task DesignerBusinessReviewRejectsUnrelatedStructuralChanges(string mutation)
    {
        var factory = new RealProductContracts.Factory(CaptureIsolatedProductContracts());
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken);
        var retained = DesignerRetained(catalog); var corrected = DesignerCorrected(catalog);
        if (mutation == "requirement") corrected.Requirements!.Outcomes.RemoveAt(0);
        if (mutation == "public_output") corrected.Requirements!.Outputs!.Single(o => o.Name == "rows").Type.MaxItems = 12;
        if (mutation == "search_value") RetainedTasks(corrected.Plan!.Root).Single(t => t.Id == "submit_search").Inputs.Single(i => i.Name == "value").Value.Source = "unrequested_query";
        if (mutation == "operation") corrected.Plan!.Root.Tasks.RemoveAll(t => t.Id == "write");
        // Structural revision is explicitly requirement-changing. This independent
        // acceptance oracle enforces its narrower authorized business scope;
        // compiler success alone would not establish that preservation.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertDesignerBusinessPreserved(retained, corrected));
        Assert.Equal(0, factory.InvocationAttempts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://catalogue.invalid/opaque?item=7")]
    public async Task DesignerSequentialAssertionsFailWhereConditionalAlternativesSucceed(string? destination)
    {
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, TestContext.Current.CancellationToken);
        var present = V10PresentValue(new() { Kind = "input", Source = "destination" });
        var absent = new TaskValue { Kind = "predicate", Predicate = "equal", Items = [new() { Kind = "input", Source = "destination" }, new() { Kind = "null" }] };
        PlanTask Step(string id, TaskValue guard, string value) => new() { Id = id, Kind = "value", Objective = "Retain the checked " + id + " result.", Requires = guard, Outputs = [new("record", new() { Kind = "string", Text = value })] };
        var plan = new TaskPlan { Inputs = [new() { Name = "destination", Type = new() { Nullable = true } }],
            Root = new() { Tasks = [Step("available", present, "visited"), Step("missing", absent, "absent")], Outputs = [new("record", V10Output("missing", "record"))] } };
        var failed = new WorkflowCompiler().Compile(WorkflowParser.Parse(BusinessCompilation(plan, catalog, "compact-bindings-v15").Yaml));
        var inputs = new JsonObject { ["destination"] = destination };
        var result = await new WorkflowEngine().ExecuteAsync(failed.Workflows[failed.Entrypoint!], inputs, TestContext.Current.CancellationToken);
        Assert.False(result.Success); Assert.Null(result.Outputs);
        plan.Root = new() { Tasks = [new() { Id = "route", Kind = "conditional", Objective = "Choose exactly one available-data alternative.", Condition = present,
            Body = new() { Outputs = [new("record", new() { Kind = "string", Text = "visited" })] },
            Otherwise = new() { Outputs = [new("record", new() { Kind = "string", Text = "absent" })] } }], Outputs = [new("record", V10Output("route", "record"))] };
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(BusinessCompilation(plan, catalog, "compact-bindings-v15").Yaml));
        result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], inputs, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(destination is null ? "absent" : "visited", result.Outputs!["record"]!.ToString());
    }

    private static void AssertDesignerBusinessPreserved(TaskPlanFixture retained, TaskPlanFixture corrected)
    {
        Assert.Equal(JsonSerializer.Serialize(retained.Requirements!.Inputs, PlanningJsonContext.Default.ListTaskInput),
            JsonSerializer.Serialize(corrected.Requirements!.Inputs, PlanningJsonContext.Default.ListTaskInput));
        Assert.Equal(JsonSerializer.Serialize(retained.Requirements.Outputs, PlanningJsonContext.Default.ListTaskInput),
            JsonSerializer.Serialize(corrected.Requirements.Outputs, PlanningJsonContext.Default.ListTaskInput));
        Assert.Equal(retained.Requirements.Summary, corrected.Requirements.Summary);
        Assert.Equal(retained.Requirements.Outcomes, corrected.Requirements.Outcomes.Where(o => o.Id != "cleanup_browser"));
        Assert.Single(corrected.Requirements.Outcomes, o => o.Id == "cleanup_browser");
        Assert.Equal(RetainedOperations(retained.Plan!), RetainedOperations(corrected.Plan!).Where(o => !o.StartsWith("release:", StringComparison.Ordinal)));
        foreach (var old in RetainedTasks(retained.Plan!.Root).Where(t => t.Kind == "operation"))
        {
            var task = RetainedTasks(corrected.Plan!.Root).Single(t => t.Id == old.Id);
            // Only the selected search reference and deterministic writer rows
            // change. In particular the user's query and every navigation/path
            // argument remain exact, including nonliteral inputs.
            foreach (var input in old.Inputs.Where(i => !(old.Id == "submit_search" && i.Name == "reference") && !(old.Id == "write" && i.Name == "rows")))
                Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(input.Value, PlanningJsonContext.Default.TaskValue),
                    JsonSerializer.SerializeToNode(task.Inputs.Single(i => i.Name == input.Name).Value, PlanningJsonContext.Default.TaskValue)));
        }
    }

    private static async Task<PlanningSession> DesignerRevision(PlanningCatalog catalog, WorkflowPlanningRuntime runtime,
        TaskPlanFixture corrected, CancellationToken ct, string? origin = null, bool approval = false)
    {
        var original = approval ? ApprovalRetained(catalog) : DesignerRetained(catalog, origin);
        if (approval) AssertApprovalBusinessPreserved(original, corrected); else AssertDesignerBusinessPreserved(original, corrected);
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var result = RecordedPlanCompilation.CompileStatic(corrected, catalog, new()
        { TenantId = "local", Prompt = corrected.Requirements!.Summary, Policy = new() { RequireExternalConfirmation = false },
            Options = new() { ["compilation_profile"] = "compact-bindings-v15", ["mapping_profile"] = "adaptive-each-v1" } });
        Assert.True(result.Status == PlanningStatus.FinalReview, string.Join(';', result.Diagnostics));
        Assert.Null(result.ApprovedHash); Assert.Equal(0, result.ModelCalls);
        return result;
    }
    private static TaskPlanFixture DesignerRetained(PlanningCatalog catalog, string? origin = null) => DesignerFixture(catalog, "retained", origin);

    private static TaskPlanFixture DesignerFixture(PlanningCatalog catalog, string name, string? origin = null)
    {
        var proposal = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "DesignerComposition", name + ".json")), PlanningFixtureJsonContext.Default.TaskPlanFixture)!;
        foreach (var task in RetainedTasks(proposal.Plan!.Root).Where(t => t.Kind == "operation"))
            task.Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == task.Operation)).Id;
        if (origin is not null) proposal.Plan.Root.Tasks.Single(t => t.Id == "open_catalogue").Inputs.Single(i => i.Name == "url").Value.Text = origin + "/";
        return proposal;
    }

    private static TaskPlanFixture DesignerCorrected(PlanningCatalog catalog, string? origin = null)
    {
        var proposal = DesignerRetained(catalog, origin);
        var original = RetainedTasks(proposal.Plan!.Root).ToDictionary(t => t.Id);
        var home = original["open_catalogue"]; var search = original["submit_search"];
        var results = original["read_results"]; var read = original["read_product"]; var write = original["write"];
        var searchCandidates = DesignerExtract("search_control", "home_records", "controls", false);
        searchCandidates.Objective = "Extract only observed fill-capable search-control references and labels from each complete page. Copy identities and labels unchanged.";
        var control = new PlanTask { Id = "choose_control", Kind = "transform", Mode = "interpret",
            Objective = "Select the unchanged offered search-control reference for the requested search. Return null only when no appropriate control is observed.",
            Inputs = [new("controls", V10Flatten(V10Output("search_control", "controls")))],
            ResultType = new() { Kind = "object", Fields = [new() { Name = "reference", Type = new() { Nullable = true } }] } };
        var fill = new PlanTask { Id = "fill_control", Kind = "foreach", MaxItems = 1,
            Objective = "Validate the offered reference before using the original observed search control.",
            Requires = DesignerAnd(V10PresentValue(V10Output("choose_control", "reference")),
                V10PresentValue(V10Lookup(V10Flatten(V10Output("search_control", "controls")), V10Array(V10Output("choose_control", "reference"))))),
            Items = V10Lookup(V10Flatten(V10Output("home_records", "records")), V10Array(V10Output("choose_control", "reference"))),
            Body = new() { Tasks = [search], Outputs = [new("completed", V10Output(search.Id, "success"))] } };
        search.DependsOn.Clear(); search.Requires = V10PresentValue(V10Field(new() { Kind = "item" }, "reference"));
        search.Inputs[search.Inputs.FindIndex(i => i.Name == "reference")] = new("reference", V10Field(new() { Kind = "item" }, "reference"));
        fill.Body.Tasks.Add(new() { Id = "check_submission", Kind = "value", Objective = "Assert both producer success fields before observing search results.",
            Requires = DesignerAnd(V10Output(search.Id, "success"), V10Output(search.Id, "ok")),
            Outputs = [new("completed", V10Output(search.Id, "success"))] });
        results.DependsOn = [fill.Id]; results.Requires = new() { Kind = "present", Source = fill.Id };

        var extraction = DesignerExtract("extract_choices", "result_records", "candidatesPerPage", false);
        var choose = new PlanTask { Id = "choose", Kind = "transform", Mode = "interpret",
            Objective = "Select up to ten product identities in their original observed order. Return unchanged offered references only, without filtering sponsored items, deduplication, reranking or placeholders.",
            Inputs = [new("candidates", V10Flatten(V10Output(extraction.Id, "candidatesPerPage")))],
            ResultType = new() { Kind = "object", Fields = [new() { Name = "selectedProductReferenceIds", Type = new() { Kind = "array", MinItems = 0, MaxItems = 10, Items = new() } }] } };
        var visit = original["visit"]; visit.DependsOn.Clear();
        visit.Requires = V10PresentValue(V10Lookup(V10Flatten(V10Output(extraction.Id, "candidatesPerPage")), V10Output(choose.Id, "selectedProductReferenceIds")));
        visit.Items = V10Lookup(V10Flatten(V10Output("result_records", "records")), V10Output(choose.Id, "selectedProductReferenceIds"));
        var details = DesignerExtract("extract_record", "product_records", "detailCandidates", true);
        var detailDecision = new PlanTask { Id = "choose_details", Kind = "transform", Mode = "interpret",
            Objective = "Select unchanged offered IDs for name, one complete description block and price. Features or characteristics without a Description heading can be relevant; multiple suitable blocks do not establish absence. Use null only for genuine absence or uncertain subject association. Do not combine, paraphrase or shorten text; interface controls do not establish business errors.",
            Inputs = [new("detailCandidates", V10Flatten(V10Output(details.Id, "detailCandidates")))],
            ResultType = new() { Kind = "object", Fields = new[] { "Name", "Description", "Price" }.Select(f => new TaskInput
                { Name = "selected" + f + "ReferenceId", Type = new() { Nullable = true } }).ToList() } };
        var fields = V10ResolvedFields("detail", V10Flatten(V10Output(details.Id, "detailCandidates")), V10Flatten(V10Output("product_records", "records")),
            field => V10Output(detailDecision.Id, "selected" + char.ToUpperInvariant(field[0]) + field[1..] + "ReferenceId"), new() { Kind = "string", Text = "observed" });
        read.DependsOn.Clear();
        var conditional = new PlanTask { Id = "available_product", Kind = "conditional", Objective = "Visit an available original URL; otherwise preserve explicit missing fields.",
            Condition = V10PresentValue(V10Field(new() { Kind = "item" }, "href")),
            Body = new() { Tasks = [read, DesignerPages("product_records", read.Id), details, detailDecision, .. fields.Tasks], Outputs = [fields.Outputs.Single(o => o.Name == "products")] },
            Otherwise = new() { Outputs = [new("products", V10Array(new TaskValue { Kind = "object", Members = [
                new("name", new() { Kind = "null" }), new("description", new() { Kind = "null" }), new("price", new() { Kind = "null" }),
                new("error", new() { Kind = "string", Text = "href null; page not visited" })] }))] } };
        visit.Body = new() { Tasks = [conditional], Outputs = [new("products", V10Output(conditional.Id, "products"))] };
        var rows = new PlanTask { Id = "rows", Kind = "foreach", MaxItems = 10, Objective = "Check the final product bound and project exact structured cells without inference.",
            Items = V10Flatten(V10Output(visit.Id, "products")),
            Body = new() { Outputs = [new("cells", V10Array(new[] { "name", "description", "price", "error" }.Select(f => V10Field(new() { Kind = "item" }, f)).ToArray()))] } };
        var rowContract = proposal.Requirements!.Outputs!.Single(o => o.Name == "rows").Type.Items!;
        proposal.Plan.Groups.Add(new() { Id = "cell_contract", Inputs = [new() { Name = "cells", Type = rowContract }],
            Body = new() { Outputs = [new("cells", new() { Kind = "input", Source = "cells" })] } });
        var cellValues = rows.Body!.Outputs.Single().Value;
        rows.Body = new() { Tasks = [
            new() { Id = "assembled_cells", Kind = "value", Objective = "Retain the exact assembled cells before applying their cardinality contract.", Outputs = [new("cells", cellValues)] },
            new() { Id = "check_cells", Kind = "call", Group = "cell_contract",
                Objective = "Check exactly four nullable cells without converting their values.", Inputs = [new("cells", V10Output("assembled_cells", "cells"))] }],
            Outputs = [new("cells", V10Output("check_cells", "cells"))] };
        var header = new PlanTask { Id = "checked_header", Kind = "call", Group = "cell_contract",
            Objective = "Validate the supplied four-cell header using the same exact row contract.",
            Inputs = [new("cells", V10Array(new[] { "Nom", "Description", "Prix", "Statut" }.Select(s => new TaskValue { Kind = "string", Text = s }).ToArray()))] };
        var workbookRows = V10Flatten(V10Array(V10Array(V10Output(header.Id, "cells")), V10Output(rows.Id, "cells")));
        // Flatten preserves cells but cannot statically prove the accepted outer
        // cardinality. The existing typed group input checks the complete rows
        // once before writing and exposes that authoritative contract to exports.
        proposal.Plan.Groups.Add(new() { Id = "workbook_contract", Inputs = [new() {
            Name = "rows", Type = proposal.Requirements!.Outputs!.Single(o => o.Name == "rows").Type }],
            Body = new() { Outputs = [new("rows", new() { Kind = "input", Source = "rows" })] } });
        var checkedRows = new PlanTask { Id = "checked_rows", Kind = "call", Group = "workbook_contract",
            Objective = "Validate the accepted one-to-eleven rows and four nullable-string cells before writing.", Inputs = [new("rows", workbookRows)] };
        write.Inputs[write.Inputs.FindIndex(i => i.Name == "rows")] = new("rows", V10Output(checkedRows.Id, "rows"));
        write.DependsOn = [checkedRows.Id]; write.Requires = new() { Kind = "present", Source = checkedRows.Id };
        proposal.Plan.Root = new() { Tasks = [home, DesignerPages("home_records", home.Id), searchCandidates, control, fill, results,
            DesignerPages("result_records", results.Id), extraction, choose, visit, rows, header, checkedRows, write,
            new() { Id = "checked_writer", Kind = "value", Objective = "Assert the actual writer result before publishing successful outputs.",
                Requires = V10Output(write.Id, "success"), Outputs = [new("status", new() { Kind = "string", Text = "completed" })] }],
            Outputs = [new("filePath", V10Output(write.Id, "filePath")), new("relativePath", V10Output(write.Id, "relativePath")),
                new("rows", V10Output(checkedRows.Id, "rows")), new("status", V10Output("checked_writer", "status"))],
            Always = [new() { Id = "release", Kind = "operation", Objective = "Close the browser through normal finalization without replay after uncertainty.",
                Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "browser_close")).Id }] };
        proposal.Requirements!.Outcomes.Add(new("cleanup_browser", "Close the browser through existing finalization; unknown completion continues to prohibit unsafe cleanup or replay."));
        return proposal;
    }

    private static TaskValue DesignerAnd(params TaskValue[] values) => values.Aggregate((left, right) => new() { Kind = "predicate", Predicate = "and", Items = [left, right] });
    private static TaskValue DesignerFalse(TaskValue value) => new() { Kind = "predicate", Predicate = "equal", Items = [value, new() { Kind = "boolean", Boolean = false }] };
    private static PlanTask DesignerPages(string id, string producer) => new()
    {
        Id = id, Kind = "foreach", MaxItems = 100, Objective = "Validate complete producer metadata and retain each original page and record array.",
        Requires = DesignerAnd(V10Output(producer, "success"), V10Output(producer, "ok"), V10PresentValue(V10Output(producer, "observationSnapshot")),
            DesignerFalse(V10Field(V10Output(producer, "observationSnapshot"), "captureTruncated")),
            DesignerFalse(V10Field(V10Output(producer, "observationSnapshot"), "manifestTruncated"))),
        Items = V10Field(V10Output(producer, "observationSnapshot"), "pages"),
        Body = new() { Tasks = [new() { Id = id + "_checked_page", Kind = "value", Objective = "Reject a truncated original page before extraction.",
            Requires = DesignerFalse(V10Field(new() { Kind = "item" }, "captureTruncated")),
            Outputs = [new("page", new() { Kind = "item" }), new("records", V10Field(new() { Kind = "item" }, "records"))] }],
            Outputs = [new("pages", V10Output(id + "_checked_page", "page")), new("records", V10Output(id + "_checked_page", "records"))] }
    };
    private static PlanTask DesignerExtract(string id, string pages, string port, bool detail) => new()
    {
        Id = id, Kind = "transform", Mode = "extract", Each = new("pages", port),
        Objective = "Extract only the original reference and observed text needed for the immediate decision, independently for every complete page. Copy fields unchanged; preserve order and repetitions. Do not select products, synthesize identities or infer DOM ancestry from selectors.",
        Inputs = [new("pages", V10Output(pages, "pages"))],
        ResultType = new() { Kind = "object", Fields = [new() { Name = port, Type = new() { Kind = "array", MinItems = 0, MaxItems = 100,
            Items = new() { Kind = "array", MinItems = 0, MaxItems = 200, Items = new() { Kind = "object", Fields =
                (detail ? new[] { "reference", "observedText", "tag" } : new[] { "reference", "observedText" }).Select(f => new TaskInput { Name = f, Type = new() }).ToList() } } } }] }
    };
}
