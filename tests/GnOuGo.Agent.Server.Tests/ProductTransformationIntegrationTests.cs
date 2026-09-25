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

public sealed class ProductTransformationIntegrationTests
{
    [Theory]
    [InlineData("nominal")]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("outside")]
    public async Task TypedTransformPipelineWritesRealWorkbookWithinDocumentPolicy(string variant)
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-transform-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = new DocumentPolicy(new DocumentServerSettings { DefaultWorkingDirectory = root }, root);
            var writer = new DocumentOperationHost(policy, NullLogger<DocumentOperationHost>.Instance);
            var fixture = new ProductTransformationFixture(variant);
            fixture.Write = (path, content) =>
            {
                var written = writer.Write(variant == "outside" ? "../outside.xlsx" : path, content, null);
                Assert.True(written.Success, written.ErrorMessage);
                return new() { ["path"] = path };
            };
            var engine = new WorkflowEngine { McpClientFactory = fixture.Factory(), LLMClient = fixture,
                LlmDefaults = new() { Model = "scripted" }, HumanInputProvider = new PlanningCorpus.Human() };
            var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
            var catalog = await runtime.DiscoverAsync(new(), TestContext.Current.CancellationToken);
            foreach (var source in await runtime.Capabilities.ListSourcesAsync(TestContext.Current.CancellationToken))
            {
                var page = await runtime.Capabilities.ListAsync(source.Id, null, TestContext.Current.CancellationToken);
                foreach (var summary in page.Capabilities) catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(summary, TestContext.Current.CancellationToken));
            }
            var planning = new ScriptedPlanner(runtime, ProductTransformationPlan.Create(catalog, fixture));
            var session = new PlanningSession { Catalog = catalog, Request = new() { TenantId = "test", Prompt = "Search products and save an XLSX file" } };
            session = await new HybridWorkflowPlanner().AdvanceAsync(session, new(), planning, TestContext.Current.CancellationToken);
            Assert.Equal(PlanningStatus.FinalReview, session.Status);
            PlanningArtifactApproval.Verify(session);
            var yaml = session.Yaml!;
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["search"] = ProductTransformationFixture.SearchUrl }, TestContext.Current.CancellationToken);
            Assert.Equal("close", fixture.Effects[^1]);
            var output = policy.ResolveFilePath(ProductTransformationFixture.OutputPath);
            if (variant == "outside") { Assert.False(result.Success); Assert.False(File.Exists(output)); return; }
            Assert.True(result.Success, result.Error?.Message);
            Assert.Equal(ProductTransformationFixture.OutputPath, result.Outputs!["file"]!.ToString());
            Assert.True(File.Exists(output));
            using var workbook = SpreadsheetDocument.Open(output, false);
            var sheets = workbook.WorkbookPart!.WorksheetParts.ToArray(); Assert.Single(sheets);
            var rows = sheets[0].Worksheet!.Descendants<Row>().Select(r => r.Elements<Cell>().Select(c => c.InnerText).ToArray()).ToArray();
            Assert.Equal(fixture.ExpectedRows.Length, rows.Length);
            for (var i = 0; i < rows.Length; i++) Assert.Equal(fixture.ExpectedRows[i], rows[i]);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ScriptedPlanner(WorkflowPlanningRuntime actual, TaskPlan plan) : IPlanningRuntime
    {
        public ICapabilityCatalog Capabilities => actual.Capabilities;
        public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => actual.DiscoverAsync(request, ct);
        public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
        {
            var proposal = new PlanningProposal { Requirements = new() { Summary = "Save product details", Outcomes = [new("xlsx", "Save accurate product rows in XLSX")] }, Plan = plan };
            return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal), request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) });
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => actual.ValidateCatalogAsync(catalog, ct);
        public Task CheckpointAsync(PlanningSession session, CancellationToken ct) => Task.CompletedTask;
    }
}
