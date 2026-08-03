using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowFinalizationTests
{
    [Fact]
    public async Task Finally_RunsAfterSuccessAndCanReadPriorOutputs()
    {
        var result = await ExecuteAsync("""
            version: 1
            workflows:
              main:
                inputs:
                  resource: string
                steps:
                  - id: allocate
                    type: set
                    input:
                      value: "${data.inputs.resource}"
                finally:
                  - id: cleanup
                    type: set
                    input:
                      value: "${data.steps.allocate.value}"
                      had_error: "${data.workflow_error != null}"
                outputs:
                  cleaned: "${data.steps.cleanup.value}"
                  had_error: "${data.steps.cleanup.had_error}"
            """, new JsonObject { ["resource"] = "lease-1" }, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("lease-1", result.Outputs!["cleaned"]!.GetValue<string>());
        Assert.False(result.Outputs["had_error"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Finally_RunsAfterFailureAndReceivesPrimaryError()
    {
        var result = await ExecuteAsync("""
            version: 1
            workflows:
              main:
                steps:
                  - id: fail
                    type: assert.non_null
                    input:
                      value: null
                finally:
                  - id: cleanup
                    type: set
                    input:
                      error_code: "${data.workflow_error.code}"
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        var cleanup = Assert.Single(result.StepResults, step => step.StepId == "cleanup");
        Assert.Equal(ErrorCodes.InputValidation, cleanup.Output!["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Finally_RunsWithIndependentTokenAfterCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await ExecuteAsync("""
            version: 1
            workflows:
              main:
                steps:
                  - id: skipped
                    type: set
                    input:
                      value: main
                finally:
                  - id: cleanup
                    type: set
                    input:
                      error_code: "${data.workflow_error.code}"
            """, cancellationToken: cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal("CANCELLED", result.Error!.Code);
        Assert.Contains(result.StepResults, step => step.StepId == "cleanup" && step.Status == StepStatus.Succeeded);
    }

    [Fact]
    public async Task Finally_HasSeparateStepBudget()
    {
        var (engine, workflow) = CreateEngine("""
            version: 1
            workflows:
              main:
                steps:
                  - id: main_step
                    type: set
                    input:
                      value: main
                finally:
                  - id: cleanup
                    type: set
                    input:
                      value: cleaned
            """);
        engine.Limits.MaxTotalStepsExecuted = 1;
        engine.Limits.MaxFinalizationSteps = 1;

        var result = await engine.ExecuteAsync(workflow, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(result.StepResults, step => step.StepId == "cleanup" && step.Status == StepStatus.Succeeded);
    }

    [Fact]
    public async Task Finally_StepBudgetIncludesNestedWorkflowAndItsFinalizer()
    {
        var (engine, workflow) = CreateEngine("""
            version: 1
            workflows:
              main:
                steps:
                  - id: main_step
                    type: set
                    input:
                      value: main
                finally:
                  - id: cleanup_call
                    type: workflow.call
                    input:
                      ref:
                        kind: local
                        name: cleanup
              cleanup:
                steps:
                  - id: cleanup_body
                    type: set
                    input:
                      value: cleaned
                finally:
                  - id: cleanup_tail
                    type: set
                    input:
                      value: finalized
            """);
        engine.Limits.MaxFinalizationSteps = 2;

        var result = await engine.ExecuteAsync(workflow, new JsonObject(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowFinalizationFailed, result.Error!.Code);
    }

    [Fact]
    public async Task Finally_FailurePreservesPrimaryFailureAndAddsDetails()
    {
        var result = await ExecuteAsync("""
            version: 1
            workflows:
              main:
                steps:
                  - id: primary_failure
                    type: assert.non_null
                    input:
                      value: null
                finally:
                  - id: cleanup_failure
                    type: assert.non_null
                    input:
                      value: null
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        var errors = Assert.IsType<JsonArray>(result.Error.Details!["finalization_errors"]);
        Assert.Single(errors);
        Assert.Equal(ErrorCodes.InputValidation, errors[0]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Finally_TimeoutFailsOtherwiseSuccessfulRun()
    {
        var (engine, workflow) = CreateEngine("""
            version: 1
            workflows:
              main:
                steps:
                  - id: main_step
                    type: set
                    input:
                      value: main
                finally:
                  - id: wait_forever
                    type: test.wait
            """);
        engine.Registry.Register(new WaitExecutor());
        engine.Limits.FinalizationTimeoutSeconds = 1;

        var result = await engine.ExecuteAsync(workflow, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowFinalizationFailed, result.Error!.Code);
        var errors = Assert.IsType<JsonArray>(result.Error.Details!["finalization_errors"]);
        Assert.Equal(ErrorCodes.WorkflowFinalizationTimeout, errors[0]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Finally_RunsForNestedWorkflowCallBeforeChildOutputsAreReturned()
    {
        var result = await ExecuteAsync("""
            version: 1
            workflows:
              main:
                steps:
                  - id: call_child
                    type: workflow.call
                    input:
                      ref:
                        kind: local
                        name: child
                outputs:
                  cleaned: "${data.steps.call_child.outputs.cleaned}"
              child:
                steps:
                  - id: allocate
                    type: set
                    input:
                      resource: lease-2
                finally:
                  - id: cleanup
                    type: set
                    input:
                      resource: "${data.steps.allocate.resource}"
                outputs:
                  cleaned: "${data.steps.cleanup.resource}"
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("lease-2", result.Outputs!["cleaned"]!.GetValue<string>());
    }

    [Fact]
    public async Task Finally_IsPersistedWhenCheckpointedWorkflowResumes()
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - id: allocate
                    type: set
                    input:
                      resource: lease-3
                  - id: use
                    type: set
                    input:
                      resource: "${data.steps.allocate.resource}"
                finally:
                  - id: cleanup
                    type: set
                    input:
                      resource: "${data.steps.allocate.resource}"
            """;
        var (engine, workflow) = CreateEngine(yaml);
        var checkpointer = new InMemoryWorkflowCheckpointer();
        await checkpointer.SaveAsync(new WorkflowCheckpoint
        {
            RunId = "finalization-resume",
            WorkflowName = "main",
            WorkflowYaml = yaml,
            NextStepIndex = 1,
            StepOutputs = new JsonObject
            {
                ["allocate"] = new JsonObject { ["resource"] = "lease-3" }
            },
            Inputs = new JsonObject(),
            Status = "paused"
        }, CancellationToken.None);
        engine.Checkpointer = checkpointer;

        var result = await engine.ResumeAsync("finalization-resume", workflow, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var checkpoint = await checkpointer.LoadAsync("finalization-resume", CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal("completed", checkpoint.Status);
        Assert.Equal(2, checkpoint.NextStepIndex);
        Assert.Equal("lease-3", checkpoint.StepOutputs["cleanup"]!["resource"]!.GetValue<string>());
    }

    private static async Task<RunResult> ExecuteAsync(
        string yaml,
        JsonNode? inputs = null,
        CancellationToken cancellationToken = default)
    {
        var (engine, workflow) = CreateEngine(yaml);
        return await engine.ExecuteAsync(workflow, inputs ?? new JsonObject(), cancellationToken);
    }

    private static (WorkflowEngine Engine, CompiledWorkflow Workflow) CreateEngine(string yaml)
    {
        var document = WorkflowParser.Parse(yaml);
        var compiled = new WorkflowCompiler().Compile(document);
        var engine = new WorkflowEngine();
        return (engine, compiled.Workflows[compiled.Entrypoint!]);
    }

    private sealed class WaitExecutor : IStepExecutor
    {
        public string StepType => "test.wait";

        public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }
    }
}
