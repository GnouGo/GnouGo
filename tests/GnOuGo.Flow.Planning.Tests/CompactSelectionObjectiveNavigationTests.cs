using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactSelectionObjectiveNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturningToFirstInterfacePagePreservesUnreadMixedPages(bool secondPageAlreadyRead)
    {
        var state = await PagingState();
        var plan = state.Plan!;
        for (var index = plan.Root.Tasks.Count; index < 13; index++)
            plan.Root.Tasks.Add(new() { Id = "accepted_" + index, Kind = "value", Objective = "Keep this accepted value.",
                Outputs = [new("field", new() { Kind = "string", Text = "accepted" })] });
        for (var index = 1; index < 17; index++)
            plan.Inputs.Add(new() { Name = "caller_" + index, Type = new() { Kind = "string" } });
        plan.Choices = Enumerable.Range(0, 17).Select(index => new PlanningChoice
            { Id = "choice_" + index, Question = "Choose this value.", Type = new() { Kind = "string" } }).ToList();
        plan.Groups = Enumerable.Range(0, 17).Select(index => new TaskGroup { Id = "group_" + index }).ToList();
        var accepted = PlanningConstruction.Hash(plan);
        Read("page:interfaces:4");
        if (secondPageAlreadyRead) Read("page:interfaces:8");
        Read("page:interfaces:0");
        Assert.Equal(0, state.Construction!.Compact!.InterfaceOffset);
        var remaining = secondPageAlreadyRead ? new[] { 12, 16 } : new[] { 8, 12, 16 };
        foreach (var offset in remaining)
        {
            var next = "page:interfaces:" + offset;
            Assert.Equal(next, PlanningCompactConstruction.ImplementationContext(state)["nextInterfaces"]!.ToString());
            Read(next);
        }
        Assert.Null(PlanningCompactConstruction.ImplementationContext(state)["nextInterfaces"]);
        var pages = state.Construction.Evidence!.Where(item => item.Key.StartsWith("page:interfaces:", StringComparison.Ordinal))
            .SelectMany(item => item.Value!.AsArray()).ToArray();
        Assert.All(plan.Root.Tasks, task => Assert.Contains(pages, item => item?["task"]?.ToString() == task.Id));
        Assert.All(plan.Inputs, input => Assert.Contains(pages, item => item?["input"]?.ToString() == input.Name));
        Assert.All(plan.Choices, choice => Assert.Contains(pages, item => item?["choice"]?.ToString() == choice.Id));
        Assert.All(plan.Groups, group => Assert.Contains(pages, item => item?["group"]?.ToString() == group.Id));
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);

        void Read(string address)
        {
            var request = Request(state, PlanningCompactConstruction.ImplementationContext(state), PlanningCompactConstruction.ImplementationSchema(state));
            Assert.Empty(PlanningContractValidation.ValidateInstance(EvidenceAction(state, address), request.StructuredOutputSchema!.AsObject()));
            PlanningConstruction.LoadEvidence(state, [address]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterSelectionPageRetainsReturnNavigationToExactCallerInput(bool firstPageAlreadyRead)
    {
        var state = await PagingState();

        var accepted = PlanningConstruction.Hash(state.Plan);
        var calls = state.ModelCalls;
        var declaration = JsonSerializer.SerializeToNode(Assert.Single(state.Plan!.Inputs), PlanningJsonContext.Default.TaskInput);
        var grammarRequest = Request(state, PlanningCompactConstruction.ImplementationContext(state), PlanningCompactConstruction.ImplementationSchema(state));
        grammarRequest = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.ImplementationPurpose, grammarRequest.Prompt,
            grammarRequest.StructuredOutputSchema!.AsObject(), null, null, out var grammarAddresses);
        var grammar = EvidenceFreeSchema(PlanningModelCalls.CanonicalEvidenceRequest(new()
        { Id = grammarRequest.ClientRequestId!, Purpose = PlanningCompactConstruction.ImplementationPurpose, Request = grammarRequest, EvidenceAddresses = grammarAddresses }));
        Assert.Null(PlanningCompactConstruction.ImplementationContext(state)["interfacesDirectory"]);
        if (firstPageAlreadyRead) PlanningConstruction.LoadEvidence(state, ["page:interfaces:0"]);
        var addresses = new Queue<string>(firstPageAlreadyRead
            ? ["page:interfaces:4", "input:caller_value"]
            : ["page:interfaces:4", "page:interfaces:0", "input:caller_value"]);
        var expectedCalls = addresses.Count;
        var runtime = new TestRuntime();
        runtime.Respond = (request, _) =>
        {
            var call = runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall?.Id == request.ClientRequestId).PendingCall!;
            var host = PlanningModelCalls.CanonicalEvidenceRequest(call);
            var context = JsonNode.Parse(host.Prompt[(host.Prompt.LastIndexOf('\n') + 1)..])!;
            var address = addresses.Dequeue();
            if (address == "page:interfaces:0")
            {
                var expected = PlanningCompactConstruction.InterfaceDirectory(state, null, 4);
                var displayed = context["interfaces"]!.DeepClone().AsArray();
                for (var index = 0; index < displayed.Count; index++)
                    if (displayed[index]?["objectiveOmitted"]?.GetValue<bool>() == true)
                    {
                        Assert.NotNull(displayed[index]!["evidence"]);
                        Assert.NotNull(expected[index]!["objective"]);
                        expected[index]!.AsObject().Remove("objective");
                        expected[index]!.AsObject().Remove("objectiveTextComplete");
                        displayed[index]!.AsObject().Remove("objectiveOmitted");
                    }
                Assert.True(JsonNode.DeepEquals(expected, displayed));
                Assert.Null(context["nextInterfaces"]);
                Assert.Equal(address, context["interfacesDirectory"]!.ToString());
                Assert.Null(context["evidence"]?[address]);
            }
            if (address == "input:caller_value")
            {
                Assert.Null(context["interfacesDirectory"]);
                Assert.Contains(context["evidence"]!["page:interfaces:0"]!.AsArray(), item => item?["input"]?.ToString() == "caller_value");
                Assert.NotEmpty(PlanningContractValidation.ValidateInstance(EvidenceAction(state, "page:interfaces:0"),
                    host.StructuredOutputSchema!.AsObject()));
            }
            var response = EvidenceAction(state, runtime.EvidenceId(request, address));
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!.AsObject()));
            Assert.Empty(PlanningContractValidation.ValidateInstance(LocalInputAction(state, "caller_value"), host.StructuredOutputSchema!));
            return new() { Json = response };
        };
        for (var index = 0; index < expectedCalls; index++)
        {
            state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state),
                new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
            Assert.Empty(state.Diagnostics);
            Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
            Assert.Null(state.Construction!.Compact!.CurrentStep);
            Assert.Empty(state.Construction.Checkpoints);
            Assert.Empty(state.Construction.Compact.PendingCommitments);
            Assert.Equal(0, state.ReplanAttempts);
        }
        Assert.Equal(expectedCalls, runtime.Calls.Count);
        Assert.Equal(calls + expectedCalls, state.ModelCalls);
        Assert.Equal(0, runtime.Discoveries);
        Assert.True(JsonNode.DeepEquals(declaration, state.Construction!.Evidence!["input:caller_value"]));
        Assert.NotNull(state.Construction.Evidence["page:interfaces:4"]);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var repeated = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(state, ["page:interfaces:0"]));
        Assert.Contains(repeated.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }


    [Fact]
    public async Task IncompleteNavigationRetainsExactObjectiveAndEveryPort()
    {
        var state = await Ready();
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var directory = PlanningCompactConstruction.InterfaceDirectory(state, null, 0);
        var context = PlanningCompactConstruction.ImplementationContext(state);
        var interfaces = context["interfaces"]!.AsArray();
        var entry = interfaces.OfType<JsonObject>().Single(item => item["task"]?.ToString() == "first");
        var prior = directory.OfType<JsonObject>().Single(item => item["task"]?.ToString() == "first");
        const string completeField = "objectiveTextComplete";
        const string absentField = "objectiveComplete";
        Assert.False(entry[completeField]!.GetValue<bool>());
        Assert.False(entry.ContainsKey(absentField));
        Assert.True(entry.ContainsKey("objective"));
        Assert.Equal(prior["objective"]!.ToString(), entry["objective"]?.ToString());
        foreach (var field in prior.Where(field => field.Key != "objective"))
            Assert.True(JsonNode.DeepEquals(field.Value, entry[field.Key]), field.Key);
        Assert.Equal(10, entry["portCount"]!.GetValue<int>());
        Assert.Equal(8, entry["ports"]!.AsArray().Count);
        Assert.NotNull(entry["nextPorts"]);
        var shortEntry = interfaces.OfType<JsonObject>().Single(item => item["task"]?.ToString() == "short");
        Assert.True(shortEntry[completeField]!.GetValue<bool>());
        Assert.False(shortEntry.ContainsKey(absentField));
        Assert.NotNull(state.Plan);
        Assert.Equal(state.Plan.Root.Tasks[2].Objective, shortEntry["objective"]!.ToString());
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));

        var address = entry["objectiveEvidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        var evidence = Assert.IsType<JsonObject>(state.Construction!.Evidence);
        Assert.Equal(state.Plan.Root.Tasks[0].Objective, evidence[address]!["text"]!.ToString());
        Assert.Null(evidence[address]!["next"]);
        var supplied = PlanningCompactConstruction.ImplementationContext(state)["interfaces"]!.AsArray()
            .OfType<JsonObject>().Single(item => item["task"]?.ToString() == "first");
        Assert.Equal(prior["objective"]!.ToString(), supplied["objective"]?.ToString());
        Assert.Equal(address, supplied["objectiveEvidence"]!.ToString());
        var stale = PlannerFixture.Clone(state);
        stale.Construction!.EvidenceRequests.Clear();
        stale.Plan!.Root.Tasks[0].Objective += " Changed original.";
        var before = JsonSerializer.Serialize(stale, PlanningJsonContext.Default.PlanningSession);
        var error = Assert.Throws<PlanningResponseException>(() => PlanningConstruction.LoadEvidence(stale, [address]));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_EVIDENCE_UNKNOWN");
        Assert.Equal(before, JsonSerializer.Serialize(stale, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task CompleteSelectionRequestRetainsBoundedMeaningAndExactRetrievalLinks()
    {
        var state = await Ready();
        var context = PlanningCompactConstruction.ImplementationContext(state);
        var previous = context.DeepClone().AsObject();
        foreach (var item in previous["interfaces"]!.AsArray().OfType<JsonObject>())
            if (item["objectiveTextComplete"]?.GetValue<bool>() == false) item.Remove("objective");
        var schema = PlanningCompactConstruction.ImplementationSchema(state);
        var current = PlanningRequestMeasurements.Measure(Request(state, context, schema),
            PlanningCompactConstruction.ImplementationPurpose, 3000);
        var old = PlanningRequestMeasurements.Measure(Request(state, previous, schema),
            PlanningCompactConstruction.ImplementationPurpose, 3000);
        Assert.Equal(old.SchemaHash, current.SchemaHash);
        Assert.Equal(old.BaselineHash, current.BaselineHash);
        Assert.True(current.CompleteRequestBytes > old.CompleteRequestBytes);
        Assert.InRange(current.EstimatedInputTokens, 1,
            PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.ImplementationPurpose).MaxInputTokens);
        Assert.Equal(current.CompleteRequestBytes, current.Components.Sum(component => component.SerializedBytes));
        foreach (var field in previous.Where(field => field.Key != "interfaces"))
            Assert.True(JsonNode.DeepEquals(field.Value, context[field.Key]), field.Key);
        var after = context["interfaces"]!.AsArray().OfType<JsonObject>().Where(item => item["task"] is not null).ToArray();
        Assert.All(after, item =>
        {
            var excerpt = item["objective"]!.ToString();
            Assert.InRange(JsonEncodedText.Encode(JsonEncodedText.Encode(excerpt).ToString()).EncodedUtf8Bytes.Length, 1, 192);
            Assert.StartsWith(excerpt, state.Plan!.Root.Tasks.Single(task => task.Id == item["task"]!.ToString()).Objective, StringComparison.Ordinal);
            if (item["objectiveTextComplete"]?.GetValue<bool>() == false) Assert.NotNull(item["objectiveEvidence"]);
        });
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("draft")]
    [InlineData("correction")]
    public async Task TruncatedObjectiveTextDoesNotReopenAClosedProducerOrHideItsExport(string phase)
    {
        var state = await ClosedProducerState();
        var construction = state.Construction!;
        var producer = state.Plan!.Root.Tasks[0];
        var selected = new PlanningConstructionStep { Id = "return_result", Scope = "/root", Kind = "close",
            Objective = "Return the accepted result.", RequirementIds = ["req_0"], OutputNames = ["result"],
            Inputs = [new() { Kind = "output", Source = producer.Id, Port = "result" }], DiscoveryComplete = true };
        PlanningCompactConstruction.ValidateSelection(state, selected);
        if (phase != "selection") construction.Compact!.CurrentStep = selected;
        if (phase == "correction")
        {
            construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["req_0"],
                Outputs = [new("result", new() { Kind = "output", Source = producer.Id, Port = "result" })] });
            Assert.Empty(construction.Candidate.Diagnostics);
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var context = phase == "selection" ? PlanningCompactConstruction.ImplementationContext(state) :
            PlanningCompactConstruction.Context(state, useCandidate: phase == "correction");
        var entries = phase == "selection" ? context["interfaces"]!.AsArray() : context["taskEvidence"]!["items"]!.AsArray();
        var entry = Assert.Single(entries.OfType<JsonObject>(), item => item["task"]?.ToString() == producer.Id);
        Assert.False(entry["objectiveTextComplete"]!.GetValue<bool>());
        Assert.False(entry.ContainsKey("objectiveComplete"));
        Assert.StartsWith(entry["objective"]!.ToString(), producer.Objective, StringComparison.Ordinal);
        var directory = PlanningCompactConstruction.InterfaceDirectory(state, [producer.Id], 0);
        var export = Assert.Single(directory.OfType<JsonObject>(), item => item["task"]?.ToString() == producer.Id);
        Assert.Equal("result", Assert.Single(export["ports"]!.AsArray())!.ToString());
        Assert.Equal(new[] { "/root" }, construction.OpenScopes);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));

        var address = entry["objectiveEvidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        Assert.Equal(producer.Objective, construction.Evidence![address]!["text"]!.ToString());
        Assert.Equal(PlanningGraphCompiler.Fingerprint(producer.Objective), construction.Evidence[address]!["version"]!.ToString());
        Assert.Null(construction.Evidence[address]!["next"]);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(new[] { "/root" }, construction.OpenScopes);
        Assert.Empty(construction.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task OpenOwnerTextCoverageDoesNotReplaceTheOpenScopeIdentity()
    {
        var state = await ClosedProducerState();
        const string scope = "/tasks/producer/body";
        state.Construction!.OpenScopes.Add(scope);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.ImplementationContext(state);
        var owner = Assert.Single(context["scopes"]!.AsArray().OfType<JsonObject>(), entry => entry["path"]?.ToString() == scope);
        Assert.False(owner["objectiveTextComplete"]!.GetValue<bool>());
        Assert.False(owner.ContainsKey("objectiveComplete"));
        Assert.Equal("scope:" + scope, owner["evidence"]!.ToString());
        Assert.DoesNotContain(context["interfaces"]!.AsArray(), entry => entry?["task"]?.ToString() == "producer");
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        PlanningConstruction.LoadEvidence(state, [owner["objectiveEvidence"]!.ToString()]);
        Assert.Equal(state.Plan!.Root.Tasks[0].Objective,
            state.Construction.Evidence![owner["objectiveEvidence"]!.ToString()]!["text"]!.ToString());
        Assert.Equal(new[] { "/root", scope }, state.Construction.OpenScopes);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactDeclaredInputNamesDoNotDependOnTheInterfacePage(bool missingPlan)
    {
        var state = await EmptyState();
        var inputs = Enumerable.Range(0, 17).Select(index => new TaskInput
            { Name = index == 0 ? "query" : "caller_" + index, Type = new() { Kind = "string" } }).ToList();
        inputs[^1].Type = new() { Kind = "object", Fields = [new() { Name = "nested", Type = new() { Kind = "string" }, Required = true }] };
        state.Requirements!.Inputs = inputs;
        state.Plan!.Inputs = inputs;
        if (missingPlan) state.Plan = null;
        Assert.DoesNotContain(PlanningCompactConstruction.InterfaceDirectory(state, null, 0), row => row?["input"]?.ToString() == inputs[^1].Name);
        var schema = ImportSchema(PlanningCompactConstruction.ImplementationSchema(state));
        foreach (var input in inputs)
            Assert.Empty(PlanningContractValidation.ValidateInstance(Input(input.Name), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Input("input:query"), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Field(Input("input:query")), schema));
        var selected = Field(Input(inputs[^1].Name));
        Assert.Empty(PlanningContractValidation.ValidateInstance(selected, schema));
        state.Catalog!.AllowedStepTypes.Add("set"); // Existing checked field projection permission.
        PlanningCompactConstruction.ValidateSelection(state, new() { Scope = "/root", Kind = "value",
            RequirementIds = ["req_0"], Inputs = [selected.Deserialize(PlanningJsonContext.Default.TaskValue)!] });

        // A prefix is not forbidden when it is part of the exact declared name.
        inputs.Add(new() { Name = "input:query", Type = new() { Kind = "string" } });
        Assert.Empty(PlanningContractValidation.ValidateInstance(Input("input:query"), ImportSchema(PlanningCompactConstruction.ImplementationSchema(state))));
        inputs.Clear();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Input("query"), ImportSchema(PlanningCompactConstruction.ImplementationSchema(state))));
    }

    [Fact]
    public async Task OfferedScopeInputUnionPreservesIsolatedGroupValidationAndFixedReselection()
    {
        var state = await EmptyState();
        state.Plan!.Root.Tasks.Add(new() { Id = "root_child", Kind = "sequence", Objective = "Keep caller scope work together.", Body = new() });
        state.Plan.Groups.Add(new() { Id = "isolated", Inputs = [new() { Name = "parameter", Type = new() { Kind = "string" } }],
            Body = new() { Tasks = [new() { Id = "group_child", Kind = "sequence", Objective = "Keep parameter scope work together.", Body = new() }] } });
        state.Construction!.OpenScopes = ["/root", "/tasks/root_child/body", "/groups/isolated/body", "/tasks/group_child/body"];
        var schema = ImportSchema(PlanningCompactConstruction.ImplementationSchema(state));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Input("caller_value"), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Input("parameter"), schema));
        foreach (var scope in state.Construction.OpenScopes)
        {
            var isolated = scope is "/groups/isolated/body" or "/tasks/group_child/body";
            var step = new PlanningConstructionStep { Scope = scope, Kind = "value", RequirementIds = ["req_0"],
                Inputs = [new() { Kind = "input", Source = isolated ? "parameter" : "caller_value" }] };
            PlanningCompactConstruction.ValidateSelection(state, step);
            step.Inputs[0].Source = isolated ? "caller_value" : "parameter";
            var error = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, step));
            Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_STEP_INPUT_INVALID");
        }
        state.Construction.Compact!.ReplacementScope = "/groups/isolated/body";
        state.Construction.Compact.ReplacementRequirements = ["req_0"];
        state.Construction.Compact.RejectedSteps.Add(new JsonObject { ["reason"] = "Use the exact isolated parameter." });
        var replacement = ImportSchema(PlanningCompactConstruction.ImplementationSchema(state));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Input("parameter"), replacement));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Input("caller_value"), replacement));
    }

    [Theory]
    [InlineData("query")]
    [InlineData("input:query")]
    public async Task ExactDeclaredInputSelectsLocalWorkWithoutDiscovery(string name)
    {
        var state = await EmptyState();
        state.Plan!.Inputs[0].Name = name;
        var accepted = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var response = LocalInputAction(state, name);
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!.AsObject()));
            return new() { Json = response };
        } };
        var result = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(result.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.Equal(0, runtime.Discoveries);
        Assert.Equal("value", result.Construction!.Compact!.CurrentStep!.Kind);
        Assert.True(result.Construction.Compact.CurrentStep.DiscoveryComplete);
        Assert.Equal(name, Assert.Single(result.Construction.Compact.CurrentStep.Inputs).Source);
        Assert.Equal(accepted, PlanningConstruction.Hash(result.Plan));
        Assert.Empty(result.Construction.Checkpoints);
        Assert.Equal(0, result.ReplanAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyAcceptedPlanOffersCallerImportsButNoTaskReferences(bool missingPlan)
    {
        var state = await EmptyState();
        if (missingPlan) state.Plan = null;
        var schema = ImportSchema(PlanningCompactConstruction.ImplementationSchema(state));
        foreach (var kind in new[] { "input" })
        {
            var value = Import(kind);
            Assert.Empty(PlanningContractValidation.ValidateInstance(value, schema));
            Assert.Empty(PlanningContractValidation.ValidateInstance(Field(value), schema));
        }
        foreach (var kind in new[] { "output", "present", "choice", "item", "index" })
        {
            var value = Import(kind);
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(value, schema));
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Field(Field(value)), schema));
        }
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject
            { ["kind"] = "field", ["port"] = "nested", ["items"] = new JsonArray() }, schema));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("always")]
    [InlineData("nested")]
    [InlineData("group")]
    public async Task OnlyVisibleAcceptedTaskPortsEnterSelectionGrammar(string placement)
    {
        var state = await EmptyState();
        var task = new PlanTask { Id = "accepted", Kind = "value", Objective = "Keep the accepted value.",
            Outputs = [new("field", new() { Kind = "string", Text = "accepted" })] };
        switch (placement)
        {
            case "root": state.Plan!.Root.Tasks.Add(task); break;
            case "always": state.Plan!.Root.Always.Add(task); break;
            case "nested": state.Plan!.Root.Tasks.Add(new() { Id = "container", Kind = "sequence", Body = new() { Tasks = [task] } }); break;
            case "group": state.Plan!.Groups.Add(new() { Id = "reusable", Body = new() { Tasks = [task] } }); break;
        }
        var schema = ImportSchema(PlanningCompactConstruction.ImplementationSchema(state));
        foreach (var kind in new[] { "output", "present" })
        {
            Assert.Equal(placement == "root", PlanningContractValidation.ValidateInstance(Import(kind), schema).Count == 0);
            Assert.Equal(placement == "root", PlanningContractValidation.ValidateInstance(Field(Import(kind)), schema).Count == 0);
        }
        // Offered syntax never grants access to a different task identity.
        var invalid = new PlanningConstructionStep { Scope = "/root", Kind = "value", Objective = "Use the supplied value.",
            RequirementIds = ["req_0"], Inputs = [new() { Kind = "present", Source = "unknown" }] };
        var error = Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.ValidateSelection(state, invalid));
        Assert.Contains(error.Diagnostics, item => item.Code == "CONSTRUCTION_STEP_INVALID");
    }

    [Fact]
    public async Task TasklessClosedGroupRemainsSelectableWithCallerInput()
    {
        var state = await EmptyState();
        state.Plan!.Groups.Add(new() { Id = "reusable", Body = new()
            { Outputs = [new("field", new() { Kind = "string", Text = "constant" })] } });
        Assert.Empty(new TaskPlanSymbols(state.Plan).Tasks);
        var response = EvidenceAction(state, "scope:/root");
        response["evidenceRequests"] = null;
        response["step"] = new JsonObject
        {
            ["scope"] = "/root", ["kind"] = "call", ["inputs"] = new JsonArray(Import("input")),
            ["groupId"] = "reusable", ["dependencyTaskIds"] = new JsonArray(),
            ["closeRequirementIds"] = new JsonArray(),
            ["outputNames"] = new JsonArray(), ["prerequisites"] = null,
            ["operationIds"] = new JsonArray(), ["closeScopes"] = null
        };
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, PlanningCompactConstruction.ImplementationSchema(state)));
        PlanningCompactConstruction.ValidateSelection(state, new() { Scope = "/root", Kind = "call", GroupId = "reusable",
            Objective = "Use the declared reusable result.", RequirementIds = ["req_0"], Inputs = [new() { Kind = "input", Source = "caller_value" }] });
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
    }


    private static JsonObject Input(string name) => new() { ["kind"] = "input", ["source"] = name };

    private static JsonObject LocalInputAction(PlanningSession state, string name) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["evidenceRequests"] = null, ["clarifications"] = null,
        ["step"] = new JsonObject
        {
            ["scope"] = "/root", ["kind"] = "value", ["inputs"] = new JsonArray(Input(name)), ["groupId"] = null,
            ["dependencyTaskIds"] = new JsonArray(), ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray(), ["prerequisites"] = null,
            ["operationIds"] = new JsonArray(), ["closeScopes"] = null
        }
    };

    private static async Task<PlanningSession> EmptyState()
    {
        var state = await Ready();
        var input = new TaskInput { Name = "caller_value", Type = new() { Kind = "string" } };
        state.Plan = new() { Inputs = [input] };
        state.Requirements!.Inputs = [input];
        return state;
    }

    private static JsonObject Import(string kind)
    {
        var value = new JsonObject { ["kind"] = kind };
        if (kind is "input" or "choice" or "present" or "output") value["source"] = kind == "input" ? "caller_value" : "accepted";
        if (kind == "output") value["port"] = "field";
        return value;
    }

    private static JsonObject Field(JsonObject value) => new()
        { ["kind"] = "field", ["port"] = "nested", ["items"] = new JsonArray(value) };

    private static JsonObject ImportSchema(JsonObject schema)
    {
        JsonNode? Resolve(JsonNode? node)
        {
            while (node is JsonObject obj && obj["$ref"] is JsonValue reference)
                node = schema["$defs"]![reference.ToString()[8..]];
            return node;
        }
        var names = schema["$defs"]!.AsObject().Where(definition => Resolve(definition.Value)?["anyOf"] is JsonArray alternatives &&
            alternatives.Any(alternative => Resolve(Resolve(alternative)?["properties"]?["kind"])?["enum"] is JsonArray kinds &&
                kinds.Any(kind => kind?.ToString() == "field"))).Select(definition => definition.Key).ToArray();
        if (names.Length == 0) return PlanningSchemas.Type("null");
        return new() { ["anyOf"] = new JsonArray(names.Select(name => (JsonNode)new JsonObject { ["$ref"] = "#/$defs/" + name }).ToArray()),
            ["$defs"] = schema["$defs"]!.DeepClone() };
    }

    private static JsonObject EvidenceFreeSchema(LLMRequest request)
    {
        var schema = request.StructuredOutputSchema!.DeepClone().AsObject();
        schema["properties"]!.AsObject().Remove("evidenceRequests");
        schema.Remove("$comment");
        return schema["$defs"] is JsonObject ? PlanningSchemas.Compact(schema, preserveDescriptions: true) : schema;
    }

    private static LLMRequest Request(PlanningSession state, JsonObject context, JsonObject schema)
    {
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.ImplementationGuidance(state),
            context, schema.DeepClone().AsObject(), PlanningCompactConstruction.ImplementationPurpose);
        request.Provider = "OpenAi"; request.Model = "retained-model"; request.StructuredOutputStrict = true;
        request.UseBackgroundMode = true; request.MaxTokens = 8192;
        request.ClientRequestId = state.Request.SessionId + ":3:" + new string('a', 64);
        return PlanningGenerationPolicy.Apply(request, state.Request.Generation);
    }

    private static JsonObject EvidenceAction(PlanningSession state, string address) => new()
    {
        ["baseHash"] = PlanningConstruction.Hash(state.Plan), ["step"] = null, ["clarifications"] = null,
        ["evidenceRequests"] = new JsonArray(address)
    };

    private static async Task<PlanningSession> PagingState()
    {
        var state = await Ready();
        var input = new TaskInput { Name = "caller_value", Type = new() { Kind = "string", Enum = ["first", "second"] }, Required = true };
        state.Plan!.Inputs = [input];
        state.Requirements!.Inputs = [input];
        for (var index = 0; index < 2; index++)
            state.Plan.Root.Tasks.Add(new() { Id = "later_" + index, Kind = "value", Objective = "Keep this accepted value.",
                Outputs = [new("field", new() { Kind = "string", Text = "accepted" })] });
        return state;
    }

    private static async Task<PlanningSession> ClosedProducerState()
    {
        var state = await Ready();

        state.Construction!.OpenScopes = ["/root"];
        state.Requirements!.Outputs = [new() { Name = "result", Type = new() { Kind = "string" } }];
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "producer", Kind = "sequence",
            Objective = string.Concat(Enumerable.Repeat("Preserve the exact accepted value and return its declared result. ", 8)),
            Body = new() { Tasks = [new() { Id = "leaf", Kind = "value", Objective = "Keep the accepted value.",
                    Outputs = [new("value", new() { Kind = "string", Text = "accepted" })] }],
                Outputs = [new("result", new() { Kind = "output", Source = "leaf", Port = "value" })] } }] } };
        return state;
    }

    private static async Task<PlanningSession> Ready()
    {
        const string objective = "Preserve every supplied value and its ordering in the requested result.";
        var runtime = new TestRuntime { Respond = (request, _) => new() { Json = new JsonObject
        {
            ["evidenceRequests"] = null, ["clarifications"] = null, ["clauses"] = new JsonArray(new JsonObject { ["text"] = objective, ["kind"] = "requirement", ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray() })
        } } };
        var state = await new HybridWorkflowPlanner().AdvanceAsync(new() { Request = new()
        { TenantId = "selection-objectives", Prompt = objective, Options = new() { ["automatic_correction"] = true } } },
            new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
        PlanningCompactConstruction.SelectBlock(state, new JsonObject
        {
            ["objective"] = objective, ["requirementIds"] = new JsonArray("req_0"),
            ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray(objective),
            ["dependencies"] = new JsonArray(), ["successCriteria"] = new JsonArray("Preserve the exact original values and declared result.")
        });
        state.Catalog = new();
        state.Plan = new() { Root = new() { Tasks = [Task("first", 10), Task("second", 7), Task("short", 1)] } };
        state.Plan.Root.Tasks[2].Objective = "Keep the supplied value.";
        return state;

        static PlanTask Task(string id, int ports) => new()
        {
            Id = id, Kind = "value", Objective = string.Concat(Enumerable.Repeat("Keep each original observed value and its exact identity. ", 8)),
            Outputs = Enumerable.Range(0, ports).Select(index => new TaskOutput("field_" + index,
                new() { Kind = "string", Text = "observed-" + index })).ToList()
        };
    }
}
