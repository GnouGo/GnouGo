using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Authoring.JavaScript.Tests;

public sealed class SourceCompilerTests
{
    private static PlanningSourceContext Context() => new(new() { Key = "main", Steps = [new() { Key = "read", Type = "mcp.call", CapabilityId = "declared", OperationIds = ["op"] }] }, new());
    private static PlanningSourceResult Compile(string source) => new JavaScriptPlanningSourceCompiler().Compile(source, Context(), TestContext.Current.CancellationToken);

    [Fact]
    public void EmitsNativeNodesTypedReferencesAndFinalizers()
    {
        var result = Compile("""
            const read = flow.step("read", "mcp.call", {request:{id:flow.input("id")}});
            flow.workflow({inputs:[flow.port("id",{type:"string"})],steps:[read,
              flow.choose("decision",flow.structured("read","allowed"),[{value:"true",steps:[
                flow.parallel("branches",[[flow.call("nested","child",{value:flow.ref("read","text")})],
                  [flow.loop("items",[1,2],[flow.step("item","set",{value:flow.item("items")})])]])
              ]}],[])],finally:[flow.step("cleanup","set",{done:true},{onError:[{action:"continue",setOutput:{done:false}}]})],
              outputs:[flow.result("text",{type:"string"},flow.ref("read","text"))]});
            """);
        Assert.Empty(result.Diagnostics);
        var workflow = Assert.IsType<PlanningWorkflow>(result.Workflow);
        Assert.Equal("declared", workflow.Steps[0].CapabilityId);
        Assert.Equal("op", Assert.Single(workflow.Steps[0].OperationIds));
        Assert.Equal("switch", workflow.Steps[1].Type);
        Assert.Equal("structured", workflow.Steps[1].Expr!.ResultChannel);
        Assert.Equal("object", workflow.Finally[0].OnError[0].SetOutput!.Kind);
        Assert.Equal("output", workflow.Outputs[0].Value.Kind);
        Assert.Equal(1, result.Locations["read"].Line);
    }

    [Theory]
    [InlineData("while(true) {}")]
    [InlineData("const a=[]; while(true) a.push('x'.repeat(100000));")]
    [InlineData("function f(){return f();} f();")]
    [InlineData("(()=>{}).constructor('return 1')();")]
    [InlineData("eval('1');")]
    [InlineData("import('fs');")]
    [InlineData("System.IO.File.ReadAllText('/etc/passwd');")]
    [InlineData("fetch('https://example.invalid');")]
    [InlineData("new Date();")]
    [InlineData("globalThis['Date'].now();")]
    [InlineData("new Intl.DateTimeFormat().format();")]
    [InlineData("Math.random();")]
    [InlineData("flow.workflow({steps:[flow.step('x','set',{bad:undefined})]});")]
    [InlineData("flow.workflow({arbitraryIgnoredField:true});")]
    public void RejectsUnsafeOrUnrepresentableSource(string source)
    {
        var result = Compile(source);
        Assert.Null(result.Workflow);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void ReportsSyntaxLocationAndHonorsCancellation()
    {
        var syntax = Compile("const = ;");
        Assert.Equal("JS_SYNTAX", Assert.Single(syntax.Diagnostics).Code);
        Assert.StartsWith("/source/1:", syntax.Diagnostics[0].Location);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new JavaScriptPlanningSourceCompiler().Compile("while(true){}", Context(), cancelled.Token));
    }

    [Fact]
    public void DocumentationUsesDeclaredSchemaTypes()
    {
        var context = Context();
        context.Preparation.Capabilities.Add(new() { Id = "renamed", InputSchema = new() { ["type"] = "array", ["items"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "integer" } } });
        Assert.Contains("Array<number>", new JavaScriptPlanningSourceCompiler().Describe(context));
    }
}
