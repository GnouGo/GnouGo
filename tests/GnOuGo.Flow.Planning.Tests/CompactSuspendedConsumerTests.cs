using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactSuspendedConsumerTests
{
    private const string ResumeAction = "resumeStep (only when offered, with its exact producer checkpoint and binding), ";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstAcceptedTransformPortResumesThroughTheIssuedSelectionWithoutAnotherProducer(bool retained)
    {
        var state = await StringProducerState(suspend: true);
        var plan = PlanningConstruction.Hash(state.Plan);
        var continuation = Assert.Single(state.Construction!.Compact!.SuspendedSteps!);
        var binding = new TaskValue { Kind = "output", Source = "format_label", Port = "label" };
        foreach (var invalidPort in new string?[] { null, "missing" })
        {
            var invalid = PlannerFixture.Clone(state); var before = Snapshot(invalid);
            Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ResumeStep(invalid, continuation.Id, 1,
                new() { Kind = "output", Source = "format_label", Port = invalidPort }));
            Assert.Equal(before, Snapshot(invalid));
        }
        string? issued = null;
        if (retained)
        {
            // This is the exact previous v5 instruction body, whose final action
            // list omitted resumeStep although its issued grammar already allowed it.
            var request = SelectionRequest(state, PlanningCompactConstruction.SelectGuidance(state)
                .Replace(ResumeAction, "", StringComparison.Ordinal));
            state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.SelectPurpose, Request = request };
            state.ModelCalls = 1;
            state = PlannerFixture.Clone(state);
            issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        }
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            if (issued is not null) Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            else Assert.Contains(ResumeAction, request.Prompt, StringComparison.Ordinal);
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            Assert.True(context["continuation"]!["canResume"]!.GetValue<bool>());
            Assert.Equal(1, Assert.Single(context["continuation"]!["acceptedCheckpoints"]!.AsArray())!["sequence"]!.GetValue<int>());
            var producer = context["interfaces"]!.AsArray().Single(item => item?["task"]?.ToString() == "format_label")!;
            Assert.Equal("label", Assert.Single(producer["ports"]!.AsArray())!.ToString());
            var response = new JsonObject
            {
                ["baseHash"] = plan, ["step"] = null, ["evidenceRequests"] = null, ["clarifications"] = null,
                ["resumeStep"] = new JsonObject { ["continuationId"] = continuation.Id, ["producerCheckpoint"] = 1,
                    ["binding"] = new JsonObject { ["kind"] = "output", ["source"] = "format_label", ["port"] = "label" } }
            };
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema!));
            return new() { Json = response };
        } };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts); Assert.Equal(0, state.Construction!.DraftCorrections);
        Assert.Null(state.PendingCall); Assert.Null(state.Construction.Candidate);
        Assert.Single(state.Construction.Checkpoints); Assert.Single(state.Plan!.Root.Tasks);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        var resumed = state.Construction.Compact!.CurrentStep!;
        Assert.Equal("Consume the formatted label.", resumed.Objective);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(binding, PlanningJsonContext.Default.TaskValue),
            JsonSerializer.SerializeToNode(Assert.Single(resumed.Inputs), PlanningJsonContext.Default.TaskValue)));
        Assert.Equal("string", Assert.Single(PlanningCompactConstruction.ImportedInterfaces(state, resumed))!["schema"]!["type"]!.ToString());
        Assert.Equal("resuming", Assert.Single(state.Construction.Compact.SuspendedSteps!).Status);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task HistoricalSelectionAndPendingRequestKeepTheirOriginalActionContract(int presentation)
    {
        var state = await StringProducerState(suspend: false, presentation);
        var guidance = PlanningCompactConstruction.SelectGuidance(state);
        Assert.DoesNotContain("resumeStep", guidance, StringComparison.Ordinal);
        if (presentation == 2) Assert.EndsWith("Return step, original evidenceRequests or clarifications for missing business facts only. Evidence grants no authority.",
            guidance, StringComparison.Ordinal);
        else Assert.EndsWith("Return exactly one non-null action; all others null: step (kind:null starts capability discovery), " +
            "evidenceRequests (copy issued addresses exactly), or clarifications (missing business facts). Evidence grants no authority.", guidance, StringComparison.Ordinal);
        var request = SelectionRequest(state, guidance);
        Assert.False(request.StructuredOutputSchema!["properties"]!.AsObject().ContainsKey("resumeStep"));
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.SelectPurpose, Request = request };
        state.ModelCalls = 1;
        var bytes = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var plan = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (actual, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            Assert.Equal(bytes, JsonSerializer.Serialize(actual, PlanningJsonContext.Default.LLMRequest));
            var response = new JsonObject { ["baseHash"] = plan, ["evidenceRequests"] = null, ["clarifications"] = null,
                ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = "value", ["objective"] = "Consume the formatted label.",
                    ["requirementIds"] = new JsonArray("req"), ["inputs"] = new JsonArray(new JsonObject
                        { ["kind"] = "output", ["source"] = "format_label", ["port"] = "label" }),
                    ["groupId"] = null, ["dependencyTaskIds"] = new JsonArray(), ["expectedOutputs"] = new JsonArray(),
                    ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray() } };
            if (presentation >= 3) response["step"]!["prerequisites"] = null;
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, actual.StructuredOutputSchema!));
            return new() { Json = response };
        } };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics); Assert.Single(runtime.Calls); Assert.Null(state.PendingCall);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan)); Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal("label", Assert.Single(state.Construction.Compact!.CurrentStep!.Inputs).Port);
        Assert.Null(state.Construction.Compact.SuspendedSteps);
    }

    [Fact]
    public void AConsumerResumesOnlyWithAnExplicitNewAcceptedTypedProducer()
    {
        var state = State(); var original = PlanningConstruction.Hash(state.Plan);
        var decision = Suspend();
        PlanningCompactConstruction.Reselect(state, decision);
        var item = Assert.Single(state.Construction!.Compact!.SuspendedSteps!);
        Assert.Equal(original, PlanningConstruction.Hash(state.Plan));
        Assert.Equal("consumer", item.Consumer.Id); Assert.Equal("/root", item.Consumer.Scope);
        Assert.Equal(["req"], item.Consumer.RequirementIds); Assert.Equal("number", item.Prerequisite.ExpectedType.Kind);
        Assert.Equal("suspend", state.Construction.Compact.RejectedSteps[0]["disposition"]!.ToString());
        Assert.False(PlanningCompactConstruction.ContinuationContext(state)!["canResume"]!.GetValue<bool>());

        AcceptProducer(state, "first", 3);
        AcceptProducer(state, "second", 7);
        Assert.Null(state.Construction.Compact.CurrentStep); // No automatic producer selection.
        Assert.True(PlanningCompactConstruction.ContinuationContext(state)!["canResume"]!.GetValue<bool>());
        var restored = PlannerFixture.Clone(state);
        PlanningCompactConstruction.ResumeStep(restored, item.Id, 1, Output("first"));
        var resumed = restored.Construction!.Compact!.CurrentStep!;
        Assert.Equal("Use the missing number to produce the result.", resumed.Objective);
        Assert.Equal(["req"], resumed.RequirementIds); Assert.Empty(resumed.CloseRequirementIds);
        Assert.Equal(PlanningConstruction.Hash(restored.Plan), resumed.BaseHash);
        Assert.Equal("first", Assert.Single(resumed.Inputs).Source);
        Assert.Equal("resuming", Assert.Single(restored.Construction.Compact.SuspendedSteps!).Status);
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ResumeStep(restored, item.Id, 1, Output("first")));
        Assert.Equal(0, restored.ModelCalls); Assert.Equal(0, restored.ReplanAttempts);

        resumed.Kind = "value";
        restored.Construction.Checkpoints.Add(new() { Sequence = 3, PlanHash = resumed.BaseHash });
        PlanningCompactConstruction.Accept(restored);
        Assert.Equal("completed", Assert.Single(restored.Construction.Compact.SuspendedSteps!).Status);
        Assert.Null(restored.Construction.Compact.ResumingStepId);
    }

    [Fact]
    public void ResumedConsumerUsesItsOwnReviewsInsteadOfTheLatestOtherRejection()
    {
        var state = State();
        var task = new PlanTask { Id = "consumer", Kind = "value", Objective = "Use the requested number.",
            Outputs = [new("number", new() { Kind = "number", Number = 0 })] };
        var extension = new PlanningConstructionExtension { Scope = "/root", RequirementIds = ["req"], Tasks = [task] };
        state.Construction!.Candidate = PlanningConstruction.Apply(state, extension);
        var context = new JsonObject
        {
            ["requirements"] = new JsonArray(new JsonObject { ["id"] = "req", ["description"] = state.Request.Prompt }),
            ["draft"] = PlanningJsonTransport.TaskPlanPart(JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension))
        };
        var finding = new JsonObject { ["kind"] = "omission", ["location"] = "/draft/tasks/0",
            ["evidence"] = new JsonArray("/requirements/0", "/draft/tasks/0"), ["explanation"] = "The requested consumer needs the selected source.",
            ["uncertainty"] = "Advisory synthetic finding.", ["fact"] = null };
        var witness = PlanningConstruction.DefectWitness(state, context, new JsonArray(finding))!;
        Assert.NotNull(witness);
        state.Construction.Candidate.Reviews.Add(new JsonObject { ["defectWitness"] = witness });
        PlanningCompactConstruction.Reselect(state, Suspend());
        var item = Assert.Single(state.Construction.Compact!.SuspendedSteps!);
        AcceptProducer(state, "producer", 3);
        state.Construction.Candidate = null;
        state.Construction.Compact.CurrentStep = Consumer(state, "other");
        state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", "An unrelated selection is unsuitable.")];
        PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection { Disposition = "abandon", Reason = "Abandon the other selection." });
        state.Construction.Compact.CurrentStep = Consumer(state, "producer");
        PlanningCompactConstruction.Accept(state); // End the other replacement chain.
        state = PlannerFixture.Clone(state);
        PlanningCompactConstruction.ResumeStep(state, item.Id, 1, Output("producer"));
        var next = PlanningConstruction.Apply(state, extension);
        PlanningConstruction.CarryDefectWitnesses(state, null, next);
        Assert.Equal(witness.ToJsonString(), Assert.Single(next.Reviews)["defectWitness"]!.ToJsonString());
        Assert.Empty(state.Construction!.Compact!.RejectedSteps[^1]["reviews"]!.AsArray());
        state.Construction.Candidate = next;
        var prior = Assert.Single(PlanningConstruction.DefectProgressContext(state, context)!)!;
        Assert.Equal("basis_changed", prior["status"]!.ToString());
        Assert.Null(next.AuthorizedPaths); Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("checkpoint")]
    [InlineData("unaccepted")]
    [InlineData("future")]
    [InlineData("invalidated")]
    public void InvalidResumeDoesNotChangeAcceptedOrContinuationState(string invalid)
    {
        var state = State(); PlanningCompactConstruction.Reselect(state, Suspend());
        AcceptProducer(state, "producer", 1);
        var item = state.Construction!.Compact!.SuspendedSteps![0];
        var binding = Output("producer"); var checkpoint = 1;
        if (invalid == "type") state.Plan!.Root.Tasks[0].Outputs[0] = new("number", new() { Kind = "string", Text = "not a number" });
        if (invalid == "checkpoint") checkpoint = 0;
        if (invalid == "unaccepted") state.Construction.Checkpoints[0].TaskIds.Clear();
        if (invalid == "future") binding.Source = "future";
        if (invalid == "invalidated") state.Construction.Checkpoints[0].InvalidatedTaskIds = ["producer"];
        var before = Snapshot(state);
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ResumeStep(state, item.Id, checkpoint, binding));
        Assert.Equal(before, Snapshot(state));
    }

    [Fact]
    public void FourExplicitPrerequisitesBoundTheChainAndAbandonIsNotSuspension()
    {
        var state = State();
        for (var index = 0; index < 4; index++)
        {
            state.Construction!.Compact!.CurrentStep = Consumer(state, "consumer_" + index);
            state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", "An exact prerequisite is missing.")];
            PlanningCompactConstruction.Reselect(state, Suspend());
        }
        var steps = state.Construction!.Compact!.SuspendedSteps!;
        Assert.Equal(4, steps.Count); Assert.Null(steps[0].ParentId);
        for (var index = 1; index < 4; index++) Assert.Equal(steps[index - 1].Id, steps[index].ParentId);
        state.Construction.Compact.CurrentStep = Consumer(state, "fifth");
        state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", "Another prerequisite is missing.")];
        var before = Snapshot(state);
        Assert.Contains(Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.Reselect(state, Suspend())).Diagnostics,
            diagnostic => diagnostic.Code == "CONSTRUCTION_CONTINUATION_LIMIT");
        Assert.Equal(before, Snapshot(state));
        PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection { Reason = "This proposed action is unsuitable.", Disposition = "abandon" });
        Assert.Equal(4, steps.Count); Assert.Equal("abandon", state.Construction.Compact.RejectedSteps[^1]["disposition"]!.ToString());
    }

    [Fact]
    public void ExactEvidenceAndDependencyChangesInvalidateOnlyTheirContinuations()
    {
        var state = State();
        var accepted = new PlanTask { Id = "existing", Kind = "value", Objective = "An existing input.",
            Outputs = [new("number", new() { Kind = "number", Number = 1 })] };
        state.Plan!.Root.Tasks.Add(accepted);
        state.Construction!.Compact!.CurrentStep!.Inputs = [Output("existing")];
        state.Construction.Compact.CurrentStep.InputTaskIds = ["existing"];
        PlanningCompactConstruction.Reselect(state, Suspend());
        AcceptProducer(state, "producer", 2);
        var unchanged = PlannerFixture.Clone(state);
        unchanged.Plan!.Root.Tasks.Add(new() { Id = "unrelated", Kind = "value", Objective = "Unrelated accepted work.", Outputs = [new("x", new() { Kind = "number", Number = 3 })] });
        PlanningCompactConstruction.ReconcileContinuations(unchanged);
        Assert.Equal("waiting", unchanged.Construction!.Compact!.SuspendedSteps![0].Status);
        foreach (var sourceChange in new[] { false, true })
        {
            var changed = PlannerFixture.Clone(state);
            if (sourceChange) changed.Construction!.Compact!.Sources[0].Text += "changed";
            else changed.Plan!.Root.Tasks.RemoveAll(task => task.Id == "existing");
            PlanningCompactConstruction.ReconcileContinuations(changed);
            Assert.Equal("invalidated", changed.Construction!.Compact!.SuspendedSteps![0].Status);
            Assert.Null(PlanningCompactConstruction.ContinuationContext(changed));
        }
    }

    [Fact]
    public void AnOlderAcceptedProducerRemainsReachableThroughTheExistingBoundedCheckpointPages()
    {
        var state = State(); PlanningCompactConstruction.Reselect(state, Suspend());
        for (var index = 1; index <= 9; index++) AcceptProducer(state, "producer" + index, index);
        var context = PlanningCompactConstruction.ContinuationContext(state)!;
        Assert.Equal("page:checkpoints:0", context["directory"]!.ToString());
        Assert.Equal(4, context["acceptedCheckpoints"]!.AsArray().Count);
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, context["directory"]!.ToString(), out var page));
        Assert.Equal("page:checkpoints:4", page!["next"]!.ToString());
        state.Construction!.Evidence = new JsonObject { [context["directory"]!.ToString()] = page };
        context = PlanningCompactConstruction.ContinuationContext(state)!;
        Assert.Equal(8, context["acceptedCheckpoints"]!.AsArray().Count);
        Assert.Equal(1, context["acceptedCheckpoints"]![0]!["sequence"]!.GetValue<int>());
        var historical = PlannerFixture.Clone(state); historical.Construction!.Compact!.PresentationVersion = 4;
        Assert.True(PlanningCompactConstruction.LoadEvidence(historical, "page:checkpoints:0", out var oldPage));
        Assert.False(oldPage!.AsObject().ContainsKey("next"));
        PlanningCompactConstruction.ResumeStep(state, state.Construction.Compact!.SuspendedSteps![0].Id, 1, Output("producer1"));
        Assert.Equal("producer1", Assert.Single(state.Construction.Compact.CurrentStep!.Inputs).Source);
    }

    [Fact]
    public void InvalidatingAnUnrelatedCheckpointSiblingDoesNotDiscardASurvivingProducer()
    {
        var state = State(); PlanningCompactConstruction.Reselect(state, Suspend());
        AcceptProducer(state, "producer", 2);
        var checkpoint = state.Construction!.Checkpoints[0];
        checkpoint.TaskIds.Add("removed_sibling"); checkpoint.InvalidatedTaskIds = ["removed_sibling"];
        PlanningCompactConstruction.ReconcileContinuations(state);
        var context = PlanningCompactConstruction.ContinuationContext(state)!;
        Assert.True(context["canResume"]!.GetValue<bool>());
        Assert.Equal(1, Assert.Single(context["acceptedCheckpoints"]!.AsArray())!["sequence"]!.GetValue<int>());
        PlanningCompactConstruction.ResumeStep(state, context["id"]!.ToString(), 1, Output("producer"));
        Assert.Equal("producer", Assert.Single(state.Construction.Compact!.CurrentStep!.Inputs).Source);
    }

    [Fact]
    public void ClearingAnInterruptedTentativeSelectionCanResumeOnlyAnUnchangedAcceptedProducer()
    {
        var state = State(); PlanningCompactConstruction.Reselect(state, Suspend()); AcceptProducer(state, "producer", 2);
        var id = state.Construction!.Compact!.SuspendedSteps![0].Id;
        PlanningCompactConstruction.ResumeStep(state, id, 1, Output("producer"));
        state.Construction.Compact.CurrentStep = null; // Existing rollback clears this tentative selection.
        PlanningCompactConstruction.ReconcileContinuations(state);
        Assert.Null(state.Construction.Compact.ResumingStepId);
        Assert.Equal("waiting", state.Construction.Compact.SuspendedSteps[0].Status);
        var removed = PlannerFixture.Clone(state); removed.Plan!.Root.Tasks.Clear();
        PlanningCompactConstruction.ReconcileContinuations(removed);
        Assert.Equal("invalidated", removed.Construction!.Compact!.SuspendedSteps![0].Status);
        PlanningCompactConstruction.ResumeStep(state, id, 1, Output("producer"));
        Assert.Equal("producer", Assert.Single(state.Construction.Compact.CurrentStep!.Inputs).Source);
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("changed")]
    [InlineData("denied")]
    [InlineData("conflicting_same_version")]
    public void RetainedOperationMustStillResolveToOneExactPolicyAllowedContract(string change)
    {
        var state = State();
        var capability = new PlanningCapability { Id = "read", Version = "v1", Kind = "tool", StepType = "mcp.call",
            Server = "source", Method = "read", Description = "Read the authorized numeric record.", EffectKind = "read",
            InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["number"] = new JsonObject { ["type"] = "number" } }, ["additionalProperties"] = false } };
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"], Capabilities = [capability] };
        state.Construction!.Compact!.CurrentStep!.Kind = "operation";
        state.Construction.Compact.CurrentStep.OperationIds = ["read"];
        PlanningCompactConstruction.Reselect(state, Suspend());
        PlanningCompactConstruction.ReconcileContinuations(state);
        Assert.Equal("waiting", state.Construction.Compact.SuspendedSteps![0].Status);
        if (change == "changed") capability.Description += " A new normative constraint.";
        else if (change == "denied") state.Catalog.Policy.DeniedCapabilityIds.Add("read");
        else
        {
            var another = JsonSerializer.Deserialize(JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability), PlanningJsonContext.Default.PlanningCapability)!;
            if (change == "conflicting_same_version") another.Description += " A conflicting same-version constraint.";
            else another.Version = "v2";
            state.Discovery.Resolved.Add(another);
        }
        PlanningCompactConstruction.ReconcileContinuations(state);
        Assert.Equal("invalidated", state.Construction.Compact.SuspendedSteps[0].Status);
        Assert.Null(PlanningCompactConstruction.ContinuationContext(state));
    }

    [Theory]
    [InlineData(4, "in_progress")]
    [InlineData(5, "closed")]
    public void ContributionsPreserveClosedStatusOnlyUnderTheNewCapturedPolicy(int version, string expected)
    {
        var state = State(version); state.Construction!.Requirements[0].Status = "closed";
        PlanningCompactConstruction.Accept(state);
        Assert.Equal(expected, state.Construction.Requirements[0].Status);
        if (version == 4) Assert.DoesNotContain("suspendedSteps", Snapshot(state), StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredReselectionUsesExistingTypesAndOldIssuedStringsRemainExact()
    {
        var state = State();
        var original = PlanningSchemas.Object(("reselectStep", PlanningSchemas.Nullable(PlanningSchemas.String())));
        var bytes = original.ToJsonString();
        var historical = State(4);
        PlanningCompactConstruction.ConstrainReselectionSchema(historical, original);
        Assert.Equal(bytes, original.ToJsonString());
        Assert.Equal("legacy", PlanningCompactConstruction.ReadReselection(state, JsonValue.Create("Retained old reason")!, original).Disposition);
        Assert.Equal(bytes, original.ToJsonString());
        var schema = original.DeepClone().AsObject();
        PlanningCompactConstruction.ConstrainReselectionSchema(state, schema);
        schema = PlanningContractValidation.ProjectStructuredOutputSchema(schema);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var decision = JsonSerializer.SerializeToNode(Suspend(), PlanningJsonContext.Default.PlanningConstructionReselection)!;
        // The issued type omits nullable DTO-only members, as normal TaskType presentation does.
        decision["prerequisite"]!["expectedType"] = new JsonObject { ["kind"] = "number", ["nullable"] = false };
        var response = new JsonObject { ["reselectStep"] = decision };
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, schema));
        Assert.Equal("suspend", PlanningCompactConstruction.ReadReselection(state, decision, schema).Disposition);
        decision["disposition"] = "abandon";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(response, schema));
    }

    private static async Task<PlanningSession> StringProducerState(bool suspend, int presentation = 5)
    {
        var state = State(presentation);
        state.IntentVersion = 3; state.Revision = 1; state.Status = PlanningStatus.Generating;
        state.Request.Prompt = "Produce and consume the requested formatted label.";
        state.Requirements!.Outcomes = [new("req", state.Request.Prompt)];
        var compact = state.Construction!.Compact!;
        var source = compact.Sources[0]; source.Text = state.Request.Prompt;
        source.Version = PlanningGraphCompiler.Fingerprint(source.Text); source.InterfaceKind = "none";
        PlanningCompactConstruction.InitializeSources(state);
        foreach (var page in compact.Sources) page.Indexed = true;
        compact.InterfaceExtractions = [];
        compact.CurrentStep!.Objective = "Consume the formatted label.";
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        if (suspend)
        {
            var decision = Suspend(); decision.Prerequisite!.Need = "The formatted label.";
            decision.Prerequisite.ExpectedType = new() { Kind = "string" };
            PlanningCompactConstruction.Reselect(state, decision);
        }
        state.Plan!.Root.Tasks.Add(new() { Id = "format_label", Kind = "transform", Mode = "interpret",
            Objective = "Format the supplied label.", Inputs = [new("text", new() { Kind = "string", Text = "sample" })],
            ResultType = new() { Kind = "object", Fields = [new() { Name = "label", Required = true, Type = new() { Kind = "string" } }] } });
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(state.Plan, state.Catalog, state.Request,
            state.Construction.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        state.Construction.Checkpoints.Add(new() { Sequence = 1, PlanHash = PlanningConstruction.Hash(state.Plan),
            TaskIds = ["format_label"], RequirementIds = ["req"] });
        compact.CurrentStep = Consumer(state, "format_label");
        PlanningCompactConstruction.Accept(state); state.Diagnostics.Clear();
        return state;
    }

    private static LLMRequest SelectionRequest(PlanningSession state, string guidance)
    {
        var request = PlanningCompactConstruction.Request(guidance, PlanningCompactConstruction.SelectionContext(state),
            PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose);
        return PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.SelectPurpose, request.Prompt,
            request.StructuredOutputSchema!.AsObject(), state.Construction!.Limits.DraftOutputTokens);
    }

    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) => new HybridWorkflowPlanner()
        .AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);

    private static PlanningSession State(int version = 5)
    {
        const string original = "Produce the requested numeric result from the authorized input.";
        var state = PlannerFixture.CapturedPresentation(new() { Request = new() { TenantId = "continuations", Prompt = original,
            Options = new() { ["automatic_correction"] = true } } }, version);
        state.Requirements = new() { Inputs = [], Outputs = [], Outcomes = [new("req", original)] };
        state.Catalog = new(); state.Plan = new();
        state.Construction!.Requirements = [new() { Id = "req", Status = "in_progress" }];
        state.Construction.Compact!.Sources = [new() { Id = "source", Kind = "requirement", Text = original,
            Version = PlanningGraphCompiler.Fingerprint(original), Indexed = true }];
        state.Construction.Compact.RequirementSources["req"] = ["source"];
        state.Construction.Compact.CurrentStep = Consumer(state, "consumer");
        state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", "The consumer needs an accepted number.")];
        return state;
    }

    private static PlanningConstructionStep Consumer(PlanningSession state, string id) => new()
    {
        Id = id, BaseHash = PlanningConstruction.Hash(state.Plan), Kind = "value", Scope = "/root",
        Objective = id == "consumer" ? "Use the missing number to produce the result." : "Produce " + id,
        RequirementIds = ["req"], DiscoveryComplete = true
    };
    private static PlanningConstructionReselection Suspend() => new()
    {
        Reason = "The selected consumer needs an accepted numeric producer.", Disposition = "suspend",
        Prerequisite = new() { Need = "The exact numeric input.", ExpectedType = new() { Kind = "number" }, EvidenceAddresses = ["requirement:req", "source:source"] }
    };
    private static TaskValue Output(string source) => new() { Kind = "output", Source = source, Port = "number" };
    private static void AcceptProducer(PlanningSession state, string id, int value)
    {
        state.Plan!.Root.Tasks.Add(new() { Id = id, Kind = "value", Objective = "Produce the prerequisite number.", Outputs = [new("number", new() { Kind = "number", Number = value })] });
        state.Construction!.Checkpoints.Add(new() { Sequence = state.Construction.Checkpoints.Count + 1,
            PlanHash = PlanningConstruction.Hash(state.Plan), TaskIds = [id], RequirementIds = ["req"] });
        state.Construction.Compact!.CurrentStep = Consumer(state, id);
        PlanningCompactConstruction.Accept(state);
        state.Diagnostics.Clear();
    }
    private static string Snapshot(PlanningSession state) => JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
}
