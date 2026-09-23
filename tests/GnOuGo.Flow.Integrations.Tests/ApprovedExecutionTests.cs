using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Planning.Examples;
using Xunit;
namespace GnOuGo.Flow.Integrations.Tests;
public sealed class ApprovedExecutionTests
{
    [Theory]
    [InlineData(false, "interactive")]
    [InlineData(true, "interactive")]
    [InlineData(true, "auto")]
    public async Task PlanExecuteAndRestartUseStoredApprovalWithoutAnotherModelCall(bool businessDecision, string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "gnougo-approved-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var model = new Model { BusinessDecision = businessDecision }; var human = new Human(); var decisions = new Decisions();
            var factory = new WorkflowPlanningRuntimeFactory(new KeyVaultRecordStore(Path.Combine(directory, "vault.db")), Path.Combine(directory, "leases"));
            var engine = new WorkflowEngine { WorkflowPlanner = new TypedWorkflowPlanner(), PlanningRuntimeFactory = factory, LLMClient = model, HumanInputProvider = human, PlanningDecisionProvider = decisions, DefaultPlanningMode = mode,
                Limits = new() { RunId = "stable-run", TenantId = "one" } };
            var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
                version: 1
                workflows:
                  main:
                    steps:
                      - id: plan
                        type: workflow.plan
                        input:
                          raw_prompt: Return 42
                          generator: { model: test }
                      - id: execute
                        type: workflow.execute
                        input: { from_step: plan }
                    outputs:
                      result: { type: number, expr: '${data.steps.execute.outputs.result}' }
                """));
            for (var i = 0; i < 2; i++)
            {
                var result = await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
                Assert.True(result.Success, result.Error?.Message); Assert.Equal("42", result.Outputs!["result"]!.ToJsonString());
            }
            Assert.Equal(2, model.Calls); Assert.Equal(1, human.Reviews); Assert.Equal(businessDecision && mode == "interactive" ? 1 : 0, decisions.Questions);
            await Assert.ThrowsAsync<PlanningConflictException>(() => factory.ReadApprovedYamlAsync(new() { Engine = engine, Limits = new() { TenantId = "other" }, Step = new(), Data = new() }, "unknown", "forged", TestContext.Current.CancellationToken));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
    private sealed class Model : ILLMClient
    {
        internal int Calls;
        internal bool BusinessDecision;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            var semantic = (request.StructuredOutputSchema?["$defs"]?["decisionResult"] ?? request.StructuredOutputSchema)?["properties"]?["actions"] is not null;
            var response = PlanningCorpus.FixtureResponse(request, semantic ? "semantic" : "binding", PlanningCorpus.Intent("local", new()));
            if (BusinessDecision && semantic)
            {
                var result = response.Json!["result"]!;
                JsonObject Option(string id, bool preferred) { var candidate = result.DeepClone(); candidate["summary"] = id; candidate["actions"]![0]!["purpose"] = "Return 42 with " + id + " presentation"; return new() { ["id"] = id, ["label"] = id, ["reason"] = "Business preference", ["preferred"] = preferred, ["result"] = candidate }; }
                response.Json = new JsonObject { ["result"] = null, ["decision"] = new JsonObject { ["question"] = "Which presentation?", ["context"] = "Presentation preference", ["evidence"] = "Return 42", ["allowCustomAnswer"] = true,
                    ["options"] = new JsonArray(Option("brief", true), Option("detailed", false)) } };
            }
            response.Usage = new JsonObject { ["total_tokens"] = 15 }; return Task.FromResult(response);
        }

    }
    private sealed class Decisions : IPlanningDecisionProvider
    {
        internal int Questions;
        public Task<PlanningCommand> RequestAsync(PlanningSession session, CancellationToken ct)
        { Questions++; return Task.FromResult(new PlanningCommand { Kind = "answer_decision", ExpectedRevision = session.Revision, DecisionAnswer = new(session.PendingDecision!.Id, "brief") }); }
        public Task CheckpointedAsync(PlanningSession session, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Human : IHumanInputProvider
    {
        internal int Reviews;
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        { Assert.StartsWith("review-", request.StepId); Reviews++; return Task.FromResult<JsonNode?>(new JsonObject { ["response"] = "approve" }); }
    }
}
