using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactActualImportReviewTests
{
    [Theory]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public async Task RetainedCreationTargetAllowsLaterCleanupButNotAnUnfulfilledClosureClaim(int presentation, bool retainedReview)
    {
        const string target = "workflows/owned-resource";
        var (state, runtime) = await Ready("Create the isolated resource at workflows/owned-resource and remove it even after partial failure.", presentation);
        var capability = new PlanningCapability
        {
            Id = "create_resource", Version = "v1", Kind = "tool", StepType = "mcp.call",
            Server = "resource", Method = "create", EffectKind = "write",
            Description = "Create a workflow-owned resource at target. Retain the declared target independently of successful completion for cleanup after partial failure. Cleanup requires the same target and workflow ownership.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
                { ["target"] = new JsonObject { ["type"] = "string", ["const"] = target } },
                ["required"] = new JsonArray("target"), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject
                { ["resource"] = new JsonObject { ["type"] = "string" } }, ["additionalProperties"] = false }
        };
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"], Capabilities = [capability] };
        state.Discovery.Resolved = [capability];
        var originalContract = JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability);
        state.Requirements!.Outcomes.Add(new("req_1", "Create the isolated resource at workflows/owned-resource."));
        state.Construction!.Requirements.Add(new() { Id = "req_1" });
        state.Construction.Compact!.RequirementSources["req_1"] = state.Construction.Compact.RequirementSources["req_0"].ToList();
        Select(state, "operation");
        state.Construction!.Compact!.CurrentStep!.Objective = "Create the isolated resource at the retained target.";
        state.Construction!.Compact!.CurrentStep!.OperationIds = [capability.Id];
        state.Construction.Compact.CurrentStep.RequirementIds = ["req_1", "req_0"];
        state.Construction.Compact.CurrentStep.CloseRequirementIds = ["req_1"];
        PlanningCompactConstruction.StageCommitments(state, null);
        Stage(state, new() { RequirementIds = ["req_1", "req_0"], Tasks = [new()
        {
            Id = "create", Kind = "operation", Objective = "Create the isolated resource at the retained target.",
            Operation = capability.Id, Inputs = [new("target", new() { Kind = "string", Text = target })]
        }] });
        foreach (var intermediate in new[] { false, true })
        {
            var review = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate).Request;
            var context = Context(review);
            var stage = context["constructionStage"]!;
            Assert.Equal(new[] { "req_1" }, context["step"]!["closeRequirementIds"]!.AsArray().Select(id => id!.ToString()));
            Assert.Equal(new[] { "req_0", "req_1" }, context["commitments"]!.AsObject().Select(item => item.Key).Order(StringComparer.Ordinal));
            foreach (var commitment in context["commitments"]!.AsObject())
                Assert.Equal(capability.Id, Assert.Single(commitment.Value!.AsArray())!["operation"]!.ToString());
            if (presentation == 3)
            {
                Assert.Equal("{\"openedScopeCount\":0,\"closedScopeCount\":0,\"requirementCompletionClaimed\":true,\"planCompletionClaimed\":false}", stage.ToJsonString());
                Assert.Null(stage["closeRequirementIds"]);
            }
            else
            {
                Assert.Equal(new[] { "req_1" }, stage["closeRequirementIds"]!.AsArray().Select(id => id!.ToString()));
                Assert.Null(stage["requirementCompletionClaimed"]);
                Assert.True(stage["preflight"]!["scopeOpen"]!.GetValue<bool>());
            }
            var guidance = review.Prompt[..review.Prompt.LastIndexOf('\n')];
            Assert.Equal(presentation == 3 ? new[] { "evidenceRequests", "findings", "uncertainty" }
                : ["evidenceRequests", "findings", "inconclusive", "uncertainty"],
                review.StructuredOutputSchema!["properties"]!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
            if (presentation == 3)
            {
                Assert.Contains("not the complete operation input contract", guidance, StringComparison.Ordinal);
                Assert.Contains("empty list does not mean no arguments", guidance, StringComparison.Ordinal);
            }
            else Assert.Contains("its exact target and ownership remain available after partial failure", guidance, StringComparison.Ordinal);
            Assert.DoesNotContain("Retrieval expands binding grammar", guidance, StringComparison.Ordinal);
            Assert.DoesNotContain("reselectStep", guidance, StringComparison.Ordinal);
            var operation = Assert.Single(context["operations"]!.AsArray())!;
            Assert.Equal(capability.Id, operation["id"]!.ToString());
            Assert.Equal(presentation == 4 ? capability.Server : null, operation["server"]?.ToString());
            Assert.Equal(presentation == 4 ? capability.Method : null, operation["method"]?.ToString());
            var historical = PlannerFixture.Clone(state);
            historical.Construction!.Compact!.PresentationVersion = 2;
            historical.Request.Options["construction_presentation_version"] = 2;
            Assert.Contains("Retrieval expands binding grammar",
                HybridWorkflowPlanner.CreateConstructionReviewRequest(historical, intermediate).Request.Prompt, StringComparison.Ordinal);
        }
        Assert.Contains("Retrieval expands binding grammar",
            PlanningConstruction.DraftRequest(state, repair: false).StructuredOutputSchema!["description"]!.ToString(), StringComparison.Ordinal);
        string? issuedBytes = null;
        if (retainedReview)
        {
            var review = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, intermediate: false).Request;
            var context = Context(review);
            foreach (var operation in context["operations"]!.AsArray())
            {
                operation!.AsObject().Remove("server"); operation.AsObject().Remove("method");
            }
            var prompt = review.Prompt[..(review.Prompt.LastIndexOf('\n') + 1)] + context.ToJsonString();
            var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, prompt, review.StructuredOutputSchema!.AsObject());
            state.Construction.PendingReviewContext = context;
            state.Construction.PendingReviewHash = PlanningGraphCompiler.Fingerprint(PlanningConstruction.LocalPurpose + "\n" +
                state.Construction.Candidate!.Hash + "\n" + context.ToJsonString());
            state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningConstruction.LocalPurpose, Request = issued };
            state.ModelCalls++;
            issuedBytes = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
            state = PlannerFixture.Clone(state);
        }
        var callsBeforeReview = state.ModelCalls;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            if (presentation == 3) Assert.True(context["constructionStage"]!["requirementCompletionClaimed"]!.GetValue<bool>());
            else Assert.Equal("req_1", Assert.Single(context["constructionStage"]!["closeRequirementIds"]!.AsArray())!.ToString());
            Assert.False(context["constructionStage"]!["planCompletionClaimed"]!.GetValue<bool>());
            Assert.Equal("req_1", Assert.Single(context["step"]!["closeRequirementIds"]!.AsArray())!.ToString());
            Assert.Empty(context["draft"]!["always"]!.AsArray());
            Assert.Empty(context["interfaces"]!.AsArray());
            var binding = Assert.Single(context["draft"]!["tasks"]![0]!["inputs"]!.AsArray())!["value"]!;
            Assert.Equal("string", binding["kind"]!.ToString());
            Assert.Equal(target, binding["text"]!.ToString());
            var contract = Assert.Single(context["operations"]!.AsArray())!;
            Assert.Equal(capability.Description, contract["description"]!.ToString());
            Assert.True(contract["descriptionComplete"]!.GetValue<bool>());
            Assert.Equal("write", contract["effect"]!.ToString());
            Assert.Equal(presentation == 4 && !retainedReview ? capability.Server : null, contract["server"]?.ToString());
            Assert.Equal(presentation == 4 && !retainedReview ? capability.Method : null, contract["method"]?.ToString());
            if (issuedBytes is not null) Assert.Equal(issuedBytes, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return Wire(request, Reviewed());
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(callsBeforeReview + (retainedReview ? 0 : 1), state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal("in_progress", state.Construction.Requirements.Single(requirement => requirement.Id == "req_0").Status);
        Assert.Equal("closed", state.Construction.Requirements.Single(requirement => requirement.Id == "req_1").Status);
        Assert.Equal(new[] { "req_1" }, state.Construction.Checkpoints[0].ClosedRequirementIds!);
        Assert.Contains("/root", state.Construction.OpenScopes);
        var accepted = PlanningConstruction.Hash(state.Plan);

        Select(state, "close");
        state.Construction.Compact!.CurrentStep!.Objective = "Confirm resource creation and cleanup are complete.";
        state.Construction.Compact!.CurrentStep!.CloseRequirementIds = ["req_0"];
        Stage(state, new() { RequirementIds = ["req_0"] });
        PlanningConstruction.LoadEvidence(state, ["scope:/root"]);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            var context = Context(request);
            if (presentation == 3) Assert.True(context["constructionStage"]!["requirementCompletionClaimed"]!.GetValue<bool>());
            else Assert.Equal("req_0", Assert.Single(context["constructionStage"]!["closeRequirementIds"]!.AsArray())!.ToString());
            Assert.False(context["constructionStage"]!["planCompletionClaimed"]!.GetValue<bool>());
            var scope = context["evidence"]!["scope:/root"]!;
            Assert.Equal(0, scope["alwaysCount"]!.GetValue<int>());
            Assert.Empty(scope["always"]!.AsArray());
            Assert.Equal(target, Assert.Single(scope["tasks"]!.AsArray())!["inputs"]![0]!["value"]!["text"]!.ToString());
            return Wire(request, new()
            {
                ["findings"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "omission", ["location"] = "/requirements/0",
                    ["evidence"] = new JsonArray("/requirements/0", "/evidence/scope:~1root"),
                    ["explanation"] = "The requirement now claims completion, but the retained resource has no cleanup for success or partial failure.",
                    ["uncertainty"] = "Execution remains unverified."
                }), ["uncertainty"] = "Only the current completion claim was reviewed.", ["evidenceRequests"] = null
            });
        };
        state = await Advance(state, runtime);
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal("in_progress", state.Construction.Requirements.Single(requirement => requirement.Id == "req_0").Status);
        Assert.Equal("closed", state.Construction.Requirements.Single(requirement => requirement.Id == "req_1").Status);
        Assert.False(state.Construction.Complete);
        Assert.Equal(originalContract, JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability));
    }

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
            Assert.Contains("interfaces are exact consumed operands", request.Prompt, StringComparison.Ordinal);
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
        var (state, runtime) = await Ready("Compare the supplied text with the requested criteria and report every discrepancy.");
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
            Assert.DoesNotContain("the draft expression computes the result", request.Prompt, StringComparison.Ordinal);
            return Wire(request, new()
            {
                ["findings"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "inconsistency", ["location"] = "/draft/tasks/0",
                    ["evidence"] = new JsonArray("/requirements/0", "/draft/tasks/0"),
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
        var (state, runtime) = await Ready("Return exactly two as the business result.");
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
                    ["evidence"] = new JsonArray("/requirements/0", invented ? "/draft/tasks/0/parallel_status/0" : "/draft/tasks/0"),
                    ["explanation"] = "The returned value does not satisfy the original requested result.",
                    ["uncertainty"] = "Execution remains unverified."
                }), ["uncertainty"] = "Only the current fragment was reviewed.", ["evidenceRequests"] = null, ["inconclusive"] = null
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
        var (state, runtime) = await Ready(presentation: 3);
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
    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready(string prompt = "Preserve the declared business value.", int presentation = 4)
    {
        var runtime = new TestRuntime { Respond = (request, _) => Wire(request, new()
        {
            ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(new JsonObject { ["text"] = prompt, ["kind"] = "requirement", ["interfaces"] = "none" })
        }) };
        var state = await Advance(PlannerFixture.CapturedPresentation(new() { Request = new() { TenantId = "actual-imports", Prompt = prompt,
            Options = new() { ["automatic_correction"] = true } } }, presentation), runtime);
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
        state.Construction!.Compact!.PresentationVersion = 3;
        state.Request.Options["construction_presentation_version"] = 3;
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
