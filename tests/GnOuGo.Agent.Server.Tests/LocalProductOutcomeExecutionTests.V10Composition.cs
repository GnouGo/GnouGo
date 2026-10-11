using System.Text;
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
    [Fact]
    public void V10CommittedResponsesStillValidateAgainstTheirOriginalIssuedSchemas()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "V10Composition");
        var responses = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "responses.json")))!.AsArray();
        var schemas = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "issued-schemas.json")))!.AsArray();
        Assert.Equal(4, responses.Count); Assert.Equal(4, schemas.Count);
        foreach (var response in responses)
        {
            var original = Assert.Single(schemas, s => s!["id"]!.ToString() == response!["id"]!.ToString())!;
            Assert.Empty(PlanningContractValidation.ValidateInstance(response!["response"], original["schema"]!));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V10CorrectedCompositionReachesReviewWithoutPaidCallsOrAutomaticRepair(bool revision)
    {
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new(), AppContext.BaseDirectory)));
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken); catalog.Policy.RequireExternalConfirmation = false;
        var corrected = V10Proposal(catalog, 4);
        var before = V10Proposal(catalog, 3);
        Assert.Equal(RetainedOperations(before.Plan!), RetainedOperations(corrected.Plan!));
        var original = BusinessCompilation(before.Plan!, catalog, "compact-bindings-v10");
        var compact = BusinessCompilation(corrected.Plan!, catalog, "compact-bindings-v10");
        var changes = $"steps={original.Steps}→{compact.Steps}; sets={original.Sets}→{compact.Sets}; YAML={original.Yaml.Length}→{compact.Yaml.Length}";
        // v10 already fuses these forwarding steps. Fewer source tasks and
        // smaller checked programs must not be bought by dropping the nullable
        // boundaries that were missing from the retained plan.
        Assert.Equal(original.Steps, compact.Steps); Assert.Equal(original.Sets, compact.Sets);
        Assert.True(RetainedTasks(corrected.Plan!.Root).Count() < RetainedTasks(before.Plan!.Root).Count());
        Assert.True(compact.Yaml.Length < original.Yaml.Length, changes);
        var adapter = new ProposalRuntime(runtime, corrected);
        var session = await V10PlanningSession(catalog, runtime, adapter, revision, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.FinalReview, session.Status); Assert.Empty(session.Diagnostics);
        Assert.Equal(revision ? 2 : 1, session.ModelCalls); Assert.Equal(0, session.ReplanAttempts);
        Assert.Null(session.ApprovedHash); Assert.Equal(0, factory.InvocationAttempts);
        Assert.InRange(adapter.InputEstimate, 1, session.Request.Generation.MaxInputTokensPerRequest);
        var issued = Assert.Single(adapter.Requests);
        Assert.Contains("Nullable ID:branch ID!=null", issued.Prompt);
        Assert.Contains("flatten([[headerRow],productRows])", issued.Prompt);
        Assert.Contains("No inference for typed copying", issued.Prompt);
        Assert.Contains("expression_contracts:", session.Yaml!); Assert.DoesNotContain("checkedMapping(", session.Yaml!);
        output.WriteLine($"GATE D: tasks={RetainedTasks(before.Plan!.Root).Count()}→{RetainedTasks(corrected.Plan!.Root).Count()}; " +
            $"steps={original.Steps}→{compact.Steps}; sets={original.Sets}→{compact.Sets}; YAML={Encoding.UTF8.GetByteCount(original.Yaml)}→{Encoding.UTF8.GetByteCount(compact.Yaml)}; " +
            $"maxLine={compact.Yaml.Split('\n').Max(l => l.Length)}; revision={revision}; planningCalls={session.ModelCalls}; repairs=0; estimate={adapter.InputEstimate}");
        if (Environment.GetEnvironmentVariable("GNOU_GO_V10_EVIDENCE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "corrected-taskplan.json"), JsonSerializer.Serialize(corrected.Plan, PlanningJsonContext.Default.TaskPlan), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(folder, "corrected.yaml"), session.Yaml, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("complete", "source")]
    [InlineData("missing", "different")]
    [InlineData("all_missing", "source")]
    [InlineData("unknown", "different")]
    [InlineData("unoffered", "source")]
    [InlineData("ambiguous", "different")]
    [InlineData("shortened", "source")]
    public async Task V10NullableFieldsReconnectOnlyExactOfferedOriginalBlocks(string variant, string prefix)
    {
        var textType = new TaskType { Kind = "string", Nullable = true };
        var blockType = new TaskType { Kind = "object", Fields = [new() { Name = "reference", Type = new() }, new() { Name = "text", Type = new() }] };
        var plan = new TaskPlan { Inputs = [
            new() { Name = "originals", Type = new() { Kind = "array", Items = blockType } },
            new() { Name = "offered", Type = new() { Kind = "array", Items = blockType } },
            .. new[] { "name", "description", "price" }.Select(f => new TaskInput { Name = f, Type = textType })],
            Root = V10ResolvedFields(prefix, new() { Kind = "input", Source = "offered" }, new() { Kind = "input", Source = "originals" },
                field => new() { Kind = "input", Source = field }, new() { Kind = "null" }) };
        var names = new[] { "=Observed title", "Complete observed block — café\nsecond line\tkept", "12,00 €" };
        var records = new JsonArray(names.Select((text, i) => (JsonNode)new JsonObject { ["reference"] = "opaque:record:" + i, ["text"] = text }).ToArray());
        records.Add(new JsonObject { ["reference"] = "not-offered", ["text"] = "Report a problem" });
        var values = new JsonObject { ["originals"] = records, ["offered"] = new JsonArray(records.Take(3).Select(r => r!.DeepClone()).ToArray()),
            ["name"] = "opaque:record:0", ["description"] = "opaque:record:1", ["price"] = "opaque:record:2" };
        if (variant is "missing" or "all_missing") values["description"] = null;
        if (variant == "all_missing") { values["name"] = null; values["price"] = null; }
        if (variant == "unknown") values["description"] = "unknown";
        if (variant == "unoffered") values["description"] = "not-offered";
        if (variant == "shortened") values["description"] = "record:1";
        if (variant == "ambiguous") records.Add(records[1]!.DeepClone());
        var original = values.ToJsonString(); var engine = new WorkflowEngine();
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, TestContext.Current.CancellationToken);
        var compiled = BusinessCompilation(plan, catalog, "compact-bindings-v10");
        Assert.DoesNotContain("mapping.dynamic", compiled.Yaml); Assert.DoesNotContain("llm.call", compiled.Yaml);
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(compiled.Yaml));
        var result = await engine.ExecuteAsync(workflow.Workflows[workflow.Entrypoint!], values, TestContext.Current.CancellationToken);
        Assert.Equal(original, values.ToJsonString());
        if (variant is "unknown" or "unoffered" or "ambiguous" or "shortened")
        {
            Assert.False(result.Success); Assert.Equal("CONTRACT_UNSATISFIED", result.Error?.Code); Assert.Null(result.Outputs); return;
        }
        Assert.True(result.Success, result.Error?.Message);
        var product = Assert.Single(result.Outputs!["products"]!.AsArray())!;
        Assert.Equal(variant == "all_missing" ? null : names[0], product["name"]?.GetValue<string>());
        Assert.Equal(variant == "complete" ? names[1] : null, product["description"]?.GetValue<string>());
        Assert.Equal(variant == "all_missing" ? null : names[2], product["price"]?.GetValue<string>());
        Assert.Null(product["error"]);
        Assert.True(JsonNode.DeepEquals(new JsonArray(product.AsObject().Select(p => p.Value?.DeepClone()).ToArray()),
            Assert.Single(result.Outputs["rows"]!.AsArray())));
    }

    [Fact]
    public async Task V10FieldReconnectionRemovesLearnedAssemblyWithoutChangingAcceptedOutputs()
    {
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new(), AppContext.BaseDirectory)));
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken); catalog.Policy.RequireExternalConfirmation = false;
        var proposal = V10Proposal(catalog, 2);
        var result = BusinessCompilation(proposal.Plan!, catalog, "compact-bindings-v10");
        Assert.DoesNotContain(RetainedTasks(proposal.Plan!.Root), t => t.Id == "assemble_product_detail");
        Assert.Equal(2, RetainedTasks(proposal.Plan.Root).Count(t => t.Mode == "extract"));
        Assert.Equal(new[] { "selectedNameReferenceId", "selectedDescriptionReferenceId", "selectedPriceReferenceId" },
            RetainedTasks(proposal.Plan.Root).Single(t => t.Id == "choose_product_detail_ids").ResultType!.Fields.Select(f => f.Name));
        output.WriteLine($"GATE B: exact field reconnection; tasks={RetainedTasks(proposal.Plan.Root).Count()}; steps={result.Steps}; sets={result.Sets}; YAML={Encoding.UTF8.GetByteCount(result.Yaml)}; paidCalls=0");
    }

    [Fact]
    public async Task V10TypedOriginalsRetainProducerRecordsAndNarrowConsumerViews()
    {
        var factory = new RealProductContracts.Factory(RealProductContracts.Capture(new DocumentPolicy(new(), AppContext.BaseDirectory)));
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken);
        catalog.Policy.RequireExternalConfirmation = false;
        var retained = V10Proposal(catalog, 0);
        var corrected = V10Proposal(catalog, 1);
        Assert.Equal(JsonSerializer.Serialize(retained.Requirements, PlanningJsonContext.Default.PlanningRequirements),
            JsonSerializer.Serialize(corrected.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(RetainedOperations(retained.Plan!), RetainedOperations(corrected.Plan!));
        var baseline = BusinessCompilation(retained.Plan!, catalog, "compact-bindings-v10");
        var result = BusinessCompilation(corrected.Plan!, catalog, "compact-bindings-v10");
        foreach (var task in RetainedTasks(corrected.Plan!.Root).Where(t => t.Id is "project_search_page_records" or "project_product_page_records"))
        {
            Assert.Equal("foreach", task.Kind); Assert.Empty(task.Body!.Tasks);
            Assert.Equal("records", task.Body.Outputs.Single().Value.Port);
            Assert.Equal("item", task.Body.Outputs.Single().Value.Items.Single().Kind);
            Assert.Null(task.Each); Assert.Null(task.ResultType);
        }
        Assert.Equal(6, RetainedTasks(retained.Plan!.Root).Count(t => t.Mode == "extract"));
        Assert.Equal(4, RetainedTasks(corrected.Plan.Root).Count(t => t.Mode == "extract"));
        foreach (var task in RetainedTasks(corrected.Plan.Root).Where(t => t.Id is "extract_product_links" or "extract_product_detail"))
        {
            Assert.Equal("pages", Assert.Single(task.Inputs).Name);
            Assert.Equal("pages", task.Each!.Input);
            Assert.Equal("array", task.ResultType!.Fields.Single().Type.Items!.Kind);
            Assert.DoesNotContain(task.ResultType.Fields.Single().Type.Items!.Items!.Fields, f => f.Name is "href" or "actions" or "kind" or "observedFact");
        }
        Assert.Contains(".map(", result.Yaml); Assert.Contains("expression_contracts:", result.Yaml);
        Assert.DoesNotContain("checkedMapping(", result.Yaml);
        Assert.Equal(0, factory.InvocationAttempts);
        output.WriteLine($"GATE A: typed copy mapping stages=2→0; tasks={RetainedTasks(corrected.Plan.Root).Count()}; " +
            $"steps={baseline.Steps}→{result.Steps}; sets={baseline.Sets}→{result.Sets}; YAML={Encoding.UTF8.GetByteCount(baseline.Yaml)}→{Encoding.UTF8.GetByteCount(result.Yaml)}; paidCalls=0");
    }

    private static PlanningProposal V10Proposal(PlanningCatalog catalog, int gate)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "V10Composition");
        var plan = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "retained.json")), PlanningJsonContext.Default.TaskPlan)!;
        var proposal = new PlanningProposal { Plan = plan, Requirements = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(directory, "requirements.json")), PlanningJsonContext.Default.PlanningRequirements)! };
        var methods = new Dictionary<string, string> { ["cap_490d9e16075cbdca5954ec27"] = "browser_get_content",
            ["cap_37c513d225bb1b870053c5d3"] = "document_write", ["cap_bdb7ba9ab04b18783883fa86"] = "browser_close" };
        foreach (var task in RetainedTasks(plan.Root).Where(t => t.Kind == "operation"))
            task.Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == methods[task.Operation!])).Id;
        if (gate == 0) return proposal;
        foreach (var task in RetainedTasks(plan.Root).Where(t => t.Id is "project_search_page_records" or "project_product_page_records"))
        {
            var port = task.Each!.Output;
            task.Kind = "foreach"; task.Items = task.Inputs.Single().Value; task.Inputs.Clear();
            task.Mode = null; task.Each = null; task.ResultType = null; task.MaxItems = 100;
            task.Objective = "Retain the producer's complete original record arrays through a checked typed projection.";
            task.Body = new() { Outputs = [new(port, V10Field(new() { Kind = "item" }, "records"))] };
        }
        foreach (var task in RetainedTasks(plan.Root).Where(t => t.Id is "extract_product_links" or "extract_product_detail"))
        {
            task.Inputs.RemoveAll(i => i.Name != "pages");
            var fields = task.ResultType!.Fields.Single().Type.Items!.Items!.Fields;
            fields.RemoveAll(f => f.Name is not ("reference" or "observedText"));
            foreach (var field in fields) field.Type.Nullable = false;
            if (task.Id == "extract_product_detail") fields.Add(new() { Name = "tag", Type = new() { Kind = "string" } });
            task.Objective = (task.Id == "extract_product_links" ? "Extract item-link candidates for the requested catalogue selection from every bounded page. " :
                "Extract observed name, complete description and price block candidates, with their native tags, from every bounded page. ") +
                "Copy all retained fields verbatim; emit only the declared decision view, without selection or invented classifications.";
        }
        if (gate == 1) return proposal;
        var visit = plan.Root.Tasks.Single(t => t.Id == "scrape_products");
        var decision = visit.Body!.Tasks.Single(t => t.Id == "choose_product_detail_ids");
        decision.ResultType!.Fields.RemoveAll(f => f.Name == "observedError");
        decision.Objective = "Choose only unchanged offered identities for the requested name, complete description block and price. " +
            "Return null only for a permitted missing fact after examining every candidate; interface affordances do not establish a business error.";
        var fieldsScope = V10ResolvedFields("detail", V10Flatten(V10Output("extract_product_detail", "detailCandidates")),
            V10Flatten(V10Output("project_product_page_records", "pageRecords")),
            field => V10Output("choose_product_detail_ids", "selected" + char.ToUpperInvariant(field[0]) + field[1..] + "ReferenceId"),
            V10Output("open_product_page", "error_message"));
        visit.Body.Tasks.RemoveAll(t => t.Id is "assemble_product_detail" or "make_product_row");
        visit.Body.Tasks.AddRange(fieldsScope.Tasks);
        visit.Body.Outputs = fieldsScope.Outputs;
        var checkedProducts = plan.Root.Tasks.Single(t => t.Id == "checked_products_projection");
        // Flatten does not prove the accepted cardinality; retain the existing
        // deterministic bound rather than relying on a learned copy to assert it.
        checkedProducts.Kind = "foreach"; checkedProducts.Mode = null; checkedProducts.ResultType = null;
        checkedProducts.Each = null; checkedProducts.Inputs.Clear(); checkedProducts.MaxItems = 10;
        checkedProducts.Items = V10Flatten(V10Output("scrape_products", "products"));
        checkedProducts.Body = new() { Outputs = [new("products", new() { Kind = "item" }),
            new("rows", V10Array(new[] { "name", "description", "price", "error" }.Select(f => V10Field(new() { Kind = "item" }, f)).ToArray()))] };
        var writer = plan.Root.Tasks.Single(t => t.Id == "save_products_xlsx");
        writer.Inputs.Single(i => i.Name == "rows").Value.Kind = "flatten";
        writer.Inputs.Single(i => i.Name == "rows").Value.Source = null;
        writer.Inputs.Single(i => i.Name == "rows").Value.Port = null;
        writer.Inputs.Single(i => i.Name == "rows").Value.Items = [V10Output("scrape_products", "rows")];
        if (gate == 2) return proposal;
        writer.Inputs.Single(i => i.Name == "rows").Value.Items = [V10Array(
            V10Array(V10Array(new[] { "name", "description", "price", "error" }.Select(f => new TaskValue { Kind = "string", Text = f }).ToArray())),
            V10Output("checked_products_projection", "rows"))];
        writer.DependsOn.Remove("build_xlsx_rows"); writer.DependsOn.Add("checked_products_projection");
        plan.Root.Tasks.RemoveAll(t => t.Id == "build_xlsx_rows");
        if (gate == 3) return proposal;
        var offeredLookup = plan.Root.Tasks.Single(t => t.Id == "validate_selected_candidates").Outputs.Single().Value;
        var originalLookup = plan.Root.Tasks.Single(t => t.Id == "resolve_selected_product_records").Outputs.Single().Value;
        visit.Requires = V10PresentValue(offeredLookup); visit.Items = originalLookup;
        visit.DependsOn.Remove("resolve_selected_product_records");
        plan.Root.Tasks.RemoveAll(t => t.Id is "validate_selected_candidates" or "resolve_selected_product_records");
        // The bounded final projection already exports rows and records. Do not
        // carry a second cell array through every nullable assembly scope.
        visit.Body.Outputs.RemoveAll(o => o.Name == "rows");
        foreach (var task in RetainedTasks(visit.Body))
            task.Body?.Outputs.RemoveAll(o => o.Name is "row" or "rows");
        return proposal;
    }

    private static async Task<PlanningSession> V10PlanningSession(PlanningCatalog catalog, WorkflowPlanningRuntime runtime,
        ProposalRuntime adapter, bool revision, CancellationToken ct)
    {
        var planner = new HybridWorkflowPlanner();
        var session = new PlanningSession { Catalog = catalog, Request = new()
        {
            TenantId = "local", Options = new() { ["compilation_profile"] = "compact-bindings-v10" },
            Prompt = "Preserve the accepted observed-data task and its exact output contracts.",
            Policy = new() { RequireExternalConfirmation = false }, Generation = new() { MaxInputTokensPerRequest = 96000, MaxOutputTokens = 32768 }
        } };
        if (!revision) return await planner.AdvanceAsync(session, new(), adapter, ct);
        session = await planner.AdvanceAsync(session, new(), new ProposalRuntime(runtime, V10Proposal(catalog, 0)), ct);
        Assert.True(session.Status == PlanningStatus.FinalReview, string.Join('\n', session.Diagnostics));
        var saved = JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSession);
        var copy = JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSession)!;
        copy = await planner.AdvanceAsync(copy, new() { Kind = "revise", PreserveRequirements = true,
            ExpectedRevision = copy.Revision, ArtifactHash = copy.ComputeArtifactHash(),
            Text = "Revise only data adaptation: typed original-record projections, compact page extraction, strict offered/original lookup, guarded nullable facts, exact observed blocks and header-plus-rows assembly. Preserve all accepted requirements, public output contracts, operations, order, writing and cleanup." }, adapter, ct);
        Assert.Null(copy.Yaml); Assert.Null(copy.ApprovedHash);
        Assert.Equal(JsonSerializer.Serialize(session.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(copy.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        copy = await planner.AdvanceAsync(copy, new() { ExpectedRevision = copy.Revision }, adapter, ct);
        Assert.Equal(saved, JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(JsonSerializer.Serialize(session.Requirements, PlanningJsonContext.Default.PlanningRequirements),
            JsonSerializer.Serialize(copy.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        return copy;
    }

    // Existing nullable branches and bounded projections are deliberately explicit:
    // lookup returns records[], including for a singleton. No array-field access,
    // scalar coercion, fabricated sentinel identity or learned reconnection.
    private static TaskScope V10ResolvedFields(string prefix, TaskValue offered, TaskValue originals,
        Func<string, TaskValue> selected, TaskValue error)
    {
        var scope = new TaskScope();
        foreach (var field in new[] { "name", "description", "price" })
        {
            var id = prefix + "_" + field;
            scope.Tasks.Add(new()
            {
                Id = id, Kind = "conditional", Objective = "Resolve an offered " + field + " block or retain explicit absence.",
                Condition = V10PresentValue(selected(field)),
                Body = new() { Tasks = [new()
                {
                    Id = id + "_original", Kind = "foreach", MaxItems = 1,
                    Objective = "Copy the exact original observed block after offered-ID validation.",
                    Requires = V10PresentValue(V10Lookup(offered, V10Array(selected(field)))),
                    Items = V10Lookup(originals, V10Array(selected(field))),
                    Body = new() { Outputs = [new("text", V10Field(new() { Kind = "item" }, "text"))] }
                }], Outputs = [new("values", V10Output(id + "_original", "text"))] },
                Otherwise = new() { Outputs = [new("values", V10Array(new TaskValue { Kind = "null" }))] }
            });
        }
        var price = new PlanTask { Id = prefix + "_price_row", Kind = "foreach", MaxItems = 1,
            Objective = "Assemble one non-null product from the checked optional fact values.", Items = V10Output(prefix + "_price", "values"),
            Body = new() { Outputs = [
                new("product", new() { Kind = "object", Members = [new("name", V10Output(prefix + "_keep_name", "name")),
                    new("description", V10Output(prefix + "_keep_description", "description")), new("price", new() { Kind = "item" }), new("error", error)] }),
                new("row", V10Array(V10Output(prefix + "_keep_name", "name"), V10Output(prefix + "_keep_description", "description"), new() { Kind = "item" }, error))] } };
        var description = new PlanTask { Id = prefix + "_description_row", Kind = "foreach", MaxItems = 1,
            Objective = "Capture the checked nullable description for deterministic assembly.", Items = V10Output(prefix + "_description", "values"),
            Body = new() { Tasks = [new() { Id = prefix + "_keep_description", Kind = "value", Objective = "Capture the exact description across the nested item scope.",
                Outputs = [new("description", new() { Kind = "item" })] }, price],
                Outputs = [new("products", V10Output(price.Id, "product")), new("rows", V10Output(price.Id, "row"))] } };
        var name = new PlanTask { Id = prefix + "_name_row", Kind = "foreach", MaxItems = 1,
            Objective = "Capture the checked nullable name for deterministic assembly.", Items = V10Output(prefix + "_name", "values"),
            Body = new() { Tasks = [new() { Id = prefix + "_keep_name", Kind = "value", Objective = "Capture the exact name across nested item scopes.",
                Outputs = [new("name", new() { Kind = "item" })] }, description],
                Outputs = [new("products", V10Flatten(V10Output(description.Id, "products"))), new("rows", V10Flatten(V10Output(description.Id, "rows")))] } };
        scope.Tasks.Add(name);
        scope.Outputs = [new("products", V10Flatten(V10Output(name.Id, "products"))), new("rows", V10Flatten(V10Output(name.Id, "rows")))];
        return scope;
    }

    private static TaskValue V10Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };
    private static TaskValue V10Field(TaskValue value, string field) => new() { Kind = "field", Port = field, Items = [value] };
    private static TaskValue V10Flatten(TaskValue value) => new() { Kind = "flatten", Items = [value] };
    private static TaskValue V10Array(params TaskValue[] values) => new() { Kind = "array", Items = values.ToList() };
    private static TaskValue V10Lookup(TaskValue records, TaskValue ids) => new() { Kind = "lookup", Port = "reference", Items = [records, ids] };
    private static TaskValue V10PresentValue(TaskValue value) => new() { Kind = "predicate", Predicate = "not_equal", Items = [value, new() { Kind = "null" }] };
}
