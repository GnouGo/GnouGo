using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CoherentBusinessFragmentTests
{
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
                Assert.Equal(10, context["actions"]!.AsArray().Count(action => action!["kind"]!.ToString() == "value"));
                return new() { Json = BusinessReviewProtocolTests.Report("ok") };
            }
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            drafts++;
            return Draft(request, new()
            {
                Scope = "/root", ContinueInScope = drafts == 1 ? "/root" : null,
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
        var finish = new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["work"] };
        var empty = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.Apply(state, finish));
        Assert.Contains(empty.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], Tasks = [Value(0)], ContinueInScope = "/root"
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

    [Fact]
    public async Task PopulatedReusableGroupCanOwnTypedIterationAndCleanupInOneFragment()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = "group";
        var extension = new PlanningConstructionExtension
        {
            Scope = "/root", RequirementIds = ["work"], CloseScopes = ["/tasks/iterate/body", "/groups/worker/body"],
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
                    }],
                    Outputs = [new("numbers", new() { Kind = "output", Source = "iterate", Port = "number" })]
                }
            }]
        };
        AssertDraftAccepts(state, extension);
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.Empty(new TaskPlanCompiler().Compile(candidate.Plan, state.Catalog!, state.Request).Diagnostics);
        Assert.Equal(3, candidate.TaskIds.Count);
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
            Scope = "/root", RequirementIds = ["work"], CloseScopes = ["/tasks/choose/body", "/tasks/choose/otherwise", "/tasks/iterate/body"],
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

    [Fact]
    public async Task NestedControlPermissionDoesNotWidenTheSelectedTopLevelAnchor()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Construction.Compact!.CurrentStep!.Kind = "foreach";
        var extension = new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["work"], Tasks =
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
            Scope = "/root", RequirementIds = ["work"], Tasks = [new()
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
            Scope = "/root", RequirementIds = ["work"], Tasks = [new()
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
            Scope = "/root", RequirementIds = ["work"], CloseScopes = ["/tasks/iterate/body"],
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
            Scope = "/root", RequirementIds = ["work"], ContinueInScope = "/tasks/block/body",
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
        var second = new PlanningConstructionExtension { Scope = step.Scope, RequirementIds = ["work"], Tasks = [Value(1)],
            Outputs = [new("result", new() { Kind = "output", Source = "value_1", Port = "result" })], CloseScopes = [step.Scope] };
        AssertDraftAccepts(state, second);
        var candidate = PlanningConstruction.Apply(state, second);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Single(candidate.Plan.Root.Tasks);
        Assert.Equal(2, candidate.Plan.Root.Tasks[0].Body!.Tasks.Count);
    }

    [Fact]
    public async Task ContinuedOriginalScopeCannotRepeatItsTopLevelWrapper()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Compact!.CurrentStep!.Kind = "sequence";
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], ContinueInScope = "/root", Tasks = [new()
                { Id = "block", Kind = "sequence", Objective = "Own the calculation.", Body = new() { Tasks = [Value(0)] } }]
        });
        runtime.Respond = (_, _) => throw new InvalidOperationException("A partial block is accepted technically.");
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.DoesNotContain("sequence", PlanningCompactConstruction.BusinessFragmentKinds(state));
        var repeated = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], Tasks = [new()
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
        var candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"],
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
            Scope = "/root", RequirementIds = ["work"], Groups = [new() { Id = "unfinished", Body = new()
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
        var extension = new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["work"] };
        if (kind == "group") extension.Groups = [new() { Id = "deferred", Body = new() }];
        else extension.Tasks = [new() { Id = "deferred", Kind = "sequence", Objective = "Own the unresolved calculation.", Body = new() }];
        var candidate = PlanningConstruction.Apply(state, extension);
        if (hasPrerequisite)
        {
            PlanningCompactConstruction.ValidateExtension(state, candidate);
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
        var extension = new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["work"],
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
            Scope = "/root", RequirementIds = ["work"], Tasks = [new() { Id = "block", Kind = "sequence", Objective = "Bounded work.",
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
