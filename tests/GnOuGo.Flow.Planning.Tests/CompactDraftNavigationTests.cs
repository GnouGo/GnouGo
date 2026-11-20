using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactDraftNavigationTests
{
    [Theory]
    [InlineData("sequence", true)]
    [InlineData("close", false)]
    public void PreparedDraftExplainsIndependentExtractionWhenTheGrammarOffersIt(string kind, bool offered)
    {
        var state = State();
        state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        state.Construction!.Compact!.CurrentStep!.Kind = kind;
        var draft = PlanningConstruction.DraftRequest(state, repair: false);
        var request = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose,
            draft.Prompt, draft.StructuredOutputSchema!.AsObject(), null, null);
        Assert.Equal(offered, request.StructuredOutputSchema!.ToJsonString().Contains("\"each\":", StringComparison.Ordinal));
        var semantics = request.StructuredOutputSchema["description"]!.ToString();
        Assert.Equal(offered, semantics.Contains("each.input", StringComparison.Ordinal));
        Assert.Equal(offered, semantics.Contains("each.output", StringComparison.Ordinal));
        Assert.Equal(0, state.ModelCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DraftSeparatesTheSelectedPrerequisiteFromTheBusinessBlocksFinalResults(bool inherited)
    {
        var state = State();
        var block = PlanningCompactConstruction.CurrentBlock(state)!;
        var step = state.Construction!.Compact!.CurrentStep!;
        const string need = "Resolve the caller's record and retain its revision for a later comparison.";
        step.Prerequisites = [new() { Need = need, EvidenceAddresses = ["requirement:req"] }];
        step.Objective = inherited ? block.Objective : "Resolve the input record.";
        step.ExpectedOutputs = inherited ? block.ExpectedResults.ToList() : ["The resolved revision."];
        var context = Context(PlanningConstruction.DraftRequest(state, repair: false));
        var selected = context["step"]!;
        Assert.Equal(need, Assert.Single(selected["prerequisites"]!.AsArray())!["need"]!.ToString());
        Assert.Equal(block.Objective, context["block"]!["objective"]!.ToString());
        Assert.Equal(block.ExpectedResults, context["block"]!["expectedResults"]!.AsArray().Select(v => v!.ToString()));
        if (inherited)
        {
            Assert.Null(selected["objective"]);
            Assert.Null(selected["expectedOutputs"]);
            Assert.Equal(block.Objective, step.Objective);
            Assert.Equal(block.ExpectedResults, step.ExpectedOutputs);
        }
        else
        {
            Assert.Equal(step.Objective, selected["objective"]!.ToString());
            Assert.Equal(step.ExpectedOutputs, selected["expectedOutputs"]!.AsArray().Select(v => v!.ToString()));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscoveryCanCiteDisplayedOriginalClausesWithoutRequestingThemAgain(bool differentRequirementText)
    {
        var state = State();
        var compact = state.Construction!.Compact!;
        var requirement = compact.Sources[1];
        const string constraint = "Release the resource even on failure.";
        const string globalId = "request:0:clause:1";
        compact.Sources.Add(new() { Id = globalId, Kind = "global", Indexed = true, Text = constraint,
            Version = PlanningGraphCompiler.Fingerprint(constraint) });
        compact.GlobalSourceIds.Add(globalId);
        if (differentRequirementText)
            state.Requirements!.Outcomes[0] = state.Requirements.Outcomes[0] with { Description = "A shorter description." };
        var request = PlanningCompactConstruction.Request(state, PlanningCompactConstruction.DiscoveryGuidance(state),
            PlanningCompactConstruction.DiscoveryContext(state), PlanningCompactConstruction.DiscoverySchema(state),
            PlanningCompactConstruction.DiscoveryPurpose);
        var context = Context(request);
        var shown = context["requirements"]![0]!;
        var link = shown["sources"]![0]!;
        var choices = request.StructuredOutputSchema!["properties"]!["evidenceRequests"]!.ToJsonString();
        Assert.Equal("source:" + requirement.Id, link["evidence"]!.ToString());
        Assert.Equal(requirement.Version, link["version"]!.ToString());
        Assert.Equal(!differentRequirementText, shown["original"]!.ToString() == requirement.Text);
        Assert.Equal(differentRequirementText, choices.Contains('"' + link["evidence"]!.ToString() + '"', StringComparison.Ordinal));
        var global = Assert.Single(context["globalConstraints"]!.AsArray())!;
        Assert.Equal(constraint, global["text"]!.ToString());
        Assert.Equal("source:" + globalId, global["evidence"]!.ToString());
        Assert.DoesNotContain('"' + global["evidence"]!.ToString() + '"', choices, StringComparison.Ordinal);
        Assert.True(PlanningCompactConstruction.LoadEvidence(state, "source:" + globalId, out var original));
        Assert.Equal(constraint, original!["text"]!.ToString());
    }

    [Fact]
    public void EmptyRootEvidenceIsAlreadyPresentDuringSelectionAndDraftWithoutChangingTheSession()
    {
        var state = State(); var original = PlanningConstruction.Hash(state.Plan);
        state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        var retained = state.Construction!.Evidence?.ToJsonString();
        var selected = state.Construction.Compact!.CurrentStep;
        state.Construction.Compact.CurrentStep = null;
        var selection = PlanningCompactConstruction.ImplementationContext(state);
        state.Construction.Compact.CurrentStep = selected;
        var draft = Context(PlanningConstruction.DraftRequest(state, repair: false));
        foreach (var context in new[] { selection, draft })
        {
            var root = context["scopeContext"]!;
            Assert.Equal("/root", root["path"]!.ToString());
            Assert.True(root["open"]!.GetValue<bool>());
            Assert.Equal(0, root["taskCount"]!.GetValue<int>());
            Assert.Equal(0, root["alwaysCount"]!.GetValue<int>());
            Assert.Empty(root["tasks"]!.AsArray());
            Assert.Empty(root["always"]!.AsArray());
        }
        Assert.Equal(original, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(retained, state.Construction.Evidence?.ToJsonString());
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(0, state.ModelCalls);
    }

    [Fact]
    public void LoadedChildScopeSuppliesItsExactNormalAndCleanupLeavesDuringDrafting()
    {
        var state = State();
        const string path = "/tasks/inspect/body";
        var body = new TaskScope();
        body.Tasks.Add(new() { Id = "normalize", Kind = "value", Objective = "Normalize the record once." });
        body.Tasks.Add(new() { Id = "nested", Kind = "sequence", Body = new() });
        body.Always.Add(new() { Id = "release", Kind = "value", Objective = "Release the observed resource." });
        state.Plan!.Root.Tasks.Add(new() { Id = "inspect", Kind = "sequence", Body = body });
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Scope = path;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        var first = PlanningConstruction.DraftRequest(state, repair: false);
        AssertResponse(first, state, "scope:" + path, valid: true);
        foreach (var id in new[] { "normalize", "release", "nested" })
            AssertResponse(first, state, "task:" + id, valid: true);
        PlanningConstruction.LoadEvidence(state, ["scope:" + path],
            followup: current => PlanningConstruction.DraftRequest(current, repair: false), purpose: PlanningConstruction.DraftPurpose);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        var scope = Context(request)["evidence"]!["scope:" + path]!;
        Assert.Equal(body.Tasks[0].Objective, scope["tasks"]![0]!["objective"]!.ToString());
        Assert.Equal(body.Always[0].Objective, scope["always"]![0]!["objective"]!.ToString());
        foreach (var id in new[] { "normalize", "release" })
            AssertResponse(request, state, "task:" + id, valid: false);
        AssertResponse(request, state, "task:nested", valid: false);
        AssertResponse(request, state, "scope:/tasks/nested/body", valid: true);
        Assert.Equal(step.BaseHash, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(0, state.ModelCalls);

        body.Tasks[0].Objective = "Normalize the updated record.";
        state.Construction.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        AssertResponse(PlanningConstruction.DraftRequest(state, repair: false), state, "task:normalize", valid: true);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(5, true)]
    public void DisplayedContainerKeepsItsTaskLookupWhenAnyChildScopeLinkIsMissing(int branches, bool needsLookup)
    {
        var state = State();
        var task = new PlanTask { Id = "compare", Kind = "parallel", Objective = string.Concat(Enumerable.Repeat("Compare independent sources. ", 10)) };
        for (var i = 0; i < branches; i++) task.Branches.Add(new());
        state.Plan!.Root.Tasks.Add(task);
        state.Construction!.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        PlanningConstruction.LoadEvidence(state, ["scope:/root"],
            followup: current => PlanningConstruction.DraftRequest(current, repair: false), purpose: PlanningConstruction.DraftPurpose);
        var next = PlanningConstruction.DraftRequest(state, repair: false);
        AssertResponse(next, state, "task:compare", valid: needsLookup);
        var objective = "task-objective:compare@" + PlanningGraphCompiler.Fingerprint(task.Objective) + ":0";
        Assert.Equal(task.Objective, Context(next)["evidence"]!["scope:/root"]!["tasks"]![0]!["objective"]!.ToString());
        AssertResponse(next, state, objective, valid: false);
        for (var i = 0; i < branches; i++)
            AssertResponse(next, state, "scope:/tasks/compare/branches/" + i, valid: i < 4);
        Assert.Equal(0, state.ModelCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectionNeverOffersUnselectedContractLinksThatTheEvidenceReaderRejects(bool diagnostic)
    {
        var state = State();
        if (diagnostic) state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        state.Catalog!.AllowedStepTypes = ["mcp.call"];
        state.Catalog!.Capabilities.Add(new() { Id = "read", Version = "v1", Kind = "tool", StepType = "mcp.call",
            Description = "Read an observed value.", InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } });
        var step = state.Construction!.Compact!.CurrentStep!;
        state.Construction.Compact.CurrentStep = null;
        var baseline = PlanningConstruction.Hash(state.Plan);
        var context = new JsonObject { ["knownOperations"] = new JsonArray(new JsonObject {
            ["id"] = "read", ["version"] = "v1", ["descriptionEvidence"] = "description:read@v1:0",
            ["inputDirectory"] = "schema-page:read@v1:/input:0", ["outputDirectory"] = "schema-page:read@v1:/output:0" }) };
        var schema = PlanningCompactConstruction.ImplementationSchema(state);
        PlanningCompactConstruction.ConstrainSelectionEvidence(state, context, schema);
        foreach (var address in new[] { "description:read@v1:0", "schema-page:read@v1:/input:0", "schema-page:read@v1:/output:0" })
        {
            Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.LoadContractEvidence(state, address));
            Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(SelectionEvidence(state, address), schema));
        }
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Null(state.Construction.Compact.CurrentStep);
        step.Kind = "operation"; step.OperationIds = ["read"];
        state.Construction.Compact.CurrentStep = step;
        Assert.NotNull(PlanningCompactConstruction.LoadContractEvidence(state, "description:read@v1:0"));
        Assert.NotNull(PlanningCompactConstruction.LoadContractEvidence(state, "schema-page:read@v1:/input:0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContainerDraftRetainsReusableNavigationAndRejectsGuessedEvidence(bool repair)
    {
        var state = State();
        state.Phase = repair ? PlanningPhase.Replanning : PlanningConstruction.DraftPurpose;
        var request = PlanningConstruction.DraftRequest(state, repair);
        var context = Context(request);
        Assert.Equal("scope:/root", context["scopeEvidence"]!.ToString());
        Assert.Equal("group:worker", Assert.Single(context["reusableInterfaces"]!.AsArray())!["evidence"]!.ToString());
        Assert.Null(context["taskEvidence"]); // No accepted root siblings yet.
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!.AsObject(), strict: true));
        AssertResponse(request, state, "group:worker", valid: true);
        AssertResponse(request, state, "scope:/root", valid: true);
        AssertResponse(request, state, "scopeInterface:/root", valid: false);
        AssertResponse(request, state, "Provide the accepted reusable interface.", valid: false);
        var hash = PlanningConstruction.Hash(state.Plan);
        PlanningConstruction.LoadEvidence(state, ["group:worker"],
            followup: current => PlanningConstruction.DraftRequest(current, repair), purpose: repair ? "replan" : PlanningConstruction.DraftPurpose);
        var followup = PlanningConstruction.DraftRequest(state, repair);
        Assert.NotNull(Context(followup)["evidence"]!["group:worker"]);
        AssertResponse(followup, state, "group:worker", valid: false);
        Assert.Equal(hash, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("foreach", null, null, true)]
    [InlineData("foreach", "worker", null, true)]
    [InlineData("call", "worker", null, true)]
    [InlineData("call", null, null, false)]
    [InlineData("call", "worker", "requirement:req", true)]
    [InlineData("call", "worker", "source:request:0:clause:0", true)]
    [InlineData("call", "worker", "request:0:clause:0", false)]
    [InlineData("call", "worker", "group:worker", false)]
    [InlineData("call", "worker", "task:accepted", false)]
    public void SelectionKeepsReusableTargetsAndPrerequisiteAuthorityDistinct(string kind, string? group, string? prerequisite, bool valid)
    {
        var state = State();
        var schema = PlanningCompactConstruction.ImplementationSchema(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var response = new JsonObject
        {
            ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["evidenceRequests"] = null, ["clarifications"] = null,
            ["step"] = new JsonObject { ["scope"] = "/root", ["kind"] = kind, ["inputs"] = new JsonArray(), ["groupId"] = group,
                ["dependencyTaskIds"] = new JsonArray(), ["closeRequirementIds"] = new JsonArray(),
                ["operationIds"] = new JsonArray(), ["closeScopes"] = null,
                ["prerequisites"] = prerequisite is null ? null : new JsonArray(new JsonObject
                    { ["need"] = "The required calculation must be available.", ["evidenceAddresses"] = new JsonArray(prerequisite) }) }
        };
        Assert.Equal(valid, PlanningContractValidation.ValidateInstanceFindings(response, schema).Count == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectionEvidenceUsesDisplayedOriginalsAndExactNavigationAcrossPages(bool reselection)
    {
        var state = State();
        var compact = state.Construction!.Compact!;

        if (reselection)
        {
            state.Diagnostics = [new("CONSTRUCTION_SELECTION_INVALID", "/step", "The tentative implementation needs another producer.")];
            PlanningCompactConstruction.Reselect(state, "Select the missing immediate producer within the original scope.");
        }
        else compact.CurrentStep = null;
        for (var i = 1; i < 3; i++)
        {
            // Exercise the actual byte bound rather than assuming an indexing
            // page boundary also limits business requirement presentation.
            var text = "Preserve original obligation " + i + ". " + new string('x', 930);
            var source = "request:" + i;
            compact.Sources.Add(new() { Id = source, Version = PlanningGraphCompiler.Fingerprint(text), Text = text, Kind = "page", Indexed = true });
            compact.Sources.Add(new() { Id = source + ":clause:0", Version = PlanningGraphCompiler.Fingerprint(text), Text = text,
                Kind = "requirement", Inputs = [], Outputs = [], Indexed = true });
            compact.RequirementSources["req_" + i] = [source + ":clause:0"];
            state.Requirements!.Outcomes.Add(new("req_" + i, text) { Commitments = [] });
            state.Construction.Requirements.Add(new() { Id = "req_" + i });
        }
        state.Requirements!.Inputs = [new() { Name = "caller", Type = new() { Kind = "string" },
            Default = new() { Kind = "string", Text = "task:forged" } }];
        state.Plan!.Inputs = state.Requirements.Inputs;
        var baseline = PlanningConstruction.Hash(state.Plan);
        var request = SelectionRequest(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!.AsObject(), strict: true));
        foreach (var address in new[] { "requirement:req", "source:request:0:clause:0", "group:worker", "input:caller", "page:requirements:1" })
            AssertSelectionEvidence(request, state, address, valid: true);
        Assert.Equal("/root", Context(request)["scopeContext"]!["path"]!.ToString());
        AssertSelectionEvidence(request, state, "scope:/root", valid: false);
        Assert.Single(Context(request)["requirements"]!.AsArray());
        AssertSelectionEvidence(request, state, "source:request:1:clause:0", valid: false);
        foreach (var address in new[] { "Find an authorized producer for this action.", "task:unknown", "request:0:clause:0", "page:requirements:2" })
            AssertSelectionEvidence(request, state, address, valid: false);
        AssertSelectionEvidence(request, state, "rejected:0", valid: reselection);
        AssertSelectionEvidence(request, state, "rejected:1", valid: false);
        var addresses = new List<string> { "page:requirements:1", "input:caller" };
        if (reselection) addresses.Add("rejected:0");
        PlanningConstruction.LoadEvidence(state, addresses, followup: SelectionRequest, purpose: PlanningCompactConstruction.ImplementationPurpose);
        request = SelectionRequest(state);
        Assert.NotNull(Context(request)["evidence"]!["page:requirements:1"]);
        AssertSelectionEvidence(request, state, "source:request:1:clause:0", valid: true);
        AssertSelectionEvidence(request, state, "requirement:req_1", valid: true);
        AssertSelectionEvidence(request, state, "page:requirements:2", valid: true);
        foreach (var address in addresses.Append("task:forged")) AssertSelectionEvidence(request, state, address, valid: false);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Null(state.Construction.Compact!.CurrentStep);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(reselection ? new[] { "req" } : null, state.Construction.Compact.ReplacementRequirements?.ToArray());
        var prepared = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.ImplementationPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject());
        var measured = PlanningRequestMeasurements.Measure(prepared, PlanningCompactConstruction.ImplementationPurpose, 6000);
        PlannerFixture.ObserveInputBudget(measured, 6000);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
    }

    [Fact]
    public void NavigationStaysPagedAsIndependentHistoryGrows()
    {
        var state = State();
        for (var i = 1; i < 4; i++) state.Plan!.Groups.Add(Group("worker_" + i));
        var before = PlanningConstruction.DraftRequest(state, repair: false);
        for (var i = 4; i < 104; i++)
        {
            state.Plan!.Groups.Add(Group("worker_" + i));
            state.Construction!.Compact!.RejectedSteps.Add(new JsonObject { ["objective"] = "Unrelated retained failure " + i });
        }
        state.Construction!.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        var context = Context(request);
        Assert.Equal(4, context["reusableInterfaces"]!.AsArray().Count);
        Assert.Equal("page:interfaces:4", context["interfacesDirectory"]!.ToString());
        Assert.DoesNotContain("worker_103", request.Prompt, StringComparison.Ordinal);
        var prepared = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose, request.Prompt, request.StructuredOutputSchema!.AsObject());
        var measured = PlanningRequestMeasurements.Measure(prepared, PlanningConstruction.DraftPurpose, 16000);
        PlannerFixture.ObserveInputBudget(measured, 16000);
        Assert.Equal(measured.CompleteRequestBytes, measured.Components.Sum(component => component.SerializedBytes));
        Assert.InRange(request.Prompt.Length - before.Prompt.Length, 1, 150);
        Assert.InRange(request.StructuredOutputSchema!.ToJsonString().Length - before.StructuredOutputSchema!.ToJsonString().Length, 1, 150);
        AssertResponse(request, state, "page:interfaces:4", valid: true);
        PlanningConstruction.LoadEvidence(state, ["page:interfaces:4"],
            followup: current => PlanningConstruction.DraftRequest(current, repair: false), purpose: PlanningConstruction.DraftPurpose);
        var next = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Null(Context(next)["reusableInterfaces"]); // Supplied page is retained once.
        AssertResponse(next, state, "group:worker_4", valid: true);
        AssertResponse(next, state, "page:interfaces:8", valid: true);
    }

    [Fact]
    public void ContinuedScopeCanReadItsUnrepresentedMiddleTaskPageInTheFirstBatch()
    {
        var state = State();
        state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        for (var i = 0; i < 20; i++)
            state.Plan!.Root.Tasks.Add(new() { Id = "work_" + i, Kind = "value", Objective = new string('x', 1200) });
        var step = state.Construction!.Compact!.CurrentStep!;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Checkpoints.Add(new() { Step = step, PlanHash = step.BaseHash, Sequence = 1,
            TaskIds = ["work_19"], Fragment = new() { ["continueInScope"] = step.Scope } });
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        AssertResponse(request, state, "page:tasks:8", valid: true);
        var middle = Assert.Single(Context(request)["taskEvidence"]!["directoryPages"]!.AsArray())!;
        Assert.Equal("page:tasks:8", middle["address"]!.ToString());
        Assert.Equal("work_8", middle["firstTask"]!.ToString());
        Assert.Equal("work_15", middle["lastTask"]!.ToString());
        var wire = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose, request.Prompt,
            request.StructuredOutputSchema!.AsObject(), null, null, out var map);
        var alias = Context(wire)["taskEvidence"]!["directoryPages"]![0]!["address"]!.ToString();
        Assert.Equal("page:tasks:8", map!.Addresses[alias]);
        state = PlannerFixture.Clone(state);
        PlanningConstruction.LoadEvidence(state, ["page:tasks:0", "page:tasks:8", "page:tasks:16"]);
        var next = Context(PlanningConstruction.DraftRequest(state, repair: false));
        Assert.Null(next["taskEvidence"]!["directoryPages"]);
        Assert.Equal("work_8", next["evidence"]!["page:tasks:8"]!["items"]![0]!["id"]!.ToString());
        Assert.Equal(0, state.ModelCalls);
    }

    [Fact]
    public void DraftCanReadTheDirectoryPagesOfRecentAcceptedTasksInOneBatch()
    {
        var state = State();
        for (var i = 0; i < 20; i++)
            state.Plan!.Root.Tasks.Add(new() { Id = "value_" + i, Kind = "value", Objective = "Retain observation " + i });
        state.Construction!.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Compact.CurrentStep.DependencyTaskIds = ["value_9"];
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        var directory = Context(request)["taskEvidence"]!;
        Assert.Equal("page:tasks:0", directory["remainingEvidence"]!.ToString());
        foreach (var id in new[] { 9, 19 })
        {
            var row = Assert.Single(directory["items"]!.AsArray(), item => item!["task"]!.ToString() == "value_" + id)!;
            Assert.Equal("page:tasks:" + id / 8 * 8, row["directory"]?.ToString());
        }
        var pages = new[] { "page:tasks:8", "page:tasks:16" };
        var schema = request.StructuredOutputSchema!.AsObject();
        var response = Response(state, pages[0]);
        response["evidenceRequests"] = new JsonArray(pages[0], pages[1]);
        Assert.Empty(PlanningContractValidation.ValidateInstance(PlanningCorpus.Transport(response, schema, schema)!, schema));
        AssertResponse(request, state, "page:tasks:24", valid: false);
        PlanningConstruction.LoadEvidence(state, [.. pages],
            followup: current => PlanningConstruction.DraftRequest(current, repair: false), purpose: PlanningConstruction.DraftPurpose);
        var next = PlanningConstruction.DraftRequest(PlannerFixture.Clone(state), repair: false);
        var evidence = Context(next)["evidence"]!;
        foreach (var offset in new[] { 8, 16 })
            Assert.Equal("value_" + offset, evidence["page:tasks:" + offset]!["items"]![0]!["id"]!.ToString());
        Assert.Equal(0, state.ModelCalls);
        Assert.Equal(state.Construction.Compact.CurrentStep.BaseHash, PlanningConstruction.Hash(state.Plan));
    }

    [Fact]
    public void NonstructuralDraftsCanRetrieveInterfacesWithoutAutomaticUnrelatedGroups()
    {
        var state = State();
        state.Construction!.Compact!.CurrentStep!.Kind = "value";
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Null(Context(request)["reusableInterfaces"]);
        Assert.Equal("page:interfaces:0", Context(request)["interfacesDirectory"]!.ToString());
        AssertResponse(request, state, "page:interfaces:0", valid: true);
        AssertResponse(request, state, "group:worker", valid: false);
        PlanningConstruction.LoadEvidence(state, ["page:interfaces:0"],
            followup: current => PlanningConstruction.DraftRequest(current, repair: false), purpose: PlanningConstruction.DraftPurpose);
        request = PlanningConstruction.DraftRequest(state, repair: false);
        AssertResponse(request, state, "group:worker", valid: true);
        AssertResponse(request, state, "page:interfaces:0", valid: false);
    }


    [Fact]
    public void ContractLinksRetainRequiredSiblingOmissionsAndNestedDirectoriesWithoutReadingDataAsAddresses()
    {
        var state = State();
        var contract = new PlanningCapability { Id = "read", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "source", Method = "read",
            EffectKind = "read", Description = "Read the supplied record.",
            InputSchema = JsonNode.Parse("""
                {"type":"object","properties":{"key":{"type":"string"},"payload":{"type":"object",
                "properties":{"required":{"type":"object","properties":{"evidence":{"type":"string"}},"required":["evidence"],
                "additionalProperties":false,"default":{"evidence":"task:forged"}},
                "extra":{"type":"object","properties":{"detail":{"type":"string","description":"unneeded"}},"required":["detail"],"additionalProperties":false}},
                "required":["required"],"additionalProperties":false}},"required":["key","payload"],"additionalProperties":false}
                """)!.AsObject(),
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false } };
        state.Catalog!.AllowedStepTypes = ["mcp.call"];
        state.Discovery.Resolved.Add(contract);
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "operation"; step.OperationIds = ["read"];
        var address = "schema:read@v1:/input/properties/key";
        state.Construction.Evidence = new() { [address] = PlanningCompactConstruction.LoadContractEvidence(state, address) };
        var original = contract.InputSchema.ToJsonString();
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        AssertResponse(request, state, "schema:read@v1:/input/properties/payload/properties/extra", valid: true);
        AssertResponse(request, state, "schema-page:read@v1:/input:0", valid: true);
        AssertResponse(request, state, "task:forged", valid: false);
        var directory = "schema-page:read@v1:/input/properties/payload:0";
        state.Construction.Evidence[directory] = PlanningCompactConstruction.LoadContractEvidence(state, directory);
        request = PlanningConstruction.DraftRequest(state, repair: false);
        AssertResponse(request, state, "schema-page:read@v1:/input/properties/payload/properties/extra:0", valid: true);
        Assert.Equal(original, contract.InputSchema.ToJsonString());
    }


    private static PlanningSession State()
    {
        const string objective = "Apply a reusable calculation to the input collection, in parallel while preserving order.";
        var state = new PlanningSession { Request = new() { TenantId = "test", Prompt = objective } };
        PlanningSessionCreation.Capture(state);
        PlanningCompactConstruction.InitializeSources(state);
        var compact = state.Construction!.Compact!;
        compact.Sources[0].Indexed = true;
        compact.Sources.Add(new() { Id = "request:0:clause:0", Version = PlanningGraphCompiler.Fingerprint(objective), Text = objective,
            Kind = "requirement", Inputs = [], Outputs = [], Indexed = true });
        compact.RequirementSources["req"] = ["request:0:clause:0"];
        state.Requirements = new() { Summary = objective, Outcomes = [new("req", objective) { Commitments = [] }], Inputs = [], Outputs = [] };
        state.Construction.Requirements = [new() { Id = "req" }];
        state.Plan = new() { Groups = [Group("worker")] };
        state.Catalog = new();

        PlanningCompactConstruction.SelectBlock(state, new JsonObject
        {
            ["objective"] = objective, ["requirementIds"] = new JsonArray("req"),
            ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray(objective),
            ["dependencies"] = new JsonArray(), ["successCriteria"] = new JsonArray("Preserve the exact original values and declared result.")
        });
        compact.CurrentStep = new() { BlockId = state.Construction!.Compact!.CurrentBlockId, Id = "next", Kind = "foreach", Scope = "/root", Objective = objective, RequirementIds = ["req"],
            BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        return state;
    }

    private static TaskGroup Group(string id) => new() { Id = id, Inputs = [new() { Name = "value", Type = new() { Kind = "number" } }],
        Body = new() { Outputs = [new("result", new() { Kind = "input", Source = "value" })] } };
    private static LLMRequest SelectionRequest(PlanningSession state) => PlanningCompactConstruction.Request(
        PlanningCompactConstruction.ImplementationGuidance(state), PlanningCompactConstruction.ImplementationContext(state),
        PlanningCompactConstruction.ImplementationSchema(state), PlanningCompactConstruction.ImplementationPurpose);
    private static JsonObject SelectionEvidence(PlanningSession state, string address) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["step"] = null,
        ["evidenceRequests"] = new JsonArray(address), ["clarifications"] = null
    };
    private static void AssertSelectionEvidence(LLMRequest request, PlanningSession state, string address, bool valid)
    {
        var findings = PlanningContractValidation.ValidateInstanceFindings(SelectionEvidence(state, address), request.StructuredOutputSchema!);
        Assert.True(valid == (findings.Count == 0), address + ": " + string.Join("; ", findings));
    }
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static JsonObject Response(PlanningSession state, string address) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["requirements"] = null, ["commitments"] = null, ["extension"] = null,
        ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = new JsonArray(address),
        ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
    };
    private static void AssertResponse(LLMRequest request, PlanningSession state, string address, bool valid)
    {
        var schema = request.StructuredOutputSchema!.AsObject();
        var response = PlanningCorpus.Transport(Response(state, address), schema, schema)!;
        Assert.Equal(valid, PlanningContractValidation.ValidateInstanceFindings(response, schema).Count == 0);
    }
}
