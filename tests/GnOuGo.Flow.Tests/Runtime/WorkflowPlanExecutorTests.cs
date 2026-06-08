using System.Text.Json.Nodes;
using Moq;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
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
        Assert.Contains("[DSL REFERENCE]", capturedPrompt);
        Assert.Contains("[AVAILABLE STEP TYPES]", capturedPrompt);
        Assert.Contains("[TASK]", capturedPrompt);
        Assert.Contains("[ERROR HANDLING AND RETRIES]", capturedPrompt);
        Assert.DoesNotContain("[STRUCTURED OUTPUT STRICT SCHEMAS]", capturedPrompt);
        Assert.Contains("Use `retry` only for transient errors that are explicitly marked retryable by the runtime.", capturedPrompt);
        Assert.Contains("Retries run before `on_error` is evaluated.", capturedPrompt);
        Assert.Contains("Inside `on_error.cases[].if`, the error context exposes `error.code`, `error.message`, `error.retryable`, `step.id`, and `step.type`.", capturedPrompt);
        Assert.Contains("Retry + fallback example for a transient LLM error:", capturedPrompt);
        Assert.Contains("Non-retryable validation example:", capturedPrompt);
        Assert.Contains("[STEP EXCEPTIONS BY TYPE]", capturedPrompt);
        Assert.Contains("- llm.call", capturedPrompt);
        Assert.Contains("LLM_TIMEOUT (retryable)", capturedPrompt);
        Assert.Contains("- template.render", capturedPrompt);
        Assert.Contains("JSON_PARSE (non-retryable)", capturedPrompt);
        Assert.Contains("Function arguments are evaluated before the function runs", capturedPrompt);
        Assert.Contains("coalesce(data.steps.branch_a.value, data.steps.branch_b.value)", capturedPrompt);
        Assert.Contains("produced only inside `switch` cases", capturedPrompt);
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
        Assert.Contains("[STEP EXCEPTIONS BY TYPE]", capturedPrompt);
        Assert.Contains("- template.render", capturedPrompt);
        Assert.Contains("- llm.call", capturedPrompt);
        Assert.DoesNotContain("- mcp.call", capturedPrompt);
        Assert.DoesNotContain("- sequence", capturedPrompt);
        Assert.Contains("[CONSTRAINTS]", capturedPrompt);
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
        // First prompt should NOT contain [PREVIOUS ERROR]
        Assert.DoesNotContain("[PREVIOUS ERROR]", prompts[0]);
        // Second prompt SHOULD contain [PREVIOUS ERROR]
        Assert.Contains("[PREVIOUS ERROR]", prompts[1]);
        Assert.Contains("Fix the issues", prompts[1]);
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
                        Text = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: definitely.not.a.step\n"
                    };
                }

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
");
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, prompts.Count);
        Assert.Contains("[PREVIOUS ERROR]", prompts[1]);
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
    public async Task WorkflowPlan_SemanticValidation_RejectsUnknownMcpResponseProperty()
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
                    InputSchema = System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"user\":{\"type\":\"string\"}},\"required\":[\"user\"]}"),
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
        Assert.Contains("[AVAILABLE MCP SERVERS]", capturedPrompt);
        Assert.Contains("Use the exact server name in mcp.call input.server and in mcp.list input.servers.", capturedPrompt);

        // Tool discovery: tool names and descriptions should appear
        Assert.Contains("- github: GitHub repository automation and file operations", capturedPrompt);
        Assert.Contains("list_repos", capturedPrompt);
        Assert.Contains("List repositories for a user", capturedPrompt);
        Assert.Contains("get_file", capturedPrompt);
        Assert.Contains("input_schema:", capturedPrompt);
        Assert.Contains("output_schema:", capturedPrompt);
        Assert.Contains("example_response:", capturedPrompt);
        Assert.Contains("repositories", capturedPrompt);
        Assert.Contains("- weather: Weather forecasts and city conditions", capturedPrompt);
        Assert.Contains("get_weather", capturedPrompt);

        // Prompt discovery
        Assert.Contains("summarize_repo", capturedPrompt);
        Assert.Contains("repo (required)", capturedPrompt);

        // Preferred direct-call guidance when tools are discovered
        Assert.Contains("Preferred MCP planning pattern: when tool names and input schemas are listed above, use `mcp.call` directly with explicit `method` and `request`", capturedPrompt);

        // MCP output access guidance
        Assert.Contains("[MCP OUTPUT ACCESS]", capturedPrompt);
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
        Assert.Contains("[MCP OUTPUT ACCESS]", capturedPrompt);
        Assert.Contains("mcp.call single-tool output shape:", capturedPrompt);
        Assert.Contains("status: \"ok\"|\"error\", response: <tool-specific JSON>", capturedPrompt);
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
        Assert.Contains("[SERVER CATALOG]", requests[0].Prompt);
        Assert.Contains("GitHub repository automation", requests[0].Prompt);
        Assert.Contains("Weather forecasts", requests[0].Prompt);
        Assert.DoesNotContain("list_repos", requests[0].Prompt);
        Assert.NotNull(requests[1].StructuredOutputSchema);
        Assert.Null(requests[1].Temperature);
        var mcpSectionStart = requests[2].Prompt.LastIndexOf("[AVAILABLE MCP SERVERS]", StringComparison.Ordinal);
        var mcpSectionEnd = requests[2].Prompt.IndexOf("[MCP OUTPUT ACCESS]", mcpSectionStart, StringComparison.Ordinal);
        var mcpSection = requests[2].Prompt[mcpSectionStart..mcpSectionEnd];
        Assert.Contains("list_repos", mcpSection);
        Assert.DoesNotContain("get_weather", mcpSection);
        Assert.DoesNotContain("delete_repo", mcpSection);
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




