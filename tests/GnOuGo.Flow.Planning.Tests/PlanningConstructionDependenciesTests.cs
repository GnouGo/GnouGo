using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningConstructionDependenciesTests
{
    [Theory]
    [InlineData("operation")]
    [InlineData("transform")]
    public void FreshMixedProofConservesAnOpaquePrefixWithoutUsingItsResults(string kind)
    {
        var state = MixedSession(kind); var plan = state.Plan!;
        var subject = new JsonObject { ["kind"] = "task", ["id"] = "calculation" };
        var before = Snapshot(state);
        var proof = Assert.IsType<JsonObject>(PlanningConstructionDependencies.Capture(state, plan, subject));
        Assert.Equal(before, Snapshot(state));
        Assert.Equal("deterministic_behavior_with_exact_opaque_prefix", proof["class"]!.ToString());
        Assert.Contains(proof["projection"]!["tasks"]!.AsArray(), row => row!["id"]!.ToString() == "external");
        plan.Root.Tasks.Add(Value("consumer", new() { Kind = "arithmetic", Text = "add", Items = [Ref("calculation"), Number(1)] }));
        Assert.True(PlanningConstructionDependencies.Preserved(state, plan, proof));
        Assert.Null(PlanningConstructionDependencies.Capture(state, plan, new() { ["kind"] = "task", ["id"] = "external" }));
        state.Construction!.Compact!.PresentationVersion = 9;
        Assert.Null(PlanningConstructionDependencies.Capture(state, plan, subject));
        Assert.False(PlanningConstructionDependencies.Preserved(state, plan, proof));
    }

    [Theory]
    [InlineData("opaque_value")]
    [InlineData("opaque_presence")]
    [InlineData("opaque_guard")]
    [InlineData("opaque_argument")]
    [InlineData("opaque_condition")]
    [InlineData("opaque_objective")]
    [InlineData("contract")]
    [InlineData("contract_conflict")]
    [InlineData("permission")]
    [InlineData("order")]
    [InlineData("opaque_after")]
    [InlineData("opaque_cleanup")]
    [InlineData("cleanup")]
    [InlineData("root_state")]
    [InlineData("pure_upstream")]
    public void MixedProofRejectsChangedOpaqueContextAndUnknownInteractions(string change)
    {
        var state = MixedSession("operation"); var plan = state.Plan!;
        var subject = new JsonObject { ["kind"] = "task", ["id"] = "calculation" };
        var proof = Assert.IsType<JsonObject>(PlanningConstructionDependencies.Capture(state, plan, subject));
        switch (change)
        {
            case "opaque_value": plan.Root.Tasks[1].Outputs[0] = new("value", Ref("external", "result")); break;
            case "opaque_presence": plan.Root.Tasks[1].Outputs[0] = new("value", new() { Kind = "present", Source = "external", Port = "result" }); break;
            case "opaque_guard": plan.Root.Tasks[1].Requires = new() { Kind = "present", Source = "external", Port = "result" }; break;
            case "opaque_argument": plan.Root.Tasks[0].Requires = new() { Kind = "boolean", Boolean = true }; break;
            case "opaque_condition": plan.Root.Tasks[0].Condition = new() { Kind = "boolean", Boolean = true }; break;
            case "opaque_objective": plan.Root.Tasks[0].Objective += " Changed interpretation."; break;
            case "contract": state.Catalog!.Capabilities[0].Description += " Consent required."; break;
            case "contract_conflict":
                var other = PlannerFixture.Clone(state).Catalog!.Capabilities[0]; other.Description += " Different original.";
                state.Discovery.Resolved.Add(other); break;
            case "permission": state.Catalog!.Policy.DeniedCapabilityIds.Add("observe"); break;
            case "order": plan.Root.Tasks.Insert(0, Value("before_external", Number(1))); break;
            case "opaque_after": plan.Root.Tasks.Add(new() { Id = "later_external", Kind = "operation", Operation = "observe", Objective = "Another effect." }); break;
            case "opaque_cleanup": plan.Root.Tasks.RemoveAt(0); plan.Root.Always.Add(new() { Id = "external", Kind = "operation", Operation = "observe", Objective = "An effect during cleanup." }); break;
            case "cleanup": plan.Root.Always.Add(Value("cleanup_value", Number(1))); break;
            case "root_state": state.Construction!.OpenScopes.Clear(); break;
            case "pure_upstream": plan.Root.Tasks[1].Outputs[0].Value.Items[0].Number = 9; break;
        }
        Assert.False(PlanningConstructionDependencies.Preserved(state, plan, proof));
        if (change is "opaque_value" or "opaque_presence" or "opaque_guard" or "opaque_argument" or "opaque_condition" or "order" or "opaque_after" or "opaque_cleanup")
            Assert.Null(PlanningConstructionDependencies.Capture(state, plan, subject));
    }

    private static PlanningSession MixedSession(string kind)
    {
        var state = ExternalSession(kind);
        state.Construction!.Compact!.PresentationVersion = 10;
        state.Request.Options["construction_presentation_version"] = 10;
        state.Plan!.Root.Tasks.Add(Value("calculation", new() { Kind = "arithmetic", Text = "multiply", Items = [Number(4), Number(2)] }));
        state.Plan.Root.Outputs = [new("result", Ref("calculation"))];
        return state;
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("transform")]
    public void ExactReviewedTransitionStateDoesNotClaimIndependentBehavior(string kind)
    {
        var state = ExternalSession(kind); var plan = state.Plan!;
        var before = Snapshot(state);
        var proof = Assert.IsType<JsonObject>(PlanningConstructionDependencies.CaptureExactTransition(state, plan));
        Assert.Equal(before, Snapshot(state));
        Assert.Equal("exact_transition_not_independence", proof["class"]!.ToString());
        Assert.True(PlanningConstructionDependencies.PreservedExactTransition(state, plan, proof));
        Assert.Null(PlanningConstructionDependencies.Capture(state, plan, new() { ["kind"] = "task", ["id"] = "external" }));
        Assert.False(PlanningConstructionDependencies.Preserved(state, plan, proof));
        Assert.False(PlanningConstructionDependencies.Conserves(proof, proof));
        plan.Root.Tasks.Add(Value("unrelated_literal", Number(1)));
        Assert.False(PlanningConstructionDependencies.PreservedExactTransition(state, plan, proof));
    }

    [Theory]
    [InlineData("normative_description")]
    [InlineData("conflicting_same_identity")]
    [InlineData("different_version")]
    [InlineData("missing_contract")]
    [InlineData("denied_contract")]
    [InlineData("permission")]
    [InlineData("original")]
    [InlineData("open_scopes")]
    [InlineData("binding")]
    public void ExactTransitionRejectsChangedOriginalContractStateOrAuthority(string change)
    {
        var state = ExternalSession("operation"); var plan = state.Plan!;
        var proof = Assert.IsType<JsonObject>(PlanningConstructionDependencies.CaptureExactTransition(state, plan));
        switch (change)
        {
            case "normative_description": state.Catalog!.Capabilities[0].Description += " Requires explicit consent."; break;
            case "conflicting_same_identity":
            case "different_version":
                var conflicting = PlannerFixture.Clone(state).Catalog!.Capabilities[0];
                if (change == "different_version") conflicting.Version = "v2";
                else conflicting.Description += " Requires explicit consent.";
                state.Discovery.Resolved.Add(conflicting); break;
            case "missing_contract": state.Catalog!.Capabilities.Clear(); break;
            case "denied_contract": state.Catalog!.Policy.DeniedCapabilityIds.Add("observe"); break;
            case "permission": state.Request.Policy.Instructions += " Restricted permission."; break;
            case "original": state.Construction!.Compact!.Sources[0].Text += " Additional obligation."; break;
            case "open_scopes": state.Construction!.OpenScopes.Clear(); break;
            case "binding": plan.Root.Outputs[0] = new("result", Number(42)); break;
        }
        Assert.False(PlanningConstructionDependencies.PreservedExactTransition(state, plan, proof));
    }

    [Fact]
    public void IdenticalRetainedContractCopiesRemainExactWithoutMutatingProjectedScopes()
    {
        var state = ExternalSession("operation");
        state.Discovery.Resolved.Add(PlannerFixture.Clone(state).Catalog!.Capabilities[0]);
        var before = Snapshot(state);
        var proof = Assert.IsType<JsonObject>(PlanningConstructionDependencies.CaptureExactTransition(state, state.Plan!, []));
        Assert.Equal(before, Snapshot(state));
        Assert.False(PlanningConstructionDependencies.PreservedExactTransition(state, state.Plan!, proof));
        Assert.True(PlanningConstructionDependencies.PreservedExactTransition(state, state.Plan!, proof, []));
    }

    [Fact]
    public void NonliteralCalledGroupAndOrderedForeachConserveAcrossTrailingPureWork()
    {
        var state = Session(); var plan = state.Plan!;
        var before = Snapshot(state);
        var proof = Assert.IsType<JsonObject>(PlanningConstructionDependencies.Capture(state, plan, Output()));
        Assert.Equal(before, Snapshot(state));
        Assert.Equal("deterministic_behavior_not_completion", proof["class"]!.ToString());
        Assert.Contains(proof["projection"]!["tasks"]!.AsArray(), row => row!["id"]!.ToString() == "scale");
        Assert.NotNull(proof["projection"]!["groups"]!["multiply"]);
        plan.Root.Tasks.Add(Value("unrelated", new() { Kind = "arithmetic", Text = "add", Items = [Number(4), Number(5)] }));
        plan.Root.Outputs.Add(new("other", Ref("unrelated")));
        var current = Assert.IsType<JsonObject>(PlanningConstructionDependencies.Capture(state, plan, Output()));
        Assert.True(PlanningConstructionDependencies.Conserves(proof, current));
        Assert.True(PlanningConstructionDependencies.Preserved(state, plan, proof));
        // A new consumer is a new interaction for its caller to review. This
        // projection conserves the original computation, not the new obligation.
        plan.Root.Tasks.Add(Value("consume", new() { Kind = "flatten", Items = [new() { Kind = "array", Items = [Ref("each", "scaled")] }] }));
        Assert.True(PlanningConstructionDependencies.Preserved(state, plan, proof));
        Assert.NotNull(PlanningConstructionDependencies.Capture(state, plan, new() { ["kind"] = "task", ["id"] = "consume" }));
    }

    [Theory]
    [InlineData("upstream_literal")]
    [InlineData("upstream_objective")]
    [InlineData("input_default")]
    [InlineData("input_type")]
    [InlineData("declared_output_type")]
    [InlineData("group_input")]
    [InlineData("group_export")]
    [InlineData("iteration_bound")]
    [InlineData("parallel_order")]
    [InlineData("guard")]
    [InlineData("preceding_work")]
    [InlineData("cleanup")]
    [InlineData("public_binding")]
    [InlineData("policy")]
    [InlineData("catalog_policy")]
    [InlineData("builtin_contract")]
    [InlineData("original")]
    [InlineData("added_original")]
    [InlineData("tenant")]
    [InlineData("session")]
    public void ExactExecutableAndAuthorityChangesInvalidate(string change)
    {
        var state = Session(); var plan = state.Plan!;
        var proof = Assert.IsType<JsonObject>(PlanningConstructionDependencies.Capture(state, plan, Output()));
        switch (change)
        {
            case "upstream_literal": plan.Groups[0].Body.Tasks[0].Outputs[0].Value.Items[1].Number = 3; break;
            case "upstream_objective": plan.Groups[0].Body.Tasks[0].Objective += " Preserve another meaning."; break;
            case "input_default": plan.Inputs[0].Default = new() { Kind = "array", Items = [Number(99)] }; break;
            case "input_type": plan.Inputs[0].Type.MaxItems = 8; break;
            case "declared_output_type": state.Requirements = new() { Outputs = [new() { Name = "values", Type = new() { Kind = "string" } }] }; break;
            case "group_input": plan.Groups[0].Inputs[0].Required = false; break;
            case "group_export": plan.Groups[0].Body.Outputs[0] = new("result", Number(3)); break;
            case "iteration_bound": plan.Root.Tasks[0].MaxItems = 10; break;
            case "parallel_order": plan.Root.Tasks[0].Parallel = true; break;
            case "guard": plan.Root.Tasks[0].Requires = new() { Kind = "boolean", Boolean = true }; break;
            case "preceding_work": plan.Root.Tasks.Insert(0, Value("prior", Number(1))); break;
            case "cleanup": plan.Root.Always.Add(Value("finally_value", Number(1))); break;
            case "public_binding": plan.Root.Outputs[0] = new("values", new() { Kind = "array", Items = [] }); break;
            case "policy": state.Request.Policy.Instructions += " A changed permission."; break;
            case "catalog_policy": state.Catalog!.Policy.DeniedCapabilityIds.Add("capability"); break;
            case "builtin_contract": state.Catalog!.StepContracts["set"] = new JsonObject { ["description"] = "Changed normative meaning." }; break;
            case "original": state.Construction!.Compact!.Sources[0].Text += " A changed original clause."; break;
            case "added_original": state.Construction!.Compact!.Sources.Add(new() { Id = "new", Text = "A new constraint", Version = "new" }); break;
            case "tenant": state.Request.TenantId = "other"; break;
            case "session": state.Request.SessionId = "other"; break;
        }
        Assert.False(PlanningConstructionDependencies.Preserved(state, plan, proof));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("transform")]
    [InlineData("unknown")]
    public void UnclassifiedWorkDoesNotBecomeIndependentByHavingNoEdges(string kind)
    {
        var state = Session(); var plan = state.Plan!;
        var proof = Assert.IsType<JsonObject>(PlanningConstructionDependencies.Capture(state, plan, Output()));
        plan.Root.Tasks.Add(new() { Id = "unrelated_external", Kind = kind, Objective = "Independent by claim only." });
        Assert.Null(PlanningConstructionDependencies.Capture(state, plan, Output()));
        Assert.False(PlanningConstructionDependencies.Preserved(state, plan, proof));
    }

    [Fact]
    public void LookupFieldAndChoiceAreCapturedTransitively()
    {
        var state = Session(); var plan = state.Plan!;
        plan.Root.Tasks.Clear(); plan.Root.Outputs.Clear(); plan.Groups.Clear(); plan.Inputs.Clear();
        plan.Choices.Add(new() { Id = "chosen", Question = "Which exact identity?", Type = new() { Kind = "string" },
            Alternatives = [new("a", "First", new() { Kind = "string", Text = "a" }), new("b", "Second", new() { Kind = "string", Text = "b" })],
            Recommended = "a", Selected = "a" });
        plan.Root.Tasks.Add(Value("records", new() { Kind = "array", Items = [new() { Kind = "object", Members =
            [new("id", new() { Kind = "string", Text = "a" }), new("amount", Number(4))] }] }));
        plan.Root.Tasks.Add(Value("selected", new() { Kind = "lookup", Port = "id", Items = [Ref("records"),
            new() { Kind = "array", Items = [new() { Kind = "choice", Source = "chosen" }] }] }));
        plan.Root.Outputs.Add(new("values", Ref("selected")));
        var proof = Assert.IsType<JsonObject>(PlanningConstructionDependencies.Capture(state, plan, Output()));
        plan.Choices[0].Selected = "b";
        Assert.False(PlanningConstructionDependencies.Preserved(state, plan, proof));
    }

    [Fact]
    public void ClosedScopeProofUsesExactProjectedBoundaryWithoutMutatingAcceptedState()
    {
        var state = Session(); var plan = state.Plan!;
        var scope = "/tasks/each/body";
        state.Construction!.OpenScopes.Add(scope);
        // An unfinished producer cannot supply this output or a closed-boundary proof.
        Assert.Null(PlanningConstructionDependencies.Capture(state, plan, new() { ["kind"] = "scope", ["path"] = scope }));
        var before = Snapshot(state);
        var proof = Assert.IsType<JsonObject>(PlanningConstructionDependencies.Capture(state, plan,
            new() { ["kind"] = "scope", ["path"] = scope }, ["/root"]));
        Assert.Equal(before, Snapshot(state));
        Assert.False(PlanningConstructionDependencies.Preserved(state, plan, proof));
        Assert.True(PlanningConstructionDependencies.Preserved(state, plan, proof, ["/root"]));
    }

    [Fact]
    public void ClosedProducerInOpenAncestorPreservesExactControlsWithoutGrantingAncestorClosure()
    {
        var state = Session(); var plan = state.Plan!;
        var producer = plan.Root.Tasks[0];
        plan.Root.Tasks = [new() { Id = "outer", Kind = "sequence", Objective = "Keep this boundary open for unrelated work.",
            Requires = new() { Kind = "boolean", Boolean = true }, Body = new() { Tasks = [producer] } }];
        plan.Root.Outputs.Clear();
        state.Construction!.OpenScopes.Add("/tasks/outer/body");
        var subject = new JsonObject { ["kind"] = "task", ["id"] = "each" };
        var proof = Assert.IsType<JsonObject>(PlanningConstructionDependencies.Capture(state, plan, subject));
        Assert.Contains(proof["projection"]!["tasks"]!.AsArray(), row => row!["id"]!.ToString() == "outer" && row["context"] is not null);
        Assert.Null(PlanningConstructionDependencies.Capture(state, plan, new() { ["kind"] = "task", ["id"] = "outer" }));
        plan.Root.Tasks[0].Body!.Tasks.Add(Value("later", Number(3)));
        Assert.True(PlanningConstructionDependencies.Preserved(state, plan, proof));
        plan.Root.Tasks[0].Requires!.Boolean = false;
        Assert.False(PlanningConstructionDependencies.Preserved(state, plan, proof));
    }

    [Fact]
    public void CandidateAndAcceptedVerificationUseTheirOwnOpenScopes()
    {
        var state = Session(); var candidate = PlanningConstruction.Clone(state.Plan!);
        candidate.Root.Tasks.Add(Value("trailing", Number(1)));
        Assert.Null(PlanningConstructionDependencies.Capture(state, candidate, Output()));
        state.Construction!.Candidate = new() { Plan = candidate, Hash = PlanningConstruction.Hash(candidate), OpenScopes = ["/root"] };
        var proof = Assert.IsType<JsonObject>(PlanningConstructionDependencies.Capture(state, candidate, Output()));
        Assert.True(PlanningConstructionDependencies.Preserved(state, state.Plan!, proof));
        state.Construction.Candidate.OpenScopes.Add("/groups/multiply/body");
        Assert.Null(PlanningConstructionDependencies.Capture(state, candidate, Output()));
        Assert.True(PlanningConstructionDependencies.Preserved(state, state.Plan!, proof));
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void HistoricalPresentationsCannotAcquireProspectiveProof(int presentation)
    {
        var state = Session(); state.Construction!.Compact!.PresentationVersion = presentation;
        var before = Snapshot(state);
        Assert.Null(PlanningConstructionDependencies.Capture(state, state.Plan!, Output()));
        Assert.Null(PlanningConstructionDependencies.CaptureExactTransition(state, state.Plan!));
        Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void AmbiguousIdentityInvalidScopeAndUnknownSubjectFailClosed()
    {
        var state = Session();
        Assert.Null(PlanningConstructionDependencies.Capture(state, state.Plan!, new() { ["kind"] = "output", ["name"] = "missing" }));
        Assert.Null(PlanningConstructionDependencies.Capture(state, state.Plan!, new() { ["kind"] = "output", ["name"] = "values", ["authority"] = true }));
        Assert.Null(PlanningConstructionDependencies.Capture(state, state.Plan!, Output(), ["/root", "/missing"]));
        state.Plan!.Root.Tasks.Add(Value("each", Number(1)));
        Assert.Null(PlanningConstructionDependencies.Capture(state, state.Plan, Output()));
    }

    private static PlanningSession ExternalSession(string kind)
    {
        var state = Session();
        state.Catalog!.AllowedStepTypes.AddRange(["mcp.call", "llm.call", "template.render"]);
        state.Catalog.Capabilities.Add(new() { Id = "observe", Version = "v1", Kind = "tool", StepType = "mcp.call",
            Server = "source", Method = "observe", Description = "Observe the exact authorized external record.", EffectKind = "unknown",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["result"] = new JsonObject { ["type"] = "number" } },
                ["required"] = new JsonArray("result"), ["additionalProperties"] = false } });
        var task = new PlanTask { Id = "external", Kind = kind, Objective = "Obtain the exact required value." };
        if (kind == "operation") task.Operation = "observe";
        else
        {
            task.Mode = "interpret"; task.Inputs = [new("supplied", Number(1))];
            task.ResultType = new() { Kind = "object", Fields = [new() { Name = "result", Type = new() { Kind = "number" } }] };
        }
        state.Plan = new() { Root = new() { Tasks = [task], Outputs = [new("result", Ref("external", "result"))] } };
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(state.Plan, state.Catalog, state.Request, new HashSet<string>(["/root"])).Diagnostics);
        return state;
    }

    private static PlanningSession Session()
    {
        var state = PlannerFixture.Session();
        state.Request.SessionId = "dependency-proof";
        state.Request.Options["construction_version"] = "2";
        state.Request.Options["construction_presentation_version"] = 9;
        state.Catalog = new() { AllowedStepTypes = ["set", "foreach", "parallel", "switch"] };
        const string original = "Return every original number multiplied by two, in original order.";
        state.Construction = new() { Version = 2, Compact = new() { PresentationVersion = 9,
            Sources = [new() { Id = "source", Text = original, Version = PlanningGraphCompiler.Fingerprint(original), Indexed = true }] } };
        state.Plan = new()
        {
            Inputs = [new() { Name = "numbers", Type = new() { Kind = "array", Items = new() { Kind = "number" } } }],
            Groups = [new() { Id = "multiply", Inputs = [new() { Name = "number", Type = new() { Kind = "number" } }],
                Body = new() { Tasks = [Value("scale", new() { Kind = "arithmetic", Text = "multiply", Items = [new() { Kind = "input", Source = "number" }, Number(2)] })],
                    Outputs = [new("result", Ref("scale"))] } }],
            Root = new() { Tasks = [new() { Id = "each", Kind = "foreach", Objective = "Multiply each original item through the closed group.",
                Items = new() { Kind = "input", Source = "numbers" }, Body = new()
                {
                    Tasks = [new() { Id = "invoke", Kind = "call", Group = "multiply", Objective = "Compute this item's exact double.",
                        Inputs = [new("number", new() { Kind = "item" })] }], Outputs = [new("scaled", Ref("invoke", "result"))]
                } }], Outputs = [new("values", Ref("each", "scaled"))] }
        };
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(state.Plan, state.Catalog, state.Request, new HashSet<string>(["/root"])).Diagnostics);
        return state;
    }

    private static JsonObject Output() => new() { ["kind"] = "output", ["name"] = "values" };
    private static PlanTask Value(string id, TaskValue value) => new() { Id = id, Kind = "value", Objective = "Compute " + id, Outputs = [new("value", value)] };
    private static TaskValue Ref(string id, string port = "value") => new() { Kind = "output", Source = id, Port = port };
    private static TaskValue Number(double value) => new() { Kind = "number", Number = value };
    private static string Snapshot(PlanningSession state) => PlanningConstruction.Node(state, PlanningJsonContext.Default.PlanningSession)!.ToJsonString();
}
