using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FunctionalResponsibilityVariationTests
{
    [Theory]
    [InlineData("coarse", 1)]
    [InlineData("coarse", 8)]
    [InlineData("fine", 1)]
    [InlineData("fine", 8)]
    [InlineData("repacked", 1)]
    [InlineData("repacked", 8)]
    public async Task EquivalentCapabilitiesAndFragmentSizesPreserveTheChosenBusinessResponsibility(string layout, int fragmentSize)
    {
        const string goal = "Double the supplied number. ";
        const string constraints = "Preserve producer order, keep the supplied value private, expose only the final result, and always reset temporary work.";
        var calls = new List<string>();
        var factory = new InMemoryMcpClientFactory();
        var servers = new Dictionary<string, MockMcpServerConfig>(StringComparer.Ordinal);
        var empty = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!;
        var unary = JsonNode.Parse("""{"type":"object","properties":{"x":{"type":"number"}},"required":["x"],"additionalProperties":false}""")!;
        var binary = JsonNode.Parse("""{"type":"object","properties":{"left":{"type":"number"},"right":{"type":"number"}},"required":["left","right"],"additionalProperties":false}""")!;
        var resultSchema = JsonNode.Parse("""{"type":"object","properties":{"result":{"type":"number"}},"required":["result"],"additionalProperties":false}""")!;
        if (layout == "coarse") Register("arithmetic", "double", unary, input => 2 * Number(input, "x"));
        else
        {
            Register(layout == "repacked" ? "observation" : "arithmetic", "identity", unary, input => Number(input, "x"));
            Register(layout == "repacked" ? "combination" : "arithmetic", "sum", binary, input => Number(input, "left") + Number(input, "right"));
        }
        factory.RegisterServer("temporary", new() { Tools = [new() { Name = "reset", Description = "Reset temporary work after success or failure.",
            InputSchema = empty.DeepClone(), OutputSchema = empty.DeepClone(), EffectKind = "lifecycle" }],
            ToolHandlers = new() { ["reset"] = _ => { calls.Add("reset"); return new() { Content = new JsonObject() }; } } });
        var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human(true) };
        var runtime = new TestRuntime(engine);
        // All variants capture the same trusted diagnostic policy before session
        // creation. Its token targets remain warnings; ordinary admission remains
        // covered by the existing strict-policy tests.
        var (state, _) = await BusinessReviewProtocolTests.Ready(advisoryOptimization: true);
        state.Catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual);
        state.Discovery.Resolved.AddRange(state.Catalog.Capabilities.Where(capability => capability.Kind == "tool"));
        state.Request.Prompt = goal + constraints;
        state.Requirements!.Outcomes = [new("work", goal)];
        state.Requirements.Outputs = [new() { Name = "result", Required = true, Type = new() { Kind = "number" } }];
        var construction = state.Construction!; var compact = construction.Compact!;
        construction.Candidate = null; construction.Requirements = [new() { Id = "work" }];
        compact.CurrentStep = null; compact.CurrentBlockId = null; compact.Blocks!.Clear();
        compact.Sources.Clear(); compact.RequirementSources.Clear(); compact.GlobalSourceIds.Clear();
        Source("request:0", "page", goal + constraints, [], []);
        Source("request:0:clause:0", "requirement", goal, state.Requirements.Inputs!, state.Requirements.Outputs);
        Source("request:0:clause:1", "global", constraints, [], []);
        compact.RequirementSources["work"] = ["request:0:clause:0"]; compact.GlobalSourceIds.Add("request:0:clause:1");
        var steps = new List<PlanTask> { new() { Id = "seed", Kind = "value", Objective = "Keep the supplied number for the actual calculation.",
            Outputs = [new("number", new() { Kind = "input", Source = "x" })] } };
        if (layout != "coarse") steps.Add(Operation("copy", "identity", [new("x", Output("seed", "number"))]));
        steps.Add(Operation("finish", layout == "coarse" ? "double" : "sum", layout == "coarse"
            ? [new("x", Output("seed", "number"))]
            : [new("left", Output("copy", "result")), new("right", Output("seed", "number"))]));
        var fragments = steps.Chunk(fragmentSize).ToArray();
        var fragment = 0; var selections = 0; var reviews = 0;
        string? chosenBusiness = null;
        runtime.Respond = (request, purpose) =>
        {
            var context = Context(request);
            if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                selections++;
                Assert.NotNull(context["overallProgress"]);
                // Wording varies deliberately; the fixture's one chosen outcome
                // is preserved without imposing one block on arbitrary requests.
                return Wire(request, new() { ["action"] = new JsonObject { ["kind"] = "block", ["block"] = new JsonObject
                {
                    ["objective"] = layout == "coarse" ? "Return the doubled sample" : "Produce twice the supplied measurement",
                    ["requirementIds"] = new JsonArray("work"), ["informationNeeds"] = new JsonArray("The supplied numeric input"),
                    ["expectedResults"] = new JsonArray("The doubled number"), ["dependencies"] = new JsonArray(),
                    ["successCriteria"] = new JsonArray("The only public result equals twice the input, with temporary work reset")
                } } });
            }
            if (purpose == PlanningCompactConstruction.ImplementationPurpose)
            {
                Assert.NotNull(context["overallProgress"]);
                Assert.Contains(constraints, request.Prompt, StringComparison.Ordinal);
                var last = fragment == fragments.Length - 1;
                return Wire(request, new()
                {
                    ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.ToString(),
                    ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = fragment == 0 ? "value" : "operation",
                        ["operationIds"] = new JsonArray(fragments[fragment].Where(task => task.Operation is not null).Select(task => (JsonNode?)JsonValue.Create(task.Operation))
                            .Concat(last ? [JsonValue.Create(OperationId("reset"))] : [] ).ToArray()),
                        ["inputs"] = new JsonArray(), ["dependencyTaskIds"] = new JsonArray(),
                        ["closeRequirementIds"] = last ? new JsonArray("work") : new JsonArray(),
                        ["outputNames"] = last ? new JsonArray("result") : new JsonArray() },
                    ["clarifications"] = null, ["evidenceRequests"] = null
                });
            }
            if (purpose == PlanningConstruction.DraftPurpose)
            {
                Assert.Contains(constraints, request.Prompt, StringComparison.Ordinal);
                var last = fragment == fragments.Length - 1;
                var extension = new PlanningConstructionExtension { Scope = "/root", Tasks = fragments[fragment++].ToList(),
                    RequirementIds = ["work"], CompleteBlock = last, Complete = last,
                    CloseScopes = last ? ["/root"] : [],
                    Outputs = last ? [new("result", Output("finish", "result"))] : [],
                    Always = last ? [Operation("release", "reset", [])] : [] };
                return Wire(request, new() { ["baseHash"] = context["baseHash"]!.ToString(),
                    ["requirements"] = null, ["commitments"] = null,
                    ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension),
                    ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null });
            }
            Assert.Equal(PlanningConstruction.GlobalPurpose, purpose);
            reviews++;
            Assert.Contains(constraints, request.Prompt, StringComparison.Ordinal);
            Assert.Single(context["requirements"]!.AsArray());
            var release = runtime.EvidenceId(request, "task:release");
            Assert.Contains(context["actions"]!.AsArray(), action => action!["id"]!.ToString() == release);
            Assert.Contains(context["scopes"]!.AsArray().Single(scope => scope!["id"]!.ToString() == runtime.EvidenceId(request, "scope:/root"))!["cleanup"]!.AsArray(),
                task => task!.ToString() == release);
            return Wire(request, BusinessReviewProtocolTests.Report("ok"));
        };
        for (var advance = 0; advance < 40 && state.Status != PlanningStatus.FinalReview; advance++)
        {
            state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
            if (PlanningCompactConstruction.CurrentBlock(state) is { } block)
            {
                var current = JsonSerializer.Serialize(block, PlanningJsonContext.Default.PlanningConstructionBlock);
                chosenBusiness ??= current; Assert.Equal(chosenBusiness, current);
            }
        }
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(1, selections); Assert.Equal(1, reviews); Assert.Equal(fragments.Length, fragment);
        Assert.All(state.Construction!.Checkpoints, checkpoint => Assert.Equal("block_1", checkpoint.Step!.BlockId));
        Assert.Equal(new[] { "work" }, Assert.Single(state.Construction.Compact!.Blocks!).RequirementIds);
        PlanningArtifactApproval.Verify(state);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        foreach (var input in new[] { -3, 0, 8 })
        {
            calls.Clear();
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["x"] = input }, PlannerFixture.Ct);
            Assert.True(result.Success, result.Error?.Message);
            var outputs = Assert.IsType<JsonObject>(result.Outputs);
            Assert.Equal(new[] { "result" }, outputs.Select(pair => pair.Key));
            Assert.Equal(2 * input, double.Parse(outputs["result"]!.ToJsonString(), CultureInfo.InvariantCulture));
            Assert.Equal(layout == "coarse" ? new[] { "double", "reset" } : ["identity", "sum", "reset"], calls);
        }

        void Register(string server, string method, JsonNode schema, Func<JsonNode?, double> calculate)
        {
            if (!servers.TryGetValue(server, out var configuration)) servers.Add(server, configuration = new());
            configuration.Tools.Add(new() { Name = method, Description = method == "double" ? "Return twice x." : method == "identity" ? "Return x unchanged." : "Return left plus right.",
                InputSchema = schema.DeepClone(), OutputSchema = resultSchema.DeepClone(), EffectKind = "read" });
            configuration.ToolHandlers.Add(method, input => { calls.Add(method); return new() { Content = new JsonObject { ["result"] = calculate(input) } }; });
            factory.RegisterServer(server, configuration);
        }
        string OperationId(string method) => TaskOperations.Describe(state.Catalog!.Capabilities.Single(capability => capability.Method == method)).Id;
        PlanTask Operation(string id, string method, List<TaskOutput> inputs) => new() { Id = id, Kind = "operation", Operation = OperationId(method),
            Objective = "Perform the inspected " + method + " operation.", Inputs = inputs };
        void Source(string id, string kind, string text, List<TaskInput> inputs, List<TaskInput> outputs) => compact.Sources.Add(new()
            { Id = id, Kind = kind, Text = text, Indexed = true, Version = PlanningGraphCompiler.Fingerprint(text), Inputs = inputs, Outputs = outputs });
    }

    private static double Number(JsonNode? input, string name) => double.Parse(input![name]!.ToJsonString(), CultureInfo.InvariantCulture);
    private static TaskValue Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
        { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
}
