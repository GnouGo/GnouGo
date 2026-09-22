using System.Text.Json.Nodes;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Moq;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Runtime.Executors;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowRuntimeValidationTests
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
    public void HumanInput_DslSnippet_ContainsPlannerContract()
    {
        var snippet = new HumanInputExecutor().DslSnippet!;

        Assert.Contains("Always set `input.mode` explicitly", snippet);
        Assert.Contains("Valid modes: text, choice, form, confirm.", snippet);
        Assert.Contains("Mode selection priority", snippet);
        Assert.Contains("`choice`: MUST use whenever the user must select exactly one answer", snippet);
        Assert.Contains("`text`: ONLY use for genuinely open-ended answers", snippet);
        Assert.Contains("Never place possible choices/options/answers only in `prompt` or `context`.", snippet);
        Assert.Contains("Dynamic questionnaire choices must still be emitted as an actual YAML array", snippet);
        Assert.Contains("\"${data.question_item.options[0]}\"", snippet);
        Assert.Contains("Invalid anti-pattern — never generate", snippet);
        Assert.Contains("prompt: \"Choices: ${json(data.question_item.options)}\"", snippet);
        Assert.Contains("date", snippet);
        Assert.Contains("mode: confirm", snippet);
        Assert.Contains("A `confirm` response is always boolean", snippet);
        Assert.Contains("Never compare a `confirm` response to a choice label", snippet);
        Assert.Contains("confirm: `data.steps.<id>.response` (boolean)", snippet);
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
    public void WorkflowPlan_DryRun_RecognizesHumanizedSnakeCaseInputInValidationDiagnostic()
    {
        var validatorType = typeof(WorkflowEngine).Assembly.GetType(
            "GnOuGo.Flow.Core.Runtime.WorkflowPlanDryRunValidator",
            throwOnError: true)!;
        var method = validatorType.GetMethod(
            "IsInconclusiveSyntheticInputValidation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var inconclusive = (bool)method.Invoke(
            null,
            new object?[]
            {
                ErrorCodes.ScriptError,
                "Invalid pull-request URL: expected an absolute URL.",
                new Dictionary<string, InputDef>
                {
                    ["pull_request_url"] = new() { Type = "string" },
                    ["instructions"] = new() { Type = "string" }
                }
            })!;

        Assert.True(inconclusive);
    }

    [Fact]
    public void WorkflowPlan_DryRun_UsesProviderNeutralUrlSample()
    {
        var validatorType = typeof(WorkflowEngine).Assembly.GetType(
            "GnOuGo.Flow.Core.Runtime.WorkflowPlanDryRunValidator",
            throwOnError: true)!;
        var method = validatorType.GetMethod(
            "BuildSampleInputs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var inputs = new Dictionary<string, InputDef>
        {
            ["resource_url"] = new()
            {
                Type = "url",
                Description = "An absolute resource URL."
            }
        };

        var sample = Assert.IsType<JsonObject>(method.Invoke(null, [inputs]));

        Assert.Equal(
            "https://example.invalid/dry-run",
            sample["resource_url"]!.GetValue<string>());
    }

    [Fact]
    public void WorkflowPlan_DryRun_UsesFirstDeclaredEnumValue()
    {
        var validatorType = typeof(WorkflowEngine).Assembly.GetType(
            "GnOuGo.Flow.Core.Runtime.WorkflowPlanDryRunValidator",
            throwOnError: true)!;
        var method = validatorType.GetMethod(
            "BuildSampleInputs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var inputs = new Dictionary<string, InputDef>
        {
            ["mode"] = new()
            {
                Type = "string",
                Enum = ["first", "second"]
            }
        };

        var sample = Assert.IsType<JsonObject>(method.Invoke(null, [inputs]));

        Assert.Equal("first", sample["mode"]!.GetValue<string>());
    }

    [Fact]
    public void WorkflowPlan_Diagnostics_ExplainsThatOrdinaryMcpCallsHaveNoJsonOutput()
    {
        var semanticError = new WorkflowSemanticValidationError
        {
            Code = "STEP_OUTPUT_PROPERTY_UNKNOWN",
            WorkflowName = "discover_projects",
            Field = "outputs.modified_projects",
            InvalidPath = "data.steps.call_cmd_run.json.modified_projects",
            AllowedPaths =
            [
                "data.steps.call_cmd_run.response.stdout",
                "data.steps.call_cmd_run.response.success"
            ],
            Message = "Property 'json' is not defined by the output schema."
        };

        var details = WorkflowPlanDiagnostics.BuildValidationFailureDetails(
            [],
            new WorkflowSemanticValidationException([semanticError]),
            compilationException: null);
        var diagnostic = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(details["diagnostics"])[0]);

        Assert.Contains(
            "ordinary mcp.call",
            diagnostic["llm_guidance"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Contains(
            "separate llm.call with strict structured_output",
            diagnostic["llm_guidance"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowRuntime_ReportsFailingStepForExpressionErrors()
    {
        var workflow = CompileMain("""
            version: 1
            workflows:
              main:
                steps:
                  - id: project_missing_value
                    type: set
                    input:
                      id: ${data.inputs.missing.id}
            """);

        var result = await new WorkflowEngine().ExecuteAsync(workflow, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.EvalError, result.Error!.Code);
        Assert.Equal("project_missing_value", result.Error.Details!["failed_step_id"]!.GetValue<string>());
        Assert.Equal("step", result.Error.Details["execution_phase"]!.GetValue<string>());
        Assert.Contains("step 'project_missing_value'", result.Error.Message, StringComparison.Ordinal);

        var dryRunDetails = WorkflowPlanDiagnostics.BuildDryRunFailureDetails(
            result.Error.Code,
            result.Error.Message,
            "execution",
            runtimeDetails: result.Error.Details);
        var diagnostic = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(dryRunDetails["diagnostics"])[0]);
        Assert.Equal("workflow:main/step:project_missing_value", diagnostic["location"]!.GetValue<string>());
        Assert.Contains("Repair step 'project_missing_value'", diagnostic["llm_guidance"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowRuntime_ReportsFailingFinalizerStepForExpressionErrors()
    {
        var workflow = CompileMain("""
            version: 1
            workflows:
              main:
                steps:
                  - id: prepare
                    type: set
                    input: { ready: true }
                finally:
                  - id: cleanup_created_directories
                    type: set
                    input:
                      directories: ${data.steps.missing.created_directories}
            """);

        var result = await new WorkflowEngine().ExecuteAsync(workflow, new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        var finalizationError = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(result.Error!.Details!["finalization_errors"])[0]);
        Assert.Equal(
            "cleanup_created_directories",
            finalizationError["details"]!["failed_step_id"]!.GetValue<string>());
        Assert.Equal("finalization", finalizationError["details"]!["execution_phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowPlan_DryRun_AllowsRepresentativeBoundedFinalizationLoop()
    {
        var validatorType = typeof(WorkflowEngine).Assembly.GetType(
            "GnOuGo.Flow.Core.Runtime.WorkflowPlanDryRunValidator",
            throwOnError: true)!;
        var method = validatorType.GetMethod(
            "ValidateAsync",
            BindingFlags.Public | BindingFlags.Static)!;
        var document = WorkflowParser.Parse("""
            version: 1
            skill:
              description: Clean up all workflow-owned directories.
              tags: [cleanup]
              inputs: {}
              outputs: {}
            workflows:
              main:
                steps:
                  - id: prepare
                    type: set
                    input:
                      value: ready
                finally:
                  - id: cleanup_directories
                    type: loop.sequential
                    input:
                      items: [checkout, dependencies, tests, review]
                    item_var: directory
                    steps:
                      - id: record_cleanup
                        type: set
                        input:
                          directory: "${data._loop.directory}"
            """);

        var validation = Assert.IsAssignableFrom<Task>(method.Invoke(
            null,
            [document, null, null, CancellationToken.None]));

        await validation;
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
}
