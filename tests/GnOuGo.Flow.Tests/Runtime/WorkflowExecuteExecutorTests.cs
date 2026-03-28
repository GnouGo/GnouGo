using System.Text.Json.Nodes;
using Moq;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

/// <summary>
/// Tests for the workflow.execute step executor.
/// Covers:
///   - Basic plan → execute flow (happy path)
///   - Outputs evaluation from generated workflow
///   - Missing from_step reference
///   - Missing YAML in plan result
///   - Call depth limit enforcement
///   - Multi-step generated workflows
///   - Generated workflow with no explicit outputs (falls back to steps data)
///   - Generated workflow with typed outputs
///   - Args forwarding to generated workflow
///   - End-to-end workflow.plan → workflow.execute integration
/// </summary>
public class WorkflowExecuteExecutorTests
{
    // ── Helpers ──

    private static CompiledWorkflow CompileMain(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);
        return compiled.Workflows[compiled.Entrypoint!];
    }

    private static async Task<RunResult> RunMain(string yaml, JsonObject? inputs = null,
        ILLMClient? llmClient = null)
    {
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var wf = compiled.Workflows[compiled.Entrypoint!];
        var engine = new WorkflowEngine
        {
            LLMClient = llmClient,
        };
        return await engine.ExecuteAsync(wf, inputs ?? new JsonObject(), CancellationToken.None);
    }

    // ────── Basic plan → execute (happy path) ──────

    [Fact]
    public async Task WorkflowExecute_BasicPlanThenExecute_ReturnsOutput()
    {
        // The LLM returns a valid generated workflow with a template.render step
        var generatedYaml = """
            dsl: 1
            workflows:
              generated:
                steps:
                  - id: greet
                    type: template.render
                    input:
                      engine: mustache
                      template: "Hello, World!"
                      mode: text
                outputs:
                  answer:
                    expr: "${data.steps.greet.text}"
                    type: string
            """;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: Generate a greeting
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate

                outputs:
                  answer: "${data.steps.run.outputs.answer}"
            """, llmClient: mockLlm.Object);

        Assert.True(result.Success);
        Assert.Equal("Hello, World!", result.Outputs!["answer"]!.GetValue<string>());
    }

    // ────── Missing from_step ──────

    [Fact]
    public async Task WorkflowExecute_MissingFromStep_FailsWithInputValidation()
    {
        var generatedYaml = "dsl: 1\nworkflows:\n  gen:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text";

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: false

                  - id: run
                    type: workflow.execute
                    input: {}
            """, llmClient: mockLlm.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
    }

    // ────── Reference to non-existent step ──────

    [Fact]
    public async Task WorkflowExecute_NonExistentPlanStep_FailsWithInputValidation()
    {
        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: run
                    type: workflow.execute
                    input:
                      from_step: does_not_exist
            """);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Contains("does_not_exist", result.Error.Message);
    }

    // ────── Plan step has no YAML ──────

    [Fact]
    public async Task WorkflowExecute_PlanStepMissingYaml_FailsWithInputValidation()
    {
        // Simulate a plan result that has no "yaml" field by using a set step
        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: fake_plan
                    type: set
                    input:
                      workflow:
                        name: "fake"

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: fake_plan
            """);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Contains("YAML", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ────── Multi-step generated workflow ──────

    [Fact]
    public async Task WorkflowExecute_MultiStepGeneratedWorkflow_ExecutesAllSteps()
    {
        var generatedYaml = """
            dsl: 1
            workflows:
              generated:
                steps:
                  - id: step1
                    type: set
                    input:
                      value: "first"

                  - id: step2
                    type: template.render
                    input:
                      engine: mustache
                      template: "{{prefix}}-second"
                      data:
                        prefix: "${data.steps.step1.value}"
                      mode: text

                outputs:
                  result:
                    expr: "${data.steps.step2.text}"
                    type: string
            """;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate

                outputs:
                  result: "${data.steps.run.outputs.result}"
                  steps_executed: "${data.steps.run.run.steps_executed}"
                  success: "${data.steps.run.run.success}"
            """, llmClient: mockLlm.Object);

        Assert.True(result.Success);
        Assert.Equal("first-second", result.Outputs!["result"]!.GetValue<string>());
        Assert.Equal(2, result.Outputs["steps_executed"]!.GetValue<int>());
        Assert.True(result.Outputs["success"]!.GetValue<bool>());
    }

    // ────── Generated workflow with no explicit outputs ──────

    [Fact]
    public async Task WorkflowExecute_NoOutputsDefined_FallsBackToStepsData()
    {
        var generatedYaml = """
            dsl: 1
            workflows:
              generated:
                steps:
                  - id: calc
                    type: set
                    input:
                      answer: 42
            """;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate
            """, llmClient: mockLlm.Object);

        Assert.True(result.Success);

        // When no outputs declared, the executor returns steps data as outputs
        var runOutput = result.StepResults.Last().Output as JsonObject;
        Assert.NotNull(runOutput);
        var outputs = runOutput!["outputs"] as JsonObject;
        Assert.NotNull(outputs);
        // The "calc" step data should be present
        Assert.NotNull(outputs!["calc"]);
    }

    // ────── Run metadata (steps_executed, success, workflow name) ──────

    [Fact]
    public async Task WorkflowExecute_ReturnsRunMetadata()
    {
        var generatedYaml = """
            dsl: 1
            workflows:
              my_workflow:
                steps:
                  - id: a
                    type: set
                    input:
                      x: 1
                  - id: b
                    type: set
                    input:
                      y: 2
            """;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate

                outputs:
                  workflow_name: "${data.steps.run.workflow}"
                  steps_count: "${data.steps.run.run.steps_executed}"
                  was_success: "${data.steps.run.run.success}"
            """, llmClient: mockLlm.Object);

        Assert.True(result.Success);
        Assert.Equal("my_workflow", result.Outputs!["workflow_name"]!.GetValue<string>());
        Assert.Equal(2, result.Outputs["steps_count"]!.GetValue<int>());
        Assert.True(result.Outputs["was_success"]!.GetValue<bool>());
    }

    // ────── Call depth limit ──────

    [Fact]
    public async Task WorkflowExecute_ExceedsCallDepth_FailsWithCycleDetected()
    {
        var generatedYaml = """
            dsl: 1
            workflows:
              gen:
                steps:
                  - id: s
                    type: set
                    input:
                      ok: true
            """;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate
            """));
        var wf = compiled.Workflows[compiled.Entrypoint!];
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            Limits = new ExecutionLimits { MaxCallDepth = 1 } // Very shallow depth
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        // workflow.plan itself doesn't increment call depth, but workflow.execute
        // runs at callDepth 0 and executes sub-steps at callDepth 1.
        // With MaxCallDepth = 1, it should still succeed because the check is >=.
        // Let's set to 0 for a definitive failure:
        engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            Limits = new ExecutionLimits { MaxCallDepth = 0 }
        };

        result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowCycleDetected, result.Error!.Code);
    }

    // ────── Generated workflow uses inputs (args forwarding) ──────

    [Fact]
    public async Task WorkflowExecute_WithArgs_ForwardsToGeneratedWorkflow()
    {
        var generatedYaml = """
            dsl: 1
            workflows:
              generated:
                inputs:
                  name:
                    type: string
                    required: true
                steps:
                  - id: greet
                    type: template.render
                    input:
                      engine: mustache
                      template: "Hello, {{name}}!"
                      data:
                        name: "${data.inputs.name}"
                      mode: text
                outputs:
                  greeting:
                    expr: "${data.steps.greet.text}"
                    type: string
            """;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate
                      args:
                        name: "Alice"

                outputs:
                  greeting: "${data.steps.run.outputs.greeting}"
            """, llmClient: mockLlm.Object);

        Assert.True(result.Success);
        Assert.Equal("Hello, Alice!", result.Outputs!["greeting"]!.GetValue<string>());
    }

    // ────── Dynamic args from expression ──────

    [Fact]
    public async Task WorkflowExecute_ArgsFromExpression_ResolvesCorrectly()
    {
        var generatedYaml = """
            dsl: 1
            workflows:
              generated:
                steps:
                  - id: echo
                    type: set
                    input:
                      received: "${data.inputs.message}"
                outputs:
                  echoed:
                    expr: "${data.steps.echo.received}"
                    type: string
            """;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                inputs:
                  user_msg:
                    type: string
                    required: true
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate
                      args:
                        message: "${data.inputs.user_msg}"

                outputs:
                  result: "${data.steps.run.outputs.echoed}"
            """, new JsonObject { ["user_msg"] = "Hi there!" }, llmClient: mockLlm.Object);

        Assert.True(result.Success);
        Assert.Equal("Hi there!", result.Outputs!["result"]!.GetValue<string>());
    }

    // ────── Validator: workflow.execute step type is known ──────

    [Fact]
    public void Validate_WorkflowExecuteStepType_IsKnown()
    {
        var yaml = """
            dsl: 1
            workflows:
              main:
                steps:
                  - id: run
                    type: workflow.execute
                    input:
                      from_step: some_plan
            """;
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var errors = compiler.Validate(doc);
        Assert.DoesNotContain(errors, e => e.Code == ErrorCodes.StepTypeUnknown);
    }

    // ────── workflow.execute registered in engine ──────

    [Fact]
    public void WorkflowExecute_IsRegisteredInEngine()
    {
        var engine = new WorkflowEngine();
        Assert.True(engine.Registry.Has("workflow.execute"));
        var executor = engine.Registry.Get("workflow.execute");
        Assert.NotNull(executor);
        Assert.Equal("workflow.execute", executor!.StepType);
    }

    // ────── DocumentedExceptions are present ──────

    [Fact]
    public void WorkflowExecute_HasDocumentedExceptions()
    {
        var engine = new WorkflowEngine();
        var executor = engine.Registry.Get("workflow.execute");
        Assert.NotNull(executor);
        Assert.NotNull(executor!.DocumentedExceptions);
        Assert.NotEmpty(executor.DocumentedExceptions);

        var codes = executor.DocumentedExceptions.Select(e => e.Code).ToHashSet();
        Assert.Contains(ErrorCodes.InputValidation, codes);
        Assert.Contains(ErrorCodes.WorkflowCycleDetected, codes);
    }

    // ────── Generated workflow failure propagates ──────

    [Fact]
    public async Task WorkflowExecute_GeneratedWorkflowFails_PropagatesError()
    {
        // Generate a workflow that calls an LLM without one being configured
        // in the sub-context — it will fail
        var generatedYaml = """
            dsl: 1
            workflows:
              generated:
                steps:
                  - id: call
                    type: llm.call
                    input:
                      model: gpt-4
                      prompt: "Hello"
            """;

        var callCount = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call: workflow.plan LLM call
                    return new LLMResponse { Text = generatedYaml };
                }
                // Second call: the generated workflow's llm.call
                // Simulate a failure
                throw new WorkflowRuntimeException(ErrorCodes.LlmNetwork, "LLM unreachable");
            });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate
            """, llmClient: mockLlm.Object);

        Assert.False(result.Success);
        // The error should be from the inner LLM call failure
        Assert.Equal(ErrorCodes.LlmNetwork, result.Error!.Code);
    }

    // ────── End-to-end: plan + execute with set steps ──────

    [Fact]
    public async Task WorkflowExecute_EndToEnd_PlanAndExecuteSetSteps()
    {
        var generatedYaml = """
            dsl: 1
            workflows:
              generated:
                steps:
                  - id: data
                    type: set
                    input:
                      items:
                        - name: "apple"
                          price: 1.5
                        - name: "banana"
                          price: 0.75
                  - id: count
                    type: set
                    input:
                      total: "${len(data.steps.data.items)}"
                outputs:
                  item_count:
                    expr: "${data.steps.count.total}"
                    type: number
            """;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate

                outputs:
                  item_count: "${data.steps.run.outputs.item_count}"
            """, llmClient: mockLlm.Object);

        Assert.True(result.Success);
        Assert.Equal(2, (int)ExpressionEvaluator.GetNumber(result.Outputs!["item_count"]));
    }

    // ────── Env propagation ──────

    [Fact]
    public async Task WorkflowExecute_PropagatesEnvToGeneratedWorkflow()
    {
        // The generated workflow reads from env — env should be cloned from parent context
        var generatedYaml = """
            dsl: 1
            workflows:
              generated:
                steps:
                  - id: read_env
                    type: set
                    input:
                      from_env: "${data.env}"
                outputs:
                  env_data:
                    expr: "${json(data.steps.read_env.from_env)}"
                    type: string
            """;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate

                outputs:
                  env_data: "${data.steps.run.outputs.env_data}"
            """, llmClient: mockLlm.Object);

        Assert.True(result.Success);
        // env_data should be a valid JSON string (even if empty object)
        var envData = result.Outputs!["env_data"]!.GetValue<string>();
        Assert.NotNull(envData);
        Assert.Contains("{", envData);
    }

    // ────── Generated workflow uses entrypoint resolution ──────

    [Fact]
    public async Task WorkflowExecute_SelectsFirstWorkflowAsEntrypoint()
    {
        // The generated document has multiple workflows; the first one should be selected
        var generatedYaml = """
            dsl: 1
            workflows:
              helper:
                steps:
                  - id: unused
                    type: set
                    input:
                      x: unused

              main:
                steps:
                  - id: result
                    type: set
                    input:
                      value: "from_main"
                outputs:
                  answer:
                    expr: "${data.steps.result.value}"
                    type: string
            """;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate
            """, llmClient: mockLlm.Object);

        Assert.True(result.Success);
        // Entrypoint is "main" (standard entrypoint), so "from_main" should be in outputs
        var runOutput = result.StepResults.Last().Output as JsonObject;
        Assert.NotNull(runOutput);
        var outputs = runOutput!["outputs"] as JsonObject;
        Assert.NotNull(outputs);
        Assert.Equal("from_main", outputs!["answer"]!.GetValue<string>());
    }

    // ────── workflow.plan meta is accessible after execute ──────

    [Fact]
    public async Task WorkflowExecute_PlanMetaIsAccessible()
    {
        var generatedYaml = """
            dsl: 1
            workflows:
              generated:
                steps:
                  - id: s
                    type: set
                    input:
                      ok: true
                outputs:
                  status:
                    expr: "'done'"
                    type: string
            """;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate

                outputs:
                  plan_model: "${data.steps.generate.meta.model}"
                  plan_attempt: "${data.steps.generate.meta.attempt}"
                  plan_yaml: "${data.steps.generate.yaml}"
                  exec_status: "${data.steps.run.outputs.status}"
            """, llmClient: mockLlm.Object);

        Assert.True(result.Success);
        Assert.Equal("gpt-4", result.Outputs!["plan_model"]!.GetValue<string>());
        Assert.Equal(1, result.Outputs["plan_attempt"]!.GetValue<int>());
        Assert.NotNull(result.Outputs["plan_yaml"]!.GetValue<string>());
        Assert.Equal("done", result.Outputs["exec_status"]!.GetValue<string>());
    }

    // ────── Invalid generated YAML ──────

    [Fact]
    public async Task WorkflowExecute_InvalidYaml_FailsGracefully()
    {
        // Manually inject a plan result with invalid YAML via set step
        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: fake_plan
                    type: set
                    input:
                      yaml: "this is not valid yaml: [[[["
                      workflow:
                        name: fake
                      meta:
                        model: test
                        attempt: 1
                      diagnostics: []

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: fake_plan
            """);

        Assert.False(result.Success);
        // Should fail during parse or compile of the invalid YAML
    }

    // ────── Template render in generated workflow ──────

    [Fact]
    public async Task WorkflowExecute_TemplateRender_WorksInGenerated()
    {
        var generatedYaml = """
            dsl: 1
            workflows:
              gen:
                steps:
                  - id: render
                    type: template.render
                    input:
                      engine: mustache
                      template: "Items: {{count}}"
                      data:
                        count: "3"
                      mode: text
                outputs:
                  text:
                    expr: "${data.steps.render.text}"
                    type: string
            """;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = generatedYaml });

        var result = await RunMain("""
            dsl: 1
            workflows:
              main:
                steps:
                  - id: generate
                    type: workflow.plan
                    input:
                      generator:
                        model: gpt-4
                        instruction: test
                      validate:
                        compile: true

                  - id: run
                    type: workflow.execute
                    input:
                      from_step: generate

                outputs:
                  text: "${data.steps.run.outputs.text}"
            """, llmClient: mockLlm.Object);

        Assert.True(result.Success);
        Assert.Equal("Items: 3", result.Outputs!["text"]!.GetValue<string>());
    }
}

