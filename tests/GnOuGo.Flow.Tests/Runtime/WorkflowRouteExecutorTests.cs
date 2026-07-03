using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowRouteExecutorTests
{
    [Fact]
    public async Task WorkflowRoute_ExpandsDatabaseCandidatesAndCallsSelectedWorkflow()
    {
        var yaml = """
version: 1
workflows:
  main:
    inputs:
      prompt: { type: string, required: true }
    steps:
      - id: route
        type: workflow.route
        input:
          prompt: "${data.inputs.prompt}"
          candidates:
            - ref: { kind: database }
              tags_any: [git]
          selection:
            mode: single
            min: 1
            max: 1
          args:
            passthrough: true
          combine:
            strategy: first
    outputs:
      answer:
        expr: "${data.steps.route.answer}"
        type: string
""";

        var agentYaml = """
version: 1
skill:
  description: Inspects git repositories.
  tags: [git, code]
workflows:
  main:
    inputs:
      prompt: { type: string, required: true }
    steps:
      - id: render
        type: template.render
        input:
          engine: mustache
          mode: text
          template: "git answer: {{prompt}}"
          data:
            prompt: "${data.inputs.prompt}"
    outputs:
      answer:
        expr: "${data.steps.render.text}"
        type: string
""";

        var compiled = Compile(yaml);
        var agent = Compile(agentYaml);
        var provider = new FakeCandidateProvider(
            new WorkflowRouteCandidate
            {
                Id = "database:GitAgent",
                Name = "GitAgent",
                Ref = new JsonObject { ["kind"] = "database", ["agent"] = "GitAgent" },
                Description = "Inspects git repositories.",
                Tags = ["git", "code"]
            });

        var engine = new WorkflowEngine
        {
            LLMClient = new SelectingLlmClient("database:GitAgent"),
            WorkflowCandidateProvider = provider,
            WorkflowCallResolver = new FakeWorkflowCallResolver(("GitAgent", agent))
        };

        var result = await engine.ExecuteAsync(compiled, new JsonObject { ["prompt"] = "show diff" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("git answer: show diff", result.Outputs!["answer"]!.GetValue<string>());
        Assert.Single(provider.Queries);
        Assert.Equal("git", Assert.Single(provider.Queries[0].TagsAny));
    }

    [Fact]
    public async Task WorkflowRoute_UsesStaticFallbackWhenDynamicCatalogIsEmpty()
    {
        var yaml = """
version: 1
workflows:
  main:
    inputs:
      prompt: { type: string, required: true }
    steps:
      - id: route
        type: workflow.route
        input:
          prompt: "${data.inputs.prompt}"
          candidates:
            - ref: { kind: database }
            - ref: { kind: local, name: fallback }
              description: General fallback.
          combine:
            strategy: first
    outputs:
      answer:
        expr: "${data.steps.route.answer}"
        type: string
  fallback:
    inputs:
      prompt: { type: string, required: true }
    steps:
      - id: render
        type: template.render
        input:
          engine: mustache
          mode: text
          template: "fallback: {{prompt}}"
          data:
            prompt: "${data.inputs.prompt}"
    outputs:
      answer:
        expr: "${data.steps.render.text}"
        type: string
""";

        var workflow = Compile(yaml);
        var engine = new WorkflowEngine
        {
            WorkflowCandidateProvider = new FakeCandidateProvider(),
        };

        var result = await engine.ExecuteAsync(workflow, new JsonObject { ["prompt"] = "hello" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("fallback: hello", result.Outputs!["answer"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowRoute_AutoExtractsSelectedWorkflowArgumentsWithConfiguredModel()
    {
        var yaml = """
version: 1
workflows:
  main:
    inputs:
      prompt: { type: string, required: true }
    steps:
      - id: route
        type: workflow.route
        input:
          prompt: "${data.inputs.prompt}"
          candidates:
            - ref: { kind: local, name: inspect_repo }
              description: Inspects a git repository.
              inputs:
                type: object
                properties:
                  prompt: { type: string }
                  repository_path: { type: string }
                  base_branch: { type: string }
                  api_key: { type: string }
          args:
            passthrough: true
            auto_extract:
              provider: test-provider
              model: test-model
              temperature: 0.1
          combine:
            strategy: first
    outputs:
      answer:
        expr: "${data.steps.route.answer}"
        type: string
  inspect_repo:
    inputs:
      prompt: { type: string, required: true }
      repository_path: { type: string, required: true }
      base_branch: { type: string, required: false, default: main }
      api_key: { type: string, required: false }
    steps:
      - id: render
        type: template.render
        input:
          engine: mustache
          mode: text
          template: "{{repository_path}} vs {{base_branch}}: {{prompt}}"
          data:
            repository_path: "${data.inputs.repository_path}"
            base_branch: "${data.inputs.base_branch}"
            prompt: "${data.inputs.prompt}"
    outputs:
      answer:
        expr: "${data.steps.render.text}"
        type: string
""";

        var llm = new ExtractingLlmClient(new JsonObject
        {
            ["arguments"] = new JsonObject
            {
                ["repository_path"] = "/tmp/repo",
                ["base_branch"] = "develop",
                ["api_key"] = "secret-value",
                ["unexpected"] = "should be ignored"
            }
        });
        var telemetry = new RecordingTelemetry();
        var workflow = Compile(yaml);
        var engine = new WorkflowEngine
        {
            LLMClient = llm,
            Telemetry = telemetry,
            LlmDefaults = new LlmRuntimeDefaults
            {
                Provider = "default-provider",
                Model = "default-model"
            },
            Limits = new ExecutionLimits { LogStepContent = true }
        };

        var result = await engine.ExecuteAsync(
            workflow,
            new JsonObject
            {
                ["prompt"] = "compare this repo with develop",
                ["task"] = "compare this repo with develop",
                ["query"] = "compare this repo with develop",
                ["message"] = "compare this repo with develop"
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("/tmp/repo vs develop: compare this repo with develop", result.Outputs!["answer"]!.GetValue<string>());
        var request = Assert.Single(llm.Requests);
        Assert.Equal("test-provider", request.Provider);
        Assert.Equal("test-model", request.Model);
        Assert.Equal(0.1, request.Temperature);
        Assert.Contains("repository_path", request.Prompt);

        var routedInputs = Assert.Single(telemetry.Events, static evt => evt.Name == "gnougo-flow.workflow_route.inputs_extracted");
        Assert.Equal("local:inspect_repo", routedInputs.Attributes["gnougo-flow.workflow_route.candidate.id"]);
        Assert.Equal("inspect_repo", routedInputs.Attributes["gnougo-flow.workflow_route.workflow.name"]);
        Assert.Equal(true, routedInputs.Attributes["gnougo-flow.workflow_route.auto_extract.enabled"]);
        var argumentsJson = Assert.IsType<string>(routedInputs.Attributes["gnougo-flow.workflow_route.arguments"]);
        var arguments = JsonNode.Parse(argumentsJson)!.AsObject();
        Assert.Equal("/tmp/repo", arguments["repository_path"]!.GetValue<string>());
        Assert.Equal("<redacted>", arguments["api_key"]!.GetValue<string>());
        Assert.False(arguments.ContainsKey("unexpected"));
        Assert.False(arguments.ContainsKey("task"));
        Assert.False(arguments.ContainsKey("query"));
        Assert.False(arguments.ContainsKey("message"));
        var resolvedInputsJson = Assert.IsType<string>(routedInputs.Attributes["gnougo-flow.workflow_route.resolved_inputs"]);
        var resolvedInputs = JsonNode.Parse(resolvedInputsJson)!.AsObject();
        Assert.Equal("develop", resolvedInputs["base_branch"]!.GetValue<string>());
        Assert.False(resolvedInputs.ContainsKey("unexpected"));
        Assert.False(resolvedInputs.ContainsKey("task"));
        Assert.False(resolvedInputs.ContainsKey("query"));
        Assert.False(resolvedInputs.ContainsKey("message"));

        var thinking = Assert.Single(telemetry.Events, static evt => evt.Name == "gnougo-flow.step.thinking");
        Assert.Equal("progress", thinking.Attributes["gnougo-flow.thinking.level"]);
        Assert.Equal("workflow.route", thinking.Attributes["gnougo-flow.thinking.source"]);
        var message = Assert.IsType<string>(thinking.Attributes["gnougo-flow.thinking.message"]);
        Assert.Contains("Triggering workflow 'inspect_repo' with inputs", message);
        Assert.Contains("repository_path", message);
        Assert.DoesNotContain("secret-value", message);
        var thinkingInputsJson = Assert.IsType<string>(thinking.Attributes["gnougo-flow.workflow_route.resolved_inputs"]);
        var thinkingInputs = JsonNode.Parse(thinkingInputsJson)!.AsObject();
        Assert.Equal("<redacted>", thinkingInputs["api_key"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowRoute_UsesWorkflowInputsAsAuthoritativeAutoExtractTarget()
    {
        var yaml = """
version: 1
workflows:
  main:
    inputs:
      prompt: { type: string, required: true }
    steps:
      - id: route
        type: workflow.route
        input:
          prompt: "${data.inputs.prompt}"
          candidates:
            - ref: { kind: local, name: issue_resolver }
              description: Resolves GitHub issues.
              inputs:
                type: object
                properties:
                  task: { type: string }
                  repo_url: { type: string, default: "https://github.com/AxaFrance/oidc-client" }
                  max_issues: { type: string, default: "4" }
          args:
            passthrough: false
            auto_extract: true
          combine:
            strategy: first
    outputs:
      answer:
        expr: "${data.steps.route.answer}"
        type: string
  issue_resolver:
    inputs:
      task: { type: string, required: true }
      repo_url: { type: string, required: true }
      max_issues: { type: string, required: false, default: "4" }
    steps:
      - id: render
        type: template.render
        input:
          engine: mustache
          mode: text
          template: "{{max_issues}} issues from {{repo_url}} for {{task}}"
          data:
            task: "${data.inputs.task}"
            repo_url: "${data.inputs.repo_url}"
            max_issues: "${data.inputs.max_issues}"
    outputs:
      answer:
        expr: "${data.steps.render.text}"
        type: string
""";

        var llm = new ExtractingLlmClient(new JsonObject
        {
            ["arguments"] = new JsonObject
            {
                ["task"] = "Resolve open issues",
                ["repo_url"] = "https://github.com/AxaFrance/axa-fr-oidc",
                ["max_issues"] = "20"
            }
        });
        var workflow = Compile(yaml);
        var engine = new WorkflowEngine { LLMClient = llm };

        var result = await engine.ExecuteAsync(
            workflow,
            new JsonObject { ["prompt"] = "Resolve the first 20 issues on https://github.com/AxaFrance/axa-fr-oidc/" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(
            "20 issues from https://github.com/AxaFrance/axa-fr-oidc for Resolve open issues",
            result.Outputs!["answer"]!.GetValue<string>());
        var request = Assert.Single(llm.Requests);
        Assert.Contains("repo_url", request.Prompt);
        Assert.Contains("max_issues", request.Prompt);
    }

    [Fact]
    public async Task WorkflowRoute_ValidatesAutoExtractedArgumentsBeforeExecutingSelectedWorkflow()
    {
        var yaml = """
version: 1
workflows:
  main:
    inputs:
      prompt: { type: string, required: true }
    steps:
      - id: route
        type: workflow.route
        input:
          prompt: "${data.inputs.prompt}"
          candidates:
            - ref: { kind: local, name: count_items }
              description: Counts items.
              inputs:
                type: object
                properties:
                  prompt: { type: string }
                  count: { type: integer }
          args:
            passthrough: true
            auto_extract: true
          combine:
            strategy: first
  count_items:
    inputs:
      prompt: { type: string, required: true }
      count: { type: integer, required: true }
    steps:
      - id: render
        type: template.render
        input:
          engine: mustache
          mode: text
          template: "{{count}}: {{prompt}}"
          data:
            count: "${data.inputs.count}"
            prompt: "${data.inputs.prompt}"
    outputs:
      answer:
        expr: "${data.steps.render.text}"
        type: string
""";

        var llm = new ExtractingLlmClient(new JsonObject
        {
            ["arguments"] = new JsonObject
            {
                ["count"] = "five"
            }
        });
        var workflow = Compile(yaml);
        var engine = new WorkflowEngine { LLMClient = llm };

        var result = await engine.ExecuteAsync(workflow, new JsonObject { ["prompt"] = "count five items" }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Contains("Input validation failed for routed workflow 'count_items'", result.Error.Message);
        Assert.Contains("'count': expected integer, got string", result.Error.Message);
        Assert.Equal("count_items", result.Error.Details!["workflow"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowRoute_UsesDistinctRunIdsForParallelHumanInputCandidates()
    {
        var yaml = """
version: 1
workflows:
  main:
    inputs:
      prompt: { type: string, required: true }
    steps:
      - id: route
        type: workflow.route
        input:
          prompt: "${data.inputs.prompt}"
          candidates:
            - ref: { kind: local, name: first }
              description: First interactive workflow.
            - ref: { kind: local, name: second }
              description: Second interactive workflow.
          selection:
            mode: multiple
            min: 2
            max: 2
          execution:
            parallel: true
            max_concurrency: 2
          combine:
            strategy: raw
  first:
    steps:
      - id: ask_user
        type: human.input
        input:
          mode: text
          prompt: First question?
    outputs:
      answer:
        expr: "${data.steps.ask_user.response}"
        type: string
  second:
    steps:
      - id: ask_user
        type: human.input
        input:
          mode: text
          prompt: Second question?
    outputs:
      answer:
        expr: "${data.steps.ask_user.response}"
        type: string
""";

        var humanInput = new CapturingHumanInputProvider();
        var workflow = Compile(yaml);
        var engine = new WorkflowEngine
        {
            LLMClient = new SelectingLlmClient("local:first", "local:second"),
            HumanInputProvider = humanInput,
            Limits = new ExecutionLimits { RunId = "parent-run" }
        };

        var result = await engine.ExecuteAsync(workflow, new JsonObject { ["prompt"] = "ask both" }, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, humanInput.Requests.Count);
        Assert.All(humanInput.Requests, static request => Assert.Equal("ask_user", request.StepId));
        Assert.All(humanInput.Requests, static request => Assert.StartsWith("parent-run:route:", request.RunId, StringComparison.Ordinal));
        Assert.Equal(2, humanInput.Requests.Select(static request => request.RunId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task WorkflowRoute_ExecutesSelectedWorkflowUnderRouteTelemetrySpan()
    {
        var yaml = """
version: 1
workflows:
  main:
    inputs:
      prompt: { type: string, required: true }
    steps:
      - id: route
        type: workflow.route
        input:
          prompt: "${data.inputs.prompt}"
          candidates:
            - ref: { kind: local, name: child }
          selection:
            mode: single
            min: 1
            max: 1
          combine:
            strategy: first
  child:
    inputs:
      prompt: { type: string, required: true }
    steps:
      - id: render
        type: template.render
        input:
          engine: mustache
          mode: text
          template: "{{prompt}}"
          data:
            prompt: "${data.inputs.prompt}"
    outputs:
      answer:
        expr: "${data.steps.render.text}"
        type: string
""";

        var telemetry = new RecordingTelemetry();
        var workflow = Compile(yaml);
        var engine = new WorkflowEngine { Telemetry = telemetry };

        var result = await engine.ExecuteAsync(workflow, new JsonObject { ["prompt"] = "hello" }, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var childWorkflow = Assert.Single(telemetry.WorkflowSpans, static span => span.Name == "child");
        Assert.Equal("route", childWorkflow.ParentName);
        var childStep = Assert.Single(telemetry.StepSpans, static span => span.Name == "render");
        Assert.Equal("child", childStep.ParentName);
    }

    private static CompiledWorkflow Compile(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        var compiled = new WorkflowCompiler().Compile(doc);
        return compiled.Workflows[compiled.Entrypoint!];
    }

    private sealed class SelectingLlmClient : ILLMClient
    {
        private readonly IReadOnlyList<string> _selectedIds;

        public SelectingLlmClient(params string[] selectedIds)
        {
            _selectedIds = selectedIds;
        }

        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
            => Task.FromResult(new LLMResponse
            {
                Json = new JsonObject
                {
                    ["selected"] = new JsonArray(_selectedIds
                        .Select(static selectedId => (JsonNode)new JsonObject
                        {
                            ["id"] = selectedId,
                            ["reason"] = "matches",
                            ["confidence"] = 0.9
                        })
                        .ToArray())
                }
            });
    }

    private sealed class CapturingHumanInputProvider : IHumanInputProvider
    {
        private readonly object _gate = new();

        public List<HumanInputRequest> Requests { get; } = new();

        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            lock (_gate)
            {
                Requests.Add(request);
            }

            return Task.FromResult<JsonNode?>(new JsonObject { ["response"] = request.Prompt });
        }
    }

    private sealed class RecordingTelemetry : IWorkflowTelemetry
    {
        public List<TestSpan> WorkflowSpans { get; } = new();
        public List<TestSpan> StepSpans { get; } = new();
        public List<TestTelemetryEvent> Events { get; } = new();

        public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info)
        {
            var span = new TestSpan(info.WorkflowName, null, Events);
            WorkflowSpans.Add(span);
            return span;
        }

        public IWorkflowSpan WorkflowStart(ITelemetrySpan parentSpan, WorkflowTelemetryInfo info)
        {
            var span = new TestSpan(info.WorkflowName, (parentSpan as TestSpan)?.Name, Events);
            WorkflowSpans.Add(span);
            return span;
        }

        public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result) { }

        public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info)
        {
            var span = new TestSpan(info.StepId, (parentSpan as TestSpan)?.Name, Events);
            StepSpans.Add(span);
            return span;
        }

        public void StepEnd(IStepSpan span, StepResultInfo result) { }
    }

    private sealed class TestSpan(string name, string? parentName, List<TestTelemetryEvent> events) : IWorkflowSpan, IStepSpan
    {
        public string Name { get; } = name;
        public string? ParentName { get; } = parentName;

        public void AddEvent(string name, IReadOnlyList<KeyValuePair<string, object?>>? attributes = null)
            => events.Add(new TestTelemetryEvent(
                name,
                attributes?.ToDictionary(static kv => kv.Key, static kv => kv.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal)));

        public void Dispose() { }
    }

    private sealed record TestTelemetryEvent(string Name, IReadOnlyDictionary<string, object?> Attributes);

    private sealed class ExtractingLlmClient : ILLMClient
    {
        private readonly JsonObject _response;

        public ExtractingLlmClient(JsonObject response)
        {
            _response = response;
        }

        public List<LLMRequest> Requests { get; } = new();

        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new LLMResponse
            {
                Json = _response.DeepClone()
            });
        }
    }

    private sealed class FakeCandidateProvider : IWorkflowCandidateProvider
    {
        private readonly IReadOnlyList<WorkflowRouteCandidate> _candidates;

        public FakeCandidateProvider(params WorkflowRouteCandidate[] candidates)
        {
            _candidates = candidates;
        }

        public List<WorkflowRouteCandidateQuery> Queries { get; } = new();

        public Task<IReadOnlyList<WorkflowRouteCandidate>> GetCandidatesAsync(
            WorkflowRouteCandidateQuery query,
            CancellationToken ct)
        {
            Queries.Add(query);
            return Task.FromResult(_candidates);
        }
    }

    private sealed class FakeWorkflowCallResolver : DefaultWorkflowCallResolver
    {
        private readonly Dictionary<string, CompiledWorkflow> _databaseWorkflows;

        public FakeWorkflowCallResolver(params (string Name, CompiledWorkflow Workflow)[] workflows)
        {
            _databaseWorkflows = workflows.ToDictionary(static item => item.Name, static item => item.Workflow, StringComparer.OrdinalIgnoreCase);
        }

        public override Task<WorkflowCallResolution> ResolveAsync(WorkflowCallResolutionContext context, CancellationToken ct)
        {
            if (string.Equals(context.Kind, "database", StringComparison.OrdinalIgnoreCase))
            {
                var agent = context.Ref["agent"]?.GetValue<string>() ?? "";
                return Task.FromResult(new WorkflowCallResolution
                {
                    Workflow = _databaseWorkflows[agent],
                    WorkflowName = agent,
                    CallStackKey = $"database:{agent}"
                });
            }

            return base.ResolveAsync(context, ct);
        }
    }
}
