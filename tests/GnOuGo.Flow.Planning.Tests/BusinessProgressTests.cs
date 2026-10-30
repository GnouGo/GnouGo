using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessProgressTests
{
    [Fact]
    public async Task RenamedEmptyCheckpointsCannotBuyASeventhNonproductiveCall()
    {
        var state = PlannerFixture.Session(); state.Plan = new();
        state.Construction!.Requirements.Add(new() { Id = "result" });
        PlanningProgress.Initialize(state);
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = new JsonObject { ["navigation"] = "received" } } };
        for (var index = 0; index < 6; index++)
        {
            await PlanningModelCalls.CallAsync(state, runtime, PlanningCompactConstruction.SelectPurpose, "Retained administrative decision",
                PlanningSchemas.Object(("navigation", PlanningSchemas.String())), PlannerFixture.Ct);
            state.Plan!.Root.Tasks.Add(new() { Id = "renamed_" + index, Kind = "sequence", Objective = "Different wording " + index, Body = new() });
            state.Construction!.Checkpoints.Add(new() { Sequence = index + 1, TaskIds = ["renamed_" + index] });
            PlanningProgress.Record(state);
            state = PlannerFixture.Clone(state);
        }
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningCompactConstruction.SelectPurpose, "Another navigation", PlanningSchemas.Object(("navigation", PlanningSchemas.String())), PlannerFixture.Ct));
        Assert.Equal("REPLAN_NO_PROGRESS", failure.Code); Assert.Contains("result", failure.Message);
        Assert.Equal(6, runtime.Calls.Count); Assert.Equal(6, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.All(state.RequestMeasurements!, measurement => Assert.All(measurement.Transitions!, transition =>
        { Assert.Equal("administrative", transition.Kind); Assert.Equal(0, transition.ProductiveChanges); }));
    }

    [Fact]
    public void RenamingAndMovingThroughSequenceScaffoldingPreservesBehaviorIdentity()
    {
        var first = PlannerFixture.Session();
        first.Plan = ProducerPlan("first", "second");
        var renamed = PlannerFixture.Clone(first);
        renamed.Plan = ProducerPlan("renamed", "another");
        foreach (var task in renamed.Plan.Root.Tasks) task.Objective = "Different label";
        var tasks = renamed.Plan.Root.Tasks;
        renamed.Plan.Root.Tasks = [new() { Id = "wrapper", Kind = "sequence", Body = new() { Tasks = tasks } }];
        Assert.Equal(PlanningProgress.Fingerprint(first), PlanningProgress.Fingerprint(renamed));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("otherwise")]
    public void TransparentSequenceInsideAControlDoesNotInventBehavior(string branch)
    {
        var first = PlannerFixture.Session(); first.Plan = new();
        var body = ProducerPlan("producer", "consumer").Root;
        first.Plan.Root.Tasks.Add(new() { Id = "branch", Kind = "conditional", Condition = new() { Kind = "boolean", Boolean = true },
            Body = branch == "body" ? body : new(), Otherwise = branch == "otherwise" ? body : new() });
        var wrapped = PlannerFixture.Clone(first);
        var control = wrapped.Plan!.Root.Tasks[0];
        var target = branch == "body" ? control.Body! : control.Otherwise!;
        target.Tasks = [new() { Id = "wrapper", Kind = "sequence", Body = new() { Tasks = target.Tasks } }];
        Assert.Equal(PlanningProgress.Fingerprint(first), PlanningProgress.Fingerprint(wrapped));
    }

    [Theory]
    [InlineData("guard")]
    [InlineData("cleanup")]
    public void ExecutableSequenceConstraintsAndCleanupRemainProductive(string change)
    {
        var first = PlannerFixture.Session(); first.Plan = new();
        first.Plan.Root.Tasks.Add(new() { Id = "sequence", Kind = "sequence", Body = ProducerPlan("producer", "consumer").Root });
        var changed = PlannerFixture.Clone(first);
        var sequence = changed.Plan!.Root.Tasks[0];
        if (change == "guard") sequence.Requires = new() { Kind = "boolean", Boolean = false };
        else sequence.Body!.Always.Add(new() { Id = "cleanup", Kind = "operation", Operation = "release" });
        Assert.NotEqual(PlanningProgress.Fingerprint(first), PlanningProgress.Fingerprint(changed));
    }

    [Theory]
    [InlineData("conditional")]
    [InlineData("parallel")]
    public void DistinctControlEdgesRemainDistinctNavigationStrategies(string kind)
    {
        var state = PlannerFixture.Session(); state.Plan = new();
        state.Plan.Root.Tasks.Add(new() { Id = "control", Kind = kind, Body = kind == "conditional" ? new() : null,
            Otherwise = kind == "conditional" ? new() : null, Branches = kind == "parallel" ? [new(), new()] : [] });
        var first = JsonNode.Parse("""{"step":{"scope":"","kind":"value","requirementIds":["work"]}}""")!;
        var next = first.DeepClone();
        first["step"]!["scope"] = kind == "conditional" ? "/tasks/control/body" : "/tasks/control/branches/0";
        next["step"]!["scope"] = kind == "conditional" ? "/tasks/control/otherwise" : "/tasks/control/branches/1";
        Assert.NotEqual(PlanningProgress.Strategy(state, "selection", first), PlanningProgress.Strategy(state, "selection", next));
    }

    [Fact]
    public void ProducerAvailabilityAndRepeatedExecutableOccurrencesRemainObservable()
    {
        var state = PlannerFixture.Session(); state.Plan = new();
        state.Plan.Root.Tasks.Add(new() { Id = "pending", Kind = "sequence", Body = new() { Tasks = [
            new() { Id = "inner", Kind = "value", Outputs = [new("number", new() { Kind = "number", Number = 7 })] }] } });
        state.Construction!.OpenScopes.Add("/tasks/pending/body");
        var nested = PlanningProgress.Fingerprint(state);
        var available = PlannerFixture.Clone(state);
        var producer = Assert.Single(available.Plan!.Root.Tasks[0].Body!.Tasks);
        available.Plan.Root.Tasks[0].Body!.Tasks.Clear(); available.Plan.Root.Tasks.Add(producer);
        Assert.NotEqual(nested, PlanningProgress.Fingerprint(available));
        state.Plan.Root.Tasks.Add(new() { Id = "root", Kind = "value", Outputs = [new("number", new() { Kind = "number", Number = 7 })] });
        var first = PlanningProgress.Fingerprint(state);
        Assert.NotEqual(nested, first);
        state.Plan.Root.Tasks.Add(new() { Id = "another", Kind = "value", Outputs = [new("number", new() { Kind = "number", Number = 7 })] });
        Assert.NotEqual(first, PlanningProgress.Fingerprint(state));
    }

    [Theory]
    [InlineData("conditional")]
    [InlineData("foreach")]
    [InlineData("parallel")]
    public void ProductiveControlRenamingDoesNotCreateNewBehavior(string kind)
    {
        var first = PlannerFixture.Session(); first.Plan = new();
        var body = ProducerPlan("read", "result").Root;
        first.Plan.Root.Tasks = [new() { Id = "control", Kind = kind, Body = kind == "parallel" ? null : body,
            Branches = kind == "parallel" ? [body] : [], Condition = new() { Kind = "boolean", Boolean = true },
            Items = new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }] } }];
        var renamed = PlannerFixture.Clone(first); renamed.Plan!.Root.Tasks[0].Id = "renamed_control";
        renamed.Plan.Root.Tasks[0].Objective = "Changed control label";
        Assert.Equal(PlanningProgress.Fingerprint(first), PlanningProgress.Fingerprint(renamed));
    }

    [Fact]
    public void ChangedProducerArgumentsAndReviewedCompletionAreDifferentProgress()
    {
        var state = PlannerFixture.Session(); state.Plan = ProducerPlan("first", "second");
        state.Construction!.Requirements.Add(new() { Id = "result" });
        state.RequestMeasurements = [new() { Admission = "admitted", RequestId = state.Request.SessionId + ":1:measurement" }];
        PlanningProgress.Initialize(state);
        state.ModelCalls = 1;
        state.Plan.Root.Tasks[0].Inputs[0].Value.Text = "different actual target";
        PlanningProgress.Record(state);
        var transition = Assert.Single(state.RequestMeasurements[0].Transitions!);
        Assert.Equal("executable_behavior", transition.Kind); Assert.True(transition.ProductiveChanges > 0);
        Assert.Equal(0, transition.CompletedRequirements);
        state.Construction.Requirements[0].Status = "closed";
        PlanningProgress.Record(state);
        Assert.Equal("business_completion", state.RequestMeasurements[0].Transitions![^1].Kind);
    }

    [Fact]
    public void UnselectedDiscoveryAcrossChangedQueriesDoesNotResetProductiveWork()
    {
        var state = PlannerFixture.Session(); PlanningProgress.Initialize(state);
        state.ModelCalls = 1;
        state.Discovery.Pages.Add(new("provider", null, [new("read", "provider", "read", "Read the source", "mcp.call", "read", "1")], null, Query: "first wording"));
        PlanningProgress.Record(state);
        Assert.Equal(1, state.Construction!.Compact!.Progress!.NonProductiveModelCalls);
        state.ModelCalls++;
        state.Discovery.Pages.Add(state.Discovery.Pages[0] with { Query = "rephrased wording" });
        PlanningProgress.Record(state);
        Assert.Equal(2, state.Construction.Compact.Progress.NonProductiveModelCalls);
    }

    [Fact]
    public void RewordedSelectionWithNewInternalIdsIsTheSameStrategy()
    {
        var state = PlannerFixture.Session();
        var first = JsonNode.Parse("""{"step":{"id":"one","scope":"/root","objective":"Produce the result","kind":"sequence","requirementIds":["r"],"expectedOutputs":["result"]}}""")!;
        var next = first.DeepClone(); next["step"]!["id"] = "two"; next["step"]!["objective"] = "Now create the result";
        next["step"]!["expectedOutputs"] = new JsonArray("renamed intention");
        Assert.Equal(PlanningProgress.Strategy(state, "selection", first), PlanningProgress.Strategy(state, "selection", next));
    }

    [Fact]
    public void DraftTopologyPreservesActualProducerSelectionAndDuplicateDeclarationsAcrossRenaming()
    {
        var state = PlannerFixture.Session();
        var first = JsonNode.Parse("""{"extension":{"tasks":[{"id":"one","kind":"value","objective":"first label","outputs":[{"name":"n","value":{"kind":"number","number":1}}]},{"id":"two","kind":"value","outputs":[{"name":"n","value":{"kind":"number","number":2}}]},{"id":"consumer","kind":"value","outputs":[{"name":"result","value":{"kind":"output","source":"one","port":"n"}}]}]}}""")!;
        var renamed = first.DeepClone();
        renamed["extension"]!["tasks"]![0]!["id"] = "renamed";
        renamed["extension"]!["tasks"]![0]!["objective"] = "rephrased label";
        renamed["extension"]!["tasks"]![2]!["outputs"]![0]!["value"]!["source"] = "renamed";
        var original = PlanningProgress.Strategy(state, PlanningConstruction.DraftPurpose, first);
        Assert.Equal(original, PlanningProgress.Strategy(state, PlanningConstruction.DraftPurpose, renamed));
        renamed["extension"]!["tasks"]![2]!["outputs"]![0]!["value"]!["source"] = "two";
        Assert.NotEqual(original, PlanningProgress.Strategy(state, PlanningConstruction.DraftPurpose, renamed));
        first["extension"]!["tasks"]![1]!["id"] = "one";
        Assert.NotEqual(original, PlanningProgress.Strategy(state, PlanningConstruction.DraftPurpose, first));
    }

    [Fact]
    public void HostFixedDraftBindingsAndObligationsStayInStrategyWhilePropertyOrderDoesNot()
    {
        var state = PlannerFixture.Session();
        state.Construction!.Compact!.CurrentStep = new() { Scope = "/root", RequirementIds = ["work"],
            Inputs = [new() { Kind = "string", Text = "selected source" }] };
        var first = JsonNode.Parse("""{"extension":{"outputs":[],"tasks":[]}}""")!;
        var reordered = JsonNode.Parse("""{"extension":{"tasks":[],"outputs":[]}}""")!;
        var original = PlanningProgress.Strategy(state, PlanningConstruction.DraftPurpose, first);
        Assert.Equal(original, PlanningProgress.Strategy(state, PlanningConstruction.DraftPurpose, reordered));
        state.Construction.Compact.CurrentStep.Inputs[0].Text = "different original binding";
        Assert.NotEqual(original, PlanningProgress.Strategy(state, PlanningConstruction.DraftPurpose, first));
        state.Construction.Compact.CurrentStep.Inputs[0].Text = "selected source";
        state.Construction.Compact.CurrentStep.RequirementIds = ["different obligation"];
        Assert.NotEqual(original, PlanningProgress.Strategy(state, PlanningConstruction.DraftPurpose, first));
    }

    [Fact]
    public void ExecutableTransformInstructionsRemainPartOfADraftStrategy()
    {
        var state = PlannerFixture.Session();
        var first = JsonNode.Parse("""{"extension":{"tasks":[{"id":"interpret","kind":"transform","objective":"Select every relevant observation.","resultType":{"kind":"object","fields":[]}}]}}""")!;
        var next = first.DeepClone(); next["extension"]!["tasks"]![0]!["objective"] = "Select only the first observation.";
        Assert.NotEqual(PlanningProgress.Strategy(state, PlanningConstruction.DraftPurpose, first),
            PlanningProgress.Strategy(state, PlanningConstruction.DraftPurpose, next));
    }

    [Theory]
    [InlineData("transform", true)]
    [InlineData("operation", false)]
    [InlineData("value", false)]
    public void ExecutableInstructionsGainProgressWhilePlanningLabelsDoNot(string kind, bool executable)
    {
        var state = PlannerFixture.Session();
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "action", Kind = kind,
            Objective = "Select every relevant observation.", ResultType = kind == "transform" ? new() { Kind = "string" } : null }] } };
        PlanningProgress.Initialize(state);
        var before = PlanningProgress.Fingerprint(state);
        state.Construction!.Compact!.Progress!.NonProductiveModelCalls = 5;
        state.Plan.Root.Tasks[0].Objective = "Select only the first observation.";
        state.ModelCalls++;
        PlanningProgress.Record(state);
        Assert.Equal(executable ? 0 : 6, state.Construction.Compact.Progress.NonProductiveModelCalls);
        Assert.Equal(!executable, before == PlanningProgress.Fingerprint(state));
        if (executable)
        {
            PlanningProgress.EnsureCallAvailable(state); // The mandatory Judge remains dispatchable.
            state = PlannerFixture.Clone(state);
            state.ModelCalls++;
            PlanningProgress.Record(state);
            Assert.Equal(1, state.Construction!.Compact!.Progress!.NonProductiveModelCalls);
        }
        else Assert.Throws<WorkflowRuntimeException>(() => PlanningProgress.EnsureCallAvailable(state));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("answer")]
    [InlineData("explicit")]
    public void NewUserOriginalBytesReopenOnlyTheirKnownNonproductiveWindow(string kind)
    {
        var state = PlannerFixture.Session();
        const string original = "Return the original result.";
        const string supplied = " Use the corrected original reference.";
        state.Request.Prompt = original;
        state.Construction!.Compact!.Sources = [new() { Id = "request:0", Kind = "page", Text = original, Version = PlanningGraphCompiler.Fingerprint(original) }];
        PlanningProgress.Initialize(state);
        state.Construction.Compact.Progress!.NonProductiveModelCalls = 6;
        var calls = state.ModelCalls;
        Assert.Throws<WorkflowRuntimeException>(() => PlanningProgress.EnsureCallAvailable(state));
        Supply();
        PlanningProgress.EnsureCallAvailable(state);
        Assert.Equal(0, state.Construction.Compact.Progress.NonProductiveModelCalls);
        Assert.Equal(calls, state.ModelCalls);
        state = PlannerFixture.Clone(state);
        state.Construction!.Compact!.Progress!.NonProductiveModelCalls = 6;
        Supply();
        Assert.Throws<WorkflowRuntimeException>(() => PlanningProgress.EnsureCallAvailable(state));
        Assert.Equal(calls, state.ModelCalls);

        void Supply()
        {
            if (kind == "revision")
            {
                state.Request.Prompt += supplied;
                state.Construction!.Compact!.Sources.Add(new() { Id = "revision:" + state.Construction.Compact.Sources.Count, Kind = "revision_page",
                    Text = supplied, Version = PlanningGraphCompiler.Fingerprint(supplied) });
            }
            else if (kind == "answer")
                (state.AnswerHistory ??= []).Add(new(0, [new("missing", "Which original reference?", [], null)], [new("missing", Text: supplied)]));
            else PlanningProgress.RecordOriginal(state, supplied);
        }
    }

    [Fact]
    public void RepeatedRetrievalSurvivesClearedPresentationAndRestart()
    {
        var state = PlannerFixture.Session(); PlanningProgress.Initialize(state);
        state.Construction!.Evidence = new() { ["schema:input"] = new JsonObject { ["type"] = "string" } };
        state.ModelCalls = 1; PlanningProgress.Record(state);
        state.Construction.Evidence = null;
        state = PlannerFixture.Clone(state);
        state.Construction!.Evidence = new() { ["schema:input"] = new JsonObject { ["type"] = "string" } };
        state.ModelCalls++; PlanningProgress.Record(state);
        Assert.Equal(1, state.Construction.Compact!.Progress!.NonProductiveModelCalls);
    }

    [Fact]
    public void FreshLimitIsCapturedWithoutAddingAFieldToHistoricalOptions()
    {
        var state = PlannerFixture.Session();
        Assert.Equal(6, state.Request.Options["construction_limits"]!["maxNonProductiveModelCalls"]!.GetValue<int>());
        var old = JsonSerializer.Deserialize("{}", PlanningJsonContext.Default.PlanningConstructionOptions)!;
        Assert.Null(old.MaxNonProductiveModelCalls);
        Assert.DoesNotContain("maxNonProductiveModelCalls", JsonSerializer.Serialize(old, PlanningJsonContext.Default.PlanningConstructionOptions));
    }

    private static TaskPlan ProducerPlan(string producer, string consumer) => new()
    {
        Root = new() { Tasks =
        [
            new() { Id = producer, Kind = "operation", Operation = "read", Inputs = [new("target", new() { Kind = "string", Text = "actual target" })] },
            new() { Id = consumer, Kind = "value", DependsOn = [producer], Outputs = [new("result", new() { Kind = "output", Source = producer, Port = "text" })] }
        ] }
    };
}
