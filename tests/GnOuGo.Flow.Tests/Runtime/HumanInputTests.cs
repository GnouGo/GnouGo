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
    public async Task HumanInput_RichOptionsPreserveLegacyValuesAndPresentationMetadata()
    {
        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: clarify
                type: human.input
                input:
                  mode: form
                  prompt: Clarify the intent.
                  allow_abandon: true
                  fields:
                    - name: scope
                      type: radio
                      required: true
                      options: [focused, broad]
                      option_definitions:
                        - value: focused
                          description: Limit the behavior to the requested target.
                          recommended: true
                        - value: broad
                          description: Apply the behavior to every target.
                          recommended: false
                      allow_custom_answer: true
                      default: focused
        """);
        var provider = new FakeHumanInputProvider(new JsonObject
        {
            ["scope"] = "custom scope",
            [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
        });

        var result = await new WorkflowEngine { HumanInputProvider = provider }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(provider.LastRequest!.AllowAbandon);
        var field = Assert.Single(provider.LastRequest.Fields!);
        Assert.Equal(["focused", "broad"], field.Options);
        Assert.True(field.AllowCustomAnswer);
        Assert.True(field.OptionDefinitions![0].Recommended);
        Assert.False(field.OptionDefinitions[1].Recommended);
        var payload = HumanInputContract.BuildRequestPayload(provider.LastRequest);
        Assert.True(payload["allow_abandon"]!.GetValue<bool>());
        Assert.True(payload["fields"]![0]!["allow_custom_answer"]!.GetValue<bool>());
        Assert.Equal("Limit the behavior to the requested target.", payload["fields"]![0]!["option_definitions"]![0]!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task HumanInput_AllowedAbandonReturnsStandardAction()
    {
        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: clarify
                type: human.input
                input:
                  mode: text
                  prompt: Clarify the intent.
                  allow_abandon: true
        """);
        var provider = new FakeHumanInputProvider(new JsonObject
        {
            [HumanInputContract.ActionProperty] = HumanInputContract.ActionAbandon
        });

        var result = await new WorkflowEngine { HumanInputProvider = provider }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(
            HumanInputContract.ActionAbandon,
            result.StepResults[0].Output![HumanInputContract.ActionProperty]!.GetValue<string>());
    }

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
    public async Task HumanInput_Confirm_ReturnsBooleanAndRoutesApprovedBranch()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: ask
        type: human.input
        input:
          mode: confirm
          prompt: Publish the result?
          choices: [approve, reject]
      - id: route
        type: switch
        cases:
          - when: ""${data.steps.ask.response}""
            steps:
              - id: approved
                type: set
                input: { published: true }
        default:
          - id: rejected
            type: set
            input: { published: false }
");

        // UI and CLI providers submit the selected presentation label. The
        // executor must normalize it to the boolean contract advertised to
        // expression validation and downstream switch conditions.
        var fakeProvider = new FakeHumanInputProvider(new JsonObject
        {
            ["response"] = "approve",
            ["source"] = "test"
        });
        var engine = new WorkflowEngine
        {
            HumanInputProvider = fakeProvider,
            Limits = new ExecutionLimits { RunId = "test-run-confirm" }
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(HumanInputContract.ModeConfirm, fakeProvider.LastRequest!.Mode);
        var confirmationOutput = Assert.IsType<JsonObject>(result.StepResults.Single(step => step.StepId == "ask").Output);
        Assert.True(confirmationOutput["response"]!.GetValue<bool>());
        Assert.Equal("test", confirmationOutput["source"]!.GetValue<string>());
        var routeOutput = Assert.IsType<JsonObject>(result.StepResults.Single(step => step.StepId == "route").Output);
        var approvedOutput = Assert.IsType<JsonObject>(routeOutput["approved"]);
        Assert.True(approvedOutput["published"]!.GetValue<bool>());
        Assert.Null(routeOutput["rejected"]);
    }

    [Fact]
    public void HumanInputContract_ConfirmMapsTwoCustomPresentationChoicesToBoolean()
    {
        var choices = new[] { "Send email", "Do not send" };

        Assert.True(HumanInputContract.TryReadConfirmation(JsonValue.Create("Send email"), choices, out var approved));
        Assert.True(approved);
        Assert.True(HumanInputContract.TryReadConfirmation(JsonValue.Create("Do not send"), choices, out var rejected));
        Assert.False(rejected);
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
    public async Task WorkflowCall_ExplicitEmptyRequiredProperties_KeepsNestedPropertiesOptional()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: call
        type: workflow.call
        input:
          ref: { kind: local, name: accept_variants }
          args:
            value:
              canonical: present
  accept_variants:
    inputs:
      value:
        type: object
        required: true
        properties:
          canonical: { type: string }
          alternative: { type: string }
        required_properties: []
    steps:
      - id: done
        type: set
        input: { ok: true }
");

        var result = await new WorkflowEngine().ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
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

}
