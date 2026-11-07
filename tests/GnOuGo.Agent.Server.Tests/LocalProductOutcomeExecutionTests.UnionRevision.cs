using GnOuGo.Planning.Examples;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Fact]
    public async Task CompactUnionUsesExistingBindingsAndPreservesRequirements()
    {
        var factory = new GnOuGo.Planning.Examples.RealProductContracts.Factory(CaptureIsolatedProductContracts());
        var runtime = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
        var catalog = await RetainedCatalog(runtime, TestContext.Current.CancellationToken);
        catalog.Policy.RequireExternalConfirmation = false;
        var original = UnionRevisionProposal(catalog); var compact = CompactUnionProposal(catalog);
        Assert.Equal(JsonSerializer.Serialize(original.Requirements, PlanningJsonContext.Default.PlanningRequirements),
            JsonSerializer.Serialize(compact.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(RetainedOperations(original.Plan!), RetainedOperations(compact.Plan!));
        Assert.Equal(0, factory.InvocationAttempts);
        var previous = BusinessCompilation(compact.Plan!, catalog, "compact-bindings-v9");
        var current = BusinessCompilation(compact.Plan!, catalog, "compact-bindings-v10");
        Assert.True(current.Yaml.Length < previous.Yaml.Length);
        Assert.Equal(previous.Steps, current.Steps); Assert.Equal(previous.Sets, current.Sets);
        output.WriteLine($"SAME COMPOSITION v9→v10 YAML={previous.Yaml.Length}→{current.Yaml.Length}; " +
            $"maxLine={previous.Yaml.Split('\n').Max(l => l.Length)}→{current.Yaml.Split('\n').Max(l => l.Length)}; " +
            $"steps={previous.Steps}→{current.Steps}; sets={previous.Sets}→{current.Sets}");
    }

    private static TaskPlanFixture UnionRevisionProposal(PlanningCatalog catalog)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UnionRevision");
        var baseline = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "failed.json")), PlanningJsonContext.Default.TaskPlan)!;
        var requirements = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "requirements.json")), PlanningJsonContext.Default.PlanningRequirements)!;
        var proposal = BusinessProposal("verbatim_rows", catalog);
        proposal.Requirements = requirements;
        var plan = proposal.Plan!;
        plan.Inputs = baseline.Inputs;
        plan.Root.Tasks.Single(t => t.Id == "locate").Objective = baseline.Root.Tasks.Single(t => t.Id == "build_search_url").Objective;
        var visit = plan.Root.Tasks.Single(t => t.Id == "visit");
        // Keep the authoritative record collection independently of the compact
        // view. Description judgment chooses an identity, never replacement text.
        visit.Body!.Tasks.Insert(2, new()
        {
            Id = "original_blocks", Kind = "foreach", MaxItems = 100,
            Objective = "Retain exact observed text for selected block identities.",
            Items = Field(Output("read", "observationSnapshot"), "pages"),
            Requires = new() { Kind = "present", Source = "extract_facts" },
            Body = new() { Outputs = [new("records", Field(new() { Kind = "item" }, "records"))] }
        });
        var decision = visit.Body.Tasks.Single(t => t.Id == "reconcile");
        visit.Body.Tasks.Insert(visit.Body.Tasks.IndexOf(decision), new()
        {
            Id = "description_view", Kind = "foreach", MaxItems = 2000,
            Objective = "Give description selection only the block identity and observed description.",
            Items = Flatten(Output("extract_facts", "pageFacts")),
            Body = new() { Outputs = [new("candidate", new() { Kind = "object", Members =
                [new("key", Field(new() { Kind = "item" }, "key")), new("description", Field(new() { Kind = "item" }, "description"))] })] }
        });
        decision.Inputs = [new("offeredBlocks", Output("description_view", "candidate"))];
        var selected = visit.Body.Tasks.Single(t => t.Id == "selected_block");
        var item = new TaskValue { Kind = "item" };
        selected.Body!.Tasks =
        [
            new() { Id = "chosen_facts", Kind = "value", Objective = "Capture the checked selected facts across the description scope.",
                Outputs = [new("record", item)] },
            new() { Id = "exact_description", Kind = "conditional", Objective = "Preserve explicit absence or recover the complete original description block.",
                Condition = new() { Kind = "predicate", Predicate = "not_equal", Items = [Field(item, "description"), new() { Kind = "null" }] },
                Body = new() { Tasks = [new()
                {
                    Id = "original_description", Kind = "foreach", MaxItems = 1,
                    Objective = "Resolve the offered description identity against the original observation.",
                    Items = new() { Kind = "lookup", Port = "reference", Items =
                        [Flatten(Output("original_blocks", "records")), new() { Kind = "array", Items = [Field(item, "key")] }] },
                    Body = new() { Outputs = [new("record", Product(Field(new() { Kind = "item" }, "text")))] }
                }], Outputs = [new("records", Output("original_description", "record"))] },
                Otherwise = new() { Outputs = [new("records", new() { Kind = "array", Items = [Product(new() { Kind = "null" })] })] } }
        ];
        selected.Body.Outputs = [new("records", Output("exact_description", "records"))];
        visit.Body.Outputs = [new("rows", Flatten(Output("selected_block", "records")))];
        var format = plan.Root.Tasks.Single(t => t.Id == "format");
        format.Body!.Outputs[0] = new("cells", new() { Kind = "array", Items =
            new[] { "name", "description", "price", "observed_error" }.Select(f => Field(new() { Kind = "item" }, f)).ToList() });
        var writer = plan.Root.Tasks.Single(t => t.Id == "write");
        writer.Inputs.Single(i => i.Name == "filePath").Value.Text = baseline.Root.Tasks.Single(t => t.Id == "write_products_file").Inputs.Single(i => i.Name == "filePath").Value.Text;
        var headers = writer.Inputs.Single(i => i.Name == "rows").Value.Items[0].Items[0].Items[0].Items;
        headers[3].Text = "observed_error";
        plan.Root.Outputs = [new("products_file_path", Output("write", "filePath")), new("products", Output("format", "record")),
            new("browser_closed", Output("release", "closed")), new("success", new() { Kind = "predicate", Predicate = "and",
                Items = [Output("write", "success"), Output("release", "closed")] })];
        // No interpretation for row assembly or status. Neither the accepted
        // interface nor any external contract is widened to make this compile.
        return proposal;

        static TaskValue Output(string source, string port) => new() { Kind = "output", Source = source, Port = port };
        static TaskValue Field(TaskValue value, string port) => new() { Kind = "field", Port = port, Items = [value] };
        static TaskValue Flatten(TaskValue value) => new() { Kind = "flatten", Items = [value] };
        static TaskValue Product(TaskValue description) => new() { Kind = "object", Members =
        [new("name", Field(Output("chosen_facts", "record"), "name")), new("description", description),
         new("price", Field(Output("chosen_facts", "record"), "price")), new("observed_error", Field(Output("chosen_facts", "record"), "error"))] };
    }

    private TaskPlanFixture CompactUnionProposal(PlanningCatalog catalog)
    {
        var proposal = UnionRevisionProposal(catalog);
        var plan = proposal.Plan!;
        var before = BusinessCompilation(plan, catalog, "compact-bindings-v9");
        var visit = plan.Root.Tasks.Single(t => t.Id == "visit");
        var offered = plan.Root.Tasks.Single(t => t.Id == "verified_ids").Items!;
        // Evaluate the strict offered lookup before the original-data lookup.
        // This is an assertion over actual typed data, not a declared success flag.
        visit.Requires = new() { Kind = "predicate", Predicate = "not_equal", Items = [offered, new() { Kind = "null" }] };
        visit.Items!.Items[1] = Output("choose", "productKeys");
        visit.DependsOn.Remove("verified_ids");
        plan.Root.Tasks.RemoveAll(t => t.Id == "verified_ids");

        var selected = visit.Body!.Tasks.Single(t => t.Id == "selected_block");
        var description = selected.Body!.Tasks.Single(t => t.Id == "exact_description");
        selected.Body.Tasks.Single(t => t.Id == "chosen_facts").Outputs = new[] { "name", "price", "error" }
            .Select(name => new TaskOutput(name, Field(new() { Kind = "item" }, name))).ToList();
        foreach (var product in new[] { description.Body!.Tasks.Single().Body!.Outputs.Single().Value,
            description.Otherwise!.Outputs.Single().Value.Items.Single() })
            foreach (var member in product.Members.Where(m => m.Value.Kind == "field" && m.Value.Items.Single() is { Kind: "output", Source: "chosen_facts" }))
            { member.Value.Kind = "output"; member.Value.Source = "chosen_facts"; member.Value.Items.Clear(); }
        // Keep the existing row projection: besides cells, it enforces the
        // accepted <=10 product contract after flatten. Moving cells to every
        // branch increases v9 YAML; dropping this loop loses the required bound.
        // Its independent check takes priority over the 17-task size target.

        var after = BusinessCompilation(plan, catalog, "compact-bindings-v9");
        Assert.Equal(18, RetainedTasks(plan.Root).Count());
        output.WriteLine($"COMPACT UNION v9 tasks=19→18; steps={before.Steps}→{after.Steps}; sets={before.Sets}→{after.Sets}; YAML={before.Yaml.Length}→{after.Yaml.Length}");
        Assert.True(after.Steps < before.Steps, $"steps {before.Steps}→{after.Steps}"); Assert.True(after.Sets < before.Sets, $"sets {before.Sets}→{after.Sets}");
        Assert.True(after.Yaml.Length < before.Yaml.Length, $"YAML {before.Yaml.Length}→{after.Yaml.Length}");
        return proposal;

        static TaskValue Output(string source, string port) => new() { Kind = "output", Source = source, Port = port };
        static TaskValue Field(TaskValue value, string port) => new() { Kind = "field", Port = port, Items = [value] };
    }

    private async Task<PlanningSession> ReviseUnionFixture(PlanningSession state, WorkflowPlanningRuntime runtime, TaskPlanFixture corrected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); await Task.CompletedTask;
        var correctedState = RecordedPlanCompilation.CompileStatic(corrected, state.Catalog!, new()
        { TenantId = "local", Prompt = corrected.Requirements!.Summary, Policy = new() { RequireExternalConfirmation = false },
            Options = new() { ["compilation_profile"] = "compact-bindings-v15", ["mapping_profile"] = "adaptive-each-v1" } });
        Assert.True(correctedState.Status == PlanningStatus.FinalReview, string.Join(';', correctedState.Diagnostics));
        return correctedState;
    }

}
