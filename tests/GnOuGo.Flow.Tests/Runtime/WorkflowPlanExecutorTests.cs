using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
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
    private const string ValidGeneratedTemplateWorkflowYaml = """
        version: 1
        name: generated-workflow
        skill:
          description: Generated workflow.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: s
                type: template.render
                input:
                  engine: mustache
                  template: ok
                  mode: text
        """;

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

    private static async Task<string> GeneratePipelineWithMainAssemblyAsync(string mainAssemblyYaml)
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect a base value." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_base"
                        goal: Collect a base value.
                        inputs:
                          query: string
                        outputs:
                          value: string
                        extract_reason: This reusable operation provides a base value for orchestration.
                        content:
                          Return a base value for the query.
                        :::

                        ## Main workflow orchestration

                        Call collect_base and shape the main output.
                        """
                    };

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_base`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-base-leaf
                        skill:
                          description: Collect a base value.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            value: string
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  value: ${data.inputs.query}
                            outputs:
                              value:
                                expr: ${data.steps.collect.value}
                                type: string
                        """
                    };

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                    return new LLMResponse { Text = mainAssemblyYaml };

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect a base value and shape it."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject { ["query"] = "fast" }, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        return result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
    }

    private static InMemoryMcpClientFactory CreateCmdRunMcpFactory()
    {
        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("cmd", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "cmd_run",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "commandName": { "type": "string" },
                        "parametersJson": {
                          "anyOf": [
                            { "type": "string" },
                            { "type": "null" }
                          ]
                        }
                      },
                      "required": ["commandName"],
                      "additionalProperties": false
                    }
                    """)
                }
            }
        });
        return mcpFactory;
    }

    private static LLMResponse CreateStructuredMarkExtractableBlocksResponse(
        string annotatedMarkdown,
        JsonArray subworkflows,
        string mainOrchestration)
    {
        var json = new JsonObject
        {
            ["annotated_markdown"] = annotatedMarkdown,
            ["subworkflows"] = subworkflows,
            ["main_orchestration"] = mainOrchestration
        };
        return new LLMResponse
        {
            Json = json,
            Text = json.ToJsonString()
        };
    }

    private static LLMResponse CreateExtractionQualityReviewResponse(
        int score,
        string verdict,
        JsonArray? diagnostics = null,
        string retryGuidance = "")
    {
        var json = new JsonObject
        {
            ["score"] = score,
            ["verdict"] = verdict,
            ["diagnostics"] = diagnostics ?? new JsonArray(),
            ["retry_guidance"] = retryGuidance
        };
        return new LLMResponse
        {
            Json = json,
            Text = json.ToJsonString()
        };
    }

    [Fact]
    public async Task WorkflowPlan_RepairMode_WithPromptOnly_ReturnsValidatedReplacementYaml()
    {
        string? capturedPrompt = null;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                capturedPrompt = request.Prompt;
                return new LLMResponse { Text = ValidGeneratedTemplateWorkflowYaml };
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: repair
                  generator:
                    model: gpt-4
                    prefilter: false
                  repair:
                    existing_yaml: |
                      version: 1
                      name: existing-agent
                      workflows:
                        main:
                          steps: []
                    prompt: "Fix the answer output mapping."
                  validate:
                    compile: true
                  on_invalid:
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var plan = Assert.IsType<JsonObject>(result.Outputs!["plan"]);
        Assert.Equal("repair", plan["meta"]?["mode"]?.GetValue<string>());
        Assert.Equal(1, plan["meta"]?["attempt"]?.GetValue<int>());
        Assert.Contains("Fix the answer output mapping.", capturedPrompt);
        Assert.Contains("<existing_workflow_yaml>", capturedPrompt);
        Assert.Contains("Make the smallest patch-style change", capturedPrompt);
    }

    [Fact]
    public async Task WorkflowPlan_RepairMode_WithErrorOnly_IncludesRuntimeErrorAndFailedInput()
    {
        string? capturedPrompt = null;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                capturedPrompt = request.Prompt;
                return new LLMResponse { Text = ValidGeneratedTemplateWorkflowYaml };
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: repair
                  generator:
                    model: gpt-4
                    prefilter: false
                  repair:
                    existing_yaml: |
                      version: 1
                      name: existing-agent
                      workflows:
                        main:
                          steps: []
                    failed_input: "summarize issue 42"
                    error:
                      code: MCP_CALL_ERROR
                      type: mcp.call
                      message: "Tool request used the wrong field name."
                      details:
                        tool: issue_get
                  validate:
                    compile: true
                  on_invalid:
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("<runtime_error>", capturedPrompt);
        Assert.Contains("MCP_CALL_ERROR", capturedPrompt);
        Assert.Contains("Tool request used the wrong field name.", capturedPrompt);
        Assert.Contains("<failed_user_input>", capturedPrompt);
        Assert.Contains("summarize issue 42", capturedPrompt);
    }

    [Theory]
    [InlineData("missing_yaml")]
    [InlineData("missing_prompt_and_error")]
    [InlineData("error_without_message")]
    public async Task WorkflowPlan_RepairMode_ValidatesRequiredRepairFields(string scenario)
    {
        var repairInput = scenario switch
        {
            "missing_yaml" => new JsonObject
            {
                ["prompt"] = "Fix it."
            },
            "missing_prompt_and_error" => new JsonObject
            {
                ["existing_yaml"] = "version: 1"
            },
            _ => new JsonObject
            {
                ["existing_yaml"] = "version: 1",
                ["error"] = new JsonObject
                {
                    ["code"] = "MCP_CALL_ERROR"
                }
            }
        };

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            inputs:
              plan_input:
                type: object
                required: true
            steps:
              - id: plan
                type: workflow.plan
                input: "${data.inputs.plan_input}"
        """);

        var planInput = new JsonObject
        {
            ["mode"] = "repair",
            ["generator"] = new JsonObject
            {
                ["model"] = "gpt-4",
                ["prefilter"] = false
            },
            ["repair"] = repairInput
        };

        var mockLlm = new Mock<ILLMClient>();
        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject { ["plan_input"] = planInput }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        mockLlm.Verify(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WorkflowPlan_RepairMode_InvalidReplacementUsesBoundedValidationRepair()
    {
        var callCount = 0;
        var prompts = new List<string>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                callCount++;
                prompts.Add(request.Prompt);
                return new LLMResponse
                {
                    Text = callCount == 1
                        ? "version: 1\nname: invalid\nworkflows: {}"
                        : ValidGeneratedTemplateWorkflowYaml
                };
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: repair
                  generator:
                    model: gpt-4
                    prefilter: false
                  repair:
                    existing_yaml: |
                      version: 1
                      name: existing-agent
                      workflows:
                        main:
                          steps: []
                    prompt: "Restore the missing workflow body."
                  validate:
                    compile: true
                  on_invalid:
                    max_attempts: 2
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, callCount);
        var plan = Assert.IsType<JsonObject>(result.Outputs!["plan"]);
        Assert.Equal(2, plan["meta"]?["attempt"]?.GetValue<int>());
        Assert.Contains("<previous_error>", prompts[1]);
        Assert.Contains("<invalid_yaml>", prompts[1]);
    }

    private sealed class StaticLlmCapabilityResolver : ILLMCapabilityResolver
    {
        private readonly bool? _supportsStructuredOutput;

        public StaticLlmCapabilityResolver(bool? supportsStructuredOutput)
        {
            _supportsStructuredOutput = supportsStructuredOutput;
        }

        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct)
            => Task.FromResult(_supportsStructuredOutput);
    }

    private static LLMResponse? TryRespondToPipelineMainAssembly(LLMRequest req)
    {
        if (!req.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
            return null;

        if (req.Prompt.Contains("collect_data", StringComparison.Ordinal)
            && req.Prompt.Contains("generate_report", StringComparison.Ordinal))
        {
            return new LLMResponse
            {
                Text = """
                document:
                  name: generated-pipeline-workflow
                  skill:
                    description: Generated pipeline workflow.
                    tags: [generated, pipeline]
                    inputs:
                      query: string
                    outputs:
                      collect_data_outputs: object
                      generate_report_outputs: object
                graph:
                  inputs:
                    query: string
                  steps:
                    - id: call_collect_data
                      leaf: collect_data
                      args:
                        query: ${data.inputs.query}
                    - id: call_generate_report
                      leaf: generate_report
                      args:
                        query: ${data.inputs.query}
                        records: ${data.steps.call_collect_data.outputs.records}
                  outputs:
                    collect_data_outputs: ${data.steps.call_collect_data.outputs}
                    generate_report_outputs: ${data.steps.call_generate_report.outputs}
                """
            };
        }

        if (req.Prompt.Contains("send_report", StringComparison.Ordinal))
        {
            return new LLMResponse
            {
                Text = """
                document:
                  name: send-report-pipeline
                  skill:
                    description: Generated send report pipeline.
                    tags: [generated, pipeline]
                    inputs:
                      report_title: string
                      recipient: string
                      dry_run: boolean
                    outputs:
                      send_report_outputs: object
                graph:
                  inputs:
                    report_title: string
                    recipient: string
                    dry_run: boolean
                  steps:
                    - id: call_send_report
                      leaf: send_report
                      args:
                        report_title: ${data.inputs.report_title}
                        recipient: ${data.inputs.recipient}
                        dry_run: ${data.inputs.dry_run}
                        priority: normal
                  outputs:
                    send_report_outputs: ${data.steps.call_send_report.outputs}
                """
            };
        }

        var (leafName, inputName) = req.Prompt.Contains("list_repositories", StringComparison.Ordinal)
            ? ("list_repositories", "owner")
            : req.Prompt.Contains("build_profile", StringComparison.Ordinal)
                ? ("build_profile", "name")
                : req.Prompt.Contains("build_token_workflow", StringComparison.Ordinal)
                    ? ("build_token_workflow", "name")
                    : req.Prompt.Contains("classify_issue_via_copilot_ask", StringComparison.Ordinal)
                        ? ("classify_issue_via_copilot_ask", "issue")
                    : req.Prompt.Contains("collect_data", StringComparison.Ordinal)
                        ? ("collect_data", "query")
                        : ("leaf", "input");

        return new LLMResponse
        {
            Text = $$"""
            document:
              name: {{leafName}}_pipeline
              skill:
                description: Generated {{leafName}} pipeline.
                tags: [generated, pipeline]
                inputs:
                  {{inputName}}: string
                outputs:
                  {{leafName}}_outputs: object
            graph:
              inputs:
                {{inputName}}: string
              steps:
                - id: call_{{leafName}}
                  leaf: {{leafName}}
                  args:
                    {{inputName}}: ${data.inputs.{{inputName}}}
              outputs:
                {{leafName}}_outputs: ${data.steps.call_{{leafName}}.outputs}
            """
        };
    }

    private static IEnumerable<StepDef> EnumerateSteps(IEnumerable<StepDef> steps)
    {
        foreach (var step in steps)
        {
            yield return step;

            if (step.Steps != null)
            {
                foreach (var child in EnumerateSteps(step.Steps))
                    yield return child;
            }

            if (step.Branches != null)
            {
                foreach (var child in step.Branches.SelectMany(branch => EnumerateSteps(branch.Steps)))
                    yield return child;
            }

            if (step.Cases != null)
            {
                foreach (var child in step.Cases.SelectMany(@case => EnumerateSteps(@case.Steps)))
                    yield return child;
            }

            if (step.Default != null)
            {
                foreach (var child in EnumerateSteps(step.Default))
                    yield return child;
            }
        }
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
        Assert.Contains("@param {string}", reference);
        Assert.Contains("@returns {string}", reference);
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
                Text = ValidGeneratedTemplateWorkflowYaml
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          mode: basic
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
        Assert.Contains("<structured_output_strict_schema_rules>", capturedPrompt);
        Assert.Contains("Never use `type: any`", capturedPrompt);
        Assert.Contains("Every object schema, including nested objects and array item objects", capturedPrompt);
        Assert.Contains("Do not generate bare object schemas", capturedPrompt);
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
        Assert.Contains("Use input-level `required: true|false` only as a boolean", capturedPrompt);
        Assert.Contains("required_properties: [field_name]", capturedPrompt);
        Assert.Contains("<workflow_plan_generation_guardrails>", capturedPrompt);
        Assert.Contains("every `results[]` item is a per-iteration `data.steps` snapshot", capturedPrompt);
        Assert.Contains("iteration.build_issue_result.handled_by_gnougo", capturedPrompt);
        Assert.Contains("Emit booleans and numbers as unquoted YAML scalars", capturedPrompt);
        Assert.Contains("Use YAML literal block scalars (`|`) for multiline prompts/templates", capturedPrompt);
        Assert.Contains("Follow the discovered MCP schema and tool description exactly", capturedPrompt);
        Assert.DoesNotContain("GitHub issue workflow rules", capturedPrompt);
        Assert.DoesNotContain("never initialize owner/repo globals", capturedPrompt);
        Assert.Contains("Every generated custom `function name(...)` declaration MUST be immediately preceded by JSDoc", capturedPrompt);
        Assert.Contains("@param {type} name - meaning", capturedPrompt);
        Assert.Contains("@returns {type} - meaning", capturedPrompt);
        Assert.Contains("1. Inspect every MCP tool used by this workflow.", capturedPrompt);
        Assert.Contains("Never satisfy a missing required MCP argument with data.env.*, empty string, fake values, or casts.", capturedPrompt);
        Assert.Contains("Prefer the exact MCP argument name and type.", capturedPrompt);
        Assert.Contains("Expression function rules:", capturedPrompt);
        Assert.Contains("Do not invent helpers such as `functions.parseRepoUrl`", capturedPrompt);
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
                Text = ValidGeneratedTemplateWorkflowYaml
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          mode: basic
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
    public async Task WorkflowPlan_DefaultAutoMode_ClassifiesAndRunsBasicPlan()
    {
        var requests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => requests.Add(req))
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("classify a GnOuGo workflow.plan request", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        {"mode":"basic","cyclomatic_complexity":4,"branch_count":3,"confidence":0.91,"reason":"Linear request with a small conditional surface."}
                        """
                    };
                }

                return new LLMResponse
                {
                    Text = ValidGeneratedTemplateWorkflowYaml
                };
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
                    instruction: Build a simple greeting workflow with one optional branch
                  validate:
                    compile: false
        """);
        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, requests.Count);
        Assert.Contains("cyclomatic complexity is less than 10", requests[0].Prompt);
        Assert.Contains("Build a simple greeting workflow", requests[0].Prompt);
        Assert.False(requests[0].UseBackgroundMode);
        Assert.Equal("low", requests[0].Reasoning);
        Assert.True(requests[1].UseBackgroundMode);

        var planOutput = Assert.IsType<JsonObject>(result.Outputs!["plan"]!);
        var meta = Assert.IsType<JsonObject>(planOutput["meta"]!);
        Assert.Equal("basic", meta["mode"]!.GetValue<string>());
        var selection = Assert.IsType<JsonObject>(meta["mode_selection"]!);
        Assert.Equal("auto", selection["source"]!.GetValue<string>());
        Assert.Equal("basic", selection["selected_mode"]!.GetValue<string>());
        Assert.Equal(4, selection["cyclomatic_complexity"]!.GetValue<int>());
        Assert.Equal(10, selection["threshold"]!.GetValue<int>());
    }

    [Fact]
    public async Task WorkflowPlan_AutoMode_CanSelectPipeline()
    {
        var requests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => requests.Add(req))
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("classify a GnOuGo workflow.plan request", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        {"mode":"pipeline","cyclomatic_complexity":14,"branch_count":12,"confidence":0.86,"reason":"Multiple phases and enough branches to split into leaf workflows."}
                        """
                    };
                }

                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records, then generate a report." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_data"
                        goal: Collect records.
                        inputs:
                          query: string
                        outputs:
                          records: string
                        extract_reason: One data collection responsibility.
                        content:
                          Collect records for the query.
                        :::

                        :::subworkflow name="generate_report"
                        goal: Generate a report.
                        inputs:
                          query: string
                          records: string
                        outputs:
                          text: string
                        extract_reason: One reporting responsibility.
                        content:
                          Generate a report from records.
                        :::

                        ## Main workflow orchestration

                        Call collect_data, then call generate_report.
                        """
                    };

                if (req.Prompt.Contains("reviewing the quality of a `workflow.plan` pipeline", StringComparison.Ordinal))
                    return CreateExtractionQualityReviewResponse(
                        92,
                        "pass",
                        retryGuidance: "Extraction is faithful and can proceed.");

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_data`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-data
                        skill:
                          description: Collect records.
                          tags: [generated]
                          inputs:
                            query: string
                          outputs:
                            records: string
                        workflows:
                          collect_data:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  records: "${data.inputs.query}-records"
                            outputs:
                              records:
                                expr: "${data.steps.collect.records}"
                                type: string
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `generate_report`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: generate-report
                        skill:
                          description: Generate a report.
                          tags: [generated]
                          inputs:
                            query: string
                            records: string
                          outputs:
                            text: string
                        workflows:
                          generate_report:
                            inputs:
                              query: string
                              records: string
                            steps:
                              - id: report
                                type: template.render
                                input:
                                  engine: mustache
                                  template: "Report for {{query}}: {{records}}"
                                  mode: text
                                  data:
                                    query: "${data.inputs.query}"
                                    records: "${data.inputs.records}"
                            outputs:
                              text: "${data.steps.report.text}"
                        """
                    };

                return TryRespondToPipelineMainAssembly(req)
                    ?? throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: auto
                  raw_prompt: "Collect records, then generate a report."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
        """);
        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(7, requests.Count);
        Assert.Contains("cyclomatic complexity is 10 or more", requests[0].Prompt);
        var planOutput = Assert.IsType<JsonObject>(result.Outputs!["plan"]!);
        var meta = Assert.IsType<JsonObject>(planOutput["meta"]!);
        Assert.Equal("pipeline", meta["mode"]!.GetValue<string>());
        Assert.Equal(2, meta["leaf_subworkflow_count"]!.GetValue<int>());
        var selection = Assert.IsType<JsonObject>(meta["mode_selection"]!);
        Assert.Equal("pipeline", selection["selected_mode"]!.GetValue<string>());
        Assert.Equal(14, selection["cyclomatic_complexity"]!.GetValue<int>());
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_ComposesMainAndLeafSubworkflows()
    {
        var requests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) =>
            {
                lock (requests)
                    requests.Add(req);
            })
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records, then generate a report." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_data"
                        goal: Collect source records.
                        inputs:
                          query: string
                        outputs:
                          records: array
                        extract_reason: This is a reusable multi-step data collection operation.
                        content:
                          Collect records for the provided query.
                          Return the records as an array.
                        :::

                        :::subworkflow name="generate_report"
                        goal: Generate the final report.
                        inputs:
                          query: string
                          records: array
                        outputs:
                          text: string
                        extract_reason: This produces a report artifact.
                        content:
                          Generate a concise report for the provided query and collected records.
                        :::

                        ## Main workflow orchestration

                        Call collect_data, then generate_report.
                        """
                    };

                if (req.Prompt.Contains("reviewing the quality of a `workflow.plan` pipeline", StringComparison.Ordinal))
                    return CreateExtractionQualityReviewResponse(
                        92,
                        "pass",
                        retryGuidance: "Extraction is faithful and can proceed.");

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_data`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-data-leaf
                        skill:
                          description: Collect source records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records: array
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  records: ["one", "two"]
                            outputs:
                              records:
                                expr: "${data.steps.collect.records}"
                                type: array
                                items:
                                  type: string
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `generate_report`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: generate-report-leaf
                        skill:
                          description: Generate a report.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            text: string
                        workflows:
                          main:
                            inputs:
                              query: string
                              records: array
                            steps:
                              - id: report
                                type: template.render
                                input:
                                  engine: mustache
                                  template: "Report for {{query}}: {{records}}"
                                  mode: text
                                  data:
                                    query: "${data.inputs.query}"
                                    records: "${data.inputs.records}"
                            outputs:
                              text: "${data.steps.report.text}"
                        """
                    };

                return TryRespondToPipelineMainAssembly(req)
                    ?? throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records, then generate a report."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
        """);
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(6, requests.Count);
        Assert.Contains("Correct spelling and grammar.", requests[0].Prompt);
        Assert.Contains(":::subworkflow name=\"snake_case_name\"", requests[1].Prompt);
        Assert.Null(requests[1].StructuredOutputSchema);
        Assert.Null(requests[1].StructuredOutputStrict);
        Assert.Contains("Return ONLY annotated Markdown.", requests[1].Prompt);
        Assert.DoesNotContain("Return ONLY JSON matching the requested structured output schema.", requests[1].Prompt);
        Assert.Contains("Avoid blocks with high cyclomatic complexity", requests[1].Prompt);
        Assert.Contains("Do not create one large block that mixes several responsibilities", requests[1].Prompt);
        Assert.Contains("simple renames, constants, guards, field mapping, routing, aggregation, or loop orchestration", requests[1].Prompt);
        Assert.Contains("Leave simple deterministic orchestration in the main workflow", requests[1].Prompt);
        Assert.Contains("Extraction scoring rubric", requests[1].Prompt);
        var mainAssemblyRequest = Assert.Single(requests, request =>
            request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal));
        Assert.Contains("Keep simple deterministic work in the main graph", mainAssemblyRequest.Prompt);
        Assert.Contains("must not emit `mcp.call`, `llm.call`, `template.render`, `human.input`, `workflow.plan`", mainAssemblyRequest.Prompt);
        var collectRequest = Assert.Single(requests, request => request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_data`.", StringComparison.Ordinal));
        var reportRequest = Assert.Single(requests, request => request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `generate_report`.", StringComparison.Ordinal));
        Assert.Contains("locked_leaf_blueprint_json", collectRequest.Prompt);
        Assert.Contains("\"leaf\": \"collect_data\"", collectRequest.Prompt);
        Assert.Contains("locked_leaf_blueprint_json", reportRequest.Prompt);
        Assert.Contains("Do not use workflow.call.", collectRequest.Prompt);
        Assert.Contains("Do not use workflow.plan.", collectRequest.Prompt);
        Assert.Contains("Any schema with `type: object` MUST be strongly typed with a non-empty `properties` mapping.", collectRequest.Prompt);
        Assert.Contains("required_properties: [field_name]", collectRequest.Prompt);
        Assert.Contains("Treat the declared input/output contract as a draft when MCP tools require additional arguments.", collectRequest.Prompt);
        Assert.Contains("1. Inspect every MCP tool used by this workflow.", collectRequest.Prompt);
        Assert.Contains("Never convert a string input to a number just to satisfy an MCP schema.", collectRequest.Prompt);
        Assert.Contains("Workflow outputs must match their declared contract type exactly.", collectRequest.Prompt);
        Assert.Contains("Comparison/predicate expressions such as `${a == b}`", collectRequest.Prompt);
        Assert.Contains("Invalid for a string output", collectRequest.Prompt);
        Assert.Contains("<structured_output_strict_schema_rules>", collectRequest.Prompt);
        Assert.Contains("Never use `type: any`", collectRequest.Prompt);
        Assert.Contains("required` listing EVERY key from `properties`", collectRequest.Prompt);
        Assert.Contains("additionalProperties: false", collectRequest.Prompt);
        Assert.DoesNotContain("Normalized prompt:", collectRequest.Prompt);
        Assert.DoesNotContain("Annotated prompt:", collectRequest.Prompt);
        Assert.DoesNotContain("generate_report", collectRequest.Prompt);
        Assert.DoesNotContain("collect_data", reportRequest.Prompt);

        var planOutput = Assert.IsType<JsonObject>(result.Outputs!["plan"]);
        Assert.Equal("pipeline", planOutput["meta"]!["mode"]!.GetValue<string>());
        Assert.Equal(2, planOutput["meta"]!["leaf_subworkflow_count"]!.GetValue<int>());

        var yaml = planOutput["yaml"]!.GetValue<string>();
        var generatedDoc = WorkflowParser.Parse(yaml);
        Assert.Contains("main", generatedDoc.Workflows.Keys);
        Assert.Contains("collect_data", generatedDoc.Workflows.Keys);
        Assert.Contains("generate_report", generatedDoc.Workflows.Keys);

        var mainSteps = generatedDoc.Workflows["main"].Steps;
        Assert.All(mainSteps, step => Assert.Equal("workflow.call", step.Type));
        Assert.Contains("name: collect_data", yaml);
        Assert.Contains("name: generate_report", yaml);
        Assert.Contains("records: ${data.steps.call_collect_data.outputs.records}", yaml);
        Assert.DoesNotContain("records", generatedDoc.Workflows["main"].Inputs!.Keys);
        Assert.DoesNotContain("workflow.call", EnumerateSteps(generatedDoc.Workflows["collect_data"].Steps).Select(step => step.Type));
        Assert.DoesNotContain("workflow.plan", EnumerateSteps(generatedDoc.Workflows["collect_data"].Steps).Select(step => step.Type));
        Assert.DoesNotContain("workflow.call", EnumerateSteps(generatedDoc.Workflows["generate_report"].Steps).Select(step => step.Type));
        Assert.DoesNotContain("workflow.plan", EnumerateSteps(generatedDoc.Workflows["generate_report"].Steps).Select(step => step.Type));

        var specs = planOutput["pipeline"]!["specs"]!;
        Assert.Equal(2, specs["subworkflows"]!.AsArray().Count);
        Assert.Empty(specs["validation"]!["errors"]!.AsArray());

        var qualityReport = planOutput["pipeline"]!["quality_report"]!;
        Assert.Equal("passed", qualityReport["status"]!.GetValue<string>());
        Assert.True(qualityReport["checks"]!["leaf_intent_validated"]!.GetValue<bool>());
        Assert.True(qualityReport["checks"]!["main_dataflow_validated"]!.GetValue<bool>());
        Assert.True(qualityReport["checks"]!["strong_output_schemas_validated"]!.GetValue<bool>());
        Assert.Equal(2, qualityReport["summary"]!["leaf_count"]!.GetValue<int>());
        Assert.Equal(2, qualityReport["summary"]!["leaf_blueprint_count"]!.GetValue<int>());
        Assert.Equal(2, qualityReport["summary"]!["main_step_count"]!.GetValue<int>());
        Assert.Equal(0, qualityReport["summary"]!["repair_count"]!.GetValue<int>());
        Assert.Empty(qualityReport["warnings"]!.AsArray());
        Assert.Equal(2, qualityReport["leaves"]!.AsArray().Count);
        Assert.Equal("collect_data", qualityReport["leaves"]!.AsArray()[0]!["name"]!.GetValue<string>());
        Assert.Equal("collect_data", qualityReport["leaves"]!.AsArray()[0]!["blueprint"]!["leaf"]!.GetValue<string>());
        Assert.Equal("main", qualityReport["contracts"]!["leaf_outputs"]!["collect_data"]!["generated_workflow_name"]!.GetValue<string>());
        var recordsContract = qualityReport["contracts"]!["leaf_outputs"]!["collect_data"]!["outputs"]!["records"]!;
        Assert.Equal("array", recordsContract["type"]!.GetValue<string>());
        Assert.Equal("string", recordsContract["items"]!.GetValue<string>());

        var inspection = planOutput["pipeline"]!["inspection"]!;
        Assert.Contains("Collect records", inspection["normalized_prompt"]!.GetValue<string>());
        Assert.Contains(":::subworkflow name=\"collect_data\"", inspection["annotated_markdown"]!.GetValue<string>());
        Assert.Equal(2, inspection["summary"]!["leaf_count"]!.GetValue<int>());
        Assert.Equal(2, inspection["summary"]!["leaf_blueprint_count"]!.GetValue<int>());
        Assert.Equal(2, inspection["leaf_manifest"]!["leaves"]!.AsArray().Count);
        Assert.Equal("collect_data", inspection["generated_leaf_blueprints"]!["collect_data"]!["leaf"]!.GetValue<string>());
        Assert.Equal("main", inspection["generated_leaf_contracts"]!["collect_data"]!["generated_workflow"]!.GetValue<string>());
        Assert.Equal(2, inspection["final_main_graph"]!["steps"]!.AsArray().Count);
        Assert.Equal("collect_data", inspection["final_main_graph"]!["steps"]!.AsArray()[0]!["leaf"]!.GetValue<string>());
        Assert.Empty(inspection["repair_history"]!.AsArray());
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_AllowsSimpleDeterministicSetInMain()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records, then expose a renamed summary." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_data"
                        goal: Collect source records.
                        inputs:
                          query: string
                        outputs:
                          records: string
                        extract_reason: This is the only nontrivial data collection operation.
                        content:
                          Collect records for the provided query.
                        :::

                        ## Main workflow orchestration

                        Call collect_data, then use a main workflow set step to rename records into summary.
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_data`.", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-data-leaf
                        skill:
                          description: Collect source records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records: string
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  records: "${data.inputs.query}-records"
                            outputs:
                              records:
                                expr: "${data.steps.collect.records}"
                                type: string
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: collect-data-main-shaping
                          skill:
                            description: Collect data and shape summary.
                            inputs:
                              query: string
                            outputs:
                              summary: string
                        graph:
                          inputs:
                            query: string
                          steps:
                            - id: call_collect_data
                              leaf: collect_data
                              args:
                                query: ${data.inputs.query}
                            - id: shape_summary
                              type: set
                              input:
                                summary: ${data.steps.call_collect_data.outputs.records}
                          outputs:
                            summary: ${data.steps.shape_summary.summary}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records, then expose a renamed summary."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("type: set", yaml);
        Assert.Contains("summary: ${data.steps.call_collect_data.outputs.records}", yaml);

        var doc = WorkflowParser.Parse(yaml);
        Assert.Contains(doc.Workflows["main"].Steps, step =>
            string.Equals(step.Id, "shape_summary", StringComparison.Ordinal)
            && string.Equals(step.Type, "set", StringComparison.Ordinal));
        Assert.Single(doc.Workflows.Keys, name => string.Equals(name, "collect_data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_MovesLeafRootFunctionsToLeafWorkflowScope()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nParse a GitHub repository URL." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="parse_repo"
                        goal: Parse a GitHub repository URL into owner and repository name.
                        inputs:
                          repo_url: string
                        outputs:
                          owner: string
                          repo: string
                        extract_reason: URL parsing is reusable leaf logic.
                        content:
                          Parse the repository URL and return the owner and repo name.
                        :::

                        ## Main workflow orchestration

                        Call parse_repo with repo_url and return owner and repo.
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `parse_repo`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: parse-repo-leaf
                        skill:
                          description: Parse a GitHub repository URL.
                          tags: [generated, leaf]
                          inputs:
                            repo_url: string
                          outputs:
                            owner: string
                            repo: string
                        functions: |
                          /**
                           * Parses a GitHub repository URL into owner and repository name.
                           *
                           * @param {string} url - Repository URL to parse.
                           * @returns {object} Parsed owner and repo fields.
                           */
                          function parseRepoUrl(url) {
                            var parts = String(url || "").replace(/\/$/, "").split("/");
                            if (parts.length < 2) return { owner: "dry-run-owner", repo: "dry-run-repo" };
                            return { owner: parts[parts.length - 2], repo: parts[parts.length - 1] };
                          }
                        workflows:
                          main:
                            inputs:
                              repo_url: string
                            steps:
                              - id: parsed
                                type: set
                                input:
                                  owner: "${functions.parseRepoUrl(data.inputs.repo_url).owner}"
                                  repo: "${functions.parseRepoUrl(data.inputs.repo_url).repo}"
                            outputs:
                              owner:
                                expr: "${data.steps.parsed.owner}"
                                type: string
                              repo:
                                expr: "${data.steps.parsed.repo}"
                                type: string
                        """
                    };

                if (req.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: parse-repo-pipeline
                          skill:
                            description: Parse a GitHub repository URL.
                            tags: [generated, pipeline]
                            inputs:
                              repo_url: string
                            outputs:
                              owner: string
                              repo: string
                        graph:
                          inputs:
                            repo_url: string
                          steps:
                            - id: call_parse_repo
                              leaf: parse_repo
                              args:
                                repo_url: ${data.inputs.repo_url}
                          outputs:
                            owner: ${data.steps.call_parse_repo.outputs.owner}
                            repo: ${data.steps.call_parse_repo.outputs.repo}
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Parse a GitHub repository URL."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: true
                    dry_run: true
                  on_invalid:
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        var generatedDoc = WorkflowParser.Parse(yaml);
        Assert.Null(generatedDoc.Functions);
        Assert.Contains("parseRepoUrl", generatedDoc.Workflows["parse_repo"].Functions);

        var compiled = new WorkflowCompiler().Compile(generatedDoc);
        var run = await new WorkflowEngine().ExecuteAsync(
            compiled.Workflows[compiled.Entrypoint!],
            new JsonObject { ["repo_url"] = "https://github.com/AxaFrance/oidc-client" },
            CancellationToken.None);

        Assert.True(run.Success, run.Error?.Message);
        Assert.Equal("AxaFrance", run.Outputs!["owner"]!.GetValue<string>());
        Assert.Equal("oidc-client", run.Outputs!["repo"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_DefaultMainOutputsUseActualCallIds()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_data"
                        goal: Collect records.
                        inputs:
                          query: string
                        outputs:
                          records: array
                        extract_reason: This is a reusable data collection operation.
                        content:
                          Collect records for the query.
                        :::

                        ## Main workflow orchestration

                        Call collect_data.
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_data`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-data-leaf
                        skill:
                          description: Collect records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records: array
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  records: ["one"]
                            outputs:
                              records:
                                expr: "${data.steps.collect.records}"
                                type: array
                                items:
                                  type: string
                        """
                    };

                if (req.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: collect-custom-output
                          skill:
                            description: Collect records.
                            tags: [generated, pipeline]
                            inputs:
                              query: string
                            outputs:
                              collect_data_outputs: object
                        graph:
                          inputs:
                            query: string
                          steps:
                            - id: collect_records_step
                              leaf: collect_data
                              args:
                                query: ${data.inputs.query}
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: true
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("collect_data_outputs:", yaml);
        Assert.Contains("expr: ${data.steps.collect_records_step.outputs}", yaml);
        Assert.DoesNotContain("collect_data_outputs: ${data.steps.call_collect_data.outputs}", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RepromptsMarkExtractableBlocksWhenExtractionValidationFails()
    {
        var markRequests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    markRequests.Add(req);
                    if (markRequests.Count == 1)
                    {
                        return new LLMResponse
                        {
                            Text = """
                            # Automation

                            :::subworkflow name="collect_data"
                            goal: Collect source records.
                            inputs:
                              query: string
                            outputs:
                              records: array
                            content:
                              Collect records for the provided query.
                            :::
                            """
                        };
                    }

                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_data"
                        goal: Collect source records.
                        inputs:
                          query: string
                        outputs:
                          records: array
                        extract_reason: This is a reusable data collection operation.
                        content:
                          Collect records for the provided query.
                        :::

                        ## Main workflow orchestration

                        Call collect_data.
                        """
                    };
                }

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_data`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-data-leaf
                        skill:
                          description: Collect source records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records: array
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  records: ["one"]
                            outputs:
                              records:
                                expr: "${data.steps.collect.records}"
                                type: array
                                items:
                                  type: string
                        """
                    };

                return TryRespondToPipelineMainAssembly(req)
                    ?? throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 2
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, markRequests.Count);
        Assert.Contains("The previous `mark_extractable_blocks` response failed extraction validation.", markRequests[1].Prompt);
        Assert.Contains("Subworkflow 'collect_data' is missing extract_reason.", markRequests[1].Prompt);
        Assert.Contains("Annotated markdown must include a '## Main workflow orchestration' section.", markRequests[1].Prompt);
        Assert.Contains("<invalid_annotated_markdown>", markRequests[1].Prompt);

        var planOutput = Assert.IsType<JsonObject>(result.Outputs!["plan"]);
        Assert.Contains(":::subworkflow name=\"collect_data\"", planOutput["pipeline"]!["annotated_markdown"]!.GetValue<string>());
        Assert.Empty(planOutput["pipeline"]!["specs"]!["validation"]!["errors"]!.AsArray());
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_ExtractionFailureHonorsConfiguredMaxAttempts()
    {
        var markRequests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    markRequests.Add(req);
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_data"
                        goal: Collect source records.
                        inputs:
                          query: string
                        outputs:
                          records: array
                        content:
                          Collect records for the provided query.
                        :::
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Single(markRequests);
        Assert.Contains("Subworkflow 'collect_data' is missing extract_reason.", result.Error.Message);
        Assert.Contains("Annotated markdown must include a '## Main workflow orchestration' section.", result.Error.Message);

        var details = Assert.IsType<JsonObject>(result.Error.Details);
        Assert.Contains("invalid_annotated_markdown", details);
        var validation = Assert.IsType<JsonObject>(details["validation"]);
        Assert.Equal(2, validation["errors"]!.AsArray().Count);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_PreservesConfiguredMainMetadataAndInputs()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nSend a report." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="send_report"
                        goal: Send the configured report.
                        inputs:
                          report_title: string
                          recipient: string
                          dry_run: boolean
                          priority: string
                        outputs:
                          sent: boolean
                        extract_reason: This is a reusable technical operation with tool orchestration.
                        content:
                          Send the report to the configured recipient.
                          Honor dry_run and priority.
                        :::

                        ## Main workflow orchestration

                        Call send_report.
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `send_report`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: send-report-leaf
                        skill:
                          description: Send the configured report.
                          tags: [generated, leaf]
                          inputs:
                            report_title: string
                            recipient: string
                            dry_run: boolean
                            priority: string
                          outputs:
                            sent: boolean
                        workflows:
                          main:
                            inputs:
                              report_title: string
                              recipient: string
                              dry_run: boolean
                              priority: string
                            steps:
                              - id: send
                                type: template.render
                                input:
                                  engine: mustache
                                  template: "Report {{report_title}} prepared for {{recipient}}."
                                  mode: text
                                  data:
                                    report_title: ${data.inputs.report_title}
                                    recipient: ${data.inputs.recipient}
                            outputs:
                              sent:
                                expr: "${data.steps.send.text != ''}"
                                type: boolean
                        """
                    };

                return TryRespondToPipelineMainAssembly(req)
                    ?? throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  name: configured-report-workflow
                  skill:
                    description: Configured report sender.
                    tags: [configured, reports]
                    inputs:
                      report_title: string
                      recipient:
                        type: string
                        description: Email recipient.
                      dry_run:
                        type: boolean
                        required: false
                        default: false
                    outputs:
                      sent: boolean
                  raw_prompt: "Send a report."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
        """);
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var planOutput = Assert.IsType<JsonObject>(result.Outputs!["plan"]);
        var yaml = planOutput["yaml"]!.GetValue<string>();
        var generatedDoc = WorkflowParser.Parse(yaml);

        Assert.Equal("configured-report-workflow", generatedDoc.Name);
        Assert.Equal("Configured report sender.", generatedDoc.Skill!.Description);
        Assert.Equal(new[] { "configured", "reports" }, generatedDoc.Skill.Tags);
        Assert.Contains("report_title", generatedDoc.Skill.Inputs!.Keys);
        Assert.Contains("recipient", generatedDoc.Skill.Inputs.Keys);
        Assert.Contains("dry_run", generatedDoc.Skill.Inputs.Keys);
        Assert.DoesNotContain("priority", generatedDoc.Skill.Inputs.Keys);
        Assert.False(generatedDoc.Skill.Inputs["dry_run"].Required);
        Assert.Equal("false", generatedDoc.Skill.Inputs["dry_run"].Default);
        Assert.Equal("Email recipient.", generatedDoc.Skill.Inputs["recipient"].Description);
        Assert.Contains("sent", generatedDoc.Skill.Outputs!.Keys);

        var mainInputs = generatedDoc.Workflows["main"].Inputs!;
        Assert.Contains("report_title", mainInputs.Keys);
        Assert.Contains("recipient", mainInputs.Keys);
        Assert.Contains("dry_run", mainInputs.Keys);
        Assert.DoesNotContain("priority", mainInputs.Keys);
        Assert.Contains("priority: normal", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_UsesGeneratedPublicContractWithoutPromotingLeafInputs()
    {
        var requests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) => requests.Add(request))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = "# Repository issue report\n\nUse target_repository_url and number_of_issues_to_process."
                    };
                }

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Repository issue report

                        :::subworkflow name="list_issues"
                        goal: List repository issues.
                        inputs:
                          repository_url: string
                          max_issues: number
                          working_directory_base: string
                        outputs:
                          issues: array
                        extract_reason: This is a reusable tool operation.
                        content:
                          List issues for the repository.
                        :::

                        ## Main workflow orchestration

                        Map target_repository_url to repository_url and number_of_issues_to_process to max_issues.
                        Derive working_directory_base internally.
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `list_issues`.", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: list-issues-leaf
                        skill:
                          description: List repository issues.
                          tags: [github, leaf]
                          inputs:
                            repository_url: string
                            max_issues: number
                            working_directory_base: string
                          outputs:
                            issues: array
                        workflows:
                          main:
                            inputs:
                              repository_url: string
                              max_issues: number
                              working_directory_base: string
                            steps:
                              - id: result
                                type: llm.call
                                input:
                                  model: gpt-4
                                  prompt: "Return an empty issue list for test planning."
                                  structured_output:
                                    strict: true
                                    schema_inline:
                                      type: object
                                      additionalProperties: false
                                      required: [issues]
                                      properties:
                                        issues:
                                          type: array
                                          items:
                                            type: object
                                            additionalProperties: false
                                            required: [number, title, body, html_url]
                                            properties:
                                              number:
                                                type: number
                                              title:
                                                type: string
                                              body:
                                                type: string
                                              html_url:
                                                type: string
                            outputs:
                              issues:
                                expr: "${data.steps.result.json.issues}"
                                type: array
                                items:
                                  type: object
                                  properties:
                                    number:
                                      type: number
                                    title:
                                      type: string
                                    body:
                                      type: string
                                    html_url:
                                      type: string
                                  required_properties: [number, title, body, html_url]
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: repository_issue_report
                          skill:
                            description: Build a repository issue report.
                            tags: [github, issues]
                            inputs:
                              target_repository_url:
                                type: string
                                required: false
                                default: https://github.com/AxaFrance/oidc-client
                              number_of_issues_to_process:
                                type: number
                                required: false
                                default: 20
                            outputs:
                              issues: array
                        graph:
                          inputs:
                            target_repository_url:
                              type: string
                              required: false
                              default: https://github.com/AxaFrance/oidc-client
                            number_of_issues_to_process:
                              type: number
                              required: false
                              default: 20
                          steps:
                            - id: derive_working_directory
                              type: set
                              input:
                                value: .GnOuGo/work
                            - id: call_list_issues
                              leaf: list_issues
                              args:
                                repository_url: ${data.inputs.target_repository_url}
                                max_issues: ${data.inputs.number_of_issues_to_process}
                                working_directory_base: ${data.steps.derive_working_directory.value}
                          outputs:
                            issues: ${data.steps.call_list_issues.outputs.issues}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Build a repository issue report."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
        """);
        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        var generatedDoc = WorkflowParser.Parse(yaml);
        var mainInputs = generatedDoc.Workflows["main"].Inputs!;

        Assert.Equal("repository_issue_report", generatedDoc.Name);
        Assert.Equal("Build a repository issue report.", generatedDoc.Skill!.Description);
        Assert.Equal(new[] { "github", "issues" }, generatedDoc.Skill.Tags);
        Assert.Equal(new[] { "target_repository_url", "number_of_issues_to_process" }, mainInputs.Keys);
        Assert.DoesNotContain("repository_url", mainInputs.Keys);
        Assert.DoesNotContain("working_directory_base", mainInputs.Keys);
        Assert.Contains("repository_url: ${data.inputs.target_repository_url}", yaml);
        Assert.Contains("max_issues: ${data.inputs.number_of_issues_to_process}", yaml);

        var assemblyPrompt = Assert.Single(requests, request =>
            request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal)).Prompt;
        Assert.Contains("leaf_input_candidates_yaml", assemblyPrompt);
        Assert.Contains("generated_leaf_contracts_yaml", assemblyPrompt);
        Assert.Contains("leaf_manifest_json", assemblyPrompt);
        Assert.Contains("main_graph_dsl_context", assemblyPrompt);
        Assert.Contains("main_graph_allowed_support_step_dsl_snippets", assemblyPrompt);
        Assert.Contains("real registered GnOuGo.Flow DSL references for support steps", assemblyPrompt);
        Assert.Contains("### set", assemblyPrompt);
        Assert.Contains("### loop.sequential", assemblyPrompt);
        Assert.Contains("type: set", assemblyPrompt);
        Assert.Contains("type: switch", assemblyPrompt);
        Assert.Contains("type: parallel", assemblyPrompt);
        Assert.Contains("type: loop.sequential", assemblyPrompt);
        Assert.Contains("Do not emit raw `type: workflow.call`", assemblyPrompt);
        Assert.Contains("`generated_leaf_contracts_yaml` is authoritative for leaf workflow names, call arguments, and available outputs.", assemblyPrompt);
        Assert.Contains("repository_url: string", assemblyPrompt);
        Assert.Contains("items:", assemblyPrompt);
        Assert.Contains("html_url:", assemblyPrompt);
        Assert.Contains("body:", assemblyPrompt);
        Assert.Contains("Leaf input names are call arguments, not automatically public main inputs.", assemblyPrompt);
        Assert.Contains("Every `data.inputs.<name>` reference MUST have an identically named declaration in `graph.inputs` or `document.skill.inputs`.", assemblyPrompt);
        Assert.Contains("Use `set` support nodes for data shaping in the main graph", assemblyPrompt);
        Assert.Contains("Exact expressions preserve the resolved JSON value.", assemblyPrompt);
        Assert.Contains("parallel output: `${data.steps.<parallel_id>.branches}`", assemblyPrompt);
        Assert.Contains("loop output: `${data.steps.<loop_id>.results}`", assemblyPrompt);
        Assert.Contains("Do not reference loop child step ids after the loop.", assemblyPrompt);
        Assert.Contains("loop result item shape: each element of `${data.steps.<loop_id>.results}`", assemblyPrompt);
        Assert.Contains("iteration.build_issue_result.<field>", assemblyPrompt);
        Assert.Contains("To flatten loop results", assemblyPrompt);
        Assert.Contains("Do not add MCP, LLM, template, human-input, workflow.plan, or raw workflow.call support nodes to the main graph.", assemblyPrompt);
        Assert.DoesNotContain("generated_leaf_workflows_yaml", assemblyPrompt);
        Assert.DoesNotContain("version: 1\nname: list-issues-leaf", assemblyPrompt);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_CapturesStructuredExtractionMetadataAndValidatesPlannedToolUsage()
    {
        var requests = new List<LLMRequest>();
        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "list_issues",
                    Description = "List repository issues.",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "repository_url": { "type": "string" }
                      },
                      "required": ["repository_url"],
                      "additionalProperties": false
                    }
                    """),
                    OutputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "issues": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "properties": {
                              "number": { "type": "number" },
                              "title": { "type": "string" },
                              "html_url": { "type": "string" }
                            },
                            "required": ["number", "title", "html_url"],
                            "additionalProperties": false
                          }
                        }
                      },
                      "required": ["issues"],
                      "additionalProperties": false
                    }
                    """)
                }
            }
        });

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) => requests.Add(request))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Repository issues\n\nList repository issues." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    const string annotatedMarkdown = """
                    # Repository issues

                    :::subworkflow name="list_issues"
                    goal: List repository issues.
                    inputs:
                      repository_url: string
                    outputs:
                      issues: array
                    extract_reason: This leaf performs tool orchestration against GitHub.
                    content:
                      Call the GitHub list_issues MCP tool for the repository and expose the issue list.
                    :::

                    ## Main workflow orchestration

                    Call list_issues with repository_url and expose issues.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        annotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "list_issues",
                                ["goal"] = "List repository issues.",
                                ["description"] = "Fetch repository issues through the GitHub MCP server.",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "repository_url",
                                        ["type"] = "string",
                                        ["description"] = "Repository URL.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "issues",
                                        ["type"] = "array",
                                        ["description"] = "Repository issues.",
                                        ["required"] = true,
                                        ["item_type"] = "object",
                                        ["properties"] = new JsonArray
                                        {
                                            new JsonObject
                                            {
                                                ["name"] = "number",
                                                ["type"] = "number",
                                                ["description"] = "Issue number.",
                                                ["required"] = true,
                                                ["item_type"] = ""
                                            },
                                            new JsonObject
                                            {
                                                ["name"] = "title",
                                                ["type"] = "string",
                                                ["description"] = "Issue title.",
                                                ["required"] = true,
                                                ["item_type"] = ""
                                            },
                                            new JsonObject
                                            {
                                                ["name"] = "html_url",
                                                ["type"] = "string",
                                                ["description"] = "Issue URL.",
                                                ["required"] = true,
                                                ["item_type"] = ""
                                            }
                                        }
                                    }
                                },
                                ["extract_reason"] = "This leaf performs tool orchestration against GitHub.",
                                ["content"] = "Call the GitHub list_issues MCP tool for the repository and expose the issue list.",
                                ["planned_tools"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["server"] = "github",
                                        ["kind"] = "tool",
                                        ["method"] = "list_issues",
                                        ["required"] = true,
                                        ["purpose"] = "Fetch repository issues.",
                                        ["consumes"] = new JsonArray { "repository_url" },
                                        ["produces"] = new JsonArray { "issues" }
                                    }
                                }
                            }
                        },
                        "Call list_issues with repository_url and expose issues.");
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `list_issues`.", StringComparison.Ordinal))
                {
                    Assert.Contains("Planned MCP tools:", request.Prompt);
                    Assert.Contains("github/list_issues", request.Prompt);
                    Assert.Contains("Structured output schemas:", request.Prompt);
                    Assert.Contains("html_url", request.Prompt);

                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: list-issues-leaf
                        skill:
                          description: List repository issues.
                          tags: [github, leaf]
                          inputs:
                            repository_url: string
                          outputs:
                            issues:
                              type: array
                              items:
                                type: object
                                properties:
                                  number:
                                    type: number
                                  title:
                                    type: string
                                  html_url:
                                    type: string
                                required_properties: [number, title, html_url]
                        workflows:
                          main:
                            inputs:
                              repository_url: string
                            steps:
                              - id: list
                                type: mcp.call
                                input:
                                  server: github
                                  kind: tool
                                  method: list_issues
                                  request:
                                    repository_url: ${data.inputs.repository_url}
                            outputs:
                              issues:
                                expr: ${data.steps.list.response.issues}
                                type: array
                                items:
                                  type: object
                                  properties:
                                    number:
                                      type: number
                                    title:
                                      type: string
                                    html_url:
                                      type: string
                                  required_properties: [number, title, html_url]
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: repository_issues_pipeline
                          skill:
                            description: Build a repository issue list.
                            tags: [github, issues]
                            inputs:
                              repository_url: string
                            outputs:
                              issues:
                                type: array
                                items:
                                  type: object
                                  properties:
                                    number:
                                      type: number
                                    title:
                                      type: string
                                    html_url:
                                      type: string
                                  required_properties: [number, title, html_url]
                        graph:
                          inputs:
                            repository_url: string
                          steps:
                            - id: call_list_issues
                              leaf: list_issues
                              args:
                                repository_url: ${data.inputs.repository_url}
                          outputs:
                            issues:
                              expr: ${data.steps.call_list_issues.outputs.issues}
                              type: array
                              items:
                                type: object
                                properties:
                                  number:
                                    type: number
                                  title:
                                    type: string
                                  html_url:
                                    type: string
                                required_properties: [number, title, html_url]
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "List repository issues."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true),
            McpClientFactory = mcpFactory
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var planOutput = Assert.IsType<JsonObject>(result.Outputs!["plan"]);
        var yaml = planOutput["yaml"]!.GetValue<string>();
        Assert.Contains("method: list_issues", yaml);

        var markRequest = Assert.Single(requests, request =>
            request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal));
        Assert.NotNull(markRequest.StructuredOutputSchema);
        Assert.True(markRequest.StructuredOutputStrict);
        var structuredSchema = Assert.IsType<JsonObject>(markRequest.StructuredOutputSchema);
        var subworkflowItems = Assert.IsType<JsonObject>(structuredSchema["properties"]!["subworkflows"]!["items"]);
        var requiredFields = Assert.IsType<JsonArray>(subworkflowItems["required"]);
        Assert.Contains(requiredFields, field => field!.GetValue<string>() == "work_kind");
        Assert.Contains(requiredFields, field => field!.GetValue<string>() == "contract_role");
        Assert.Contains(requiredFields, field => field!.GetValue<string>() == "concrete_outcome");

        var pipeline = Assert.IsType<JsonObject>(planOutput["pipeline"]);
        var specs = Assert.IsType<JsonObject>(pipeline["specs"]);
        var subworkflows = Assert.IsType<JsonArray>(specs["subworkflows"]);
        var spec = Assert.IsType<JsonObject>(subworkflows[0]);
        Assert.Equal("Fetch repository issues through the GitHub MCP server.", spec["description"]!.GetValue<string>());
        var plannedTools = Assert.IsType<JsonArray>(spec["planned_tools"]);
        var plannedTool = Assert.IsType<JsonObject>(plannedTools[0]);
        Assert.Equal("github", plannedTool["server"]!.GetValue<string>());
        Assert.Equal("list_issues", plannedTool["method"]!.GetValue<string>());
        Assert.True(plannedTool["required"]!.GetValue<bool>());
        Assert.Contains("html_url", spec["output_schemas"]!.ToJsonString());
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RejectsLeafThatOmitsRequiredPlannedMcpTool()
    {
        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "list_issues",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "repository_url": { "type": "string" }
                      },
                      "required": ["repository_url"],
                      "additionalProperties": false
                    }
                    """)
                }
            }
        });

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Repository issues\n\nList repository issues." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    const string annotatedMarkdown = """
                    # Repository issues

                    :::subworkflow name="list_issues"
                    goal: List repository issues.
                    inputs:
                      repository_url: string
                    outputs:
                      issues: array
                    extract_reason: This leaf performs tool orchestration against GitHub.
                    content:
                      Call the GitHub list_issues MCP tool for the repository and expose the issue list.
                    :::

                    ## Main workflow orchestration

                    Call list_issues with repository_url and expose issues.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        annotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "list_issues",
                                ["goal"] = "List repository issues.",
                                ["description"] = "Fetch repository issues through the GitHub MCP server.",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "repository_url",
                                        ["type"] = "string",
                                        ["description"] = "Repository URL.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "issues",
                                        ["type"] = "array",
                                        ["description"] = "Repository issues.",
                                        ["required"] = true,
                                        ["item_type"] = "object",
                                        ["properties"] = new JsonArray
                                        {
                                            new JsonObject
                                            {
                                                ["name"] = "title",
                                                ["type"] = "string",
                                                ["description"] = "Issue title.",
                                                ["required"] = true,
                                                ["item_type"] = ""
                                            }
                                        }
                                    }
                                },
                                ["extract_reason"] = "This leaf performs tool orchestration against GitHub.",
                                ["content"] = "Call the GitHub list_issues MCP tool for the repository and expose the issue list.",
                                ["planned_tools"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["server"] = "github",
                                        ["kind"] = "tool",
                                        ["method"] = "list_issues",
                                        ["required"] = true,
                                        ["purpose"] = "Fetch repository issues.",
                                        ["consumes"] = new JsonArray { "repository_url" },
                                        ["produces"] = new JsonArray { "issues" }
                                    }
                                }
                            }
                        },
                        "Call list_issues with repository_url and expose issues.");
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `list_issues`.", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: list-issues-leaf
                        skill:
                          description: List repository issues.
                          tags: [github, leaf]
                          inputs:
                            repository_url: string
                          outputs:
                            issues:
                              type: array
                              items:
                                type: object
                                properties:
                                  title:
                                    type: string
                        workflows:
                          main:
                            inputs:
                              repository_url: string
                            steps:
                              - id: fake
                                type: set
                                input:
                                  issues: []
                            outputs:
                              issues:
                                expr: ${data.steps.fake.issues}
                                type: array
                                items:
                                  type: object
                                  properties:
                                    title:
                                      type: string
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "List repository issues."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 1
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true),
            McpClientFactory = mcpFactory
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("did not use required planned MCP tool", result.Error!.Message);
        Assert.Contains("github/list_issues", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RejectsExternalLeafWithoutRequiredToolContractBeforeLeafGeneration()
    {
        var leafGenerationRequested = false;
        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("git", new MockMcpServerConfig
        {
            Description = "Repository clone operations.",
            Tools =
            {
                new McpToolInfo
                {
                    Name = "git_clone",
                    Description = "Clone a repository into a local workspace directory.",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "remoteUrl": { "type": "string" },
                        "targetDirectory": { "type": "string" }
                      },
                      "required": ["remoteUrl", "targetDirectory"],
                      "additionalProperties": false
                    }
                    """)
                }
            }
        });

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Clone\n\nClone a repository." };

                if (request.Prompt.Contains("MCP server-selection assistant", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Json = JsonNode.Parse("{\"servers\":[{\"name\":\"git\",\"reason\":\"clone task\"}]}")!,
                        Text = "{\"servers\":[{\"name\":\"git\",\"reason\":\"clone task\"}]}"
                    };

                if (request.Prompt.Contains("tool-selection assistant", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Json = JsonNode.Parse("{\"servers\":[{\"name\":\"git\",\"tools\":[\"git_clone\"],\"prompts\":[]}]}")!,
                        Text = "{\"servers\":[{\"name\":\"git\",\"tools\":[\"git_clone\"],\"prompts\":[]}]}"
                    };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    const string annotatedMarkdown = """
                    # Clone

                    :::subworkflow name="clone_repository"
                    goal: Clone the repository.
                    inputs:
                      repository_url: string
                    outputs:
                      project_root: string
                    extract_reason: This performs external repository clone work.
                    content:
                      Clone the repository into a local workspace directory.
                    :::

                    ## Main workflow orchestration

                    Call clone_repository.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        annotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "clone_repository",
                                ["goal"] = "Clone the repository.",
                                ["description"] = "Clone the repository into the workspace.",
                                ["work_kind"] = "external_work",
                                ["contract_role"] = "external_action",
                                ["concrete_outcome"] = "A cloned repository directory that exists in the workspace.",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "repository_url",
                                        ["type"] = "string",
                                        ["description"] = "Repository URL.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "project_root",
                                        ["type"] = "string",
                                        ["description"] = "Cloned project root.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["extract_reason"] = "This performs external repository clone work.",
                                ["content"] = "Clone the repository into a local workspace directory.",
                                ["planned_tools"] = new JsonArray()
                            }
                        },
                        "Call clone_repository.");
                }

                if (request.Prompt.Contains("Generate exactly one leaf", StringComparison.Ordinal))
                    leafGenerationRequested = true;

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Clone a repository."
                  generator:
                    model: gpt-4
                    prefilter: true
                  validate:
                    compile: false
                    max_repair_attempts: 1
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true),
            McpClientFactory = mcpFactory
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(leafGenerationRequested);
        Assert.NotNull(result.Error);
        Assert.Contains("PIPELINE_EXTRACTION_MISSING_REQUIRED_LEAF_TOOL", result.Error!.Message);
        var rootCauses = result.Error.Details!["root_causes"]!.AsArray();
        Assert.Contains(rootCauses, cause =>
            cause!["category"]!.GetValue<string>() == "missing_required_leaf_tool"
            && cause["invalid_path"]!.GetValue<string>() == "subworkflows.clone_repository.planned_tools");
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RejectsExternalLeafWithoutPlannedToolsEvenWhenNoCapabilityMatch()
    {
        var leafGenerationRequested = false;
        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("opaque", new MockMcpServerConfig
        {
            Description = "Opaque capability catalog.",
            Tools =
            {
                new McpToolInfo
                {
                    Name = "zqxv_process",
                    Description = "Performs a zqxv operation.",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "payload": { "type": "string" }
                      },
                      "required": ["payload"],
                      "additionalProperties": false
                    }
                    """)
                }
            }
        });

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Clone\n\nClone a repository." };

                if (request.Prompt.Contains("MCP server-selection assistant", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Json = JsonNode.Parse("{\"servers\":[{\"name\":\"opaque\",\"reason\":\"only available server\"}]}")!,
                        Text = "{\"servers\":[{\"name\":\"opaque\",\"reason\":\"only available server\"}]}"
                    };

                if (request.Prompt.Contains("tool-selection assistant", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Json = JsonNode.Parse("{\"servers\":[{\"name\":\"opaque\",\"tools\":[\"zqxv_process\"],\"prompts\":[]}]}")!,
                        Text = "{\"servers\":[{\"name\":\"opaque\",\"tools\":[\"zqxv_process\"],\"prompts\":[]}]}"
                    };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    const string annotatedMarkdown = """
                    # Clone

                    :::subworkflow name="clone_repository"
                    goal: Clone the repository.
                    inputs:
                      repository_url: string
                    outputs:
                      project_root: string
                    extract_reason: This performs external repository clone work.
                    content:
                      Clone the repository into a local workspace directory.
                    :::

                    ## Main workflow orchestration

                    Call clone_repository.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        annotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "clone_repository",
                                ["goal"] = "Clone the repository.",
                                ["description"] = "Clone the repository into the workspace.",
                                ["work_kind"] = "external_work",
                                ["contract_role"] = "external_action",
                                ["concrete_outcome"] = "A cloned repository directory that exists in the workspace.",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "repository_url",
                                        ["type"] = "string",
                                        ["description"] = "Repository URL.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "project_root",
                                        ["type"] = "string",
                                        ["description"] = "Cloned project root.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["extract_reason"] = "This performs external repository clone work.",
                                ["content"] = "Clone the repository into a local workspace directory.",
                                ["planned_tools"] = new JsonArray()
                            }
                        },
                        "Call clone_repository.");
                }

                if (request.Prompt.Contains("Generate exactly one leaf", StringComparison.Ordinal))
                    leafGenerationRequested = true;

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Clone a repository."
                  generator:
                    model: gpt-4
                    prefilter: true
                  validate:
                    compile: false
                    max_repair_attempts: 1
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true),
            McpClientFactory = mcpFactory
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(leafGenerationRequested);
        Assert.NotNull(result.Error);
        Assert.Contains("PIPELINE_EXTRACTION_MISSING_REQUIRED_LEAF_TOOL", result.Error!.Message);
        var rootCauses = result.Error.Details!["root_causes"]!.AsArray();
        Assert.Contains(rootCauses, cause =>
            cause!["category"]!.GetValue<string>() == "missing_required_leaf_tool"
            && cause["invalid_path"]!.GetValue<string>() == "subworkflows.clone_repository.planned_tools");
        var inspection = Assert.IsType<JsonObject>(result.Error.Details["pipeline_inspection"]);
        var mcpContext = Assert.IsType<JsonObject>(inspection["mcp_context"]);
        var toolNames = Assert.IsType<JsonArray>(mcpContext["tool_names"]);
        Assert.Contains(toolNames, tool => tool!.GetValue<string>() == "opaque/zqxv_process");
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_PromotesOptionalPlannedToolToRequiredAndEnforcesYaml()
    {
        var leafPrompt = "";
        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("git", new MockMcpServerConfig
        {
            Description = "Repository clone operations.",
            Tools =
            {
                new McpToolInfo
                {
                    Name = "git_clone",
                    Description = "Clone a repository into a local workspace directory.",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "remoteUrl": { "type": "string" },
                        "targetDirectory": { "type": "string" }
                      },
                      "required": ["remoteUrl", "targetDirectory"],
                      "additionalProperties": false
                    }
                    """)
                }
            }
        });

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Clone\n\nClone a repository." };

                if (request.Prompt.Contains("MCP server-selection assistant", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Json = JsonNode.Parse("{\"servers\":[{\"name\":\"git\",\"reason\":\"clone task\"}]}")!,
                        Text = "{\"servers\":[{\"name\":\"git\",\"reason\":\"clone task\"}]}"
                    };

                if (request.Prompt.Contains("tool-selection assistant", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Json = JsonNode.Parse("{\"servers\":[{\"name\":\"git\",\"tools\":[\"git_clone\"],\"prompts\":[]}]}")!,
                        Text = "{\"servers\":[{\"name\":\"git\",\"tools\":[\"git_clone\"],\"prompts\":[]}]}"
                    };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    const string annotatedMarkdown = """
                    # Clone

                    :::subworkflow name="clone_repository"
                    goal: Clone the repository.
                    inputs:
                      repository_url: string
                    outputs:
                      project_root: string
                    extract_reason: This performs external repository clone work.
                    content:
                      Clone the repository into a local workspace directory.
                    :::

                    ## Main workflow orchestration

                    Call clone_repository.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        annotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "clone_repository",
                                ["goal"] = "Clone the repository.",
                                ["description"] = "Clone the repository into the workspace.",
                                ["work_kind"] = "external_work",
                                ["contract_role"] = "external_action",
                                ["concrete_outcome"] = "A cloned repository directory that exists in the workspace.",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "repository_url",
                                        ["type"] = "string",
                                        ["description"] = "Repository URL.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "project_root",
                                        ["type"] = "string",
                                        ["description"] = "Cloned project root.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["extract_reason"] = "This performs external repository clone work.",
                                ["content"] = "Clone the repository into a local workspace directory.",
                                ["planned_tools"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["server"] = "git",
                                        ["kind"] = "tool",
                                        ["method"] = "git_clone",
                                        ["required"] = false,
                                        ["purpose"] = "Clone the repository.",
                                        ["consumes"] = new JsonArray { "repository_url" },
                                        ["produces"] = new JsonArray { "project_root" }
                                    }
                                }
                            }
                        },
                        "Call clone_repository.");
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `clone_repository`.", StringComparison.Ordinal))
                {
                    leafPrompt = request.Prompt;
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: clone-repository-leaf
                        skill:
                          description: Clone repository.
                          tags: [generated, leaf]
                          inputs:
                            repository_url: string
                          outputs:
                            project_root: string
                        workflows:
                          main:
                            inputs:
                              repository_url: string
                            steps:
                              - id: fake
                                type: set
                                input:
                                  project_root: clones/repo
                            outputs:
                              project_root:
                                expr: ${data.steps.fake.project_root}
                                type: string
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Clone a repository."
                  generator:
                    model: gpt-4
                    prefilter: true
                  validate:
                    compile: false
                    max_repair_attempts: 1
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true),
            McpClientFactory = mcpFactory
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("git/git_clone (tool, required)", leafPrompt);
        Assert.NotNull(result.Error);
        Assert.Contains("required planned MCP tool", result.Error!.Message);
        Assert.Contains("git/git_clone", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_AllowsAlgorithmicLlmLeafWithoutMcpToolContract()
    {
        var judgeCalls = 0;
        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("analysis", new MockMcpServerConfig
        {
            Description = "Issue analysis helpers.",
            Tools =
            {
                new McpToolInfo
                {
                    Name = "summarize_issue",
                    Description = "Summarize and classify an issue.",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "issue_body": { "type": "string" }
                      },
                      "required": ["issue_body"],
                      "additionalProperties": false
                    }
                    """)
                }
            }
        });

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Issue analysis\n\nSummarize and classify an issue with an LLM." };

                if (request.Prompt.Contains("MCP server-selection assistant", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Json = JsonNode.Parse("{\"servers\":[{\"name\":\"analysis\",\"reason\":\"analysis metadata is available\"}]}")!,
                        Text = "{\"servers\":[{\"name\":\"analysis\",\"reason\":\"analysis metadata is available\"}]}"
                    };

                if (request.Prompt.Contains("tool-selection assistant", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Json = JsonNode.Parse("{\"servers\":[{\"name\":\"analysis\",\"tools\":[\"summarize_issue\"],\"prompts\":[]}]}")!,
                        Text = "{\"servers\":[{\"name\":\"analysis\",\"tools\":[\"summarize_issue\"],\"prompts\":[]}]}"
                    };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    const string annotatedMarkdown = """
                    # Issue analysis

                    :::subworkflow name="analyze_issue_need"
                    goal: Summarize and classify the issue need.
                    inputs:
                      issue_body: string
                    outputs:
                      summary: string
                      classification: string
                    extract_reason: This is a nontrivial LLM analysis transform.
                    content:
                      Use an LLM to read the issue body, summarize the user need, classify whether it is a bug or question, and return typed fields for later routing.
                    :::

                    ## Main workflow orchestration

                    Call analyze_issue_need and expose the analysis.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        annotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "analyze_issue_need",
                                ["goal"] = "Summarize and classify the issue need.",
                                ["description"] = "LLM-only issue analysis.",
                                ["work_kind"] = "external_work",
                                ["contract_role"] = "algorithmic_transform",
                                ["concrete_outcome"] = "Typed issue summary and classification.",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "issue_body",
                                        ["type"] = "string",
                                        ["description"] = "Issue body.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "summary",
                                        ["type"] = "string",
                                        ["description"] = "Need summary.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "classification",
                                        ["type"] = "string",
                                        ["description"] = "Issue classification.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["extract_reason"] = "This is a nontrivial LLM analysis transform.",
                                ["content"] = "Use an LLM to read the issue body, summarize the user need, classify whether it is a bug or question, and return typed fields for later routing.",
                                ["planned_tools"] = new JsonArray()
                            }
                        },
                        "Call analyze_issue_need and expose the analysis.");
                }

                if (request.Prompt.Contains("reviewing the quality of a `workflow.plan` pipeline", StringComparison.Ordinal))
                {
                    judgeCalls++;
                    return CreateExtractionQualityReviewResponse(
                        94,
                        "pass",
                        retryGuidance: "Extraction is faithful and can proceed.");
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `analyze_issue_need`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: analyze-issue-need-leaf
                        skill:
                          description: Analyze issue need.
                          tags: [generated, leaf]
                          inputs:
                            issue_body: string
                          outputs:
                            summary: string
                            classification: string
                        workflows:
                          main:
                            inputs:
                              issue_body: string
                            steps:
                              - id: analyze
                                type: llm.call
                                input:
                                  model: gpt-4
                                  system: Summarize and classify the issue.
                                  prompt: ${data.inputs.issue_body}
                                  structured_output:
                                    strict: true
                                    schema_inline:
                                      type: object
                                      additionalProperties: false
                                      required: [summary, classification]
                                      properties:
                                        summary:
                                          type: string
                                        classification:
                                          type: string
                            outputs:
                              summary:
                                expr: ${data.steps.analyze.json.summary}
                                type: string
                              classification:
                                expr: ${data.steps.analyze.json.classification}
                                type: string
                        """
                    };

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: issue_analysis_pipeline
                          skill:
                            description: Analyze issue need.
                            inputs:
                              issue_body: string
                            outputs:
                              result:
                                type: object
                                properties:
                                  summary:
                                    type: string
                                  classification:
                                    type: string
                                required_properties: [summary, classification]
                        graph:
                          inputs:
                            issue_body: string
                          steps:
                            - id: call_analyze_issue_need
                              leaf: analyze_issue_need
                              args:
                                issue_body: ${data.inputs.issue_body}
                          outputs:
                            result:
                              expr: ${data.steps.call_analyze_issue_need.outputs}
                              type: object
                              properties:
                                summary:
                                  type: string
                                classification:
                                  type: string
                              required_properties: [summary, classification]
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Summarize and classify an issue with an LLM."
                  generator:
                    model: gpt-4
                    prefilter: true
                  validate:
                    compile: false
                    max_repair_attempts: 1
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true),
            McpClientFactory = mcpFactory
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var planOutput = Assert.IsType<JsonObject>(result.Outputs!["plan"]);
        Assert.Contains("type: llm.call", planOutput["yaml"]!.GetValue<string>());
        var pipeline = Assert.IsType<JsonObject>(planOutput["pipeline"]);
        var specs = Assert.IsType<JsonArray>(pipeline["specs"]!["subworkflows"]);
        var requiredCapabilities = Assert.IsType<JsonArray>(specs[0]!["required_capabilities"]);
        Assert.Empty(requiredCapabilities);
        Assert.Equal(1, judgeCalls);
        var qualityReview = Assert.IsType<JsonObject>(pipeline["quality_report"]!["extraction"]!["quality_review"]);
        Assert.Equal(94, qualityReview["score"]!.GetValue<int>());
        Assert.Equal("pass", qualityReview["verdict"]!.GetValue<string>());
        Assert.True(pipeline["quality_report"]!["checks"]!["extraction_quality_reviewed"]!.GetValue<bool>());
        Assert.Equal(94, pipeline["quality_report"]!["summary"]!["extraction_quality_score"]!.GetValue<int>());
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RepromptsStructuredExtractionWhenJudgeRequestsRetry()
    {
        var markAttempts = 0;
        var markRequests = new List<LLMRequest>();
        var judgeCalls = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) =>
            {
                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    markRequests.Add(request);
            })
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Issue automation\n\nClone a repository, classify an issue, and cleanup the clone." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    markAttempts++;
                    const string annotatedMarkdown = """
                    # Issue automation

                    :::subworkflow name="classify_issue_need"
                    goal: Classify the issue need.
                    inputs:
                      issue_body: string
                    outputs:
                      classification: string
                    extract_reason: This is a nontrivial LLM classification transform.
                    content:
                      Summarize the issue body, classify the user need, and return the classification for later routing.
                    :::

                    ## Main workflow orchestration

                    Call classify_issue_need and expose the classification.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        annotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "classify_issue_need",
                                ["goal"] = "Classify the issue need.",
                                ["description"] = "LLM-only issue classification.",
                                ["work_kind"] = "deterministic_shaping",
                                ["contract_role"] = "algorithmic_transform",
                                ["concrete_outcome"] = "Typed issue classification.",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "issue_body",
                                        ["type"] = "string",
                                        ["description"] = "Issue body.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "classification",
                                        ["type"] = "string",
                                        ["description"] = "Issue classification.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["extract_reason"] = "This is a nontrivial LLM classification transform.",
                                ["content"] = "Summarize the issue body, classify the user need, and return the classification for later routing.",
                                ["planned_tools"] = new JsonArray()
                            }
                        },
                        "Call classify_issue_need and expose the classification.");
                }

                if (request.Prompt.Contains("reviewing the quality of a `workflow.plan` pipeline", StringComparison.Ordinal))
                {
                    judgeCalls++;
                    if (judgeCalls == 1)
                    {
                        return CreateExtractionQualityReviewResponse(
                            42,
                            "retry",
                            new JsonArray
                            {
                                new JsonObject
                                {
                                    ["code"] = "MISSING_CLONE_WORK",
                                    ["severity"] = "critical",
                                    ["leaf_name"] = "",
                                    ["message"] = "The original prompt requires clone and cleanup work, but extraction only classifies the issue.",
                                    ["recommendation"] = "Add a clone-producing leaf and cleanup leaf, or explicitly keep simple orchestration in main while preserving these obligations."
                                }
                            },
                            "Add a clone-producing leaf before classification and preserve cleanup after processing.");
                    }

                    return CreateExtractionQualityReviewResponse(91, "pass", retryGuidance: "Corrected extraction can proceed.");
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `classify_issue_need`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: classify-issue-need-leaf
                        skill:
                          description: Classify issue need.
                          tags: [generated, leaf]
                          inputs:
                            issue_body: string
                          outputs:
                            classification: string
                        workflows:
                          main:
                            inputs:
                              issue_body: string
                            steps:
                              - id: classify
                                type: llm.call
                                input:
                                  model: gpt-4
                                  system: Classify the issue.
                                  prompt: ${data.inputs.issue_body}
                                  structured_output:
                                    strict: true
                                    schema_inline:
                                      type: object
                                      additionalProperties: false
                                      required: [classification]
                                      properties:
                                        classification:
                                          type: string
                            outputs:
                              classification:
                                expr: ${data.steps.classify.json.classification}
                                type: string
                        """
                    };

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: issue_classification_pipeline
                          skill:
                            description: Classify issue.
                            inputs:
                              issue_body: string
                            outputs:
                              classification: string
                        graph:
                          inputs:
                            issue_body: string
                          steps:
                            - id: call_classify_issue_need
                              leaf: classify_issue_need
                              args:
                                issue_body: ${data.inputs.issue_body}
                          outputs:
                            classification: ${data.steps.call_classify_issue_need.outputs.classification}
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Clone a repository, classify an issue, and cleanup the clone."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 2
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true)
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, markAttempts);
        Assert.Equal(2, judgeCalls);
        Assert.Equal(2, markRequests.Count);
        Assert.Contains("MISSING_CLONE_WORK", markRequests[1].Prompt);
        Assert.Contains("Add a clone-producing leaf", markRequests[1].Prompt);
        var qualityReview = result.Outputs!["plan"]!["pipeline"]!["quality_report"]!["extraction"]!["quality_review"]!;
        Assert.Equal(91, qualityReview["score"]!.GetValue<int>());
        Assert.Equal("pass", qualityReview["verdict"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RepromptsMarkdownExtractionWhenJudgeRequestsRetry()
    {
        var markAttempts = 0;
        var markRequests = new List<LLMRequest>();
        var judgeRequests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) =>
            {
                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    markRequests.Add(request);
                if (request.Prompt.Contains("reviewing the quality of a `workflow.plan` pipeline", StringComparison.Ordinal))
                    judgeRequests.Add(request);
            })
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Issue analysis\n\nSummarize and classify an issue." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    markAttempts++;
                    if (markAttempts == 1)
                    {
                        return new LLMResponse
                        {
                            Text = """
                            # Issue analysis

                            :::subworkflow name="analyze_issue_need"
                            goal: Summarize the issue need.
                            inputs:
                              issue_body: string
                            outputs:
                              summary: string
                            extract_reason: This is a nontrivial LLM analysis transform.
                            content:
                              Use an LLM to read the issue body and summarize the user need.
                            :::

                            ## Main workflow orchestration

                            Call analyze_issue_need and expose the summary.
                            """
                        };
                    }

                    return new LLMResponse
                    {
                        Text = """
                        # Issue analysis

                        :::subworkflow name="analyze_issue_need"
                        goal: Summarize and classify the issue need.
                        inputs:
                          issue_body: string
                        outputs:
                          summary: string
                          classification: string
                        extract_reason: This is a nontrivial LLM analysis transform.
                        content:
                          Use an LLM to read the issue body, summarize the user need, classify whether it is a bug or question, and return typed fields for later routing.
                        :::

                        ## Main workflow orchestration

                        Call analyze_issue_need and expose the analysis.
                        """
                    };
                }

                if (request.Prompt.Contains("reviewing the quality of a `workflow.plan` pipeline", StringComparison.Ordinal))
                {
                    Assert.Null(request.StructuredOutputSchema);
                    Assert.Null(request.StructuredOutputStrict);

                    if (judgeRequests.Count == 1)
                    {
                        return new LLMResponse
                        {
                            Text = CreateExtractionQualityReviewResponse(
                                63,
                                "retry",
                                new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["code"] = "MISSING_CLASSIFICATION_OUTPUT",
                                        ["severity"] = "critical",
                                        ["leaf_name"] = "analyze_issue_need",
                                        ["message"] = "The original prompt asks for classification, but the extracted leaf only summarizes.",
                                        ["recommendation"] = "Add a concrete classification output to the analysis leaf."
                                    }
                                },
                                "Add a typed classification output and mention it in the main orchestration.").Text
                        };
                    }

                    return new LLMResponse
                    {
                        Text = CreateExtractionQualityReviewResponse(
                            90,
                            "pass",
                            retryGuidance: "Corrected extraction can proceed.").Text
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `analyze_issue_need`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: analyze-issue-need-leaf
                        skill:
                          description: Analyze issue need.
                          tags: [generated, leaf]
                          inputs:
                            issue_body: string
                          outputs:
                            summary: string
                            classification: string
                        workflows:
                          main:
                            inputs:
                              issue_body: string
                            steps:
                              - id: analyze
                                type: llm.call
                                input:
                                  model: gpt-4
                                  system: Summarize and classify the issue.
                                  prompt: ${data.inputs.issue_body}
                                  structured_output:
                                    strict: true
                                    schema_inline:
                                      type: object
                                      additionalProperties: false
                                      required: [summary, classification]
                                      properties:
                                        summary:
                                          type: string
                                        classification:
                                          type: string
                            outputs:
                              summary:
                                expr: ${data.steps.analyze.json.summary}
                                type: string
                              classification:
                                expr: ${data.steps.analyze.json.classification}
                                type: string
                        """
                    };

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: issue_analysis_pipeline
                          skill:
                            description: Analyze issue need.
                            inputs:
                              issue_body: string
                            outputs:
                              result:
                                type: object
                                properties:
                                  summary:
                                    type: string
                                  classification:
                                    type: string
                                required_properties: [summary, classification]
                        graph:
                          inputs:
                            issue_body: string
                          steps:
                            - id: call_analyze_issue_need
                              leaf: analyze_issue_need
                              args:
                                issue_body: ${data.inputs.issue_body}
                          outputs:
                            result:
                              expr: ${data.steps.call_analyze_issue_need.outputs}
                              type: object
                              properties:
                                summary:
                                  type: string
                                classification:
                                  type: string
                              required_properties: [summary, classification]
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Summarize and classify an issue."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 2
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(false)
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, markAttempts);
        Assert.Equal(2, markRequests.Count);
        Assert.Equal(2, judgeRequests.Count);
        Assert.Null(markRequests[0].StructuredOutputSchema);
        Assert.Null(markRequests[1].StructuredOutputSchema);
        Assert.Contains("MISSING_CLASSIFICATION_OUTPUT", markRequests[1].Prompt);
        Assert.Contains("Add a concrete classification output", markRequests[1].Prompt);
        var qualityReview = result.Outputs!["plan"]!["pipeline"]!["quality_report"]!["extraction"]!["quality_review"]!;
        Assert.Equal(90, qualityReview["score"]!.GetValue<int>());
        Assert.Equal("pass", qualityReview["verdict"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_ExtractionQualityJudgeInvalidOutputRecordsWarningAndContinues()
    {
        var judgeCalls = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Issue analysis\n\nSummarize and classify an issue." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    const string annotatedMarkdown = """
                    # Issue analysis

                    :::subworkflow name="analyze_issue_need"
                    goal: Summarize and classify the issue need.
                    inputs:
                      issue_body: string
                    outputs:
                      summary: string
                    extract_reason: This is a nontrivial LLM analysis transform.
                    content:
                      Use an LLM to read the issue body, summarize the user need, and return typed fields for later routing.
                    :::

                    ## Main workflow orchestration

                    Call analyze_issue_need and expose the summary.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        annotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "analyze_issue_need",
                                ["goal"] = "Summarize and classify the issue need.",
                                ["description"] = "LLM-only issue analysis.",
                                ["work_kind"] = "deterministic_shaping",
                                ["contract_role"] = "algorithmic_transform",
                                ["concrete_outcome"] = "Typed issue summary.",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "issue_body",
                                        ["type"] = "string",
                                        ["description"] = "Issue body.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "summary",
                                        ["type"] = "string",
                                        ["description"] = "Need summary.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["extract_reason"] = "This is a nontrivial LLM analysis transform.",
                                ["content"] = "Use an LLM to read the issue body, summarize the user need, and return typed fields for later routing.",
                                ["planned_tools"] = new JsonArray()
                            }
                        },
                        "Call analyze_issue_need and expose the summary.");
                }

                if (request.Prompt.Contains("reviewing the quality of a `workflow.plan` pipeline", StringComparison.Ordinal))
                {
                    judgeCalls++;
                    return new LLMResponse { Text = "not-json" };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `analyze_issue_need`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: analyze-issue-need-leaf
                        skill:
                          description: Analyze issue need.
                          tags: [generated, leaf]
                          inputs:
                            issue_body: string
                          outputs:
                            summary: string
                        workflows:
                          main:
                            inputs:
                              issue_body: string
                            steps:
                              - id: analyze
                                type: llm.call
                                input:
                                  model: gpt-4
                                  system: Summarize the issue.
                                  prompt: ${data.inputs.issue_body}
                                  structured_output:
                                    strict: true
                                    schema_inline:
                                      type: object
                                      additionalProperties: false
                                      required: [summary]
                                      properties:
                                        summary:
                                          type: string
                            outputs:
                              summary:
                                expr: ${data.steps.analyze.json.summary}
                                type: string
                        """
                    };

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: issue_analysis_pipeline
                          skill:
                            description: Analyze issue.
                            inputs:
                              issue_body: string
                            outputs:
                              summary: string
                        graph:
                          inputs:
                            issue_body: string
                          steps:
                            - id: call_analyze_issue_need
                              leaf: analyze_issue_need
                              args:
                                issue_body: ${data.inputs.issue_body}
                          outputs:
                            summary: ${data.steps.call_analyze_issue_need.outputs.summary}
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Summarize and classify an issue."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 1
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true)
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, judgeCalls);
        var qualityReport = result.Outputs!["plan"]!["pipeline"]!["quality_report"]!;
        Assert.False(qualityReport["checks"]!["extraction_quality_reviewed"]!.GetValue<bool>());
        var warning = Assert.Single(qualityReport["warnings"]!.AsArray());
        Assert.Equal("PIPELINE_EXTRACTION_QUALITY_REVIEW_WARNING", warning!["code"]!.GetValue<string>());
        Assert.Contains("review_extraction_quality failed", warning["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RepromptsMainWhenUrlInputIsAssignedToParsedIdentifiers()
    {
        var mainAssemblyRequests = 0;
        var repairPrompt = "";
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Repository automation\n\nParse repository identity and analyze it." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    const string annotatedMarkdown = """
                    # Repository automation

                    :::subworkflow name="parse_repository_identity"
                    goal: Parse repository identity from a repository URL.
                    inputs:
                      repository_url: string
                    outputs:
                      owner: string
                      repo: string
                    extract_reason: This exposes canonical parsed identifiers for later workflow steps.
                    content:
                      Parse the repository URL into owner and repo identifiers.
                    :::

                    :::subworkflow name="analyze_repository_identity"
                    goal: Analyze the parsed repository identity.
                    inputs:
                      owner: string
                      repo: string
                    outputs:
                      summary: string
                    extract_reason: This consumes parsed identifiers.
                    content:
                      Build a short summary from owner and repo.
                    :::

                    ## Main workflow orchestration

                    Parse the repository URL first, then pass parsed owner and repo to analysis.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        annotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "parse_repository_identity",
                                ["goal"] = "Parse repository identity from a repository URL.",
                                ["description"] = "Produce canonical owner and repo identifiers.",
                                ["work_kind"] = "deterministic_shaping",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "repository_url",
                                        ["type"] = "string",
                                        ["description"] = "Repository URL.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "owner",
                                        ["type"] = "string",
                                        ["description"] = "Repository owner identifier.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "repo",
                                        ["type"] = "string",
                                        ["description"] = "Repository name identifier.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["extract_reason"] = "This exposes canonical parsed identifiers for later workflow steps.",
                                ["content"] = "Parse the repository URL into owner and repo identifiers.",
                                ["planned_tools"] = new JsonArray()
                            },
                            new JsonObject
                            {
                                ["name"] = "analyze_repository_identity",
                                ["goal"] = "Analyze the parsed repository identity.",
                                ["description"] = "Build a summary from parsed identifiers.",
                                ["work_kind"] = "deterministic_shaping",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "owner",
                                        ["type"] = "string",
                                        ["description"] = "Repository owner identifier.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    },
                                    new JsonObject
                                    {
                                        ["name"] = "repo",
                                        ["type"] = "string",
                                        ["description"] = "Repository name identifier.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "summary",
                                        ["type"] = "string",
                                        ["description"] = "Analysis summary.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["extract_reason"] = "This consumes parsed identifiers.",
                                ["content"] = "Build a short summary from owner and repo.",
                                ["planned_tools"] = new JsonArray()
                            }
                        },
                        "Parse the repository URL first, then pass parsed owner and repo to analysis.");
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `parse_repository_identity`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: parse-repository-identity-leaf
                        skill:
                          description: Parse repository identity.
                          tags: [generated, leaf]
                          inputs:
                            repository_url: string
                          outputs:
                            owner: string
                            repo: string
                        workflows:
                          main:
                            inputs:
                              repository_url: string
                            steps:
                              - id: parsed
                                type: set
                                input:
                                  owner: AxaFrance
                                  repo: oidc-client
                            outputs:
                              owner:
                                expr: ${data.steps.parsed.owner}
                                type: string
                              repo:
                                expr: ${data.steps.parsed.repo}
                                type: string
                        """
                    };

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `analyze_repository_identity`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: analyze-repository-identity-leaf
                        skill:
                          description: Analyze repository identity.
                          tags: [generated, leaf]
                          inputs:
                            owner: string
                            repo: string
                          outputs:
                            summary: string
                        workflows:
                          main:
                            inputs:
                              owner: string
                              repo: string
                            steps:
                              - id: summary
                                type: set
                                input:
                                  summary: "${data.inputs.owner}/${data.inputs.repo}"
                            outputs:
                              summary:
                                expr: ${data.steps.summary.summary}
                                type: string
                        """
                    };

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    mainAssemblyRequests++;
                    if (request.Prompt.Contains("PIPELINE_MAIN_SUSPICIOUS_NARROWING", StringComparison.Ordinal))
                        repairPrompt = request.Prompt;

                    if (mainAssemblyRequests == 1)
                    {
                        return new LLMResponse
                        {
                            Text = """
                            document:
                              name: repository_identity_pipeline
                              skill:
                                description: Analyze repository identity.
                                inputs:
                                  repository_url: string
                                outputs:
                                  summary: string
                            graph:
                              inputs:
                                repository_url: string
                              steps:
                                - id: derive_repo_identity
                                  type: set
                                  input:
                                    owner: ${data.inputs.repository_url}
                                    repo: ${data.inputs.repository_url}
                                - id: call_analyze_repository_identity
                                  leaf: analyze_repository_identity
                                  args:
                                    owner: ${data.steps.derive_repo_identity.owner}
                                    repo: ${data.steps.derive_repo_identity.repo}
                              outputs:
                                summary: ${data.steps.call_analyze_repository_identity.outputs.summary}
                            """
                        };
                    }

                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: repository_identity_pipeline
                          skill:
                            description: Analyze repository identity.
                            inputs:
                              repository_url: string
                            outputs:
                              summary: string
                        graph:
                          inputs:
                            repository_url: string
                          steps:
                            - id: call_parse_repository_identity
                              leaf: parse_repository_identity
                              args:
                                repository_url: ${data.inputs.repository_url}
                            - id: call_analyze_repository_identity
                              leaf: analyze_repository_identity
                              args:
                                owner: ${data.steps.call_parse_repository_identity.outputs.owner}
                                repo: ${data.steps.call_parse_repository_identity.outputs.repo}
                          outputs:
                            summary: ${data.steps.call_analyze_repository_identity.outputs.summary}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Parse repository identity and analyze it."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 2
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true)
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, mainAssemblyRequests);
        Assert.Contains("PIPELINE_MAIN_SUSPICIOUS_NARROWING", repairPrompt);
        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("owner: ${data.steps.call_parse_repository_identity.outputs.owner}", yaml);
        Assert.DoesNotContain("owner: ${data.inputs.repository_url}", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RejectsCloneLeafThatOnlyEmitsCommand()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Clone\n\nClone a repository into the workspace." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Clone

                        :::subworkflow name="clone_repository"
                        goal: Clone the repository.
                        inputs:
                          repository_url: string
                        outputs:
                          project_root: string
                        extract_reason: This performs external repository clone work.
                        content:
                          Clone the repository into a local workspace directory.
                        :::

                        ## Main workflow orchestration

                        Call clone_repository.
                        """
                    };

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `clone_repository`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: clone-repository-leaf
                        skill:
                          description: Clone repository.
                          tags: [generated, leaf]
                          inputs:
                            repository_url: string
                          outputs:
                            project_root: string
                        workflows:
                          main:
                            inputs:
                              repository_url: string
                            steps:
                              - id: tell_user
                                type: emit
                                input:
                                  message: "Run git clone ${data.inputs.repository_url} clones/repo"
                              - id: result
                                type: set
                                input:
                                  project_root: clones/repo
                            outputs:
                              project_root:
                                expr: ${data.steps.result.project_root}
                                type: string
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Clone a repository."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("PIPELINE_LEAF_FAKE_ACTION_EMIT", result.Error!.Message);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RejectsCleanupLeafThatOnlySetsSuccessTrue()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Cleanup\n\nClean up a local directory." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Cleanup

                        :::subworkflow name="cleanup_workspace_directory"
                        goal: Clean up a local workspace directory.
                        inputs:
                          project_root: string
                        outputs:
                          cleanup_success: boolean
                        extract_reason: This performs external cleanup work.
                        content:
                          Clean up the local directory and report whether cleanup succeeded.
                        :::

                        ## Main workflow orchestration

                        Call cleanup_workspace_directory.
                        """
                    };

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `cleanup_workspace_directory`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: cleanup-workspace-directory-leaf
                        skill:
                          description: Clean up workspace directory.
                          tags: [generated, leaf]
                          inputs:
                            project_root: string
                          outputs:
                            cleanup_success: boolean
                        workflows:
                          main:
                            inputs:
                              project_root: string
                            steps:
                              - id: result
                                type: set
                                input:
                                  cleanup_success: true
                            outputs:
                              cleanup_success:
                                expr: ${data.steps.result.cleanup_success}
                                type: boolean
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Clean up a workspace directory."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("PIPELINE_LEAF_SUCCESS_OUTPUT_WITHOUT_ACTION", result.Error!.Message);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RepromptsStructuredExtractionWhenExternalWorkOmitsPlannedTools()
    {
        var markRequests = new List<LLMRequest>();
        var markAttempts = 0;
        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("git", new MockMcpServerConfig
        {
            Description = "Repository clone operations.",
            Tools =
            {
                new McpToolInfo
                {
                    Name = "git_clone",
                    Description = "Clone a repository into a local workspace directory.",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "remoteUrl": { "type": "string" },
                        "targetDirectory": { "type": "string" }
                      },
                      "required": ["remoteUrl", "targetDirectory"],
                      "additionalProperties": false
                    }
                    """),
                    OutputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "project_root": { "type": "string" }
                      },
                      "required": ["project_root"],
                      "additionalProperties": false
                    }
                    """)
                }
            }
        });

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) =>
            {
                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    markRequests.Add(request);
            })
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Clone\n\nClone a repository." };

                if (request.Prompt.Contains("MCP server-selection assistant", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Json = JsonNode.Parse("{\"servers\":[{\"name\":\"git\",\"reason\":\"clone task\"}]}")!,
                        Text = "{\"servers\":[{\"name\":\"git\",\"reason\":\"clone task\"}]}"
                    };

                if (request.Prompt.Contains("tool-selection assistant", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Json = JsonNode.Parse("{\"servers\":[{\"name\":\"git\",\"tools\":[\"git_clone\"],\"prompts\":[]}]}")!,
                        Text = "{\"servers\":[{\"name\":\"git\",\"tools\":[\"git_clone\"],\"prompts\":[]}]}"
                    };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    markAttempts++;
                    var plannedTools = markAttempts == 1
                        ? new JsonArray()
                        : new JsonArray
                        {
                            new JsonObject
                            {
                                ["server"] = "git",
                                ["kind"] = "tool",
                                ["method"] = "git_clone",
                                ["required"] = true,
                                ["purpose"] = "Clone the repository.",
                                ["consumes"] = new JsonArray { "repository_url" },
                                ["produces"] = new JsonArray { "project_root" }
                            }
                        };

                    const string annotatedMarkdown = """
                    # Clone

                    :::subworkflow name="clone_repository"
                    goal: Clone the repository.
                    inputs:
                      repository_url: string
                    outputs:
                      project_root: string
                    extract_reason: This performs external repository clone work.
                    content:
                      Clone the repository into a local workspace directory.
                    :::

                    ## Main workflow orchestration

                    Call clone_repository.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        annotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "clone_repository",
                                ["goal"] = "Clone the repository.",
                                ["description"] = "Clone the repository into the workspace.",
                                ["work_kind"] = "external_work",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "repository_url",
                                        ["type"] = "string",
                                        ["description"] = "Repository URL.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "project_root",
                                        ["type"] = "string",
                                        ["description"] = "Cloned project root.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["extract_reason"] = "This performs external repository clone work.",
                                ["content"] = "Clone the repository into a local workspace directory.",
                                ["planned_tools"] = plannedTools
                            }
                        },
                        "Call clone_repository.");
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `clone_repository`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: clone-repository-leaf
                        skill:
                          description: Clone repository.
                          tags: [generated, leaf]
                          inputs:
                            repository_url: string
                          outputs:
                            project_root: string
                        workflows:
                          main:
                            inputs:
                              repository_url: string
                            steps:
                              - id: clone
                                type: mcp.call
                                input:
                                  server: git
                                  kind: tool
                                  method: git_clone
                                  request:
                                    remoteUrl: ${data.inputs.repository_url}
                                    targetDirectory: clones/repo
                            outputs:
                              project_root:
                                expr: ${data.steps.clone.response.project_root}
                                type: string
                        """
                    };

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: clone_repository_pipeline
                          skill:
                            description: Clone repository.
                            inputs:
                              repository_url: string
                            outputs:
                              project_root: string
                        graph:
                          inputs:
                            repository_url: string
                          steps:
                            - id: call_clone_repository
                              leaf: clone_repository
                              args:
                                repository_url: ${data.inputs.repository_url}
                          outputs:
                            project_root: ${data.steps.call_clone_repository.outputs.project_root}
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Clone a repository."
                  generator:
                    model: gpt-4
                  validate:
                    compile: false
                    max_repair_attempts: 2
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true),
            McpClientFactory = mcpFactory
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, markAttempts);
        Assert.Equal(2, markRequests.Count);
        Assert.Contains("declares no planned_tools", markRequests[1].Prompt);
        Assert.Contains("git/git_clone", markRequests[1].Prompt);
        var planOutput = Assert.IsType<JsonObject>(result.Outputs!["plan"]);
        var pipeline = Assert.IsType<JsonObject>(planOutput["pipeline"]);
        var qualityReport = Assert.IsType<JsonObject>(pipeline["quality_report"]);
        var qualityMcpContext = Assert.IsType<JsonObject>(qualityReport["mcp_context"]);
        Assert.Equal(1, qualityMcpContext["selected_server_count"]!.GetValue<int>());
        Assert.Equal(1, qualityMcpContext["selected_tool_count"]!.GetValue<int>());
        var qualityToolNames = Assert.IsType<JsonArray>(qualityMcpContext["tool_names"]);
        Assert.Contains(qualityToolNames, tool => tool!.GetValue<string>() == "git/git_clone");

        var inspection = Assert.IsType<JsonObject>(pipeline["inspection"]);
        var inspectionMcpContext = Assert.IsType<JsonObject>(inspection["mcp_context"]);
        Assert.Equal(1, inspectionMcpContext["selected_server_count"]!.GetValue<int>());
        var inspectionServerNames = Assert.IsType<JsonArray>(inspectionMcpContext["server_names"]);
        Assert.Contains(inspectionServerNames, server => server!.GetValue<string>() == "git");
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RepromptsStructuredExtractionWhenScoreIsWeak()
    {
        var markRequests = new List<LLMRequest>();
        var markAttempts = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) =>
            {
                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    markRequests.Add(request);
            })
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Identifier\n\nNormalize an identifier for later use." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    markAttempts++;
                    if (markAttempts == 1)
                    {
                        const string weakAnnotatedMarkdown = """
                        # Identifier

                        :::subworkflow name="rename_identifier"
                        goal: Rename raw_id to id.
                        inputs:
                          raw_id: string
                        outputs:
                          id: string
                        extract_reason: This maps fields.
                        content:
                          Rename raw_id to id.
                        :::

                        ## Main workflow orchestration

                        Call rename_identifier.
                        """;

                        return CreateStructuredMarkExtractableBlocksResponse(
                            weakAnnotatedMarkdown,
                            new JsonArray
                            {
                                new JsonObject
                                {
                                    ["name"] = "rename_identifier",
                                    ["goal"] = "Rename raw_id to id.",
                                    ["description"] = "Simple field rename.",
                                    ["work_kind"] = "deterministic_shaping",
                                    ["inputs"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["name"] = "raw_id",
                                            ["type"] = "string",
                                            ["description"] = "Raw identifier.",
                                            ["required"] = true,
                                            ["item_type"] = "",
                                            ["properties"] = new JsonArray()
                                        }
                                    },
                                    ["outputs"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["name"] = "id",
                                            ["type"] = "string",
                                            ["description"] = "Renamed identifier.",
                                            ["required"] = true,
                                            ["item_type"] = "",
                                            ["properties"] = new JsonArray()
                                        }
                                    },
                                    ["extract_reason"] = "This maps fields.",
                                    ["content"] = "Rename raw_id to id.",
                                    ["planned_tools"] = new JsonArray()
                                }
                            },
                            "Call rename_identifier.");
                    }

                    const string strongAnnotatedMarkdown = """
                    # Identifier

                    :::subworkflow name="parse_identifier"
                    goal: Parse and normalize the raw identifier.
                    inputs:
                      raw_id: string
                    outputs:
                      id: string
                    extract_reason: This is a reusable parsing and normalization operation.
                    content:
                      Parse the raw identifier, trim whitespace, split any optional prefix, normalize the casing, validate that a non-empty canonical identifier remains, and return the canonical id.
                    :::

                    ## Main workflow orchestration

                    Call parse_identifier, then expose the canonical id.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        strongAnnotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "parse_identifier",
                                ["goal"] = "Parse and normalize the raw identifier.",
                                ["description"] = "Produce a canonical identifier.",
                                ["work_kind"] = "deterministic_shaping",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "raw_id",
                                        ["type"] = "string",
                                        ["description"] = "Raw identifier.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "id",
                                        ["type"] = "string",
                                        ["description"] = "Canonical identifier.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["extract_reason"] = "This is a reusable parsing and normalization operation.",
                                ["content"] = "Parse the raw identifier, trim whitespace, split any optional prefix, normalize the casing, validate that a non-empty canonical identifier remains, and return the canonical id.",
                                ["planned_tools"] = new JsonArray()
                            }
                        },
                        "Call parse_identifier, then expose the canonical id.");
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `parse_identifier`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: parse-identifier-leaf
                        skill:
                          description: Parse identifier.
                          tags: [generated, leaf]
                          inputs:
                            raw_id: string
                          outputs:
                            id: string
                        workflows:
                          main:
                            inputs:
                              raw_id: string
                            steps:
                              - id: parsed
                                type: set
                                input:
                                  id: "${data.inputs.raw_id}"
                            outputs:
                              id:
                                expr: "${data.steps.parsed.id}"
                                type: string
                        """
                    };

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: parse_identifier_pipeline
                          skill:
                            description: Parse identifier.
                            inputs:
                              raw_id: string
                            outputs:
                              id: string
                        graph:
                          inputs:
                            raw_id: string
                          steps:
                            - id: call_parse_identifier
                              leaf: parse_identifier
                              args:
                                raw_id: ${data.inputs.raw_id}
                          outputs:
                            id: ${data.steps.call_parse_identifier.outputs.id}
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Normalize an identifier."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 2
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true)
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, markAttempts);
        Assert.Equal(2, markRequests.Count);
        Assert.Contains("PIPELINE_EXTRACTION_TRIVIAL_LEAF", markRequests[1].Prompt);
        Assert.Contains("PIPELINE_EXTRACTION_LOW_SCORE", markRequests[1].Prompt);
        Assert.Contains("Fix low extraction scores", markRequests[1].Prompt);

        var qualityReport = result.Outputs!["plan"]!["pipeline"]!["quality_report"]!;
        Assert.Equal(1, qualityReport["summary"]!["extraction_scored_leaf_count"]!.GetValue<int>());
        Assert.True(qualityReport["summary"]!["min_extraction_score"]!.GetValue<int>() >= 45);
        var leaf = Assert.Single(qualityReport["leaves"]!.AsArray());
        Assert.Equal("parse_identifier", leaf!["name"]!.GetValue<string>());
        Assert.Equal("acceptable", leaf["extraction_score"]!["rating"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RejectsAbstractStructuredLeafBeforeGeneration()
    {
        var leafGenerationRequested = false;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Issue automation\n\nCoordinate the full issue lifecycle." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    const string annotatedMarkdown = """
                    # Issue automation

                    :::subworkflow name="coordinate_issue_lifecycle"
                    goal: Coordinate the issue lifecycle.
                    inputs:
                      repository_url: string
                    outputs:
                      status: string
                    extract_reason: This describes the overall orchestration policy.
                    content:
                      Decide how the overall issue lifecycle should be coordinated across all phases.
                    :::

                    ## Main workflow orchestration

                    Keep lifecycle orchestration in main.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        annotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "coordinate_issue_lifecycle",
                                ["goal"] = "Coordinate the issue lifecycle.",
                                ["description"] = "Cross-cutting lifecycle policy.",
                                ["work_kind"] = "orchestration",
                                ["contract_role"] = "abstract_policy",
                                ["concrete_outcome"] = "Policy guidance for main orchestration.",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "repository_url",
                                        ["type"] = "string",
                                        ["description"] = "Repository URL.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "status",
                                        ["type"] = "string",
                                        ["description"] = "Lifecycle status.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["extract_reason"] = "This describes the overall orchestration policy.",
                                ["content"] = "Decide how the overall issue lifecycle should be coordinated across all phases.",
                                ["planned_tools"] = new JsonArray()
                            }
                        },
                        "Keep lifecycle orchestration in main.");
                }

                if (request.Prompt.Contains("Generate exactly one leaf", StringComparison.Ordinal))
                    leafGenerationRequested = true;

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Coordinate issue lifecycle."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 1
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true)
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(leafGenerationRequested);
        Assert.NotNull(result.Error);
        Assert.Contains("PIPELINE_EXTRACTION_NON_LEAF_ROLE", result.Error!.Message);
        var rootCauses = result.Error.Details!["root_causes"]!.AsArray();
        Assert.Contains(rootCauses, cause =>
            cause!["category"]!.GetValue<string>() == "abstract_leaf"
            && cause["invalid_path"]!.GetValue<string>() == "subworkflows.coordinate_issue_lifecycle.contract_role");
        Assert.NotNull(result.Error.Details!["pipeline_inspection"]);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RejectsWeakExtractionOutputContractBeforeGeneration()
    {
        var leafGenerationRequested = false;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Records\n\nCollect records and return them." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    const string annotatedMarkdown = """
                    # Records

                    :::subworkflow name="collect_records"
                    goal: Collect records.
                    inputs:
                      query: string
                    outputs:
                      records: array
                    extract_reason: This leaf produces a typed record collection.
                    content:
                      Produce the record collection needed by later orchestration.
                    :::

                    ## Main workflow orchestration

                    Call collect_records and loop over records.
                    """;

                    return CreateStructuredMarkExtractableBlocksResponse(
                        annotatedMarkdown,
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "collect_records",
                                ["goal"] = "Collect records.",
                                ["description"] = "Produces records for downstream processing.",
                                ["work_kind"] = "external_work",
                                ["contract_role"] = "typed_data_producer",
                                ["concrete_outcome"] = "A record array for downstream looping.",
                                ["inputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "query",
                                        ["type"] = "string",
                                        ["description"] = "Query.",
                                        ["required"] = true,
                                        ["item_type"] = "",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["outputs"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["name"] = "records",
                                        ["type"] = "array",
                                        ["description"] = "Records.",
                                        ["required"] = true,
                                        ["item_type"] = "any",
                                        ["properties"] = new JsonArray()
                                    }
                                },
                                ["extract_reason"] = "This leaf produces a typed record collection.",
                                ["content"] = "Produce the record collection needed by later orchestration.",
                                ["planned_tools"] = new JsonArray()
                            }
                        },
                        "Call collect_records and loop over records.");
                }

                if (request.Prompt.Contains("Generate exactly one leaf", StringComparison.Ordinal))
                    leafGenerationRequested = true;

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 1
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true)
        }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(leafGenerationRequested);
        Assert.NotNull(result.Error);
        Assert.Contains("WEAK_EXTRACTION_OUTPUT_SCHEMA", result.Error!.Message);
        var rootCauses = result.Error.Details!["root_causes"]!.AsArray();
        Assert.Contains(rootCauses, cause =>
            cause!["category"]!.GetValue<string>() == "weak_extraction_contract"
            && cause["invalid_path"]!.GetValue<string>() == "subworkflows.collect_records.outputs.records.items");
        Assert.Equal(
            rootCauses.Count,
            result.Error.Details!["pipeline_inspection"]!["summary"]!["root_cause_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task WorkflowPlan_BasicMode_RepromptsWhenGeneratedOutputsAreWeak()
    {
        var requests = new List<LLMRequest>();
        var generationAttempts = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) => requests.Add(request))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                generationAttempts++;
                if (generationAttempts == 1)
                {
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: weak-output-workflow
                        skill:
                          description: Produce issue results.
                          tags: [generated]
                          inputs: {}
                          outputs:
                            result:
                              type: array
                              description: Per-issue results.
                        workflows:
                          main:
                            steps:
                              - id: build
                                type: set
                                input:
                                  result: []
                            outputs:
                              result:
                                expr: ${data.steps.build.result}
                                type: array
                        """
                    };
                }

                Assert.Contains("WEAK_OUTPUT_SCHEMA", request.Prompt);
                Assert.Contains("skill.outputs.result", request.Prompt);
                Assert.Contains("workflows.main.outputs.result", request.Prompt);
                return new LLMResponse
                {
                    Text = """
                    version: 1
                    name: strong-output-workflow
                    skill:
                      description: Produce issue results.
                      tags: [generated]
                      inputs: {}
                      outputs:
                        result:
                          type: array
                          description: Per-issue results.
                          items:
                            type: object
                            properties:
                              issue_number:
                                type: number
                              status:
                                type: string
                            required_properties: [issue_number, status]
                    workflows:
                      main:
                        steps:
                          - id: build
                            type: set
                            output_schema:
                              type: object
                              properties:
                                result:
                                  type: array
                                  items:
                                    type: object
                                    properties:
                                      issue_number:
                                        type: number
                                      status:
                                        type: string
                                    required_properties: [issue_number, status]
                            input:
                              result: []
                        outputs:
                          result:
                            expr: ${data.steps.build.result}
                            type: array
                            items:
                              type: object
                              properties:
                                issue_number:
                                  type: number
                                status:
                                  type: string
                              required_properties: [issue_number, status]
                    """
                };
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Produce issue results.
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 2
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, generationAttempts);
        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("items:", yaml);
        Assert.Contains("issue_number:", yaml);
        Assert.Contains("status:", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_StrengthensPublicOutputSchemaFromLeafContract()
    {
        var mainAssemblyAttempts = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Results\n\nBuild per-issue results." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Results

                        :::subworkflow name="collect_results"
                        goal: Collect per-issue results.
                        inputs:
                          query: string
                        outputs:
                          result: array
                        extract_reason: This creates typed per-issue result records.
                        content:
                          Build an empty per-issue result list for the query.
                        :::

                        ## Main workflow orchestration

                        Call collect_results and expose result.
                        """
                    };

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_results`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-results-leaf
                        skill:
                          description: Collect per-issue results.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            result:
                              type: array
                              items:
                                type: object
                                properties:
                                  issue_number:
                                    type: number
                                  status:
                                    type: string
                                required_properties: [issue_number, status]
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: build
                                type: llm.call
                                input:
                                  model: gpt-4
                                  prompt: "Return an empty array of issue processing results."
                                  structured_output:
                                    strict: true
                                    schema_inline:
                                      type: object
                                      additionalProperties: false
                                      required: [result]
                                      properties:
                                        result:
                                          type: array
                                          items:
                                            type: object
                                            additionalProperties: false
                                            required: [issue_number, status]
                                            properties:
                                              issue_number:
                                                type: number
                                              status:
                                                type: string
                            outputs:
                              result:
                                expr: ${data.steps.build.json.result}
                                type: array
                                items:
                                  type: object
                                  properties:
                                    issue_number:
                                      type: number
                                    status:
                                      type: string
                                  required_properties: [issue_number, status]
                        """
                    };

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    mainAssemblyAttempts++;
                    if (mainAssemblyAttempts == 1)
                    {
                        return new LLMResponse
                        {
                            Text = """
                            document:
                              name: weak_pipeline_outputs
                              skill:
                                description: Build per-issue results.
                                inputs:
                                  query: string
                                outputs:
                                  result:
                                    type: array
                                    description: Per-issue processing results.
                            graph:
                              inputs:
                                query: string
                              steps:
                                - id: call_collect_results
                                  leaf: collect_results
                                  args:
                                    query: ${data.inputs.query}
                              outputs:
                                result: ${data.steps.call_collect_results.outputs.result}
                            """
                        };
                    }

                    Assert.Contains("WEAK_OUTPUT_SCHEMA", request.Prompt);
                    Assert.Contains("skill.outputs.result", request.Prompt);
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: strong_pipeline_outputs
                          skill:
                            description: Build per-issue results.
                            inputs:
                              query: string
                            outputs:
                              result:
                                type: array
                                description: Per-issue processing results.
                                items:
                                  type: object
                                  properties:
                                    issue_number:
                                      type: number
                                    status:
                                      type: string
                                  required_properties: [issue_number, status]
                        graph:
                          inputs:
                            query: string
                          steps:
                            - id: call_collect_results
                              leaf: collect_results
                              args:
                                query: ${data.inputs.query}
                          outputs:
                            result: ${data.steps.call_collect_results.outputs.result}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Build per-issue results."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 2
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject { ["query"] = "issues" }, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, mainAssemblyAttempts);
        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("result:", yaml);
        Assert.Contains("expr: ${data.steps.call_collect_results.outputs.result}", yaml);
        Assert.Contains("items:", yaml);
        Assert.Contains("issue_number:", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RegeneratesLeafWhenFinalOutputSchemaIsWeak()
    {
        var leafRepairRequests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Records\n\nCollect records." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Records

                        :::subworkflow name="collect_records"
                        goal: Collect records.
                        inputs:
                          query: string
                        outputs:
                          records: any
                        extract_reason: This reusable operation gathers records.
                        content:
                          Collect records for the query.
                        :::

                        ## Main workflow orchestration

                        Call collect_records and expose records.
                        """
                    };

                if (request.Prompt.Contains("You are repairing a GnOuGo.Flow YAML workflow", StringComparison.Ordinal)
                    && request.Prompt.Contains("pipeline_leaf_contract_demand", StringComparison.Ordinal)
                    && request.Prompt.Contains("WEAK_OUTPUT_SCHEMA", StringComparison.Ordinal))
                {
                    leafRepairRequests.Add(request);
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-records-leaf
                        skill:
                          description: Collect records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records:
                              type: array
                              items:
                                type: string
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  records: ["r1"]
                            outputs:
                              records:
                                expr: ${data.steps.collect.records}
                                type: array
                                items:
                                  type: string
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_records`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-records-leaf
                        skill:
                          description: Collect records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records: any
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: llm.call
                                input:
                                  prompt: "Return records for ${data.inputs.query}."
                            outputs:
                              records:
                                expr: ${data.steps.collect.raw}
                                type: any
                        """
                    };

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: collect-records-pipeline
                          skill:
                            description: Collect records.
                            inputs:
                              query: string
                            outputs:
                              records: any
                        graph:
                          inputs:
                            query: string
                          steps:
                            - id: call_collect_records
                              leaf: collect_records
                              args:
                                query: ${data.inputs.query}
                          outputs:
                            records: ${data.steps.call_collect_records.outputs.records}
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 3
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject { ["query"] = "records" }, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var repairRequest = Assert.Single(leafRepairRequests);
        Assert.Contains("WEAK_OUTPUT_SCHEMA", repairRequest.Prompt);
        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("records:", yaml);
        Assert.Contains("items:", yaml);
        Assert.DoesNotContain("type: any", yaml);

        var qualityReport = result.Outputs!["plan"]!["pipeline"]!["quality_report"]!;
        Assert.Equal("passed", qualityReport["status"]!.GetValue<string>());
        Assert.Equal(1, qualityReport["summary"]!["repair_count"]!.GetValue<int>());
        Assert.Equal(1, qualityReport["summary"]!["leaf_contract_repair_count"]!.GetValue<int>());
        Assert.Equal(1, qualityReport["summary"]!["main_retry_count"]!.GetValue<int>());
        var repair = Assert.Single(qualityReport["repairs"]!.AsArray());
        Assert.Equal("leaf_contract_repair", repair!["kind"]!.GetValue<string>());
        Assert.Equal("collect_records", repair["leaf"]!.GetValue<string>());
        Assert.Equal("records", repair["output"]!.GetValue<string>());
        Assert.Contains("stronger output contract", repair["message"]!.GetValue<string>());
        var rootCauses = qualityReport["root_causes"]!.AsArray();
        Assert.Contains(rootCauses, cause => cause!["category"]!.GetValue<string>() == "weak_leaf_contract");
        Assert.Contains(rootCauses, cause => cause!["category"]!.GetValue<string>() == "downstream_symptom");

        var inspection = result.Outputs!["plan"]!["pipeline"]!["inspection"]!;
        var repairHistory = inspection["repair_history"]!.AsArray();
        var inspectionRepair = Assert.Single(repairHistory);
        Assert.Equal("leaf_contract_repair", inspectionRepair!["kind"]!.GetValue<string>());
        Assert.Equal("collect_records", inspectionRepair["leaf"]!.GetValue<string>());
        Assert.Equal(rootCauses.Count, inspection["summary"]!["root_cause_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_StrengthensMainOutputFromSequenceAnalyzer()
    {
        var yaml = await GeneratePipelineWithMainAssemblyAsync("""
        document:
          name: sequence-output-pipeline
          skill:
            description: Analyze sequence output.
            inputs:
              query: string
            outputs:
              sequence_snapshot: any
        graph:
          inputs:
            query: string
          steps:
            - id: call_collect_base
              leaf: collect_base
              args:
                query: ${data.inputs.query}
            - id: build_sequence
              type: sequence
              steps:
                - id: shape_value
                  type: set
                  output_schema:
                    type: object
                    properties:
                      label:
                        type: string
                    required_properties: [label]
                  input:
                    label: ${data.steps.call_collect_base.outputs.value}
          outputs:
            sequence_snapshot: ${data.steps.build_sequence}
        """);

        Assert.Contains("sequence_snapshot:", yaml);
        Assert.Contains("expr: ${data.steps.build_sequence}", yaml);
        Assert.Contains("shape_value:", yaml);
        Assert.Contains("label:", yaml);
        Assert.DoesNotContain("type: any", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_StrengthensMainOutputFromParallelAnalyzer()
    {
        var yaml = await GeneratePipelineWithMainAssemblyAsync("""
        document:
          name: parallel-output-pipeline
          skill:
            description: Analyze parallel output.
            inputs:
              query: string
            outputs:
              branch_snapshots: any
        graph:
          inputs:
            query: string
          steps:
            - id: run_parallel
              type: parallel
              branches:
                - steps:
                    - id: branch_left
                      type: set
                      output_schema:
                        type: object
                        properties:
                          label:
                            type: string
                        required_properties: [label]
                      input:
                        label: left
                - steps:
                    - id: branch_right
                      type: set
                      output_schema:
                        type: object
                        properties:
                          label:
                            type: string
                        required_properties: [label]
                      input:
                        label: right
          outputs:
            branch_snapshots: ${data.steps.run_parallel.branches}
        """);

        Assert.Contains("branch_snapshots:", yaml);
        Assert.Contains("expr: ${data.steps.run_parallel.branches}", yaml);
        Assert.Contains("items:", yaml);
        Assert.Contains("branch_left:", yaml);
        Assert.Contains("branch_right:", yaml);
        Assert.Contains("label:", yaml);
        Assert.DoesNotContain("type: any", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_StrengthensMainOutputFromSwitchAnalyzer()
    {
        var yaml = await GeneratePipelineWithMainAssemblyAsync("""
        document:
          name: switch-output-pipeline
          skill:
            description: Analyze switch output.
            inputs:
              query: string
            outputs:
              switch_snapshot: any
        graph:
          inputs:
            query: string
          steps:
            - id: route
              type: switch
              expr: ${data.inputs.query}
              cases:
                - value: fast
                  steps:
                    - id: selected_fast
                      type: set
                      output_schema:
                        type: object
                        properties:
                          mode:
                            type: string
                        required_properties: [mode]
                      input:
                        mode: fast
              default:
                - id: selected_default
                  type: set
                  output_schema:
                    type: object
                    properties:
                      mode:
                        type: string
                    required_properties: [mode]
                  input:
                    mode: default
          outputs:
            switch_snapshot: ${data.steps.route}
        """);

        Assert.Contains("switch_snapshot:", yaml);
        Assert.Contains("expr: ${data.steps.route}", yaml);
        Assert.Contains("selected_fast:", yaml);
        Assert.Contains("selected_default:", yaml);
        Assert.Contains("mode:", yaml);
        Assert.DoesNotContain("type: any", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_StructuredCapableModelRejectsLegacyMarkdownExtraction()
    {
        var requests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) => requests.Add(request))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect data." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_data"
                        goal: Collect data.
                        inputs:
                          query: string
                        outputs:
                          records: array
                        extract_reason: This is reusable data collection.
                        content:
                          Collect records for the query.
                        :::

                        ## Main workflow orchestration

                        Call collect_data.
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect data."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                    max_repair_attempts: 1
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            LLMCapabilities = new StaticLlmCapabilityResolver(true)
        }.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("must be structured JSON with annotated_markdown", result.Error!.Message);
        var markRequest = Assert.Single(requests, request =>
            request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal));
        Assert.NotNull(markRequest.StructuredOutputSchema);
        Assert.True(markRequest.StructuredOutputStrict);
        Assert.Contains("Return ONLY JSON matching the requested structured output schema.", markRequest.Prompt);
        Assert.DoesNotContain("Return ONLY annotated Markdown.", markRequest.Prompt);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RepromptsWhenGraphOmitsEvolvedLeafInput()
    {
        var assemblyRequests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Repository issue report\n\nList issues and use an internal working directory." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Repository issue report

                        :::subworkflow name="list_issues"
                        goal: List repository issues.
                        inputs:
                          repository_url: string
                        outputs:
                          issues: array
                        extract_reason: This is a reusable repository operation.
                        content:
                          List issues for the repository.
                        :::

                        ## Main workflow orchestration

                        Map target_repository_url to repository_url and derive working_directory_base internally.
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `list_issues`.", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: list-issues-leaf
                        skill:
                          description: List repository issues.
                          tags: [github, leaf]
                          inputs:
                            repository_url: string
                            working_directory_base: string
                          outputs:
                            issues: array
                        workflows:
                          main:
                            inputs:
                              repository_url: string
                              working_directory_base: string
                            steps:
                              - id: result
                                type: llm.call
                                input:
                                  model: gpt-4
                                  prompt: "Return an empty issue list for test planning."
                                  structured_output:
                                    strict: true
                                    schema_inline:
                                      type: object
                                      additionalProperties: false
                                      required: [issues]
                                      properties:
                                        issues:
                                          type: array
                                          items:
                                            type: object
                                            additionalProperties: false
                                            required: [number, title, body, html_url]
                                            properties:
                                              number:
                                                type: number
                                              title:
                                                type: string
                                              body:
                                                type: string
                                              html_url:
                                                type: string
                            outputs:
                              issues:
                                expr: "${data.steps.result.json.issues}"
                                type: array
                                items:
                                  type: object
                                  properties:
                                    number:
                                      type: number
                                    title:
                                      type: string
                                    body:
                                      type: string
                                    html_url:
                                      type: string
                                  required_properties: [number, title, body, html_url]
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    assemblyRequests.Add(request);
                    if (assemblyRequests.Count == 1)
                    {
                        return new LLMResponse
                        {
                            Text = """
                            document:
                              name: repository_issue_report
                              skill:
                                description: Build a repository issue report.
                                inputs:
                                  target_repository_url: string
                                outputs:
                                  issues: array
                            graph:
                              inputs:
                                target_repository_url: string
                              steps:
                                - id: call_list_issues
                                  leaf: list_issues
                                  args:
                                    repository_url: ${data.inputs.target_repository_url}
                              outputs:
                                issues: ${data.steps.call_list_issues.outputs.issues}
                            """
                        };
                    }

                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: repository_issue_report
                          skill:
                            description: Build a repository issue report.
                            inputs:
                              target_repository_url: string
                            outputs:
                              issues: array
                        graph:
                          inputs:
                            target_repository_url: string
                          steps:
                            - id: derive_working_directory
                              type: set
                              input:
                                value: .GnOuGo/work
                            - id: call_list_issues
                              leaf: list_issues
                              args:
                                repository_url: ${data.inputs.target_repository_url}
                                working_directory_base: ${data.steps.derive_working_directory.value}
                          outputs:
                            issues: ${data.steps.call_list_issues.outputs.issues}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Build a repository issue report."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 2
        """);
        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, assemblyRequests.Count);
        Assert.Contains("missing required leaf argument(s): working_directory_base", assemblyRequests[1].Prompt);

        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("working_directory_base: ${data.steps.derive_working_directory.value}", yaml);
        Assert.Contains("workflow.call", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RetriesFinalAssemblyUsingConfiguredAttemptBudget()
    {
        var assemblyRequests = new List<LLMRequest>();
        var collectLeafGenerationCount = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records for the configured query." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_data"
                        goal: Collect records.
                        inputs:
                          query: string
                        outputs:
                          records: array
                        extract_reason: This is a reusable operation.
                        content:
                          Collect records for the query.
                        :::

                        ## Main workflow orchestration

                        Call collect_data with the configured query.
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_data`.", StringComparison.Ordinal))
                {
                    collectLeafGenerationCount++;
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-data-leaf
                        skill:
                          description: Collect records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records: array
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  records: []
                            outputs:
                              records:
                                expr: "${data.steps.collect.records}"
                                type: array
                                items:
                                  type: string
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    assemblyRequests.Add(request);
                    var inputReference = assemblyRequests.Count == 1 ? "undeclared_query" : "query";
                    return new LLMResponse
                    {
                        Text = $$"""
                        document:
                          name: collect-records-pipeline
                          skill:
                            description: Collect configured records.
                            inputs:
                              query: string
                            outputs:
                              records: array
                        graph:
                          inputs:
                            query: string
                          steps:
                            - id: call_collect_data
                              leaf: collect_data
                              args:
                                query: ${data.inputs.{{inputReference}}}
                          outputs:
                            records: ${data.steps.call_collect_data.outputs.records}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  skill:
                    description: Collect configured records.
                    inputs:
                      query: string
                  raw_prompt: "Collect records."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 2
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, assemblyRequests.Count);
        Assert.Contains("previous main workflow assembly failed final validation", assemblyRequests[1].Prompt);
        Assert.Contains("invalid_main_assembly_yaml", assemblyRequests[1].Prompt);
        Assert.Contains("undeclared_query", assemblyRequests[1].Prompt);
        Assert.Contains("main_assembly_validation_error", assemblyRequests[1].Prompt);
        Assert.DoesNotContain("pipeline_leaf_contract_demand", assemblyRequests[1].Prompt);
        Assert.Equal(1, collectLeafGenerationCount);

        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("query: ${data.inputs.query}", yaml);
        Assert.DoesNotContain("undeclared_query", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RegeneratesLeafWhenMainLoopsOverOpaqueArrayOutput()
    {
        var assemblyRequests = new List<LLMRequest>();
        var leafRepairRequests = new List<LLMRequest>();
        var collectLeafGenerationCount = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records and process each record id." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_records"
                        goal: Collect records.
                        inputs:
                          query: string
                        outputs:
                          records: any
                        extract_reason: This reusable operation gathers records for orchestration.
                        content:
                          Collect records for the query.
                        :::

                        ## Main workflow orchestration

                        Call collect_records, then loop over each record and use its id and name.
                        """
                    };
                }

                if (request.Prompt.Contains("You are repairing a GnOuGo.Flow YAML workflow", StringComparison.Ordinal)
                    && request.Prompt.Contains("pipeline_leaf_contract_demand", StringComparison.Ordinal))
                {
                    leafRepairRequests.Add(request);
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-records-leaf
                        skill:
                          description: Collect records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records:
                              type: array
                              items:
                                type: object
                                properties:
                                  id:
                                    type: string
                                  name:
                                    type: string
                                required_properties: [id, name]
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                output_schema:
                                  type: object
                                  properties:
                                    records:
                                      type: array
                                      items:
                                        type: object
                                        properties:
                                          id:
                                            type: string
                                          name:
                                            type: string
                                        required_properties: [id, name]
                                input:
                                  records:
                                    - id: "r1"
                                      name: "Record 1"
                            outputs:
                              records:
                                expr: "${data.steps.collect.records}"
                                type: array
                                items:
                                  type: object
                                  properties:
                                    id:
                                      type: string
                                    name:
                                      type: string
                                  required_properties: [id, name]
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_records`.", StringComparison.Ordinal))
                {
                    collectLeafGenerationCount++;
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-records-leaf
                        skill:
                          description: Collect records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records: any
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: llm.call
                                input:
                                  prompt: "Return records for ${data.inputs.query}."
                            outputs:
                              records:
                                expr: "${data.steps.collect.raw}"
                                type: any
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    assemblyRequests.Add(request);
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: collect-records-pipeline
                          skill:
                            description: Collect and process records.
                            inputs:
                              query: string
                            outputs:
                              processed_records: array
                        graph:
                          inputs:
                            query: string
                          steps:
                            - id: call_collect_records
                              leaf: collect_records
                              args:
                                query: ${data.inputs.query}
                            - id: process_records
                              type: loop.sequential
                              input:
                                items: ${data.steps.call_collect_records.outputs.records}
                              item_var: record
                              steps:
                                - id: shape_record
                                  type: set
                                  output_schema:
                                    type: object
                                    properties:
                                      id:
                                        type: string
                                      name:
                                        type: string
                                  input:
                                    id: ${data.record.id}
                                    name: ${data.record.name}
                          outputs:
                            processed_records: ${data.steps.process_records.results}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records and process each record id."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 3
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, assemblyRequests.Count);
        var repairRequest = Assert.Single(leafRepairRequests);
        Assert.Contains("items.id", repairRequest.Prompt);
        Assert.Contains("items.name", repairRequest.Prompt);
        Assert.Equal(1, collectLeafGenerationCount);

        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("id: ${data.record.id}", yaml);
        Assert.Contains("required_properties: [id, name]", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RegeneratesLeafWhenMainLoopsOverOpaqueItemsWithoutDeepAccess()
    {
        var assemblyRequests = new List<LLMRequest>();
        var leafRepairRequests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records and count each record." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_records"
                        goal: Collect records.
                        inputs:
                          query: string
                        outputs:
                          records: any
                        extract_reason: This reusable operation gathers records for orchestration.
                        content:
                          Collect records for the query.
                        :::

                        ## Main workflow orchestration

                        Call collect_records, then loop over each returned record to count iterations.
                        """
                    };
                }

                if (request.Prompt.Contains("You are repairing a GnOuGo.Flow YAML workflow", StringComparison.Ordinal)
                    && request.Prompt.Contains("pipeline_leaf_contract_demand", StringComparison.Ordinal))
                {
                    leafRepairRequests.Add(request);
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-records-leaf
                        skill:
                          description: Collect records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records:
                              type: array
                              items:
                                type: string
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  records: ["r1", "r2"]
                            outputs:
                              records:
                                expr: "${data.steps.collect.records}"
                                type: array
                                items:
                                  type: string
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_records`.", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-records-leaf
                        skill:
                          description: Collect records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records: any
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: llm.call
                                input:
                                  prompt: "Return records for ${data.inputs.query}."
                            outputs:
                              records:
                                expr: "${data.steps.collect.raw}"
                                type: any
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    assemblyRequests.Add(request);
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: collect-records-count-pipeline
                          skill:
                            description: Collect and count records.
                            inputs:
                              query: string
                            outputs:
                              record_count: number
                        graph:
                          inputs:
                            query: string
                          steps:
                            - id: call_collect_records
                              leaf: collect_records
                              args:
                                query: ${data.inputs.query}
                            - id: count_records
                              type: loop.sequential
                              input:
                                items: ${data.steps.call_collect_records.outputs.records}
                              item_var: record
                              steps:
                                - id: mark_seen
                                  type: set
                                  input:
                                    seen_index: ${data._loop.index}
                          outputs:
                            record_count: ${data.steps.count_records.count}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records and count each record."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 3
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, assemblyRequests.Count);
        var repairRequest = Assert.Single(leafRepairRequests);
        Assert.Contains("OPAQUE_ARRAY_LOOP_ITEMS", repairRequest.Prompt);
        Assert.Contains("\"required_output_paths\"", repairRequest.Prompt);
        Assert.Contains("\"items\"", repairRequest.Prompt);
        Assert.Contains("required_output_schema_guidance_yaml", repairRequest.Prompt);
        Assert.Contains("items:", repairRequest.Prompt);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RegeneratesLeafWhenMainRequiresMissingArrayItemField()
    {
        var leafRepairRequests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records and read each record id." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_records"
                        goal: Collect records.
                        inputs:
                          query: string
                        outputs:
                          records: array
                        extract_reason: This reusable operation gathers records for orchestration.
                        content:
                          Collect records for the query.
                        :::

                        ## Main workflow orchestration

                        Call collect_records, then loop over each record and use its id.
                        """
                    };
                }

                if (request.Prompt.Contains("You are repairing a GnOuGo.Flow YAML workflow", StringComparison.Ordinal)
                    && request.Prompt.Contains("pipeline_leaf_contract_demand", StringComparison.Ordinal))
                {
                    leafRepairRequests.Add(request);
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-records-leaf
                        skill:
                          description: Collect records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records:
                              type: array
                              items:
                                type: object
                                properties:
                                  id:
                                    type: string
                                  name:
                                    type: string
                                required_properties: [id, name]
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  records:
                                    - id: "r1"
                                      name: "Record 1"
                            outputs:
                              records:
                                expr: "${data.steps.collect.records}"
                                type: array
                                items:
                                  type: object
                                  properties:
                                    id:
                                      type: string
                                    name:
                                      type: string
                                  required_properties: [id, name]
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_records`.", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-records-leaf
                        skill:
                          description: Collect records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records:
                              type: array
                              items:
                                type: object
                                properties:
                                  name:
                                    type: string
                                required_properties: [name]
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  records:
                                    - name: "Record 1"
                            outputs:
                              records:
                                expr: "${data.steps.collect.records}"
                                type: array
                                items:
                                  type: object
                                  properties:
                                    name:
                                      type: string
                                  required_properties: [name]
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: collect-records-pipeline
                          skill:
                            description: Collect record ids.
                            inputs:
                              query: string
                            outputs:
                              record_ids: array
                        graph:
                          inputs:
                            query: string
                          steps:
                            - id: call_collect_records
                              leaf: collect_records
                              args:
                                query: ${data.inputs.query}
                            - id: process_records
                              type: loop.sequential
                              input:
                                items: ${data.steps.call_collect_records.outputs.records}
                              item_var: record
                              steps:
                                - id: shape_record
                                  type: set
                                  output_schema:
                                    type: object
                                    properties:
                                      id:
                                        type: string
                                  input:
                                    id: ${data.record.id}
                          outputs:
                            record_ids: ${data.steps.process_records.results}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records and read each record id."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 3
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var repairRequest = Assert.Single(leafRepairRequests);
        Assert.Contains("items.id", repairRequest.Prompt);
        Assert.Contains("current_leaf_output_schema_yaml", repairRequest.Prompt);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_LeafContractRepairExhaustionRemainsBounded()
    {
        var assemblyRequests = new List<LLMRequest>();
        var leafRepairRequests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records and process each id." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_records"
                        goal: Collect records.
                        inputs:
                          query: string
                        outputs:
                          records: any
                        extract_reason: This reusable operation gathers records for orchestration.
                        content:
                          Collect records for the query.
                        :::

                        ## Main workflow orchestration

                        Call collect_records, then loop over each record and use its id.
                        """
                    };
                }

                if (request.Prompt.Contains("You are repairing a GnOuGo.Flow YAML workflow", StringComparison.Ordinal)
                    && request.Prompt.Contains("pipeline_leaf_contract_demand", StringComparison.Ordinal))
                {
                    leafRepairRequests.Add(request);
                    return new LLMResponse
                    {
                        Text = """
                        not: [valid
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_records`.", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-records-leaf
                        skill:
                          description: Collect records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records: any
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: llm.call
                                input:
                                  prompt: "Return records for ${data.inputs.query}."
                            outputs:
                              records:
                                expr: "${data.steps.collect.raw}"
                                type: any
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    assemblyRequests.Add(request);
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: collect-records-pipeline
                          skill:
                            description: Collect record ids.
                            inputs:
                              query: string
                            outputs:
                              record_ids: array
                        graph:
                          inputs:
                            query: string
                          steps:
                            - id: call_collect_records
                              leaf: collect_records
                              args:
                                query: ${data.inputs.query}
                            - id: process_records
                              type: loop.sequential
                              input:
                                items: ${data.steps.call_collect_records.outputs.records}
                              item_var: record
                              steps:
                                - id: shape_record
                                  type: set
                                  output_schema:
                                    type: object
                                    properties:
                                      id:
                                        type: string
                                  input:
                                    id: ${data.record.id}
                          outputs:
                            record_ids: ${data.steps.process_records.results}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records and process each id."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 2
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.Equal(2, assemblyRequests.Count);
        Assert.Single(leafRepairRequests);
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("Pipeline main workflow assembly failed after 2 attempt", result.Error.Message);
        Assert.Contains("OPAQUE_DATA_VARIABLE_DEEP_ACCESS", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_FinalDryRunFailureRepromptsMainAssembly()
    {
        var assemblyRequests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nProcess the configured name." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="prepare_name"
                        goal: Prepare the configured name.
                        inputs:
                          name: string
                        outputs:
                          prepared_name: string
                        extract_reason: This is a reusable preparation operation.
                        content:
                          Return the configured name.
                        :::

                        ## Main workflow orchestration

                        Call prepare_name, then use the prepared name.
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `prepare_name`.", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: prepare-name-leaf
                        skill:
                          description: Prepare a name.
                          tags: [generated, leaf]
                          inputs:
                            name: string
                          outputs:
                            prepared_name: string
                        workflows:
                          main:
                            inputs:
                              name: string
                            steps:
                              - id: prepare
                                type: set
                                input:
                                  prepared_name: "${data.inputs.name}"
                            outputs:
                              prepared_name: "${data.steps.prepare.prepared_name}"
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    assemblyRequests.Add(request);
                    var transformExpression = assemblyRequests.Count == 1
                        ? "\"${data.inputs.name()}\""
                        : "\"${data.inputs.name}\"";
                    return new LLMResponse
                    {
                        Text = """
                        main:
                          inputs:
                            name: string
                          steps:
                            - id: call_prepare_name
                              type: workflow.call
                              input:
                                ref: { kind: local, name: prepare_name }
                                args:
                                  name: "${data.inputs.name}"
                            - id: transform
                              type: set
                              input:
                                prepared_name: __TRANSFORM_EXPRESSION__
                          outputs:
                            prepared_name: "${data.steps.transform.prepared_name}"
                        """.Replace("__TRANSFORM_EXPRESSION__", transformExpression, StringComparison.Ordinal)
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  skill:
                    description: Process a configured name.
                    inputs:
                      name: string
                  raw_prompt: "Process a configured name."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: true
                    dry_run: true
                  on_invalid:
                    max_attempts: 2
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, assemblyRequests.Count);
        Assert.Contains("Generated workflow dry_run failed", assemblyRequests[1].Prompt);
        Assert.Contains("data.inputs.name()", assemblyRequests[1].Prompt);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_DryRunRepromptsWhenPublicOutputExposesRawLoopResults()
    {
        var assemblyRequests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records and return processed records." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_records"
                        goal: Collect records.
                        inputs:
                          query: string
                        outputs:
                          records: array
                        extract_reason: This reusable operation gathers records.
                        content:
                          Return records for the query.
                        :::

                        ## Main workflow orchestration

                        Call collect_records, loop over each record, and expose a flat processed_records result.
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_records`.", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-records-leaf
                        skill:
                          description: Collect records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records:
                              type: array
                              items:
                                type: object
                                properties:
                                  id:
                                    type: string
                                  name:
                                    type: string
                                required_properties: [id, name]
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  records:
                                    - id: r1
                                      name: One
                            outputs:
                              records:
                                expr: ${data.steps.collect.records}
                                type: array
                                items:
                                  type: object
                                  properties:
                                    id:
                                      type: string
                                    name:
                                      type: string
                                  required_properties: [id, name]
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    assemblyRequests.Add(request);
                    if (assemblyRequests.Count == 1)
                    {
                        return new LLMResponse
                        {
                            Text = """
                            document:
                              name: collect-records-pipeline
                              skill:
                                description: Collect and process records.
                                inputs:
                                  query: string
                                outputs:
                                  processed_records:
                                    type: array
                                    items:
                                      type: object
                                      properties:
                                        id:
                                          type: string
                                        name:
                                          type: string
                                      required_properties: [id, name]
                            graph:
                              inputs:
                                query: string
                              steps:
                                - id: call_collect_records
                                  leaf: collect_records
                                  args:
                                    query: ${data.inputs.query}
                                - id: process_records
                                  type: loop.sequential
                                  input:
                                    items: ${data.steps.call_collect_records.outputs.records}
                                  item_var: record
                                  steps:
                                    - id: shape_record
                                      type: set
                                      output_schema:
                                        type: object
                                        properties:
                                          id:
                                            type: string
                                          name:
                                            type: string
                                        required_properties: [id, name]
                                      input:
                                        id: ${data.record.id}
                                        name: ${data.record.name}
                              outputs:
                                processed_records: ${data.steps.process_records.results}
                            """
                        };
                    }

                    Assert.Contains("PIPELINE_MAIN_RAW_LOOP_RESULTS_OUTPUT", request.Prompt);
                    Assert.Contains("graph.functions", request.Prompt);
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: collect-records-pipeline
                          skill:
                            description: Collect and process records.
                            inputs:
                              query: string
                            outputs:
                              processed_records:
                                type: array
                                items:
                                  type: object
                                  properties:
                                    id:
                                      type: string
                                    name:
                                      type: string
                                  required_properties: [id, name]
                        graph:
                          inputs:
                            query: string
                          functions: |
                            /**
                             * Projects loop iteration snapshots into processed record results.
                             * @param {Array<object>} iterations - Per-iteration loop result snapshots.
                             * @returns {Array<object>} Clean public processed record results.
                             */
                            function projectProcessedRecords(iterations) {
                              if (!Array.isArray(iterations)) return [];
                              return iterations.map(function (iteration) {
                                var shaped = iteration && iteration.shape_record ? iteration.shape_record : {};
                                return {
                                  id: shaped.id || "",
                                  name: shaped.name || ""
                                };
                              });
                            }
                          steps:
                            - id: call_collect_records
                              leaf: collect_records
                              args:
                                query: ${data.inputs.query}
                            - id: process_records
                              type: loop.sequential
                              input:
                                items: ${data.steps.call_collect_records.outputs.records}
                              item_var: record
                              steps:
                                - id: shape_record
                                  type: set
                                  output_schema:
                                    type: object
                                    properties:
                                      id:
                                        type: string
                                      name:
                                        type: string
                                    required_properties: [id, name]
                                  input:
                                    id: ${data.record.id}
                                    name: ${data.record.name}
                            - id: project_processed_records
                              type: set
                              output_schema:
                                type: object
                                properties:
                                  processed_records:
                                    type: array
                                    items:
                                      type: object
                                      properties:
                                        id:
                                          type: string
                                        name:
                                          type: string
                                      required_properties: [id, name]
                                required_properties: [processed_records]
                              input:
                                processed_records: ${functions.projectProcessedRecords(data.steps.process_records.results)}
                          outputs:
                            processed_records: ${data.steps.project_processed_records.processed_records}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records and return processed records."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: true
                    dry_run: true
                  on_invalid:
                    max_attempts: 2
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject { ["query"] = "records" }, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, assemblyRequests.Count);
        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("function projectProcessedRecords", yaml);
        Assert.Contains("project_processed_records", yaml);
        Assert.Contains("expr: ${data.steps.project_processed_records.processed_records}", yaml);
        Assert.DoesNotContain("expr: ${data.steps.process_records.results}", yaml);
        var qualityReport = result.Outputs!["plan"]!["pipeline"]!["quality_report"]!;
        Assert.Equal(1, qualityReport["summary"]!["main_retry_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_FinalDryRunAcceptsStringConversionHelper()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nPrepare a path for an issue number." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="prepare_issue"
                        goal: Prepare issue metadata.
                        inputs:
                          path: string
                        outputs:
                          prepared_path: string
                        extract_reason: This keeps the leaf contract explicit.
                        content:
                          Return the configured path as prepared_path.
                        :::

                        ## Main workflow orchestration

                        Build a path from issue_number and call prepare_issue.
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `prepare_issue`.", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: prepare-issue-leaf
                        skill:
                          description: Prepare issue metadata.
                          tags: [generated, leaf]
                          inputs:
                            path: string
                          outputs:
                            prepared_path: string
                        workflows:
                          main:
                            inputs:
                              path: string
                            steps:
                              - id: prepare
                                type: set
                                input:
                                  prepared_path: "${data.inputs.path}"
                            outputs:
                              prepared_path: "${data.steps.prepare.prepared_path}"
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: issue_path_pipeline
                          skill:
                            description: Prepare a path for an issue number.
                            tags: [issue, pipeline]
                            outputs:
                              prepared_path: string
                        main:
                          inputs:
                            issue_number: number
                          steps:
                            - id: build_path
                              type: set
                              input:
                                path: '${"issue_" + string(data.inputs.issue_number)}'
                            - id: call_prepare_issue
                              type: workflow.call
                              input:
                                ref: { kind: local, name: prepare_issue }
                                args:
                                  path: "${data.steps.build_path.path}"
                          outputs:
                            prepared_path: "${data.steps.call_prepare_issue.outputs.prepared_path}"
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Prepare a path for an issue number."
                  inputs:
                    issue_number: number
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: true
                    dry_run: true
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("string(data.inputs.issue_number)", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_ComposesDetachedMainAssemblyNodes()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nBuild a profile for a name." };

                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="build_profile"
                        goal: Build a simple profile.
                        inputs:
                          name: string
                        outputs:
                          profile: object
                        extract_reason: This owns the profile construction logic.
                        content:
                          Return a profile object containing the provided name.
                        :::

                        ## Main workflow orchestration

                        Call build_profile and return its profile.
                        """
                    };
                }

                if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `build_profile`.", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: build-profile-leaf
                        skill:
                          description: Build a profile.
                          tags: [generated, leaf]
                          inputs:
                            name: string
                          outputs:
                            profile:
                              type: object
                              properties:
                                name:
                                  type: string
                              required_properties: [name]
                        workflows:
                          main:
                            inputs:
                              name: string
                            steps:
                              - id: profile
                                type: set
                                input:
                                  profile:
                                    name: "${data.inputs.name}"
                            outputs:
                              profile:
                                expr: "${data.steps.profile.profile}"
                                type: object
                                properties:
                                  name:
                                    type: string
                                required_properties: [name]
                        """
                    };
                }

                if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: profile_pipeline
                          skill:
                            description: Build profiles.
                            tags: [profile, pipeline]
                            inputs:
                              name:
                                type: string
                                description: Person name.
                            outputs:
                              profile:
                                type: object
                                properties:
                                  name:
                                    type: string
                                required_properties: [name]
                        main:
                          inputs:
                            name:
                              type: string
                              description: Person name.
                          steps:
                            - id: call_build_profile
                              type: workflow.call
                              input:
                                ref: { kind: local, name: build_profile }
                                args:
                                  name: "${data.inputs.name}"
                          outputs:
                            profile:
                              expr: "${data.steps.call_build_profile.outputs.profile}"
                              type: object
                              properties:
                                name:
                                  type: string
                              required_properties: [name]
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Build a profile for a name."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: true
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject { ["name"] = "Ada" }, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("name: profile_pipeline", yaml);
        Assert.Contains("profile:", yaml);
        Assert.Contains("name: build_profile", yaml);
        WorkflowParser.Parse(yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_AllowsMainSupportStepsWhenPolicyRestrictive()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nCollect records." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_data"
                        goal: Collect source records.
                        inputs:
                          query: string
                        outputs:
                          records: array
                        extract_reason: This is a reusable multi-step data collection operation.
                        content:
                          Collect records for the provided query.
                        :::

                        ## Main workflow orchestration

                        Call collect_data.
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_data`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-data-leaf
                        skill:
                          description: Collect source records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records: array
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: set
                                input:
                                  value: ["one", "two"]
                            outputs:
                              records:
                                expr: "${data.steps.collect.value}"
                                type: array
                                items:
                                  type: string
                        """
                    };

                if (req.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: collect-data-pipeline
                          skill:
                            description: Collect records.
                            tags: [generated, pipeline]
                            inputs:
                              query: string
                            outputs:
                              collect_data_outputs: object
                        graph:
                          inputs:
                            query: string
                          steps:
                            - id: derive_query
                              type: set
                              input:
                                query: ${data.inputs.query}
                            - id: call_collect_data
                              leaf: collect_data
                              args:
                                query: ${data.steps.derive_query.query}
                          outputs:
                            collect_data_outputs: ${data.steps.call_collect_data.outputs}
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });
        var logger = new Mock<ILogger>();

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records."
                  generator:
                    model: gpt-4
                    prefilter: false
                  policy:
                    allowed_step_types: [workflow.call]
                    denied_step_types: [set, workflow.call]
                  validate:
                    compile: false
        """);
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            Logger = logger.Object
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var planOutput = Assert.IsType<JsonObject>(result.Outputs!["plan"]);
        var yaml = planOutput["yaml"]!.GetValue<string>();
        var generatedDoc = WorkflowParser.Parse(yaml);
        Assert.Contains(generatedDoc.Workflows["main"].Steps, step => step.Type == "set");
        Assert.Contains(generatedDoc.Workflows["main"].Steps, step => step.Type == "workflow.call");
        Assert.DoesNotContain(EnumerateSteps(generatedDoc.Workflows["collect_data"].Steps), step => step.Type == "workflow.call");
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((value, _) => value.ToString()!.Contains("workflow.call", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((value, _) => value.ToString()!.Contains("set", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RespectsLeafMcpPrefilter()
    {
        var requests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) =>
            {
                lock (requests)
                    requests.Add(req);
            })
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nList repositories." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="list_repositories"
                        goal: List repositories.
                        inputs:
                          owner: string
                        outputs:
                          repositories: array
                        extract_reason: This orchestrates an external tool to collect repository data.
                        content:
                          List repositories for the provided owner.
                        :::

                        ## Main workflow orchestration

                        Call list_repositories.
                        """
                    };

                if (req.Prompt.Contains("MCP server-selection assistant", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Json = JsonNode.Parse("{\"servers\":[{\"name\":\"github\",\"reason\":\"repository task\"}]}")!,
                        Text = "{\"servers\":[{\"name\":\"github\",\"reason\":\"repository task\"}]}"
                    };

                if (req.Prompt.Contains("tool-selection assistant", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Json = JsonNode.Parse("{\"servers\":[{\"name\":\"github\",\"tools\":[\"list_repos\"],\"prompts\":[]}]}")!,
                        Text = "{\"servers\":[{\"name\":\"github\",\"tools\":[\"list_repos\"],\"prompts\":[]}]}"
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `list_repositories`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: list-repositories-leaf
                        skill:
                          description: List repositories.
                          tags: [generated, leaf]
                          inputs:
                            owner: string
                          outputs:
                            repositories: array
                        workflows:
                          main:
                            inputs:
                              owner: string
                            steps:
                              - id: fetch_repos
                                type: mcp.call
                                input:
                                  server: github
                                  kind: tool
                                  method: list_repos
                                  request:
                                    owner: ${data.inputs.owner}
                              - id: repos
                                type: set
                                input:
                                  repositories: []
                            outputs:
                              repositories:
                                expr: "${data.steps.repos.repositories}"
                                type: array
                                items:
                                  type: object
                                  properties:
                                    name:
                                      type: string
                                    url:
                                      type: string
                                  required_properties: [name, url]
                        """
                    };

                return TryRespondToPipelineMainAssembly(req)
                    ?? throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Description = "GitHub repository operations",
            Tools =
            {
                new McpToolInfo { Name = "list_repos", Description = "List repositories for an owner" }
            }
        });
        mcpFactory.RegisterServer("weather", new MockMcpServerConfig
        {
            Description = "Weather forecast operations",
            Tools =
            {
                new McpToolInfo { Name = "get_weather", Description = "Get weather for a city" }
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
                  mode: pipeline
                  raw_prompt: "List repositories."
                  generator:
                    model: gpt-4
                    prefilter: true
                  validate:
                    compile: false
        """);
        var engine = new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            McpClientFactory = mcpFactory
        };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(requests, request => request.Prompt.Contains("MCP server-selection assistant", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Prompt.Contains("tool-selection assistant", StringComparison.Ordinal));
        var generationRequest = Assert.Single(requests, request =>
            request.Prompt.StartsWith("You are a GnOuGo.Flow YAML workflow generator.", StringComparison.Ordinal)
            && request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `list_repositories`.", StringComparison.Ordinal));
        var availableMcpStart = generationRequest.Prompt.LastIndexOf("<available_mcp_servers>", StringComparison.Ordinal);
        var availableMcpEnd = generationRequest.Prompt.LastIndexOf("</available_mcp_servers>", StringComparison.Ordinal);
        Assert.True(availableMcpStart >= 0, "Expected available MCP server section in generation prompt.");
        Assert.True(availableMcpEnd > availableMcpStart, "Expected closing available MCP server section in generation prompt.");
        var availableMcpServers = generationRequest.Prompt[availableMcpStart..availableMcpEnd];
        Assert.Contains("list_repos", availableMcpServers);
        Assert.DoesNotContain("get_weather", availableMcpServers);
        Assert.DoesNotContain("<pipeline_available_mcp_servers>", generationRequest.Prompt);

        var markRequest = Assert.Single(requests, request =>
            request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal));
        var pipelineMcpStart = markRequest.Prompt.LastIndexOf("<pipeline_available_mcp_servers>", StringComparison.Ordinal);
        var pipelineMcpEnd = markRequest.Prompt.LastIndexOf("</pipeline_available_mcp_servers>", StringComparison.Ordinal);
        Assert.True(pipelineMcpStart >= 0, "Expected pipeline-level MCP server section in mark_extractable_blocks prompt.");
        Assert.True(pipelineMcpEnd > pipelineMcpStart, "Expected closing pipeline-level MCP server section in mark_extractable_blocks prompt.");
        var pipelineMcpServers = markRequest.Prompt[pipelineMcpStart..pipelineMcpEnd];
        Assert.Contains("list_repos", pipelineMcpServers);
        Assert.Contains("capability_card_yaml:", pipelineMcpServers);
        Assert.Contains("explicit input/output variables", markRequest.Prompt);
        Assert.DoesNotContain("get_weather", pipelineMcpServers);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_MainAssemblyDoesNotReceiveLeafResourceLinks()
    {
        string? mainAssemblyPrompt = null;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nClone a repository and classify it." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="clone_repository_for_issue"
                        goal: Clone the repository.
                        inputs:
                          repository_url: string
                        outputs:
                          project_root_relative: string
                        extract_reason: This creates the repository workspace.
                        content:
                          Clone the repository and return the existing workspace-relative project root.
                        :::

                        :::subworkflow name="classify_issue"
                        goal: Classify the issue using the cloned repository.
                        inputs:
                          project_root_relative: string
                        outputs:
                          ok: boolean
                        extract_reason: This consumes the existing repository workspace.
                        content:
                          Use the existing workspace-relative project root to classify the issue.
                        :::

                        ## Main workflow orchestration

                        Clone the repository, then classify the issue with that cloned project root.
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `clone_repository_for_issue`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: clone-repository-leaf
                        skill:
                          description: Clone repository.
                          tags: [generated, leaf]
                          inputs:
                            repository_url: string
                          outputs:
                            project_root_relative: string
                        workflows:
                          main:
                            inputs:
                              repository_url: string
                            steps:
                              - id: clone_repo
                                type: mcp.call
                                input:
                                  server: git
                                  kind: tool
                                  method: clone_repo
                                  request:
                                    remoteUrl: ${data.inputs.repository_url}
                                    targetDirectory: repo
                            outputs:
                              project_root_relative:
                                expr: "${data.steps.clone_repo.response.project_root_relative}"
                                type: string
                                description: Existing workspace-relative cloned project root produced by this leaf.
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `classify_issue`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: classify-issue-leaf
                        skill:
                          description: Classify issue.
                          tags: [generated, leaf]
                          inputs:
                            project_root_relative: string
                          outputs:
                            ok: boolean
                        workflows:
                          main:
                            inputs:
                              project_root_relative:
                                type: string
                                required: true
                                description: Existing project root relative to the workspace.
                            steps:
                              - id: result
                                type: set
                                input:
                                  ok: true
                            outputs:
                              ok:
                                expr: "${data.steps.result.ok}"
                                type: boolean
                        """
                    };

                if (req.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    mainAssemblyPrompt = req.Prompt;
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: resource_link_pipeline
                          skill:
                            description: Clone and classify.
                            tags: [generated, pipeline]
                            inputs:
                              repository_url: string
                            outputs:
                              ok: boolean
                        graph:
                          inputs:
                            repository_url: string
                          steps:
                            - id: clone_repository_for_issue_step
                              leaf: clone_repository_for_issue
                              args:
                                repository_url: ${data.inputs.repository_url}
                            - id: classify_issue_step
                              leaf: classify_issue
                              args:
                                project_root_relative: ${data.steps.clone_repository_for_issue_step.outputs.project_root_relative}
                          outputs:
                            ok: ${data.steps.classify_issue_step.outputs.ok}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("git", new MockMcpServerConfig
        {
            Tools =
            {
                new McpToolInfo
                {
                    Name = "clone_repo",
                    Description = "Clone a repository into the workspace.",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "remoteUrl": { "type": "string" },
                        "targetDirectory": { "type": "string" }
                      },
                      "required": ["remoteUrl", "targetDirectory"],
                      "additionalProperties": false
                    }
                    """),
                    OutputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "project_root_relative": {
                          "type": "string",
                          "description": "Existing workspace-relative cloned project root created by this tool."
                        }
                      },
                      "required": ["project_root_relative"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: pipeline
                  raw_prompt: "Clone and classify."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(mainAssemblyPrompt);
        Assert.DoesNotContain("<leaf_resource_links_yaml>", mainAssemblyPrompt);
        Assert.DoesNotContain("resource_producers", mainAssemblyPrompt);
        Assert.DoesNotContain("resource_consumers", mainAssemblyPrompt);
        Assert.DoesNotContain("resource_links", mainAssemblyPrompt);
        Assert.Contains("clone_repository_for_issue", mainAssemblyPrompt);
        Assert.Contains("classify_issue", mainAssemblyPrompt);
        Assert.DoesNotContain("${data.steps.<producer_call_id>.outputs.project_root_relative}", mainAssemblyPrompt);
        Assert.DoesNotContain("existing_workspace_relative_path", mainAssemblyPrompt);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_DoesNotFailBeforeMainAssemblyForPathLikeLeafContracts()
    {
        var mainAssemblyCalled = false;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nClone a repository and classify it." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="clone_repository_for_issue"
                        goal: Derive the repository path.
                        inputs:
                          issue_number: number
                        outputs:
                          project_root_relative: string
                        extract_reason: This prepares the repository path.
                        content:
                          Derive the workspace-relative project root path.
                        :::

                        :::subworkflow name="classify_issue"
                        goal: Classify the issue using the cloned repository.
                        inputs:
                          project_root_relative: string
                        outputs:
                          ok: boolean
                        extract_reason: This consumes the existing repository workspace.
                        content:
                          Use the existing workspace-relative project root to classify the issue.
                        :::

                        ## Main workflow orchestration

                        Derive the repository path, then classify the issue.
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `clone_repository_for_issue`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: clone-repository-leaf
                        skill:
                          description: Derive repository path.
                          tags: [generated, leaf]
                          inputs:
                            issue_number: number
                          outputs:
                            project_root_relative: string
                        workflows:
                          main:
                            inputs:
                              issue_number: number
                            steps:
                              - id: path
                                type: set
                                input:
                                  project_root_relative: "repo-${data.inputs.issue_number}"
                            outputs:
                              project_root_relative:
                                expr: "${data.steps.path.project_root_relative}"
                                type: string
                                description: Workspace-relative project root directory string for the repository clone.
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `classify_issue`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: classify-issue-leaf
                        skill:
                          description: Classify issue.
                          tags: [generated, leaf]
                          inputs:
                            project_root_relative: string
                          outputs:
                            ok: boolean
                        workflows:
                          main:
                            inputs:
                              project_root_relative:
                                type: string
                                required: true
                                description: Existing project root relative to the workspace.
                            steps:
                              - id: result
                                type: set
                                input:
                                  ok: true
                            outputs:
                              ok:
                                expr: "${data.steps.result.ok}"
                                type: boolean
                        """
                    };

                if (req.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                {
                    mainAssemblyCalled = true;
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: path_like_pipeline
                          skill:
                            description: Derive and classify.
                            inputs:
                              issue_number: number
                            outputs:
                              ok: boolean
                        graph:
                          inputs:
                            issue_number: number
                          steps:
                            - id: clone_repository_for_issue_step
                              leaf: clone_repository_for_issue
                              args:
                                issue_number: ${data.inputs.issue_number}
                            - id: classify_issue_step
                              leaf: classify_issue
                              args:
                                project_root_relative: ${data.steps.clone_repository_for_issue_step.outputs.project_root_relative}
                          outputs:
                            ok: ${data.steps.classify_issue_step.outputs.ok}
                        """
                    };
                }

                throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Clone and classify."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(mainAssemblyCalled);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_ReportsExtractionValidationErrors()
    {
        var callCount = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? new LLMResponse { Text = "# Automation\n\nDo nested work." }
                    : new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="parent_task"
                        goal: Do parent task.
                        inputs:
                          prompt: string
                        outputs:
                          result: string
                        extract_reason: This has branching logic.
                        content:
                          Use workflow.call to invoke a child subworkflow.
                        :::

                        ## Main workflow orchestration

                        Call parent_task.
                        """
                    };
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Do nested work."
                  generator:
                    model: gpt-4
                    prefilter: false
                  on_invalid:
                    max_attempts: 1
        """);
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("appears to call another subworkflow", result.Error.Message);
        Assert.NotNull(result.Error.Details);
        Assert.Contains("appears to call another subworkflow", result.Error.Details.ToJsonString());
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RejectsGeneratedLeafWorkflowCalls()
    {
        var callCount = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new LLMResponse { Text = "# Automation\n\nCollect data." },
                    2 => new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_data"
                        goal: Collect source records.
                        inputs:
                          query: string
                        outputs:
                          records: array
                        extract_reason: This is a reusable technical operation.
                        content:
                          Collect records for the provided query.
                        :::

                        ## Main workflow orchestration

                        Call collect_data.
                        """
                    },
                    _ => new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: bad-leaf
                        workflows:
                          main:
                            steps:
                              - id: child
                                type: workflow.call
                                input:
                                  ref: { kind: local, name: child_workflow }
                        """
                    }
                };
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect data."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
        """);
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePolicy, result.Error!.Code);
        Assert.Contains("workflow.call", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RejectsBareObjectSchemasInLeafWorkflows()
    {
        var callCount = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new LLMResponse { Text = "# Automation\n\nBuild a profile object." },
                    2 => new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="build_profile"
                        goal: Build a profile object.
                        inputs:
                          name: string
                        outputs:
                          profile: object
                        extract_reason: This creates structured output with state.
                        content:
                          Build a profile object for the provided name.
                        :::

                        ## Main workflow orchestration

                        Call build_profile.
                        """
                    },
                    _ => new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: build-profile-leaf
                        skill:
                          description: Build a profile object.
                          tags: [generated, leaf]
                          inputs:
                            name: string
                          outputs:
                            profile:
                              type: object
                        workflows:
                          main:
                            inputs:
                              name: string
                            steps:
                              - id: profile
                                type: llm.call
                                input:
                                  prompt: "Build a profile object for ${data.inputs.name}."
                            outputs:
                              profile:
                                expr: "${data.steps.profile.raw}"
                                type: object
                        """
                    }
                };
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Build a profile object."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
        """);
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("weak object schemas", result.Error.Message);
        Assert.Contains("type object without properties", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RejectsUntypedArrayOutputsInLeafWorkflows()
    {
        var callCount = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new LLMResponse { Text = "# Automation\n\nCollect records." },
                    2 => new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="collect_data"
                        goal: Collect source records.
                        inputs:
                          query: string
                        outputs:
                          records: array
                        extract_reason: This produces records for the parent workflow.
                        content:
                          Collect records for the provided query.
                        :::

                        ## Main workflow orchestration

                        Call collect_data.
                        """
                    },
                    _ => new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: collect-data-leaf
                        skill:
                          description: Collect source records.
                          tags: [generated, leaf]
                          inputs:
                            query: string
                          outputs:
                            records: array
                        workflows:
                          main:
                            inputs:
                              query: string
                            steps:
                              - id: collect
                                type: llm.call
                                input:
                                  prompt: "Collect records for ${data.inputs.query}."
                            outputs:
                              records: "${data.steps.collect.raw}"
                        """
                    }
                };
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Collect records."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 1
        """);
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("weak array output schemas", result.Error.Message);
        Assert.Contains("not typed as array", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_RegeneratesLeafWorkflowsUpToParentMaxAttempts()
    {
        var requests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) =>
            {
                lock (requests)
                    requests.Add(req);
            })
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nBuild a profile object." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="build_profile"
                        goal: Build a profile object.
                        inputs:
                          name: string
                        outputs:
                          profile: object
                        extract_reason: This creates structured output with state.
                        content:
                          Build a profile object for the provided name.
                        :::

                        ## Main workflow orchestration

                        Call build_profile.
                        """
                    };

                if (req.Prompt.Contains("reviewing the quality of a `workflow.plan` pipeline", StringComparison.Ordinal))
                    return CreateExtractionQualityReviewResponse(
                        92,
                        "pass",
                        retryGuidance: "Extraction is faithful and can proceed.");

                if (req.Prompt.Contains("Previous generated YAML for this leaf workflow failed validation", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: build-profile-leaf
                        skill:
                          description: Build a profile object.
                          tags: [generated, leaf]
                          inputs:
                            name: string
                          outputs:
                            profile:
                              type: object
                              properties:
                                name:
                                  type: string
                        workflows:
                          main:
                            inputs:
                              name: string
                            steps:
                              - id: profile
                                type: llm.call
                                input:
                                  prompt: "Build a profile object for ${data.inputs.name}."
                            outputs:
                              profile:
                                expr: "${data.steps.profile.raw}"
                                type: object
                                properties:
                                  name:
                                    type: string
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `build_profile`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: build-profile-leaf
                        skill:
                          description: Build a profile object.
                          tags: [generated, leaf]
                          inputs:
                            name: string
                          outputs:
                            profile:
                              type: object
                        workflows:
                          main:
                            inputs:
                              name: string
                            steps:
                              - id: profile
                                type: llm.call
                                input:
                                  prompt: "Build a profile object for ${data.inputs.name}."
                            outputs:
                              profile:
                                expr: "${data.steps.profile.raw}"
                                type: object
                        """
                    };

                return TryRespondToPipelineMainAssembly(req)
                    ?? throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Build a profile object."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 2
        """);
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(6, requests.Count);
        Assert.Contains(requests, request =>
            request.Prompt.Contains("Previous generated YAML for this leaf workflow failed validation", StringComparison.Ordinal)
            && request.Prompt.Contains("Cumulative leaf retry requirements:", StringComparison.Ordinal)
            && request.Prompt.Contains("Preserve all fixes made for earlier validation failures", StringComparison.Ordinal)
            && request.Prompt.Contains("Re-check every mcp.call in the leaf", StringComparison.Ordinal)
            && request.Prompt.Contains("Workflow outputs must resolve to their declared type on every path.", StringComparison.Ordinal)
            && request.Prompt.Contains("weak object schemas", StringComparison.Ordinal)
            && request.Prompt.Contains("<user_prompt>", StringComparison.Ordinal)
            && request.Prompt.Contains("</user_prompt>", StringComparison.Ordinal)
            && request.Prompt.Contains("<previous_prompt>", StringComparison.Ordinal)
            && request.Prompt.Contains("</previous_prompt>", StringComparison.Ordinal)
            && request.Prompt.Contains("<invalid_yaml>", StringComparison.Ordinal)
            && request.Prompt.Contains("name: build-profile-leaf", StringComparison.Ordinal)
            && request.Prompt.Contains("type: object", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_AllowsLeafDeclaredTargetPathInput()
    {
        var requests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) =>
            {
                lock (requests)
                    requests.Add(req);
            })
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Clone\n\nClone a repository for an issue into a derived working directory." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Clone

                        :::subworkflow name="clone_repository_for_issue"
                        goal: Clone a repository for one issue.
                        inputs:
                          repo_url: string
                          issue_number: number
                          working_directory_base: string
                        outputs:
                          local_repo_path: string
                        extract_reason: This prepares an isolated working directory for later code steps.
                        content:
                          Derive a target directory from the working directory base and issue number.
                          Clone the repository into that derived directory and return the relative local path.
                        :::

                        ## Main workflow orchestration

                        Derive working_directory_base, then call clone_repository_for_issue.
                        """
                    };

                if (req.Prompt.Contains("Previous generated YAML for this leaf workflow failed validation", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: clone-repository-leaf
                        skill:
                          description: Clone repository for one issue.
                          tags: [generated, leaf]
                          inputs:
                            repo_url: string
                            issue_number: number
                            working_directory_base: string
                          outputs:
                            local_repo_path: string
                        workflows:
                          main:
                            inputs:
                              repo_url: string
                              issue_number: number
                              working_directory_base: string
                            steps:
                              - id: derive_target_directory
                                type: set
                                output_schema:
                                  type: object
                                  properties:
                                    targetDirectory:
                                      type: string
                                  required: [targetDirectory]
                                  additionalProperties: false
                                input:
                                  targetDirectory: "${data.inputs.working_directory_base}/issue-${toString(data.inputs.issue_number)}"
                            outputs:
                              local_repo_path:
                                expr: "${data.steps.derive_target_directory.targetDirectory}"
                                type: string
                                description: Relative clone path for the issue.
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `clone_repository_for_issue`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: clone-repository-leaf
                        skill:
                          description: Clone repository for one issue.
                          tags: [generated, leaf]
                          inputs:
                            repo_url: string
                            issue_number: number
                            working_directory_base: string
                            targetDirectory:
                              type: string
                              required: true
                              description: Workspace-relative empty or non-existing directory path where the repository should be created.
                          outputs:
                            local_repo_path: string
                        workflows:
                          main:
                            inputs:
                              repo_url: string
                              issue_number: number
                              working_directory_base: string
                              targetDirectory:
                                type: string
                                required: true
                                description: Workspace-relative empty or non-existing directory path where the repository should be created.
                            steps:
                              - id: prepare_clone_message
                                type: template.render
                                input:
                                  engine: mustache
                                  template: "clone {{repo_url}} into {{targetDirectory}}"
                                  mode: text
                                  data:
                                    repo_url: ${data.inputs.repo_url}
                                    targetDirectory: ${data.inputs.targetDirectory}
                              - id: result
                                type: set
                                input:
                                  local_repo_path: "${data.inputs.targetDirectory}"
                            outputs:
                              local_repo_path:
                                expr: "${data.steps.result.local_repo_path}"
                                type: string
                                description: Relative clone path for the issue.
                        """
                    };

                if (req.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        document:
                          name: clone_issue_pipeline
                          skill:
                            description: Clone a repository for one issue.
                            inputs:
                              repo_url: string
                              issue_number: number
                            outputs:
                              local_repo_path: string
                        graph:
                          inputs:
                            repo_url: string
                            issue_number: number
                          steps:
                            - id: derive_working_directory_base
                              type: set
                              input:
                                working_directory_base: issues
                            - id: derive_target_directory
                              type: set
                              input:
                                targetDirectory: "${data.steps.derive_working_directory_base.working_directory_base}/issue-${toString(data.inputs.issue_number)}"
                            - id: call_clone_repository_for_issue
                              leaf: clone_repository_for_issue
                              args:
                                repo_url: ${data.inputs.repo_url}
                                issue_number: ${data.inputs.issue_number}
                                working_directory_base: ${data.steps.derive_working_directory_base.working_directory_base}
                                targetDirectory: ${data.steps.derive_target_directory.targetDirectory}
                          outputs:
                            local_repo_path: ${data.steps.call_clone_repository_for_issue.outputs.local_repo_path}
                        """
                    };

                throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Clone a repository for an issue."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 2
        """);
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.DoesNotContain(requests, request =>
            request.Prompt.Contains("Previous generated YAML for this leaf workflow failed validation", StringComparison.Ordinal));

        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("targetDirectory:", yaml);
        Assert.Contains("working_directory_base: ${data.steps.derive_working_directory_base.working_directory_base}", yaml);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_LeafRetryIncludesYamlFromChildPlanValidationFailure()
    {
        var requests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) =>
            {
                lock (requests)
                    requests.Add(req);
            })
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nBuild a token workflow." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="build_token_workflow"
                        goal: Build a token workflow.
                        inputs:
                          name: string
                        outputs:
                          text: string
                        extract_reason: This orchestrates an LLM call and validates runtime arguments.
                        content:
                          Use the provided name in an LLM call.
                        :::

                        ## Main workflow orchestration

                        Call build_token_workflow.
                        """
                    };

                if (req.Prompt.Contains("Previous generated YAML for this leaf workflow failed validation", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: build-token-workflow-leaf
                        skill:
                          description: Build a token workflow.
                          tags: [generated, leaf]
                          inputs:
                            name: string
                          outputs:
                            text: string
                        workflows:
                          main:
                            inputs:
                              name: string
                            steps:
                              - id: answer
                                type: llm.call
                                input:
                                  prompt: "Hello ${data.inputs.name}"
                            outputs:
                              text: "${data.steps.answer.text}"
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `build_token_workflow`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: build-token-workflow-leaf
                        skill:
                          description: Build a token workflow.
                          tags: [generated, leaf]
                          inputs:
                            name: string
                          outputs:
                            text: string
                        workflows:
                          main:
                            inputs:
                              name: string
                            steps:
                              - id: answer
                                type: llm.call
                                input:
                                  prompt: "Hello ${data.inputs.name}"
                                  max_tokens: "${data.inputs.name}"
                            outputs:
                              text: "${data.steps.answer.text}"
                        """
                    };

                return TryRespondToPipelineMainAssembly(req)
                    ?? throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: pipeline
                  raw_prompt: "Build a token workflow."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: true
                    dry_run: true
                  on_invalid:
                    max_attempts: 2
        """);
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(requests, request =>
            request.Prompt.Contains("Previous generated YAML for this leaf workflow failed validation", StringComparison.Ordinal)
            && request.Prompt.Contains(ErrorCodes.ExprTypeMismatch, StringComparison.Ordinal)
            && request.Prompt.Contains("<invalid_yaml>", StringComparison.Ordinal)
            && request.Prompt.Contains("max_tokens: \"${data.inputs.name}\"", StringComparison.Ordinal)
            && request.Prompt.Contains("</invalid_yaml>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_LeafRetryIncludesLikelyMcpToolDefinitionsForUnknownServerAlias()
    {
        var requests = new List<LLMRequest>();
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) =>
            {
                lock (requests)
                    requests.Add(req);
            })
            .ReturnsAsync((LLMRequest req, CancellationToken _) =>
            {
                if (req.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Automation\n\nClassify an issue with Copilot." };

                if (req.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        # Automation

                        :::subworkflow name="classify_issue_via_copilot_ask"
                        goal: Classify an issue with Copilot.
                        inputs:
                          issue: string
                        outputs:
                          category: string
                        extract_reason: This leaf calls the Copilot ask tool.
                        content:
                          Use GitHub Copilot ask to classify the provided issue.
                        :::

                        ## Main workflow orchestration

                        Call classify_issue_via_copilot_ask.
                        """
                    };

                if (req.Prompt.Contains("Previous generated YAML for this leaf workflow failed validation", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: classify-issue-via-copilot-ask-leaf
                        skill:
                          description: Classify an issue with Copilot.
                          tags: [generated, leaf, copilot]
                          inputs:
                            issue: string
                          outputs:
                            category: string
                        workflows:
                          classify_issue_via_copilot_ask:
                            inputs:
                              issue: string
                            steps:
                              - id: call_copilot_ask
                                type: mcp.call
                                input:
                                  server: GnOuGo.GithubCopilot.Mcp
                                  method: ask
                                  request:
                                    question: "Classify this issue: ${data.inputs.issue}"
                            outputs:
                              category: triage
                        """
                    };

                if (req.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `classify_issue_via_copilot_ask`.", StringComparison.Ordinal))
                    return new LLMResponse
                    {
                        Text = """
                        version: 1
                        name: classify-issue-via-copilot-ask-leaf
                        skill:
                          description: Classify an issue with Copilot.
                          tags: [generated, leaf, copilot]
                          inputs:
                            issue: string
                          outputs:
                            category: string
                        workflows:
                          classify_issue_via_copilot_ask:
                            inputs:
                              issue: string
                            steps:
                              - id: call_copilot_ask
                                type: mcp.call
                                input:
                                  server: copilot-ask
                                  method: ask
                                  request:
                                    question: "Classify this issue: ${data.inputs.issue}"
                            outputs:
                              category: triage
                        """
                    };

                return TryRespondToPipelineMainAssembly(req)
                    ?? throw new InvalidOperationException("Unexpected LLM prompt: " + req.Prompt);
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("GnOuGo.GithubCopilot.Mcp", new MockMcpServerConfig
        {
            Description = "GitHub Copilot operations",
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "ask",
                    Description = "Ask GitHub Copilot",
                    InputSchema = JsonNode.Parse("""
                    { "type": "object", "required": ["question"], "properties": { "question": { "type": "string" } }, "additionalProperties": false }
                    """)
                }
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
                  mode: pipeline
                  raw_prompt: "Classify an issue with Copilot."
                  generator:
                    model: gpt-4
                    prefilter: false
                  validate:
                    compile: true
                  on_invalid:
                    max_attempts: 2
        """);
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(requests, request =>
            request.Prompt.Contains("Previous generated YAML for this leaf workflow failed validation", StringComparison.Ordinal)
            && request.Prompt.Contains("Additional validation repair context", StringComparison.Ordinal)
            && request.Prompt.Contains("unknown MCP server `copilot-ask`", StringComparison.Ordinal)
            && request.Prompt.Contains("Likely matching discovered server(s):", StringComparison.Ordinal)
            && request.Prompt.Contains("Available tools on `GnOuGo.GithubCopilot.Mcp`: ask", StringComparison.Ordinal)
            && request.Prompt.Contains("- ask: Ask GitHub Copilot", StringComparison.Ordinal)
            && request.Prompt.Contains("input_schema_json", StringComparison.Ordinal));
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
                Text = ValidGeneratedTemplateWorkflowYaml
            });

        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: plan
        type: workflow.plan
        input:
          mode: basic
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
    public async Task WorkflowPlan_GenerationPromptWarnsAgainstBooleanPredicatesForStringOutputs()
    {
        string? capturedPrompt = null;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated workflow.
                         tags: [generated]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: ok
                               type: set
                               input:
                                 value: done
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a classification workflow
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(capturedPrompt);
        Assert.Contains("Workflow output expressions must match the declared output contract type exactly.", capturedPrompt);
        Assert.Contains("Comparison/predicate expressions such as `${a == b}`", capturedPrompt);
        Assert.Contains("Invalid for a string output", capturedPrompt);
        Assert.Contains("structured_output", capturedPrompt);
    }

    [Fact]
    public async Task WorkflowPlan_RepromptExplainsBooleanPredicateStringOutputMismatch()
    {
        var requests = new List<LLMRequest>();
        var responses = new Queue<string>(new[]
        {
            """
            version: 1
            skill:
              description: Generated classification workflow.
              tags: [generated, classification]
              inputs: {}
              outputs:
                classification: string
                feasibility_level: string
                security_level: string
            workflows:
              main:
                steps:
                  - id: classify
                    type: set
                    input:
                      classification: bug
                      feasibility_level: high
                      security_level: low
                outputs:
                  classification:
                    expr: "${data.steps.classify.classification == 'bug'}"
                    type: string
                  feasibility_level:
                    expr: "${data.steps.classify.feasibility_level == 'high'}"
                    type: string
                  security_level:
                    expr: "${data.steps.classify.security_level == 'low'}"
                    type: string
            """,
            """
            version: 1
            skill:
              description: Generated classification workflow.
              tags: [generated, classification]
              inputs: {}
              outputs:
                classification: string
                feasibility_level: string
                security_level: string
            workflows:
              main:
                steps:
                  - id: classify
                    type: set
                    input:
                      classification: bug
                      feasibility_level: high
                      security_level: low
                outputs:
                  classification:
                    expr: "${data.steps.classify.classification}"
                    type: string
                  feasibility_level:
                    expr: "${data.steps.classify.feasibility_level}"
                    type: string
                  security_level:
                    expr: "${data.steps.classify.security_level}"
                    type: string
            """
        });

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => requests.Add(req))
            .ReturnsAsync(() => new LLMResponse { Text = responses.Dequeue() });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a classification workflow
                  on_invalid:
                    action: reprompt
                    max_attempts: 2
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, requests.Count);
        var retryPrompt = requests[1].Prompt;
        Assert.Contains("\"details\"", retryPrompt);
        Assert.Contains("\"diagnostics\"", retryPrompt);
        Assert.Contains("\"llm_guidance\"", retryPrompt);
        Assert.Contains("code=EXPR_TYPE_MISMATCH", retryPrompt);
        Assert.Contains("Expression type mismatch repair guidance", retryPrompt);
        Assert.Contains("Affected output field(s): `outputs.classification`, `outputs.feasibility_level`, `outputs.security_level`", retryPrompt);
        Assert.Contains("A comparison/predicate expression returns boolean", retryPrompt);
        Assert.Contains("cannot satisfy a string output contract", retryPrompt);
        Assert.Contains("Invalid string output", retryPrompt);
        Assert.Contains("Valid string output", retryPrompt);
        Assert.Contains("structured_output", retryPrompt);
        Assert.Contains("<structured_output_strict_schema_rules>", retryPrompt);
        Assert.Contains("Never use `type: any`", retryPrompt);
        Assert.Contains("Do not generate bare object schemas", retryPrompt);
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
                                     mode: basic
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
          mode: basic
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
                    Text = """
                           version: 1
                           name: repaired
                           skill:
                             description: Repaired workflow.
                             tags: [test]
                             inputs: {}
                             outputs: {}
                           workflows:
                             main:
                               steps:
                                 - id: s
                                   type: template.render
                                   input:
                                     engine: mustache
                                     template: ok
                                     mode: text
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
          mode: basic
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
        Assert.Contains("required_properties", prompts[1]);
        Assert.Contains("Input-level `required` is only a boolean", prompts[1]);
        Assert.Contains("<workflow_plan_generation_guardrails>", prompts[1]);
        Assert.Contains("iteration.build_issue_result.handled_by_gnougo", prompts[1]);
        Assert.Contains("Emit booleans and numbers as unquoted YAML scalars", prompts[1]);
        Assert.Contains("Use YAML literal block scalars (`|`) for multiline prompts/templates", prompts[1]);
        Assert.Contains("Fix the issues", prompts[1]);
    }

    [Fact]
    public async Task WorkflowPlan_Reprompt_IsAutomaticByDefault()
    {
        var prompts = new List<string>();
        var callCount = 0;

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((req, _) => prompts.Add(req.Prompt))
            .ReturnsAsync(() =>
            {
                callCount++;
                return new LLMResponse
                {
                    Text = callCount == 1
                        ? "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s\n"
                        : """
                          version: 1
                          name: repaired-by-default
                          skill:
                            description: Repaired workflow.
                            tags: [test]
                            inputs: {}
                            outputs: {}
                          workflows:
                            main:
                              steps:
                                - id: s
                                  type: set
                                  input:
                                    value: ok
                          """
                };
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build something
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 2
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, prompts.Count);
        Assert.DoesNotContain("<previous_error>", prompts[0]);
        Assert.Contains("<previous_error>", prompts[1]);
        Assert.Contains("<invalid_yaml>", prompts[1]);
    }

    [Fact]
    public async Task WorkflowPlan_StrictValidation_IgnoresCompileFalse()
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
                             - id: s
                               type: template.render
                               input:
                                 template: ok
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build something invalid
                  validate:
                    compile: false
                  on_invalid:
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("Generated workflow validation failed", result.Error.Message);
        Assert.Contains("SKILL_REQUIRED", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_Reprompt_ExplainsDuplicateRequiredKey()
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
                        skill:
                          description: Duplicate required test.
                          tags: [test]
                          inputs:
                            payload:
                              type: object
                              required: true
                              properties:
                                title:
                                  type: string
                              required: [title]
                          outputs: {}
                        workflows:
                          main:
                            inputs:
                              payload:
                                type: object
                                required: true
                                properties:
                                  title:
                                    type: string
                                required: [title]
                            steps: []
                        """
                    };
                }

                return new LLMResponse
                {
                    Text = """
                    version: 1
                    name: duplicate-required-test
                    skill:
                      description: Duplicate required test.
                      tags: [test]
                      inputs:
                        payload:
                          type: object
                          required: true
                          properties:
                            title:
                              type: string
                          required_properties: [title]
                      outputs: {}
                    workflows:
                      main:
                        inputs:
                          payload:
                            type: object
                            required: true
                            properties:
                              title:
                                type: string
                            required_properties: [title]
                        steps:
                          - id: render
                            type: template.render
                            input:
                              engine: mustache
                              template: "{{title}}"
                              data:
                                title: "${data.inputs.payload.title}"
                              mode: text
                    """
                };
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build an object schema workflow
                  on_invalid:
                    action: reprompt
                    max_attempts: 2
                  validate:
                    compile: false
        """);
        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, prompts.Count);
        Assert.Contains("<duplicate_required_key_fix>", prompts[1]);
        Assert.Contains("Move required object property names to `required_properties:`", prompts[1]);
        Assert.Contains("required_properties: [name]", prompts[1]);
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
                    Text = ValidGeneratedTemplateWorkflowYaml
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
          mode: basic
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
                        Text = "version: 1\nname: generated-workflow\nskill:\n  description: Generated workflow.\n  tags: [generated]\n  inputs: {}\n  outputs: {}\nworkflows:\n  main:\n    steps:\n      - id: s\n        type: definitely.not.a.step\n"
                    };
                }

                return new LLMResponse
                {
                    Text = ValidGeneratedTemplateWorkflowYaml
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
          mode: basic
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
          mode: basic
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
          mode: basic
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
          mode: basic
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
    public async Task WorkflowPlan_SemanticValidation_RejectsUnknownMcpBatchMethod()
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
                                 methods: [get_doc, missing_doc]
                                 request: { id: "intro" }
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo> { new() { Name = "get_doc", Description = "Get a document" } }
        });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a docs workflow
                    prefilter: false
        """);

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_METHOD_UNKNOWN", result.Error.Message);
        Assert.Contains("input.methods[1]:missing_doc", result.Error.Message);
        Assert.Contains("mcp.server:docs.method:get_doc", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_RepromptIncludesTargetedMcpToolDefinitionsForUnknownMethod()
    {
        var requests = new List<LLMRequest>();
        var responses = new Queue<string>(new[]
        {
            """
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
            """,
            """
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
            """
        });

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) => requests.Add(request))
            .ReturnsAsync(() => new LLMResponse { Text = responses.Dequeue() });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Description = "Document operations",
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "get_doc",
                    Description = "Get a document by id",
                    InputSchema = JsonNode.Parse("""
                    { "type": "object", "required": ["id"], "properties": { "id": { "type": "string" } }, "additionalProperties": false }
                    """),
                    OutputSchema = JsonNode.Parse("""
                    { "type": "object", "properties": { "title": { "type": "string" } }, "additionalProperties": false }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a docs workflow
                    prefilter: false
                  on_invalid:
                    action: reprompt
                    max_attempts: 2
        """);

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, requests.Count);
        var retryPrompt = requests[1].Prompt;
        Assert.Contains("MCP docs for failed/referenced calls", retryPrompt);
        Assert.Contains("Available MCP servers: docs", retryPrompt);
        Assert.Contains("Available tools on `docs`: get_doc", retryPrompt);
        Assert.Contains("Unknown requested method(s): missing_doc", retryPrompt);
        Assert.Contains("- get_doc: Get a document by id", retryPrompt);
        Assert.Contains("input_schema_json", retryPrompt);
        Assert.Contains("output_schema_json", retryPrompt);
        Assert.Contains("method: <exact-tool>", retryPrompt);
        Assert.Contains("For every listed input_schema_json, copy all required request properties into input.request", retryPrompt);
        Assert.Contains("When repairing one MCP call, re-check every MCP call in the YAML", retryPrompt);
    }

    [Fact]
    public async Task WorkflowPlan_RepromptIncludesLikelyMcpToolDefinitionsForUnknownServerAlias()
    {
        var requests = new List<LLMRequest>();
        var responses = new Queue<string>(new[]
        {
            """
            version: 1
            skill:
              description: Generated Copilot workflow.
              tags: [copilot]
              inputs: {}
              outputs: {}
            workflows:
              main:
                steps:
                  - id: call_copilot_ask
                    type: mcp.call
                    input:
                      server: copilot-ask
                      method: ask
                      request: { question: "How should I classify this issue?" }
            """,
            """
            version: 1
            skill:
              description: Generated Copilot workflow.
              tags: [copilot]
              inputs: {}
              outputs: {}
            workflows:
              main:
                steps:
                  - id: call_copilot_ask
                    type: mcp.call
                    input:
                      server: GnOuGo.GithubCopilot.Mcp
                      method: ask
                      request: { question: "How should I classify this issue?" }
            """
        });

        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) => requests.Add(request))
            .ReturnsAsync(() => new LLMResponse { Text = responses.Dequeue() });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("GnOuGo.GithubCopilot.Mcp", new MockMcpServerConfig
        {
            Description = "GitHub Copilot operations",
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "ask",
                    Description = "Ask GitHub Copilot",
                    InputSchema = JsonNode.Parse("""
                    { "type": "object", "required": ["question"], "properties": { "question": { "type": "string" } }, "additionalProperties": false }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a Copilot classification workflow
                    prefilter: false
                  on_invalid:
                    action: reprompt
                    max_attempts: 2
        """);

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, requests.Count);
        var retryPrompt = requests[1].Prompt;
        Assert.Contains("unknown MCP server `copilot-ask`", retryPrompt);
        Assert.Contains("Available MCP servers: GnOuGo.GithubCopilot.Mcp", retryPrompt);
        Assert.Contains("Likely matching discovered server(s):", retryPrompt);
        Assert.Contains("GnOuGo.GithubCopilot.Mcp: GitHub Copilot operations", retryPrompt);
        Assert.Contains("Available tools on `GnOuGo.GithubCopilot.Mcp`: ask", retryPrompt);
        Assert.Contains("- ask: Ask GitHub Copilot", retryPrompt);
        Assert.Contains("input_schema_json", retryPrompt);
    }

    [Fact]
    public async Task WorkflowPlan_Validation_FailsClosedWhenMcpDiscoveryIsUnavailable()
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
                                 request: { id: intro }
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a docs workflow
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_DISCOVERY_REQUIRED", result.Error.Message);
        Assert.Contains("fail-closed", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsUnknownMcpCallEnvelopeField()
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
                                 id: intro
                                 request: {}
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo> { new() { Name = "get_doc", InputSchema = new JsonObject { ["type"] = "object" } } }
        });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a docs workflow
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_CALL_INPUT_FIELD_UNKNOWN", result.Error.Message);
        Assert.Contains("input.id", result.Error.Message);
        Assert.Contains("input.request", result.Error.Message);
        Assert.Contains("repair diagnostics", result.Error.Message);

        var details = Assert.IsType<JsonObject>(result.Error.Details);
        Assert.Equal("validation", details["phase"]?.GetValue<string>());
        Assert.Contains("llm_guidance", details);
        var diagnostics = Assert.IsType<JsonArray>(details["diagnostics"]);
        var diagnostic = Assert.IsType<JsonObject>(diagnostics
            .OfType<JsonObject>()
            .Single(item => item["code"]?.GetValue<string>() == "MCP_CALL_INPUT_FIELD_UNKNOWN"));
        Assert.Equal("MCP_CALL_INPUT_FIELD_UNKNOWN", diagnostic["code"]?.GetValue<string>());
        Assert.Equal("workflow:main/step:fetch/field:input.id", diagnostic["location"]?.GetValue<string>());
        Assert.Contains("input.request", diagnostic["hint"]?.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_EnforcesExpandedMcpRequestSchemaKeywords()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated constrained workflow.
                         tags: [schema]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: search
                               type: mcp.call
                               input:
                                 server: catalog
                                 method: search
                                 request:
                                   mode: invalid
                                   version: v2
                                   query: x
                                   limit: 11
                                   tags: [same, same, extra]
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("catalog", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "search",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "mode": { "type": "string" },
                        "version": { "type": "string" },
                        "query": { "type": "string" },
                        "limit": { "type": "integer" },
                        "tags": { "type": "array", "items": { "type": "string" } }
                      },
                      "required": ["mode", "version", "query", "limit", "tags"],
                      "additionalProperties": false,
                      "allOf": [
                        { "properties": { "mode": { "enum": ["fast", "deep"] } } },
                        { "properties": { "version": { "const": "v1" } } },
                        { "properties": { "query": { "minLength": 3, "pattern": "^[a-z]+$" } } },
                        { "properties": { "limit": { "minimum": 1, "maximum": 10 } } },
                        { "properties": { "tags": { "minItems": 1, "maxItems": 2, "uniqueItems": true } } }
                      ]
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a constrained catalog workflow
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUEST_SCHEMA_INVALID", result.Error.Message);
        Assert.Contains("value must be one of", result.Error.Message);
        Assert.Contains("value must equal", result.Error.Message);
        Assert.Contains("at least 3 characters", result.Error.Message);
        Assert.Contains("less than or equal to 10", result.Error.Message);
        Assert.Contains("at most 2 items", result.Error.Message);
        Assert.Contains("must be unique", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsNullableMcpRequestExpression()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated command workflow.
                         tags: [cmd]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: decide
                               type: llm.call
                               input:
                                 prompt: Decide which command to run.
                                 structured_output:
                                   schema_inline:
                                     type: object
                                     properties:
                                       commandName:
                                         anyOf:
                                           - type: string
                                           - type: null
                                       parametersJson:
                                         anyOf:
                                           - type: string
                                           - type: null
                                     required: [commandName, parametersJson]
                                     additionalProperties: false
                             - id: run
                               type: mcp.call
                               input:
                                 server: cmd
                                 method: cmd_run
                                 request:
                                   commandName: "${data.steps.decide.json.commandName}"
                                   parametersJson: "${data.steps.decide.json.parametersJson}"
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a command workflow
                    prefilter: false
                  validate:
                    compile: true
                    dry_run: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = CreateCmdRunMcpFactory() }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUEST_EXPR_TYPE_MISMATCH", result.Error.Message);
        Assert.Contains("input.request.commandName", result.Error.Message);
        Assert.Contains("null", result.Error.Message);
        Assert.Contains("requires string", result.Error.Message);

        var details = Assert.IsType<JsonObject>(result.Error.Details);
        var diagnostics = Assert.IsType<JsonArray>(details["diagnostics"]);
        var diagnostic = Assert.IsType<JsonObject>(diagnostics
            .OfType<JsonObject>()
            .Single(item => item["code"]?.GetValue<string>() == "MCP_REQUEST_EXPR_TYPE_MISMATCH"));
        Assert.Equal("workflow:main/step:run/field:input.request.commandName", diagnostic["location"]?.GetValue<string>());
        Assert.Contains("nullable", diagnostic["hint"]?.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_AllowsNullableMcpRequestExpressionWithNonNullGuard()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated command workflow.
                         tags: [cmd]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: decide
                               type: llm.call
                               input:
                                 prompt: Decide which command to run.
                                 structured_output:
                                   schema_inline:
                                     type: object
                                     properties:
                                       commandName:
                                         anyOf:
                                           - type: string
                                           - type: null
                                       parametersJson:
                                         anyOf:
                                           - type: string
                                           - type: null
                                     required: [commandName, parametersJson]
                                     additionalProperties: false
                             - id: run
                               type: mcp.call
                               if: "${data.steps.decide.json.commandName != null}"
                               input:
                                 server: cmd
                                 method: cmd_run
                                 request:
                                   commandName: "${data.steps.decide.json.commandName}"
                                   parametersJson: "${data.steps.decide.json.parametersJson}"
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a guarded command workflow
                    prefilter: false
                  validate:
                    compile: true
                    dry_run: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = CreateCmdRunMcpFactory() }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var plan = Assert.IsType<JsonObject>(result.Outputs!["plan"]);
        Assert.Contains("commandName != null", plan["yaml"]?.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowPlan_RepairPrompt_ExplainsNullableMcpRequestExpression()
    {
        var requests = new List<LLMRequest>();
        var callIndex = 0;
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LLMRequest, CancellationToken>((request, _) => requests.Add(request))
            .ReturnsAsync(() => (++callIndex) switch
            {
                1 => new LLMResponse
                {
                    Text = """
                           version: 1
                           skill:
                             description: Generated command workflow.
                             tags: [cmd]
                             inputs: {}
                             outputs: {}
                           workflows:
                             main:
                               steps:
                                 - id: decide
                                   type: llm.call
                                   input:
                                     prompt: Decide which command to run.
                                     structured_output:
                                       schema_inline:
                                         type: object
                                         properties:
                                           commandName:
                                             anyOf:
                                               - type: string
                                               - type: null
                                           parametersJson:
                                             anyOf:
                                               - type: string
                                               - type: null
                                         required: [commandName, parametersJson]
                                         additionalProperties: false
                                 - id: run
                                   type: mcp.call
                                   input:
                                     server: cmd
                                     method: cmd_run
                                     request:
                                       commandName: "${data.steps.decide.json.commandName}"
                                       parametersJson: "${data.steps.decide.json.parametersJson}"
                           """
                },
                _ => new LLMResponse
                {
                    Text = """
                           version: 1
                           skill:
                             description: Generated command workflow.
                             tags: [cmd]
                             inputs: {}
                             outputs: {}
                           workflows:
                             main:
                               steps:
                                 - id: decide
                                   type: llm.call
                                   input:
                                     prompt: Decide which command to run.
                                     structured_output:
                                       schema_inline:
                                         type: object
                                         properties:
                                           commandName:
                                             anyOf:
                                               - type: string
                                               - type: null
                                           parametersJson:
                                             anyOf:
                                               - type: string
                                               - type: null
                                         required: [commandName, parametersJson]
                                         additionalProperties: false
                                 - id: run
                                   type: mcp.call
                                   if: "${data.steps.decide.json.commandName != null}"
                                   input:
                                     server: cmd
                                     method: cmd_run
                                     request:
                                       commandName: "${data.steps.decide.json.commandName}"
                                       parametersJson: "${data.steps.decide.json.parametersJson}"
                           """
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a command workflow
                    prefilter: false
                  validate:
                    compile: true
                    dry_run: false
                  on_invalid:
                    action: reprompt
                    max_attempts: 2
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = CreateCmdRunMcpFactory() }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, requests.Count);
        var repairPrompt = requests[1].Prompt;
        Assert.Contains("MCP_REQUEST_EXPR_TYPE_MISMATCH", repairPrompt);
        Assert.Contains("input.request.commandName", repairPrompt);
        Assert.Contains("nullable", repairPrompt);
        Assert.Contains("if: \"${data.steps.decide.json.commandName != null}\"", repairPrompt);
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
          mode: basic
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
          mode: basic
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
    public async Task WorkflowPlan_SemanticValidation_ReportsOpaqueNonResponsePathWithoutCrashing()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated workflow with an invalid opaque mapping.
                         tags: [generated]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: generate
                               type: llm.call
                               input:
                                 prompt: Generate a value.
                             - id: map
                               type: set
                               input:
                                 value: "${data.steps.generate.raw.missing}"
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a generated workflow
                    prefilter: false
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("OPAQUE_RESPONSE_DEEP_ACCESS", result.Error.Message);
        Assert.Contains("data.steps.generate.raw.missing", result.Error.Message);
        Assert.Contains("opaque output", result.Error.Message);
        Assert.DoesNotContain("length ('-1')", result.Error.Message, StringComparison.OrdinalIgnoreCase);
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
          mode: basic
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
    public async Task WorkflowPlan_SemanticValidation_AllowsOptionalNumericPerPageToBeOmitted()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated GitHub issue workflow.
                         tags: [github, issues]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: issues
                               type: mcp.call
                               input:
                                 server: Github
                                 kind: tool
                                 method: list_issues
                                 request:
                                   owner: AxaFrance
                                   repo: oidc-client
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("Github", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "list_issues",
                    Description = "List GitHub issues",
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
          mode: basic
          generator:
            model: gpt-4
            instruction: List GitHub issues
            prefilter: false
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
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
          mode: basic
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
        Assert.Contains(ErrorCodes.ExprTypeMismatch, result.Error.Message);
        Assert.Contains("requires integer", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_StrictValidation_RejectsUnknownMcpToolWhenCompileValidationIsDisabled()
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
                                 method: invented_tool
                                 request: {}
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo> { new() { Name = "get_doc", InputSchema = new JsonObject { ["type"] = "object" } } }
        });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a docs workflow
                    prefilter: false
                  validate:
                    compile: false
                    dry_run: true
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("Generated workflow validation failed", result.Error.Message);
        Assert.Contains("MCP_METHOD_UNKNOWN", result.Error.Message);
        Assert.Contains("invented_tool", result.Error.Message);
        Assert.Contains("get_doc", result.Error.Message);

        var details = Assert.IsType<JsonObject>(result.Error.Details);
        Assert.Equal("validation", details["phase"]?.GetValue<string>());
        Assert.Contains("llm_guidance", details);
        var diagnostics = Assert.IsType<JsonArray>(details["diagnostics"]);
        var diagnostic = Assert.IsType<JsonObject>(Assert.Single(diagnostics)!);
        Assert.Equal("MCP_METHOD_UNKNOWN", diagnostic["code"]?.GetValue<string>());
        Assert.Equal("semantic_validation", diagnostic["phase"]?.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowPlan_DryRun_AllowsMcpSuccessEnvelopeWithNullableErrorFields()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       name: generated-mcp-write-workflow
                       skill:
                         description: Generated MCP write workflow.
                         tags: [mcp, dry-run]
                         inputs:
                           content:
                             type: string
                             required: true
                         outputs:
                           write_status:
                             type: string
                       workflows:
                         main:
                           inputs:
                             content:
                               type: string
                               required: true
                           steps:
                             - id: write
                               type: mcp.call
                               input:
                                 server: docs
                                 kind: tool
                                 method: write_result
                                 request:
                                   filePath: "reports/dry-run-result.md"
                                   content: "${data.inputs.content}"
                                   append: false
                           outputs:
                             write_status:
                               expr: "${data.steps.write.status}"
                               type: string
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "write_result",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "filePath": { "type": "string" },
                        "content": { "type": "string" },
                        "append": { "type": "boolean" }
                      },
                      "required": ["filePath", "content"],
                      "additionalProperties": false
                    }
                    """),
                    OutputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "success": { "type": "boolean" },
                        "filePath": {
                          "anyOf": [
                            { "type": "string" },
                            { "type": "null" }
                          ]
                        },
                        "bytesWritten": {
                          "anyOf": [
                            { "type": "integer" },
                            { "type": "null" }
                          ]
                        },
                        "errorCode": {
                          "anyOf": [
                            { "type": "string" },
                            { "type": "null" }
                          ]
                        },
                        "errorMessage": {
                          "anyOf": [
                            { "type": "string" },
                            { "type": "null" }
                          ]
                        },
                        "relativePath": {
                          "anyOf": [
                            { "type": "string" },
                            { "type": "null" }
                          ]
                        },
                        "filePathAbsolute": {
                          "anyOf": [
                            { "type": "string" },
                            { "type": "null" }
                          ]
                        }
                      },
                      "required": ["success", "filePath", "bytesWritten", "errorCode", "errorMessage"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a workflow that writes a result through MCP
                    prefilter: false
                  validate:
                    dry_run: true
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
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
          mode: basic
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
          mode: basic
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
          mode: basic
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
          mode: basic
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
    public async Task WorkflowPlan_SemanticValidation_RejectsQuotedTypedStepScalars()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated extraction workflow.
                         tags: [generated]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: extract
                               type: llm.call
                               input:
                                 prompt: Extract issues.
                                 structured_output:
                                   strict: "true"
                                   schema_inline:
                                     type: object
                                     properties:
                                       issues:
                                         type: array
                                         items:
                                           type: object
                                           properties:
                                             title:
                                               type: string
                                           required: [title]
                                           additionalProperties: false
                                     required: [issues]
                                     additionalProperties: false
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build an extraction workflow
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("input.structured_output.strict", result.Error.Message);
        Assert.Contains("boolean", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsQuotedTypedMcpRequestScalars()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated GitHub issue workflow.
                         tags: [github, issues]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: list_issues
                               type: mcp.call
                               input:
                                 server: github
                                 kind: tool
                                 method: list_issues
                                 request:
                                   owner: AxaFrance
                                   repo: oidc-client
                                   perPage: "30"
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "list_issues",
                    Description = "List issues",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "owner": { "type": "string" },
                        "repo": { "type": "string" },
                        "perPage": { "type": "integer" }
                      },
                      "required": ["owner", "repo", "perPage"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: List GitHub issues
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("input.request.perPage", result.Error.Message);
        Assert.Contains("integer", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_AllowsSchemaValidAbsoluteMcpPathLiteral()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated code workflow.
                         tags: [code]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: suggest
                               type: mcp.call
                               input:
                                 server: code
                                 kind: tool
                                 method: code_suggest_change
                                 request:
                                   workspacePath: /workspace/issue-issue-1676
                                   task: Fix the issue
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("code", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "code_suggest_change",
                    Description = "Suggest code changes",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "workspacePath": { "type": "string", "description": "Existing workspace-relative path. The directory must already exist." },
                        "task": { "type": "string" }
                      },
                      "required": ["workspacePath", "task"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Suggest a code fix
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_AllowsSchemaValidDiagnosticPathOutputInMcpStringField()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated clone and code workflow.
                         tags: [git, code]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: clone
                               type: mcp.call
                               input:
                                 server: git
                                 kind: tool
                                 method: git_clone
                                 request:
                                   remoteUrl: https://github.com/AxaFrance/oidc-client
                                   targetDirectory: issue-issue-1676
                             - id: suggest
                               type: mcp.call
                               input:
                                 server: code
                                 kind: tool
                                 method: code_suggest_change
                                 request:
                                   workspacePath: "${data.steps.clone.response.rootPath}"
                                   task: Fix the issue
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("git", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "git_clone",
                    Description = "Clone a repository",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "remoteUrl": { "type": "string" },
                        "targetDirectory": { "type": "string", "description": "Target directory path relative to the workspace root only." }
                      },
                      "required": ["remoteUrl", "targetDirectory"],
                      "additionalProperties": false
                    }
                    """),
                    OutputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "rootPath": { "type": "string" },
                        "rootPathRelative": { "type": "string" }
                      },
                      "required": ["rootPath", "rootPathRelative"]
                    }
                    """)
                }
            }
        });
        mcpFactory.RegisterServer("code", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "code_suggest_change",
                    Description = "Suggest code changes",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "workspacePath": { "type": "string", "description": "Existing workspace-relative path. The directory must already exist." },
                        "task": { "type": "string" }
                      },
                      "required": ["workspacePath", "task"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Clone a repo and suggest a code fix
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsExplicitEmptyMcpWorkspacePath()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated code workflow.
                         tags: [code]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: suggest
                               type: mcp.call
                               input:
                                 server: code
                                 kind: tool
                                 method: code_suggest_change
                                 request:
                                   workspacePath: ""
                                   task: Fix the issue
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("code", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "code_suggest_change",
                    Description = "Suggest code changes",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "workspacePath": { "type": "string", "description": "Existing workspace-relative path." },
                        "task": { "type": "string" }
                      },
                      "required": ["workspacePath", "task"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Suggest a code fix
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUIRED_STRING_EMPTY", result.Error.Message);
        Assert.Contains("input.request.workspacePath", result.Error.Message);
        Assert.Contains("empty string literal", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsExplicitNullRequiredMcpString()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated code workflow.
                         tags: [code]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: suggest
                               type: mcp.call
                               input:
                                 server: code
                                 kind: tool
                                 method: code_suggest_change
                                 request:
                                   workspacePath: null
                                   task: Fix the issue
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("code", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "code_suggest_change",
                    Description = "Suggest code changes",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "workspacePath": { "type": "string", "description": "Existing workspace-relative path." },
                        "task": { "type": "string" }
                      },
                      "required": ["workspacePath", "task"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Suggest a code fix
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUEST_SCHEMA_INVALID", result.Error.Message);
        Assert.Contains("input.request.workspacePath", result.Error.Message);
        Assert.Contains("string", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_AllowsStringOutputWithEmptyFallback()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated clone workflow.
                         tags: [git]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: done
                               type: set
                               input:
                                 ok: true
                         clone_repository_for_issue:
                           steps:
                             - id: clone_repo
                               type: mcp.call
                               input:
                                 server: git
                                 kind: tool
                                 method: git_clone
                                 request:
                                   remoteUrl: https://github.com/AxaFrance/oidc-client
                                   targetDirectory: issue-1676
                             - id: clone_result
                               type: set
                               output_schema:
                                 type: object
                                 properties:
                                   success:
                                     type: boolean
                                   workspacePathRelative:
                                     type: string
                                 required: [success, workspacePathRelative]
                                 additionalProperties: false
                               input:
                                 success: true
                                 workspacePathRelative: "${data.steps.clone_repo.response.workspacePathRelative}"
                           outputs:
                             local_workspace_path:
                               expr: "${data.steps.clone_result.success ? data.steps.clone_result.workspacePathRelative : ''}"
                               type: string
                               description: "Workspace-relative path to the created workspace, or empty string if the operation failed."
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("git", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "git_clone",
                    Description = "Clone a repository",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "remoteUrl": { "type": "string" },
                        "targetDirectory": { "type": "string", "description": "Target directory path relative to the workspace root only." }
                      },
                      "required": ["remoteUrl", "targetDirectory"],
                      "additionalProperties": false
                    }
                    """),
                    OutputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "rootPath": { "type": "string" },
                        "rootPathRelative": { "type": "string" },
                        "workspacePathRelative": { "type": "string" }
                      },
                      "required": ["rootPath", "rootPathRelative", "workspacePathRelative"]
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Clone a repo
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_AllowsMcpStringExpressionWithEmptyFallback()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated code workflow.
                         tags: [code]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: clone_result
                               type: set
                               output_schema:
                                 type: object
                                 properties:
                                   success:
                                     type: boolean
                                   workspacePathRelative:
                                     type: string
                                 required: [success, workspacePathRelative]
                                 additionalProperties: false
                               input:
                                 success: true
                                 workspacePathRelative: issue-1676
                             - id: suggest
                               type: mcp.call
                               input:
                                 server: code
                                 kind: tool
                                 method: code_suggest_change
                                 request:
                                   workspacePath: "${data.steps.clone_result.success ? data.steps.clone_result.workspacePathRelative : ''}"
                                   task: Fix the issue
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("code", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "code_suggest_change",
                    Description = "Suggest code changes",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "workspacePath": { "type": "string", "description": "Existing workspace-relative path." },
                        "task": { "type": "string" }
                      },
                      "required": ["workspacePath", "task"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Suggest a code fix
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_AllowsSchemaValidConstructedMcpStringRequest()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated workspace workflow.
                         tags: [workspace]
                         inputs:
                           item_id: string
                         outputs: {}
                       workflows:
                         main:
                           inputs:
                             item_id: string
                           steps:
                             - id: prepare
                               type: set
                               input:
                                 local_path: "repos/item-${data.inputs.item_id}"
                             - id: summarize
                               type: mcp.call
                               input:
                                 server: workspace
                                 kind: tool
                                 method: summarize_project
                                 request:
                                   workspacePath: "${data.steps.prepare.local_path}"
                                   task: Summarize the project
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("workspace", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "summarize_project",
                    Description = "Summarize an existing project workspace",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "workspacePath": {
                          "type": "string",
                          "description": "Existing workspace-relative path. The directory must already exist; pass a previous producer output."
                        },
                        "task": { "type": "string" }
                      },
                      "required": ["workspacePath", "task"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Summarize an existing workspace
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject { ["item_id"] = "1703" }, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_AllowsExistingWorkspacePathFromMcpProducerOutput()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated workspace workflow.
                         tags: [workspace]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: create_workspace
                               type: mcp.call
                               input:
                                 server: workspace
                                 kind: tool
                                 method: create_workspace
                                 request:
                                   name: item-1703
                             - id: summarize
                               type: mcp.call
                               input:
                                 server: workspace
                                 kind: tool
                                 method: summarize_project
                                 request:
                                   workspacePath: "${data.steps.create_workspace.response.workspacePathRelative}"
                                   task: Summarize the project
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("workspace", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "create_workspace",
                    Description = "Create a workspace directory",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string" }
                      },
                      "required": ["name"],
                      "additionalProperties": false
                    }
                    """),
                    OutputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "workspacePathRelative": {
                          "type": "string",
                          "description": "Workspace-relative path created by this tool."
                        }
                      },
                      "required": ["workspacePathRelative"],
                      "additionalProperties": false
                    }
                    """)
                },
                new()
                {
                    Name = "summarize_project",
                    Description = "Summarize an existing project workspace",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "workspacePath": {
                          "type": "string",
                          "description": "Existing workspace-relative path. The directory must already exist; pass a previous producer output."
                        },
                        "task": { "type": "string" }
                      },
                      "required": ["workspacePath", "task"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Create and summarize a workspace
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_AllowsCreationTargetLiteralAndDocumentedResponseProducer()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated clone workflow.
                         tags: [git, code]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: clone_repo
                               type: mcp.call
                               input:
                                 server: git
                                 kind: tool
                                 method: git_clone
                                 request:
                                   remoteUrl: https://github.com/AxaFrance/oidc-client
                                   targetDirectory: issue-1676
                             - id: suggest
                               type: mcp.call
                               input:
                                 server: code
                                 kind: tool
                                 method: code_suggest_change
                                 request:
                                   workspacePath: "${data.steps.clone_repo.response.workspacePathRelative}"
                                   task: Fix the issue
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("git", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "git_clone",
                    Description = "Creates a workspace directory. targetDirectory is a creation target, not an existing path before the call. After success, pass response.workspacePathRelative to existing workspace path inputs.",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "remoteUrl": { "type": "string" },
                        "targetDirectory": {
                          "type": "string",
                          "description": "Creation target directory path relative to the workspace root only. Must be empty or non-existing. After success, use response.workspacePathRelative as the existing workspace path for later tools."
                        }
                      },
                      "required": ["remoteUrl", "targetDirectory"],
                      "additionalProperties": false
                    }
                    """),
                    OutputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "workspacePathRelative": {
                          "type": "string",
                          "description": "Workspace-relative path created by this tool."
                        }
                      },
                      "required": ["workspacePathRelative"],
                      "additionalProperties": false
                    }
                    """)
                }
            }
        });
        mcpFactory.RegisterServer("code", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "code_suggest_change",
                    Description = "Suggest code changes",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "workspacePath": {
                          "type": "string",
                          "description": "Existing workspace-relative path. The directory must already exist; pass a previous producer output."
                        },
                        "task": { "type": "string" }
                      },
                      "required": ["workspacePath", "task"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Clone a repo and suggest a code fix
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_AllowsSchemaValidConstructedLocalWorkflowStringArg()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated workspace workflow.
                         tags: [workspace]
                         inputs:
                           issue_number: integer
                         outputs: {}
                       workflows:
                         main:
                           inputs:
                             issue_number: integer
                           steps:
                             - id: prepare
                               type: set
                               input:
                                 local_workspace_path: "repos/issue-${data.inputs.issue_number}"
                             - id: analyze
                               type: workflow.call
                               input:
                                 ref:
                                   kind: local
                                   name: analyze_workspace
                                 args:
                                   local_workspace_path: "${data.steps.prepare.local_workspace_path}"
                         analyze_workspace:
                           inputs:
                             local_workspace_path:
                               type: string
                               description: Existing workspace-relative path created by a previous step.
                           steps:
                             - id: done
                               type: set
                               input:
                                 ok: true
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Analyze an existing workspace with a local workflow
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject { ["issue_number"] = 1703 }, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsOpaqueFunctionResultInClosedSetOutputSchema()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated workflow.
                         tags: [test]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           functions: |-
                             /**
                              * Selects public items.
                              *
                              * @param {Array<object>} items - Source items.
                              * @returns {Array<object>} Selected source item objects.
                              */
                             function selectPublicItems(items) {
                               var out = [];
                               for (var i = 0; i < items.length; i++) {
                                 var item = items[i];
                                 if (item.public) out.push(item);
                               }
                               return out;
                             }
                           steps:
                             - id: source
                               type: set
                               input:
                                 items:
                                   - id: 1
                                     title: Alpha
                                     public: true
                                     internal_flag: true
                             - id: final_items
                               type: set
                               output_schema:
                                 type: object
                                 properties:
                                   items:
                                     type: array
                                     items:
                                       type: object
                                       properties:
                                         id: { type: number }
                                         title: { type: string }
                                       required: [id, title]
                                       additionalProperties: false
                                 required: [items]
                                 additionalProperties: false
                               input:
                                 items: "${functions.selectPublicItems(data.steps.source.items)}"
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Generate a filtered item workflow
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("SET_OUTPUT_SCHEMA_OPAQUE_FUNCTION", result.Error.Message);
        Assert.Contains("input.items", result.Error.Message);
        Assert.Contains("selectPublicItems", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsNullableSetOutputInRequiredMcpString()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated GitHub workflow.
                         tags: [github]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: derive_identity
                               type: set
                               output_schema:
                                 type: object
                                 properties:
                                   owner:
                                     anyOf:
                                       - type: string
                                       - type: "null"
                                   repo:
                                     anyOf:
                                       - type: string
                                       - type: "null"
                                 required: [owner, repo]
                                 additionalProperties: false
                               input:
                                 owner: "${data.inputs.owner}"
                                 repo: "${data.inputs.repo}"
                             - id: comment
                               type: mcp.call
                               input:
                                 server: github
                                 kind: tool
                                 method: add_issue_comment
                                 request:
                                   owner: "${data.steps.derive_identity.owner}"
                                   repo: "${data.steps.derive_identity.repo}"
                                   issue_number: 1703
                                   body: Investigating.
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "add_issue_comment",
                    Description = "Add an issue comment",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "owner": { "type": "string" },
                        "repo": { "type": "string" },
                        "issue_number": { "type": "integer" },
                        "body": { "type": "string" }
                      },
                      "required": ["owner", "repo", "issue_number", "body"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Add a GitHub issue comment
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUEST_EXPR_TYPE_MISMATCH", result.Error.Message);
        Assert.Contains("input.request.owner", result.Error.Message);
        Assert.Contains("resolves to null or string", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_AcceptsAssertNonNullOutputInRequiredMcpString()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated GitHub workflow.
                         tags: [github]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: derive_identity
                               type: set
                               output_schema:
                                 type: object
                                 properties:
                                   owner:
                                     anyOf:
                                       - type: string
                                       - type: "null"
                                   repo:
                                     anyOf:
                                       - type: string
                                       - type: "null"
                                 required: [owner, repo]
                                 additionalProperties: false
                               input:
                                 owner: AxaFrance
                                 repo: oidc-client
                             - id: require_identity
                               type: assert.non_null
                               input:
                                 owner: "${data.steps.derive_identity.owner}"
                                 repo: "${data.steps.derive_identity.repo}"
                             - id: comment
                               type: mcp.call
                               input:
                                 server: github
                                 kind: tool
                                 method: add_issue_comment
                                 request:
                                   owner: "${data.steps.require_identity.owner}"
                                   repo: "${data.steps.require_identity.repo}"
                                   issue_number: 1703
                                   body: Investigating.
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "add_issue_comment",
                    Description = "Add an issue comment",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "owner": { "type": "string" },
                        "repo": { "type": "string" },
                        "issue_number": { "type": "integer" },
                        "body": { "type": "string" }
                      },
                      "required": ["owner", "repo", "issue_number", "body"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Add a GitHub issue comment
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("assert.non_null", result.Outputs!["plan"]!["yaml"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsEmptyRequiredMcpString()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated GitHub issue workflow.
                         tags: [github, issues]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: comment
                               type: mcp.call
                               input:
                                 server: github
                                 kind: tool
                                 method: issue_comment
                                 request:
                                   owner: ""
                                   repo: oidc-client
                                   body: "Investigating."
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("github", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "issue_comment",
                    Description = "Comment on an issue",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "owner": { "type": "string" },
                        "repo": { "type": "string" },
                        "body": { "type": "string" }
                      },
                      "required": ["owner", "repo", "body"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Comment on a GitHub issue
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUIRED_STRING_EMPTY", result.Error.Message);
        Assert.Contains("input.request.owner", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsEmptyRequiredMcpStringForAnyField()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated note workflow.
                         tags: [notes]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: write_note
                               type: mcp.call
                               input:
                                 server: docs
                                 kind: tool
                                 method: write_note
                                 request:
                                   note: ""
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "write_note",
                    Description = "Write a note",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "note": { "type": "string" }
                      },
                      "required": ["note"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Write a note
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUIRED_STRING_EMPTY", result.Error.Message);
        Assert.Contains("input.request.note", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsEmptyMcpStringWhenSchemaRequiresNonEmpty()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated search workflow.
                         tags: [search]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: search
                               type: mcp.call
                               input:
                                 server: docs
                                 kind: tool
                                 method: search_docs
                                 request:
                                   query: ""
                       """
            });

        var mcpFactory = new InMemoryMcpClientFactory();
        mcpFactory.RegisterServer("docs", new MockMcpServerConfig
        {
            Tools = new List<McpToolInfo>
            {
                new()
                {
                    Name = "search_docs",
                    Description = "Search documents",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "query": { "type": "string", "minLength": 1 }
                      },
                      "required": ["query"],
                      "additionalProperties": false
                    }
                    """)
                }
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
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Search docs
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object, McpClientFactory = mcpFactory }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUIRED_STRING_EMPTY", result.Error.Message);
        Assert.Contains("input.request.query", result.Error.Message);
        Assert.Contains("empty string literal", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsEmptyRequiredWorkflowCallString()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated local workflow.
                         tags: [github]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: call_child
                               type: workflow.call
                               input:
                                 ref:
                                   kind: local
                                   name: append_report
                                 args:
                                   pull_request_link: ""
                         append_report:
                           inputs:
                             pull_request_link:
                               type: string
                               required: true
                           steps:
                             - id: done
                               type: set
                               input:
                                 ok: true
                           outputs:
                             ok:
                               expr: "${data.steps.done.ok}"
                               type: boolean
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a local workflow
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("WORKFLOW_CALL_REQUIRED_STRING_EMPTY", result.Error.Message);
        Assert.Contains("input.args.pull_request_link", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsEmptyRequiredWorkflowCallStringArg()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated local workflow.
                         tags: [local]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: call_child
                               type: workflow.call
                               input:
                                 ref:
                                   kind: local
                                   name: process_record
                                 args:
                                   recipient: ""
                                   subject: report
                         process_record:
                           inputs:
                             recipient:
                               type: string
                               required: true
                             subject:
                               type: string
                               required: true
                           steps:
                             - id: done
                               type: set
                               input:
                                 ok: true
                           outputs:
                             ok:
                               expr: "${data.steps.done.ok}"
                               type: boolean
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a local workflow
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("WORKFLOW_CALL_REQUIRED_STRING_EMPTY", result.Error.Message);
        Assert.Contains("input.args.recipient", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsWorkflowCallStringArgSourcedFromKnownEmptySetOutput()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated local workflow.
                         tags: [local]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: init_globals
                               type: set
                               output_schema:
                                 type: object
                                 properties:
                                   recipient:
                                     type: string
                                   subject:
                                     type: string
                                 required_properties: [recipient, subject]
                                 additionalProperties: false
                               input:
                                 recipient: ""
                                 subject: ""
                             - id: call_child
                               type: workflow.call
                               input:
                                 ref:
                                   kind: local
                                   name: process_record
                                 args:
                                   recipient: "${data.steps.init_globals.recipient}"
                                   subject: "${data.steps.init_globals.subject}"
                         process_record:
                           inputs:
                             recipient:
                               type: string
                               required: true
                             subject:
                               type: string
                               required: true
                           steps:
                             - id: done
                               type: set
                               input:
                                 ok: true
                           outputs:
                             ok:
                               expr: "${data.steps.done.ok}"
                               type: boolean
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a local workflow
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("WORKFLOW_CALL_REQUIRED_STRING_EMPTY", result.Error.Message);
        Assert.Contains("input.args.recipient", result.Error.Message);
        Assert.Contains("data.steps.init_globals.recipient", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsDirectLoopResultFieldAccessInCustomFunction()
    {
        var mockLlm = new Mock<ILLMClient>();
        mockLlm.Setup(l => l.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse
            {
                Text = """
                       version: 1
                       skill:
                         description: Generated issue filtering workflow.
                         tags: [github, issues]
                         inputs: {}
                         outputs: {}
                       functions: |
                         /**
                          * Filters unprocessed issue iterations.
                          * @param {Array<object>} iterations - Per-iteration loop result snapshots.
                          * @returns {Array<object>} The unprocessed issue snapshots.
                          */
                         function filterUnprocessedIssues(iterations) {
                           if (!Array.isArray(iterations)) return [];
                           return iterations.filter(function (issue) {
                             return issue && issue.handled_by_gnougo === false;
                           });
                         }
                       workflows:
                         main:
                           steps:
                             - id: source
                               type: set
                               output_schema:
                                 type: object
                                 properties:
                                   issues:
                                     type: array
                                     items:
                                       type: object
                                       properties:
                                         title:
                                           type: string
                                       required: [title]
                                       additionalProperties: false
                                 required: [issues]
                                 additionalProperties: false
                               input:
                                 issues:
                                   - title: First
                             - id: process_issues
                               type: loop.sequential
                               input:
                                 items: "${data.steps.source.issues}"
                               item_var: issue
                               steps:
                                 - id: build_issue_result
                                   type: set
                                   output_schema:
                                     type: object
                                     properties:
                                       title:
                                         type: string
                                       handled_by_gnougo:
                                         type: boolean
                                     required: [title, handled_by_gnougo]
                                     additionalProperties: false
                                   input:
                                     title: "${data.issue.title}"
                                     handled_by_gnougo: false
                             - id: filter_results
                               type: set
                               output_schema:
                                 type: object
                                 properties:
                                   issues_to_process:
                                     type: array
                                     items:
                                       type: object
                                       additionalProperties: true
                                 required: [issues_to_process]
                                 additionalProperties: false
                               input:
                                 issues_to_process: "${functions.filterUnprocessedIssues(data.steps.process_issues.results)}"
                       """
            });

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build an issue filtering workflow
                    prefilter: false
                  on_invalid:
                    action: stop
                    max_attempts: 1
        """);

        var result = await new WorkflowEngine { LLMClient = mockLlm.Object }
            .ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("LOOP_RESULTS_FUNCTION_FIELD_ACCESS", result.Error.Message);
        Assert.Contains("issue.handled_by_gnougo", result.Error.Message);
        Assert.Contains("iteration.build_issue_result", result.Error.Message);
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
          mode: basic
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
          mode: basic
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
    public async Task WorkflowPlan_SemanticValidation_RejectsSwitchStepDirectFieldMapping()
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
                               output_schema:
                                 type: object
                                 properties:
                                   classification:
                                     type: string
                                 required: [classification]
                                 additionalProperties: false
                               input:
                                 classification: bug
                             - id: compute_pr_result
                               type: switch
                               cases:
                                 - when: "${data.steps.classify.classification == 'bug'}"
                                   steps:
                                     - id: pr_success
                                       type: set
                                       output_schema:
                                         type: object
                                         properties:
                                           pr_url:
                                             type: string
                                         required: [pr_url]
                                         additionalProperties: false
                                       input:
                                         pr_url: "https://example.test/pull/1"
                               default:
                                 - id: pr_failure
                                   type: set
                                   output_schema:
                                     type: object
                                     properties:
                                       pr_url:
                                         type: string
                                     required: [pr_url]
                                     additionalProperties: false
                                   input:
                                     pr_url: "PR creation failed"
                           outputs:
                             pr_url:
                               expr: "${data.steps.compute_pr_result.pr_url}"
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
          mode: basic
          generator:
            model: gpt-4
            instruction: Build a PR workflow
");

        var engine = new WorkflowEngine { LLMClient = mockLlm.Object };

        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("STEP_OUTPUT_PROPERTY_UNKNOWN", result.Error.Message);
        Assert.Contains("data.steps.compute_pr_result.pr_url", result.Error.Message);
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
          mode: basic
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
        Assert.Contains("If a step has an `if`, later unconditional steps must not reference that step directly.", prompts[1]);
        Assert.Contains("Function arguments are evaluated before the function runs", prompts[1]);
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
          mode: basic
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
                Text = """
                       ```yaml
                       version: 1
                       name: generated-workflow
                       skill:
                         description: Generated workflow.
                         tags: [generated]
                         inputs: {}
                         outputs: {}
                       workflows:
                         main:
                           steps:
                             - id: s
                               type: template.render
                               input:
                                 engine: mustache
                                 template: ok
                                 mode: text
                       ```
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
          mode: basic
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
    public async Task WorkflowPlan_McpDiscovery_RetriesThreeTimesWithBackoff()
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
                       """
            });

        var session = new Mock<IMcpSession>();
        session.SetupGet(s => s.ServerName).Returns("docs");
        session.SetupSequence(s => s.ListToolsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient discovery failure 1"))
            .ThrowsAsync(new InvalidOperationException("transient discovery failure 2"))
            .ReturnsAsync(new List<McpToolInfo>
            {
                new() { Name = "get_doc", Description = "Get a document" }
            });
        session.Setup(s => s.ListPromptsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<McpPromptInfo>());

        var factory = new Mock<IMcpClientFactory>();
        factory.SetupGet(f => f.ServerMetadata).Returns(new List<McpServerMetadata>
        {
            new() { Name = "docs", Description = "Document operations" }
        });
        factory.Setup(f => f.GetClientAsync("docs", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);

        var wf = CompileMain("""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  generator:
                    model: gpt-4
                    instruction: Build a docs workflow
                    prefilter: false
        """);

        var result = await new WorkflowEngine
        {
            LLMClient = mockLlm.Object,
            McpClientFactory = factory.Object
        }.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        factory.Verify(f => f.GetClientAsync("docs", It.IsAny<CancellationToken>()), Times.Exactly(3));
        session.Verify(s => s.ListToolsAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
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
                Text = ValidGeneratedTemplateWorkflowYaml
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
                new()
                {
                    Name = "issue_read",
                    Description = "Read a GitHub issue or its comments.",
                    InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "method": { "type": "string", "enum": ["get", "get_comments"] },
                        "owner": { "type": "string" },
                        "repo": { "type": "string" },
                        "issue_number": { "type": "integer" },
                        "page": { "type": "integer" },
                        "perPage": { "type": "integer" },
                        "filters": {
                          "type": "object",
                          "properties": {
                            "labels": { "type": "array", "items": { "type": "string" } },
                            "include_reactions": { "type": "boolean" },
                            "window": {
                              "type": "object",
                              "properties": {
                                "created_after": { "type": "string", "format": "date-time" },
                                "max_age_days": { "type": "integer" }
                              }
                            }
                          }
                        }
                      },
                      "required": ["method", "owner", "repo", "issue_number", "page", "perPage"],
                      "additionalProperties": false
                    }
                    """)
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
           mode: basic
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
        Assert.Contains("capability_card_yaml:", capturedPrompt);
        Assert.Contains("tool: \"issue_read\"", capturedPrompt);
        Assert.Contains("purpose: \"Read a GitHub issue or its comments.\"", capturedPrompt);
        Assert.Contains("required_arguments:", capturedPrompt);
        Assert.Contains("issue_number: integer", capturedPrompt);
        Assert.Contains("optional_arguments:", capturedPrompt);
        Assert.Contains("filters: object { labels?: array<string>, include_reactions?: boolean, window?: object { created_after?: string(date-time), max_age_days?: integer } }", capturedPrompt);
        Assert.Contains("valid_values:", capturedPrompt);
        Assert.Contains("get_comments", capturedPrompt);
        Assert.Contains("call_issue_read_get", capturedPrompt);
        Assert.Contains("call_issue_read_get_comments", capturedPrompt);
        Assert.Contains("type: mcp.call", capturedPrompt);
        Assert.Contains("server: \"github\"", capturedPrompt);
        Assert.Contains("kind: tool", capturedPrompt);
        Assert.Contains("method: \"issue_read\"", capturedPrompt);
        Assert.Contains("owner: \"${data.inputs.owner}\"", capturedPrompt);
        Assert.Contains("page: \"${data.inputs.page}\"", capturedPrompt);
        Assert.Contains("perPage: \"${data.inputs.perPage}\"", capturedPrompt);
        Assert.Contains("Use input.method: issue_read for the MCP tool name; use input.request.method only as this tool's argument.", capturedPrompt);
        Assert.Contains("issue_number, page, perPage must resolve to numbers, not strings.", capturedPrompt);
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
        Assert.Contains("Prefer adapting each `capability_card_yaml` example when it matches the task", capturedPrompt);
        Assert.Contains("output_schema_json", capturedPrompt);
        Assert.Contains("\"repositories\"", capturedPrompt);
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
                Text = ValidGeneratedTemplateWorkflowYaml
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
           mode: basic
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
                    Text = ValidGeneratedTemplateWorkflowYaml
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
           mode: basic
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
                    Text = ValidGeneratedTemplateWorkflowYaml
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
           mode: basic
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
                         mode: basic
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
                    Text = ValidGeneratedTemplateWorkflowYaml
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
           mode: basic
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
        Assert.Contains("The root schema MUST be `type: object`", llmCallSnippet);
        Assert.Contains("Never use `type: any`", llmCallSnippet);
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
