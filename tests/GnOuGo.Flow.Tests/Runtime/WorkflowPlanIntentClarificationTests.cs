using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Moq;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowPlanIntentClarificationTests
{
    private const string ValidGeneratedWorkflow = """
        version: 1
        name: clarified-workflow
        skill:
          description: Produce one deterministic result.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: result
                type: set
                input:
                  status: complete
        """;

    [Fact]
    public async Task MissingConfiguration_DefaultsToOff()
    {
        var human = new RecordingHumanInputProvider();
        var calls = 0;
        var llm = CreateLlm(request =>
        {
            calls++;
            Assert.False(IsClarificationRequest(request));
            return new LLMResponse { Text = ValidGeneratedWorkflow };
        });
        var workflow = CompileMain("""
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      mode: basic
                      raw_prompt: "Create one deterministic result."
                      generator:
                        model: test-model
                        prefilter: false
                        instruction: "Create one deterministic result."
                      policy:
                        allowed_step_types: [set]
                      validate:
                        compile: false
            """);

        var result = await new WorkflowEngine
        {
            LLMClient = llm,
            HumanInputProvider = human
        }.ExecuteAsync(workflow, new JsonObject(), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, calls);
        Assert.Empty(human.Requests);
    }

    [Theory]
    [InlineData("mode: unsupported")]
    [InlineData("mode: always\nmax_rounds: 0")]
    [InlineData("mode: always\nmax_questions: 2\nmax_questions_per_round: 3")]
    public async Task InvalidConfigurationFailsInputValidation(string clarificationConfiguration)
    {
        var indentedConfiguration = string.Join(
            Environment.NewLine,
            clarificationConfiguration.Split('\n').Select(static line => $"                        {line}"));
        var yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      mode: basic
                      raw_prompt: "Create one deterministic result."
                      intent_clarification:
            CONFIGURATION
                      generator:
                        instruction: "Create one deterministic result."
            """.Replace("CONFIGURATION", indentedConfiguration, StringComparison.Ordinal);
        var workflow = CompileMain(yaml);

        var result = await new WorkflowEngine().ExecuteAsync(
            workflow,
            new JsonObject(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
    }

    [Fact]
    public async Task AlwaysMode_AsksRichRecommendedFormBeforePlanning()
    {
        var human = new RecordingHumanInputProvider();
        var prompts = new List<string>();
        var clarificationCalls = 0;
        var llm = CreateLlm(request =>
        {
            prompts.Add(request.Prompt);
            if (IsClarificationRequest(request))
            {
                clarificationCalls++;
                return clarificationCalls == 1
                    ? QuestionsAssessment("Précisez le résultat attendu.", 2, "portee")
                    : Assessment("sufficient", "La demande est maintenant complète.");
            }
            return new LLMResponse { Text = ValidGeneratedWorkflow };
        });

        var result = await ExecuteAsync("always", llm, human, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var request = Assert.Single(human.Requests);
        Assert.True(request.AllowAbandon);
        Assert.All(request.Fields!, static field =>
        {
            Assert.Equal("radio", field.Type);
            Assert.True(field.AllowCustomAnswer);
            Assert.Equal(field.Options![0], field.Default);
            Assert.True(field.OptionDefinitions![0].Recommended);
            Assert.All(field.OptionDefinitions.Skip(1), static option => Assert.False(option.Recommended));
        });
        Assert.Contains("same language as the raw request", prompts[0], StringComparison.Ordinal);
        Assert.Contains("Créer un workflow", prompts[0], StringComparison.Ordinal);
        Assert.Contains(prompts, static prompt => prompt.Contains("<user_intent_clarification_json>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenNeededMode_ProceedsWithoutHumanWhenIntentIsSufficient()
    {
        var human = new RecordingHumanInputProvider();
        var llm = CreateLlm(request => IsClarificationRequest(request)
            ? Assessment("sufficient", "The request is decision-complete.")
            : new LLMResponse { Text = ValidGeneratedWorkflow });

        var result = await ExecuteAsync("when_needed", llm, human, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(human.Requests);
    }

    [Fact]
    public async Task AlwaysMode_IntrinsicImpossibilityFailsWithoutHumanPrompt()
    {
        var human = new RecordingHumanInputProvider();
        var llm = CreateLlm(request => IsClarificationRequest(request)
            ? Assessment("cannot_plan_safely", "The requested outcomes are mutually contradictory.")
            : throw new InvalidOperationException("Planning must not start."));

        var result = await ExecuteAsync("always", llm, human, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowPlanCannotPlanSafely, result.Error!.Code);
        Assert.Equal("cannot_plan_safely", result.Error.Details!["planning_outcome"]!.GetValue<string>());
        Assert.Empty(human.Requests);
    }

    [Fact]
    public async Task AlwaysMode_ExplicitAbandonReturnsDedicatedFailure()
    {
        var human = new RecordingHumanInputProvider(abandon: true);
        var llm = CreateLlm(request => IsClarificationRequest(request)
            ? QuestionsAssessment("Choose the intended scope.", 1, "scope")
            : throw new InvalidOperationException("Planning must not start."));

        var result = await ExecuteAsync("always", llm, human, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowPlanAborted, result.Error!.Code);
        Assert.Single(human.Requests);
    }

    [Fact]
    public async Task AlwaysMode_InitialPhaseReservesFollowUpBudget()
    {
        var human = new RecordingHumanInputProvider();
        var clarificationCalls = 0;
        var llm = CreateLlm(request =>
        {
            if (!IsClarificationRequest(request))
                return new LLMResponse { Text = ValidGeneratedWorkflow };

            clarificationCalls++;
            return QuestionsAssessment("Clarify the primary intent.", 5, "primary");
        });

        var result = await ExecuteAsync("always", llm, human, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Single(human.Requests);
        Assert.Equal(5, human.Requests[0].Fields!.Count);
        Assert.Equal(1, clarificationCalls);
    }

    [Fact]
    public async Task AlwaysMode_ReusedQuestionIdIsDisambiguatedWithinForm()
    {
        var human = new RecordingHumanInputProvider();
        var llm = CreateLlm(request =>
        {
            if (!IsClarificationRequest(request))
                return new LLMResponse { Text = ValidGeneratedWorkflow };

            var response = QuestionsAssessment("Clarify the primary intent.", 2, "scope");
            response.Json!["questions"]![1]!["id"] = "scope_1";
            return response;
        });

        var result = await ExecuteAsync("always", llm, human, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        var fields = Assert.Single(human.Requests).Fields!;
        Assert.Equal("scope_1", fields[0].Name);
        Assert.Equal("scope_1_2", fields[1].Name);
    }

    [Fact]
    public async Task AlwaysMode_DuplicateOptionValueRepairRequiresDistinctTrimmedLabels()
    {
        var human = new RecordingHumanInputProvider();
        var clarificationPrompts = new List<string>();
        var clarificationCalls = 0;
        var llm = CreateLlm(request =>
        {
            if (!IsClarificationRequest(request))
                return new LLMResponse { Text = ValidGeneratedWorkflow };

            clarificationPrompts.Add(request.Prompt);
            clarificationCalls++;
            if (clarificationCalls == 2)
                return QuestionsAssessment("Choose the intended scope.", 1, "scope");

            var response = QuestionsAssessment("Choose the intended scope.", 1, "scope");
            response.Json!["questions"]![0]!["options"]![1]!["value"] = " Recommended 1 ";
            return response;
        });

        var result = await ExecuteAsync("always", llm, human, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, clarificationCalls);
        Assert.Single(human.Requests);
        Assert.Contains(
            "duplicates an earlier value after trimming",
            clarificationPrompts[1],
            StringComparison.Ordinal);
        Assert.Contains("Return one complete corrected object", clarificationPrompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlwaysMode_InvalidAnalysisAfterRepairFailsClosed()
    {
        var human = new RecordingHumanInputProvider();
        var calls = 0;
        var llm = CreateLlm(request =>
        {
            if (!IsClarificationRequest(request))
                throw new InvalidOperationException("Planning must not start.");
            calls++;
            return new LLMResponse { Text = calls == 1 ? "not-json" : "still-not-json" };
        });

        var result = await ExecuteAsync("always", llm, human, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowPlanClarificationFailed, result.Error!.Code);
        Assert.Equal(
            "The clarification analyst returned invalid JSON.",
            result.Error.Details!["reason"]!.GetValue<string>());
        Assert.Equal(2, calls);
        Assert.Empty(human.Requests);
    }

    [Fact]
    public async Task AlwaysMode_RequiredFormWithoutProviderReturnsClarificationFailure()
    {
        var llm = CreateLlm(request => IsClarificationRequest(request)
            ? QuestionsAssessment("Choose the intended scope.", 1, "scope")
            : throw new InvalidOperationException("Planning must not start."));

        var result = await ExecuteAsync(
            "always",
            llm,
            human: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowPlanClarificationFailed, result.Error!.Code);
        Assert.Equal("clarification_provider_unavailable", result.Error.Details!["classification"]!.GetValue<string>());
    }

    [Fact]
    public async Task AlwaysMode_HumanTimeoutReturnsClarificationFailure()
    {
        var human = new AwaitCancellationHumanInputProvider();
        var llm = CreateLlm(request => IsClarificationRequest(request)
            ? QuestionsAssessment("Choose the intended scope.", 1, "scope")
            : throw new InvalidOperationException("Planning must not start."));

        var result = await ExecuteAsync(
            "always",
            llm,
            human,
            timeoutMs: 25,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowPlanClarificationFailed, result.Error!.Code);
        Assert.Equal("clarification_timeout", result.Error.Details!["classification"]!.GetValue<string>());
    }

    [Fact]
    public async Task AlwaysMode_WorkflowCancellationIsNotReportedAsClarificationFailure()
    {
        var human = new AwaitCancellationHumanInputProvider();
        var llm = CreateLlm(request => IsClarificationRequest(request)
            ? QuestionsAssessment("Choose the intended scope.", 1, "scope")
            : throw new InvalidOperationException("Planning must not start."));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));

        var result = await ExecuteAsync("always", llm, human, cancellationToken: cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal("CANCELLED", result.Error!.Code);
    }

    [Fact]
    public async Task AlwaysMode_MissingRequiredAnswerReturnsClarificationFailure()
    {
        var human = new EmptyHumanInputProvider();
        var llm = CreateLlm(request => IsClarificationRequest(request)
            ? QuestionsAssessment("Choose the intended scope.", 1, "scope")
            : throw new InvalidOperationException("Planning must not start."));

        var result = await ExecuteAsync("always", llm, human, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowPlanClarificationFailed, result.Error!.Code);
        Assert.Equal("clarification_invalid_response", result.Error.Details!["classification"]!.GetValue<string>());
    }

    private static async Task<RunResult> ExecuteAsync(
        string mode,
        ILLMClient llm,
        IHumanInputProvider? human,
        int timeoutMs = 10000,
        CancellationToken cancellationToken = default)
    {
        var workflow = CompileMain($$"""
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      mode: basic
                      raw_prompt: "Créer un workflow qui produit un résultat."
                      intent_clarification:
                        mode: {{mode}}
                        timeout_ms: {{timeoutMs}}
                        max_rounds: 2
                        max_questions: 8
                        max_questions_per_round: 5
                      generator:
                        model: test-model
                        reasoning: medium
                        prefilter: false
                        instruction: "Create a workflow that produces one result."
                      policy:
                        allowed_step_types: [set]
                      validate:
                        compile: false
            """);

        return await new WorkflowEngine
        {
            LLMClient = llm,
            HumanInputProvider = human,
            Limits = new ExecutionLimits { RunId = "intent-clarification-test" }
        }.ExecuteAsync(workflow, new JsonObject(), cancellationToken);
    }

    private static CompiledWorkflow CompileMain(string yaml)
    {
        var document = WorkflowParser.Parse(yaml);
        var compiled = new WorkflowCompiler().Compile(document);
        return compiled.Workflows[compiled.Entrypoint!];
    }

    private static ILLMClient CreateLlm(Func<LLMRequest, LLMResponse> responseFactory)
    {
        var mock = new Mock<ILLMClient>();
        mock.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) => responseFactory(request));
        return mock.Object;
    }

    private static bool IsClarificationRequest(LLMRequest request) =>
        request.Prompt.Contains("provider-neutral workflow intent clarification analyst", StringComparison.Ordinal);

    private static LLMResponse Assessment(string outcome, string reason)
    {
        var json = new JsonObject
        {
            ["outcome"] = outcome,
            ["reason"] = reason,
            ["questions"] = new JsonArray()
        };
        return new LLMResponse { Json = json, Text = json.ToJsonString() };
    }

    private static LLMResponse QuestionsAssessment(string reason, int count, string prefix)
    {
        var questions = new JsonArray();
        for (var index = 1; index <= count; index++)
        {
            questions.Add((JsonNode)new JsonObject
            {
                ["id"] = $"{prefix}_{index}",
                ["prompt"] = $"Question {index}?",
                ["options"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["value"] = $"Recommended {index}",
                        ["description"] = "Uses the most likely interpretation.",
                        ["recommended"] = true
                    },
                    new JsonObject
                    {
                        ["value"] = $"Alternative {index}",
                        ["description"] = "Uses the alternate interpretation.",
                        ["recommended"] = false
                    }
                }
            });
        }
        var json = new JsonObject
        {
            ["outcome"] = "questions",
            ["reason"] = reason,
            ["questions"] = questions
        };
        return new LLMResponse { Json = json, Text = json.ToJsonString() };
    }

    private sealed class RecordingHumanInputProvider(bool abandon = false) : IHumanInputProvider
    {
        public List<HumanInputRequest> Requests { get; } = [];

        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (abandon)
            {
                return Task.FromResult<JsonNode?>(new JsonObject
                {
                    [HumanInputContract.ActionProperty] = HumanInputContract.ActionAbandon
                });
            }

            var response = new JsonObject
            {
                [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
            };
            foreach (var field in request.Fields ?? [])
                response[field.Name] = field.Default ?? field.Options?.FirstOrDefault() ?? "custom answer";
            return Task.FromResult<JsonNode?>(response);
        }
    }

    private sealed class AwaitCancellationHumanInputProvider : IHumanInputProvider
    {
        public async Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }
    }

    private sealed class EmptyHumanInputProvider : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) =>
            Task.FromResult<JsonNode?>(new JsonObject
            {
                [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
            });
    }
}
