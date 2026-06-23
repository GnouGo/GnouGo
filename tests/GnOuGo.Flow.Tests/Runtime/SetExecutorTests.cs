using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class SetExecutorTests
{
    private static CompiledWorkflow CompileMain(string yaml)
    {
        var doc = WorkflowParser.Parse(yaml);
        var compiler = new WorkflowCompiler();
        var compiled = compiler.Compile(doc);
        return compiled.Workflows[compiled.Entrypoint!];
    }

    [Fact]
    public async Task Set_LiteralValues_StoresInOutput()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: vars
        type: set
        input:
          name: hello
          count: 42
          active: true
");
        var engine = new WorkflowEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        var output = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(output);
        Assert.Equal("hello", output!["name"]!.GetValue<string>());
        Assert.Equal(42, output["count"]!.GetValue<int>());
        Assert.True(output["active"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Set_Expressions_EvaluatesCorrectly()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    inputs:
      first: { type: string, required: true }
      last: { type: string, required: true }
      items: { type: array, required: true }
    steps:
      - id: vars
        type: set
        input:
          full_name: ""${data.inputs.first} ${data.inputs.last}""
          items_count: ""${len(data.inputs.items)}""
");
        var engine = new WorkflowEngine();
        var inputs = new JsonObject
        {
            ["first"] = "John",
            ["last"] = "Doe",
            ["items"] = new JsonArray(JsonValue.Create(1), JsonValue.Create(2), JsonValue.Create(3))
        };
        var result = await engine.ExecuteAsync(wf, inputs, CancellationToken.None);

        Assert.True(result.Success);
        var output = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(output);
        Assert.Equal("John Doe", output!["full_name"]!.GetValue<string>());
        Assert.Equal(3, output["items_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Set_ExpressionWithYamlDecodedNewlineInStringLiteral_EvaluatesCorrectly()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: row
        type: set
        input:
          data_row: ""${'a' + '\n'}""
    outputs:
      data_row: ""${data.steps.row.data_row}""
");
        var engine = new WorkflowEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("a\n", result.Outputs!["data_row"]!.GetValue<string>());
    }

    [Fact]
    public async Task Set_OutputAccessibleByNextStep()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    inputs:
      x: { type: number, required: true }
    steps:
      - id: init
        type: set
        input:
          doubled: ""${data.inputs.x * 2}""
          label: computed

      - id: use
        type: template.render
        input:
          engine: mustache
          template: ""Result: {{val}} ({{label}})""
          data:
            val: ""${data.steps.init.doubled}""
            label: ""${data.steps.init.label}""
          mode: text
    outputs:
      result: ""${data.steps.use.text}""
");
        var engine = new WorkflowEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject { ["x"] = 5 }, CancellationToken.None);

        Assert.True(result.Success);
        var outputs = result.Outputs as JsonObject;
        Assert.NotNull(outputs);
        Assert.Equal("Result: 10 (computed)", outputs!["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task Set_MultipleSteps_CanChain()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: a
        type: set
        input:
          x: 10

      - id: b
        type: set
        input:
          y: ""${data.steps.a.x + 5}""
          z: ""${data.steps.a.x * 2}""

    outputs:
      y: ""${data.steps.b.y}""
      z: ""${data.steps.b.z}""
");
        var engine = new WorkflowEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        var outputs = result.Outputs as JsonObject;
        Assert.NotNull(outputs);
        Assert.Equal(15, outputs!["y"]!.GetValue<int>());
        Assert.Equal(20, outputs["z"]!.GetValue<int>());
    }

    [Fact]
    public async Task Set_WithOutputAlias_ExposedAsTopLevelData()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: config
        type: set
        output: cfg
        input:
          api_url: ""https://api.example.com""
          timeout: 30

      - id: use
        type: template.render
        input:
          engine: mustache
          template: ""URL: {{url}}, Timeout: {{t}}""
          data:
            url: ""${data.cfg.api_url}""
            t: ""${data.cfg.timeout}""
          mode: text
    outputs:
      result: ""${data.steps.use.text}""
");
        var engine = new WorkflowEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        var outputs = result.Outputs as JsonObject;
        Assert.NotNull(outputs);
        Assert.Equal("URL: https://api.example.com, Timeout: 30", outputs!["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task Set_NestedObjects_PreservedCorrectly()
    {
        var wf = CompileMain(@"
version: 1
workflows:
  main:
    steps:
      - id: vars
        type: set
        input:
          config:
            host: localhost
            port: 8080
          tags:
            - alpha
            - beta
");
        var engine = new WorkflowEngine();
        var result = await engine.ExecuteAsync(wf, new JsonObject(), CancellationToken.None);

        Assert.True(result.Success);
        var output = result.StepResults[0].Output as JsonObject;
        Assert.NotNull(output);
        var config = output!["config"] as JsonObject;
        Assert.NotNull(config);
        Assert.Equal("localhost", config!["host"]!.GetValue<string>());
        Assert.Equal(8080, config["port"]!.GetValue<int>());
        var tags = output["tags"] as JsonArray;
        Assert.NotNull(tags);
        Assert.Equal(2, tags!.Count);
    }

    [Fact]
    public void Set_IsRegistered()
    {
        var engine = new WorkflowEngine();
        Assert.True(engine.Registry.Has("set"));
    }

    [Fact]
    public void Set_HasDslSnippet()
    {
        var engine = new WorkflowEngine();
        var executor = engine.Registry.Get("set");
        Assert.NotNull(executor);
        Assert.NotNull(executor!.DslSnippet);
        Assert.Contains("set", executor.DslSnippet);
    }
}

