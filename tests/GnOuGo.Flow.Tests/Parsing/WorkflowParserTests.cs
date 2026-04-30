using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using Xunit;

namespace GnOuGo.Flow.Tests.Parsing;

public class WorkflowParserTests
{
    [Fact]
    public void Parse_MinimalDocument_ReturnsDocument()
    {
        var yaml = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n        input:\n          engine: mustache\n          template: hello\n          mode: text\n";
        var doc = WorkflowParser.Parse(yaml);
        Assert.Equal(1, doc.Version);
        Assert.Single(doc.Workflows);
        Assert.True(doc.Workflows.ContainsKey("main"));
    }

    [Fact]
    public void Parse_WithLegacyDslField_ThrowsMissingVersion()
    {
        var yaml = "dsl: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n";
        Assert.Throws<WorkflowParseException>(() => WorkflowParser.Parse(yaml));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    public void Parse_WithVersionStringOne_ReturnsDocument(string version)
    {
        var yaml = $"version: \"{version}\"\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n";
        var doc = WorkflowParser.Parse(yaml);
        Assert.Equal(1, doc.Version);
    }

    [Fact]
    public void Parse_WithName_SetsName()
    {
        var yaml = "version: 1\nname: test-wf\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n";
        var doc = WorkflowParser.Parse(yaml);
        Assert.Equal("test-wf", doc.Name);
    }

    [Fact]
    public void Parse_WithEntrypoint_SetsEntrypoint()
    {
        var yaml = "version: 1\nentrypoint: myWf\nworkflows:\n  myWf:\n    steps:\n      - id: s1\n        type: template.render\n";
        var doc = WorkflowParser.Parse(yaml);
        Assert.Equal("myWf", doc.Entrypoint);
    }

    [Fact]
    public void Parse_WithExports_ParsesList()
    {
        var yaml = "version: 1\nexports:\n  - helper\nworkflows:\n  helper:\n    steps:\n      - id: s1\n        type: template.render\n";
        var doc = WorkflowParser.Parse(yaml);
        Assert.NotNull(doc.Exports);
        Assert.Single(doc.Exports);
        Assert.Equal("helper", doc.Exports[0]);
    }

    [Fact]
    public void Parse_WithFunctions_SetsGlobalFunctions()
    {
        var yaml = "version: 1\nfunctions: |\n  function foo() { return 1; }\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n";
        var doc = WorkflowParser.Parse(yaml);
        Assert.NotNull(doc.Functions);
        Assert.Contains("function foo()", doc.Functions);
    }

    [Fact]
    public void Parse_StepWithIf_ParsesGuard()
    {
        var yaml = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n        if: \"${true}\"\n";
        var doc = WorkflowParser.Parse(yaml);
        Assert.Equal("${true}", doc.Workflows["main"].Steps[0].If);
    }

    [Fact]
    public void Parse_StepWithRetry_ParsesRetryPolicy()
    {
        var yaml = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: llm.call\n        retry:\n          max: 3\n          backoff_ms: 500\n";
        var doc = WorkflowParser.Parse(yaml);
        var retry = doc.Workflows["main"].Steps[0].Retry;
        Assert.NotNull(retry);
        Assert.Equal(3, retry!.Max);
        Assert.Equal(500, retry.BackoffMs);
    }

    [Fact]
    public void Parse_StepWithOnError_ParsesOnError()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: llm.call
        on_error:
          cases:
            - action: continue
              set_output: fallback
            - if: "${error.code == \"LLM_TIMEOUT\"}"
              action: retry
""";
        var doc = WorkflowParser.Parse(yaml);
        var oe = doc.Workflows["main"].Steps[0].OnError;
        Assert.NotNull(oe);
        Assert.Equal(2, oe!.Cases.Count);
        Assert.Equal("continue", oe.Cases[0].Action);
        Assert.Equal("retry", oe.Cases[1].Action);
    }

    [Fact]
    public void Parse_WorkflowInputs_ParsesInputDefs()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      name:
        type: string
        required: true
        default: world
      count:
        type: number
    steps:
      - id: s1
        type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        var inputs = doc.Workflows["main"].Inputs;
        Assert.NotNull(inputs);
        Assert.Equal(2, inputs!.Count);
        Assert.Equal("string", inputs["name"].Type);
        Assert.True(inputs["name"].Required);
        Assert.Equal("world", inputs["name"].Default);
        Assert.Equal("number", inputs["count"].Type);
    }

    [Fact]
    public void Parse_WorkflowOutputs_ParsesOutputMap()
    {
        var yaml = "version: 1\nworkflows:\n  main:\n    steps:\n      - id: s1\n        type: template.render\n    outputs:\n      result: \"${data.steps.s1}\"\n";
        var doc = WorkflowParser.Parse(yaml);
        var outputs = doc.Workflows["main"].Outputs;
        Assert.NotNull(outputs);
        Assert.Equal("${data.steps.s1}", outputs!["result"].Expr);
        Assert.Equal("any", outputs["result"].Type); // untyped short form defaults to "any"
    }

    [Fact]
    public void Parse_TypedOutputs_ParsesLongForm()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
    outputs:
      summary:
        expr: ""${data.steps.s1.text}""
        type: string
        description: The summary text
      count:
        expr: ""${data.steps.s1.count}""
        type: number
        description: Number of items
      items:
        expr: ""${data.steps.s1.items}""
        type: array
        items: { type: string }
        description: List of item names
      details:
        expr: ""${data.steps.s1.json}""
        type: object
        description: Structured details
        properties:
          name: { type: string }
          score: { type: number }
        required: [name]
";
        var doc = WorkflowParser.Parse(yaml);
        var outputs = doc.Workflows["main"].Outputs;
        Assert.NotNull(outputs);
        Assert.Equal(4, outputs!.Count);

        // string output
        Assert.Equal("${data.steps.s1.text}", outputs["summary"].Expr);
        Assert.Equal("string", outputs["summary"].Type);
        Assert.Equal("The summary text", outputs["summary"].Description);

        // number output
        Assert.Equal("number", outputs["count"].Type);

        // array output with items
        Assert.Equal("array", outputs["items"].Type);
        Assert.NotNull(outputs["items"].Items);
        Assert.Equal("string", outputs["items"].Items!.Type);

        // object output with properties and required
        Assert.Equal("object", outputs["details"].Type);
        Assert.NotNull(outputs["details"].Properties);
        Assert.Equal(2, outputs["details"].Properties!.Count);
        Assert.Equal("string", outputs["details"].Properties["name"].Type);
        Assert.Equal("number", outputs["details"].Properties["score"].Type);
        Assert.NotNull(outputs["details"].RequiredProperties);
        Assert.Contains("name", outputs["details"].RequiredProperties!);
    }

    [Fact]
    public void Parse_NestedObjectOutputs_BackwardCompat()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
    outputs:
      meta:
        model: ""${data.steps.s1.model}""
        attempts: ""${data.steps.s1.attempts}""
";
        var doc = WorkflowParser.Parse(yaml);
        var outputs = doc.Workflows["main"].Outputs;
        Assert.NotNull(outputs);
        var meta = outputs!["meta"];
        Assert.Equal("object", meta.Type);
        Assert.NotNull(meta.Properties);
        Assert.Equal("${data.steps.s1.model}", meta.Properties!["model"].Expr);
        Assert.Equal("${data.steps.s1.attempts}", meta.Properties["attempts"].Expr);
    }

    [Fact]
    public void Parse_SequenceStep_ParsesSubSteps()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: seq1
        type: sequence
        steps:
          - id: inner1
            type: template.render
          - id: inner2
            type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        var step = doc.Workflows["main"].Steps[0];
        Assert.Equal("sequence", step.Type);
        Assert.NotNull(step.Steps);
        Assert.Equal(2, step.Steps!.Count);
    }

    [Fact]
    public void Parse_ParallelStep_ParsesBranches()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: par1
        type: parallel
        branches:
          - steps:
              - id: b1s1
                type: template.render
          - steps:
              - id: b2s1
                type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        var step = doc.Workflows["main"].Steps[0];
        Assert.Equal("parallel", step.Type);
        Assert.NotNull(step.Branches);
        Assert.Equal(2, step.Branches!.Count);
    }

    [Fact]
    public void Parse_SwitchStep_ParsesCasesAndDefault()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: sw1
        type: switch
        expr: ""${data.inputs.x}""
        cases:
          - value: a
            steps:
              - id: ca1
                type: template.render
          - when: ""${true}""
            steps:
              - id: ca2
                type: template.render
        default:
          - id: def1
            type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        var step = doc.Workflows["main"].Steps[0];
        Assert.Equal("switch", step.Type);
        Assert.NotNull(step.Cases);
        Assert.Equal(2, step.Cases!.Count);
        Assert.Equal("a", step.Cases[0].Value);
        Assert.NotNull(step.Cases[1].When);
        Assert.NotNull(step.Default);
        Assert.Single(step.Default!);
    }

    [Fact]
    public void Parse_QuotedExpressionWithInnerDoubleQuotes_ParsesSuccessfully()
    {
        var yaml = """
version: 1
workflows:
  main:
    steps:
      - id: config
        type: set
        input:
          is_fast: '${data.inputs.mode == "fast"}'
      - id: branch
        type: switch
        cases:
          - when: '${data.inputs.mode == "standard"}'
            steps:
              - id: s1
                type: template.render
        default:
          - id: s2
            type: template.render
""";

        var doc = WorkflowParser.Parse(yaml);
        var steps = doc.Workflows["main"].Steps;

        Assert.Equal("${data.inputs.mode == \"fast\"}", ((steps[0].Input as System.Text.Json.Nodes.JsonObject)?["is_fast"] as System.Text.Json.Nodes.JsonValue)?.GetValue<string>());
        Assert.Equal("${data.inputs.mode == \"standard\"}", steps[1].Cases![0].When);
    }

    [Fact]
    public void Parse_LoopParallel_ParsesItemVarAndIndexVar()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: lp1
        type: loop.parallel
        item_var: element
        index_var: idx
        input:
          items: ""${data.inputs.list}""
        steps:
          - id: inner
            type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        var step = doc.Workflows["main"].Steps[0];
        Assert.Equal("element", step.ItemVar);
        Assert.Equal("idx", step.IndexVar);
    }

    [Fact]
    public void Parse_MultipleWorkflows_AllParsed()
    {
        var yaml = @"
version: 1
workflows:
  main:
    steps:
      - id: s1
        type: template.render
  helper:
    steps:
      - id: s1
        type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        Assert.Equal(2, doc.Workflows.Count);
        Assert.True(doc.Workflows.ContainsKey("main"));
        Assert.True(doc.Workflows.ContainsKey("helper"));
    }

    [Fact]
    public void Parse_InvalidYaml_Throws()
    {
        Assert.ThrowsAny<Exception>(() => WorkflowParser.Parse("not valid yaml: ["));
    }

    [Fact]
    public void Parse_MissingWorkflows_Throws()
    {
        Assert.Throws<WorkflowParseException>(() => WorkflowParser.Parse("version: 1\n"));
    }

    [Fact]
    public void Parse_ArrayInput_WithItems_ParsesElementType()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      tags:
        type: array
        items:
          type: string
    steps:
      - id: s1
        type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        var tags = doc.Workflows["main"].Inputs!["tags"];
        Assert.Equal("array", tags.Type);
        Assert.NotNull(tags.Items);
        Assert.Equal("string", tags.Items!.Type);
    }

    [Fact]
    public void Parse_ArrayInput_WithShortItems_ParsesElementType()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      tags:
        type: array
        items: string
    steps:
      - id: s1
        type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        var tags = doc.Workflows["main"].Inputs!["tags"];
        Assert.Equal("string", tags.Items!.Type);
    }

    [Fact]
    public void Parse_ObjectInput_WithProperties_ParsesSchema()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      config:
        type: object
        properties:
          host:
            type: string
            required: true
          port:
            type: number
            default: 8080
        required:
          - host
    steps:
      - id: s1
        type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        var config = doc.Workflows["main"].Inputs!["config"];
        Assert.Equal("object", config.Type);
        Assert.NotNull(config.Properties);
        Assert.Equal(2, config.Properties!.Count);
        Assert.Equal("string", config.Properties["host"].Type);
        Assert.True(config.Properties["host"].Required);
        Assert.Equal("number", config.Properties["port"].Type);
        Assert.Equal("8080", config.Properties["port"].Default);
        Assert.NotNull(config.RequiredProperties);
        Assert.Single(config.RequiredProperties!);
        Assert.Equal("host", config.RequiredProperties[0]);
    }

    [Fact]
    public void Parse_DictionaryInput_WithAdditionalProperties_ParsesValueType()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      scores:
        type: dictionary
        additional_properties:
          type: number
    steps:
      - id: s1
        type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        var scores = doc.Workflows["main"].Inputs!["scores"];
        Assert.Equal("dictionary", scores.Type);
        Assert.NotNull(scores.AdditionalProperties);
        Assert.Equal("number", scores.AdditionalProperties!.Type);
    }

    [Fact]
    public void Parse_NestedRichTypes_ArrayOfObjects()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      people:
        type: array
        items:
          type: object
          properties:
            name: { type: string }
            age: { type: number }
    steps:
      - id: s1
        type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        var people = doc.Workflows["main"].Inputs!["people"];
        Assert.Equal("array", people.Type);
        Assert.NotNull(people.Items);
        Assert.Equal("object", people.Items!.Type);
        Assert.NotNull(people.Items.Properties);
        Assert.Equal("string", people.Items.Properties!["name"].Type);
        Assert.Equal("number", people.Items.Properties["age"].Type);
    }

    [Fact]
    public void Parse_InputWithDescription_ParsesDescription()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      query:
        type: string
        description: The search query to execute
    steps:
      - id: s1
        type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        var query = doc.Workflows["main"].Inputs!["query"];
        Assert.Equal("The search query to execute", query.Description);
    }

    [Fact]
    public void Parse_DictionaryOfComplexValues()
    {
        var yaml = @"
version: 1
workflows:
  main:
    inputs:
      env_map:
        type: dictionary
        additional_properties:
          type: object
          properties:
            url: { type: string }
            port: { type: number }
    steps:
      - id: s1
        type: template.render
";
        var doc = WorkflowParser.Parse(yaml);
        var env = doc.Workflows["main"].Inputs!["env_map"];
        Assert.Equal("dictionary", env.Type);
        Assert.NotNull(env.AdditionalProperties);
        Assert.Equal("object", env.AdditionalProperties!.Type);
        Assert.Equal("string", env.AdditionalProperties.Properties!["url"].Type);
        Assert.Equal("number", env.AdditionalProperties.Properties["port"].Type);
    }
}
