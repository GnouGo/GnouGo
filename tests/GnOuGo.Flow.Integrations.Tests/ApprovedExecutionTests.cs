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
    [Fact]
    public async Task PlanExecuteAndRestartUseStoredApprovalWithoutAnotherModelCall()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gnougo-approved-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var model = new Model(); var human = new Human();
            var factory = new WorkflowPlanningRuntimeFactory(new KeyVaultRecordStore(Path.Combine(directory, "vault.db")), Path.Combine(directory, "leases"));
            var engine = new WorkflowEngine { WorkflowPlanner = new TypedWorkflowPlanner(), PlanningRuntimeFactory = factory, LLMClient = model, HumanInputProvider = human,
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
            Assert.Equal(2, model.Calls); Assert.Equal(1, human.Reviews);
            await Assert.ThrowsAsync<PlanningConflictException>(() => factory.ReadApprovedYamlAsync(new() { Engine = engine, Limits = new() { TenantId = "other" }, Step = new(), Data = new() }, "unknown", "forged", TestContext.Current.CancellationToken));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
    private sealed class Model : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; var response = PlanningCorpus.FixtureResponse(request, request.StructuredOutputSchema?["properties"]?["actions"] is not null ? "semantic" : "binding", PlanningCorpus.Intent("local", new())); response.Usage = new JsonObject { ["total_tokens"] = 15 }; return Task.FromResult(response); }
    }
    private sealed class Human : IHumanInputProvider
    {
        internal int Reviews;
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        { Reviews++; return Task.FromResult<JsonNode?>(new JsonObject { ["response"] = "approve" }); }
    }
}
