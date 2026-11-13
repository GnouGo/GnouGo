using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
using static GnOuGo.Flow.Planning.Tests.LookupCompilationTests;
using static GnOuGo.Planning.Examples.ProductTransformationPlan;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class InitialCompositionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("complete")]
    [InlineData("incomplete")]
    [InlineData("unknown")]
    public async Task CompiledCompositionUsesOriginalRecordsForActionsAndRows(string variant)
    {
        var original = new JsonArray(Enumerable.Range(0, 20).Select(i => (JsonNode)new JsonObject
        {
            ["records"] = new JsonArray(new JsonObject { ["id"] = "opaque:entry:" + i,
                ["text"] = "Whole observed block " + i + "\t— café\nsecond line",
                ["href"] = "https://fixture.invalid/arbitrary?entry=" + i + "&evidence=" + new string('x', 500) })
        }).ToArray());
        var originalBytes = original.ToJsonString();
        var calls = new List<string>(); JsonNode? written = null;
        var record = Obj(("id", new()), ("text", new()), ("href", new()));
        var page = Obj(("records", ArrayOf(record)));
        var rowType = ArrayOf(ArrayOf(new() { Nullable = true }));
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("generic", new()
        {
            Tools = [
                Tool("read", new() { Kind = "object" }, Obj(("ready", new() { Kind = "boolean" }), ("pages", ArrayOf(page)))),
                Tool("visit", Obj(("address", new())), Obj(("visited", new() { Kind = "boolean" }))),
                Tool("store", Obj(("rows", rowType)), Obj(("path", new()))),
                Tool("release", new() { Kind = "object" }, new() { Kind = "object" })],
            ToolHandlers = new()
            {
                ["read"] = _ => { calls.Add("read"); return new() { Content = new JsonObject { ["ready"] = variant != "incomplete", ["pages"] = original.DeepClone() } }; },
                ["visit"] = args => { calls.Add(args!["address"]!.ToString()); return new() { Content = new JsonObject { ["visited"] = true } }; },
                ["store"] = args => { calls.Add("store"); written = args!["rows"]!.DeepClone(); return new() { Content = new JsonObject { ["path"] = "observed.xlsx" } }; },
                ["release"] = _ => { calls.Add("release"); return new() { Content = new JsonObject() }; }
            }
        });
        var model = new ExampleModel(variant);
        var engine = new WorkflowEngine { McpClientFactory = factory, LLMClient = model, LlmDefaults = new() { Model = "deterministic" },
            LLMUsageBudget = new(new() { MaxCalls = 4 }) };
        var runtime = new TestRuntime(engine); var state = PlannerFixture.Session();
        state.Catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual); state.Catalog.Policy.RequireExternalConfirmation = false;
        string Operation(string method) => TaskOperations.Describe(state.Catalog.Capabilities.Single(c => c.Method == method)).Id;
        var extraction = new PlanTask { Id = "adapt", Kind = "transform", Mode = "extract", Objective = "Extract the compact observed label before the tab, keeping unchanged IDs per page; retain original full text separately",
            Requires = Ref("observe", "ready"), Inputs = [new("pages", Ref("observe", "pages"))],
            Each = new("pages", "candidates"),
            ResultType = Obj(("candidates", ArrayOf(ArrayOf(Obj(("id", new()), ("label", new())))))) };
        var offered = Flatten(Ref("adapt", "candidates")); var ids = Ref("choose", "ids");
        var plan = new TaskPlan { Root = new()
        {
            Tasks = [
                new() { Id = "observe", Objective = "Read complete original observations", Operation = Operation("read") },
                extraction,
                new() { Id = "choose", Kind = "transform", Mode = "interpret", Objective = "Choose offered IDs for the requested visits",
                    Inputs = [new("candidates", offered)], ResultType = Obj(("ids", new() { Kind = "array", Items = new(), MaxItems = 3 })) },
                new() { Id = "originals", Kind = "foreach", Objective = "Project original record arrays without inference", MaxItems = 20,
                    Items = Ref("observe", "pages"), Body = new() { Outputs = [new("records", Field("records"))] } },
                new() { Id = "visits", Kind = "foreach", Objective = "Perform only the requested visits in selected order", MaxItems = 3,
                    Requires = new() { Kind = "predicate", Predicate = "not_equal", Items = [Lookup(offered, ids, "id"), new() { Kind = "null" }] },
                    Items = Lookup(Flatten(Ref("originals", "records")), ids, "id"), Body = new()
                    {
                        Tasks = [new() { Id = "visit", Objective = "Visit the original observed address", Operation = Operation("visit"), Inputs = [new("address", Field("href"))] }],
                        Outputs = [new("rows", new() { Kind = "array", Items = [Field("text"), Field("id")] })]
                    } },
                new() { Id = "save", Objective = "Write exact supplied rows", Operation = Operation("store"), Inputs = [new("rows",
                    Flatten(new() { Kind = "array", Items = [new() { Kind = "array", Items = [new() { Kind = "array", Items = [Text("text"), Text("id")] }] }, Ref("visits", "rows")] }))] }
            ],
            Outputs = [new("path", Ref("save", "path"))],
            Always = [new() { Id = "close", Objective = "Release resources", Operation = Operation("release") }]
        } };
        // Static compiler/runtime fixture: no planning, business review or approval is claimed.
        var compiled = new TaskPlanCompiler().Compile(plan, state.Catalog);
        Assert.Empty(compiled.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, state.Catalog,
            "composition", true, true, true, true, true, true, true, true, true, true, true, true, true);
        Assert.Empty(calls); Assert.Empty(model.Requests);
        var result = await Execute(yaml, new(), engine);
        Assert.True(result.Success == (variant == "complete"), result.Error?.Message);
        Assert.Equal(originalBytes, original.ToJsonString()); Assert.Equal("release", calls[^1]);
        if (variant == "incomplete")
        {
            Assert.Empty(model.Requests); Assert.Equal(new[] { "read", "release" }, calls); Assert.Null(written);
        }
        else
        {
            Assert.Equal(2, model.Requests.Count); // One mapping and one decision; no copy/row/status inference.
            var decision = model.Requests[1];
            Assert.DoesNotContain("href", decision.Prompt); Assert.DoesNotContain("fixture.invalid", decision.Prompt);
            Assert.DoesNotContain("second line", decision.Prompt);
            var offeredCandidates = ExampleModel.Business(decision)["candidates"]!.AsArray();
            Assert.Equal(20, offeredCandidates.Count);
            for (var i = 0; i < offeredCandidates.Count; i++)
                Assert.Equal("Whole observed block " + i, offeredCandidates[i]!["label"]!.ToString());
            if (variant == "unknown") { Assert.Equal(new[] { "read", "release" }, calls); Assert.Null(written); }
            else
            {
                var selected = new[] { 19, 0, 19 };
                Assert.Equal(selected.Select(i => original[i]!["records"]![0]!["href"]!.ToString()), calls.Skip(1).Take(3));
                Assert.Equal("store", calls[^2]); Assert.Equal("observed.xlsx", result.Outputs!["path"]!.ToString());
                Assert.Equal(new[] { "text", "id" }, written![0]!.AsArray().Select(v => v!.ToString()));
                for (var i = 0; i < selected.Length; i++)
                {
                    var source = original[selected[i]]!["records"]![0]!;
                    Assert.Equal(source["text"]!.ToString(), written[i + 1]![0]!.ToString());
                    Assert.Equal(source["id"]!.ToString(), written[i + 1]![1]!.ToString());
                }
            }
        }
        output.WriteLine($"{variant}: static TaskPlan, no planning or approval; runtime calls={model.Requests.Count}; source pages=20; effects={calls.Count}. Deterministic adapter, not evidence of model compliance.");
    }

    private static McpToolInfo Tool(string name, TaskType input, TaskType result) => new()
    { Name = name, InputSchema = TaskPlanCompiler.TypeSchema(input), OutputSchema = TaskPlanCompiler.TypeSchema(result), EffectKind = "none" };
    private static TaskValue Flatten(TaskValue value) => new() { Kind = "flatten", Items = [value] };
    private static TaskValue Field(string port) => new() { Kind = "field", Port = port, Items = [new() { Kind = "item" }] };

    private sealed class ExampleModel(string variant) : ILLMClient, ILLMCapabilityResolver
    {
        internal readonly List<LLMRequest> Requests = [];
        internal static JsonNode Business(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf("Business data (JSON):", StringComparison.Ordinal) + "Business data (JSON):".Length)..])!;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.StructuredOutputSchema?["properties"]?["script"] is not null)
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = """item.records.map(record=>({id:record.id,label:m.text(record.text,"^[^\\t]+",0)}))""" } });
            Assert.NotNull(request.StructuredOutputSchema?["properties"]?["ids"]);
            var candidates = Business(request)["candidates"]!.AsArray();
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["ids"] = variant == "unknown" ? new JsonArray("not-offered")
                : new JsonArray(candidates[^1]!["id"]!.DeepClone(), candidates[0]!["id"]!.DeepClone(), candidates[^1]!["id"]!.DeepClone()) } });
        }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(12000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
    }
}
