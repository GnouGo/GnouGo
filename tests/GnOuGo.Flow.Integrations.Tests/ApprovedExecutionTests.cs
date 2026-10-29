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
    [InlineData("interactive")]
    [InlineData("auto")]
    public async Task PlanExecuteAndRestartUseStoredApprovalWithoutAnotherModelCall(string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "gnougo-approved-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var calls = 0; var model = new CurrentPlanningFixture.LiteralModel(onRequest: _ => calls++); var human = new Human();
            var factory = new WorkflowPlanningRuntimeFactory(new KeyVaultRecordStore(Path.Combine(directory, "vault.db")), Path.Combine(directory, "leases"));
            var engine = new WorkflowEngine { WorkflowPlanner = new HybridWorkflowPlanner(), PlanningRuntimeFactory = factory, LLMClient = model, HumanInputProvider = human, DefaultPlanningMode = mode,
                Limits = new() { RunId = "stable-run", TenantId = "one" } };
            var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
                version: 1
                workflows:
                  main:
                    steps:
                      - id: plan
                        type: workflow.plan
                        input:
                          raw_prompt: Return the required public numeric result named result with value 42.
                          generator: { model: test }
                      - id: execute
                        type: workflow.execute
                        input: { from_step: plan }
                    outputs:
                      result: { type: number, expr: '${data.steps.execute.outputs.result}' }
                """));
            var firstCalls = 0;
            for (var i = 0; i < 2; i++)
            {
                var result = await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
                Assert.True(result.Success, result.Error?.Message); Assert.Equal("42", result.Outputs!["result"]!.ToJsonString());
                if (i == 0) firstCalls = calls;
                else Assert.Equal(firstCalls, calls);
            }
            Assert.Equal(4, calls); Assert.Equal(1, human.Reviews);
            await Assert.ThrowsAsync<PlanningConflictException>(() => factory.ReadApprovedYamlAsync(new() { Engine = engine, Limits = new() { TenantId = "other" }, Step = new(), Data = new() }, "unknown", "forged", TestContext.Current.CancellationToken));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
    private sealed class Human : IHumanInputProvider
    {
        internal int Reviews;
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            Assert.StartsWith("review-", request.StepId); Assert.Equal(HumanInputContract.ModeForm, request.Mode); Reviews++;
            var response = new JsonObject { ["response"] = "approve" };
            foreach (var field in request.Fields!.Where(f => f.Name.StartsWith("requirement:", StringComparison.Ordinal)))
            { Assert.Null(field.Default); response[field.Name] = "covered"; }
            return Task.FromResult<JsonNode?>(response);
        }
    }
}
