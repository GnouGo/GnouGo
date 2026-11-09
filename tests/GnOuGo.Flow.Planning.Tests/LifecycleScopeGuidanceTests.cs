using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LifecycleScopeGuidanceTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("consumer_failure")]
    [InlineData("producer_failure")]
    [InlineData("unused")]
    public async Task ParentOwnsCleanupAcrossAReusableHelperAndItsLaterConsumer(string mode)
    {
        var effects = new List<string>();
        var empty = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!;
        var label = JsonNode.Parse("""{"type":"object","properties":{"label":{"type":"string"}},"required":["label"],"additionalProperties":false}""")!;
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("resource", new()
        {
            Tools = [new() { Name = "acquire", InputSchema = empty, OutputSchema = label, EffectKind = "none" },
                new() { Name = "consume", InputSchema = label, OutputSchema = empty, EffectKind = "none" },
                new() { Name = "release", InputSchema = label, OutputSchema = empty, EffectKind = "none" }],
            ToolHandlers = new()
            {
                ["acquire"] = _ => { effects.Add("acquire"); if (mode == "producer_failure") throw new IOException("Acquire failed");
                    return new() { Content = new JsonObject { ["label"] = "actual resource" } }; },
                ["consume"] = input => { Assert.Equal("actual resource", input!["label"]!.ToString()); effects.Add("consume");
                    if (mode == "consumer_failure") throw new IOException("Consumer failed"); return new() { Content = new JsonObject() }; },
                ["release"] = input => { Assert.Equal("actual resource", input!["label"]!.ToString()); effects.Add("release"); return new() { Content = new JsonObject() }; }
            }
        });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await TaskPlanCompilerTests.Catalog(runtime);
        PlanTask Operation(string id, string name) => new() { Id = id, Kind = "operation", Objective = "Use the declared resource operation",
            Operation = catalog.Capabilities.Single(capability => capability.Method == name).Id };
        var acquire = Operation("open", "acquire");
        var consume = Operation("use", "consume");
        consume.Inputs = [new("label", new() { Kind = "output", Source = "helper", Port = "label" })];
        var release = Operation("close", "release");
        release.Inputs = [new("label", new() { Kind = "output", Source = "helper", Port = "label" })];
        var plan = new TaskPlan
        {
            Groups = [new() { Id = "acquisition", Body = new() { Tasks = [acquire], Outputs = [new("label", new()
                { Kind = "field", Port = "label", Items = [new() { Kind = "output", Source = "open" }] })] } }],
            Root = new() { Tasks = [new() { Id = "helper", Kind = "call", Objective = "Acquire the resource", Group = "acquisition" }, consume],
                Always = [new() { Id = "cleanup", Kind = "conditional", Objective = "Release the actual acquired resource",
                    Condition = new() { Kind = "present", Source = "helper" }, Body = new() { Tasks = [release] }, Otherwise = new() }] }
        };
        if (mode == "unused") { plan.Root.Tasks.Clear(); plan.Root.Always.Clear(); }
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics);
        Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        Assert.Empty(await runtime.ValidateAsync(new(yaml, new(), catalog, PlanningGraphCompiler.CapabilityBindings(compiled.Graph!)), PlannerFixture.Ct));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows["main"], new JsonObject(), PlannerFixture.Ct);
        Assert.Equal(mode is "success" or "unused", result.Success);
        Assert.Equal(mode switch { "unused" => [], "producer_failure" => ["acquire"], _ => new[] { "acquire", "consume", "release" } }, effects);
        if (mode == "consumer_failure") Assert.Contains("Consumer failed", result.Error!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("construction_implementation", true)]
    [InlineData("construction_discovery", true)]
    [InlineData("construction_draft", true)]
    [InlineData("construction_select", false)]
    public async Task FreshTechnicalDecisionsDistinguishBusinessCompletionFromResourceLifetime(string purpose, bool technical)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready(advisoryOptimization: true);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        const string prompt = "Preserve the original cleanup and failure obligations.\n{\"original\":\"Keep the resource until all consumers finish.\"}";
        var schema = JsonNode.Parse("""{"type":"object","properties":{"choice":{"type":"string","enum":["sequence","group","operation"]}},"required":["choice"],"additionalProperties":false}""")!.AsObject();
        var request = PlanningModelCalls.PrepareRequest(state, purpose, prompt, schema);
        Assert.Equal(technical, request.Prompt.Contains("Business-block review does not execute tasks or close an owning workflow scope", StringComparison.Ordinal));
        if (technical)
        {
            Assert.Contains("Cleanup belongs to the scope covering the resource's actual consumers", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("operationIds for its nested operations", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("Preserve explicit lifetime and cleanup requirements", request.Prompt, StringComparison.Ordinal);
        }
        Assert.EndsWith(prompt[prompt.LastIndexOf('\n')..], request.Prompt, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.True(JsonNode.DeepEquals(schema["properties"], request.StructuredOutputSchema!["properties"]));
        Assert.Equal(request.ClientRequestId, PlanningModelCalls.PrepareRequest(state, purpose, prompt, schema).ClientRequestId);
    }

    [Fact]
    public async Task OptionalGuidanceDoesNotExceedTheStrictCapturedTarget()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var prompt = "Choose from the issued actions.\n{\"original\":\"" + new string('x', 3200) + " Release after all consumers.\"}";
        var schema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!.AsObject();
        var initial = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.ImplementationPurpose, prompt, schema);
        var bare = JsonSerializer.Deserialize(JsonSerializer.Serialize(initial, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        bare.Prompt = prompt;
        var limit = checked((int)ModelRequestAdmission.EstimateTokens(bare) + 1);
        var policy = state.Request.Options["construction_limits"]!["phasePolicy"]!["navigation"]!;
        policy["targetInputTokens"] = limit;
        policy["maxInputTokens"] = limit;
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.ImplementationPurpose, prompt, schema);
        Assert.Equal(prompt, issued.Prompt);
        Assert.InRange(ModelRequestAdmission.EstimateTokens(issued), 1, limit);
        Assert.Null(state.PendingCall);
    }
}
