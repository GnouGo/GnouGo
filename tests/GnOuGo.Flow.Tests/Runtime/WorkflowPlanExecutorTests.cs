using System.Text.Json.Nodes;
using Moq;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Runtime.Executors;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class WorkflowPlanExecutorTests
{
    private static CompiledWorkflow CompileMain(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);
        return compiled.Workflows[compiled.Entrypoint!];
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    // ------ DslSnippet tests ------

    [Fact]
    public void AllExecutors_WithDslSnippet_ContainTheirStepType()
    {
        var engine = new WorkflowEngine();
        foreach (var stepType in engine.Registry.RegisteredTypes)
        {
            var executor = engine.Registry.Get(stepType);
            Assert.NotNull(executor);
            var snippet = executor!.DslSnippet;
            // Executors that opt out (null) are fine � but those that provide one must mention their type
            if (snippet != null)
            {
                Assert.Contains(stepType, snippet);
            }
        }
    }

    [Fact]
    public void GetDslSnippets_ReturnsNonEmpty()
    {
        var engine = new WorkflowEngine();
        var snippets = engine.Registry.GetDslSnippets().ToList();
        Assert.NotEmpty(snippets);
        // Should contain major types
        var joined = string.Join("\n", snippets);
        Assert.Contains("template.render", joined);
        Assert.Contains("llm.call", joined);
        Assert.Contains("loop.parallel", joined);
        Assert.Contains("sequence", joined);
    }

    [Fact]
    public void GetDslSnippets_FilteredByAllowedTypes()
    {
        var engine = new WorkflowEngine();
        var allowed = new HashSet<string> { "template.render", "llm.call" };
        var snippets = engine.Registry.GetDslSnippets(allowed).ToList();
        var joined = string.Join("\n", snippets);

        Assert.Contains("template.render", joined);
        Assert.Contains("llm.call", joined);
        Assert.DoesNotContain("### mcp.call", joined);
        Assert.DoesNotContain("### sequence", joined);
    }

    [Fact]
    public void HumanInput_DslSnippet_ContainsPlannerContract()
    {
        var snippet = new HumanInputExecutor().DslSnippet!;

        Assert.Contains("Always set `input.mode` explicitly", snippet);
        Assert.Contains("Valid modes: text, choice, form, confirm.", snippet);
        Assert.Contains("date", snippet);
        Assert.Contains("mode: confirm", snippet);
        Assert.Contains("data.steps.<id>.response", snippet);
        Assert.Contains("data.steps.<id>.<field_name>", snippet);
    }

    [Fact]
    public void WorkflowPlanSemanticValidator_AllowsHumanInputResponseAndFormFields()
    {
        var doc = WorkflowParser.Parse("""
version: 1
workflows:
  main:
    steps:
      - id: approval
        type: human.input
        input:
          mode: choice
          prompt: "Approve?"
          choices: [approve, reject]
      - id: schedule
        type: human.input
        input:
          mode: form
          prompt: "Pick a due date"
          fields:
            - name: due_date
              type: date
              required: true
            - name: retry_count
              type: integer
              required: false
      - id: use_values
        type: set
        input:
          decision: "${data.steps.approval.response}"
          due: "${data.steps.schedule.due_date}"
          retries: "${data.steps.schedule.retry_count}"
""");

        var validatorType = typeof(WorkflowEngine).Assembly.GetType("GnOuGo.Flow.Core.Runtime.WorkflowPlanSemanticValidator", throwOnError: true)!;
        var validate = validatorType.GetMethod("Validate", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        validate.Invoke(null, new object?[] { doc, null });
    }

    [Fact]
    public void DslReference_CommonReference_ContainsBuiltInFunctions()
    {
        var reference = GnOuGo.Flow.Core.Runtime.Executors.DslReference.CommonReference;
        Assert.Contains("exists(val)", reference);
        Assert.Contains("coalesce(a", reference);
        Assert.Contains("len(val)", reference);
        Assert.Contains("lower(s)", reference);
        Assert.Contains("upper(s)", reference);
        Assert.Contains("trim(s)", reference);
        Assert.Contains("contains(s", reference);
        Assert.Contains("startsWith(s", reference);
        Assert.Contains("endsWith(s", reference);
        Assert.Contains("replace(s", reference);
        Assert.Contains("toNumber(val)", reference);
        Assert.Contains("json(val)", reference);
        Assert.Contains("now()", reference);
        Assert.Contains("formatDate(", reference);
    }

    [Fact]
    public void DslReference_CommonReference_ContainsExpressionSyntax()
    {
        var reference = GnOuGo.Flow.Core.Runtime.Executors.DslReference.CommonReference;
        Assert.Contains("data.inputs.*", reference);
        Assert.Contains("data.steps.<step_id>.*", reference);
        Assert.Contains("data.env.*", reference);
        Assert.Contains("${", reference);
    }

    [Fact]
    public void DslReference_CommonReference_ContainsStepCommonFields()
    {
        var reference = GnOuGo.Flow.Core.Runtime.Executors.DslReference.CommonReference;
        Assert.Contains("retry:", reference);
        Assert.Contains("on_error:", reference);
        Assert.Contains("if:", reference);
        Assert.Contains("output:", reference);
        Assert.Contains("continue", reference);
        Assert.Contains("stop", reference);
    }

    [Fact]
    public void DslReference_CommonReference_ContainsSkillMetadataGuidance()
    {
        var reference = GnOuGo.Flow.Core.Runtime.Executors.DslReference.CommonReference;
        Assert.Contains("skill:", reference);
        Assert.Contains("Skill metadata", reference);
        Assert.Contains("MUST include a top-level `skill` block", reference);
        Assert.Contains("auto-extract", reference);
    }

    // ------ Prompt construction tests ------

    [Fact]
    public async Task WorkflowPlan_PromptContainsDslReference()
    {
        string? capturedPrompt = null;

        // Mock LLM that captures the prompt and returns valid YAML
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new LLMResponse
            {
                Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a simple greeting workflow
          validate:
            compile: false
");
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };
        await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.NotNull(capturedPrompt);
        // Prompt should contain the DSL reference
        Assert.Contains("<dsl_reference>", capturedPrompt);
        Assert.Contains("</dsl_reference>", capturedPrompt);
        Assert.DoesNotContain("[DSL REFERENCE]", capturedPrompt);
        Assert.Contains("<available_step_types>", capturedPrompt);
        Assert.Contains("</available_step_types>", capturedPrompt);
        Assert.DoesNotContain("[AVAILABLE STEP TYPES]", capturedPrompt);
        Assert.Contains("<task>", capturedPrompt);
        Assert.Contains("</task>", capturedPrompt);
        Assert.DoesNotContain("[TASK]", capturedPrompt);
        Assert.Contains("<user_prompt>", capturedPrompt);
        Assert.Contains("Build a simple greeting workflow", capturedPrompt);
        Assert.Contains("</user_prompt>", capturedPrompt);
        Assert.DoesNotContain("Instruction: Build a simple greeting workflow", capturedPrompt);
        Assert.Contains("<error_handling_and_retries>", capturedPrompt);
        Assert.Contains("</error_handling_and_retries>", capturedPrompt);
        Assert.DoesNotContain("[STRUCTURED OUTPUT STRICT SCHEMAS]", capturedPrompt);
        Assert.Contains("Use `retry` only for transient errors that are explicitly marked retryable by the runtime.", capturedPrompt);
        Assert.Contains("Retries run before `on_error` is evaluated.", capturedPrompt);
        Assert.Contains("Inside `on_error.cases[].if`, the error context exposes `error.code`, `error.message`, `error.retryable`, `step.id`, and `step.type`.", capturedPrompt);
        Assert.Contains("Retry + fallback example for a transient LLM error, as YAML:", capturedPrompt);
        Assert.Contains("Non-retryable validation example, as YAML:", capturedPrompt);
        Assert.Contains("<step_exceptions_by_type>", capturedPrompt);
        Assert.Contains("</step_exceptions_by_type>", capturedPrompt);
        Assert.Contains("- llm.call", capturedPrompt);
        Assert.Contains("LLM_TIMEOUT (retryable)", capturedPrompt);
        Assert.Contains("- template.render", capturedPrompt);
        Assert.Contains("JSON_PARSE (non-retryable)", capturedPrompt);
        Assert.DoesNotContain("```", capturedPrompt);
        Assert.Contains("Function arguments are evaluated before the function runs", capturedPrompt);
        Assert.Contains("coalesce(data.steps.branch_a.value, data.steps.branch_b.value)", capturedPrompt);
        Assert.Contains("produced only inside `switch` cases", capturedPrompt);
        Assert.Contains("version, name, skill, workflows", capturedPrompt);
        Assert.Contains("- skill: required object", capturedPrompt);
        // Should contain built-in functions doc
        Assert.Contains("exists(val)", capturedPrompt);
        Assert.Contains("len(val)", capturedPrompt);
        // Should contain step type snippets
        Assert.Contains("template.render", capturedPrompt);
        Assert.Contains("loop.parallel", capturedPrompt);
        // Should contain the instruction
        Assert.Contains("Build a simple greeting workflow", capturedPrompt);
    }

    [Fact]
    public async Task WorkflowPlan_UsesRuntimeDefaults_WhenGeneratorProviderAndModelAreOmitted()
    {
        LLMRequest? capturedRequest = null;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new LLMResponse
            {
                Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            instruction: Build a simple greeting workflow
          validate:
            compile: false
");
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LlmDefaults = new LlmRuntimeDefaults
            {
                Provider = "openai",
                Model = "gpt-4o-mini"
            }
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(capturedRequest);
        Assert.Equal("openai", capturedRequest!.Provider);
        Assert.Equal("gpt-4o-mini", capturedRequest.Model);
        Assert.Equal("medium", capturedRequest.Reasoning);
        Assert.True(capturedRequest.UseBackgroundMode);
    }

    [Fact]
    public async Task WorkflowPlan_WithAllowedTypes_FiltersSnippets()
    {
        string? capturedPrompt = null;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new LLMResponse
            {
                Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build something
          policy:
            allowed_step_types: [template.render, llm.call]
          validate:
            compile: false
");
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };
        await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.NotNull(capturedPrompt);
        // Should contain allowed types
        Assert.Contains("template.render", capturedPrompt);
        Assert.Contains("llm.call", capturedPrompt);
        // Should NOT contain snippets for non-allowed types
        Assert.DoesNotContain("### mcp.call", capturedPrompt);
        Assert.DoesNotContain("### sequence", capturedPrompt);
        Assert.Contains("<step_exceptions_by_type>", capturedPrompt);
        Assert.Contains("- template.render", capturedPrompt);
        Assert.Contains("- llm.call", capturedPrompt);
        Assert.DoesNotContain("- mcp.call", capturedPrompt);
        Assert.DoesNotContain("- sequence", capturedPrompt);
        Assert.Contains("<constraints>", capturedPrompt);
        Assert.Contains("Allowed step types:", capturedPrompt);
    }


    [Fact]
    public async Task WorkflowPlan_EnforcesPolicyOnNestedSteps()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated token budget workflow.
                         tags: [tokens]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: wrapper
                               type: sequence
                               steps:
                                 - id: nested_plan
                                   type: workflow.plan
                                   input:
                                     generator:
                                       instruction: nested
                       """
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            instruction: Build something
          policy:
            allowed_step_types: [sequence]
            denied_step_types: [workflow.plan]
          validate:
            compile: false
");
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCodes.TemplatePolicy, result.Error!.Code);
        Assert.Contains("workflow.plan", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_Reprompt_InjectsPreviousError()
    {
        var prompts = new List<string>();
        int callCount = 0;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => prompts.Add(req.Prompt))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call: return invalid YAML (missing 'type' in step)
                    return new LLMResponse { Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n" };
                }
                // Second call: return valid YAML
                return new LLMResponse
                {
                    Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
                };
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build something
          on_invalid:
            action: reprompt
            max_attempts: 3
          validate:
            compile: false
");
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, prompts.Count);
        // First prompt should NOT contain a previous-error block.
        Assert.DoesNotContain("<previous_error>", prompts[0]);
        // Second prompt SHOULD contain previous-error and invalid-YAML blocks.
        Assert.Contains("<previous_error>", prompts[1]);
        Assert.Contains("<invalid_yaml>", prompts[1]);
        Assert.Contains("<user_prompt>", prompts[1]);
        Assert.Contains("Build something", prompts[1]);
        Assert.Contains("</user_prompt>", prompts[1]);
        Assert.DoesNotContain("Instruction: Build something", prompts[1]);
        Assert.Contains("<previous_error>", prompts[1]);
        Assert.Contains("</previous_error>", prompts[1]);
        Assert.Contains("<invalid_yaml>", prompts[1]);
        Assert.Contains("</invalid_yaml>", prompts[1]);
        Assert.Contains("version: 1", prompts[1]);
        Assert.DoesNotContain("<dsl_reference>", prompts[1]);
        Assert.DoesNotContain("<step_exceptions_by_type>", prompts[1]);
        Assert.Contains("Fix the issues", prompts[1]);
    }

    [Fact]
    public async Task WorkflowPlan_Reprompt_StripsDuplicatedTaskPreambleFromInvalidYaml()
    {
        const string uniqueTaskMarker = "UNIQUE_ORIGINAL_USER_PROMPT_TEST12";
        var prompts = new List<string>();
        int callCount = 0;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => prompts.Add(req.Prompt))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new LLMResponse
                    {
                        Text = $"""
                                Agent description:
                                Build an agent for {uniqueTaskMarker} and keep the user request intent.
                                version: 1
                                workflows:
                                  main:
                                    steps:
                                      - id: s
                                """
                    };
                }

                return new LLMResponse
                {
                    Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
                };
            });

        var wf = CompileMain($$"""
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: |
              Agent description:
              Build an agent for {{uniqueTaskMarker}} and keep the user request intent.
          on_invalid:
            action: reprompt
            max_attempts: 3
          validate:
            compile: false
""");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, prompts.Count);
        Assert.Equal(1, CountOccurrences(prompts[1], uniqueTaskMarker));
        Assert.Contains("<invalid_yaml>", prompts[1]);
        Assert.Contains("version: 1", prompts[1]);
    }

    [Fact]
    public async Task WorkflowPlan_Reprompt_OnValidatorDiagnostics_NotOnlyCompilerFatalErrors()
    {
        var prompts = new List<string>();
        int callCount = 0;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => prompts.Add(req.Prompt))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new LLMResponse
                    {
                        Text = "version: 1\nskill:\n  description: Generated workflow.\n  tags: [generated]\n  inputs: {}\n  outputs: {}\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: definitely.not.a.step\n"
                    };
                }

                return new LLMResponse
                {
                    Text = "version: 1\nskill:\n  description: Generated workflow.\n  tags: [generated]\n  inputs: {}\n  outputs: {}\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
                };
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build something
          on_invalid:
            action: reprompt
            max_attempts: 3
");
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, prompts.Count);
        Assert.Contains("<previous_error>", prompts[1]);
        Assert.Contains("UNKNOWN_STEP_TYPE", prompts[1]);
        Assert.Contains("STEP_TYPE_UNKNOWN", prompts[1]);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_AllowsKnownMcpResponseProperty()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated docs workflow.
                         tags: [docs]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: fetch
                               type: mcp.call
                               input:
                                 server: docs
                                 method: get_doc
                                 request: { id: "intro" }
                             - id: map
                               type: set
                               input:
                                 title: "${data.steps.fetch.response.title}"
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "get_doc",
                    Description = "Get a document",
                    OutputSchema = JsonNode.Parse("""
                    { "type": "object", "properties": { "title": { "type": "string" } }, "additionalProperties": false }
                    """)
                }
            }
        });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a docs workflow
            prefilter: false
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsUnknownMcpServer()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated docs workflow.
                         tags: [docs]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: fetch
                               type: mcp.call
                               input:
                                 server: missing_docs
                                 method: get_doc
                                 request: { id: "intro" }
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo> { new() { Name = "get_doc", Description = "Get a document" } }
        });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a docs workflow
            prefilter: false
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_SERVER_UNKNOWN", result.Error.Message);
        Assert.Contains("missing_docs", result.Error.Message);
        Assert.Contains("mcp.server:docs", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsUnknownMcpMethod()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated docs workflow.
                         tags: [docs]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: fetch
                               type: mcp.call
                               input:
                                 server: docs
                                 method: missing_doc
                                 request: { id: "intro" }
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo> { new() { Name = "get_doc", Description = "Get a document" } }
        });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a docs workflow
            prefilter: false
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_METHOD_UNKNOWN", result.Error.Message);
        Assert.Contains("missing_doc", result.Error.Message);
        Assert.Contains("mcp.server:docs.method:get_doc", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsUnknownMcpResponseProperty()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated token budget workflow.
                         tags: [tokens]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: fetch
                               type: mcp.call
                               input:
                                 server: docs
                                 method: get_doc
                                 request: { id: "intro" }
                             - id: map
                               type: set
                               input:
                                 title: "${data.steps.fetch.response.missing_title}"
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "get_doc",
                    Description = "Get a document",
                    OutputSchema = JsonNode.Parse("""
                    { "type": "object", "properties": { "title": { "type": "string" } }, "additionalProperties": false }
                    """)
                }
            }
        });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a docs workflow
            prefilter: false
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("STEP_OUTPUT_PROPERTY_UNKNOWN", result.Error.Message);
        Assert.Contains("data.steps.fetch.response.missing_title", result.Error.Message);
        Assert.Contains("data.steps.fetch.response.title", result.Error.Message);
        Assert.Contains("suggestion", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsDeepAccessIntoOpaqueMcpResponse()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated token budget workflow.
                         tags: [tokens]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: fetch
                               type: mcp.call
                               input:
                                 server: docs
                                 method: get_doc
                                 request: { id: "intro" }
                             - id: map
                               type: set
                               input:
                                 title: "${data.steps.fetch.response.title}"
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "get_doc", Description = "Get a document without a declared output contract" }
            }
        });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a docs workflow
            prefilter: false
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("OPAQUE_RESPONSE_DEEP_ACCESS", result.Error.Message);
        Assert.Contains("json(data.steps.fetch.response)", result.Error.Message);
        Assert.Contains("structured_output", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsInvalidMcpRequestAgainstInputSchema()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated GitHub pull request workflow.
                         tags: [github, pull-requests]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: close_issue
                               type: mcp.call
                               input:
                                 server: github
                                 kind: tool
                                 method: issue_write
                                 request:
                                   owner: AxaFrance
                                   repo: oidc-client
                                   issue_number: 1651
                                   state: closed
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "issue_write",
                    Description = "Update an issue state",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "owner": { "type": "string" },
                        "repo": { "type": "string" },
                        "method": { "type": "string" },
                        "issue_number": { "type": "integer" },
                        "state": { "type": "string" }
                      },
                      "required": ["owner", "repo", "method", "issue_number", "state"],
                      "additionalProperties": false
                    }
                    """),
                    OutputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "status": { "type": "string" }
                      },
                      "additionalProperties": false
                    }
                    """)
                }
            }
        });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Close GitHub issue
            prefilter: false
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUEST_SCHEMA_INVALID", result.Error.Message);
        Assert.Contains("input.request.method", result.Error.Message);
        Assert.Contains("missing required property", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_DryRun_RejectsFreeformLlmTextUsedAsNumber()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated token budget workflow.
                         tags: [tokens]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: extract
                               type: llm.call
                               input:
                                 prompt: "Return a token budget"
                             - id: answer
                               type: llm.call
                               input:
                                 prompt: "Answer briefly"
                                 max_tokens: "${data.steps.extract.text}"
                       """
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a workflow that extracts a token budget
            prefilter: false
          validate:
            dry_run: true
          on_invalid:
            action: stop
            max_attempts: 1
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("dry_run", result.Error.Message);
        Assert.Contains("Expected number", result.Error.Message);
        Assert.Contains("dry-run text response", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_DryRun_AllowsStructuredLlmJsonUsedAsNumber()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated GitHub pull request workflow.
                         tags: [github, pull-requests]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: extract
                               type: llm.call
                               input:
                                 prompt: "Return a token budget"
                                 structured_output:
                                   schema_inline:
                                     type: object
                                     properties:
                                       max_tokens:
                                         type: integer
                                     required: [max_tokens]
                                     additionalProperties: false
                             - id: answer
                               type: llm.call
                               input:
                                 prompt: "Answer briefly"
                                 max_tokens: "${data.steps.extract.json.max_tokens}"
                           outputs:
                             answer:
                               expr: "${data.steps.answer.text}"
                               type: string
                       """
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a workflow that extracts a token budget
            prefilter: false
          validate:
            dry_run: true
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task WorkflowPlan_DryRun_AllowsNumericInputDefaultParsedFromYamlScalar()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated issue triage workflow.
                         tags: [issues]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           inputs:
                             issue_limit:
                               type: number
                               required: false
                               default: "5"
                           steps:
                             - id: echo
                               type: set
                               input:
                                 limit: "${data.inputs.issue_limit}"
                           outputs:
                             limit:
                               expr: "${data.steps.echo.limit}"
                               type: number
                       """
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a workflow with a numeric issue limit
            prefilter: false
          validate:
            dry_run: true
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task WorkflowPlan_CompileValidation_RejectsDuplicateStepIdsBeforeDryRun()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated workflow with duplicate ids.
                         tags: [test]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: need_summary
                               type: set
                               input:
                                 value: one
                             - id: branch
                               type: switch
                               cases:
                                 - when: "${true}"
                                   steps:
                                     - id: need_summary
                                       type: set
                                       input:
                                         value: two
                       """
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a workflow with duplicate ids
            prefilter: false
          validate:
            compile: true
            dry_run: true
          on_invalid:
            action: stop
            max_attempts: 1
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("DUPLICATE_STEP_ID", result.Error.Message);
        Assert.Contains("need_summary", result.Error.Message);
        Assert.DoesNotContain("same key", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkflowPlan_DryRun_TreatsOnlyInternalErrorAsInconclusive()
    {
        var validatorType = typeof(WorkflowEngine).Assembly.GetType(
            "GnOuGo.Flow.Core.Runtime.WorkflowPlanDryRunValidator",
            throwOnError: true)!;
        var method = validatorType.GetMethod(
            "IsInconclusiveInternalError",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        Assert.True((bool)method.Invoke(null, new object?[] { "INTERNAL_ERROR" })!);
        Assert.False((bool)method.Invoke(null, new object?[] { ErrorCodes.EvalError })!);
        Assert.False((bool)method.Invoke(null, new object?[] { ErrorCodes.InputValidation })!);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_AllowsIntegerYamlScalarForNumberMcpSchema()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated GitHub pull request workflow.
                         tags: [github, pull-requests]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: list_prs
                               type: mcp.call
                               input:
                                 server: github
                                 kind: tool
                                 method: list_pull_requests
                                 request:
                                   owner: AxaFrance
                                   repo: oidc-client
                                   perPage: 100
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "list_pull_requests",
                    Description = "List pull requests",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "owner": { "type": "string" },
                        "repo": { "type": "string" },
                        "perPage": { "type": "number" }
                      },
                      "required": ["owner", "repo"],
                      "additionalProperties": false
                    }
                    """)
                }
            }
        });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: List GitHub pull requests
            prefilter: false
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task WorkflowPlan_Validation_ReturnsStructuralAndSemanticDiagnosticsTogether()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       workflows:
                         main:
                           inputs:
                             bad_input:
                               type: string
                               properties:
                                 nested:
                                   type: string
                           steps:
                             - id: collect
                               type: set
                               input:
                                 value: ok
                           outputs:
                             bad_output:
                               type: string
                               properties:
                                 nested:
                                   type: string
                               expr: "${data.steps.missing.value}"
                       """
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a workflow with typed inputs and outputs
            prefilter: false
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("INVALID_INPUT_SCHEMA", result.Error.Message);
        Assert.Contains("INVALID_OUTPUT_SCHEMA", result.Error.Message);
        Assert.Contains("STEP_REFERENCE_UNKNOWN", result.Error.Message);
        Assert.Contains("data.steps.missing.value", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsSwitchBranchStepOutputMappingAfterSwitch()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       workflows:
                         main:
                           steps:
                             - id: classify
                               type: set
                               input:
                                 classification: question
                             - id: route_action
                               type: switch
                               cases:
                                 - when: "${data.steps.classify.classification == 'question'}"
                                   steps:
                                     - id: set_question_result
                                       type: set
                                       input:
                                         pr_link: "N/A"
                                 - when: "${data.steps.classify.classification == 'bug'}"
                                   steps:
                                     - id: set_fix_result
                                       type: set
                                       input:
                                         pr_link: "https://example.test/pr/1"
                               default:
                                 - id: set_complex_result
                                   type: set
                                   input:
                                     pr_link: "N/A"
                             - id: map_result
                               type: set
                               input:
                                 pr_link: "${coalesce(data.steps.set_fix_result.pr_link, data.steps.set_question_result.pr_link, data.steps.set_complex_result.pr_link, 'N/A')}"
                       """
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a branching workflow
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("STEP_REFERENCE_NOT_AVAILABLE", result.Error.Message);
        Assert.Contains("data.steps.set_fix_result.pr_link", result.Error.Message);
        Assert.Contains("data.steps.set_question_result.pr_link", result.Error.Message);
        Assert.Contains("data.steps.set_complex_result.pr_link", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RepromptCanFixSwitchBranchMapping()
    {
        var prompts = new List<string>();
        var callCount = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => prompts.Add(req.Prompt))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new LLMResponse
                    {
                        Text = """
                               version: 1
                               skill:
                                 description: Generated docs workflow.
                                 tags: [docs]
                                 inputs: {}
                                 outputs: {}
                               workflows:
                                 main:
                                   steps:
                                     - id: classify
                                       type: set
                                       input:
                                         classification: question
                                     - id: route_action
                                       type: switch
                                       cases:
                                         - when: "${data.steps.classify.classification == 'question'}"
                                           steps:
                                             - id: set_question_result
                                               type: set
                                               input:
                                                 pr_link: "N/A"
                                         - when: "${data.steps.classify.classification == 'bug'}"
                                           steps:
                                             - id: set_fix_result
                                               type: set
                                               input:
                                                 pr_link: "https://example.test/pr/1"
                                       default:
                                         - id: set_complex_result
                                           type: set
                                           input:
                                             pr_link: "N/A"
                                     - id: map_result
                                       type: set
                                       input:
                                         pr_link: "${coalesce(data.steps.set_fix_result.pr_link, data.steps.set_question_result.pr_link, data.steps.set_complex_result.pr_link, 'N/A')}"
                               """
                    };
                }

                return new LLMResponse
                {
                    Text = """
                           version: 1
                           skill:
                             description: Generated docs workflow.
                             tags: [docs]
                             inputs: {}
                             outputs: {}
                           workflows:
                             main:
                               steps:
                                 - id: classify
                                   type: set
                                   input:
                                     classification: question
                                 - id: route_action
                                   type: switch
                                   cases:
                                     - when: "${data.steps.classify.classification == 'question'}"
                                       steps:
                                         - id: set_question_result
                                           type: set
                                           output: branch_result
                                           input:
                                             pr_link: "N/A"
                                     - when: "${data.steps.classify.classification == 'bug'}"
                                       steps:
                                         - id: set_fix_result
                                           type: set
                                           output: branch_result
                                           input:
                                             pr_link: "https://example.test/pr/1"
                                   default:
                                     - id: set_complex_result
                                       type: set
                                       output: branch_result
                                       input:
                                         pr_link: "N/A"
                                 - id: map_result
                                   type: set
                                   input:
                                     pr_link: "${data.branch_result.pr_link}"
                           """
                };
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a branching workflow
          on_invalid:
            action: reprompt
            max_attempts: 2
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, prompts.Count);
        Assert.Contains("SEMANTIC_MAPPING_ERROR", prompts[1]);
        Assert.Contains("STEP_REFERENCE_NOT_AVAILABLE", prompts[1]);
        Assert.Contains("set_fix_result", prompts[1]);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RepromptCanFixOpaqueMcpResponseMapping()
    {
        var prompts = new List<string>();
        int callCount = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => prompts.Add(req.Prompt))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new LLMResponse
                    {
                        Text = """
                               version: 1
                               workflows:
                                 main:
                                   steps:
                                     - id: fetch
                                       type: mcp.call
                                       input:
                                         server: docs
                                         method: get_doc
                                     - id: map
                                       type: set
                                       input:
                                         title: "${data.steps.fetch.response.title}"
                               """
                    };
                }

                return new LLMResponse
                {
                    Text = """
                           version: 1
                           skill:
                             description: Generated docs workflow.
                             tags: [docs]
                             inputs: {}
                             outputs: {}
                           workflows:
                             main:
                               steps:
                                 - id: fetch
                                   type: mcp.call
                                   input:
                                     server: docs
                                     method: get_doc
                                 - id: normalize
                                   type: llm.call
                                   input:
                                     model: gpt-4o-mini
                                     prompt: "Normalize this MCP response: ${json(data.steps.fetch.response)}"
                                     structured_output:
                                       schema_inline:
                                         type: object
                                         properties:
                                           title: { type: string }
                                         required: [title]
                                         additionalProperties: false
                                 - id: map
                                   type: set
                                   input:
                                     title: "${data.steps.normalize.json.title}"
                           """
                };
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new() { Name = "get_doc", Description = "Get a document without a declared output contract" }
            }
        });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a docs workflow
            prefilter: false
          on_invalid:
            action: reprompt
            max_attempts: 2
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, prompts.Count);
        Assert.Contains("OPAQUE_RESPONSE_DEEP_ACCESS", prompts[1]);
        Assert.Contains("structured_output", prompts[1]);
    }

    [Fact]
    public async Task WorkflowPlan_StripMarkdownFences()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = "```yaml\nversion: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text\n```"
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: test
          validate:
            compile: false
");
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        var planOutput = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(planOutput);
        var yaml = planOutput!["yaml"]?.GetValue<string>();
        Assert.NotNull(yaml);
        Assert.DoesNotContain("```", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PromptContainsAvailableMcpServers()
    {
        string? capturedPrompt = null;
        var longSchemaDescription = new string('x', 620) + " schema-tail-marker";

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new LLMResponse
            {
                Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Description = "GitHub repository automation and file operations",
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "list_repos",
                    Description = "List repositories for a user",
                    InputSchema = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["user"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = longSchemaDescription
                            }
                        },
                        ["required"] = new JsonArray("user")
                    },
                    OutputSchema = JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"repositories\":{\"type\":\"array\"}},\"additionalProperties\":false}"),
                    ExampleResponse = JsonNode.Parse("{\"repositories\":[{\"name\":\"demo\"}]}")
                },
                new() { Name = "get_file", Description = "Get file contents from a repo" }
            },
            Prompts = new List<McpPromptInfo>
            {
                new() { Name = "summarize_repo", Description = "Summarize a repository", Arguments = new List<McpPromptArgument> { new() { Name = "repo", Required = true } } }
            }
        });
        mcpFactory.RegisterServer("weather", new MockMcpServerConfig
        {
            Description = "Weather forecasts and city conditions",
            Tools = new List<McpToolInfo>
            {
                new() { Name = "get_weather", Description = "Get weather for a city", InputSchema = System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}},\"required\":[\"city\"]}") }
            }
        });

        var wf = CompileMain(@"
 version: 1
 workflows:
   main:
     steps:
       - id: plan
         type: workflow.plan
         input:
           generator:
             model: gpt-4
             instruction: Build an MCP workflow
           validate:
             compile: false
 ");
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            McpClientFactory = mcpFactory
        };
        await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("<available_mcp_servers>", capturedPrompt);
        Assert.Contains("</available_mcp_servers>", capturedPrompt);
        Assert.Contains("Use the exact server name in mcp.call input.server and in mcp.list input.servers.", capturedPrompt);

        // Tool discovery: tool names and descriptions should appear
        Assert.Contains("- github: GitHub repository automation and file operations", capturedPrompt);
        Assert.Contains("list_repos", capturedPrompt);
        Assert.Contains("List repositories for a user", capturedPrompt);
        Assert.Contains("get_file", capturedPrompt);
        Assert.Contains("input_schema_json:", capturedPrompt);
        Assert.Contains("output_schema_json:", capturedPrompt);
        Assert.Contains("example_response_json:", capturedPrompt);
        Assert.DoesNotContain("```", capturedPrompt);
        Assert.Contains("\"type\": \"string\"", capturedPrompt);
        Assert.Contains("schema-tail-marker", capturedPrompt);
        Assert.DoesNotContain("schema-tail-marker…", capturedPrompt);
        Assert.Contains("repositories", capturedPrompt);
        Assert.Contains("- weather: Weather forecasts and city conditions", capturedPrompt);
        Assert.Contains("get_weather", capturedPrompt);

        // Prompt discovery
        Assert.Contains("summarize_repo", capturedPrompt);
        Assert.Contains("repo (required)", capturedPrompt);

        // Preferred direct-call guidance when tools are discovered
        Assert.Contains("Preferred MCP planning pattern: when tool names and input schemas are listed above, use `mcp.call` directly with explicit `method` and `request`", capturedPrompt);

        // MCP output access guidance
        Assert.Contains("<mcp_output_access>", capturedPrompt);
        Assert.Contains("</mcp_output_access>", capturedPrompt);
        Assert.Contains("data.steps.<id>.status", capturedPrompt);
        Assert.Contains("data.steps.<id>.response", capturedPrompt);
        Assert.Contains("`response` value is opaque, tool-specific JSON", capturedPrompt);

        // General guidance still present
        Assert.Contains("If the exact tool or prompt name is unknown, use mcp.list first", capturedPrompt);
        Assert.Contains("When using LLM-assisted mcp.call, put the natural-language instruction in input.prompt", capturedPrompt);
        Assert.Contains("Do NOT generate mcp.call with only input.server as the default plan.", capturedPrompt);
    }

    [Fact]
    public async Task WorkflowPlan_PromptContainsMcpOutputAccessGuidance()
    {
        string? capturedPrompt = null;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new LLMResponse
            {
                Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("test-server", new MockMcpServerConfig
        {
            Description = "Test server",
            Tools = new List<McpToolInfo>
            {
                new() { Name = "do_something", Description = "Does something" }
            }
        });

        var wf = CompileMain(@"
 version: 1
 workflows:
   main:
     steps:
       - id: plan
         type: workflow.plan
         input:
           generator:
             model: gpt-4
             instruction: test
           validate:
             compile: false
 ");
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            McpClientFactory = mcpFactory
        };
        await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("<mcp_output_access>", capturedPrompt);
        Assert.Contains("mcp.call single-tool output shape:", capturedPrompt);
        Assert.Contains("status: \"ok\"|\"error\", response: tool-specific JSON", capturedPrompt);
        Assert.Contains("data.steps.<id>.status", capturedPrompt);
        Assert.Contains("data.steps.<id>.response", capturedPrompt);
        Assert.Contains("Do NOT assume field names inside `response`", capturedPrompt);
        Assert.Contains("json(data.steps.<id>.response)", capturedPrompt);
        Assert.Contains("data.steps.<id>.results", capturedPrompt);
        Assert.Contains("data.steps.<id>.json", capturedPrompt);
        Assert.Contains("data.steps.<id>.text", capturedPrompt);
    }

    [Fact]
    public async Task WorkflowPlan_ServerPrefilter_UsesDescriptionsBeforeCapabilityDiscovery()
    {
        var requests = new List<LLMRequest>();
        var callIndex = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => requests.Add(req))
            .ReturnsAsync(() => (++callIndex) switch
            {
                1 => new LLMResponse
                {
                    Text = "{\"servers\":[{\"name\":\"github\",\"reason\":\"repository task\"}]}",
                    Json = JsonNode.Parse("{\"servers\":[{\"name\":\"github\",\"reason\":\"repository task\"}]}")
                },
                2 => new LLMResponse
                {
                    Text = "{\"servers\":[{\"name\":\"github\",\"tools\":[\"list_repos\"],\"prompts\":[]}]}",
                    Json = JsonNode.Parse("{\"servers\":[{\"name\":\"github\",\"tools\":[\"list_repos\"],\"prompts\":[]}]}")
                },
                _ => new LLMResponse
                {
                    Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
                }
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Description = "GitHub repository automation and file operations",
            Tools = new List<McpToolInfo>
            {
                new() { Name = "list_repos", Description = "List repositories for a user" },
                new() { Name = "delete_repo", Description = "Delete a repository" }
            }
        });
        mcpFactory.RegisterServer("weather", new MockMcpServerConfig
        {
            Description = "Weather forecasts and city conditions",
            Tools = new List<McpToolInfo>
            {
                new() { Name = "get_weather", Description = "Get weather for a city" }
            }
        });

        var wf = CompileMain(@"
 version: 1
 workflows:
   main:
     steps:
       - id: plan
         type: workflow.plan
         input:
           generator:
             model: gpt-4
             instruction: Build a workflow that lists GitHub repositories
           validate:
             compile: false
 ");
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            McpClientFactory = mcpFactory
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, requests.Count);
        Assert.NotNull(requests[0].StructuredOutputSchema);
        Assert.True(requests[0].StructuredOutputStrict);
        Assert.Null(requests[0].Temperature);
        Assert.Contains("<server_catalog>", requests[0].Prompt);
        Assert.Contains("</server_catalog>", requests[0].Prompt);
        Assert.Contains("GitHub repository automation", requests[0].Prompt);
        Assert.Contains("Weather forecasts", requests[0].Prompt);
        Assert.DoesNotContain("list_repos", requests[0].Prompt);
        Assert.Contains("<user_prompt>", requests[0].Prompt);
        Assert.Contains("Build a workflow that lists GitHub repositories", requests[0].Prompt);
        Assert.Contains("</user_prompt>", requests[0].Prompt);
        Assert.DoesNotContain("Instruction: Build a workflow that lists GitHub repositories", requests[0].Prompt);
        Assert.NotNull(requests[1].StructuredOutputSchema);
        Assert.Null(requests[1].Temperature);
        Assert.Contains("<user_prompt>", requests[1].Prompt);
        Assert.Contains("Build a workflow that lists GitHub repositories", requests[1].Prompt);
        Assert.Contains("</user_prompt>", requests[1].Prompt);
        Assert.DoesNotContain("Instruction: Build a workflow that lists GitHub repositories", requests[1].Prompt);
        var mcpSectionStart = requests[2].Prompt.LastIndexOf("<available_mcp_servers>", StringComparison.Ordinal);
        var mcpSectionEnd = requests[2].Prompt.IndexOf("<mcp_output_access>", mcpSectionStart, StringComparison.Ordinal);
        var mcpSection = requests[2].Prompt[mcpSectionStart..mcpSectionEnd];
        Assert.Contains("list_repos", mcpSection);
        Assert.DoesNotContain("get_weather", mcpSection);
        Assert.DoesNotContain("delete_repo", mcpSection);
    }

    [Fact]
    public async Task WorkflowPlan_ServerPrefilter_ForceIncludesServersReferencedByCurrentWorkflowText()
    {
        var requests = new List<LLMRequest>();
        var callIndex = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => requests.Add(req))
            .ReturnsAsync(() => (++callIndex) switch
            {
                1 => new LLMResponse
                {
                    Text = "{\"servers\":[{\"name\":\"Github\",\"reason\":\"issue context\"}]}",
                    Json = JsonNode.Parse("{\"servers\":[{\"name\":\"Github\",\"reason\":\"issue context\"}]}")
                },
                2 => new LLMResponse
                {
                    Text = "{\"servers\":[{\"name\":\"Github\",\"tools\":[\"github_issue_search\"],\"prompts\":[]}]}",
                    Json = JsonNode.Parse("{\"servers\":[{\"name\":\"Github\",\"tools\":[\"github_issue_search\"],\"prompts\":[]}]}")
                },
                _ => new LLMResponse
                {
                    Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
                }
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("Github", new MockMcpServerConfig
        {
            Description = "GitHub repository automation",
            Tools = new List<McpToolInfo>
            {
                new() { Name = "github_issue_search", Description = "Search GitHub issues" }
            }
        });
        mcpFactory.RegisterServer("GnOuGo.Document.Mcp", new MockMcpServerConfig
        {
            Description = "Document generation",
            Tools = new List<McpToolInfo>
            {
                new() { Name = "document_create", Description = "Create a document" }
            }
        });
        mcpFactory.RegisterServer("Weather", new MockMcpServerConfig
        {
            Description = "Weather forecasts",
            Tools = new List<McpToolInfo>
            {
                new() { Name = "get_weather", Description = "Get weather" }
            }
        });

        var wf = CompileMain("""
 version: 1
 workflows:
   main:
     steps:
       - id: plan
         type: workflow.plan
         input:
           generator:
             model: gpt-4
             instruction: |
               Repair this workflow after a GitHub issue run failed.
               <current_workflow_yaml>
               version: 1
               workflows:
                 main:
                   steps:
                     - id: create_doc
                       type: mcp.call
                       input:
                         server: GnOuGo.Document.Mcp
                         method: document_create
                         request: {}
               </current_workflow_yaml>
           validate:
             compile: false
 """);
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            McpClientFactory = mcpFactory
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, requests.Count);
        var mcpSectionStart = requests[2].Prompt.LastIndexOf("<available_mcp_servers>", StringComparison.Ordinal);
        var mcpSectionEnd = requests[2].Prompt.IndexOf("<mcp_output_access>", mcpSectionStart, StringComparison.Ordinal);
        var mcpSection = requests[2].Prompt[mcpSectionStart..mcpSectionEnd];
        Assert.Contains("Github", mcpSection);
        Assert.Contains("github_issue_search", mcpSection);
        Assert.Contains("GnOuGo.Document.Mcp", mcpSection);
        Assert.Contains("document_create", mcpSection);
        Assert.DoesNotContain("Weather", mcpSection);
        Assert.DoesNotContain("get_weather", mcpSection);
    }

    [Fact]
    public async Task WorkflowPlan_ServerPrefilter_UsesExplicitTemperatureOnlyWhenConfigured()
    {
        var requests = new List<LLMRequest>();
        var callIndex = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => requests.Add(req))
            .ReturnsAsync(() => (++callIndex) switch
            {
                1 => new LLMResponse
                {
                    Text = "{\"servers\":[{\"name\":\"github\",\"reason\":\"repository task\"}]}",
                    Json = JsonNode.Parse("{\"servers\":[{\"name\":\"github\",\"reason\":\"repository task\"}]}")
                },
                2 => new LLMResponse
                {
                    Text = "{\"servers\":[{\"name\":\"github\",\"tools\":[\"list_repos\"],\"prompts\":[]}]}",
                    Json = JsonNode.Parse("{\"servers\":[{\"name\":\"github\",\"tools\":[\"list_repos\"],\"prompts\":[]}]}")
                },
                _ => new LLMResponse
                {
                    Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: template.render\n        input:\n          engine: mustache\n          template: ok\n          mode: text"
                }
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Description = "GitHub repository automation and file operations",
            Tools = new List<McpToolInfo>
            {
                new() { Name = "list_repos", Description = "List repositories for a user" }
            }
        });

        var wf = CompileMain(@"
 version: 1
 workflows:
   main:
     steps:
       - id: plan
         type: workflow.plan
         input:
           generator:
             model: gpt-4
             instruction: Build a workflow that lists GitHub repositories
             prefilter:
               temperature: 1.0
           validate:
             compile: false
 ");
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            McpClientFactory = mcpFactory
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, requests.Count);
        Assert.Equal(1.0, requests[0].Temperature);
        Assert.Equal(1.0, requests[1].Temperature);
        Assert.Null(requests[2].Temperature);
    }

    [Fact]
    public void McpExecutors_DslSnippets_ContainDiscoveryGuidance()
    {
        var engine = new WorkflowEngine();

        var mcpCallSnippet = engine.Registry.Get("mcp.call")?.DslSnippet;
        Assert.NotNull(mcpCallSnippet);
        Assert.Contains("Direct MCP call pattern (preferred when tool names are known", mcpCallSnippet);
        Assert.Contains("use `mcp.call` directly with explicit `method` and `request`", mcpCallSnippet);
        Assert.Contains("Fallback: discover candidate servers -> choose one server -> use `mcp.list`", mcpCallSnippet);
        Assert.Contains("Direct mode keeps the generic `request` object contract for both tools and prompts.", mcpCallSnippet);
        Assert.Contains("Even when `kind: prompt`, `request` contains the named prompt arguments expected by the MCP server", mcpCallSnippet);
        Assert.Contains("Single prompt call:", mcpCallSnippet);
        Assert.Contains("request: { text: \"Long document here\" }", mcpCallSnippet);
        Assert.Contains("do NOT use `mcp.call` with only `server` as the default next step after `mcp.list`", mcpCallSnippet);
        Assert.Contains("use `mcp.list` first -> pass `tools` and/or `prompts` from that step into `mcp.call`", mcpCallSnippet);
        Assert.Contains("For generated plans, do NOT use `mcp.call` with only `server` as the default next step after `mcp.list` unless calling everything is the explicit goal.", mcpCallSnippet);
        // Output access patterns
        Assert.Contains("Output access patterns:", mcpCallSnippet);
        Assert.Contains("data.steps.<id>.status", mcpCallSnippet);
        Assert.Contains("data.steps.<id>.response", mcpCallSnippet);
        Assert.Contains("json(data.steps.<id>.response)", mcpCallSnippet);

        var mcpListSnippet = engine.Registry.Get("mcp.list")?.DslSnippet;
        Assert.NotNull(mcpListSnippet);
        Assert.Contains("select the exact tool/prompt -> build the request arguments -> use `mcp.call`", mcpListSnippet);
        Assert.Contains("can be passed directly into `mcp.call.input.tools` and/or `mcp.call.input.prompts`", mcpListSnippet);
        Assert.Contains("Do not go directly from `mcp.list` to `mcp.call` with only `server`", mcpListSnippet);

        var llmCallSnippet = engine.Registry.Get("llm.call")?.DslSnippet;
        Assert.NotNull(llmCallSnippet);
        Assert.Contains("Structured output:", llmCallSnippet);
        Assert.Contains("structured_output:", llmCallSnippet);
        Assert.Contains("schema_inline:", llmCallSnippet);
        Assert.Contains("strict: true", llmCallSnippet);
        Assert.Contains("Every schema object with `properties` MUST have `required` listing EVERY key from `properties`", llmCallSnippet);
        Assert.Contains("Optional fields must still be listed in `required`", llmCallSnippet);
        Assert.Contains("additionalProperties: false", llmCallSnippet);
        Assert.Contains("anyOf:", llmCallSnippet);
        Assert.Contains("structured_output.schema_ref", llmCallSnippet);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_ParsesAndValidates()
    {
        var manifest = WorkflowPlanManifestParser.Parse(SplitManifestYaml());

        WorkflowPlanManifestValidator.Validate(manifest);

        Assert.Equal("split-demo", manifest.Name);
        Assert.Equal(2, manifest.SubPlans.Count);
        Assert.Equal("collect", manifest.SubPlans[0].Id);
        Assert.Equal("sequence", manifest.Algorithm.Type);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_AllowsSubPlansWithoutInputsOrOutputs()
    {
        var manifest = WorkflowPlanManifestParser.Parse("""
name: contract-free-subplans
description: Keeps contracts only on the main workflow manifest.
inputs:
  topic: { type: string, required: true }
outputs:
  result: "${data.steps.call_leaf.outputs.result}"
subplans:
  - id: leaf
    responsibility: Generate a useful answer for the topic.
    inputs:
      topic: { type: string, required: true }
    outputs:
      result: { type: string }
    constraints:
      implementation_detail: should_be_removed
algorithm:
  type: workflow.call
  id: call_leaf
  plan: leaf
  args:
    topic: "${data.inputs.topic}"
""");

        manifest = WorkflowPlanManifestNormalizer.Normalize(manifest, null, null);
        WorkflowPlanManifestValidator.Validate(manifest);
        var normalizedYaml = WorkflowPlanManifestCompiler.CompileManifestYaml(manifest);
        var compactYaml = WorkflowPlanManifestCompiler.CompileManifestYaml(manifest, includeRuntimePaths: false);

        Assert.Contains("inputs:", normalizedYaml);
        Assert.Contains("outputs:", normalizedYaml);
        Assert.Contains("path: \"./contract-free-subplans/leaf.yaml\"", normalizedYaml);
        Assert.DoesNotContain("    inputs:", normalizedYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("    outputs:", normalizedYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("constraints:", normalizedYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("path:", compactYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("constraints:", compactYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_AcceptsPromptAliasForSubPlanResponsibility()
    {
        var manifest = WorkflowPlanManifestParser.Parse("""
name: prompt-alias
description: Demonstrates prompt-based sub-plan descriptions.
subplans:
  - id: answer_issue
    prompt: |
      Answer the GitHub issue in English.
      Never modify an existing issue comment.
algorithm:
  type: workflow.call
  plan: answer_issue
""");

        manifest = WorkflowPlanManifestNormalizer.Normalize(manifest, null, null);

        WorkflowPlanManifestValidator.Validate(manifest);
        Assert.Contains("Answer the GitHub issue in English.", manifest.SubPlans.Single().Responsibility);
        Assert.Contains("Never modify an existing issue comment.", manifest.SubPlans.Single().Responsibility);
    }

    [Fact]
    public void WorkflowPlanSplitManifestPrompt_TreatsSubPlanResponsibilityAsGenerationPrompt()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "BuildSplitManifestPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var prompt = (string)method.Invoke(null, ["Build an issue workflow", null])!;

        Assert.Contains("The subplan responsibility is not a label", prompt);
        Assert.Contains("generation prompt for that sub-workflow", prompt);
        Assert.Contains("preserves the exact user-requested behavior", prompt);
        Assert.Contains("Do not invent details, weaken constraints", prompt);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_AllowsInlineTasksInMainAlgorithm()
    {
        var manifest = WorkflowPlanManifestParser.Parse("""
name: inline-task-demo
description: Uses inline orchestration tasks for simple work.
inputs:
  issue_count: { type: number, required: true }
subplans:
  - id: process_issue_lifecycle
    responsibility: Process one issue through classification, action, logging, and cleanup.
algorithm:
  type: sequence
  id: orchestrate
  steps:
    - type: task
      id: list_recent_open_issues
      task: List the most recent open issues and keep only the requested count.
    - type: task
      id: filter_already_handled_issues
      responsibility: Remove issues already handled by GnOuGo.
    - type: foreach.sequential
      id: each_issue
      items: remaining_issues
      item_var: issue
      steps:
        - type: workflow.call
          id: call_process_issue_lifecycle
          plan: process_issue_lifecycle
""");

        manifest = WorkflowPlanManifestNormalizer.Normalize(manifest, null, null);
        WorkflowPlanManifestValidator.Validate(manifest);

        var manifestYaml = WorkflowPlanManifestCompiler.CompileManifestYaml(manifest, includeRuntimePaths: false);
        var mermaid = WorkflowPlanManifestCompiler.CompileMermaid(manifest);
        var mainYaml = WorkflowPlanManifestCompiler.CompileMainYaml(manifest);
        var doc = WorkflowParser.Parse(mainYaml);
        var errors = new WorkflowValidator().Validate(doc);
        var steps = doc.Workflows["main"].Steps;

        Assert.Empty(errors);
        Assert.Contains("type: \"task\"", manifestYaml);
        Assert.Contains("task: \"List the most recent open issues", manifestYaml);
        Assert.Contains("Note over Main: List the most recent open issues", mermaid);
        Assert.Equal("set", steps[0].Type);
        Assert.Equal("list_recent_open_issues", steps[0].Id);
        Assert.Equal("set", steps[1].Type);
        Assert.IsType<JsonArray>(steps[1].Input!["remaining_issues"]);
        Assert.Equal("loop.sequential", steps[2].Type);
        Assert.Equal("${data.steps.filter_already_handled_issues.remaining_issues}", steps[2].Input!["items"]!.GetValue<string>());
        Assert.Contains("type: \"workflow.call\"", mainYaml);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_InfersAlgorithmTypesWhenLlmOmitsType()
    {
        var manifest = WorkflowPlanManifestParser.Parse("""
name: inferred-types
description: Handles issue processing with omitted algorithm node types.
subplans:
  - id: handle_single_issue_lifecycle
    responsibility: Handle one issue lifecycle.
    algorithm:
      type: sequence
      steps:
        - id: classify_issue
          task: Classify the issue.
        - id: route_action
          cases:
            - when: "${data.steps.classify_issue.kind == 'question'}"
              steps:
                - id: answer_question
                  task: Answer and close the question issue.
          default:
            steps:
              - id: implement_change
                plan: implement_change_and_pr
        - id: summarize_issue
          description: Summarize the issue result.
  - id: implement_change_and_pr
    responsibility: Implement a code change and open a pull request.
algorithm:
  type: sequence
  steps:
    - id: list_recent_open_issues
      task: List recent open issues.
    - id: each_issue
      items: "${data.steps.list_recent_open_issues.issues}"
      item_var: issue
      steps:
        - id: call_single_issue
          plan: handle_single_issue_lifecycle
""");

        manifest = WorkflowPlanManifestNormalizer.Normalize(manifest, null, null);

        WorkflowPlanManifestValidator.Validate(manifest);
        var lifecycle = manifest.SubPlans.Single(plan => plan.Id == "handle_single_issue_lifecycle");
        var mainYaml = WorkflowPlanManifestCompiler.CompileMainYaml(manifest);
        var doc = WorkflowParser.Parse(mainYaml);
        var errors = new WorkflowValidator().Validate(doc);

        Assert.Empty(errors);
        Assert.Equal("task", manifest.Algorithm.Steps[0].Type);
        Assert.Equal("foreach.sequential", manifest.Algorithm.Steps[1].Type);
        Assert.Equal("workflow.call", manifest.Algorithm.Steps[1].Steps[0].Type);
        Assert.Equal("task", lifecycle.Algorithm!.Steps[0].Type);
        Assert.Equal("switch", lifecycle.Algorithm.Steps[1].Type);
        Assert.Equal("task", lifecycle.Algorithm.Steps[1].Cases[0].Step.Steps[0].Type);
        Assert.Equal("workflow.call", lifecycle.Algorithm.Steps[1].Default!.Steps[0].Type);
        Assert.Equal("task", lifecycle.Algorithm.Steps[2].Type);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_RejectsDuplicateIdsUnsafePathsAndUnknownPlan()
    {
        var manifest = WorkflowPlanManifestParser.Parse("""
name: bad
inputs: { topic: { type: string, required: true } }
outputs: { final: "${data.steps.call.outputs.result}" }
subplans:
  - id: same
    path: ../bad/workflow.yaml
    responsibility: one
    inputs: { topic: { type: string } }
    outputs: { result: { type: string } }
  - id: same
    path: ./bad/two/workflow.yaml
    responsibility: two
    inputs: { topic: { type: string } }
    outputs: { result: { type: string } }
algorithm:
  type: workflow.call
  plan: missing
""");

        var ex = Assert.Throws<WorkflowPlanManifestValidationException>(() => WorkflowPlanManifestValidator.Validate(manifest));

        Assert.Contains(ex.Errors, error => error.Contains("duplicate subplan id", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, error => error.Contains("path must be safe", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, error => error.Contains("undeclared subplan", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkflowPlanSplitManifest_CompilesMainWorkflowWithRelativeWorkspaceCalls()
    {
        var manifest = WorkflowPlanManifestParser.Parse(SplitManifestYaml());
        WorkflowPlanManifestValidator.Validate(manifest);

        var mainYaml = WorkflowPlanManifestCompiler.CompileMainYaml(manifest);
        var doc = WorkflowParser.Parse(mainYaml);
        var errors = new WorkflowValidator().Validate(doc);

        Assert.Empty(errors);
        Assert.Contains("kind: \"workspace\"", mainYaml);
        Assert.Contains("name: \"split-demo\"", mainYaml);
        Assert.Contains("description: \"Researches a topic and summarizes the result.\"", mainYaml);
        Assert.Contains("path: \"./split-demo/collect.yaml\"", mainYaml);
        Assert.Contains("path: \"./split-demo/summarize.yaml\"", mainYaml);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_CompilesForeachParallelAndSwitch()
    {
        var manifest = WorkflowPlanManifestParser.Parse("""
name: split-control
description: Processes each item and routes fallback work by mode.
inputs:
  items: { type: array, required: true }
  mode: { type: string, required: true }
outputs:
  final: "${data.steps.route}"
subplans:
  - id: item_worker
    path: ./split-control/item_worker.yaml
    responsibility: Process one item.
    inputs: { item: { type: any } }
    outputs: { result: { type: any } }
  - id: fallback
    path: ./split-control/fallback.yaml
    responsibility: Fallback work.
    inputs: { mode: { type: string } }
    outputs: { result: { type: any } }
algorithm:
  type: switch
  id: route
  expr: "${data.inputs.mode}"
  cases:
    - value: batch
      step:
        type: parallel
        id: both
        branches:
          - type: foreach.sequential
            id: each_seq
            items: "${data.inputs.items}"
            item_var: item
            steps:
              - type: workflow.call
                id: call_item_seq
                plan: item_worker
                args: { item: "${data.item}" }
          - type: foreach.parallel
            id: each_par
            items: "${data.inputs.items}"
            item_var: item
            steps:
              - type: workflow.call
                id: call_item_par
                plan: item_worker
                args: { item: "${data.item}" }
  default:
    type: workflow.call
    id: call_fallback
    plan: fallback
    args: { mode: "${data.inputs.mode}" }
""");

        WorkflowPlanManifestValidator.Validate(manifest);
        var mainYaml = WorkflowPlanManifestCompiler.CompileMainYaml(manifest);
        var doc = WorkflowParser.Parse(mainYaml);
        var compiled = new WorkflowCompiler().Compile(doc);
        var route = doc.Workflows["main"].Steps.Single();
        var parallel = route.Cases![0].Steps.Single();
        var sequentialLoop = parallel.Branches![0].Steps.Single();
        var parallelLoop = parallel.Branches![1].Steps.Single();

        Assert.NotNull(compiled);
        Assert.Equal("switch", route.Type);
        Assert.Equal("parallel", parallel.Type);
        Assert.Equal("loop.sequential", sequentialLoop.Type);
        Assert.Equal("loop.parallel", parallelLoop.Type);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_CompilesCompositeSubPlanWorkflowCalls()
    {
        var manifest = WorkflowPlanManifestParser.Parse(CompositeSplitManifestYaml());

        WorkflowPlanManifestValidator.Validate(manifest);
        var reportPlan = manifest.SubPlans.Single(plan => plan.Id == "report");
        var reportYaml = WorkflowPlanManifestCompiler.CompileSubPlanYaml(manifest, reportPlan);
        var doc = WorkflowParser.Parse(reportYaml);
        var errors = new WorkflowValidator().Validate(doc);

        Assert.Empty(errors);
        Assert.Contains("name: \"split-composite-report\"", reportYaml);
        Assert.Contains("path: \"./split-composite/collect.yaml\"", reportYaml);
        Assert.Contains("path: \"./split-composite/summarize.yaml\"", reportYaml);
        Assert.Contains("expr: \"${data.steps.call_summarize.outputs.result}\"", reportYaml);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_BuildsDependencyGenerationBatches()
    {
        var manifest = WorkflowPlanManifestParser.Parse(CompositeSplitManifestYaml());
        WorkflowPlanManifestValidator.Validate(manifest);

        var batches = WorkflowPlanManifestDependencyPlanner.BuildGenerationBatches(manifest);

        Assert.Equal(2, batches.Count);
        Assert.Equal(["collect", "summarize"], batches[0].Select(static plan => plan.Id).ToArray());
        Assert.Equal(["report"], batches[1].Select(static plan => plan.Id).ToArray());
    }

    [Fact]
    public void WorkflowPlanSplitManifest_BuildsTransitiveDependencyGenerationBatches()
    {
        var manifest = WorkflowPlanManifestParser.Parse("""
name: transitive
description: Demonstrates transitive sub-plan generation order.
inputs:
  topic: { type: string, required: true }
outputs:
  result: "${data.steps.call_parent.outputs.result}"
subplans:
  - id: leaf
    path: ./transitive/leaf.yaml
    responsibility: Leaf work.
    inputs: { topic: { type: string } }
    outputs: { result: { type: string } }
  - id: parent
    path: ./transitive/parent.yaml
    responsibility: Parent work.
    inputs: { topic: { type: string } }
    outputs: { result: { type: string } }
    algorithm:
      type: workflow.call
      id: call_leaf
      plan: leaf
      args: { topic: "${data.inputs.topic}" }
  - id: grandparent
    path: ./transitive/grandparent.yaml
    responsibility: Grandparent work.
    inputs: { topic: { type: string } }
    outputs: { result: { type: string } }
    algorithm:
      type: workflow.call
      id: call_parent
      plan: parent
      args: { topic: "${data.inputs.topic}" }
algorithm:
  type: workflow.call
  id: call_parent
  plan: grandparent
  args: { topic: "${data.inputs.topic}" }
""");
        WorkflowPlanManifestValidator.Validate(manifest);

        var batches = WorkflowPlanManifestDependencyPlanner.BuildGenerationBatches(manifest);

        Assert.Equal(3, batches.Count);
        Assert.Equal(["leaf"], batches[0].Select(static plan => plan.Id).ToArray());
        Assert.Equal(["parent"], batches[1].Select(static plan => plan.Id).ToArray());
        Assert.Equal(["grandparent"], batches[2].Select(static plan => plan.Id).ToArray());
    }

    [Fact]
    public void WorkflowPlanSplitManifest_NormalizesEmptySubPlanAlgorithmsToLeafSubPlans()
    {
        var manifest = WorkflowPlanManifestParser.Parse("""
name: empty-subplan-algorithm
description: Handles empty sub-plan algorithm placeholders.
inputs:
  topic: { type: string, required: true }
outputs:
  result: "${data.steps.call_leaf.outputs.result}"
subplans:
  - id: leaf
    path: ./empty-subplan-algorithm/leaf.yaml
    responsibility: Generate this leaf workflow with the normal workflow planner.
    inputs: { topic: { type: string } }
    outputs: { result: { type: string } }
    algorithm:
      type: sequence
algorithm:
  type: workflow.call
  id: call_leaf
  plan: leaf
  args: { topic: "${data.inputs.topic}" }
""");

        manifest = WorkflowPlanManifestNormalizer.Normalize(manifest, null, null);

        WorkflowPlanManifestValidator.Validate(manifest);
        Assert.Null(manifest.SubPlans.Single().Algorithm);

        var normalizedManifestYaml = WorkflowPlanManifestCompiler.CompileManifestYaml(manifest);
        Assert.DoesNotContain("    algorithm:", normalizedManifestYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_NormalizesEmptySwitchBranchesAndRemovesManifestArgs()
    {
        var manifest = WorkflowPlanManifestParser.Parse("""
name: empty-switch-branches
description: Handles empty branches produced by the split manifest LLM.
inputs:
  repo_url: { type: string, required: false }
outputs:
  results: "${data.steps.run.outputs.results}"
subplans:
  - id: classify
    path: ./empty-switch-branches/classify.yaml
    responsibility: Classify one issue.
  - id: fix
    path: ./empty-switch-branches/fix.yaml
    responsibility: Fix one issue when needed.
  - id: process-single-issue
    path: ./empty-switch-branches/process-single-issue.yaml
    responsibility: Process one issue and call children when needed.
    algorithm:
      type: sequence
      steps:
        - type: workflow.call
          id: classify_issue
          plan: classify
          args:
            issue: "${inputs.issue}"
        - type: switch
          id: route_issue
          expr: "${data.steps.classify_issue.outputs.category}"
          cases:
            - when: bug
              steps:
                - type: switch
                  expr: "${data.steps.classify_issue.outputs.feasibility}"
                  cases:
                    - when: simple
                      steps:
                        - type: workflow.call
                          id: handle_bug
                          plan: fix
                          args:
                            issue: "${inputs.issue}"
                  default:
                    steps:
            - when: question
              steps:
            - when: already_resolved
              steps:
          default:
            steps:
algorithm:
  type: workflow.call
  id: run
  plan: process-single-issue
  args:
    repo_url: "${inputs.repo_url}"
""");

        manifest = WorkflowPlanManifestNormalizer.Normalize(manifest, null, null);

        WorkflowPlanManifestValidator.Validate(manifest);
        var process = manifest.SubPlans.Single(plan => plan.Id == "process-single-issue");
        var route = process.Algorithm!.Steps[1];
        Assert.Equal("switch", route.Type);
        Assert.Single(route.Cases);
        Assert.Null(route.Default);

        var normalizedManifestYaml = WorkflowPlanManifestCompiler.CompileManifestYaml(manifest);
        var algorithmYaml = WorkflowPlanManifestCompiler.CompileAlgorithmYaml(process.Algorithm);
        Assert.DoesNotContain("args:", normalizedManifestYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("args:", algorithmYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_ParsesSwitchDefaultStepsAsSequence()
    {
        var manifest = WorkflowPlanManifestParser.Parse("""
name: default-steps
description: Handles switch default shorthand.
inputs:
  kind: { type: string, required: true }
outputs:
  result: "${data.steps.call_router.outputs.result}"
subplans:
  - id: router
    path: ./default-steps/router.yaml
    responsibility: Route work by kind.
    inputs: { kind: { type: string } }
    outputs: { result: { type: string } }
    algorithm:
      type: switch
      expr: "${data.inputs.kind}"
      cases:
        - value: known
          steps:
            - type: workflow.call
              id: call_known
              plan: known
              args: { kind: "${data.inputs.kind}" }
      default:
        steps:
          - type: workflow.call
            id: call_unknown
            plan: unknown
            args: { kind: "${data.inputs.kind}" }
  - id: known
    path: ./default-steps/known.yaml
    responsibility: Handle known work.
    inputs: { kind: { type: string } }
    outputs: { result: { type: string } }
  - id: unknown
    path: ./default-steps/unknown.yaml
    responsibility: Handle unknown work.
    inputs: { kind: { type: string } }
    outputs: { result: { type: string } }
algorithm:
  type: workflow.call
  id: call_router
  plan: router
  args: { kind: "${data.inputs.kind}" }
""");

        WorkflowPlanManifestValidator.Validate(manifest);
        var router = manifest.SubPlans.Single(plan => plan.Id == "router");
        Assert.Equal("sequence", router.Algorithm!.Default!.Type);

        var yaml = WorkflowPlanManifestCompiler.CompileSubPlanYaml(manifest, router);
        Assert.Contains("default:", yaml);
        Assert.Contains("id: \"call_unknown\"", yaml);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_CompilesSwitchCaseOutputsWithoutChildSteps()
    {
        var manifest = WorkflowPlanManifestParser.Parse("""
name: case-outputs
description: Handles switch cases that only publish outputs.
inputs:
  items: { type: array, required: true }
outputs:
  result: "${data.steps.call_loop.outputs.result}"
subplans:
  - id: loop
    path: ./case-outputs/loop.yaml
    responsibility: Process items and publish aggregate outputs.
    inputs: { items: { type: array } }
    outputs:
      result: { type: string }
    algorithm:
      type: sequence
      steps:
        - type: foreach.sequential
          id: each_item
          items: "${data.inputs.items}"
          item_var: item
          steps:
            - type: workflow.call
              id: process_item
              plan: leaf
              args: { item: "${data.item}" }
            - type: switch
              id: update_context
              cases:
                - when: "true"
                  steps:
                  outputs:
                    result:
                      expr: "${data.steps.process_item.outputs.result}"
        - type: switch
          id: finalize
          cases:
            - when: "true"
              steps:
              outputs:
                result:
                  expr: "${data.steps.each_item.outputs.result}"
  - id: leaf
    path: ./case-outputs/leaf.yaml
    responsibility: Process one item.
    inputs: { item: { type: any } }
    outputs:
      result: { type: string }
algorithm:
  type: workflow.call
  id: call_loop
  plan: loop
  args: { items: "${data.inputs.items}" }
""");

        WorkflowPlanManifestValidator.Validate(manifest);
        var loop = manifest.SubPlans.Single(plan => plan.Id == "loop");
        var yaml = WorkflowPlanManifestCompiler.CompileSubPlanYaml(manifest, loop);

        Assert.True(yaml.Split("type: \"set\"", StringSplitOptions.None).Length >= 3, yaml);
        Assert.Contains("id: \"set_outputs_", yaml);
        Assert.Contains("expr: \"${data.steps.set_outputs_", yaml);
        Assert.Contains(".result}\"", yaml);
    }

    [Fact]
    public void WorkflowPlanSplitManifest_RejectsCompositeSubPlanCallCycles()
    {
        var manifest = WorkflowPlanManifestParser.Parse("""
name: cyclic
description: Demonstrates invalid cyclic sub-plan calls.
inputs:
  topic: { type: string, required: true }
outputs:
  final: "${data.steps.call_a.outputs.result}"
subplans:
  - id: a
    path: ./cyclic/a.yaml
    responsibility: Call B.
    inputs: { topic: { type: string } }
    outputs: { result: { type: string } }
    algorithm:
      type: workflow.call
      id: call_b
      plan: b
      args: { topic: "${data.inputs.topic}" }
  - id: b
    path: ./cyclic/b.yaml
    responsibility: Call A.
    inputs: { topic: { type: string } }
    outputs: { result: { type: string } }
    algorithm:
      type: workflow.call
      id: call_a
      plan: a
      args: { topic: "${data.inputs.topic}" }
algorithm:
  type: workflow.call
  id: call_a
  plan: a
  args: { topic: "${data.inputs.topic}" }
""");

        var ex = Assert.Throws<WorkflowPlanManifestValidationException>(() => WorkflowPlanManifestValidator.Validate(manifest));

        Assert.Contains(ex.Errors, error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WorkflowPlanSplit_GeneratesBundleAndSubPlansInParallel()
    {
        var activeSubPlanCalls = 0;
        var maxConcurrentSubPlanCalls = 0;
        var requests = new System.Collections.Concurrent.ConcurrentBag<LLMRequest>();

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Returns<LLMRequest, CancellationToken>(async (req, ct) =>
            {
                requests.Add(req);
                if (req.Prompt.Contains("split-planning assistant", StringComparison.Ordinal))
                    return new LLMResponse { Text = SplitManifestYaml() };

                var current = Interlocked.Increment(ref activeSubPlanCalls);
                maxConcurrentSubPlanCalls = Math.Max(maxConcurrentSubPlanCalls, current);
                try
                {
                    await Task.Delay(50, ct);
                    return new LLMResponse { Text = SubPlanYaml(req.Prompt.Contains("'collect'", StringComparison.Ordinal) ? "collect" : "summarize") };
                }
                finally
                {
                    Interlocked.Decrement(ref activeSubPlanCalls);
                }
            });

        var wf = CompileMain("""
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            mode: split
            workflow_name: Test2
            description: Answers product questions for customers.
            model: gpt-4
            instruction: Build a large research workflow
          validate:
            compile: true
            dry_run: true
""");
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var planOutput = result.Outputs!["plan"] as JsonObject;
        Assert.NotNull(planOutput);
        Assert.NotNull(planOutput!["manifest"]);
        Assert.Contains("kind: \"local\"", planOutput["main.yaml"]!.GetValue<string>());
        Assert.DoesNotContain("kind: \"workspace\"", planOutput["main.yaml"]!.GetValue<string>());
        var workflows = planOutput["workflows"] as JsonObject;
        Assert.NotNull(workflows);
        Assert.Contains("name: \"Test2\"", planOutput["main.yaml"]!.GetValue<string>());
        Assert.Contains("description: \"Answers product questions for customers.\"", planOutput["main.yaml"]!.GetValue<string>());
        Assert.Contains("  \"collect\":", planOutput["main.yaml"]!.GetValue<string>());
        Assert.Contains("  \"summarize\":", planOutput["main.yaml"]!.GetValue<string>());
        Assert.True(workflows!.ContainsKey("./Test2/workflow.yaml"));
        Assert.True(workflows.ContainsKey("./Test2/collect.yaml"));
        Assert.True(workflows.ContainsKey("./Test2/summarize.yaml"));
        Assert.Equal(3, requests.Count);
        Assert.True(maxConcurrentSubPlanCalls > 1);
    }

    [Fact]
    public async Task WorkflowPlanSplit_SubPlanPromptPreservesOriginalUserLogic()
    {
        var requests = new System.Collections.Concurrent.ConcurrentBag<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Returns<LLMRequest, CancellationToken>((req, _) =>
            {
                requests.Add(req);
                if (req.Prompt.Contains("split-planning assistant", StringComparison.Ordinal))
                {
                    return Task.FromResult(new LLMResponse
                    {
                        Text = """
name: issue-agent
description: Answers GitHub issues with the requested policy.
subplans:
  - id: answer_issue
    responsibility: |
      Generate the workflow that answers a GitHub issue.
      Always answer in English, never modify an existing issue comment, add a new comment, and use the anthropic provider when asking Copilot.
algorithm:
  type: workflow.call
  id: call_answer_issue
  plan: answer_issue
"""
                    });
                }

                return Task.FromResult(new LLMResponse { Text = SubPlanYaml("answer_issue") });
            });

        var wf = CompileMain("""
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            mode: split
            model: gpt-4
            instruction: |
              Build a GitHub issue workflow.
              Always answer in English.
              Never modify an existing issue comment.
              Use the anthropic provider for Copilot Ask.
          validate:
            compile: true
""");
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var subPlanRequest = requests.Single(request =>
            !request.Prompt.Contains("split-planning assistant", StringComparison.Ordinal));
        Assert.Contains("Sub-workflow generation prompt:", subPlanRequest.Prompt);
        Assert.Contains("Always answer in English, never modify an existing issue comment", subPlanRequest.Prompt);
        Assert.Contains("Original user request:", subPlanRequest.Prompt);
        Assert.Contains("Never modify an existing issue comment.", subPlanRequest.Prompt);
        Assert.Contains("Use the original request only to preserve the exact rules relevant to this sub-workflow.", subPlanRequest.Prompt);
    }

    [Fact]
    public async Task WorkflowPlanSplit_GeneratesParentSubPlansWithChildContracts()
    {
        var requests = new System.Collections.Concurrent.ConcurrentBag<LLMRequest>();

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Returns<LLMRequest, CancellationToken>((req, _) =>
            {
                requests.Add(req);
                if (req.Prompt.Contains("split-planning assistant", StringComparison.Ordinal))
                    return Task.FromResult(new LLMResponse { Text = CompositeSplitManifestYaml() });

                if (req.Prompt.Contains("'report'", StringComparison.Ordinal))
                    return Task.FromResult(new LLMResponse { Text = ParentSubPlanYaml() });

                var name = req.Prompt.Contains("'collect'", StringComparison.Ordinal) ? "collect" : "summarize";
                return Task.FromResult(new LLMResponse { Text = SubPlanYaml(name) });
            });

        var wf = CompileMain("""
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            mode: split
            model: gpt-4
            instruction: Build a composite workflow
          validate:
            compile: true
            dry_run: true
""");
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var planOutput = result.Outputs!["plan"] as JsonObject;
        Assert.NotNull(planOutput);
        var workflows = planOutput!["workflows"] as JsonObject;
        Assert.NotNull(workflows);
        Assert.True(workflows!.ContainsKey("./split-composite/report.yaml"));
        Assert.Contains("name: collect", workflows["./split-composite/report.yaml"]!.GetValue<string>());
        Assert.DoesNotContain("kind: workspace", workflows["./split-composite/report.yaml"]!.GetValue<string>());
        Assert.Contains("workflow.call", workflows["./split-composite/report.yaml"]!.GetValue<string>());
        Assert.Contains(requests, request =>
            request.Prompt.Contains("'report'", StringComparison.Ordinal)
            && request.Prompt.Contains("\"inputs\"", StringComparison.Ordinal)
            && request.Prompt.Contains("\"outputs\"", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, request =>
            request.Prompt.Contains("'report'", StringComparison.Ordinal)
            && request.Prompt.Contains("./split-composite/collect.yaml", StringComparison.Ordinal));
        var meta = planOutput["meta"] as JsonObject;
        Assert.NotNull(meta);
        Assert.Equal("collect", meta!["generation_batches"]![0]![0]!.GetValue<string>());
        Assert.Equal("report", meta["generation_batches"]![1]![0]!.GetValue<string>());
        Assert.Equal(4, requests.Count);
    }

    [Fact]
    public async Task WorkflowPlanSplit_MainDryRunUsesGeneratedRootChildArrayInputs()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Returns<LLMRequest, CancellationToken>((req, _) =>
            {
                if (req.Prompt.Contains("split-planning assistant", StringComparison.Ordinal))
                    return Task.FromResult(new LLMResponse { Text = ArrayRootSplitManifestYaml() });

                return Task.FromResult(new LLMResponse { Text = ArrayLoopSubPlanYaml() });
            });

        var wf = CompileMain("""
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            mode: split
            model: gpt-4
            instruction: Build an issue processor
          validate:
            compile: true
            dry_run: true
""");
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var planOutput = Assert.IsType<JsonObject>(result.Outputs!["plan"]);
        var mainYaml = planOutput["main.yaml"]!.GetValue<string>();
        Assert.Contains("issues:", mainYaml);
        Assert.Contains("type: \"array\"", mainYaml);
    }

    [Fact]
    public async Task WorkflowPlan_DefaultMode_RemainsSinglePlan()
    {
        var callCount = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => callCount++)
            .ReturnsAsync(new LLMResponse { Text = SubPlanYaml("single") });

        var wf = CompileMain("""
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          generator:
            model: gpt-4
            instruction: Build a normal workflow
          validate:
            compile: true
""");
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, callCount);
        var planOutput = result.Outputs!["plan"] as JsonObject;
        Assert.NotNull(planOutput);
        Assert.NotNull(planOutput!["yaml"]);
        Assert.Null(planOutput["main.yaml"]);
    }

    private static string SplitManifestYaml() => """
name: split-demo
description: Researches a topic and summarizes the result.
inputs:
  topic: { type: string, required: true }
outputs:
  final: "${data.steps.call_summarize.outputs.result}"
subplans:
  - id: collect
    path: ./split-demo/collect.yaml
    responsibility: Collect source material for the topic.
    inputs:
      topic: { type: string, required: true }
    outputs:
      result: { type: string }
  - id: summarize
    path: ./split-demo/summarize.yaml
    responsibility: Summarize collected material.
    inputs:
      topic: { type: string, required: true }
    outputs:
      result: { type: string }
algorithm:
  type: sequence
  id: orchestrate
  steps:
    - type: workflow.call
      id: call_collect
      plan: collect
      args: { topic: "${data.inputs.topic}" }
    - type: workflow.call
      id: call_summarize
      plan: summarize
      args: { topic: "${data.inputs.topic}" }
""";

    private static string CompositeSplitManifestYaml() => """
name: split-composite
description: Collects information and produces a final report.
inputs:
  topic: { type: string, required: true }
outputs:
  final: "${data.steps.call_report.outputs.result}"
subplans:
  - id: collect
    path: ./split-composite/collect.yaml
    responsibility: Collect source material for the topic.
    inputs:
      topic: { type: string, required: true }
    outputs:
      result: { type: string }
  - id: summarize
    path: ./split-composite/summarize.yaml
    responsibility: Summarize collected material.
    inputs:
      topic: { type: string, required: true }
    outputs:
      result: { type: string }
  - id: report
    path: ./split-composite/report.yaml
    responsibility: Coordinate collection and summarization for a final report.
    inputs:
      topic: { type: string, required: true }
    outputs:
      result: { type: string }
    algorithm:
      type: sequence
      id: build_report
      steps:
        - type: workflow.call
          id: call_collect
          plan: collect
          args: { topic: "${data.inputs.topic}" }
        - type: workflow.call
          id: call_summarize
          plan: summarize
          args: { topic: "${data.inputs.topic}" }
algorithm:
  type: workflow.call
  id: call_report
  plan: report
  args: { topic: "${data.inputs.topic}" }
""";

    private static string ArrayRootSplitManifestYaml() => """
name: split-array-root
description: Processes a list of issues.
subplans:
  - id: process_many
    responsibility: Process issues one by one.
algorithm:
  type: workflow.call
  id: call_process_many
  plan: process_many
""";

    private static string ArrayLoopSubPlanYaml() => """
version: 1
name: process_many
skill:
  description: Process issues one by one.
  tags: [generated]
  inputs:
    issues:
      type: array
      required: true
      items: { type: string }
  outputs:
    result: { type: number }
workflows:
  main:
    inputs:
      issues:
        type: array
        required: true
        items: { type: string }
    steps:
      - id: init
        type: set
        input:
          issues: "${data.inputs.issues}"
      - id: each_issue
        type: loop.sequential
        item_var: issue
        input:
          items: "${data.steps.init.issues}"
        steps:
          - id: render_issue
            type: template.render
            input:
              engine: mustache
              template: "{{issue}}"
              data:
                issue: "${data.issue}"
              mode: text
      - id: summarize
        type: set
        input:
          count: "${data.steps.each_issue.count}"
      - id: normalize
        type: set
        input:
          result: "${data.steps.summarize.count}"
      - id: complete
        type: set
        input:
          result: "${data.steps.normalize.result}"
    outputs:
      result: "${data.steps.complete.result}"
""";

    private static string ParentSubPlanYaml() => """
version: 1
name: report
skill:
  description: Coordinate collection and summarization for a final report.
  tags: [generated]
  inputs:
    topic: { type: string, required: true }
  outputs:
    result: { type: string }
workflows:
  report:
    inputs:
      topic: { type: string, required: true }
    steps:
      - id: init
        type: set
        input:
          topic: "${data.inputs.topic}"
      - id: call_collect
        type: workflow.call
        input:
          ref:
            kind: local
            name: collect
          args:
            topic: "${data.steps.init.topic}"
      - id: call_summarize
        type: workflow.call
        input:
          ref:
            kind: local
            name: summarize
          args:
            topic: "${data.steps.init.topic}"
      - id: compose
        type: set
        input:
          result: "${data.steps.call_summarize.outputs.result}"
      - id: complete
        type: set
        input:
          result: "${data.steps.compose.result}"
    outputs:
      result: "${data.steps.complete.result}"
  collect:
    inputs:
      topic: { type: string, required: true }
    steps:
      - id: init
        type: set
        input:
          topic: "${data.inputs.topic}"
      - id: prepare
        type: set
        input:
          topic: "${data.steps.init.topic}"
      - id: render
        type: template.render
        input:
          engine: mustache
          template: "collect"
          data:
            topic: "${data.steps.prepare.topic}"
          mode: text
      - id: normalize
        type: set
        input:
          result: "${data.steps.render.text}"
      - id: complete
        type: set
        input:
          result: "${data.steps.normalize.result}"
    outputs:
      result: "${data.steps.complete.result}"
  summarize:
    inputs:
      topic: { type: string, required: true }
    steps:
      - id: init
        type: set
        input:
          topic: "${data.inputs.topic}"
      - id: prepare
        type: set
        input:
          topic: "${data.steps.init.topic}"
      - id: render
        type: template.render
        input:
          engine: mustache
          template: "summarize"
          data:
            topic: "${data.steps.prepare.topic}"
          mode: text
      - id: normalize
        type: set
        input:
          result: "${data.steps.render.text}"
      - id: complete
        type: set
        input:
          result: "${data.steps.normalize.result}"
    outputs:
      result: "${data.steps.complete.result}"
""";

    private static string SubPlanYaml(string name) => $$"""
version: 1
name: {{name}}
skill:
  description: Generated {{name}} sub-plan.
  tags: [generated]
  inputs:
    topic: { type: string, required: true }
  outputs:
    result: { type: string }
workflows:
  main:
    inputs:
      topic: { type: string, required: true }
    steps:
      - id: init
        type: set
        input:
          topic: "${data.inputs.topic}"
      - id: prepare
        type: set
        input:
          topic: "${data.steps.init.topic}"
      - id: render
        type: template.render
        input:
          engine: mustache
          template: "{{name}}"
          data:
            topic: "${data.steps.prepare.topic}"
          mode: text
      - id: normalize
        type: set
        input:
          result: "${data.steps.render.text}"
      - id: complete
        type: set
        input:
          result: "${data.steps.normalize.result}"
    outputs:
      result: "${data.steps.complete.result}"
""";

    // ------ Removed step types ------

    [Fact]
    public void TemplatePlan_NotRegistered()
    {
        var engine = new WorkflowEngine();
        Assert.False(engine.Registry.Has("template.plan"));
    }

    [Fact]
    public void TemplateExecute_NotRegistered()
    {
        var engine = new WorkflowEngine();
        Assert.False(engine.Registry.Has("template.execute"));
    }
}
