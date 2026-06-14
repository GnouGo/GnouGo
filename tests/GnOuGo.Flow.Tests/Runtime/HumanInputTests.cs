using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

/// <summary>
/// Tests for human-in-the-loop features:
///   Level 1: human.input step type
///   Level 2: Checkpoint / resume
/// </summary>
public class HumanInputTests
{
    // -- Helpers --

    private static CompiledWorkflow CompileMain(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);
        return compiled.Workflows[compiled.Entrypoint!];
    }

    /// <summary>
    /// A test IHumanInputProvider that immediately returns a pre-configured response.
    /// </summary>
    private sealed class FakeHumanInputProvider : IHumanInputProvider
    {
        private readonly JsonNode? _response;
        public HumanInputRequest? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        public FakeHumanInputProvider(JsonNode? response) => _response = response;

        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            LastRequest = request;
            CallCount++;
            return Task.FromResult(_response?.DeepClone());
        }
    }


    // ------------------------------------------------------------------
    // Level 1: human.input step
    // ------------------------------------------------------------------

    [Fact]
    public async Task HumanInput_BasicPrompt_ReturnsUserResponse()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: ask
        type: human.input
        input:
          mode: choice
          prompt: Do you approve?
          choices:
            - approve
            - reject
");

        var fakeProvider = new FakeHumanInputProvider(new JsonObject { ["response"] = "approve" });
        var engine = new WorkflowEngine
        {
            HumanInputProvider = fakeProvider,
            Limits = new ExecutionLimits { RunId = "test-run-1" }
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, fakeProvider.CallCount);
        Assert.Equal("Do you approve?", fakeProvider.LastRequest!.Prompt);
        Assert.Equal(HumanInputContract.ModeChoice, fakeProvider.LastRequest.Mode);
        Assert.Equal("test-run-1", fakeProvider.LastRequest.RunId);
        Assert.Equal(HumanInputContract.DefaultTimeoutMs, fakeProvider.LastRequest.TimeoutMs);
        Assert.Contains("approve", fakeProvider.LastRequest.Choices!);
        Assert.Contains("reject", fakeProvider.LastRequest.Choices!);

        var output = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(output);
        Assert.Equal("approve", output!["response"]!.GetValue<string>());
    }

    [Fact]
    public async Task HumanInput_WithFields_ParsesFieldDefs()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: form
        type: human.input
        input:
          mode: form
          prompt: Please fill in your details
          fields:
            - name: email
              type: string
              required: true
              description: Your email
            - name: priority
              type: select
              options: [low, medium, high]
              default: medium
");

        var fakeProvider = new FakeHumanInputProvider(new JsonObject
        {
            ["email"] = "user@example.com",
            ["priority"] = "high"
        });
        var engine = new WorkflowEngine
        {
            HumanInputProvider = fakeProvider,
            Limits = new ExecutionLimits { RunId = "test-run-2" }
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(fakeProvider.LastRequest!.Fields);
        Assert.Equal(HumanInputContract.ModeForm, fakeProvider.LastRequest.Mode);
        Assert.Equal(2, fakeProvider.LastRequest.Fields!.Count);
        Assert.Equal("email", fakeProvider.LastRequest.Fields[0].Name);
        Assert.True(fakeProvider.LastRequest.Fields[0].Required);
        Assert.Equal("select", fakeProvider.LastRequest.Fields[1].Type);
        Assert.Contains("high", fakeProvider.LastRequest.Fields[1].Options!);
    }

    [Fact]
    public async Task HumanInput_WithScalarFieldMetadata_NormalizesForProvider()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: form
        type: human.input
        input:
          mode: form
          prompt: Numeric select
          fields:
            - name: sets_homme
              type: select
              required: ""true""
              options: [3, 4, 5, 6]
              default: 5
            - name: optional_note
              type: string
              required: ""false""
              description: 123
");

        var fakeProvider = new FakeHumanInputProvider(new JsonObject
        {
            ["sets_homme"] = "5",
            ["optional_note"] = "ok"
        });
        var engine = new WorkflowEngine
        {
            HumanInputProvider = fakeProvider,
            Limits = new ExecutionLimits { RunId = "test-run-scalar-metadata" }
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        var fields = fakeProvider.LastRequest!.Fields!;
        Assert.Equal(["3", "4", "5", "6"], fields[0].Options);
        Assert.Equal("5", fields[0].Default);
        Assert.True(fields[0].Required);
        Assert.False(fields[1].Required);
        Assert.Equal("123", fields[1].Description);
    }

    [Fact]
    public async Task HumanInput_WithDateField_ParsesDateFieldDef()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: form
        type: human.input
        input:
          mode: form
          prompt: Please pick a date
          fields:
            - name: due_date
              type: date
              required: true
              description: Due date
              default: ""2026-06-09""
");

        var fakeProvider = new FakeHumanInputProvider(new JsonObject
        {
            ["due_date"] = "2026-06-10"
        });
        var engine = new WorkflowEngine
        {
            HumanInputProvider = fakeProvider,
            Limits = new ExecutionLimits { RunId = "test-run-date" }
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(HumanInputContract.ModeForm, fakeProvider.LastRequest!.Mode);
        Assert.Equal("date", fakeProvider.LastRequest.Fields![0].Type);
        Assert.Equal("2026-06-09", fakeProvider.LastRequest.Fields[0].Default);
    }

    [Fact]
    public void HumanInput_WithUnsupportedFieldType_FailsCompilation()
    {
        var ex = Assert.Throws<WorkflowCompilationException>(() => CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: form
        type: human.input
        input:
          mode: form
          prompt: Bad field
          fields:
            - name: value
              type: magical
"));

        Assert.Contains(ex.Errors, e => e.Code == ErrorCodes.InputValidation && e.Message.Contains("unsupported type"));
    }

    [Fact]
    public async Task HumanInput_NoProvider_ThrowsError()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: ask
        type: human.input
        input:
          mode: text
          prompt: Hello?
");

        var engine = new WorkflowEngine(); // No HumanInputProvider set

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("NO_HITL_PROVIDER", result.Error!.Code);
    }

    [Fact]
    public async Task HumanInput_SequentialWorkflow_PassesResponseToNextStep()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: ask
        type: human.input
        input:
          mode: text
          prompt: What is your name?
      - id: greet
        type: set
        input:
          greeting: ""Hello, ${data.steps.ask.response}!""
");

        var fakeProvider = new FakeHumanInputProvider(new JsonObject { ["response"] = "Alice" });
        var engine = new WorkflowEngine
        {
            HumanInputProvider = fakeProvider,
            Limits = new ExecutionLimits { RunId = "test-run-3" }
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.StepResults.Count);
        var greetOutput = result.StepResults[1].Output as JsonObject;
        Assert.Equal("Hello, Alice!", greetOutput!["greeting"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------
    // Level 2: Checkpoint / Resume
    // ------------------------------------------------------------------

    [Fact]
    public async Task Checkpoint_SavedAfterEachStep()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: step1
        type: set
        input:
          value: one
      - id: step2
        type: set
        input:
          value: two
");

        var checkpointer = new InMemoryWorkflowCheckpointer();
        var engine = new WorkflowEngine
        {
            Checkpointer = checkpointer,
            Limits = new ExecutionLimits { RunId = "cp-test-1" }
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);

        // Checkpoint should exist and point past the last step
        var cp = await checkpointer.LoadAsync("cp-test-1", CancellationToken.None);
        Assert.NotNull(cp);
        Assert.Equal(2, cp!.NextStepIndex);
        Assert.Equal("running", cp.Status);
    }

    [Fact]
    public async Task InMemoryCheckpointer_SaveLoadDelete()
    {
        var checkpointer = new InMemoryWorkflowCheckpointer();
        var cp = new WorkflowCheckpoint
        {
            RunId = "test-1",
            NextStepIndex = 3,
            Status = "paused",
            StepOutputs = new JsonObject { ["step1"] = "result1" },
            TenantId = "tenant-a"
        };

        await checkpointer.SaveAsync(cp, CancellationToken.None);
        var loaded = await checkpointer.LoadAsync("test-1", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.NextStepIndex);
        Assert.Equal("paused", loaded.Status);

        var list = await checkpointer.ListAsync("tenant-a");
        Assert.Single(list);

        await checkpointer.DeleteAsync("test-1", CancellationToken.None);
        var deleted = await checkpointer.LoadAsync("test-1", CancellationToken.None);
        Assert.Null(deleted);
    }


    // ------------------------------------------------------------------
    // Checkpoint: WorkflowYaml persistence
    // ------------------------------------------------------------------

    [Fact]
    public async Task Checkpoint_IncludesWorkflowYaml()
    {
        const string yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: step1
        type: set
        input:
          value: one
";

        var wf = CompileMain(yaml);
        var checkpointer = new InMemoryWorkflowCheckpointer();
        var engine = new WorkflowEngine
        {
            Checkpointer = checkpointer,
            Limits = new ExecutionLimits { RunId = "cp-yaml-1" }
        };

        await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        var cp = await checkpointer.LoadAsync("cp-yaml-1", CancellationToken.None);
        Assert.NotNull(cp);
        Assert.False(string.IsNullOrWhiteSpace(cp!.WorkflowYaml));
        Assert.Contains("step1", cp.WorkflowYaml);
    }

    // ------------------------------------------------------------------
    // Resume across human.input boundary
    // ------------------------------------------------------------------

    [Fact]
    public async Task Resume_AcrossHumanInput_ContinuesFromCheckpoint()
    {
        const string yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: step1
        type: set
        input:
          value: before_human
      - id: ask
        type: human.input
        input:
          mode: text
          prompt: What is the secret?
      - id: step3
        type: set
        input:
          greeting: ""Answer was ${data.steps.ask.response}""
";

        var wf = CompileMain(yaml);
        var checkpointer = new InMemoryWorkflowCheckpointer();

        // -- First run: will succeed all steps because we provide a provider --
        var fakeProvider = new FakeHumanInputProvider(new JsonObject { ["response"] = "42" });
        var engine1 = new WorkflowEngine
        {
            HumanInputProvider = fakeProvider,
            Checkpointer = checkpointer,
            Limits = new ExecutionLimits { RunId = "resume-hitl-1" }
        };

        var result1 = await engine1.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);
        Assert.True(result1.Success);

        // Checkpoint should point past all 3 steps
        var cp = await checkpointer.LoadAsync("resume-hitl-1", CancellationToken.None);
        Assert.NotNull(cp);
        Assert.Equal(3, cp!.NextStepIndex);

        // Manually create a checkpoint as if execution paused after step1
        var pausedCp = new WorkflowCheckpoint
        {
            RunId = "resume-hitl-2",
            WorkflowName = "main",
            WorkflowYaml = yaml,
            NextStepIndex = 1, // resume from step index 1 (the human.input step)
            StepOutputs = new JsonObject
            {
                ["step1"] = new JsonObject { ["value"] = "before_human" }
            },
            Inputs = new JsonObject(),
            Status = "paused"
        };
        await checkpointer.SaveAsync(pausedCp, CancellationToken.None);

        // -- Resume from the paused checkpoint --
        var fakeProvider2 = new FakeHumanInputProvider(new JsonObject { ["response"] = "secret_value" });
        var engine2 = new WorkflowEngine
        {
            HumanInputProvider = fakeProvider2,
            Checkpointer = checkpointer,
            Limits = new ExecutionLimits { RunId = "resume-hitl-2" }
        };

        var result2 = await engine2.ResumeAsync("resume-hitl-2", wf, CancellationToken.None);
        Assert.True(result2.Success);

        // Verify step3 used the human response
        var step3Output = result2.StepResults.FirstOrDefault(s => s.StepId == "step3")?.Output as JsonObject;
        Assert.NotNull(step3Output);
        Assert.Equal("Answer was secret_value", step3Output!["greeting"]!.GetValue<string>());
    }
}
