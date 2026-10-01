using System.Text.Json;
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

public sealed class PlanningClarificationExecutionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealWorkflowPausesAnswersReviewsExecutesAndRestarts(bool interaction, bool custom)
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-clarification-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new Model(); var human = new Human(custom); var decisions = new Decisions(custom);
            var engine = new WorkflowEngine { WorkflowPlanner = new HybridWorkflowPlanner(), LLMClient = model,
                PlanningRuntimeFactory = new WorkflowPlanningRuntimeFactory(new KeyVaultRecordStore(Path.Combine(root, "vault.db")), Path.Combine(root, "leases")),
                HumanInputProvider = human, PlanningInteraction = interaction ? decisions : null, DefaultPlanningMode = "auto",
                Limits = new() { RunId = "stable", TenantId = "one" } };
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
                version: 1
                workflows:
                  main:
                    steps:
                      - id: plan
                        type: workflow.plan
                        input:
                          raw_prompt: Return a value using the requested presentation
                          generator: { model: deterministic }
                      - id: execute
                        type: workflow.execute
                        input: { from_step: plan }
                    outputs:
                      result: { type: number, expr: '${data.steps.execute.outputs.result}' }
                """));
            for (var i = 0; i < 2; i++)
            {
                var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
                Assert.True(result.Success, result.Error?.Message); Assert.Equal(42, result.Outputs!["result"]!.GetValue<int>());
            }
            Assert.Equal(2, model.Calls); Assert.Equal(1, human.Reviews);
            Assert.Equal(interaction ? 1 : 0, decisions.Questions); Assert.Equal(interaction ? 0 : 1, human.Questions);
            Assert.Contains(custom ? "CUSTOM_INTENT" : "compact", model.Requests[1].Prompt);
            if (interaction) Assert.True(decisions.AnswerCheckpointed);
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain("CUSTOM_INTENT", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken)));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HostWithoutHumanProviderReturnsWaitingSessionWithoutExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-waiting-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new Model(); var engine = new WorkflowEngine { WorkflowPlanner = new HybridWorkflowPlanner(), LLMClient = model,
                PlanningRuntimeFactory = new WorkflowPlanningRuntimeFactory(new KeyVaultRecordStore(Path.Combine(root, "vault.db")), Path.Combine(root, "leases")),
                Limits = new() { RunId = "waiting", TenantId = "one" } };
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
                version: 1
                workflows:
                  main:
                    steps:
                      - id: plan
                        type: workflow.plan
                        input:
                          raw_prompt: Return a value
                          generator: { model: deterministic }
                    outputs:
                      status: { type: string, expr: '${data.steps.plan.status}' }
                      session: { type: string, expr: '${data.steps.plan.session_id}' }
                """));
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Error?.Message); Assert.Equal("clarification", result.Outputs!["status"]!.GetValue<string>());
            Assert.False(string.IsNullOrEmpty(result.Outputs["session"]!.GetValue<string>())); Assert.Equal(1, model.Calls);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class Model : ILLMClient
    {
        internal int Calls;
        internal readonly List<LLMRequest> Requests = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++; Requests.Add(request);
            var proposal = Calls == 1 ? new PlanningProposal { Clarifications = [new("presentation", "Which presentation?",
                [new("compact", "Compact numeric result"), new("detailed", "Detailed numeric result")], "compact")] }
                : new PlanningProposal { Requirements = PlanningCorpus.Requirements("local"), Plan = PlanningCorpus.LiteralResult() };
            return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal),
                request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()), Usage = new JsonObject { ["total_tokens"] = 15 } });
        }
    }
    private sealed class Human(bool custom) : IHumanInputProvider
    {
        internal int Questions, Reviews;
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            if (request.Mode == HumanInputContract.ModeForm)
            {
                Questions++; var field = Assert.Single(request.Fields!); Assert.True(field.AllowCustomAnswer);
                Assert.Equal("compact", field.Default); Assert.Equal("compact", Assert.Single(field.OptionDefinitions!, o => o.Recommended).Value);
                return Task.FromResult<JsonNode?>(new JsonObject { ["presentation"] = custom ? "CUSTOM_INTENT: a number" : "compact" });
            }
            Assert.StartsWith("review-", request.StepId); Reviews++;
            return Task.FromResult<JsonNode?>(new JsonObject { ["response"] = "approve" });
        }
    }
    private sealed class Decisions(bool custom) : IPlanningInteraction
    {
        internal int Questions; internal bool AnswerCheckpointed;
        public Task<PlanningCommand> RequestAsync(PlanningSession state, CancellationToken ct)
        {
            Questions++; Assert.Null(state.Plan); Assert.Single(state.PendingQuestions!); Assert.Null(state.ApprovedHash);
            return Task.FromResult(new PlanningCommand { Kind = "answer", ExpectedRevision = state.Revision,
                Answers = [custom ? new("presentation", Text: "CUSTOM_INTENT: a number") : new("presentation", "compact")] });
        }
        public Task CheckpointedAsync(PlanningSession state, CancellationToken ct)
        {
            if (state.AnswerHistory is not null && state.ModelCalls == 1)
            { Assert.Equal(PlanningStatus.Generating, state.Status); Assert.Null(state.ApprovedHash); AnswerCheckpointed = true; }
            return Task.CompletedTask;
        }
    }
}
