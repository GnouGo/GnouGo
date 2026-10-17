using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GnOuGo.Document.Mcp;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BusinessTaskPlanExecutionTests
{
    [Theory]
    [InlineData("alpha", "title", false, "multiple")]
    [InlineData("omega", "label", true, "multiple")]
    [InlineData("alpha", "title", false, "empty")]
    [InlineData("omega", "label", true, "empty")]
    [InlineData("alpha", "title", false, "missing")]
    [InlineData("omega", "label", true, "denied")]
    public async Task FourBusinessTasksGenerateOnceAndWriteAnIndependentlyVerifiedWorkbook(string operation, string field, bool parallel, string variant)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("gnougo-business-plan-").FullName;
        try
        {
            var writer = new DocumentOperationHost(new DocumentPolicy(new() { DefaultWorkingDirectory = root }, root), NullLogger<DocumentOperationHost>.Instance);
            var rows = variant == "empty" ? new JsonArray() : new JsonArray(new JsonObject { [field] = "First product", ["cost"] = "12.50" }, new JsonObject { [field] = "第二 produit", ["cost"] = "8.00" });
            if (variant == "missing") rows[1]!.AsObject().Remove(field);
            var record = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { [field] = new JsonObject { ["type"] = "string" }, ["cost"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray(field, "cost"), ["additionalProperties"] = false };
            var collection = new JsonObject { ["type"] = "array", ["items"] = record };
            var calls = new List<string>(); var factory = new InMemoryMcpClientFactory();
            // Plain MCP schemas, deliberately without GnOuGo effect/artifact metadata.
            // This producer accepts rows and owns their deterministic document encoding.
            factory.RegisterServer("fixture", new() { Tools = [
                new() { Name = operation, InputSchema = JsonNode.Parse("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}"""), OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["entries"] = collection.DeepClone() }, ["required"] = new JsonArray("entries"), ["additionalProperties"] = false } },
                new() { Name = operation + "_store", InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["rows"] = collection.DeepClone() }, ["required"] = new JsonArray("rows"), ["additionalProperties"] = false }, OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""") }
            ], ToolHandlers = new()
            {
                [operation] = input => { Assert.Equal("requested products", input!["query"]!.ToString()); calls.Add("search"); return new() { Content = new JsonObject { ["entries"] = rows.DeepClone() } }; },
                [operation + "_store"] = input =>
                {
                    calls.Add("write");
                    var content = "Name\tPrice\n" + string.Join('\n', input!["rows"]!.AsArray().Select(r => r![field]!.ToString() + "\t" + r["cost"]!.ToString()));
                    var written = writer.Write("result.xlsx", content, null, false); Assert.True(written.Success);
                    return new() { Content = new JsonObject { ["path"] = Path.Combine(root, "result.xlsx") } };
                }
            } });
            var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human(variant != "denied") };
            var actual = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
            var catalog = await actual.DiscoverAsync(new(), ct);
            foreach (var source in await actual.Capabilities.ListSourcesAsync(ct))
                foreach (var entry in (await actual.Capabilities.ListAsync(source.Id, null, ct)).Capabilities)
                    catalog.Capabilities.Add(await actual.Capabilities.ResolveAsync(entry, ct));
            string Id(string method) => catalog.Capabilities.Single(c => c.Method == method).Id;
            TaskValue Field(string name) => new() { Kind = "field", Items = [new() { Kind = "item" }], Port = name };
            var plan = new TaskPlan { Inputs = [new() { Name = "query" }], Root = new() { Tasks = [
                new() { Id = "search", Objective = "Search requested products", Operation = Id(operation), Inputs = [new("query", new() { Kind = "input", Source = "query" })] },
                new() { Id = "products", Kind = "foreach", Objective = "Extract each product's selected fields", Items = ProductTransformationPlan.Ref("search", "entries"), Parallel = parallel, MaxItems = 3, MaxConcurrency = 2,
                    Body = new() { Tasks = [new() { Id = "extract", Kind = "value", Objective = "Select name and price", Outputs = [new("row", new() { Kind = "object", Members = [new(field, Field(field)), new("cost", Field("cost"))] })] }], Outputs = [new("rows", ProductTransformationPlan.Ref("extract", "row"))] } },
                new() { Id = "write", Objective = "Write selected rows to Excel", Operation = Id(operation + "_store"), Inputs = [new("rows", ProductTransformationPlan.Ref("products", "rows"))] }
            ], Outputs = [new("workbook", ProductTransformationPlan.Ref("write", "path"))] } };
            var proposal = new PlanningProposal { Plan = plan, Requirements = new() { Summary = "Search products, extract names and prices, write Excel", Inputs = plan.Inputs, Outcomes = [new("report", "An Excel workbook containing the selected products")] } };
            var runtime = new LocalProductOutcomeExecutionTests.ProposalRuntime(actual, proposal);
            var session = await new HybridWorkflowPlanner().AdvanceAsync(new() { Catalog = catalog, Request = new() { TenantId = "local", Prompt = proposal.Requirements.Summary } }, new(), runtime, ct);
            Assert.True(session.Status == PlanningStatus.FinalReview, string.Join(';', session.Diagnostics.Select(d => d.Message)));
            Assert.Equal(1, session.ModelCalls); Assert.Equal(0, session.ReplanAttempts); Assert.Null(session.OutcomeBindings); Assert.Null(session.OutcomeVersion);
            Assert.All(session.Requirements!.Outcomes, o => Assert.Null(o.Execution));
            PlanningArtifactApproval.Verify(session);
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(session.Yaml!));
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["query"] = "requested products" }, ct);
            Assert.Equal(variant is "multiple" or "empty", result.Success);
            if (!result.Success)
            {
                Assert.False(File.Exists(Path.Combine(root, "result.xlsx")));
                Assert.Equal(variant == "denied" ? [] : new[] { "search" }, calls);
                return;
            }
            Assert.Equal(new[] { "search", "write" }, calls);
            Assert.Equal(Path.Combine(root, "result.xlsx"), result.Outputs!["workbook"]!.ToString());
            using var workbook = SpreadsheetDocument.Open(Path.Combine(root, "result.xlsx"), false);
            var cells = Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet!.Descendants<Row>().Select(r => r.Elements<Cell>().Select(c => c.InnerText).ToArray()).ToArray();
            Assert.Equal(variant == "empty" ? 1 : 3, cells.Length);
            Assert.Equal(new[] { "Name", "Price" }, cells[0]);
            if (variant != "empty") { Assert.Equal(new[] { "First product", "12.50" }, cells[1]); Assert.Equal(new[] { "第二 produit", "8.00" }, cells[2]); }
        }
        finally { Directory.Delete(root, true); }
    }
}
