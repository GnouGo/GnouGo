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
            Assert.Equal(next, PlanningCompactConstruction.SelectionContext(state)["nextInterfaces"]!.ToString());
            Read(next);
        }
        Assert.Null(PlanningCompactConstruction.SelectionContext(state)["nextInterfaces"]);
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
            var request = Request(state, PlanningCompactConstruction.SelectionContext(state), PlanningCompactConstruction.SelectionSchema(state));
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
        var grammar = Request(state, PlanningCompactConstruction.SelectionContext(state),
            PlanningCompactConstruction.SelectionSchema(state)).StructuredOutputSchema!.DeepClone().AsObject();
        grammar["properties"]!.AsObject().Remove("evidenceRequests");
        Assert.Null(PlanningCompactConstruction.SelectionContext(state)["interfacesDirectory"]);
        if (firstPageAlreadyRead) PlanningConstruction.LoadEvidence(state, ["page:interfaces:0"]);
        var addresses = new Queue<string>(firstPageAlreadyRead
            ? ["page:interfaces:4", "input:caller_value"]
            : ["page:interfaces:4", "page:interfaces:0", "input:caller_value"]);
        var expectedCalls = addresses.Count;
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            var context = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
            var address = addresses.Dequeue();
            if (address == "page:interfaces:0")
            {
                Assert.Null(context["interfaces"]);
                Assert.Null(context["nextInterfaces"]);
                Assert.Equal(address, context["interfacesDirectory"]!.ToString());
                Assert.Null(context["evidence"]?[address]);
            }
            if (address == "input:caller_value")
            {
                Assert.Null(context["interfacesDirectory"]);
                Assert.Contains(context["evidence"]!["page:interfaces:0"]!.AsArray(), item => item?["input"]?.ToString() == "caller_value");
                Assert.NotEmpty(PlanningContractValidation.ValidateInstance(EvidenceAction(state, "page:interfaces:0"),
                    request.StructuredOutputSchema!.AsObject()));
            }
            var response = EvidenceAction(state, address);
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!.AsObject()));
            var actualGrammar = request.StructuredOutputSchema.DeepClone().AsObject();
            actualGrammar["properties"]!.AsObject().Remove("evidenceRequests");
            Assert.True(JsonNode.DeepEquals(grammar, actualGrammar));
            return new() { Json = response };
        } };
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

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task SavedPagedSelectionRetainsItsIssuedNavigationBytes(int presentation)
    {
        var state = await PagingState();
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        PlanningConstruction.LoadEvidence(state, ["page:interfaces:4"]);
        var context = PlanningCompactConstruction.SelectionContext(state);
        Assert.Equal(presentation == 5, context.ContainsKey("interfacesDirectory"));
        var current = Request(state, context, PlanningCompactConstruction.SelectionSchema(state));
        context.Remove("interfacesDirectory");
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        if (presentation == 5) PlanningCompactConstruction.ConstrainSelectionEvidence(state, context, schema);
        var issued = Request(state, context, schema);
        var saved = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        if (presentation == 5) Assert.NotEqual(saved, JsonSerializer.Serialize(current, PlanningJsonContext.Default.LLMRequest));
        else Assert.Equal(saved, JsonSerializer.Serialize(current, PlanningJsonContext.Default.LLMRequest));
        state.ModelCalls++;
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.SelectPurpose, Request = issued };
        state = PlannerFixture.Clone(state);
        var calls = state.ModelCalls;
        var accepted = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(saved, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            var response = EvidenceAction(state, "scope:/root");
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!.AsObject()));
            return new() { Json = response };
        } };
        var recovered = await new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(recovered.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.Equal(calls, recovered.ModelCalls);
        Assert.Equal(0, recovered.ReplanAttempts);
        Assert.Equal(accepted, PlanningConstruction.Hash(recovered.Plan));
        Assert.Equal("caller_value", recovered.Construction!.Evidence!["scope:/root"]!["inputs"]![0]!["name"]!.ToString());
    }

    [Theory]
    [InlineData("fresh")]
    [InlineData("historical")]
    [InlineData("retainedCompact")]
    public async Task IncompleteNavigationRetainsExactObjectiveAndEveryPort(string profile)
    {
        var state = await Ready();
        if (profile == "historical") state.Construction!.Compact!.PresentationVersion = null;
        if (profile == "retainedCompact")
        {
            state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
            state.Construction!.Limits.PhasePolicy = null;
        }
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var directory = PlanningCompactConstruction.InterfaceDirectory(state, null, 0);
        var context = PlanningCompactConstruction.SelectionContext(state);
        var interfaces = context["interfaces"]!.AsArray();
        var entry = interfaces.OfType<JsonObject>().Single(item => item["task"]?.ToString() == "first");
        var prior = directory.OfType<JsonObject>().Single(item => item["task"]?.ToString() == "first");
        Assert.False(entry["objectiveComplete"]!.GetValue<bool>());
        Assert.Equal(profile != "retainedCompact", entry.ContainsKey("objective"));
        Assert.Equal(profile != "retainedCompact" ? prior["objective"]!.ToString() : null, entry["objective"]?.ToString());
        foreach (var field in prior.Where(field => field.Key != "objective"))
            Assert.True(JsonNode.DeepEquals(field.Value, entry[field.Key]), field.Key);
        Assert.Equal(10, entry["portCount"]!.GetValue<int>());
        Assert.Equal(8, entry["ports"]!.AsArray().Count);
        Assert.NotNull(entry["nextPorts"]);
        var shortEntry = interfaces.OfType<JsonObject>().Single(item => item["task"]?.ToString() == "short");
        Assert.True(shortEntry["objectiveComplete"]!.GetValue<bool>());
        Assert.NotNull(state.Plan);
        Assert.Equal(state.Plan.Root.Tasks[2].Objective, shortEntry["objective"]!.ToString());
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));

        var address = entry["objectiveEvidence"]!.ToString();
        PlanningConstruction.LoadEvidence(state, [address]);
        var evidence = Assert.IsType<JsonObject>(state.Construction!.Evidence);
        Assert.Equal(state.Plan.Root.Tasks[0].Objective, evidence[address]!["text"]!.ToString());
        Assert.Null(evidence[address]!["next"]);
        var supplied = PlanningCompactConstruction.SelectionContext(state)["interfaces"]!.AsArray()
            .OfType<JsonObject>().Single(item => item["task"]?.ToString() == "first");
        Assert.Equal(profile != "retainedCompact" ? prior["objective"]!.ToString() : null, supplied["objective"]?.ToString());
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
        var context = PlanningCompactConstruction.SelectionContext(state);
        var previous = context.DeepClone().AsObject();
        foreach (var item in previous["interfaces"]!.AsArray().OfType<JsonObject>())
            if (item["objectiveComplete"]?.GetValue<bool>() == false) item.Remove("objective");
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        var current = PlanningRequestMeasurements.Measure(Request(state, context, schema),
            PlanningCompactConstruction.SelectPurpose, 3000);
        var old = PlanningRequestMeasurements.Measure(Request(state, previous, schema),
            PlanningCompactConstruction.SelectPurpose, 3000);
        Assert.Equal(old.SchemaHash, current.SchemaHash);
        Assert.Equal(old.BaselineHash, current.BaselineHash);
        Assert.True(current.CompleteRequestBytes > old.CompleteRequestBytes);
        Assert.InRange(current.EstimatedInputTokens, 1,
            PlanningConstructionPhasePolicies.Resolve(state.Request, PlanningCompactConstruction.SelectPurpose).MaxInputTokens);
        Assert.Equal(current.CompleteRequestBytes, current.Components.Sum(component => component.SerializedBytes));
        foreach (var field in previous.Where(field => field.Key != "interfaces"))
            Assert.True(JsonNode.DeepEquals(field.Value, context[field.Key]), field.Key);
        var after = context["interfaces"]!.AsArray().OfType<JsonObject>().Where(item => item["task"] is not null).ToArray();
        Assert.All(after, item =>
        {
            var excerpt = item["objective"]!.ToString();
            Assert.InRange(JsonEncodedText.Encode(JsonEncodedText.Encode(excerpt).ToString()).EncodedUtf8Bytes.Length, 1, 192);
            Assert.StartsWith(excerpt, state.Plan!.Root.Tasks.Single(task => task.Id == item["task"]!.ToString()).Objective, StringComparison.Ordinal);
            if (item["objectiveComplete"]?.GetValue<bool>() == false) Assert.NotNull(item["objectiveEvidence"]);
        });
    }

    [Fact]
    public async Task SavedSelectionWithoutNavigationPrefixesResumesExactIssuedBytesAndAccounting()
    {
        var state = await Ready();
        var context = PlanningCompactConstruction.SelectionContext(state);
        foreach (var item in context["interfaces"]!.AsArray().OfType<JsonObject>())
            if (item["objectiveComplete"]?.GetValue<bool>() == false) item.Remove("objective");
        var request = Request(state, context, PlanningCompactConstruction.SelectionSchema(state));
        state.ModelCalls++;
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.SelectPurpose, Request = request };
        state = PlannerFixture.Clone(state);
        var issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var calls = state.ModelCalls;
        var address = context["interfaces"]!.AsArray().OfType<JsonObject>().First()["objectiveEvidence"]!.ToString();
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = new JsonObject
        {
            ["baseHash"] = accepted, ["step"] = null, ["clarifications"] = null,
            ["evidenceRequests"] = new JsonArray(address)
        } } };
        var result = await new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(calls, result.ModelCalls);
        Assert.Equal(0, result.ReplanAttempts);
        Assert.Equal(accepted, PlanningConstruction.Hash(result.Plan));
        Assert.Equal(requirements, JsonSerializer.Serialize(result.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(state.Plan!.Root.Tasks[0].Objective, result.Construction!.Evidence![address]!["text"]!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyAcceptedPlanOffersCallerImportsButNoTaskReferences(bool missingPlan)
    {
        var state = await EmptyState();
        if (missingPlan) state.Plan = null;
        var schema = ImportSchema(PlanningCompactConstruction.SelectionSchema(state));
        foreach (var kind in new[] { "input", "choice", "item", "index" })
        {
            var value = Import(kind);
            Assert.Empty(PlanningContractValidation.ValidateInstance(value, schema));
            Assert.Empty(PlanningContractValidation.ValidateInstance(Field(value), schema));
        }
        foreach (var kind in new[] { "output", "present" })
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
    public async Task AnyAcceptedTaskKeepsTaskImportGrammar(string placement)
    {
        var state = await EmptyState();
        var task = new PlanTask { Id = "accepted", Kind = "value", Objective = "Keep the accepted value.",
            Outputs = [new("field", new() { Kind = "string", Text = "accepted" })] };
        switch (placement)
        {
            case "root": state.Plan!.Root.Tasks.Add(task); break;
            case "always": state.Plan!.Root.Always.Add(task); break;
            case "nested": state.Plan!.Root.Tasks.Add(new() { Id = "container", Kind = "group", Body = new() { Tasks = [task] } }); break;
            case "group": state.Plan!.Groups.Add(new() { Id = "reusable", Body = new() { Tasks = [task] } }); break;
        }
        var schema = ImportSchema(PlanningCompactConstruction.SelectionSchema(state));
        foreach (var kind in new[] { "output", "present" })
        {
            Assert.Empty(PlanningContractValidation.ValidateInstance(Import(kind), schema));
            Assert.Empty(PlanningContractValidation.ValidateInstance(Field(Import(kind)), schema));
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
            ["scope"] = "/root", ["kind"] = "call", ["objective"] = "Use the declared reusable result.",
            ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(Import("input")),
            ["groupId"] = "reusable", ["dependencyTaskIds"] = new JsonArray(),
            ["expectedOutputs"] = new JsonArray("The reusable result."), ["closeRequirementIds"] = new JsonArray(),
            ["outputNames"] = new JsonArray(), ["prerequisites"] = null
        };
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, PlanningCompactConstruction.SelectionSchema(state)));
        PlanningCompactConstruction.ValidateSelection(state, new() { Scope = "/root", Kind = "call", GroupId = "reusable",
            Objective = "Use the declared reusable result.", RequirementIds = ["req_0"], Inputs = [new() { Kind = "input", Source = "caller_value" }] });
        Assert.Empty(state.Construction!.Checkpoints);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task HistoricalEmptySelectionKeepsTaskImportGrammar(int presentation)
    {
        var state = await EmptyState();
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        var schema = ImportSchema(PlanningCompactConstruction.SelectionSchema(state));
        foreach (var kind in new[] { "input", "choice", "output", "present", "item", "index" })
        {
            Assert.Empty(PlanningContractValidation.ValidateInstance(Import(kind), schema));
            Assert.Empty(PlanningContractValidation.ValidateInstance(Field(Import(kind)), schema));
        }
    }

    [Fact]
    public async Task SavedZeroTaskSelectionKeepsIssuedTaskImportGrammarAndExactRequestBytes()
    {
        var state = await EmptyState();
        var schema = PlanningCompactConstruction.SelectionSchema(state);
        var key = ImportKey(schema);
        // A captured request may still have the unrestricted pre-pruning v5 grammar.
        schema["$defs"]![key] = new JsonObject { ["anyOf"] = new JsonArray(
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("output")), ("source", Text()), ("port", PlanningSchemas.Nullable(Text()))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("input", "choice", "present")), ("source", Text())),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("item", "index"))),
            PlanningSchemas.Object(("kind", PlanningSchemas.Enum("field")), ("port", Text()),
                ("items", PlanningSchemas.Array(PlanningSchemas.Ref(key), 1, 1)))) };
        var issued = Request(state, PlanningCompactConstruction.SelectionContext(state), schema);
        Assert.Empty(PlanningContractValidation.ValidateInstance(Import("present"), ImportSchema(issued.StructuredOutputSchema!.AsObject())));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Import("present"), ImportSchema(PlanningCompactConstruction.SelectionSchema(state))));
        var saved = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        state.ModelCalls++;
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.SelectPurpose, Request = issued };
        state = PlannerFixture.Clone(state);
        var calls = state.ModelCalls;
        var accepted = PlanningConstruction.Hash(state.Plan);
        var runtime = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(saved, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            var response = EvidenceAction(state, "scope:/root");
            Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!.AsObject()));
            return new() { Json = response };
        } };
        var recovered = await new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(recovered.Diagnostics);
        Assert.Single(runtime.Calls);
        Assert.Equal(calls, recovered.ModelCalls);
        Assert.Equal(0, recovered.ReplanAttempts);
        Assert.Equal(accepted, PlanningConstruction.Hash(recovered.Plan));
        Assert.Empty(recovered.Construction!.Checkpoints);
        Assert.Equal("caller_value", recovered.Construction.Evidence!["scope:/root"]!["inputs"]![0]!["name"]!.ToString());

        static JsonObject Text() => new() { ["type"] = "string", ["maxLength"] = 96 };
    }

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

    private static string ImportKey(JsonObject schema) => schema["$defs"]!.AsObject().Single(definition =>
        definition.Value?["anyOf"] is JsonArray alternatives && alternatives.Any(alternative =>
            alternative?["properties"]?["kind"]?["enum"] is JsonArray kinds && kinds.Any(kind => kind?.ToString() == "field"))).Key;

    private static JsonObject ImportSchema(JsonObject schema) => new()
        { ["$ref"] = "#/$defs/" + ImportKey(schema), ["$defs"] = schema["$defs"]!.DeepClone() };

    private static LLMRequest Request(PlanningSession state, JsonObject context, JsonObject schema)
    {
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state),
            context, schema.DeepClone().AsObject(), PlanningCompactConstruction.SelectPurpose);
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

    private static async Task<PlanningSession> Ready()
    {
        const string objective = "Preserve every supplied value and its ordering in the requested result.";
        var runtime = new TestRuntime { Respond = (request, _) => new() { Json = new JsonObject
        {
            ["sourceId"] = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(new JsonObject { ["text"] = objective, ["kind"] = "requirement", ["interfaces"] = "none" })
        } } };
        var state = await new HybridWorkflowPlanner().AdvanceAsync(new() { Request = new()
        { TenantId = "selection-objectives", Prompt = objective, Options = new() { ["automatic_correction"] = true } } },
            new() { ExpectedRevision = 0 }, runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics);
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
