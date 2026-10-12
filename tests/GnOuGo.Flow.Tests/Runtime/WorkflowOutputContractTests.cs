using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowOutputContractTests
{
    internal const string CheckedOutputYaml = """
        version: 1
        workflows:
          main:
            inputs:
              pages: {type: array, items: {type: array, items: {type: string, nullable: true}}}
            steps: []
            finally:
            - id: cleanup
              type: set
              input: {closed: true}
            outputs:
              products:
                type: array
                schema:
                  type: array
                  items: {type: [string, 'null']}
                  maxItems: 3
                expr: |-
                  ${(() => {
                    const closed = data.steps.cleanup.closed;
                    const pages = data.inputs.pages;
                    const products = pages.flatMap(page => page);
                    return products;
                  })()}
                expression_contracts:
                  '':
                    closed: {origin: verifyCleanup, order: 0, source: closed, schema: {const: true}}
                    pages: {origin: collectPages, order: 1, source: pages, schema: {type: array}}
                    products: {origin: collectProducts, order: 2, source: products, schema: {type: array}}
                    publish: {origin: validateProducts, order: 3, source: products, schema: {type: array}}
        """;

    [Theory]
    [InlineData("[[\"a\",null],[],[\"a\"]]", true)]
    [InlineData("[[\"a\",\"b\"],[\"c\",\"d\"]]", false)]
    public async Task CheckedOutputsRunAfterFinalizationAndRequireTheFinalContract(string pages, bool success)
    {
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(CheckedOutputYaml)).Workflows["main"];
        var result = await new WorkflowEngine().ExecuteAsync(workflow, new JsonObject { ["pages"] = JsonNode.Parse(pages) }, TestContext.Current.CancellationToken);
        Assert.Equal(success, result.Success);
        Assert.True(Assert.Single(result.StepResults).Output!["closed"]!.GetValue<bool>());
        if (success) Assert.Equal("[\"a\",null,\"a\"]", result.Outputs!["products"]!.ToJsonString());
        else
        {
            Assert.Null(result.Outputs); Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
            Assert.Equal("products", result.Error.Details!["failed_output"]!.GetValue<string>());
            Assert.Contains("must contain at most 3 items", result.Error.Message);
        }
    }

    [Theory]
    [InlineData("schema: {type: array}", "schema: {type: array, maxItems: 0}", "collectPages")]
    [InlineData("source: products, schema: {type: array}}", "source: products, schema: {oneOf: [{type: array}, {type: array}]}}", "collectProducts")]
    public async Task InvalidIntermediatesFailInOrderWithoutPublishingOutputs(string from, string to, string origin)
    {
        var yaml = CheckedOutputYaml.Replace(from, to, StringComparison.Ordinal);
        var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
            JsonNode.Parse("""{"pages":[["observed"]]}"""), TestContext.Current.CancellationToken);
        Assert.False(result.Success); Assert.Null(result.Outputs);
        Assert.Equal(origin, result.Error!.Details!["location"]!.GetValue<string>());
        Assert.Equal("INPUT_VALIDATION", result.Error.Code);
    }

    [Fact]
    public async Task OutputFailureCannotReplaceEarlierExecutionFailure()
    {
        var document = WorkflowParser.Parse(CheckedOutputYaml);
        document.Workflows["main"].Steps.Add(new() { Id = "required", Type = "set", Input = new JsonObject { ["allowed"] = false },
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"allowed":{"const":true}},"required":["allowed"]}""") });
        var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(document).Workflows["main"],
            JsonNode.Parse("""{"pages":[["a","b","c","d"]]}"""), TestContext.Current.CancellationToken);
        Assert.False(result.Success); Assert.Null(result.Outputs);
        Assert.Equal("required", result.Error!.Details!["failed_step_id"]!.GetValue<string>());
        Assert.DoesNotContain("maxItems", result.Error.Message);
        Assert.Equal(2, result.StepResults.Count);
    }

    [Theory]
    [InlineData("pointer")]
    [InlineData("no_schema")]
    [InlineData("no_expression")]
    [InlineData("invalid_schema")]
    [InlineData("invalid_source")]
    public void InvalidOutputMetadataIsRejectedBeforeExecution(string variant)
    {
        var document = WorkflowParser.Parse(CheckedOutputYaml); var output = document.Workflows["main"].Outputs!["products"];
        switch (variant)
        {
            case "pointer": var entry = output.ExpressionContracts![""]!.DeepClone(); output.ExpressionContracts.Clear(); output.ExpressionContracts["/products"] = entry; break;
            case "no_schema": output.Schema = null; output.Type = "any"; break;
            case "no_expression": output.Expr = ""; break;
            case "invalid_schema": output.Schema!["type"] = "unsupported"; break;
            case "invalid_source": output.ExpressionContracts![""]!["pages"]!["source"] = "unavailable"; break;
        }
        Assert.Throws<WorkflowCompilationException>(() => new WorkflowCompiler().Compile(document));
    }

    [Fact]
    public void OutputMetadataCannotHideInUnevaluatedSchemaDescriptors()
    {
        var document = WorkflowParser.Parse(CheckedOutputYaml);
        var output = document.Workflows["main"].Outputs!["products"];
        var hidden = new OutputDef { Type = "string", Expr = "${'never evaluated'}", ExpressionContracts = new() { [""] = new JsonObject() } };
        output.Items = hidden;
        Assert.Throws<WorkflowCompilationException>(() => new WorkflowCompiler().Compile(document));
        output.Items = null;
        output.Properties = new() { ["hidden"] = hidden };
        Assert.Throws<WorkflowCompilationException>(() => new WorkflowCompiler().Compile(document));
    }

    [Fact]
    public void NestedMetadataBesideAuthoritativeSchemaStillRequiresCorrespondence()
    {
        var document = WorkflowParser.Parse(CheckedOutputYaml);
        var products = document.Workflows["main"].Outputs!["products"];
        products.ExpressionContracts![""]!["pages"]!["source"] = "unavailable";
        document.Workflows["main"].Outputs = new()
        {
            ["report"] = new() { Schema = JsonNode.Parse("""{"type":"object"}""")!.AsObject(), Properties = new() { ["products"] = products } }
        };
        Assert.Throws<WorkflowCompilationException>(() => new WorkflowCompiler().Compile(document));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedFinalContractEnforcementIsOptInAndPreservesLegacySchemaPrecedence(bool checkedExpression)
    {
        var document = WorkflowParser.Parse(CheckedOutputYaml);
        var products = document.Workflows["main"].Outputs!["products"];
        if (!checkedExpression) products.ExpressionContracts = null;
        document.Workflows["main"].Outputs = new()
        {
            ["report"] = new() { Schema = JsonNode.Parse("""{"type":"object"}""")!.AsObject(), Properties = new() { ["products"] = products } }
        };
        var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(document).Workflows["main"],
            JsonNode.Parse("""{"pages":[["a","b","c","d"]]}"""), TestContext.Current.CancellationToken);
        Assert.Equal(!checkedExpression, result.Success);
        if (checkedExpression) Assert.Contains("must contain at most 3 items", result.Error!.Message);
        else Assert.Equal(4, result.Outputs!["report"]!["products"]!.AsArray().Count);
    }

    [Fact]
    public async Task MetadataRoundTripsAndCompletedRecoveryDoesNotRecomputeOutputs()
    {
        var document = WorkflowParser.Parse(CheckedOutputYaml);
        var json = JsonSerializer.Serialize(document, WorkflowRunJsonContext.Default.WorkflowDocument);
        var restored = JsonSerializer.Deserialize(json, WorkflowRunJsonContext.Default.WorkflowDocument)!;
        Assert.True(JsonNode.DeepEquals(document.Workflows["main"].Outputs!["products"].ExpressionContracts, restored.Workflows["main"].Outputs!["products"].ExpressionContracts));
        Assert.DoesNotContain("expression_contracts", JsonSchemaConverter.OutputsToJsonSchema(restored.Workflows["main"].Outputs!).ToJsonString());
        var workflow = new WorkflowCompiler().Compile(restored).Workflows["main"];
        var store = new InMemoryWorkflowRunStore(); var engine = new WorkflowEngine { RunStore = store, Limits = new() { TenantId = "tenant", RunId = "checked-outputs" } };
        var result = await engine.ExecuteAsync(workflow, JsonNode.Parse("""{"pages":[["exact",null]]}"""), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        var saved = (await store.ReadAsync("tenant", "checked-outputs", TestContext.Current.CancellationToken))!;
        var resumed = await new WorkflowEngine { RunStore = store }.ResumeAsync("tenant", "checked-outputs", saved.Revision, workflow, TestContext.Current.CancellationToken);
        Assert.Equal(result.Outputs!.ToJsonString(), resumed.Outputs!.ToJsonString());
        var after = (await store.ReadAsync("tenant", "checked-outputs", TestContext.Current.CancellationToken))!;
        Assert.Equal(saved.Revision, after.Revision); Assert.Equal(saved.Invocations.Count, after.Invocations.Count);
        restored.Workflows["main"].Outputs!["products"].ExpressionContracts![""]!["publish"]!["origin"] = "changed";
        await Assert.ThrowsAsync<WorkflowRunConflictException>(() => new WorkflowEngine { RunStore = store }.ResumeAsync("tenant", "checked-outputs", after.Revision,
            new WorkflowCompiler().Compile(restored).Workflows["main"], TestContext.Current.CancellationToken));
        var legacy = new OutputDef { Expr = "${null}" };
        Assert.DoesNotContain("ExpressionContracts", JsonSerializer.Serialize(legacy, WorkflowRunJsonContext.Default.OutputDef));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckedOutputNullabilityComesOnlyFromTheDeclaredContract(bool nullable)
    {
        var definition = new OutputDef { Type = "string", Nullable = nullable,
            Expr = "${(()=>{const missing=null;return true ? missing : null;})()}",
            ExpressionContracts = JsonNode.Parse("""{"":{"missing":{"origin":"missingFact","order":0,"source":"missing","schema":{"type":"null"}}}}""")!.AsObject() };
        var workflow = new WorkflowCompiler().Compile(new WorkflowDocument { Workflows = new() { ["main"] = new() { Outputs = new() { ["fact"] = definition } } } }).Workflows["main"];
        var result = await new WorkflowEngine().ExecuteAsync(workflow, new JsonObject(), TestContext.Current.CancellationToken);
        Assert.Equal(nullable, result.Success);
        if (nullable) Assert.Null(result.Outputs!["fact"]);
        else { Assert.Null(result.Outputs); Assert.Equal("INPUT_VALIDATION", result.Error!.Code); }
    }
}
