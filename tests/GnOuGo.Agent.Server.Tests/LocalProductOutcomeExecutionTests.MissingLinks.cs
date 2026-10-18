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

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Theory]
    [InlineData("continue")]
    [InlineData("empty")]
    [InlineData("assertion")]
    [InlineData("write_failed")]
    public async Task MissingLinksUseExplicitErrorRowsAndDoNotStopLaterVisits(string variant)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("gnougo-missing-link-rows-").FullName;
        try
        {
            var policy = new DocumentPolicy(new DocumentServerSettings { DefaultWorkingDirectory = root }, root);
            Assert.Equal(root, policy.DefaultWorkingDirectory);
            var contracts = RealProductContracts.Capture(policy);
            contracts.Remove("browser");
            contracts["observations"] = JsonSerializer.SerializeToNode(new[]
            {
                // Like an observed-page reader, null may mean the current resource.
                // This request instead requires an explicit offered link for a visit.
                new McpToolInfo { Name = "read_item", EffectKind = "read", InputSchema = JsonNode.Parse("""{"type":"object","properties":{"url":{"type":["string","null"]}},"required":["url"],"additionalProperties":false}"""),
                    OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"name":{"type":"string"},"description":{"type":["string","null"]},"price":{"type":["string","null"]}},"required":["name","description","price"],"additionalProperties":false}""") },
                new McpToolInfo { Name = "close", EffectKind = "lifecycle", InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
                    OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"closed":{"type":"boolean"}},"required":["closed"],"additionalProperties":false}""") }
            }, RealProductContracts.Json);
            var factory = new RealProductContracts.Factory(contracts);
            var writer = new DocumentTools(new DocumentOperationHost(policy, NullLogger<DocumentOperationHost>.Instance), NullLogger<DocumentTools>.Instance);
            var effects = new List<string>();
            var visits = new List<string>();
            const string repeated = "https://fixture.invalid/item?opaque=a%2Fb";
            var last = "https://fixture.invalid/" + new string('r', 600);
            var observations = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
            {
                [repeated] = new() { ["name"] = "=Exact\t— café 漢🙂", ["description"] = "Complete\nsecond line ${literal}", ["price"] = null },
                [last] = new() { ["name"] = "Last", ["description"] = null, ["price"] = "12,00 €" }
            };
            factory.Handler = (_, tool, args, _) =>
            {
                effects.Add(tool);
                if (tool == "read_item")
                {
                    var url = args!["url"]!.GetValue<string>(); visits.Add(url);
                    return Task.FromResult(new McpCallResult { Content = observations[url].DeepClone() });
                }
                if (tool == "close") return Task.FromResult(new McpCallResult { Content = new JsonObject { ["closed"] = true } });
                Assert.Equal("document_write", tool);
                var result = writer.WriteDocument(variant == "write_failed" ? "../forbidden.xlsx" : args!["filePath"]!.GetValue<string>(),
                    rows: args!["rows"]!.Deserialize<string?[][]>(RealProductContracts.Json));
                return Task.FromResult(new McpCallResult { IsError = !result.Success, Content = JsonSerializer.SerializeToNode(result, RealProductContracts.Json) });
            };
            var inference = new ForbiddenRowInference();
            var engine = new WorkflowEngine { McpClientFactory = factory, LLMClient = inference };
            var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
            var catalog = await RetainedCatalog(runtime, ct); catalog.Policy.RequireExternalConfirmation = false;
            string Operation(string method) => TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == method)).Id;
            TaskValue Item(string name) => new() { Kind = "field", Port = name, Items = [new() { Kind = "item" }] };
            TaskValue PresentLink() => new() { Kind = "predicate", Predicate = "not_equal", Items = [Item("href"), new() { Kind = "null" }] };
            TaskValue Array(params TaskValue[] values) => new() { Kind = "array", Items = [.. values] };
            TaskValue Text(string value) => new() { Kind = "string", Text = value };
            TaskValue Ref(string task, string port) => new() { Kind = "output", Source = task, Port = port };
            var visit = new PlanTask { Id = "visit", Kind = "operation", Objective = "Read the exact observed link", Operation = Operation("read_item"),
                Requires = PresentLink(), Inputs = [new("url", Item("href"))] };
            var visited = new TaskScope { Tasks = [visit], Outputs = [new("row", Array(Ref("visit", "name"), Ref("visit", "description"), Ref("visit", "price"), new() { Kind = "null" }))] };
            var plan = new TaskPlan
            {
                Inputs = [new() { Name = "items", Type = new() { Kind = "array", MaxItems = 10, Items = ProductTransformationPlan.Obj(("href", new() { Nullable = true }), ("label", new())) } }],
                Root = new() { Tasks = [new() { Id = "items", Kind = "foreach", MaxItems = 10, MaxConcurrency = 1,
                    Objective = "Keep every requested occurrence, including missing links", Items = new() { Kind = "input", Source = "items" },
                    Body = variant == "assertion" ? visited : new()
                    {
                        Tasks = [new() { Id = "has_link", Kind = "conditional", Objective = "Visit an observed link or preserve an explicit missing-link row", Condition = PresentLink(), Body = visited,
                            Otherwise = new() { Outputs = [new("row", Array(Item("label"), new() { Kind = "null" }, new() { Kind = "null" }, Text("Missing link")))] } }],
                        Outputs = [new("row", Ref("has_link", "row"))]
                    } },
                    new() { Id = "write", Kind = "operation", Objective = "Write exact ordered rows", Operation = Operation("document_write"),
                        Inputs = [new("filePath", Text("products.xlsx")), new("rows", new() { Kind = "flatten", Items = [Array(
                            Array(Array(Text("name"), Text("description"), Text("price"), Text("error"))), Ref("items", "row"))] })] }],
                    Outputs = [new("success", Ref("write", "success")), new("path", Ref("write", "filePath"))],
                    Always = [new() { Id = "close", Kind = "operation", Objective = "Close on success or failure", Operation = Operation("close") }] }
            };
            var compiled = BusinessCompilation(plan, catalog, "compact-bindings-v15");
            Assert.DoesNotContain("mapping.dynamic", compiled.Yaml); Assert.DoesNotContain("llm.call", compiled.Yaml);
            var items = variant == "empty" ? new JsonArray() : new JsonArray(new JsonObject { ["href"] = null, ["label"] = "Missing product" },
                new JsonObject { ["href"] = repeated, ["label"] = "Offered first" }, new JsonObject { ["href"] = repeated, ["label"] = "Offered repeat" },
                new JsonObject { ["href"] = last, ["label"] = "Offered last" });
            var original = items.ToJsonString();
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(compiled.Yaml));
            var execution = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["items"] = items }, ct);
            Assert.Equal(original, items.ToJsonString()); Assert.Equal(0, inference.Calls);
            Assert.Equal("close", effects[^1]); Assert.Single(effects, e => e == "close");
            Assert.Equal(variant is "empty" or "assertion" ? [] : new[] { repeated, repeated, last }, visits);
            var path = Path.Combine(root, "products.xlsx");
            if (variant is "assertion" or "write_failed")
            {
                Assert.False(execution.Success); Assert.Null(execution.Outputs); Assert.False(File.Exists(path));
                return;
            }
            Assert.True(execution.Success, execution.Error?.Message); Assert.True(execution.Outputs!["success"]!.GetValue<bool>());
            Assert.Equal(path, execution.Outputs["path"]!.GetValue<string>());
            using var workbook = SpreadsheetDocument.Open(path, false);
            var sheet = Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet!;
            var rows = sheet.Descendants<Row>().Select(r => r.Elements<Cell>().Select(c => c.InnerText).ToArray()).ToArray();
            string[][] expected = variant == "empty" ? [["name", "description", "price", "error"]] :
                [["name", "description", "price", "error"], ["Missing product", "", "", "Missing link"],
                    ["=Exact\t— café 漢🙂", "Complete\nsecond line ${literal}", "", ""],
                    ["=Exact\t— café 漢🙂", "Complete\nsecond line ${literal}", "", ""], ["Last", "", "12,00 €", ""]];
            Assert.Equal(expected.Length, rows.Length);
            for (var i = 0; i < rows.Length; i++) Assert.Equal(expected[i], rows[i]);
            Assert.Empty(sheet.Descendants<CellFormula>());
            output.WriteLine($"Missing-link fixture: variant={variant}, visits={visits.Count}, rows={rows.Length}, inference={inference.Calls}, cleanup=1.");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ForbiddenRowInference : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("Typed row assembly must not dispatch inference."); }
    }
}
