using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CoherentBusinessFragmentTests
{
    [Theory]
    [InlineData("current")]
    [InlineData("stale")]
    [InlineData("strict")]
    [InlineData("oversized")]
    public async Task AContinuedDraftCanReuseTheClosedChildScopeItInspectedBeforeTheCheckpoint(string variant)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready(advisoryOptimization: variant != "strict");
        var construction = state.Construction!;
        construction.Candidate = null;
        const string path = "/tasks/collected/body";
        const string scopeAddress = "scope:" + path;
        const string taskAddress = "task:value_1";
        var child = Value(0);
        child.Id = "value_1";
        if (variant == "oversized") child.Objective = new string('界', 3000);
        state.Plan!.Root.Tasks.Add(new() { Id = "collected", Kind = "sequence", Objective = "Collect the records.", Body = new() { Tasks = [child] } });
        construction.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        PlanningConstruction.LoadEvidence(state, [scopeAddress, taskAddress]);
        var original = construction.Evidence![scopeAddress]!.DeepClone();
        if (variant == "stale")
        {
            child.Objective = "Changed evidence on the accepted plan.";
            construction.Compact.CurrentStep.BaseHash = PlanningConstruction.Hash(state.Plan);
        }
        var calls = 0;
        runtime.Respond = (request, _) =>
        {
            var task = Value(0);
            task.Id = "next_" + calls;
            return Draft(request, new()
            {
                Scope = "/root", Tasks = [task], RequirementIds = ["work"], CompleteBlock = false,
                ContinueInScope = calls++ == 0 ? "/root" : null
            });
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        var continued = PlannerFixture.Clone(state);
        var retained = continued.Construction!.Evidence;
        if (variant == "current")
        {
            Assert.True(JsonNode.DeepEquals(original, retained?[scopeAddress]));
            Assert.Equal(child.Objective, retained![taskAddress]!["objective"]!.ToString());
            var request = PlanningConstruction.DraftRequest(continued, repair: false);
            Assert.Contains(scopeAddress, request.Prompt, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(retained?[scopeAddress]);
            Assert.Null(retained?[taskAddress]);
        }
        state = await Advance(continued, runtime);
        Assert.Empty(state.Diagnostics);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.Construction!.Evidence?[scopeAddress]);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("description:read@v1:0")]
    [InlineData("schema:read@v1:/input")]
    [InlineData("contract:read@v1")]
    public async Task ExactPrerequisiteContractSurvivesCandidateValidationAcceptanceAndColdContinuation(string address)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Catalog!.Capabilities.Add(new() { Id = "read", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = "read",
            EffectKind = "read", Description = "Use only the originally authorized numeric record.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false } });
        var step = state.Construction.Compact!.CurrentStep!;
        step.OperationIds = ["read"];
        var original = PlanningCompactConstruction.LoadContractEvidence(state, address)!.DeepClone();
        step.OperationIds.Clear();
        step.Prerequisites = [new() { Need = "Preserve the inspected original restriction.", EvidenceAddresses = [address] }];
        state.Construction.Evidence = new() { [address] = original.DeepClone(), ["task:stale"] = new JsonObject() };
        var drafts = 0;
        runtime.Respond = (request, purpose) => purpose == PlanningConstruction.LocalPurpose
            ? new() { Json = BusinessReviewProtocolTests.Report("ok") }
            : Draft(request, new() { Scope = "/root", Tasks = [Value(drafts)], RequirementIds = ["work"],
                ContinueInScope = ++drafts == 1 ? "/root" : null, CompleteBlock = drafts == 2 });
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(JsonNode.DeepEquals(original, state.Construction!.Evidence![address]));
        Assert.Null(state.Construction.Evidence["task:stale"]);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(state.Construction!.Checkpoints);
        state = PlannerFixture.Clone(state);
        Assert.True(JsonNode.DeepEquals(original, state.Construction!.Evidence![address]));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, drafts); Assert.Equal(2, state.Construction!.Checkpoints.Count);
        Assert.Null(state.Construction.Compact!.CurrentStep);
        Assert.Null(state.Construction.Evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenTasksContinueOneSelectedBlockAcrossRestartWithTwoDraftsAndOneReview(bool closesRequirement)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        if (!closesRequirement) state.Construction.Compact!.CurrentStep!.CloseRequirementIds.Clear();
        var drafts = 0; var reviews = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose == PlanningConstruction.LocalPurpose)
            {
                reviews++;
                var context = BusinessProjectionDeclarationsTests.Context(request);
                Assert.False(context.ContainsKey("maxTasksPerDraft"));
                Assert.DoesNotContain("maxTasksPerDraft", request.Prompt, StringComparison.Ordinal);
                Assert.Equal(10, context["actions"]!.AsArray().Count(action => action!["kind"]!.ToString() == "value"));
                return new() { Json = BusinessReviewProtocolTests.Report("ok") };
            }
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            Assert.Equal(8, BusinessProjectionDeclarationsTests.Context(request)["maxTasksPerDraft"]?.GetValue<int>());
            drafts++;
            return Draft(request, new()
            {
                CompleteBlock = drafts == 2, Scope = "/root", ContinueInScope = drafts == 1 ? "/root" : null,
                RequirementIds = ["work"], Tasks = Enumerable.Range(drafts == 1 ? 0 : 8, drafts == 1 ? 8 : 2).Select(Value).ToList()
            });
        };
        state = await Advance(state, runtime); // first draft
        Assert.Empty(state.Diagnostics);
        state = await Advance(state, runtime); // technical acceptance
        Assert.Empty(state.Diagnostics);
        Assert.Equal("calculation", state.Construction!.Compact!.CurrentStep!.Id);
        Assert.Equal(PlanningConstruction.Hash(state.Plan), state.Construction.Compact.CurrentStep.BaseHash);
        Assert.Empty(Assert.Single(state.Construction.Checkpoints).ClosedRequirementIds!);
        Assert.Equal("in_progress", state.Construction.Requirements.Single(requirement => requirement.Id == "work").Status);
        Assert.Equal(0, reviews);
        state = await Advance(PlannerFixture.Clone(state), runtime); // exact continued block after restart
        Assert.Empty(state.Diagnostics);
        state = await Advance(state, runtime); // one coherent review and acceptance
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, drafts); Assert.Equal(1, reviews);
        Assert.Equal(10, state.Plan!.Root.Tasks.Count);
        Assert.Null(state.Construction!.Compact!.CurrentStep);
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.Equal("calculation", checkpoint.Step!.Id));
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.False(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
        Assert.Equal(closesRequirement ? "closed" : "in_progress", state.Construction.Requirements.Single(requirement => requirement.Id == "work").Status);
    }

    [Fact]
    public async Task ContinuedProductiveBlockCanFinishWithoutInventingAnotherTaskOrClosingItsRequirement()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.CloseRequirementIds.Clear();
        var finish = new PlanningConstructionExtension { CompleteBlock = true, Scope = "/root", RequirementIds = ["work"] };
        var empty = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.Apply(state, finish));
        Assert.Contains(empty.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], Tasks = [Value(0)], ContinueInScope = "/root"
        });
        runtime.Respond = (_, _) => throw new InvalidOperationException("A partial block remains a technical checkpoint.");
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        state.Construction!.Candidate = PlanningConstruction.Apply(state, finish);
        PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            Assert.Single(BusinessProjectionDeclarationsTests.Context(request)["actions"]!.AsArray());
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.Single(state.Plan!.Root.Tasks);
        Assert.Null(state.Construction!.Compact!.CurrentStep);
        Assert.Equal(2, state.Construction.Checkpoints.Count);
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.False(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
        Assert.Equal("in_progress", state.Construction.Requirements.Single(requirement => requirement.Id == "work").Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PopulatedReusableGroupCanOwnTypedIterationAndCleanupInOneFragment(bool explicitClosures)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = "group";
        var extension = new PlanningConstructionExtension
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["work"],
            CloseScopes = explicitClosures ? ["/tasks/iterate/body", "/groups/worker/body"] : [],
            Groups = [new()
            {
                Id = "worker", Inputs = [new() { Name = "numbers", Type = new() { Kind = "array", Items = new() { Kind = "number" } } }],
                Body = new()
                {
                    Tasks = [new()
                    {
                        Id = "iterate", Kind = "foreach", Objective = "Transform each isolated input number.", MaxItems = 20,
                        Items = new() { Kind = "input", Source = "numbers" }, Body = new()
                        {
                            Tasks = [new() { Id = "increment", Kind = "value", Objective = "Increment this number.",
                                Outputs = [new("number", new() { Kind = "arithmetic", Text = "add", Items =
                                    [new() { Kind = "item" }, new() { Kind = "number", Number = 1 }] })] }],
                            Outputs = [new("number", new() { Kind = "output", Source = "increment", Port = "number" })],
                            Always = [new() { Id = "final_item", Kind = "value", Objective = "Retain the exact item on finalization.",
                                Outputs = [new("number", new() { Kind = "item" })] }]
                        }
                    }, new()
                    {
                        Id = "collect", Kind = "value", Objective = "Consume the completed loop in this same fragment.", DependsOn = ["iterate"],
                        Outputs = [new("numbers", new() { Kind = "output", Source = "iterate", Port = "number" })]
                    }],
                    Outputs = [new("numbers", new() { Kind = "output", Source = "collect", Port = "numbers" })]
                }
            }]
        };
        AssertDraftAccepts(state, extension);
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.Empty(new TaskPlanCompiler().Compile(candidate.Plan, state.Catalog!, state.Request).Diagnostics);
        Assert.Equal(4, candidate.TaskIds.Count);
        Assert.Equal(["/root"], candidate.OpenScopes);
        Assert.Equal(["/tasks/iterate/body", "/groups/worker/body"], candidate.Extension.CloseScopes);
        if (!explicitClosures) Assert.Empty(extension.CloseScopes); // received response remains unchanged
        Assert.Equal("final_item", Assert.Single(candidate.Plan.Groups[0].Body.Tasks[0].Body!.Always).Id);
    }

    [Fact]
    public async Task NestedConditionalKeepsExactOperationsAndPerItemCleanupInsideForeach()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Plan!.Inputs = [new() { Name = "numbers", Type = new() { Kind = "array", Items = new() { Kind = "number" } } }];
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "foreach"; step.OperationIds = ["read_number", "final_read"];
        if (!state.Catalog!.AllowedStepTypes.Contains("mcp.call")) state.Catalog.AllowedStepTypes.Add("mcp.call");
        foreach (var operation in step.OperationIds) state.Catalog.Capabilities.Add(new()
        {
            Id = operation, Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = operation,
            EffectKind = "read", Description = "Read the exact authorized numeric record.",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["target"] = new JsonObject { ["type"] = "number" } },
                ["required"] = new JsonArray("target"), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }
        });
        var extension = new PlanningConstructionExtension
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], CloseScopes = ["/tasks/choose/body", "/tasks/choose/otherwise", "/tasks/iterate/body"],
            Tasks = [new()
            {
                Id = "iterate", Kind = "foreach", Objective = "Read qualifying items and finalize every item.",
                Items = new() { Kind = "input", Source = "numbers" }, MaxItems = 20,
                Body = new()
                {
                    Tasks = [new()
                    {
                        Id = "choose", Kind = "conditional", Objective = "Read nonzero records.",
                        Condition = new() { Kind = "predicate", Predicate = "not_equal", Items = [new() { Kind = "item" }, new() { Kind = "number", Number = 0 }] },
                        Body = new() { Tasks = [Operation("read", "read_number")] },
                        Otherwise = new() { Tasks = [new() { Id = "skipped", Kind = "value", Objective = "Retain the skipped zero.",
                            Outputs = [new("number", new() { Kind = "item" })] }] }
                    }],
                    Always = [Operation("finalize", "final_read")]
                }
            }]
        };
        AssertDraftAccepts(state, extension);
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.StageCommitments(state, null, candidate);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.Empty(new TaskPlanCompiler().Compile(candidate.Plan, state.Catalog, state.Request).Diagnostics);
        Assert.Equal(5, candidate.TaskIds.Count);
        Assert.Equal("final_read", Assert.Single(candidate.Plan.Root.Tasks[0].Body!.Always).Operation);
        Assert.Equal(2, state.Construction.Compact.PendingCommitments["work"].Count);
        extension.Tasks[0].Body!.Tasks[0].Body!.Tasks[0].Operation = "unselected";
        var unselected = PlanningConstruction.Apply(state, extension);
        Assert.Contains(Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, unselected)).Diagnostics,
            diagnostic => diagnostic.Code == "CONSTRUCTION_STEP_INVALID");

        static PlanTask Operation(string id, string operation) => new() { Id = id, Kind = "operation", Operation = operation,
            Objective = "Read this exact item.", Inputs = [new("target", new() { Kind = "item" })] };
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("foreach")]
    public async Task DraftShowsWhereToDeclareAnExplicitNestedLoopLimit(string anchor)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = anchor;
        state.Plan!.Inputs = [new() { Name = "records", Type = new() { Kind = "array", Items = new() { Kind = "number" } } }];
        var inner = new PlanTask
        {
            Id = "rows", Kind = "foreach", Objective = "Visit every row within the supplied capacity.",
            Items = new() { Kind = "input", Source = "records" }, MaxItems = 200,
            Body = new() { Tasks = [new() { Id = "row", Kind = "value", Objective = "Retain the current row.",
                Outputs = [new("result", new() { Kind = "item" })] }] }
        };
        var outer = new PlanTask { Id = "batch", Kind = anchor, Objective = "Process the batch.", Body = new() { Tasks = [inner] } };
        if (anchor == "foreach") outer.Items = new() { Kind = "input", Source = "records" };
        var extension = new PlanningConstructionExtension { CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], Tasks = [outer] };
        state.Construction.IssuedBaseHash = state.Construction.Compact.CurrentStep.BaseHash = PlanningConstruction.Hash(state.Plan);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        var schema = request.StructuredOutputSchema!.AsObject();
        var response = new JsonObject { ["extension"] = PlanningConstruction.Node(extension, PlanningJsonContext.Default.PlanningConstructionExtension) };
        var wire = PlanningCorpus.Transport(response, schema, schema)!;
        var nested = wire["extension"]!["tasks"]![0]!["body"]!["tasks"]![0]!.AsObject();
        Assert.Equal(200, nested["itemLimit"]?.GetValue<int>());
        Assert.False(nested.ContainsKey("maxItems"));
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(wire, schema));
        var normalized = PlanningConstruction.NormalizeDraftResponse(PlannerFixture.Clone(state), wire, request);
        var restored = normalized["extension"]!.Deserialize(PlanningJsonContext.Default.PlanningConstructionExtension)!;
        Assert.Equal(200, restored.Tasks[0].Body!.Tasks[0].MaxItems);
        Assert.Equal(200, nested["itemLimit"]!.GetValue<int>());
        nested["maxItems"] = 100;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(wire, schema));
        Assert.Throws<PlanningConflictException>(() => PlanningConstruction.NormalizeDraftResponse(state, wire, request));
        var candidate = PlanningConstruction.Apply(state, restored);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(new TaskPlanCompiler().Compile(candidate.Plan, state.Catalog!, state.Request).Diagnostics);
        Assert.Contains(anchor + ".body.tasks", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("foreach.itemLimit (1..10000)", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("iterate that ordered collection once", request.Prompt, StringComparison.Ordinal);
        inner.MaxItems = TaskPlanCompiler.MaximumIterationItems + 1;
        var raw = new JsonObject { ["extension"] = PlanningConstruction.Node(extension, PlanningJsonContext.Default.PlanningConstructionExtension) };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(PlanningCorpus.Transport(raw, schema, schema)!, schema));
        state.Construction.Compact.CurrentStep.Kind = "value";
        Assert.DoesNotContain("foreach.itemLimit (", PlanningConstruction.DraftRequest(state, repair: false).Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedControlPermissionDoesNotWidenTheSelectedTopLevelAnchor()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = "foreach";
        var extension = new PlanningConstructionExtension { CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], Tasks =
            [new() { Id = "unselected", Kind = "sequence", Objective = "Unselected top-level wrapper.", Body = new() { Tasks = [Value(0)] } }] };
        var candidate = PlanningConstruction.Apply(state, extension);
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, candidate));
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_STEP_INVALID");
    }

    [Fact]
    public async Task PopulatedControlAndLocalHelpersShareOneTechnicallyValidatedFragment()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = "sequence";
        var extension = new PlanningConstructionExtension
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], Tasks = [new()
            {
                Id = "block", Kind = "sequence", Objective = "Perform the two calculations together.",
                Body = new() { Tasks = [Value(0), Value(1)], Outputs = [new("result", new() { Kind = "output", Source = "value_1", Port = "result" })] }
            }], CloseScopes = ["/tasks/block/body"]
        };
        AssertDraftAccepts(state, extension);
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.DoesNotContain("/tasks/block/body", candidate.OpenScopes);
        Assert.Equal(3, candidate.TaskIds.Count);
    }

    [Fact]
    public async Task ThreeBoundOperationsAndTheirLocalProducerShareOneControlFragment()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "sequence";
        if (!state.Catalog!.AllowedStepTypes.Contains("mcp.call")) state.Catalog.AllowedStepTypes.Add("mcp.call");
        for (var index = 0; index < 3; index++)
        {
            var id = "read_" + index;
            step.OperationIds.Add(id);
            state.Catalog.Capabilities.Add(new()
            {
                Id = id, Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = id, EffectKind = "read",
                Description = "Read the authorized numeric record.",
                InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["target"] = new JsonObject { ["type"] = "number" } },
                    ["required"] = new JsonArray("target"), ["additionalProperties"] = false },
                OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }
            });
        }
        var operations = step.OperationIds.Select((operation, index) => new PlanTask
        {
            Id = "operation_" + index, Kind = "operation", Operation = operation, Objective = "Read one part of the requested result.",
            Inputs = [new("target", new() { Kind = "output", Source = "value_0", Port = "result" })]
        });
        var extension = new PlanningConstructionExtension
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], Tasks = [new()
            {
                Id = "block", Kind = "sequence", Objective = "Acquire the related records with their shared input.",
                Body = new() { Tasks = new[] { Value(0) }.Concat(operations).ToList() }
            }], CloseScopes = ["/tasks/block/body"]
        };
        AssertDraftAccepts(state, extension);
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.StageCommitments(state, null, candidate);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.Equal(3, state.Construction.Compact.PendingCommitments["work"].Count);
        Assert.Equal(5, candidate.TaskIds.Count);
    }

    [Fact]
    public async Task SelectedClosedGroupCanBeCalledInsideOnePopulatedForeachFragment()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Plan!.Inputs = [new() { Name = "values", Required = true, Type = new() { Kind = "array", Items = new() { Kind = "number" } } }];
        state.Plan.Groups.Add(new()
        {
            Id = "double", Inputs = [new() { Name = "number", Type = new() { Kind = "number" } }],
            Body = new()
            {
                Tasks = [new() { Id = "multiply", Kind = "value", Objective = "Double the isolated numeric parameter.",
                    Outputs = [new("doubled", new() { Kind = "arithmetic", Text = "multiply", Items =
                        [new() { Kind = "input", Source = "number" }, new() { Kind = "number", Number = 2 }] })] }],
                Outputs = [new("doubled", new() { Kind = "output", Source = "multiply", Port = "doubled" })]
            }
        });
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "foreach"; step.GroupId = "double"; step.Inputs.Clear();
        PlanningCompactConstruction.ValidateSelection(state, step);
        var extension = new PlanningConstructionExtension
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], CloseScopes = ["/tasks/iterate/body"],
            Tasks = [new()
            {
                Id = "iterate", Kind = "foreach", Objective = "Apply the reusable doubling group to every item.",
                Items = new() { Kind = "input", Source = "values" }, MaxItems = 100, MaxConcurrency = 4, Parallel = true,
                Body = new()
                {
                    Tasks = [new() { Id = "invoke", Kind = "call", Group = "double", Objective = "Double this item.",
                        Inputs = [new("number", new() { Kind = "item" })] }],
                    Outputs = [new("doubled", new() { Kind = "output", Source = "invoke", Port = "doubled" })]
                }
            }]
        };
        AssertDraftAccepts(state, extension);
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.Equal(2, candidate.TaskIds.Count);
        Assert.DoesNotContain("/tasks/iterate/body", candidate.OpenScopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedControlContinuationRetainsIdentityAndDoesNotCreateAnotherWrapper(bool prerequisiteSelection)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = "sequence";
        if (prerequisiteSelection)
        {
            state.Construction.Compact.ReplacementScope = "/root";
            state.Construction.Compact.ReplacementRequirements = ["work"];
        }
        var first = new PlanningConstructionExtension
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], ContinueInScope = "/tasks/block/body",
            Tasks = [new() { Id = "block", Kind = "sequence", Objective = "Compute this block's result.", Body = new() { Tasks = [Value(0)] } }]
        };
        state.Construction.Candidate = PlanningConstruction.Apply(state, first);
        PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate);
        runtime.Respond = (_, _) => throw new InvalidOperationException("A partial block receives no Judge or selection.");
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var step = state.Construction!.Compact!.CurrentStep!;
        Assert.Equal("calculation", step.Id);
        Assert.Equal("/tasks/block/body", step.Scope);
        Assert.True(step.DiscoveryComplete);
        Assert.Null(state.Construction.Compact.ReplacementScope);
        Assert.Contains("conditional", PlanningCompactConstruction.BusinessFragmentKinds(state));
        var second = new PlanningConstructionExtension { CompleteBlock = false, Scope = step.Scope, RequirementIds = ["work"], Tasks = [Value(1)],
            Outputs = [new("result", new() { Kind = "output", Source = "value_1", Port = "result" })], CloseScopes = [step.Scope] };
        AssertDraftAccepts(state, second);
        var candidate = PlanningConstruction.Apply(state, second);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Single(candidate.Plan.Root.Tasks);
        Assert.Equal(2, candidate.Plan.Root.Tasks[0].Body!.Tasks.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ContinuingTheSameFragmentCanUseTheAcceptedScopeAndCleanupWithoutRereadingThem(bool oversized, bool strictPacking)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        var first = Value(0);
        if (oversized) first.Objective += new string('x', 9000);
        state.Construction!.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = false, ContinueInScope = "/root",
            Tasks = [first], Always = [new() { Id = "cleanup", Kind = "value", Objective = "Record the cleanup.",
                Outputs = [new("finished", new() { Kind = "boolean", Boolean = true })] }]
        });
        runtime.Respond = (_, _) => throw new InvalidOperationException("The technical checkpoint needs no model response.");
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        state = PlannerFixture.Clone(state);
        if (!strictPacking) state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        Assert.Equal(!oversized, PlanningCompactConstruction.Context(state)["scopeContext"] is not null);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        var choices = request.StructuredOutputSchema!["properties"]!["evidenceRequests"]!.ToJsonString();
        if (oversized || strictPacking) Assert.Null(context["scopeContext"]);
        else
        {
            var scope = context["scopeContext"];
            Assert.True(JsonNode.DeepEquals(PlanningConstruction.ScopeEvidence(state, "/root"), scope));
            Assert.Equal("value_0", scope!["tasks"]![0]!["id"]!.ToString());
            Assert.Equal("cleanup", scope["always"]![0]!["id"]!.ToString());
        }
        foreach (var address in new[] { "scope:/root", "task:value_0", "task:cleanup" })
            Assert.Equal(oversized || strictPacking, choices.Contains(System.Text.Json.JsonSerializer.Serialize(address), StringComparison.Ordinal));
        Assert.Single(state.Construction!.Checkpoints);
        Assert.False(state.Construction.Evidence?.ContainsKey("scope:/root") == true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ContinuedControlCanComposeItsExportInAnotherControlAtTheSameScope(bool predicateOperand, bool forwardThroughValues)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Compact!.CurrentStep!.Kind = "conditional";
        TaskScope Result(int index) => new()
        {
            Tasks = [new() { Id = "value_" + index, Kind = "value", Objective = "Retain this quantity.",
                Outputs = [new("result", new() { Kind = "number", Number = index })] }],
            Outputs = [new("result", new() { Kind = "output", Source = "value_" + index, Port = "result" })]
        };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = false, ContinueInScope = "/root",
            Tasks = [new() { Id = "choose", Kind = "conditional", Objective = "Choose the applicable quantity.",
                Condition = new() { Kind = "boolean", Boolean = true }, Body = Result(0), Otherwise = Result(1) }]
        });
        runtime.Respond = (_, _) => throw new InvalidOperationException("A partial contribution needs no selection or review.");
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var consumer = Value(2);
        consumer.Outputs[0] = new("result", new() { Kind = "number", Number = 3 });
        var accepted = new TaskValue { Kind = "output", Source = "choose", Port = "result" };
        if (forwardThroughValues)
        {
            var verified = new PlanTask { Id = "verify", Kind = "value", Objective = "Retain the checked quantity.",
                Requires = new() { Kind = "predicate", Predicate = "not_equal", Items = [accepted, new() { Kind = "null" }] },
                Outputs = [new("checked", accepted), new("unrelated", new() { Kind = "number", Number = 3 })] };
            var forward = new PlanTask { Id = "forward", Kind = "value", Objective = "Expose both quantities.",
                Outputs = [new("quantity", new() { Kind = "output", Source = "verify", Port = "checked" }),
                    new("other", new() { Kind = "output", Source = "verify", Port = "unrelated" })] };
            state.Construction!.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"],
                CompleteBlock = false, ContinueInScope = "/root", Tasks = [verified, forward] });
            state = await Advance(state, runtime);
            Assert.Empty(state.Diagnostics);
            accepted = new() { Kind = "output", Source = "forward", Port = "quantity" };
        }
        if (predicateOperand)
            consumer.Requires = new() { Kind = "predicate", Predicate = "not_equal",
                Items = [accepted, new() { Kind = "number", Number = -1 }] };
        else consumer.Outputs[0] = new("result", accepted);
        var extension = new PlanningConstructionExtension
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = false,
            Tasks = [new() { Id = "use_result", Kind = "sequence", Objective = "Process the chosen quantity.", Body = new() { Tasks = [consumer] } }]
        };
        AssertDraftAccepts(state, extension);
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.Equal(forwardThroughValues ? 4 : 2, candidate.Plan.Root.Tasks.Count);
        consumer.Requires = null;
        consumer.Outputs[0] = new("result", forwardThroughValues ? new() { Kind = "output", Source = "forward", Port = "other" } :
            new() { Kind = "number", Number = 3 });
        Assert.Contains(Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.ValidateExtension(state, PlanningConstruction.Apply(state, extension))).Diagnostics,
            diagnostic => diagnostic.Code == "CONSTRUCTION_STEP_INVALID");
        state.Construction!.OpenScopes.Add("/tasks/choose/body");
        Assert.DoesNotContain("sequence", PlanningCompactConstruction.BusinessFragmentKinds(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherIterationCanUseTheSameOriginalCollectionForAnIndependentCheck(bool nestedField)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Compact!.CurrentStep!.Kind = "sequence";
        TaskValue Source(string port) => nestedField
            ? new() { Kind = "field", Port = port, Items = [new() { Kind = "output", Source = "samples", Port = "snapshot" }] }
            : new() { Kind = "output", Source = "samples", Port = port };
        static TaskValue Samples() => new() { Kind = "array", Items = [new() { Kind = "number", Number = 7 }] };
        var original = new PlanTask { Id = "samples", Kind = "value", Objective = "Keep both series.",
            Outputs = nestedField
                ? [new("snapshot", new() { Kind = "object", Members = [new("measurements", Samples()), new("estimates", Samples())] })]
                : [new("measurements", Samples()), new("estimates", Samples())] };
        var projection = new PlanTask { Id = "project", Kind = "foreach", Objective = "Project the measured values.",
            Items = Source("measurements"), Body = new() { Outputs = [new("measurement", new() { Kind = "item" })] } };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = false, ContinueInScope = "/root",
            Tasks = [original, new() { Id = "summary", Kind = "sequence", Objective = "Retain the projected result.",
                Body = new() { Tasks = [projection], Outputs = [new("derived", new() { Kind = "output", Source = "project", Port = "measurement" })] } }]
        });
        runtime.Respond = (_, _) => throw new InvalidOperationException("Technical acceptance needs no model call.");
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var check = new PlanTask { Id = "verify", Kind = "foreach", Objective = "Verify every original measurement.",
            Items = Source("measurements"), Body = new() { Tasks = [new() { Id = "checked", Kind = "value",
                Objective = "Require a nonzero sample.", Requires = new() { Kind = "predicate", Predicate = "not_equal",
                    Items = [new() { Kind = "item" }, new() { Kind = "number", Number = 0 }] },
                Outputs = [new("verified", new() { Kind = "boolean", Boolean = true })] }] } };
        var extension = new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["work"], CompleteBlock = false, Tasks = [check] };
        AssertDraftAccepts(state, extension);
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);

        check.Items = Source("estimates");
        Assert.Contains(Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.ValidateExtension(state, PlanningConstruction.Apply(state, extension))).Diagnostics,
            diagnostic => diagnostic.Code == "CONSTRUCTION_STEP_INVALID");

        check.Items = Source("measurements");
        check.Body!.Outputs = [new("verified", new() { Kind = "output", Source = "checked", Port = "verified" })];
        extension.ContinueInScope = "/root";
        state.Construction!.Candidate = PlanningConstruction.Apply(state, extension);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        var applyCheck = new PlanTask { Id = "use_checks", Kind = "foreach", Objective = "Retain the ordered verified flags.",
            Items = new() { Kind = "output", Source = "verify", Port = "verified" },
            Body = new() { Outputs = [new("flag", new() { Kind = "item" })] } };
        var consumer = new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["work"], CompleteBlock = false, Tasks = [applyCheck] };
        AssertDraftAccepts(state, consumer);
        var composed = PlanningConstruction.Apply(state, consumer);
        PlanningCompactConstruction.ValidateExtension(state, composed);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(composed.Plan, state.Catalog!, state.Request,
            composed.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);

        state.Construction!.OpenScopes.Add("/tasks/verify/body");
        Assert.Contains(Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.ValidateExtension(state, PlanningConstruction.Apply(state, consumer))).Diagnostics,
            diagnostic => diagnostic.Code == "CONSTRUCTION_STEP_INVALID");
    }

    [Fact]
    public async Task ContinuedOriginalScopeCannotRepeatItsTopLevelWrapper()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Compact!.CurrentStep!.Kind = "sequence";
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], ContinueInScope = "/root", Tasks = [new()
                { Id = "block", Kind = "sequence", Objective = "Own the calculation.", Body = new() { Tasks = [Value(0)] } }]
        });
        runtime.Respond = (_, _) => throw new InvalidOperationException("A partial block is accepted technically.");
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.DoesNotContain("sequence", PlanningCompactConstruction.BusinessFragmentKinds(state));
        var repeated = PlanningConstruction.Apply(state, new()
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], Tasks = [new()
                { Id = "another", Kind = "sequence", Objective = "Repeat the same wrapper.", Body = new() { Tasks = [Value(1)] } }]
        });
        Assert.Contains(Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, repeated)).Diagnostics,
            diagnostic => diagnostic.Code == "CONSTRUCTION_STEP_INVALID");
        Assert.Single(state.Plan!.Root.Tasks);
    }

    [Fact]
    public async Task ContinuationCannotMoveIntoAnotherBlocksOpenScope()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Plan!.Root.Tasks.Add(new() { Id = "other", Kind = "sequence", Objective = "Independent work.", Body = new() });
        state.Construction.OpenScopes.Add("/tasks/other/body");
        state.Construction.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        var candidate = PlanningConstruction.Apply(state, new() { CompleteBlock = false, Scope = "/root", RequirementIds = ["work"],
            Tasks = [Value(0)], ContinueInScope = "/tasks/other/body" });
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, candidate));
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BLOCK_CONTINUATION_INVALID");
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task ContinuingGroupCannotPrematurelyCloseTheWorkflowRoot()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = "group";
        var candidate = PlanningConstruction.Apply(state, new()
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], Groups = [new() { Id = "unfinished", Body = new()
                { Tasks = [new() { Id = "declared", Kind = "value", Objective = "Compute the isolated result.",
                    Outputs = [new("number", new() { Kind = "number", Number = 1 })] }] } }],
            CloseScopes = ["/root"], ContinueInScope = "/groups/unfinished/body"
        });
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, candidate));
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BLOCK_CONTINUATION_INVALID");
        Assert.Contains("/root", state.Construction.OpenScopes);
    }

    [Theory]
    [InlineData("sequence", false)]
    [InlineData("sequence", true)]
    [InlineData("group", false)]
    [InlineData("group", true)]
    public async Task EmptyOnlyOpeningRequiresAnEvidencedCurrentPrerequisite(string kind, bool hasPrerequisite)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = kind;
        if (hasPrerequisite) step.Prerequisites = [new()
        {
            Need = "The exact numeric producer required by this original calculation is still unavailable.",
            EvidenceAddresses = ["requirement:work"]
        }];
        var extension = new PlanningConstructionExtension { CompleteBlock = false, Scope = "/root", RequirementIds = ["work"] };
        if (kind == "group") extension.Groups = [new() { Id = "deferred", Body = new() }];
        else extension.Tasks = [new() { Id = "deferred", Kind = "sequence", Objective = "Own the unresolved calculation.", Body = new() }];
        var candidate = PlanningConstruction.Apply(state, extension);
        if (hasPrerequisite)
        {
            PlanningCompactConstruction.ValidateExtension(state, candidate);
            Assert.Contains(kind == "group" ? "/groups/deferred/body" : "/tasks/deferred/body", candidate.OpenScopes);
            Assert.False(HybridWorkflowPlanner.NeedsBusinessReview(state, candidate));
            PlanningProgress.Initialize(state);
            state.Construction.Candidate = candidate;
            state.ModelCalls++;
            PlanningProgress.Record(state);
            Assert.Equal(1, state.Construction.Compact.Progress!.NonProductiveModelCalls);
        }
        else
        {
            var failure = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateExtension(state, candidate));
            Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EMPTY_OPENING");
            Assert.Empty(state.Construction.Checkpoints);
        }
    }

    [Fact]
    public async Task PopulatedGroupCannotCaptureCallerInputs()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = "group";
        var extension = new PlanningConstructionExtension { CompleteBlock = false, Scope = "/root", RequirementIds = ["work"],
            Groups = [new() { Id = "isolated", Body = new() { Tasks = [Value(0)] } }], CloseScopes = ["/groups/isolated/body"] };
        AssertDraftAccepts(state, extension);
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.NotEmpty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
    }

    [Fact]
    public async Task RecursiveChildrenCannotExceedTheExistingFragmentAllowance()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = "sequence";
        var failure = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.Apply(state, new()
        {
            CompleteBlock = false, Scope = "/root", RequirementIds = ["work"], Tasks = [new() { Id = "block", Kind = "sequence", Objective = "Bounded work.",
                Body = new() { Tasks = Enumerable.Range(0, 8).Select(Value).ToList() } }]
        }));
        Assert.Contains(failure.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_DRAFT_LIMIT");
    }

    private static PlanTask Value(int index) => new() { Id = "value_" + index, Kind = "value", Objective = "Derive the next numeric value.",
        Outputs = [new("result", new() { Kind = "arithmetic", Text = "add", Items =
            [index == 0 ? new() { Kind = "input", Source = "x" } : new() { Kind = "output", Source = "value_" + (index - 1), Port = "result" },
             new() { Kind = "number", Number = 1 }] })] };

    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) => BusinessReviewProtocolTests.Advance(state, runtime);
    private static LLMResponse Draft(LLMRequest request, PlanningConstructionExtension extension)
    {
        var response = new JsonObject { ["extension"] = PlanningConstruction.Node(extension, PlanningJsonContext.Default.PlanningConstructionExtension) };
        var schema = request.StructuredOutputSchema!.AsObject();
        return new() { Json = PlanningCorpus.Transport(response, schema, schema) };
    }
    private static void AssertDraftAccepts(PlanningSession state, PlanningConstructionExtension extension)
    {
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var response = new JsonObject { ["extension"] = PlanningConstruction.Node(extension, PlanningJsonContext.Default.PlanningConstructionExtension) };
        var wire = PlanningCorpus.Transport(response, schema, schema)!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(wire, schema));
    }
}
