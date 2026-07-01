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
        Assert.Contains("For path-like MCP request fields, follow the MCP schema and tool description exactly", capturedPrompt);
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
        Assert.Equal(6, requests.Count);
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
        Assert.Equal(5, requests.Count);
        Assert.Contains("Correct spelling and grammar.", requests[0].Prompt);
        Assert.Contains(":::subworkflow name=\"snake_case_name\"", requests[1].Prompt);
        Assert.Null(requests[1].StructuredOutputSchema);
        Assert.Null(requests[1].StructuredOutputStrict);
        Assert.Contains("Return ONLY annotated Markdown.", requests[1].Prompt);
        Assert.DoesNotContain("Return ONLY JSON matching the requested structured output schema.", requests[1].Prompt);
        Assert.Contains("Avoid blocks with high cyclomatic complexity", requests[1].Prompt);
        Assert.Contains("Do not create one large block that mixes several responsibilities", requests[1].Prompt);
        var collectRequest = Assert.Single(requests, request => request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `collect_data`.", StringComparison.Ordinal));
        var reportRequest = Assert.Single(requests, request => request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `generate_report`.", StringComparison.Ordinal));
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
        Assert.Contains("collect_data_outputs: ${data.steps.collect_records_step.outputs}", yaml);
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
                                type: set
                                input:
                                  sent: true
                            outputs:
                              sent: "${data.steps.send.sent}"
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
                                type: set
                                input:
                                  issues: []
                            outputs:
                              issues:
                                expr: "${data.steps.result.issues}"
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
                                        ["properties"] = new JsonArray()
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
                                type: set
                                input:
                                  issues: []
                            outputs:
                              issues:
                                expr: "${data.steps.result.issues}"
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

        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("query: ${data.inputs.query}", yaml);
        Assert.DoesNotContain("undeclared_query", yaml);
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
    public async Task WorkflowPlan_PipelineMode_MainAssemblyReceivesLeafResourceLinks()
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
        Assert.Contains("<leaf_resource_links_yaml>", mainAssemblyPrompt);
        Assert.Contains("resource_producers", mainAssemblyPrompt);
        Assert.Contains("resource_consumers", mainAssemblyPrompt);
        Assert.Contains("resource_links", mainAssemblyPrompt);
        Assert.Contains("clone_repository_for_issue", mainAssemblyPrompt);
        Assert.Contains("classify_issue", mainAssemblyPrompt);
        Assert.Contains("${data.steps.<producer_call_id>.outputs.project_root_relative}", mainAssemblyPrompt);
        Assert.Contains("existing_workspace_relative_path", mainAssemblyPrompt);
    }

    [Fact]
    public async Task WorkflowPlan_PipelineMode_FailsBeforeMainAssemblyWhenResourceConsumerHasOnlyNearMissProducer()
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
                    return new LLMResponse { Text = "document: {}\ngraph:\n  steps: []" };
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

        Assert.False(result.Success);
        Assert.False(mainAssemblyCalled);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("Pipeline leaf resource contracts are inconsistent", result.Error.Message);
        Assert.Contains("classify_issue.project_root_relative", result.Error.Message);
        Assert.Contains("near_miss_producers", result.Error.Details!.ToJsonString());
        Assert.Contains("clone_repository_for_issue", result.Error.Details!.ToJsonString());
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
                                type: set
                                input:
                                  value:
                                    name: "${data.inputs.name}"
                            outputs:
                              profile:
                                expr: "${data.steps.profile.value}"
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
                                type: set
                                input:
                                  records: ["one"]
                            outputs:
                              records: "${data.steps.collect.records}"
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
                                type: set
                                input:
                                  value:
                                    name: "${data.inputs.name}"
                            outputs:
                              profile:
                                expr: "${data.steps.profile.value}"
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
                                type: set
                                input:
                                  value:
                                    name: "${data.inputs.name}"
                            outputs:
                              profile:
                                expr: "${data.steps.profile.value}"
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
        Assert.Equal(5, requests.Count);
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
    public async Task WorkflowPlan_PipelineMode_RegeneratesLeafThatLeaksGeneratedTargetPathInput()
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
                            - id: call_clone_repository_for_issue
                              leaf: clone_repository_for_issue
                              args:
                                repo_url: ${data.inputs.repo_url}
                                issue_number: ${data.inputs.issue_number}
                                working_directory_base: ${data.steps.derive_working_directory_base.working_directory_base}
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
        Assert.Contains(requests, request =>
            request.Prompt.Contains("Previous generated YAML for this leaf workflow failed validation", StringComparison.Ordinal)
            && request.Prompt.Contains("generated implementation input", StringComparison.Ordinal)
            && request.Prompt.Contains("targetDirectory", StringComparison.Ordinal)
            && request.Prompt.Contains("derive", StringComparison.OrdinalIgnoreCase));

        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Contains("targetDirectory: \"${data.inputs.working_directory_base}/issue-${toString(data.inputs.issue_number)}\"", yaml);
        Assert.DoesNotContain("targetDirectory:\n        type: string\n        required: true", yaml);
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
    public async Task WorkflowPlan_SemanticValidation_RequiresExplicitNumericPerPageWhenAdvertised()
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

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUEST_SCHEMA_INVALID", result.Error.Message);
        Assert.Contains("input.request.perPage", result.Error.Message);
        Assert.Contains("missing explicit numeric pagination property", result.Error.Message);
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
    public async Task WorkflowPlan_SemanticValidation_RejectsAbsoluteMcpWorkspacePathLiteral()
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

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUEST_SCHEMA_INVALID", result.Error.Message);
        Assert.Contains("input.request.workspacePath", result.Error.Message);
        Assert.Contains("must be relative", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsDiagnosticAbsolutePathOutputInMcpPathField()
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

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("rootPath", result.Error.Message);
        Assert.Contains("sibling field documented as relative", result.Error.Message);
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
        Assert.Contains("MCP_REQUEST_SCHEMA_INVALID", result.Error.Message);
        Assert.Contains("input.request.workspacePath", result.Error.Message);
        Assert.Contains("empty string is invalid", result.Error.Message);
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
    public async Task WorkflowPlan_SemanticValidation_RejectsWorkspacePathOutputWithEmptyFallback()
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

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("WORKFLOW_PATH_OUTPUT_EMPTY_FALLBACK", result.Error.Message);
        Assert.Contains("outputs.local_workspace_path", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsMcpWorkspacePathExpressionWithEmptyFallback()
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

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUEST_SCHEMA_INVALID", result.Error.Message);
        Assert.Contains("input.request.workspacePath", result.Error.Message);
        Assert.Contains("can resolve to an empty string", result.Error.Message);
    }

    [Fact]
    public async Task WorkflowPlan_SemanticValidation_RejectsInventedExistingWorkspacePathForMcpRequest()
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

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUEST_RESOURCE_PROVENANCE", result.Error.Message);
        Assert.Contains("input.request.workspacePath", result.Error.Message);
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
    public async Task WorkflowPlan_SemanticValidation_RejectsInventedExistingWorkspacePathForLocalWorkflowCall()
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

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("WORKFLOW_CALL_RESOURCE_PROVENANCE", result.Error.Message);
        Assert.Contains("input.args.local_workspace_path", result.Error.Message);
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
        Assert.Contains("page: 1", capturedPrompt);
        Assert.Contains("perPage: 30", capturedPrompt);
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
