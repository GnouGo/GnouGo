using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactConstructionClosureTests
{
    [Fact]
    public void ReadyLeafRetainsOneDraftCompletion()
    {
        var state = State();
        var schema = PlanningCompactConstruction.DraftSchema(state);
        AssertCompletion(schema, allowed: true);
        AssertScopeClosure(schema, "/root", allowed: true);
        var extension = Leaf(complete: true);
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Response(state, extension, schema), schema));
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(candidate.OpenScopes);
        Assert.Empty(PlanningCompactConstruction.CandidateFindings(WithCandidate(state, candidate)));
    }

    [Theory]
    [InlineData("remaining_requirement")]
    [InlineData("reopened_requirement")]
    [InlineData("unindexed_request")]
    [InlineData("unindexed_answer")]
    [InlineData("unindexed_revision")]
    [InlineData("open_child")]
    [InlineData("open_group")]
    public void RootPresentationExcludesKnownPrematureCompletion(string obstacle)
    {
        var state = State();
        var construction = state.Construction!;
        switch (obstacle)
        {
            case "remaining_requirement":
                construction.Requirements.Add(new() { Id = "later" });
                state.Requirements!.Outcomes.Add(new("later", "Preserve the other original obligation."));
                break;
            case "reopened_requirement":
                construction.Requirements[0].Status = "closed";
                construction.Compact!.CurrentStep!.CloseRequirementIds.Clear();
                break;
            case "unindexed_request": construction.Compact!.Sources[0].Indexed = false; break;
            case "unindexed_answer": construction.Compact!.Sources.Add(new() { Id = "answer:0", Kind = "answer_page" }); break;
            case "unindexed_revision": construction.Compact!.Sources.Add(new() { Id = "revision:0", Kind = "revision_page" }); break;
            case "open_child":
                state.Plan = new() { Root = new() { Tasks = [Container("container")] } };
                construction.OpenScopes.Add("/tasks/container/body");
                break;
            case "open_group":
                state.Plan = new() { Groups = [new() { Id = "reusable", Body = new() }] };
                construction.OpenScopes.Add("/groups/reusable/body");
                break;
        }
        construction.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        var schema = PlanningCompactConstruction.DraftSchema(state);
        AssertCompletion(schema, allowed: false);
        AssertScopeClosure(schema, "/root", allowed: false);
        AssertScopeClosure(schema, null, allowed: true);
    }

    [Theory]
    [InlineData("group", false)]
    [InlineData("sequence", false)]
    [InlineData("conditional", false)]
    [InlineData("parallel", false)]
    [InlineData("foreach", false)]
    [InlineData("group", true)]
    [InlineData("sequence", true)]
    [InlineData("conditional", true)]
    [InlineData("parallel", true)]
    [InlineData("foreach", true)]
    public void ANewContainerPresentationLeavesItsChildScopesForLaterDrafts(string kind, bool current)
    {
        var state = State();
        if (current) PlanningSessionCreation.Capture(state);
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = kind;
        step.CloseRequirementIds.Clear();
        step.OutputNames.Clear();
        var selection = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance(state),
            PlanningCompactConstruction.SelectionContext(state), PlanningCompactConstruction.SelectionSchema(state),
            PlanningCompactConstruction.SelectPurpose);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Equal(current, selection.Prompt.Contains("Select an empty container/group opening separately from filling and closing its children", StringComparison.Ordinal));
        Assert.Equal(current, request.StructuredOutputSchema!["description"]!.ToString().Contains("Do not consume or export the new container's outputs until its children are complete and closed", StringComparison.Ordinal));
        Assert.DoesNotContain("use reselectStep when offered", PlanningCompactConstruction.DraftSemantics(state, step, review: true), StringComparison.Ordinal);
        var schema = request.StructuredOutputSchema!.AsObject();
        AssertCompletion(schema, allowed: false);
        AssertScopeClosure(schema, "/root", allowed: false);
        var extension = new PlanningConstructionExtension { RequirementIds = ["req"] };
        if (kind == "group") extension.Groups.Add(new() { Id = "reusable", Body = new() });
        else
        {
            var task = new PlanTask { Id = "container", Kind = kind, Objective = "Open the scope for the required work." };
            if (kind == "parallel") task.Branches = [new(), new()];
            else task.Body = new();
            if (kind == "conditional") { task.Otherwise = new(); task.Condition = new() { Kind = "boolean", Boolean = true }; }
            if (kind == "foreach") task.Items = new() { Kind = "array", Items = [new() { Kind = "string", Text = "item" }] };
            extension.Tasks.Add(task);
        }
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Response(state, extension, schema), schema));
        var original = PlanningConstruction.Hash(state.Plan);
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        var open = candidate.OpenScopes.ToHashSet(StringComparer.Ordinal);
        Assert.True(open.Count > 1);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request, open).Diagnostics);
        Assert.NotEqual("closed", Assert.Single(state.Construction.Requirements).Status);
        Assert.Equal(original, PlanningConstruction.Hash(state.Plan));
        if (kind == "group") candidate.Plan.Root.Tasks.Add(new() { Id = "premature", Kind = "call", Group = "reusable", Objective = "Read unfinished work." });
        else candidate.Plan.Root.Outputs.Add(new("message", new() { Kind = "output", Source = "container", Port = "message" }));
        Assert.Contains(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request, open).Diagnostics,
            diagnostic => diagnostic.Code == "TASK_SCOPE_OPEN");
    }

    [Fact]
    public void AnUnrelatedClosedRequirementDoesNotPreventTheFinalLeaf()
    {
        var state = State();
        state.Construction!.Requirements.Add(new() { Id = "earlier", Status = "closed" });
        state.Requirements!.Outcomes.Add(new("earlier", "Already accepted independent work."));
        AssertCompletion(PlanningCompactConstruction.DraftSchema(state), allowed: true);
    }

    [Fact]
    public void ARequirementOnlyCloseStepMayPreservePreviouslyClosedRequirements()
    {
        var state = State();
        state.Construction!.Requirements[0].Status = "closed";
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "close";
        step.CloseRequirementIds.Clear();
        AssertCompletion(PlanningCompactConstruction.DraftSchema(state), allowed: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildClosureUsesParentRelationshipsAndIgnoresUnrelatedSiblings(bool openDescendant)
    {
        var state = State();
        var selected = Container("outer");
        if (openDescendant) selected.Body!.Tasks.Add(Container("inner"));
        state.Plan = new() { Root = new() { Tasks = [selected, Container("sibling")] } };
        state.Construction!.OpenScopes = ["/root", "/tasks/outer/body", "/tasks/sibling/body"];
        if (openDescendant) state.Construction.OpenScopes.Add("/tasks/inner/body");
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "close";
        step.Scope = "/tasks/outer/body";
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        var schema = PlanningCompactConstruction.DraftSchema(state);
        AssertCompletion(schema, allowed: false);
        AssertScopeClosure(schema, step.Scope, allowed: !openDescendant);
        if (!openDescendant)
        {
            var candidate = PlanningConstruction.Apply(state, new() { Scope = step.Scope, CloseScopes = [step.Scope], RequirementIds = ["req"] });
            PlanningCompactConstruction.ValidateExtension(state, candidate);
            Assert.Contains("/tasks/sibling/body", candidate.OpenScopes);
        }
    }

    [Fact]
    public void TheHostAlsoRejectsPrematureCompletionOutsideStructuredOutput()
    {
        var state = State();
        state.Construction!.Requirements.Add(new() { Id = "later" });
        state.Requirements!.Outcomes.Add(new("later", "Another original obligation."));
        var candidate = PlanningConstruction.Apply(state, Leaf(complete: true));
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Contains(PlanningCompactConstruction.CandidateFindings(WithCandidate(state, candidate)),
            d => d.Code == "CONSTRUCTION_REQUIREMENT_REMAINING" && d.Location == "/requirements/later");
        Assert.Null(state.Plan);
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public void ExplicitTypedRevisionRetainsItsExistingAuthority()
    {
        var state = State();
        state.Construction!.Requirements.Add(new() { Id = "later" });
        var candidate = PlanningConstruction.Apply(state, Leaf(complete: true));
        candidate.AuthorizedPaths = ["/tasks/value/outputs/message"];
        PlanningCompactConstruction.ValidateExtension(state, candidate);
    }

    [Fact]
    public async Task RetainedIssuedRootClosureAndFinalIndependentGroupStillRecover()
    {
        var state = State();
        state.Plan = new() { Groups = [new() { Id = "reusable", Body = new() }] };
        state.Construction!.OpenScopes.Add("/groups/reusable/body");
        var step = state.Construction.Compact!.CurrentStep!;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        step.CloseRequirementIds.Clear();
        var currentSchema = PlanningCompactConstruction.DraftSchema(state);
        AssertScopeClosure(currentSchema, "/root", allowed: false);
        var issued = currentSchema.DeepClone().AsObject();
        var properties = ExtensionProperties(issued);
        properties["complete"] = PlanningSchemas.Type("boolean");
        properties["closeScopes"] = PlanningSchemas.Array(PlanningSchemas.Enum("/root"), 0, 1);
        issued["title"] = PlanningConstruction.DraftPurpose;
        var extension = Leaf(complete: false);
        extension.CloseScopes = ["/root"];
        var response = Response(state, extension, issued);
        var request = new LLMRequest { ClientRequestId = "retained:1:" + new string('a', 64), Prompt = "Exact issued presentation.", StructuredOutputSchema = issued, MaxTokens = 8192 };
        state.ModelCalls = 1;
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningConstruction.DraftPurpose, Request = request };
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = response } };
        var receipt = await PlanningModelCalls.CallAsync(state, runtime, PlanningConstruction.DraftPurpose,
            "New context must not replace this pending request.", currentSchema, PlannerFixture.Ct);
        Assert.True(JsonNode.DeepEquals(response, receipt));
        Assert.True(JsonNode.DeepEquals(issued, Assert.Single(runtime.Calls).StructuredOutputSchema));
        Assert.Equal(1, state.ModelCalls);
        Assert.Null(state.PendingCall);
        var candidate = PlanningConstruction.Apply(state, extension);
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(PlanningCompactConstruction.CandidateFindings(WithCandidate(state, candidate)));
        Assert.Equal(["/groups/reusable/body"], candidate.OpenScopes);
        state.Plan = candidate.Plan;
        state.Construction.OpenScopes = candidate.OpenScopes;
        step.Scope = "/groups/reusable/body";
        step.Kind = "close";
        step.CloseRequirementIds = ["req"];
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        var finalSchema = PlanningCompactConstruction.DraftSchema(state);
        AssertCompletion(finalSchema, allowed: true);
        AssertScopeClosure(finalSchema, step.Scope, allowed: true);
        var final = PlanningConstruction.Apply(state, new() { Scope = step.Scope, RequirementIds = ["req"], CloseScopes = [step.Scope], Complete = true });
        PlanningCompactConstruction.ValidateExtension(state, final);
        Assert.Empty(PlanningCompactConstruction.CandidateFindings(WithCandidate(state, final)));
        Assert.Empty(final.OpenScopes);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    public void IntentArrayBoundsRetainUnboundedNullAndExplicitNumericValues(int? minimum, int? maximum)
    {
        var source = new PlanningConstructionSource { Id = "request:0", Text = "Accept the declared array interface." };
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.IndexInstructions,
            new() { ["sources"] = new JsonArray(new JsonObject { ["id"] = source.Id, ["text"] = source.Text }), ["diagnostics"] = new JsonArray() },
            PlanningCompactConstruction.IntentSchema(source), PlanningCompactConstruction.IntentPurpose);
        var schema = request.StructuredOutputSchema!.AsObject();
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var arrayType = schema["$defs"]!["type"]!["anyOf"]!.AsArray().OfType<JsonObject>()
            .Single(item => item["properties"]?["kind"]?["enum"]?[0]?.ToString() == "array");
        foreach (var name in new[] { "minItems", "maxItems" })
            Assert.Contains("null", arrayType["properties"]![name]!["description"]!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("empty", arrayType["properties"]!["maxItems"]!["description"]!.ToString(), StringComparison.OrdinalIgnoreCase);
        var response = JsonNode.Parse("""
            {"sourceId":"request:0","clauses":[{"text":"Accept the declared array interface.","kind":"requirement"}],
             "inputs":[{"name":"values","required":true,"type":{"kind":"array","nullable":false,
             "items":{"kind":"number","nullable":false},"minItems":null,"maxItems":null}}],"outputs":[]}
            """)!.AsObject();
        response["inputs"]![0]!["type"]!["minItems"] = minimum;
        response["inputs"]![0]!["type"]!["maxItems"] = maximum;
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, schema));
        var input = response["inputs"]!.Deserialize(PlanningJsonContext.Default.ListTaskInput)!.Single();
        Assert.Equal(minimum, input.Type.MinItems);
        Assert.Equal(maximum, input.Type.MaxItems);
        var roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(input, PlanningJsonContext.Default.TaskInput), PlanningJsonContext.Default.TaskInput)!;
        Assert.Equal(minimum, roundTrip.Type.MinItems);
        Assert.Equal(maximum, roundTrip.Type.MaxItems);
        var complete = PlanningGenerationPolicy.Apply(new() { Prompt = request.Prompt, StructuredOutputSchema = schema,
            StructuredOutputStrict = true, UseBackgroundMode = true, MaxTokens = 8192,
            ClientRequestId = "array-test:1:" + new string('a', 64) }, new("medium", 32000, 8192));
        Assert.InRange(PlanningRequestMeasurements.Measure(complete, PlanningCompactConstruction.IntentPurpose, 3000).EstimatedInputTokens, 1, 3000);
    }

    private static PlanningSession WithCandidate(PlanningSession state, PlanningConstructionCandidate candidate)
    { state.Construction!.Candidate = candidate; return state; }

    private static void AssertCompletion(JsonObject schema, bool allowed)
    {
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var completion = ExtensionPropertyWithDefinitions(schema, "complete");
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(JsonValue.Create(false), completion));
        Assert.Equal(allowed, PlanningContractValidation.ValidateInstanceFindings(JsonValue.Create(true), completion).Count == 0);
    }

    private static void AssertScopeClosure(JsonObject schema, string? scope, bool allowed)
    {
        var closures = ExtensionPropertyWithDefinitions(schema, "closeScopes");
        var value = scope is null ? new JsonArray() : new JsonArray(scope);
        Assert.Equal(allowed, PlanningContractValidation.ValidateInstanceFindings(value, closures).Count == 0);
    }

    private static JsonObject ExtensionProperties(JsonObject schema) => schema["properties"]!["extension"]!["anyOf"]!
        .AsArray().OfType<JsonObject>().Single(value => value["properties"] is not null)["properties"]!.AsObject();

    private static JsonObject ExtensionPropertyWithDefinitions(JsonObject schema, string name)
    {
        var property = ExtensionProperties(schema)[name]!.DeepClone().AsObject();
        property["$defs"] = schema["$defs"]!.DeepClone();
        return property;
    }

    private static JsonNode Response(PlanningSession state, PlanningConstructionExtension extension, JsonObject schema) => PlanningCorpus.Transport(new JsonObject
    {
        ["baseHash"] = state.Construction!.Compact!.CurrentStep!.BaseHash,
        ["requirements"] = null, ["commitments"] = null,
        ["extension"] = JsonSerializer.SerializeToNode(extension, PlanningJsonContext.Default.PlanningConstructionExtension),
        ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
        ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
    }, schema, schema)!;

    private static PlanningConstructionExtension Leaf(bool complete) => new()
    {
        Tasks = [new() { Id = "value", Kind = "value", Objective = "Produce the requested message.", Outputs = [new("message", new() { Kind = "string", Text = "Hello" })] }],
        Outputs = [new("message", new() { Kind = "output", Source = "value", Port = "message" })], RequirementIds = ["req"],
        CloseScopes = complete ? ["/root"] : [], Complete = complete
    };

    private static PlanTask Container(string id) => new() { Id = id, Kind = "sequence", Objective = "Preserve the selected scope boundary.", Body = new() };

    private static PlanningSession State()
    {
        var state = new PlanningSession
        {
            IntentVersion = 3,
            Request = new() { TenantId = "closure-test", Prompt = "Return the requested message.", Generation = new("medium", 32000, 8192) },
            Catalog = new(),
            Requirements = new() { Summary = "Return the requested message.", Inputs = [],
                Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }], Outcomes = [new("req", "Return the requested message.")] },
            Construction = new() { Version = 2, Requirements = [new() { Id = "req" }], Compact = new()
            {
                Sources = [new() { Id = "request:0", Kind = "page", Text = "Return the requested message.", Indexed = true }],
                CurrentStep = new() { Id = "selected", Scope = "/root", Kind = "value", RequirementIds = ["req"], CloseRequirementIds = ["req"], OutputNames = ["message"] }
            } }
        };
        state.Construction.Compact.CurrentStep.BaseHash = PlanningConstruction.Hash(state.Plan);
        return state;
    }
}
