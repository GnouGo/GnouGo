using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactActualImportReviewTests
{
    [Fact]
    public async Task OrdinaryDraftDerivesTheExactProjectedInputAtItsConsumerBeforeReview()
    {
        var (state, runtime) = await Ready();
        state.Plan!.Inputs = [new() { Name = "record", Type = new() { Kind = "object", Fields =
            [new() { Name = "amount", Type = new() { Kind = "number" } },
             new() { Name = "unused", Type = new() { Kind = "string", Enum = [new string('z', 8000)] } }] } }];
        state.Requirements!.Inputs = state.Plan.Inputs;
        Select(state, "value");
        var baseline = PlanningConstruction.Hash(state.Plan);
        var extension = new PlanningConstructionExtension { RequirementIds = ["req_0"], Tasks =
            [new() { Id = "consumer", Kind = "value", Objective = "Preserve the selected amount.",
                Outputs = [new("amount", Field(Input("record"), "amount"))] }] };
        runtime.Respond = (request, _) => Wire(request, new()
        {
            ["baseHash"] = Context(request)["baseHash"]!.DeepClone(),
            ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension)
        });
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.NotNull(state.Construction!.Candidate);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            var imported = Assert.Single(context["interfaces"]!.AsArray())!;
            Assert.Equal("/tasks/consumer/outputs/amount", imported["location"]!.ToString());
            Assert.Equal("/root", imported["scope"]!.ToString());
            Assert.Equal("number", imported["schema"]!["type"]!.ToString());
            Assert.Equal("field", imported["value"]!["kind"]!.ToString());
            Assert.Equal("amount", imported["value"]!["port"]!.ToString());
            Assert.DoesNotContain("unused", context["interfaces"]!.ToJsonString(), StringComparison.Ordinal);
            Assert.Null(context["step"]!["inputs"]);
            var measurement = PlanningRequestMeasurements.Measure(request, purpose, 8000);
            Assert.InRange(measurement.EstimatedInputTokens, 1, 8000);
            Assert.Equal(measurement.CompleteRequestBytes, measurement.Components.Sum(component => component.SerializedBytes));
            return Wire(request, Reviewed());
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Empty(state.Construction.Compact!.RejectedSteps);
    }

    [Fact]
    public async Task CleanupPredicatesDependenciesAndExportsUseTheirActualConsumerScopes()
    {
        var (state, _) = await Ready();
        state.Plan!.Inputs = [new() { Name = "allowed", Type = new() { Kind = "boolean" } }];
        state.Requirements!.Inputs = state.Plan.Inputs;
        state.Plan.Root.Tasks = [new() { Id = "accepted", Kind = "value", Objective = "Retain the amount.",
            Outputs = [new("amount", new() { Kind = "number", Number = 3 })] }];
        Select(state, "value");
        var candidate = Stage(state, new()
        {
            RequirementIds = ["req_0"],
            Tasks = [new() { Id = "normal", Kind = "value", Objective = "Copy the amount when allowed.",
                DependsOn = ["accepted"], Requires = Input("allowed"), Outputs = [new("amount", Output("accepted", "amount"))] }],
            Always = [new() { Id = "cleanup", Kind = "value", Objective = "Observe cleanup availability.",
                Outputs = [new("ran", new() { Kind = "present", Source = "accepted" })] }],
            Outputs = [new("final", Output("accepted", "amount"))]
        });
        var interfaces = PlanningCompactConstruction.ActualImportedInterfaces(state);
        Assert.Contains(interfaces, value => value!["location"]!.ToString() == "/tasks/normal/requires" &&
            value["schema"]!["type"]!.ToString() == "boolean");
        Assert.Contains(interfaces, value => value!["location"]!.ToString() == "/tasks/cleanup/outputs/ran" &&
            value["phase"]!.ToString() == "always");
        Assert.Contains(interfaces, value => value!["location"]!.ToString() == "/root/outputs/final" &&
            value["phase"]!.ToString() == "outputs");
        Assert.Contains("accepted", candidate.ImportedTaskIds);
        Assert.Empty(state.Construction!.Compact!.CurrentStep!.DependencyTaskIds);
    }

    [Fact]
    public async Task LoopBodyReferencesResolveIterationTypesAndLegalAncestorCaptures()
    {
        var (state, _) = await Ready();
        state.Plan!.Inputs =
        [
            new() { Name = "values", Type = new() { Kind = "array", Items = new() { Kind = "number" } } },
            new() { Name = "allowed", Type = new() { Kind = "boolean" } }
        ];
        state.Requirements!.Inputs = state.Plan.Inputs;
        state.Plan.Root.Tasks = [new() { Id = "loop", Kind = "foreach", Objective = "Visit declared values.",
            Items = Input("values"), Body = new() }];
        state.Construction!.OpenScopes.Add("/tasks/loop/body");
        Select(state, "value", "/tasks/loop/body");
        Stage(state, new()
        {
            Scope = "/tasks/loop/body", RequirementIds = ["req_0"], Tasks = [new()
            {
                Id = "element", Kind = "value", Objective = "Preserve each permitted item and its index.",
                Requires = Input("allowed"), Outputs =
                [
                    new("item", new() { Kind = "item" }),
                    new("index", new() { Kind = "index" })
                ]
            }]
        });
        var interfaces = PlanningCompactConstruction.ActualImportedInterfaces(state);
        Assert.Equal(3, interfaces.Count);
        Assert.All(interfaces, value => Assert.Equal("/tasks/loop/body", value!["scope"]!.ToString()));
        Assert.Contains(interfaces, value => value!["value"]!["kind"]!.ToString() == "item" &&
            value["schema"]!["type"]!.ToString() == "number");
        Assert.Contains(interfaces, value => value!["value"]!["kind"]!.ToString() == "index" &&
            value["schema"]!["type"]!.ToString() == "integer");
        Assert.Contains(interfaces, value => value!["location"]!.ToString() == "/tasks/element/requires" &&
            value["schema"]!["type"]!.ToString() == "boolean");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompilerStillRejectsIsolatedCapturesAndOpenContainerResults(bool openContainer)
    {
        var (state, _) = await Ready();
        PlanningConstructionExtension extension;
        if (openContainer)
        {
            state.Plan!.Root.Tasks = [new() { Id = "unfinished", Kind = "sequence", Objective = "Open work.", Body = new() }];
            state.Construction!.OpenScopes.Add("/tasks/unfinished/body");
            Select(state, "value");
            extension = new() { RequirementIds = ["req_0"], Tasks = [new()
                { Id = "consumer", Kind = "value", Objective = "Read unfinished work.", Outputs = [new("result", Output("unfinished", "result"))] }] };
        }
        else
        {
            state.Plan!.Inputs = [new() { Name = "caller", Type = new() { Kind = "number" } }];
            state.Requirements!.Inputs = state.Plan.Inputs;
            state.Plan.Groups = [new() { Id = "isolated" }];
            state.Construction!.OpenScopes.Add("/groups/isolated/body");
            Select(state, "close", "/groups/isolated/body");
            extension = new() { Scope = "/groups/isolated/body", RequirementIds = ["req_0"],
                Outputs = [new("result", Input("caller"))] };
        }
        var baseline = PlanningConstruction.Hash(state.Plan);
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        state.Construction!.Candidate = candidate;
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ActualImportedInterfaces(state));
        Assert.NotEmpty(failure.Diagnostics);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task HistoricalImportWhitelistAndExplicitRevisionAuthorityRemainDistinct()
    {
        var (state, _) = await Ready();
        state.Plan!.Inputs = [new() { Name = "value", Type = new() { Kind = "number" } }];
        state.Requirements!.Inputs = state.Plan.Inputs;
        Select(state, "value");
        var candidate = PlanningConstruction.Apply(state, new() { RequirementIds = ["req_0"], Tasks =
            [new() { Id = "consumer", Kind = "value", Objective = "Copy value.", Outputs = [new("result", Input("value"))] }] });
        Assert.True(PlanningCompactConstruction.UsesDerivedImports(state, candidate));
        candidate.AuthorizedPaths = ["/tasks/consumer/outputs/result"];
        Assert.False(PlanningCompactConstruction.UsesDerivedImports(state, candidate));
        candidate.AuthorizedPaths = null;
        Historical(state);
        Assert.False(PlanningCompactConstruction.UsesDerivedImports(state, candidate));
        Assert.Equal("CONSTRUCTION_STEP_INPUT_INVALID", Assert.Single(Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.ValidateExtension(state, candidate)).Diagnostics).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssuedObjectAnchorsAcceptGroundedFindingsAndRejectInventedSuffixes(bool invented)
    {
        var (state, runtime) = await Ready();
        Select(state, "value");
        Stage(state, new() { RequirementIds = ["req_0"], Tasks =
            [new() { Id = "consumer", Kind = "value", Objective = "Return a value.", Outputs = [new("result", new() { Kind = "number", Number = 1 })] }] });
        var accepted = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (request, _) =>
        {
            var properties = FindingProperties(request.StructuredOutputSchema!.AsObject());
            Assert.Contains(properties["location"]!["enum"]!.AsArray(), node => node!.ToString() == "/draft/tasks/0");
            var anchors = properties["evidence"]!["items"]!["enum"]!.AsArray();
            Assert.Contains(anchors, node => node!.ToString() == "/requirements/0");
            Assert.DoesNotContain(anchors, node => node!.ToString().StartsWith("/taskEvidence", StringComparison.Ordinal));
            // Raw wire response exercises actual issued-schema rejection rather
            // than allowing a fixture transporter to normalize the enum.
            return new() { Json = new JsonObject
            {
                ["findings"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "inconsistency", ["location"] = "/draft/tasks/0",
                    ["evidence"] = new JsonArray(invented ? "/draft/tasks/0/parallel_status/0" : "/requirements/0"),
                    ["explanation"] = "The returned value does not satisfy the original requested result.",
                    ["uncertainty"] = "Execution remains unverified."
                }), ["uncertainty"] = "Only the current fragment was reviewed.", ["evidenceRequests"] = null
            } };
        };
        state = await Advance(state, runtime);
        Assert.Equal(invented ? "CONSTRUCTION_REVIEW_INCONCLUSIVE" : "CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
        if (invented)
        {
            var calls = runtime.Calls.Count;
            state = await Advance(PlannerFixture.Clone(state), runtime);
            Assert.Equal(calls, runtime.Calls.Count);
            Assert.Equal(PlanningStatus.Stopped, state.Status);
        }
    }

    [Fact]
    public async Task PreviouslyIssuedJudgeKeepsItsExactFreePointerSchemaAndAccounting()
    {
        var (state, runtime) = await Ready();
        Historical(state);
        Select(state, "value");
        Stage(state, new() { RequirementIds = ["req_0"], Tasks =
            [new() { Id = "consumer", Kind = "value", Objective = "Return a value.", Outputs = [new("result", new() { Kind = "number", Number = 1 })] }] });
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall?.Purpose == PlanningConstruction.LocalPurpose));
        var issued = saved.PendingCall!.Request;
        Assert.Null(FindingProperties(issued.StructuredOutputSchema!.AsObject())["location"]!["enum"]);
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var context = saved.Construction!.PendingReviewContext!.ToJsonString();
        var calls = saved.ModelCalls; var corrections = saved.ReplanAttempts;
        saved.Status = PlanningStatus.Generating;
        runtime.Calls.Clear();
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return Wire(request, Reviewed());
        };
        state = await Advance(saved, runtime);
        Assert.Single(runtime.Calls);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(corrections, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Contains(context, runtime.Checkpoints.Select(checkpoint => checkpoint.Construction?.PendingReviewContext?.ToJsonString()));
    }

    private static PlanningConstructionCandidate Stage(PlanningSession state, PlanningConstructionExtension extension)
    {
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        state.Construction!.Candidate = candidate;
        return candidate;
    }
    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready()
    {
        const string prompt = "Preserve the declared business value.";
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, new()
        {
            ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(new JsonObject { ["text"] = prompt, ["kind"] = "requirement", ["interfaces"] = "none" })
        }) };
        var state = await Advance(new() { Request = new() { TenantId = "actual-imports", Prompt = prompt,
            Options = new() { ["automatic_correction"] = true } } }, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(PlanningConstructionPhasePolicies.IsFresh(state.Request));
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new();
        return (state, runtime);
    }
    private static void Select(PlanningSession state, string kind, string scope = "/root") =>
        state.Construction!.Compact!.CurrentStep = new()
        {
            Id = "selected", Kind = kind, Scope = scope, BaseHash = PlanningConstruction.Hash(state.Plan),
            Objective = "Preserve the declared business value.", RequirementIds = ["req_0"], DiscoveryComplete = true
        };
    private static void Historical(PlanningSession state)
    {
        state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        state.Construction!.Limits.PhasePolicy = null;
    }
    private static TaskValue Input(string name) => new() { Kind = "input", Source = name };
    private static TaskValue Output(string id, string port) => new() { Kind = "output", Source = id, Port = port };
    private static TaskValue Field(TaskValue value, string port) => new() { Kind = "field", Port = port, Items = [value] };
    private static JsonObject FindingProperties(JsonObject schema) => schema["properties"]!["findings"]!["anyOf"]!.AsArray()
        .Single(node => node?["type"]?.ToString() == "array")!["items"]!["properties"]!.AsObject();
    private static JsonObject Reviewed() => new() { ["findings"] = new JsonArray(), ["uncertainty"] = "Execution unverified.", ["evidenceRequests"] = null };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
