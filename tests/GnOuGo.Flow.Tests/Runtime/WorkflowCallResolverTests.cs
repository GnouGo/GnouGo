using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowCallResolverTests
{
    [Fact]
    public async Task WorkflowCall_UsesInjectedResolver()
    {
        var compiled = CompileDoc("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: workflow.call
        input:
          ref: { kind: custom, name: anything }
    outputs:
      value: "${data.steps.call.outputs.value}"
""");

        var engine = new WorkflowEngine
        {
            WorkflowCallResolver = new InlineWorkflowCallResolver("""
version: 1
workflows:
  generated:
    steps:
      - id: out
        type: set
        input: { value: 123 }
    outputs:
      value: "${data.steps.out.value}"
""")
        };

        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(123, result.Outputs!["value"]!.GetValue<int>());
    }

    [Fact]
    public async Task WorkflowCall_Workspace_ResolvesRelativePathInsideWorkspace()
    {
        var workspaceRoot = CreateTempDirectory();
        var workflowsDir = Directory.CreateDirectory(Path.Combine(workspaceRoot, "workflows"));
        await File.WriteAllTextAsync(Path.Combine(workflowsDir.FullName, "helper.yaml"), """
version: 1
workflows:
  helper:
    steps:
      - id: out
        type: set
        input: { value: "from workspace" }
    outputs:
      value: "${data.steps.out.value}"
""");

        var compiled = CompileDoc("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: workflow.call
        input:
          ref: { kind: workspace, path: workflows/helper.yaml }
    outputs:
      value: "${data.steps.call.outputs.value}"
""");

        var engine = new WorkflowEngine { WorkflowCallResolver = new DefaultWorkflowCallResolver(workspaceRoot) };
        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("from workspace", result.Outputs!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowCall_Workspace_ResolvesDotPathRelativeToCurrentWorkspaceSource()
    {
        var workspaceRoot = CreateTempDirectory();
        var parentDir = Directory.CreateDirectory(Path.Combine(workspaceRoot, "agents", "writer"));
        await File.WriteAllTextAsync(Path.Combine(parentDir.FullName, "helper.yaml"), """
version: 1
workflows:
  helper:
    steps:
      - id: out
        type: set
        input: { value: "from source directory" }
    outputs:
      value: "${data.steps.out.value}"
""");

        var compiled = CompileDoc("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: workflow.call
        input:
          ref: { path: ./helper.yaml }
    outputs:
      value: "${data.steps.call.outputs.value}"
""", sourceKind: "workspace", sourcePath: "agents/writer/workflow.yaml");

        var engine = new WorkflowEngine { WorkflowCallResolver = new DefaultWorkflowCallResolver(workspaceRoot) };
        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("from source directory", result.Outputs!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowCall_Workspace_DotPathFallsBackToWorkspaceRootBundlePath()
    {
        var workspaceRoot = CreateTempDirectory();
        var bundleDir = Directory.CreateDirectory(Path.Combine(workspaceRoot, "Test2"));
        await File.WriteAllTextAsync(Path.Combine(bundleDir.FullName, "answer.yaml"), """
version: 1
workflows:
  helper:
    steps:
      - id: out
        type: set
        input: { value: "from bundle root" }
    outputs:
      value: "${data.steps.out.value}"
""");

        var compiled = CompileDoc("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: workflow.call
        input:
          ref: { kind: workspace, path: ./Test2/answer.yaml }
    outputs:
      value: "${data.steps.call.outputs.value}"
""", sourceKind: "workspace", sourcePath: "Test2/workflow.yaml");

        var engine = new WorkflowEngine { WorkflowCallResolver = new DefaultWorkflowCallResolver(workspaceRoot) };
        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("from bundle root", result.Outputs!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowCall_Workspace_RejectsAbsolutePath()
    {
        var workspaceRoot = CreateTempDirectory();
        var absolutePath = Path.Combine(workspaceRoot, "helper.yaml").Replace("\\", "\\\\");
        var compiled = CompileDoc($$"""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: workflow.call
        input:
          ref: { kind: workspace, path: "{{absolutePath}}" }
""");

        var engine = new WorkflowEngine { WorkflowCallResolver = new DefaultWorkflowCallResolver(workspaceRoot) };
        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowFetchPolicy, result.Error!.Code);
        Assert.Contains("relative", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkflowCall_Workspace_RejectsPathTraversal()
    {
        var workspaceRoot = CreateTempDirectory();
        var compiled = CompileDoc("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: workflow.call
        input:
          ref: { kind: workspace, path: ../outside.yaml }
""");

        var engine = new WorkflowEngine { WorkflowCallResolver = new DefaultWorkflowCallResolver(workspaceRoot) };
        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowFetchPolicy, result.Error!.Code);
        Assert.Contains("traversal", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkflowCall_Url_UsesConstructorAllowedHostnames()
    {
        var compiled = CompileDoc("""
version: 1
workflows:
  main:
    steps:
      - id: call
        type: workflow.call
        input:
          ref: { kind: url, url: "https://evil.example/wf.yaml" }
""");

        var engine = new WorkflowEngine
        {
            WorkflowFetcher = new StaticFetcher("version: 1\nworkflows: { main: { steps: [] } }"),
            WorkflowCallResolver = new DefaultWorkflowCallResolver(allowedHostnames: ["good.example"])
        };

        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowFetchPolicy, result.Error!.Code);
        Assert.Contains("allow-list", result.Error.Message);
    }

    private static CompiledDocument CompileDoc(string yaml, string? sourceKind = null, string? sourcePath = null)
    {
        var doc = WorkflowParser.Parse(yaml);
        doc.SourceKind = sourceKind;
        doc.SourcePath = sourcePath;
        return new WorkflowCompiler().Compile(doc);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "gnougo-flow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StaticFetcher : IWorkflowFetcher
    {
        private readonly string _yaml;

        public StaticFetcher(string yaml) => _yaml = yaml;

        public Task<string> FetchAsync(string url, string? integrity, CancellationToken ct) => Task.FromResult(_yaml);
    }

    private sealed class InlineWorkflowCallResolver : IWorkflowCallResolver
    {
        private readonly string _yaml;

        public InlineWorkflowCallResolver(string yaml) => _yaml = yaml;

        public Task<WorkflowCallResolution> ResolveAsync(WorkflowCallResolutionContext context, CancellationToken ct)
        {
            var compiled = CompileDoc(_yaml);
            var workflow = compiled.Workflows[compiled.Entrypoint!];
            return Task.FromResult(new WorkflowCallResolution
            {
                Workflow = workflow,
                WorkflowName = workflow.Name,
                CallStackKey = "custom:inline"
            });
        }
    }
}
