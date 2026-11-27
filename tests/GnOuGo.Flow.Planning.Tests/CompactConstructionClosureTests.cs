using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactConstructionClosureTests
{
    [Fact]
    public void DeferredExportUsesOnlyTheSelectedResolvedContractWithoutChangingTheAcceptedCatalog()
    {
        var state = State();
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "operation";
        state.Catalog!.AllowedStepTypes.Add("mcp.call");
        step.OperationIds = ["deliver"];
        var extension = Leaf(false);
        extension.Tasks = [new() { Id = "deliver_message", Kind = "operation", Operation = "deliver",
            Objective = "Deliver the requested message." }];
        extension.BoundaryTransitions = [new() { Scope = "/root", RequirementIds = ["req"],
            Outputs = [new("message", new() { Kind = "output", Source = "deliver_message", Port = "message" })] }];
        extension.Outputs = [];
        var candidate = PlanningConstruction.Apply(state, extension);
        Assert.Contains(Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.ValidateExtension(state, candidate)).Diagnostics, d => d.Code == "TASK_OPERATION_UNKNOWN");
        state.Discovery.Resolved.Add(new() { Id = "deliver", StepType = "mcp.call",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{}}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""")!.AsObject() });
        PlanningCompactConstruction.ValidateExtension(state, candidate);
        Assert.Empty(state.Catalog!.Capabilities);
        step.OperationIds.Clear();
        Assert.Contains(Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.ValidateExtension(state, candidate)).Diagnostics, d => d.Code == "CONSTRUCTION_STEP_INVALID");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BoundPublicOutputChecksNestedFieldsBeforeFinalReview(bool allowsExtraFields)
    {
        var state = State();
        state.Catalog!.AllowedStepTypes.Add("mcp.call");
        var resultSchema = JsonNode.Parse("""{"type":"object","properties":{"message":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}},"required":["message"]}""")!.AsObject();
        resultSchema["properties"]!["message"]!["additionalProperties"] = allowsExtraFields;
        state.Catalog.Capabilities.Add(new() { Id = "read", StepType = "mcp.call", InputSchema = new() { ["type"] = "object" }, OutputSchema = resultSchema });
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "read", Kind = "operation", Operation = "read", Objective = "Read the message." }],
            Outputs = [new("message", new() { Kind = "output", Source = "read", Port = "message" })] } };
        List<TaskInput> declared = [new() { Name = "message", Type = new() { Kind = "object", Fields = [new() { Name = "text", Required = true, Type = new() { Kind = "string" } }] } },
            new() { Name = "later", Required = true, Type = new() { Kind = "number" } }];
        var findings = TaskPlanCompiler.ValidateConstruction(plan, state.Catalog, state.Request, new HashSet<string> { "/root" }, declaredOutputs: declared).Diagnostics;
        if (allowsExtraFields)
            Assert.Contains(findings, d => d.Code == "REQUIREMENTS_OUTPUTS_CHANGED" && d.Location == "/root/outputs/message/value");
        else Assert.Empty(findings);
        Assert.DoesNotContain(findings, d => d.Location.Contains("later", StringComparison.Ordinal));
    }

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
                construction.Requirements[0].Status = "remaining";
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
    [InlineData("group")]
    [InlineData("sequence")]
    [InlineData("conditional")]
    [InlineData("parallel")]
    [InlineData("foreach")]
    public void AnUnfinishedContainerStillWithholdsItsResultWhenCoherentBodiesAreAllowed(string kind)
    {
        var state = State();
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = kind;
        step.CloseRequirementIds.Clear();
        step.OutputNames.Clear();
        var selection = PlanningCompactConstruction.Request(PlanningCompactConstruction.ImplementationGuidance(state),
            PlanningCompactConstruction.ImplementationContext(state), PlanningCompactConstruction.ImplementationSchema(state),
            PlanningCompactConstruction.SelectPurpose);
        var request = PlanningConstruction.DraftRequest(state, repair: false);
        Assert.Contains("unfinished containers expose no usable result", request.StructuredOutputSchema!["description"]!.ToString(), StringComparison.Ordinal);
        var schema = request.StructuredOutputSchema!.AsObject();
        AssertCompletion(schema, allowed: false);
        AssertScopeClosure(schema, "/root", allowed: false);
        var extension = new PlanningConstructionExtension { RequirementIds = ["req"], CompleteBlock = false };
        extension.ContinueInScope = kind == "group" ? "/groups/reusable/body" : kind == "parallel"
            ? "/tasks/container/branches/0" : "/tasks/container/body";
        if (kind == "group") extension.Groups.Add(new() { Id = "reusable", Body = Body("group_value") });
        else
        {
            var task = new PlanTask { Id = "container", Kind = kind, Objective = "Open the scope for the required work." };
            if (kind == "parallel") task.Branches = [Body("left_value"), Body("right_value")];
            else task.Body = Body("body_value");
            if (kind == "conditional") { task.Otherwise = Body("otherwise_value"); task.Condition = new() { Kind = "boolean", Boolean = true }; }
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
        Assert.NotEqual("closed", Assert.Single(state.Construction!.Requirements).Status);
        Assert.Equal(original, PlanningConstruction.Hash(state.Plan));
        if (kind == "group") candidate.Plan.Root.Tasks.Add(new() { Id = "premature", Kind = "call", Group = "reusable", Objective = "Read unfinished work." });
        else candidate.Plan.Root.Outputs.Add(new("message", new() { Kind = "output", Source = "container", Port = "message" }));
        Assert.Contains(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request, open).Diagnostics,
            diagnostic => diagnostic.Code == "TASK_SCOPE_OPEN");

        static TaskScope Body(string id) => new() { Tasks = [new() { Id = id, Kind = "value", Objective = "Compute the enclosed greeting.",
            Outputs = [new("message", new() { Kind = "string", Text = "Hello" })] }] };
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
        var step = state.Construction!.Compact!.CurrentStep!;
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
        if (openDescendant) state.Construction!.OpenScopes.Add("/tasks/inner/body");
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "close";
        step.Scope = "/tasks/outer/body";
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        var schema = PlanningCompactConstruction.DraftSchema(state);
        AssertCompletion(schema, allowed: false);
        AssertScopeClosure(schema, step.Scope, allowed: !openDescendant);
        if (!openDescendant)
        {
            var candidate = PlanningConstruction.Apply(state, new() { Scope = step.Scope, CloseScopes = [step.Scope], RequirementIds = ["req"], CompleteBlock = false });
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
        Assert.Empty(state.Construction!.Checkpoints);
    }

    [Fact]
    public void SelectedClosureCannotAlsoDeferTheSameBoundaryButRetainsItsParent()
    {
        var state = State();
        var outer = Container("outer");
        outer.Body!.Tasks.Add(Container("inner"));
        state.Plan = new() { Root = new() { Tasks = [outer] } };
        state.Construction!.OpenScopes = ["/root", "/tasks/outer/body", "/tasks/inner/body"];
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = "close";
        step.Scope = "/tasks/inner/body";
        step.CloseScopes = [step.Scope];
        step.CloseRequirementIds.Clear();
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var extension = new PlanningConstructionExtension
        {
            Scope = step.Scope, CloseScopes = [step.Scope], RequirementIds = ["req"], CompleteBlock = false
        };
        var wire = Response(state, extension, schema);
        extension.BoundaryTransitions = [new() { Scope = step.Scope, RequirementIds = ["req"], CloseScope = true }];
        wire["extension"]!["boundaryTransitions"] = JsonSerializer.SerializeToNode(extension.BoundaryTransitions,
            PlanningJsonContext.Default.ListPlanningConstructionBoundaryTransition);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(wire, schema));
        var error = Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.ValidateExtension(state, PlanningConstruction.Apply(state, extension)));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_TRANSITION_INVALID" &&
            diagnostic.Message.Contains(step.Scope, StringComparison.Ordinal) &&
            diagnostic.Message.Contains("extension.outputs", StringComparison.Ordinal));

        extension.BoundaryTransitions[0].Scope = "/tasks/outer/body";
        wire["extension"]!["boundaryTransitions"] = JsonSerializer.SerializeToNode(extension.BoundaryTransitions,
            PlanningJsonContext.Default.ListPlanningConstructionBoundaryTransition);
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(wire, schema));
        PlanningCompactConstruction.ValidateExtension(state, PlanningConstruction.Apply(state, extension));
        Assert.Contains(step.Scope, state.Construction.OpenScopes);
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppendDraftOffersOnlyUnboundPublicOutputs(bool deferred)
    {
        var state = State();
        state.Plan = new() { Root = new() { Tasks = Leaf(false).Tasks, Outputs = Leaf(false).Outputs } };
        state.Requirements!.Outputs!.Add(new() { Name = "summary", Type = new() { Kind = "string" } });
        var step = state.Construction!.Compact!.CurrentStep!;
        step.OutputNames = ["message", "summary"];
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var property = ExtensionPropertyWithDefinitions(schema, deferred ? "boundaryTransitions" : "outputs");
        foreach (var name in step.OutputNames)
        {
            var outputs = new JsonArray(new JsonObject { ["name"] = name,
                ["value"] = new JsonObject { ["kind"] = "string", ["text"] = "Ready" } });
            var declaration = deferred ? new JsonArray(new JsonObject { ["scope"] = "/root", ["closeScope"] = false,
                ["requirementIds"] = new JsonArray("req"), ["outputs"] = outputs }) : outputs;
            Assert.Equal(name == "summary", PlanningContractValidation.ValidateInstanceFindings(declaration, property).Count == 0);
        }
        Assert.Equal(step.BaseHash, PlanningConstruction.Hash(state.Plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DraftDescribesCallerDeliveryWhenAPublicReturnIsDeclared(bool declaresPublicReturn)
    {
        var state = State();
        if (!declaresPublicReturn)
        {
            state.Requirements!.Outputs = [];
            state.Construction!.Compact!.CurrentStep!.OutputNames.Clear();
        }
        var draft = PlanningConstruction.DraftRequest(state, false);
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.DraftPurpose,
            draft.Prompt, draft.StructuredOutputSchema!.AsObject());
        Assert.Equal(declaresPublicReturn, issued.Prompt.Contains("returns bound root outputs to the caller", StringComparison.Ordinal));
        if (declaresPublicReturn)
            Assert.Contains("separately requested delivery", issued.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PlanningConstruction.DraftPurpose)]
    [InlineData("replan")]
    public void IssuedDraftSeparatesAnOpenScopeContinuationFromADeferredPublicBinding(string purpose)
    {
        var state = State();
        var step = state.Construction!.Compact!.CurrentStep!;
        step.OutputNames.Clear();
        step.CloseRequirementIds.Clear();
        var proposal = PlanningConstruction.DraftRequest(state, false);
        var schema = PlanningModelCalls.PrepareRequest(state, purpose,
            proposal.Prompt, proposal.StructuredOutputSchema!.AsObject()).StructuredOutputSchema!;
        var extension = Leaf(complete: false);
        extension.BoundaryTransitions = [new() { Scope = "/root", RequirementIds = ["req"], Outputs = extension.Outputs }];
        extension.Outputs = [];
        extension.ContinueInScope = "/root";
        var wire = Response(state, extension, proposal.StructuredOutputSchema.AsObject());
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(wire, schema));
        Assert.Contains(Assert.Throws<PlanningResponseException>(() =>
            PlanningCompactConstruction.ValidateExtension(state, PlanningConstruction.Apply(state, extension))).Diagnostics,
            d => d.Code == "CONSTRUCTION_BLOCK_CONTINUATION_INVALID");

        wire["extension"]!["continueInScope"] = null;
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(wire, schema));
        wire["extension"]!["continueInScope"] = "/root";
        wire["extension"]!["boundaryTransitions"] = null;
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(wire, schema));
        wire["extension"]!["boundaryTransitions"] = new JsonArray();
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(wire, schema));
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
    }

    [Fact]
    public void SelectedRootCompletionOffersNoDeferredBoundaryClaim()
    {
        var state = State();
        var leaf = Leaf(complete: true);
        state.Plan = new() { Root = new() { Tasks = leaf.Tasks, Outputs = leaf.Outputs } };
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "close";
        step.CloseScopes = ["/root"];
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        var schema = PlanningCompactConstruction.DraftSchema(state);
        var extension = new PlanningConstructionExtension { Scope = "/root", CloseScopes = ["/root"],
            RequirementIds = ["req"], Complete = true, CompleteBlock = true };
        var wire = Response(state, extension, schema);
        extension.BoundaryTransitions = [new() { Scope = "/root", RequirementIds = ["req"],
            Outputs = [new("message", new() { Kind = "string", Text = "Hello" })] }];
        wire["extension"]!["boundaryTransitions"] = JsonNode.Parse("""
            [{"scope":"/root","requirementIds":["req"],"outputs":[{"name":"message",
              "value":{"kind":"string","text":"Hello"}}],"closeScope":false}]
            """);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(wire, schema));
        extension.BoundaryTransitions = null;
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(Response(state, extension, schema), schema));
        PlanningCompactConstruction.ValidateExtension(state, PlanningConstruction.Apply(state, extension));
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

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    public void IntentArrayBoundsRetainUnboundedNullAndExplicitNumericValues(int? minimum, int? maximum)
    {
        var source = new PlanningConstructionSource { Id = "request:0", Text = "Accept the declared array interface." };
        var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.IntentInstructions,
            new() { ["sources"] = new JsonArray(new JsonObject { ["id"] = source.Id, ["text"] = source.Text }), ["diagnostics"] = new JsonArray() },
            PlanningCompactConstruction.IntentSchema(source), PlanningCompactConstruction.IntentPurpose);
        var schema = request.StructuredOutputSchema!.AsObject();
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var response = JsonNode.Parse("""
            {"sourceId":"request:0",
             "inputs":[{"name":"values","required":true,"type":{"kind":"array","nullable":false,
             "items":{"kind":"number","nullable":false},"minItems":null,"maxItems":null}}],"outputs":[]}
            """)!.AsObject();
        response["inputs"]![0]!["type"]!["minItems"] = minimum;
        response["inputs"]![0]!["type"]!["maxItems"] = maximum;
        response = new JsonObject { ["clauses"] = new JsonArray(new JsonObject { ["text"] = source.Text, ["kind"] = "requirement",
            ["inputs"] = response["inputs"]!.DeepClone(), ["outputs"] = response["outputs"]!.DeepClone() }) };
        response = PlanningCorpus.Transport(response, schema, schema)!.AsObject();
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, schema));
        var input = response["clauses"]![0]!["inputs"]!.Deserialize(PlanningJsonContext.Default.ListTaskInput)!.Single();
        Assert.Equal(minimum, input.Type.MinItems);
        Assert.Equal(maximum, input.Type.MaxItems);
        var roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(input, PlanningJsonContext.Default.TaskInput), PlanningJsonContext.Default.TaskInput)!;
        Assert.Equal(minimum, roundTrip.Type.MinItems);
        Assert.Equal(maximum, roundTrip.Type.MaxItems);
        var complete = PlanningGenerationPolicy.Apply(new() { Prompt = request.Prompt, StructuredOutputSchema = schema,
            StructuredOutputStrict = true, UseBackgroundMode = true, MaxTokens = 8192,
            ClientRequestId = "array-test:1:" + new string('a', 64) }, new("medium", 32000, 8192));
        PlannerFixture.ObserveInputBudget(PlanningRequestMeasurements.Measure(complete, PlanningCompactConstruction.IntentPurpose, 3000));
    }

    private static PlanningSession WithCandidate(PlanningSession state, PlanningConstructionCandidate candidate)
    { state.Construction!.Candidate = candidate; return state; }

    private static void AssertCompletion(JsonObject schema, bool allowed)
    {
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        if (ExtensionProperties(schema)["complete"] is null)
        {
            Assert.False(allowed);
            return; // Omission is the fixed DTO default false, not a model choice.
        }
        var completion = ExtensionPropertyWithDefinitions(schema, "complete");
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(JsonValue.Create(false), completion));
        Assert.Equal(allowed, PlanningContractValidation.ValidateInstanceFindings(JsonValue.Create(true), completion).Count == 0);
    }

    private static void AssertScopeClosure(JsonObject schema, string? scope, bool allowed)
    {
        if (ExtensionProperties(schema)["closeScopes"] is null)
        {
            Assert.Equal(scope is null, allowed);
            return;
        }
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
        CloseScopes = complete ? ["/root"] : [], Complete = complete, CompleteBlock = complete
    };

    private static PlanTask Container(string id) => new() { Id = id, Kind = "sequence", Objective = "Preserve the selected scope boundary.", Body = new() };

    [Theory]
    [InlineData("/tasks/decision/body")]
    [InlineData("/tasks/decision/otherwise")]
    [InlineData("/groups/nested/body")]
    public void EmptyContinuationEntersTheChosenAcceptedBodyOnce(string path)
    {
        var state = PlannerFixture.Clone(ContinuedControl());
        var construction = state.Construction!;
        var original = PlanningConstruction.Hash(state.Plan);
        var checkpoint = JsonSerializer.Serialize(construction.Checkpoints, PlanningJsonContext.Default.Options);
        var proposal = new PlanningConstructionResponse { BaseHash = original,
            Extension = new() { CompleteBlock = false, ContinueInScope = path, RequirementIds = ["req"] } };
        construction.EvidenceRequests.Add("already-read");
        construction.DraftCorrections = 2;

        Assert.True(PlanningCompactConstruction.TryEnterAcceptedChildScope(state, proposal));
        Assert.Equal(path, construction.Compact!.CurrentStep!.Scope);
        Assert.Equal(original, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(checkpoint, JsonSerializer.Serialize(construction.Checkpoints, PlanningJsonContext.Default.Options));
        Assert.Null(construction.Candidate);
        Assert.Equal("remaining", Assert.Single(construction.Requirements).Status);
        Assert.Equal(2, construction.DraftCorrections);
        Assert.Contains("already-read", construction.EvidenceRequests);
        Assert.False(PlanningCompactConstruction.TryEnterAcceptedChildScope(state, proposal));
        proposal.Extension.Scope = path;
        proposal.Extension.ContinueInScope = "/root";
        Assert.False(PlanningCompactConstruction.TryEnterAcceptedChildScope(state, proposal));
    }

    [Fact]
    public void EmptyContinuationCannotClaimWorkOrEnterAnotherCheckpointsControl()
    {
        var state = ContinuedControl();
        var request = new PlanningConstructionResponse { Extension = new() { CompleteBlock = false,
            ContinueInScope = "/tasks/older/body", RequirementIds = ["req"] } };
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.False(PlanningCompactConstruction.TryEnterAcceptedChildScope(state, request));
        request.Extension.ContinueInScope = "/tasks/decision/body";
        request.Extension.CompleteBlock = true;
        Assert.False(PlanningCompactConstruction.TryEnterAcceptedChildScope(state, request));
        request.Extension.CompleteBlock = false;
        request.Extension.RequirementIds = ["elsewhere"];
        Assert.False(PlanningCompactConstruction.TryEnterAcceptedChildScope(state, request));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    private static PlanningSession ContinuedControl()
    {
        var state = State();
        state.Plan = new() { Root = new() { Tasks = [Container("older"), new()
        {
            Id = "decision", Kind = "conditional", Objective = "Use the observed decision.",
            Condition = new() { Kind = "boolean", Boolean = true }, Body = new(), Otherwise = new()
        }] }, Groups = [new() { Id = "nested", Body = new() }] };
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        step.Kind = "conditional";
        step.CloseRequirementIds.Clear();
        step.OutputNames.Clear();
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        construction.OpenScopes = ["/root", "/tasks/older/body", "/tasks/decision/body", "/tasks/decision/otherwise", "/groups/nested/body"];
        construction.Checkpoints.Add(new() { Step = step, PlanHash = step.BaseHash, TaskIds = ["decision"],
            Fragment = new() { ["continueInScope"] = "/root", ["groups"] = new JsonArray("nested") } });
        return state;
    }

    private static PlanningSession State()
    {
        var state = new PlanningSession
        {
            Request = new() { TenantId = "closure-test", Prompt = "Return the requested message.", Generation = new("medium", 32000, 8192) },
            Catalog = new(),
            Requirements = new() { Summary = "Return the requested message.", Inputs = [],
                Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }], Outcomes = [new("req", "Return the requested message.")] },
            Construction = new() { Requirements = [new() { Id = "req" }], Compact = new()
            {
                Sources = [new() { Id = "request:0", Kind = "page", Text = "Return the requested message.", Indexed = true }]
            } }
        };
        PlanningSessionCreation.Capture(state);
        state.Construction!.Compact!.CurrentStep = new() { Id = "selected", Scope = "/root", Kind = "value",
            RequirementIds = ["req"], CloseRequirementIds = ["req"], OutputNames = ["message"] };
        state.Construction.Compact.Blocks = [new() { Id = "block_1", Objective = state.Request.Prompt,
            RequirementIds = ["req"], ExpectedResults = ["The exact requested message."], SuccessCriteria = ["The message is returned unchanged."] }];
        state.Construction.Compact.CurrentBlockId = "block_1";
        state.Construction.Compact.CurrentStep!.BlockId = "block_1";
        state.Construction!.Compact!.CurrentStep!.BaseHash = PlanningConstruction.Hash(state.Plan);
        return state;
    }
}
