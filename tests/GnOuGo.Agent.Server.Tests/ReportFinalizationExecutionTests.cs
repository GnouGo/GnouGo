using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ReportFinalizationExecutionTests
{
    [Theory]
    [InlineData("alpha", "instruction", "success")]
    [InlineData("omega", "request", "verified_failure")]
    [InlineData("alpha", "request", "preserve_failure")]
    [InlineData("omega", "instruction", "unknown")]
    [InlineData("alpha", "instruction", "denied")]
    public async Task ExistingScopesPreserveObservedEvidenceBeforeRelease(string prefix, string field, string variant)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("report-finalization-").FullName;
        try
        {
            var scratch = Path.Combine(root, "scratch"); var report = Path.Combine(root, "report.txt");
            var calls = new List<string>(); var factory = new InMemoryMcpClientFactory();
            var empty = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!;
            var config = new MockMcpServerConfig();
            foreach (var operation in new[] { "prepare", "work", "preserve", "release" })
                config.Tools.Add(new() { Name = prefix + operation, InputSchema = operation == "work"
                    ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { [field] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray(field), ["additionalProperties"] = false }
                    : empty.DeepClone(), OutputSchema = empty.DeepClone() });
            McpCallResult Done() => new() { Content = new JsonObject() };
            config.ToolHandlers[prefix + "prepare"] = _ => { calls.Add("prepare"); Directory.CreateDirectory(scratch); return Done(); };
            config.ToolHandlers[prefix + "work"] = args =>
            {
                calls.Add("work"); Assert.Equal("actual caller value", args![field]!.ToString());
                File.WriteAllText(Path.Combine(scratch, "evidence.txt"), "observed: exact command exited 7");
                if (variant == "unknown") throw new WorkflowRuntimeException("RUN_NEEDS_RECONCILIATION", "Completion is not known.");
                return variant == "success" ? Done() : new() { IsError = true, Content = new JsonObject { ["error_code"] = "VERIFIED_FAILURE", ["message"] = "Command failed with observed exit 7" } };
            };
            config.ToolHandlers[prefix + "preserve"] = _ =>
            {
                calls.Add("preserve");
                if (variant == "preserve_failure") return new() { IsError = true, Content = new JsonObject { ["message"] = "Preservation denied" } };
                File.Copy(Path.Combine(scratch, "evidence.txt"), report); return Done();
            };
            config.ToolHandlers[prefix + "release"] = _ => { calls.Add("release"); Directory.Delete(scratch, true); return Done(); };
            factory.RegisterServer("arbitrary", config);
            var store = new InMemoryWorkflowRunStore();
            var engine = new WorkflowEngine { McpClientFactory = factory, RunStore = store,
                Limits = new() { TenantId = "fixture", RunId = prefix + variant }, HumanInputProvider = new PlanningCorpus.Human(variant != "denied") };
            var actual = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
            var catalog = await actual.DiscoverAsync(new(), ct);
            foreach (var source in await actual.Capabilities.ListSourcesAsync(ct))
                foreach (var entry in (await actual.Capabilities.ListAsync(source.Id, null, ct)).Capabilities)
                    catalog.Capabilities.Add(await actual.Capabilities.ResolveAsync(entry, ct));
            PlanTask Operation(string id) => new() { Id = id, Objective = id + " observed evidence", Operation = catalog.Capabilities.Single(c => c.Method == prefix + id).Id };
            var work = Operation("work"); work.DependsOn = ["prepare"];
            work.Inputs = [new(field, new() { Kind = "input", Source = "instruction" })];
            var plan = new TaskPlan { Inputs = [new() { Name = "instruction" }], Root = new() { Tasks = [Operation("prepare"), work],
                Always = [new() { Id = "finish", Kind = "sequence", Objective = "Preserve available evidence, then release temporary resources even if preservation fails",
                    Body = new() { Tasks = [Operation("preserve")], Always = [Operation("release")] } }] } };
            var proposal = new PlanningProposal { Plan = plan, Requirements = new() { Summary = "Preserve observed work evidence before cleanup, including verified failure",
                Inputs = plan.Inputs, Outputs = [], Outcomes = [new("evidence", "Preserve the observed report"), new("cleanup", "Release temporary resources")] } };
            var runtime = new LocalProductOutcomeExecutionTests.ProposalRuntime(actual, proposal);
            var session = await new HybridWorkflowPlanner().AdvanceAsync(new() { Catalog = catalog, Request = new() { TenantId = "fixture", Prompt = proposal.Requirements.Summary } }, new(), runtime, ct);
            Assert.True(session.Status == PlanningStatus.FinalReview, string.Join(';', session.Diagnostics.Select(d => d.Message)));
            Assert.Equal(1, session.ModelCalls); Assert.Equal(0, session.ReplanAttempts);
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(session.Yaml!));
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["instruction"] = "actual caller value" }, ct);
            Assert.Equal(variant == "success", result.Success);
            if (variant == "denied") { Assert.Empty(calls); Assert.False(Directory.Exists(scratch)); }
            else if (variant == "unknown")
            {
                Assert.Equal("RUN_NEEDS_RECONCILIATION", result.Error?.Code);
                Assert.Equal(new[] { "prepare", "work" }, calls); Assert.True(Directory.Exists(scratch));
            }
            else
            {
                Assert.Equal(new[] { "prepare", "work", "preserve", "release" }, calls); Assert.False(Directory.Exists(scratch));
                if (variant == "verified_failure") Assert.Equal("MCP_CALL_ERROR", result.Error?.Code);
            }
            Assert.Equal(variant is "success" or "verified_failure", File.Exists(report));
            if (File.Exists(report)) Assert.Equal("observed: exact command exited 7", await File.ReadAllTextAsync(report, ct));
        }
        finally { Directory.Delete(root, true); }
    }
}
