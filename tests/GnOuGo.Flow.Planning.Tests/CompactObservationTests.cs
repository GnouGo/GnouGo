using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactObservationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "records", "observations")]
    [InlineData(true, "entries", "facts")]
    public async Task CompleteCompactionPrecedesGlobalReasoningWithoutRawCollectionLeakage(bool typed, string input, string port)
    {
        var plan = Plan(typed, input, port); var values = Observations(typed, input);
        var model = new Model(input, port); var result = await Execute(plan, values, model);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new[] { "middle_required", "middle_required" }, result.Outputs!["labels"]!.AsArray().Select(v => v!.ToString()));
        Assert.Equal(typed ? 0 : 1, model.Mapping.Count); Assert.Single(model.Interpretation);
        var business = Business(model.Interpretation[0]);
        Assert.Equal(120, business[port]!.AsArray().Count);
        Assert.Equal(new[] { port, "group" }, business.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("irrelevant", model.Interpretation[0].Prompt);
        Assert.DoesNotContain("note", business.ToJsonString());
        Assert.Null(business[port]![0]!["group"]);
        Assert.Equal("ref/60", business[port]![60]!["reference"]!.ToString());
        if (!typed) Assert.DoesNotContain("middle_required", Assert.Single(model.Mapping).Prompt);
        output.WriteLine($"{input}: mapping requests={model.Mapping.Count}, global requests={model.Interpretation.Count}, complete global request bytes={Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(model.Interpretation[0], PlanningJsonContext.Default.LLMRequest))}, estimated input={Model.Estimate(model.Interpretation[0])}, processed items=120");

        // The retained failure's shape: a global transform directly consumes the
        // complete raw collection. The adapter enforces the same fixed allowance.
        var direct = Plan(typed, input, port); direct.Root.Tasks.RemoveAt(0);
        direct.Root.Tasks[0].Inputs[0] = new(port, new() { Kind = "input", Source = input });
        var rejected = new Model(input, port);
        var failed = await Execute(direct, values, rejected);
        Assert.False(failed.Success); Assert.Equal(ErrorCodes.LlmBudgetExceeded, failed.Error!.Code);
        Assert.Empty(rejected.Interpretation); Assert.Empty(rejected.Mapping);
        output.WriteLine($"Raw rejected request: bytes={rejected.RejectedBytes}, input estimate={rejected.RejectedEstimate}; fixed allowance=12000.");
    }

    [Theory]
    [InlineData(true, "entries", "facts")]
    [InlineData(false, "records", "observations")]
    public async Task ConsumerViewsOmitBulkyActionReferencesWithoutLosingTheirSource(bool typed, string input, string port)
    {
        var plan = Plan(typed, input, port); var values = Observations(typed, input);
        foreach (var value in values[input]!.AsArray())
        {
            var business = typed ? value! : JsonNode.Parse(value!["note"]!.ToString())!;
            business["reference"] = business["reference"]!.ToString() + "/" + new string('r', 700);
            business["actionOnly"] = new string('z', 700);
            if (!typed) value!["note"] = business.ToJsonString();
        }
        if (typed) plan.Inputs[0].Type.Items!.Fields.Add(new() { Name = "actionOnly", Type = new() });
        var original = values.ToJsonString();
        var broad = Plan(typed, input, port);
        if (typed) broad.Inputs[0].Type.Items!.Fields.Add(new() { Name = "actionOnly", Type = new() });
        var broadModel = new Model(input, port);
        var rejected = await Execute(broad, values, broadModel);
        Assert.False(rejected.Success); Assert.Equal(ErrorCodes.LlmBudgetExceeded, rejected.Error!.Code);
        Assert.Empty(broadModel.Interpretation);
        var view = new PlanTask { Id = "decision_view", Kind = "foreach", Objective = "Select only the fields needed for the group comparison; retain original references separately",
            MaxItems = 200, Items = ProductTransformationPlan.Ref("compact", port), Body = new()
            { Outputs = [new("decision", new() { Kind = "object", Members = new[] { "label", "group" }
                .Select(n => new TaskOutput(n, new() { Kind = "field", Port = n, Items = [new() { Kind = "item" }] })).ToList() })] } };
        plan.Root.Tasks.Insert(1, view);
        var diagram = WebUtility.HtmlDecode(PlanningReviewFormatter.TaskDiagram(plan));
        Assert.Contains("Exports: decision ← {label: item.label, group: item.group}", diagram);
        if (!typed) Assert.Contains("reference: string", diagram);
        plan.Root.Tasks[^1].Inputs[0] = new(port, ProductTransformationPlan.Ref("decision_view", "decision"));
        // A second business consumer needs the exact references, after the global
        // comparison. This is ordinary wiring and must never enter its prompt.
        plan.Root.Tasks.Add(new() { Id = "later_actions", Kind = "value", Objective = "Retain complete action arguments after comparison",
            DependsOn = ["compare"], Outputs = [new("arguments", ProductTransformationPlan.Ref("compact", port))] });
        plan.Root.Outputs.Add(new("arguments", ProductTransformationPlan.Ref("later_actions", "arguments")));
        var model = new Model(input, port); var result = await Execute(plan, values, model);
        Assert.True(result.Success, result.Error?.Message);
        var request = Assert.Single(model.Interpretation);
        Assert.DoesNotContain("reference", request.Prompt); Assert.DoesNotContain("actionOnly", request.Prompt);
        Assert.Equal(120, Business(request)[port]!.AsArray().Count);
        Assert.Equal(new[] { "middle_required", "middle_required" }, result.Outputs!["labels"]!.AsArray().Select(v => v!.ToString()));
        var arguments = result.Outputs["arguments"]!.AsArray();
        Assert.Equal(120, arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            var source = typed ? values[input]![i]! : JsonNode.Parse(values[input]![i]!["note"]!.ToString())!;
            Assert.Equal(source["reference"]!.ToString(), arguments[i]!["reference"]!.ToString());
        }
        Assert.Equal(original, values.ToJsonString()); Assert.Equal(typed ? 0 : 1, model.Mapping.Count);
        output.WriteLine($"consumer={port}; broad_request_bytes={broadModel.RejectedBytes}; broad_input_estimate={broadModel.RejectedEstimate}; request_bytes={Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest))}; input_estimate={Model.Estimate(request)}; exact_action_records={arguments.Count}");
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("incomplete")]
    [InlineData("oversized_item")]
    [InlineData("missing_observation")]
    [InlineData("still_oversized")]
    public async Task ReductionCannotHideMissingDataOrBypassLimits(string variant)
    {
        var typed = variant == "still_oversized";
        var plan = Plan(typed, "records", "observations"); var values = Observations(typed, "records");
        var records = values["records"]!.AsArray();
        if (variant == "empty") records.Clear();
        if (variant == "incomplete") values["complete"] = false;
        if (variant == "oversized_item") records[0]!["irrelevant"] = new string('x', 30000);
        if (variant == "missing_observation")
        {
            records[60]!["note"] = "{}";
            foreach (var record in records) record!["irrelevant"] = "unused"; // Leave room for the complete failing item in repair context.
        }
        if (variant == "still_oversized") foreach (var record in records) record!["label"] = new string('y', 3000);
        var model = new Model("records", "observations");
        var result = await Execute(plan, values, model);
        if (variant == "empty")
        {
            Assert.True(result.Success, result.Error?.Message); Assert.Empty(model.Mapping);
            Assert.Empty(result.Outputs!["labels"]!.AsArray()); return;
        }
        Assert.False(result.Success); Assert.Empty(model.Interpretation);
        if (variant == "missing_observation")
        {
            Assert.Equal("CONTRACT_UNSATISFIED", result.Error!.Code); Assert.Equal(2, model.Mapping.Count);
            Assert.Contains("60", model.Mapping[1].Prompt);
        }
        else Assert.Empty(model.Mapping);
        if (variant == "still_oversized") Assert.Equal(ErrorCodes.LlmBudgetExceeded, result.Error!.Code);
        if (variant == "incomplete") Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
    }

    [Fact]
    public async Task SinglePlanningCallReachesReviewWithExplicitModesSourcesAndUnchangedSchema()
    {
        var plan = Plan(false, "records", "observations");
        var runtime = new TestRuntime { Proposal = new() { Plan = plan, Requirements = new()
        {
            Summary = "Compare complete observations and return matching labels", Inputs = plan.Inputs,
            Outputs = [new() { Name = "labels", Type = new() { Kind = "array", Items = new() } }],
            Outcomes = [new("compare", "Compare all observed records with the requested group")]
        } } };
        var state = PlannerFixture.Session(); state.Request.Prompt = runtime.Proposal.Requirements.Summary;
        state.Request.Generation.MaxInputTokensPerRequest = 24000;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics));
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        var review = WebUtility.HtmlDecode(PlanningReviewFormatter.TaskDiagram(plan));
        Assert.Contains("transform, extract", review); Assert.Contains("transform, interpret", review);
        Assert.Contains("records ← input records", review); Assert.Contains("observations ← compact.observations", review);
        Assert.Contains("observations: array", review); Assert.Contains("labels: array", review);
        var before = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        plan.Root.Tasks[^1].Inputs[0] = new("observations", new() { Kind = "json", Items = [new() { Kind = "object", Members = [new("raw", new() { Kind = "input", Source = "records" })] }] });
        review = WebUtility.HtmlDecode(PlanningReviewFormatter.TaskDiagram(plan));
        Assert.Contains("observations ← json({raw: input records})", review);
        Assert.DoesNotContain("mapping.dynamic", before); Assert.DoesNotContain("script", before);
    }

    private static TaskPlan Plan(bool typed, string input, string port)
    {
        var row = ProductTransformationPlan.Obj(("label", new()), ("group", new() { Nullable = true }), ("reference", new()));
        var source = typed ? ProductTransformationPlan.Obj(("label", new()), ("group", new() { Nullable = true }), ("reference", new()), ("irrelevant", new()))
            : ProductTransformationPlan.Obj(("note", new()), ("irrelevant", new()));
        var compact = new PlanTask { Id = "compact", Kind = "transform", Mode = "extract", Each = new(input, port),
            Objective = "Extract each observed label, group and reference in order. Retain duplicates and explicit nulls.",
            Inputs = [new(input, new() { Kind = "input", Source = input })],
            ResultType = ProductTransformationPlan.Obj((port, new() { Kind = "array", Items = row })) };
        if (typed)
        {
            compact = new() { Id = "compact", Kind = "foreach", Objective = "Select declared business fields without irrelevant data", MaxItems = 200,
                Items = new() { Kind = "input", Source = input }, Body = new() { Outputs = [new(port, new() { Kind = "object",
                    Members = new[] { "label", "group", "reference" }.Select(n => new TaskOutput(n, new() { Kind = "field", Port = n, Items = [new() { Kind = "item" }] })).ToList() })] } };
        }
        compact.Requires = new() { Kind = "input", Source = "complete" };
        return new() { Inputs = [new() { Name = input, Type = new() { Kind = "array", Items = source } }, new() { Name = "complete", Type = new() { Kind = "boolean" } }],
            Root = new() { Tasks = [compact, new() { Id = "compare", Kind = "transform", Objective = "Globally compare all observations against group, preserving matching labels in source order and duplicates.",
                Inputs = [new(port, ProductTransformationPlan.Ref("compact", port)), new("group", ProductTransformationPlan.Text("target"))],
                ResultType = ProductTransformationPlan.Obj(("labels", new() { Kind = "array", Items = new() })) }], Outputs = [new("labels", ProductTransformationPlan.Ref("compare", "labels"))] } };
    }

    private static JsonObject Observations(bool typed, string input) => new() { ["complete"] = true, [input] = new JsonArray(Enumerable.Range(0, 120).Select(i =>
    {
        var business = new JsonObject { ["label"] = i is 60 or 61 ? "middle_required" : "row/" + i,
            ["group"] = i == 0 ? null : i is 60 or 61 ? "target" : "other", ["reference"] = "ref/" + i };
        var item = typed ? business : new JsonObject { ["note"] = business.ToJsonString() };
        item["irrelevant"] = new string('x', 1500); return (JsonNode)item;
    }).ToArray()) };

    private static async Task<GnOuGo.Flow.Core.Models.RunResult> Execute(TaskPlan plan, JsonObject values, Model model)
    {
        var engine = new WorkflowEngine { LLMClient = model, LlmDefaults = new() { Model = "deterministic" } };
        var catalog = await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask).DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics); Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        return await engine.ExecuteAsync(doc.Workflows[doc.Entrypoint!], values, PlannerFixture.Ct);
    }

    private static JsonNode Business(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf("Business data (JSON):", StringComparison.Ordinal) + "Business data (JSON):".Length)..])!;
    private sealed class Model(string input, string port) : ILLMClient, ILLMCapabilityResolver
    {
        internal readonly List<LLMRequest> Mapping = [], Interpretation = [];
        internal int RejectedBytes, RejectedEstimate;
        internal static int Estimate(LLMRequest request) => PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject());
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            if (Estimate(request) > 12000)
            {
                RejectedEstimate = Estimate(request); RejectedBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
                throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetExceeded, "Complete request exceeds the fixed test allowance before dispatch.");
            }
            if (request.StructuredOutputSchema?["properties"]?["script"] is not null)
            {
                Mapping.Add(request); return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = "({label:m.parse(source." + input + ".note).label,group:m.parse(source." + input + ".note).group,reference:m.parse(source." + input + ".note).reference})" } });
            }
            Interpretation.Add(request); var data = Business(request);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["labels"] = new JsonArray(data[port]!.AsArray()
                .Where(r => r!["group"]?.ToString() == data["group"]!.ToString()).Select(r => r!["label"]!.DeepClone()).ToArray()) } });
        }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(12000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
    }
}
