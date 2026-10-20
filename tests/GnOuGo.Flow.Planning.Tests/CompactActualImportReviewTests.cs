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
            Assert.Equal("/tasks/consumer/outputs/amount", imported["consumerLocation"]!.ToString());
            Assert.Equal("/root", imported["scope"]!.ToString());
            Assert.Equal("number", imported["schema"]!["type"]!.ToString());
            Assert.Equal("field", imported["binding"]!["kind"]!.ToString());
            Assert.Equal("amount", imported["binding"]!["port"]!.ToString());
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

    [Theory]
    [InlineData("value")]
    [InlineData("transform")]
    public async Task ReviewDistinguishesAnImportedOperandFromItsComputedConsumerResult(string kind)
    {
        var (state, runtime) = await Ready();
        state.Plan!.Inputs = [new() { Name = "record", Type = new() { Kind = "object", Fields =
            [new() { Name = "payload", Type = new() { Kind = "object", Fields =
                [new() { Name = "amount", Type = new() { Kind = "number" } },
                 new() { Name = "unused", Type = new() { Kind = "string", Enum = [new string('z', 8000)] } }] } }] } }];
        state.Requirements!.Inputs = state.Plan.Inputs;
        Select(state, kind);
        var operand = Field(Field(Input("record"), "payload"), "amount");
        var calculation = new TaskValue { Kind = "arithmetic", Text = "multiply",
            Items = [operand, new() { Kind = "number", Number = 2 }] };
        var task = kind == "value"
            ? new PlanTask { Id = "consumer", Kind = kind, Objective = "Double the supplied amount.", Outputs = [new("adjusted", calculation)] }
            : new PlanTask { Id = "consumer", Kind = kind, Mode = "extract", Objective = "Extract the supplied adjusted amount.",
                Inputs = [new("adjusted", calculation)], ResultType = new() { Kind = "object",
                    Fields = [new() { Name = "adjusted", Type = new() { Kind = "number" } }] } };
        var baseline = PlanningConstruction.Hash(state.Plan);
        Stage(state, new() { RequirementIds = ["req_0"], Tasks = [task] });
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            var imported = Assert.Single(context["interfaces"]!.AsArray())!;
            Assert.Equal("/tasks/consumer/" + (kind == "value" ? "outputs" : "inputs") + "/adjusted",
                imported["consumerLocation"]!.ToString());
            Assert.False(imported.AsObject().ContainsKey("location"));
            Assert.False(imported.AsObject().ContainsKey("value"));
            Assert.Equal("number", imported["schema"]!["type"]!.ToString());
            Assert.True(JsonNode.DeepEquals(PlanningJsonTransport.TaskPlanPart(
                JsonSerializer.SerializeToNode(operand, PlanningJsonContext.Default.TaskValue)), imported["binding"]));
            Assert.DoesNotContain("unused", context["interfaces"]!.ToJsonString(), StringComparison.Ordinal);
            var expression = context["draft"]!["tasks"]![0]![kind == "value" ? "outputs" : "inputs"]![0]!["value"]!;
            Assert.Equal("arithmetic", expression["kind"]!.ToString());
            Assert.Equal("multiply", expression["text"]!.ToString());
            Assert.Equal(2d, expression["items"]![1]!["number"]!.GetValue<double>());
            Assert.Contains("not the produced result", request.Prompt, StringComparison.Ordinal);
            var evidenceAnchors = FindingProperties(request.StructuredOutputSchema!.AsObject())["evidence"]!["items"]!["enum"]!.AsArray();
            Assert.Contains(evidenceAnchors, pointer => pointer!.ToString() == "/interfaces/0");
            Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
            return Wire(request, Reviewed());
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task InterpretationReviewUsesTheRuntimeInstructionAndPreservesResultConstraints()
    {
        var (state, runtime) = await Ready();
        state.Plan!.Inputs = [new() { Name = "text", Type = new() { Kind = "string" } }];
        state.Requirements!.Inputs = state.Plan.Inputs;
        Select(state, "transform");
        var task = new PlanTask
        {
            Id = "consumer", Kind = "transform", Mode = "interpret",
            Objective = "Compare the supplied text with the requested criteria and report every discrepancy.",
            Inputs = [new("text", Input("text"))],
            ResultType = new() { Kind = "object", Fields = [new() { Name = "discrepancies",
                Type = new() { Kind = "array", Items = new() { Kind = "string" }, MaxItems = 0 } }] }
        };
        var baseline = PlanningConstruction.Hash(state.Plan);
        var candidate = Stage(state, new() { RequirementIds = ["req_0"], Tasks = [task] });
        var compiled = new TaskPlanCompiler().Compile(candidate.Plan, state.Catalog!, state.Request);
        Assert.Empty(compiled.Diagnostics);
        Assert.NotNull(compiled.Graph);
        var steps = Assert.Single(compiled.Graph.Workflows).Steps;
        var prompt = Assert.Single(steps, step => step.Type == "template.render");
        var data = Assert.Single(prompt.Input.Members, member => member.Name == "data").Value;
        Assert.Equal(task.Objective, Assert.Single(data.Members, member => member.Name == "instruction").Value.Text);
        var inference = Assert.Single(steps, step => step.Type == "llm.call");
        Assert.NotNull(inference.StructuredOutput);
        Assert.Equal(0, inference.StructuredOutput.Schema.Contract!["properties"]!["discrepancies"]!["maxItems"]!.GetValue<int>());
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            var draft = Assert.Single(context["draft"]!["tasks"]!.AsArray())!;
            Assert.Equal("interpret", draft["mode"]!.ToString());
            Assert.Equal(task.Objective, draft["objective"]!.ToString());
            Assert.Equal("input", Assert.Single(draft["inputs"]!.AsArray())!["value"]!["kind"]!.ToString());
            Assert.Equal(0, draft["resultType"]!["fields"]![0]!["type"]!["maxItems"]!.GetValue<int>());
            Assert.Contains("interpret uses its objective as the runtime LLM instruction", request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("the draft expression computes the result", request.Prompt, StringComparison.Ordinal);
            return Wire(request, new()
            {
                ["findings"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "inconsistency", ["location"] = "/draft/tasks/0",
                    ["evidence"] = new JsonArray("/draft/tasks/0"),
                    ["explanation"] = "The zero-length result contract cannot report discrepancies required by the objective.",
                    ["uncertainty"] = "The runtime interpretation has not executed."
                }), ["uncertainty"] = "The result constraint contradicts the stated task.", ["evidenceRequests"] = null
            });
        };
        state = await Advance(state, runtime);
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction!.Checkpoints);
    }

    [Fact]
    public async Task ChoiceOperandStillIncludesItsExactAcceptedChoiceEvidence()
    {
        var (state, runtime) = await Ready();
        state.Plan!.Choices = [new() { Id = "amount", Question = "Choose the amount.", Type = new() { Kind = "number" },
            Recommended = "first", Selected = "first", Alternatives =
            [new("first", "First amount", new() { Kind = "number", Number = 3 }),
             new("second", "Second amount", new() { Kind = "number", Number = 5 })] }];
        Select(state, "value");
        Stage(state, new() { RequirementIds = ["req_0"], Tasks =
            [new() { Id = "consumer", Kind = "value", Objective = "Return the selected amount.",
                Outputs = [new("amount", new() { Kind = "choice", Source = "amount" })] }] });
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            Assert.Equal("choice", Assert.Single(context["interfaces"]!.AsArray())!["binding"]!["kind"]!.ToString());
            var choice = Assert.Single(context["choices"]!.AsArray())!;
            Assert.Equal("amount", choice["id"]!.ToString());
            Assert.Equal("first", choice["selected"]!.ToString());
            return Wire(request, Reviewed());
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(state.Construction!.Checkpoints);
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
        Assert.Contains(interfaces, value => value!["consumerLocation"]!.ToString() == "/tasks/normal/requires" &&
            value["schema"]!["type"]!.ToString() == "boolean");
        Assert.Contains(interfaces, value => value!["consumerLocation"]!.ToString() == "/tasks/cleanup/outputs/ran" &&
            value["phase"]!.ToString() == "always");
        Assert.Contains(interfaces, value => value!["consumerLocation"]!.ToString() == "/root/outputs/final" &&
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
        Assert.Contains(interfaces, value => value!["binding"]!["kind"]!.ToString() == "item" &&
            value["schema"]!["type"]!.ToString() == "number");
        Assert.Contains(interfaces, value => value!["binding"]!["kind"]!.ToString() == "index" &&
            value["schema"]!["type"]!.ToString() == "integer");
        Assert.Contains(interfaces, value => value!["consumerLocation"]!.ToString() == "/tasks/element/requires" &&
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviouslyIssuedJudgeKeepsItsExactSchemaContextAndAccounting(bool historical)
    {
        var (state, runtime) = await Ready();
        if (historical) Historical(state);
        Select(state, "value");
        Stage(state, new() { RequirementIds = ["req_0"], Tasks =
            [new() { Id = "consumer", Kind = "value", Objective = "Return a value.", Outputs = [new("result", new() { Kind = "number", Number = 1 })] }] });
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall?.Purpose == PlanningConstruction.LocalPurpose));
        var issued = saved.PendingCall!.Request;
        Assert.Equal(historical, FindingProperties(issued.StructuredOutputSchema!.AsObject())["location"]!["enum"] is null);
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
