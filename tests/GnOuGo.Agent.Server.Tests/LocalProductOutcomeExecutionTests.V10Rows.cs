using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    [Fact]
    public void V10RetainedConsumerRepairBypassedTheHeaderWithoutFixingItsProducer()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "V10Composition");
        var responses = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "responses.json")))!.AsArray();
        Assert.Equal(4, responses.Count);
        var edit = Assert.Single(responses[^1]!["response"]!["patch"]!["edits"]!.AsArray())!;
        var repaired = edit["value"]!.Deserialize(PlanningJsonContext.Default.TaskValue)!;
        var retained = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "retained.json")), PlanningJsonContext.Default.TaskPlan)!;
        var writer = retained.Root.Tasks.Single(t => t.Id == "save_products_xlsx");
        Assert.Equal(JsonSerializer.Serialize(repaired, PlanningJsonContext.Default.TaskValue),
            JsonSerializer.Serialize(writer.Inputs.Single(i => i.Name == "rows").Value, PlanningJsonContext.Default.TaskValue));
        Assert.Equal("scrape_products", repaired.Source); Assert.Equal("row", repaired.Port);
        var unused = retained.Root.Tasks.Single(t => t.Id == "build_xlsx_rows").Outputs.Single().Value;
        Assert.Equal("flatten", unused.Kind);
        Assert.All(unused.Items.Single().Items.Single().Items, v => Assert.Equal("string", v.Kind));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(1, false)]
    public async Task V10HeaderAndRowsUseNativeBindingsAndWriteExactCells(int count, bool header)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("gnougo-v10-rows-").FullName;
        try
        {
            await using var transport = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions> { ["document"] = new()
            {
                Type = "stdio", Command = Environment.GetEnvironmentVariable("GNOU_GO_DOCUMENT_MCP_TEST_EXECUTABLE") ??
                    Path.Combine(AppContext.BaseDirectory, "GnOuGo.Document.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : "")),
                EnvironmentVariables = new() { ["Document__DefaultWorkingDirectory"] = root, ["OpenTelemetry__Enabled"] = "false" }
            } });
            var engine = new WorkflowEngine { McpClientFactory = transport };
            var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
            var catalog = await RetainedCatalog(runtime, ct); catalog.Policy.RequireExternalConfirmation = false;
            var rows = new JsonArray();
            for (var i = 0; i < count; i++) rows.Add(new JsonArray("=Exact title " + i, "Complete block " + i + "\nnext\tline — café ${literal}", "12,00 €", (string?)null));
            var supplied = rows.ToJsonString();
            var input = new TaskValue { Kind = "input", Source = "records" };
            var binding = header ? V10Flatten(V10Array(V10Array(V10Array(new[] { "name", "description", "price", "error" }
                .Select(f => new TaskValue { Kind = "string", Text = f }).ToArray())), input)) : input;
            var plan = new TaskPlan { Inputs = [new() { Name = "records", Type = new() { Kind = "array", MinItems = 0, MaxItems = 10,
                Items = new() { Kind = "array", MinItems = 4, MaxItems = 4, Items = new() { Kind = "string", Nullable = true } } } }],
                Root = new() { Tasks = [new() { Id = "persist", Kind = "operation", Objective = "Write the supplied exact rows including their explicit header.",
                    Operation = TaskOperations.Describe(catalog.Capabilities.Single(c => c.Method == "document_write")).Id,
                    Inputs = [new("filePath", new() { Kind = "string", Text = "exact.xlsx" }), new("rows", binding)] }],
                    Outputs = [new("path", V10Output("persist", "filePath"))] } };
            var compiled = BusinessCompilation(plan, catalog, "compact-bindings-v10");
            Assert.DoesNotContain("llm.call", compiled.Yaml); Assert.DoesNotContain("mapping.dynamic", compiled.Yaml);
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(compiled.Yaml));
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["records"] = rows }, ct);
            Assert.True(result.Success, result.Error?.Message); Assert.Equal(supplied, rows.ToJsonString());
            var path = Path.Combine(root, "exact.xlsx"); Assert.Equal(path, result.Outputs!["path"]!.ToString());
            using var workbook = SpreadsheetDocument.Open(path, false);
            var sheet = Assert.Single(workbook.WorkbookPart!.WorksheetParts).Worksheet!;
            var written = sheet.Descendants<Row>().Select(row => row.Elements<Cell>().Select(cell => cell.InnerText).ToArray()).ToArray();
            Assert.Equal(count + (header ? 1 : 0), written.Length);
            if (header) Assert.Equal(new[] { "name", "description", "price", "error" }, written[0]);
            for (var i = 0; i < count; i++) Assert.Equal(rows[i]!.AsArray().Select(v => v?.GetValue<string>() ?? ""), written[i + (header ? 1 : 0)]);
            Assert.Empty(sheet.Descendants<CellFormula>());
            if (count == 1)
            {
                var events = ObservedProductOracleTests.Events("https://fixture.invalid/arbitrary?x=1", "https://fixture.invalid/arbitrary?x=1");
                var observed = events[1]["result"]!["observationSnapshot"]!["pages"]![0]!["records"]!.AsArray();
                for (var i = 0; i < 3; i++) observed[i]!["text"] = rows[0]![i]!.DeepClone();
                var oracle = LiveWorkflowOracles.VerifyObservedProducts(events, written, 10);
                Assert.Equal(header, oracle["passed"]!.GetValue<bool>());
                output.WriteLine($"GATE C independent oracle: header={header}; {oracle.ToJsonString()}");
            }
            output.WriteLine($"GATE C: products={count}; header={header}; realRows={written.Length}; exactCells=true; formulaCells=0; inference=0; steps={compiled.Steps}; sets={compiled.Sets}");
        }
        finally { Directory.Delete(root, true); }
    }
}
