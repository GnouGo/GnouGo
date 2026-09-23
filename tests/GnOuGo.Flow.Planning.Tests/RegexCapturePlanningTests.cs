using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RegexCapturePlanningTests
{
    // Sanitized reproduction of the saved Designer calculation: regex, capture aliases,
    // direct replace calls, then whole-value validation before any consumer.
    private const string CaptureCalculation = """
        (() => {
          const match = text.match(/^([^:]+):([^:]+):(\d+)$/);
          if (!match) { throw new Error('Invalid reference'); }
          const first = match[1]; const second = match[2]; const number = Number(match[3]);
          const safeFirst = first.replace(/[^A-Za-z0-9_.-]/g, '-');
          const safeSecond = second.replace(/[^A-Za-z0-9_.-]/g, '-');
          return { value: `${safeFirst}-${safeSecond}-${number}` };
        })()
        """;

    private const string StringCaptureCalculation = """
        (() => { const s = String(text); const m = s.match(/^([^:]+):([^:]+):([0-9]+)$/);
          return m ? {value: (m[1] + '-' + m[2] + '-' + Number(m[3])).replace(/[^A-Za-z0-9_.-]/g, '-')}
            : {value: null}; })()
        """;
    private const string DecodedCaptureCalculation = """
        (() => { const m = text.match(/^([^:]+):([^:]+):([0-9]+)$/);
          if (!m) throw new Error('Invalid reference');
          const first = decodeURIComponent(m[1]); const second = decodeURIComponent(m[2]); const n = Number(m[3]);
          const safeFirst = first.replace(/[^A-Za-z0-9_.-]/g, '-'); const safeSecond = second.replace(/[^A-Za-z0-9_.-]/g, '-');
          return {value: `${safeFirst}-${safeSecond}-${n}`}; })()
        """;

    [Theory]
    [InlineData(CaptureCalculation)]
    [InlineData(StringCaptureCalculation)]
    [InlineData(DecodedCaptureCalculation)]
    [InlineData("(() => { const match = text.match(/(a)?b/); const capture = match[1]; return capture.replace(/a/g, 'x').trim().toUpperCase(); })()")]
    [InlineData("text.match(/a/g).map(value => value.replace(/a/g, 'x'))")]
    public void CaptureAliasesAndStringChainsHaveDeclaredMembers(string expression)
        => PlanningComputationContracts.Validate(expression, new Dictionary<string, JsonObject> { ["text"] = new() { ["type"] = "string" } });

    [Theory]
    [InlineData("text.match(/(a)?b/)[1].invented")]
    [InlineData("text.match(/(a)?b/)[1].replace(/a/g, 'x').invented")]
    [InlineData("text.match(/a/g).map(value => value.invented)")]
    [InlineData("raw.match(/(a)?b/)[1].replace(/a/g, 'x')")]
    [InlineData("text.match(pattern)[1].replace(/a/g, 'x')")]
    [InlineData("text.match('(a)?b')[1].replace(/a/g, 'x')")]
    [InlineData("String(raw).match(/a/)")]
    [InlineData("decodeURIComponent(raw).replace(/a/g, 'x')")]
    [InlineData("(() => { const result = String(text).match(/a/); const String = text; return result; })()")]
    [InlineData("[text].map(String => String(text).match(/a/))")]
    [InlineData("(() => { String = text; return String(text).match(/a/); })()")]
    [InlineData("decodeURIComponent(text).invented")]
    public void UnsupportedMembersAndUnknownPatternsRemainRejected(string expression)
        => Assert.Throws<InvalidOperationException>(() => PlanningComputationContracts.Validate(expression, new Dictionary<string, JsonObject>
        {
            ["text"] = new() { ["type"] = "string" }, ["raw"] = new() { ["x-gnougo-opaque"] = true }, ["pattern"] = new() { ["type"] = "string" }
        }));

    [Theory]
    [InlineData("{\"x-gnougo-opaque\":true}")]
    [InlineData("{\"type\":\"number\"}")]
    [InlineData("{\"type\":\"boolean\"}")]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
    public void StringAlternativeCannotAuthorizeMethodsOnIncompatibleAlternatives(string alternative)
    {
        var args = new Dictionary<string, JsonObject> { ["value"] = new() { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string" }, JsonNode.Parse(alternative)) } };
        Assert.Throws<InvalidOperationException>(() => PlanningComputationContracts.Validate("value.replace(/a/g, 'x')", args));
        Assert.Null(ExpressionContractInference.Infer("value.replace(/a/g, 'x')", args));
    }

    [Theory]
    [InlineData(CaptureCalculation, "\"one space:two space:12\"", "one-space-two-space-12")]
    [InlineData(CaptureCalculation, "\"invalid\"", null)]
    [InlineData(CaptureCalculation, "42", null)]
    [InlineData(CaptureCalculation, "null", null)]
    [InlineData(StringCaptureCalculation, "\"one space:two space:12\"", "one-space-two-space-12")]
    [InlineData(StringCaptureCalculation, "\"invalid\"", null)]
    [InlineData(DecodedCaptureCalculation, "\"one%20space:two%20space:12\"", "one-space-two-space-12")]
    [InlineData(DecodedCaptureCalculation, "\"invalid\"", null)]
    [InlineData(DecodedCaptureCalculation, "\"one%ZZ:two:12\"", null)]
    [InlineData(DecodedCaptureCalculation, "\"one%ED%BF%BF:two:12\"", null)]
    [InlineData("(() => { const m = text.match(/^(a)?b$/); return { value: m[1].replace(/a/g, 'x') }; })()", "\"ab\"", "x")]
    [InlineData("(() => { const m = text.match(/^(a)?b$/); return { value: m[1].replace(/a/g, 'x') }; })()", "\"b\"", null)]
    [InlineData("({value: text.match(/a/g)[1].replace(/a/g, 'x')})", "\"aba\"", "x")]
    [InlineData("({value: text.match(/a/g)[1].replace(/a/g, 'x')})", "\"ab\"", null)]
    [InlineData("({value: text.match(/(a)?b/)[99].replace(/a/g, 'x')})", "\"ab\"", null)]
    [InlineData("({value: text.match(/(a)?b/)[1]})", "\"b\"", null)]
    public async Task ValidatedCapturesCompileAndMissingValuesPreventDownstreamCalls(string expression, string input, string? expected)
    {
        var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig();
        var schema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")!.AsObject();
        server.Tools.Add(new() { Name = "observe", EffectKind = "read", InputSchema = schema, OutputSchema = schema.DeepClone() });
        var observed = new List<string>();
        server.ToolHandlers["observe"] = args => { observed.Add(args!["value"]!.GetValue<string>()); return new() { Content = args.DeepClone() }; };
        factory.RegisterServer("capture_fixture", server);
        var runtime = new TestRuntime(mcp: factory);
        var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, TestContext.Current.CancellationToken);
        var plan = new GroundedPlan { Inputs = [new("text", new() { Type = "string" })], Operations = [
            new CalculateGroundedOperation { Id = "captures", Value = new() { Kind = "compute", Text = expression, Members = [new("text", new() { Kind = "input", Source = "text" })] } },
            new ValidateGroundedOperation { Id = "validated", Value = new() { Kind = "result", Source = "captures" }, ResultType = new() { Type = "object", Fields = [new("value", new() { Type = "string" })] } },
            new InvokeGroundedOperation { Id = "downstream", Capability = catalog.Capabilities.Single().Id, Arguments = [new("value", new() { Kind = "result", Source = "validated", Path = ["value"] })] }],
            Outputs = [new("value", new() { Kind = "result", Source = "downstream", Path = ["value"] })] };
        var graph = PlanningGraphBuilder.Build(GroundedPlanValidator.RequireValid(plan, catalog));
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog, "capture-validation");
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var inputs = new JsonObject { ["text"] = JsonNode.Parse(input) };
        if (inputs["text"] is not JsonValue value || !value.TryGetValue<string>(out _))
        {
            var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => engine.ExecuteAsync(document.Workflows["main"], inputs, TestContext.Current.CancellationToken));
            Assert.Equal(GnOuGo.Flow.Core.Models.ErrorCodes.InputValidation, error.Code);
            Assert.Empty(observed); Assert.Empty(runtime.Calls); return;
        }
        var result = await engine.ExecuteAsync(document.Workflows["main"], inputs, TestContext.Current.CancellationToken);
        Assert.Equal(expected is not null, result.Success);
        if (expected is not null) { Assert.Equal(expected, Assert.Single(observed)); Assert.Equal(expected, result.Outputs!["value"]!.GetValue<string>()); }
        else Assert.Empty(observed);
        Assert.Empty(runtime.Calls);
    }

    [Theory]
    [InlineData(CaptureCalculation)]
    [InlineData(StringCaptureCalculation)]
    [InlineData(DecodedCaptureCalculation)]
    public async Task SavedCalculationPatternsRetainWholeValueValidationAndReachFinalReview(string expression)
    {
        var plan = new GroundedPlan
        {
            Inputs = [new("text", new() { Type = "string" }, false, new() { Kind = "string", Text = "one:two:12" })],
            Operations = [
                new CalculateGroundedOperation { Id = "parse", Value = new() { Kind = "compute", Text = expression, Members = [new("text", new() { Kind = "input", Source = "text" })] } },
                new ValidateGroundedOperation { Id = "validated", Value = new() { Kind = "result", Source = "parse" }, ResultType = new() { Type = "object", Fields = [new("value", new() { Type = "string" })] } }],
            Outputs = [new("value", new() { Kind = "result", Source = "validated", Path = ["value"] })]
        };
        var runtime = new TestRuntime(plan);
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Contains(ComputationInferenceProfile.Guidance, runtime.Calls[1].Prompt);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingDecision); Assert.Empty(state.Decisions);
        Assert.Null(state.ApprovedHash); Assert.NotEmpty(state.Scenarios);
        Assert.DoesNotContain(state.Diagnostics, d => d.Required);

        plan.Outputs[0] = new("value", new() { Kind = "result", Source = "parse", Path = ["value"] });
        plan.Operations.RemoveAt(1);
        // Numeric conversion and guarded control flow still need a runtime validation boundary.
        if (expression != StringCaptureCalculation)
            Assert.Contains(GroundedPlanValidator.Validate(plan, state.Catalog!).Diagnostics, d => d.Required);
    }
}
