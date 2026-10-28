using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactSelectionScopeTests
{
    [Fact]
    public async Task SelectionShowsEmptyIsolatedParametersAndRootOwnershipWithinCompleteRequestLimit()
    {
        var state = await Ready();
        var original = Snapshot(state);
        var context = PlanningCompactConstruction.SelectionContext(state);
        var group = context["scopes"]!.AsArray().Single(scope => scope!["kind"]?.ToString() == "group")!;
        Assert.True(group["isolated"]!.GetValue<bool>());
        Assert.Equal(0, group["inputCount"]!.GetValue<int>());
        Assert.Empty(group["inputs"]!.AsArray());
        Assert.Equal("group:isolated", group["evidence"]!.ToString());
        var input = context["interfaces"]!.AsArray().Single(item => item!["input"]?.ToString() == "query")!;
        Assert.Equal("/root", input["scope"]!.ToString());
        Assert.Equal("input:query", input["evidence"]!.ToString());
        var producer = context["interfaces"]!.AsArray().Single(item => item!["task"]?.ToString() == "copied")!;
        Assert.Equal("/root", producer["scope"]!.ToString());
        var nested = context["scopes"]!.AsArray().Single(scope => scope!["path"]!.ToString() == "/tasks/nested/body")!;
        Assert.Equal("/groups/isolated/body", nested["inputScope"]!.ToString());
        Assert.Null(nested["inputs"]);

        var request = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state),
            context, PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose));
        var limit = PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose).MaxInputTokens;
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningCompactConstruction.SelectPurpose, limit).EstimatedInputTokens, 1, limit);
        Assert.Equal(original, Snapshot(state));
        // Caller ownership remains explicit before the first TaskPlan exists too.
        state.Plan = null;
        Assert.Equal("/root", PlanningCompactConstruction.InterfaceDirectory(state, null, 0)[0]!["scope"]!.ToString());
    }

    [Theory]
    [InlineData("input", "/groups/isolated/body")]
    [InlineData("output", "/groups/isolated/body")]
    [InlineData("input", "/tasks/nested/body")]
    [InlineData("output", "/tasks/nested/body")]
    public async Task RejectedImportsIdentifyTheBindingAndIsolatedScopeWithoutChangingAcceptedWork(string kind, string scope)
    {
        var state = await Ready();
        var original = Snapshot(state);
        var step = Step(scope, new() { Kind = "string", Text = "Valid local literal" }, CallerReference(kind));
        var error = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, step));
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("CONSTRUCTION_STEP_INPUT_INVALID", diagnostic.Code);
        Assert.Equal("/step/inputs/1", diagnostic.Location);
        Assert.Contains(scope, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(kind == "input" ? "'query'" : "'copied' port 'value'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("declaring scope is '/root'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("declares 0 inputs: []", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("group:isolated", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(original, Snapshot(state));
    }

    [Theory]
    [InlineData("/root", "input")]
    [InlineData("/tasks/lexical/body", "input")]
    [InlineData("/tasks/lexical/body", "output")]
    [InlineData("/groups/isolated/body", "parameter")]
    [InlineData("/tasks/nested/body", "parameter")]
    public async Task DirectCallerImportsLexicalCapturesAndDeclaredGroupParametersKeepTheirExactTypes(string scope, string kind)
    {
        var state = await Ready();
        state.Plan!.Groups[0].Inputs.Add(new() { Name = "local", Type = new() { Kind = "string" } });
        var original = Snapshot(state);
        var value = kind == "parameter" ? new TaskValue { Kind = "input", Source = "local" } : CallerReference(kind);
        var step = Step(scope, value);
        PlanningCompactConstruction.ValidateSelection(state, step);
        var imported = Assert.Single(PlanningCompactConstruction.ImportedInterfaces(state, step));
        Assert.Equal("string", imported!["schema"]!["type"]!.ToString());
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.TaskValue), imported["value"]));
        Assert.Equal(original, Snapshot(state));
    }

    [Fact]
    public async Task ImportDiagnosticsDoNotAttributeUnrelatedAcceptedPlanFailuresToASelectedBinding()
    {
        var state = await Ready();
        state.Plan!.Root.Tasks.Insert(0, new() { Id = "unrelated", Kind = "value", Objective = "An invalid retained fixture task.",
            Outputs = [new("value", new() { Kind = "output", Source = "missing-unrelated-producer", Port = "value" })] });
        var error = Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.ValidateSelection(state, Step("/root", CallerReference("input"))));
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("/step/inputs", diagnostic.Location);
        Assert.DoesNotContain("missing-unrelated-producer", diagnostic.Message, StringComparison.Ordinal);
    }


    [Fact]
    public async Task CorrectedSelectionChargesOneCorrectionAndPreservesAcceptedGroupAndCallerBindings()
    {
        var state = await Ready();
        var plan = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var calls = state.ModelCalls;
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, Response(state,
            Step("/groups/isolated/body", CallerReference("output")))) };
        state = await Advance(state, runtime);
        Assert.Equal("/step/inputs/0", Assert.Single(state.Diagnostics).Location);
        Assert.Null(state.Construction!.Compact!.CurrentStep);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(calls + 1, state.ModelCalls);
        runtime.Respond = (request, _) =>
        {
            Assert.Contains("declares 0 inputs: []", request.Prompt, StringComparison.Ordinal);
            return Wire(request, Response(state, Step("/root", CallerReference("input"))));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(calls + 2, state.ModelCalls);
        Assert.Equal("/root", state.Construction!.Compact!.CurrentStep!.Scope);
        Assert.Equal("query", Assert.Single(state.Construction.Compact.CurrentStep.Inputs).Source);
        Assert.Empty(state.Plan!.Groups[0].Inputs);
        Assert.Equal(plan, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Fact]
    public async Task PreviouslyIssuedSelectionKeepsItsExactPromptSchemaAndCallAccounting()
    {
        var state = await Ready();
        var context = PlanningCompactConstruction.SelectionContext(state);
        var issued = Complete(state, PlanningCompactConstruction.Request("Retained instructions: select one exact business step.",
            context, PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose), out var evidenceAddresses);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.SelectPurpose, Request = issued,
            EvidenceAddresses = evidenceAddresses };
        state.ModelCalls++;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var calls = state.ModelCalls;
        var plan = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var response = Wire(issued, Response(state, Step("/root", CallerReference("input"))));
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return response; // Receipt for the exact previously issued request.
        } };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal("query", Assert.Single(state.Construction!.Compact!.CurrentStep!.Inputs).Source);
        Assert.Equal(plan, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
    }

    private static PlanningConstructionStep Step(string scope, params TaskValue[] inputs) => new()
    {
        Scope = scope, Kind = null, Objective = "Read the supplied value using an inspected capability.",
        RequirementIds = ["req_0"], Inputs = inputs.ToList(), ExpectedOutputs = ["Observed value"]
    };

    private static TaskValue CallerReference(string kind) => kind == "input"
        ? new() { Kind = "input", Source = "query" }
        : new() { Kind = "output", Source = "copied", Port = "value" };

    private static JsonObject Response(PlanningSession state, PlanningConstructionStep step) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["step"] = JsonSerializer.SerializeToNode(step, PlanningJsonContext.Default.PlanningConstructionStep),
        ["evidenceRequests"] = null, ["clarifications"] = null
    };

    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };

    private static LLMRequest Complete(PlanningSession state, LLMRequest request) => Complete(state, request, out _);

    private static LLMRequest Complete(PlanningSession state, LLMRequest request, out PlanningEvidenceAddresses? evidenceAddresses) =>
        PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.SelectPurpose, request.Prompt,
            request.StructuredOutputSchema!.AsObject(), 8192, null, out evidenceAddresses);

    private static string Snapshot(PlanningSession state) => JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);

    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);

    private static async Task<PlanningSession> Ready()
    {
        const string objective = "Preserve the supplied value through the required boundaries.";
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            return Wire(request, new()
            {
                ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(),
                ["clauses"] = new JsonArray(new JsonObject { ["text"] = objective, ["kind"] = "requirement", ["interfaces"] = "none" })
            });
        } };
        // These cases isolate scope/import correction, with the whole small
        // directory offered. The hard ceiling remains the normal 6k limit.
        var state = await Advance(PlannerFixture.Capture(new() { Request = new() { TenantId = "selection-scopes", Prompt = objective,
            Options = new() { ["automatic_correction"] = true } }, Construction = new() { Limits = new() { PhasePolicy = new()
            { Navigation = new() { TargetInputTokens = 6000, MaxInputTokens = 6000 } } } } }), runtime);
        Assert.Empty(state.Diagnostics);
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Requirements!.Inputs = [new() { Name = "query", Type = new() { Kind = "string" } }];
        // Seed accepted, compiler-valid boundaries; the tested selection may not rewrite them.
        state.Plan = new()
        {
            Inputs = [new() { Name = "query", Type = new() { Kind = "string" } }],
            Root = new() { Tasks = [
                new() { Id = "copied", Kind = "value", Objective = "Retained caller binding.", Outputs = [new("value", CallerReference("input"))] },
                new() { Id = "lexical", Kind = "sequence", Objective = "A lexical child.", Body = new() }
            ] },
            Groups = [new() { Id = "isolated", Body = new() { Tasks = [
                new() { Id = "nested", Kind = "sequence", Objective = "A lexical child of the isolated group.", Body = new() }
            ] } }]
        };
        state.Construction!.OpenScopes = ["/root", "/groups/isolated/body", "/tasks/lexical/body", "/tasks/nested/body"];
        return state;
    }
}
