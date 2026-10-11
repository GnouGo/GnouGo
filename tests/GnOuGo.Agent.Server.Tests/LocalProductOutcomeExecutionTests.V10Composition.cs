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
            task.Objective = "Extract candidate block identities and complete observed text from every bounded page. " +
                "Copy all retained fields verbatim; emit only the declared decision view, without selection or invented classifications.";
        }
        return proposal;
    }

    private static TaskValue V10Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };
    private static TaskValue V10Field(TaskValue value, string field) => new() { Kind = "field", Port = field, Items = [value] };
    private static TaskValue V10Flatten(TaskValue value) => new() { Kind = "flatten", Items = [value] };
    private static TaskValue V10Array(params TaskValue[] values) => new() { Kind = "array", Items = values.ToList() };
    private static TaskValue V10Lookup(TaskValue records, TaskValue ids) => new() { Kind = "lookup", Port = "reference", Items = [records, ids] };
    private static TaskValue V10PresentValue(TaskValue value) => new() { Kind = "predicate", Predicate = "not_equal", Items = [value, new() { Kind = "null" }] };
}
