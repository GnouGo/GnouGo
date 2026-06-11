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
                ["base_branch"] = "develop"
            }
        });
        var workflow = Compile(yaml);
        var engine = new WorkflowEngine
        {
            LLMClient = llm,
            LlmDefaults = new LlmRuntimeDefaults
            {
                Provider = "default-provider",
                Model = "default-model"
            }
        };

        var result = await engine.ExecuteAsync(workflow, new JsonObject { ["prompt"] = "compare this repo with develop" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("/tmp/repo vs develop: compare this repo with develop", result.Outputs!["answer"]!.GetValue<string>());
        var request = Assert.Single(llm.Requests);
        Assert.Equal("test-provider", request.Provider);
        Assert.Equal("test-model", request.Model);
        Assert.Equal(0.1, request.Temperature);
        Assert.Contains("repository_path", request.Prompt);
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

        public IWorkflowSpan WorkflowStart(WorkflowTelemetryInfo info)
        {
            var span = new TestSpan(info.WorkflowName, null);
            WorkflowSpans.Add(span);
            return span;
        }

        public IWorkflowSpan WorkflowStart(ITelemetrySpan parentSpan, WorkflowTelemetryInfo info)
        {
            var span = new TestSpan(info.WorkflowName, (parentSpan as TestSpan)?.Name);
            WorkflowSpans.Add(span);
            return span;
        }

        public void WorkflowEnd(IWorkflowSpan span, WorkflowResultInfo result) { }

        public IStepSpan StepStart(ITelemetrySpan parentSpan, StepTelemetryInfo info)
        {
            var span = new TestSpan(info.StepId, (parentSpan as TestSpan)?.Name);
            StepSpans.Add(span);
            return span;
        }

        public void StepEnd(IStepSpan span, StepResultInfo result) { }
    }

    private sealed class TestSpan(string name, string? parentName) : IWorkflowSpan, IStepSpan
    {
        public string Name { get; } = name;
        public string? ParentName { get; } = parentName;
        public void Dispose() { }
    }

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
