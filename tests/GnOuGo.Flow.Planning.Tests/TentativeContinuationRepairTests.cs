using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TentativeContinuationRepairTests
{
    private const string RowsPath = "/tasks/write_table/inputs/rows";

    [Fact]
    public async Task InvalidContinuedMatrixRetainsCandidateAndIssuesOnlyOwnedTypedRepair()
    {
        var (state, runtime) = await Ready();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var checkpoint = Checkpoint(state);
        runtime.Respond = (request, _) => Draft(request, Extension());
        state = await Advance(state, runtime);

        var candidate = Assert.IsType<PlanningConstructionCandidate>(state.Construction!.Candidate);
        var finding = Assert.Single(candidate.Diagnostics);
        Assert.Equal("TASK_INPUT_TYPE", finding.Code);
        Assert.Equal(RowsPath, finding.Location);
        Assert.Equal(accepted, candidate.BaseHash);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(checkpoint, Checkpoint(state));
        Assert.NotNull(PlanningConstruction.TechnicalRejectionContext(state));
        Assert.False(candidate.BusinessReviewed);
        var inspected = TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<TaskValue>> { ["/root"] = state.Construction.Compact!.CurrentStep!.Inputs });
        Assert.Empty(inspected.Interfaces);

        state = PlannerFixture.Clone(state);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
            var context = PlanningRepairPatch.RequestContext(request);
            Assert.Equal(candidate.Hash, context["candidateHash"]!.ToString());
            Assert.NotNull(context["draft"]);
            var slots = context["repair"]!["slots"]!.AsArray();
            Assert.All(slots, slot => Assert.Equal(RowsPath, slot!["location"]!.ToString()));
            return Patch(request, slots, Matrix());
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(checkpoint, Checkpoint(state));
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(1, state.Construction!.Candidate!.Corrections);
        Assert.False(state.Construction.Candidate.BusinessReviewed);
        Assert.Null(state.ApprovedHash);
        Assert.Equal(2, runtime.Calls.Count);
        await VerifyMatrixExecution(state);
    }

    [Fact]
    public async Task ReceivedTypedRepairRestoresExactRequestAndCandidateWithoutAnotherDispatch()
    {
        var (state, runtime) = await Diagnosed();
        var checkpoint = Checkpoint(state);
        var candidate = JsonSerializer.Serialize(state.Construction!.Candidate, PlanningJsonContext.Default.PlanningConstructionCandidate);
        var dispatches = 0;
        string? receipt = null;
        runtime.Respond = (request, _) =>
        {
            dispatches++;
            var response = Patch(request, PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray(), Matrix());
            receipt = JsonSerializer.Serialize(response, PlanningJsonContext.Default.LLMResponse);
            return response;
        };
        var completed = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(point => point.PendingCall is not null));
        Assert.Equal(candidate, JsonSerializer.Serialize(saved.Construction!.Candidate, PlanningJsonContext.Default.PlanningConstructionCandidate));
        var exact = JsonSerializer.Serialize(saved.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var coldRuntime = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return JsonSerializer.Deserialize(receipt!, PlanningJsonContext.Default.LLMResponse)!;
        } };
        var recovered = await Advance(saved, coldRuntime);
        Assert.Empty(recovered.Diagnostics);
        Assert.Equal(1, dispatches);
        Assert.Equal(completed.ModelCalls, recovered.ModelCalls);
        Assert.Equal(completed.ReplanAttempts, recovered.ReplanAttempts);
        Assert.Equal(completed.Construction!.Candidate!.Corrections, recovered.Construction!.Candidate!.Corrections);
        Assert.Equal(completed.Construction.Candidate.Hash, recovered.Construction.Candidate.Hash);
        Assert.Equal(checkpoint, Checkpoint(recovered));
        Assert.Null(recovered.PendingCall);
    }

    [Fact]
    public async Task ExhaustedTypedRepairRetainsUnchangedCandidateAndNeverAppliesPartialContent()
    {
        var (state, runtime) = await Diagnosed();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var candidate = state.Construction!.Candidate!.Hash;
        var checkpoint = Checkpoint(state);
        LLMResponse? receipt = null;
        runtime.Respond = (request, _) =>
        {
            Assert.True(PlanningRepairPatch.Issued(request.StructuredOutputSchema!.AsObject()));
            receipt = Patch(request, PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray(), Matrix());
            receipt.CompletionStatus = "output_limit";
            receipt.Text = "{\"patch\":{\"edits\":[";
            receipt.Usage = new JsonObject { ["input_tokens"] = 123, ["output_tokens"] = request.MaxTokens, ["reasoning_tokens"] = 480 };
            return receipt;
        };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("output_limit", receipt!.CompletionStatus);
        Assert.Equal(candidate, state.Construction!.Candidate!.Hash);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(checkpoint, Checkpoint(state));
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "MODEL_OUTPUT_LIMIT");
        var measurement = state.RequestMeasurements!.Last();
        Assert.Equal("output_limit", measurement.Result);
        Assert.Equal(measurement.IssuedOutputTokens, measurement.OutputTokens);
        Assert.Equal(480, measurement.ReasoningTokens);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(1, state.Construction.Candidate.Corrections);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Null(state.PendingCall);
        var stopped = PlannerFixture.Clone(state);
        Assert.Same(stopped, await Advance(stopped, runtime));
        Assert.Equal(2, runtime.Calls.Count);
    }

    [Fact]
    public async Task HeaderOnlyTypedMatrixStillRequiresBusinessReviewOfAllPreparedRows()
    {
        var (state, runtime) = await Diagnosed();
        runtime.Respond = (request, _) => Patch(request, PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray(), Header());
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); // A matrix of headers satisfies types, not the original obligation.
        state = await Advance(state, runtime); // partial technical checkpoint
        runtime.Respond = (request, _) => Draft(request, new() { Scope = "/root", RequirementIds = ["work"], CompleteBlock = true });
        state = await Advance(state, runtime);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            Assert.Contains("name", request.Prompt, StringComparison.Ordinal);
            return new() { Json = new JsonObject { ["decision"] = new JsonObject
            {
                ["verdict"] = "needs_change", ["reason"] = "The writer uses only the header; none of the prepared rows reach its arguments.",
                ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray("write_table")
            } } };
        };
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW");
        Assert.False(state.Construction!.Candidate!.BusinessReviewed);
        Assert.NotEqual("closed", state.Construction.Requirements.Single().Status);
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("operation")]
    [InlineData("identity")]
    [InlineData("continuation")]
    public async Task InvalidEnvelopeDoesNotReplaceTentativeCommitmentsOrAcceptedWork(string invalid)
    {
        var (state, runtime) = await Ready();
        state.Construction!.Compact!.PendingCommitments["work"] = [];
        var accepted = PlanningConstruction.Hash(state.Plan);
        var checkpoint = Checkpoint(state);
        var commitments = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!["construction"]!["compact"]!["pendingCommitments"]!.ToJsonString();
        var extension = Extension();
        switch (invalid)
        {
            case "scope": extension.Scope = "/tasks/prepared/body"; break;
            case "operation": extension.Tasks[0].Body!.Tasks[1].Operation = "unselected"; break;
            case "identity": extension.Tasks[0].Id = "prepared"; break;
            case "continuation": extension.CompleteBlock = true; break;
        }
        runtime.Respond = (request, _) =>
        {
            var response = Draft(request, extension);
            // The fixture's ordinary transport restores host-owned constants;
            // inject the invalid wire value after that projection.
            if (invalid == "scope") response.Json!["extension"]!["scope"] = extension.Scope;
            return response;
        };
        state = await Advance(state, runtime);
        Assert.NotEmpty(state.Diagnostics);
        if (invalid == "continuation") Assert.Contains(state.Diagnostics, finding => finding.Code == "CONSTRUCTION_BLOCK_INVALID");
        Assert.Null(state.Construction!.Candidate);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(checkpoint, Checkpoint(state));
        Assert.Equal(commitments, JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!["construction"]!["compact"]!["pendingCommitments"]!.ToJsonString());
    }

    [Fact]
    public async Task RepairedConsumerCannotReferenceAnUnavailableProducerOrUseAStaleBaseline()
    {
        var (state, runtime) = await Diagnosed();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var original = state.Construction!.Candidate!.Hash;
        runtime.Respond = (request, _) => Patch(request, PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray(),
            Output("missing", "rows"));
        state = await Advance(state, runtime);
        Assert.NotEmpty(state.Diagnostics);
        Assert.Equal(original, state.Construction!.Candidate!.Hash);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        state.Plan!.Root.Tasks[0].Objective += " Changed baseline.";
        await Assert.ThrowsAsync<PlanningConflictException>(() => Advance(state, runtime));
        Assert.Equal(2, runtime.Calls.Count);
    }

    [Fact]
    public async Task RepairCannotEditAnAcceptedProducerOutsideItsIssuedSlots()
    {
        var (state, runtime) = await Diagnosed();
        var accepted = PlanningConstruction.Hash(state.Plan);
        var original = state.Construction!.Candidate!.Hash;
        runtime.Respond = (request, _) =>
        {
            var response = Patch(request, PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray(), Matrix());
            response.Json!["patch"]!["edits"]![0]!["slot"] = "/tasks/prepared/outputs/rows";
            return response;
        };
        state = await Advance(state, runtime);
        Assert.NotEmpty(state.Diagnostics);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(original, state.Construction!.Candidate!.Hash);
        Assert.Single(state.Construction.Checkpoints);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task NestedTypeDiagnosticDescribesItemsWithoutChangingRepairLocation()
    {
        var (state, _) = await Ready();
        var candidate = PlanningConstruction.Apply(state, Extension());
        var finding = Assert.Single(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.Equal("TASK_INPUT_TYPE", finding.Code);
        Assert.Equal(RowsPath, finding.Location);
        Assert.Contains("items", finding.Message, StringComparison.Ordinal);
        Assert.Contains("string", finding.Message, StringComparison.Ordinal);
        Assert.Contains("null", finding.Message, StringComparison.Ordinal);
        Assert.Contains("anyOf(\"string\")", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiteralItemFindingKeepsItsInstancePointerInsideTheMessage()
    {
        var (state, _) = await Ready();
        var candidate = PlanningConstruction.Apply(state, Extension());
        var writer = new TaskPlanSymbols(candidate.Plan).Tasks["write_table"].Task;
        writer.Inputs[0] = new("rows", Header().Items[0]);
        var finding = Assert.Single(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.Equal("TASK_INPUT_TYPE", finding.Code);
        Assert.Equal(RowsPath, finding.Location);
        Assert.Contains("Instance /0:", finding.Message, StringComparison.Ordinal);
        Assert.Contains("expected array", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepItemSchemasHaveBoundedDiagnosticPresentation()
    {
        var (state, _) = await Ready();
        JsonObject expected = new() { ["type"] = "string" };
        for (var index = 0; index < 20; index++) expected = new() { ["type"] = "array", ["items"] = expected };
        state.Catalog!.Capabilities.Single(capability => capability.Id == "table").InputSchema!["properties"]!["rows"] = expected;
        var candidate = PlanningConstruction.Apply(state, Extension());
        var finding = Assert.Single(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        Assert.Equal(RowsPath, finding.Location);
        Assert.Contains("...", finding.Message, StringComparison.Ordinal);
        Assert.InRange(finding.Message.Length, 1, 1_024);
    }

    private static async Task<(PlanningSession, TestRuntime)> Ready()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        const string obligation = "Write the header and every prepared row to a table, preserving order, duplicates and nullable cells.";
        state.Request.Prompt = obligation;
        state.Requirements = new()
        {
            Inputs = [new() { Name = "rows", Type = new() { Kind = "array", Items = new()
                { Kind = "array", Items = new() { Kind = "string", Nullable = true } } } }],
            Outputs = [], Outcomes = [new("work", obligation)]
        };
        state.Plan = new() { Inputs = state.Requirements.Inputs.ToList() };
        state.Construction!.Requirements = [new() { Id = "work" }];
        var compact = state.Construction.Compact!;
        state.Construction.Candidate = null;
        compact.Blocks = []; compact.CurrentBlockId = null; compact.CurrentStep = null;
        compact.Sources.Clear(); compact.RequirementSources.Clear();
        foreach (var (id, kind) in new[] { ("request:0", "page"), ("request:0:clause:0", "requirement") })
            compact.Sources.Add(new() { Id = id, Kind = kind, Indexed = true, Text = obligation,
                Version = PlanningGraphCompiler.Fingerprint(obligation), Inputs = [], Outputs = [] });
        compact.RequirementSources["work"] = ["request:0:clause:0"];
        PlanningCompactConstruction.SelectBlock(state, new JsonObject
        {
            ["objective"] = obligation, ["requirementIds"] = new JsonArray("work"),
            ["informationNeeds"] = new JsonArray("The complete prepared rows"),
            ["expectedResults"] = new JsonArray("The header followed by all prepared rows"),
            ["dependencies"] = new JsonArray(), ["successCriteria"] = new JsonArray(obligation)
        });
        compact.CurrentStep = new()
        {
            BlockId = compact.CurrentBlockId, Id = "prepare", Scope = "/root", Kind = "value", Objective = "Retain all prepared rows.",
            BaseHash = PlanningConstruction.Hash(state.Plan), RequirementIds = ["work"], DiscoveryComplete = true
        };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new()
        {
            Scope = "/root", RequirementIds = ["work"], CompleteBlock = false, ContinueInScope = "/root",
            Tasks = [new() { Id = "prepared", Kind = "value", Objective = "Retain the complete prepared rows.",
                Outputs = [new("rows", new() { Kind = "input", Source = "rows" })] }]
        });
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Single(state.Construction!.Checkpoints);
        if (!state.Catalog!.AllowedStepTypes.Contains("mcp.call")) state.Catalog.AllowedStepTypes.Add("mcp.call");
        state.Catalog.Capabilities.Add(new()
        {
            Id = "table", Version = "v1", Kind = "tool", StepType = "mcp.call", Server = "table", Method = "write", EffectKind = "write",
            Description = "Write the supplied rows in order, preserving duplicates and nullable cells.",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"rows":{"type":["array","null"],"items":{"type":["array","null"],"items":{"type":["string","null"]}}}},"required":["rows"],"additionalProperties":false}""")!.AsObject(),
            OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false }
        });
        state.Construction.Compact!.CurrentStep = new()
        {
            BlockId = state.Construction.Compact.CurrentBlockId, Id = "write", Scope = "/root", Kind = "sequence",
            Objective = obligation, RequirementIds = ["work"], DiscoveryComplete = true, OperationIds = ["table"],
            Inputs = [Output("prepared", "rows")], BaseHash = PlanningConstruction.Hash(state.Plan)
        };
        _ = PlanningCompactConstruction.LoadContractEvidence(state, "contract:table@v1");
        return (state, runtime);
    }

    private static async Task<(PlanningSession, TestRuntime)> Diagnosed()
    {
        var (state, runtime) = await Ready();
        runtime.Respond = (request, _) => Draft(request, Extension());
        state = await Advance(state, runtime);
        Assert.NotNull(state.Construction!.Candidate);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "TASK_INPUT_TYPE");
        return (state, runtime);
    }

    private static async Task VerifyMatrixExecution(PlanningSession state)
    {
        var calls = 0;
        JsonNode? written = null;
        var capability = state.Catalog!.Capabilities.Single(capability => capability.Id == "table");
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("table", new()
        {
            Tools = [new() { Name = "write", EffectKind = "write", InputSchema = capability.InputSchema, OutputSchema = capability.OutputSchema }],
            ToolHandlers = new() { ["write"] = arguments => { calls++; written = arguments?["rows"]?.DeepClone(); return new() { Content = new JsonObject() }; } }
        });
        var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human(true) };
        var compiled = new TaskPlanCompiler().Compile(state.Construction!.Candidate!.Plan, state.Catalog, state.Request);
        Assert.Empty(compiled.Diagnostics);
        PlanningConfirmationGuards.Apply(compiled.Graph!, state.Catalog);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, state.Catalog);
        var result = await LookupCompilationTests.Execute(yaml, new()
        {
            ["rows"] = JsonNode.Parse("""[["a",null,"10"],["b"," ",null],["a",null,"10"]]""")
        }, engine);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, calls);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[["name","description","price"],["a",null,"10"],["b"," ",null],["a",null,"10"]]"""), written));
    }

    private static PlanningConstructionExtension Extension() => new()
    {
        Scope = "/root", RequirementIds = ["work"], CompleteBlock = false, ContinueInScope = "/root",
        Tasks = [new()
        {
            Id = "assemble", Kind = "sequence", Objective = "Collect each row and write the table.", Body = new()
            {
                Tasks = [new()
                {
                    Id = "collect", Kind = "foreach", Objective = "Retain each source row in order.", MaxItems = 20,
                    Items = Output("prepared", "rows"), Body = new()
                    {
                        Tasks = [new() { Id = "row", Kind = "value", Objective = "Retain this row's exact cells.",
                            Outputs = [new("row", new() { Kind = "item" })] }],
                        Outputs = [new("rows", Output("row", "row"))]
                    }
                }, new()
                {
                    Id = "write_table", Kind = "operation", Operation = "table", Objective = "Write the header followed by the collected rows.",
                    Inputs = [new("rows", new() { Kind = "flatten", Items = [Header()] })]
                }]
            }
        }]
    };

    private static TaskValue Header() => new() { Kind = "array", Items = [new() { Kind = "array", Items =
        [new() { Kind = "string", Text = "name" }, new() { Kind = "string", Text = "description" }, new() { Kind = "string", Text = "price" }] }] };
    private static TaskValue Matrix() => new() { Kind = "flatten", Items = [new() { Kind = "array", Items = [Header(), Output("collect", "rows")] }] };
    private static TaskValue Output(string task, string port) => new() { Kind = "output", Source = task, Port = port };
    private static string Checkpoint(PlanningSession state) => JsonSerializer.SerializeToNode(state,
        PlanningJsonContext.Default.PlanningSession)!["construction"]!["checkpoints"]!.ToJsonString();
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) => BusinessReviewProtocolTests.Advance(state, runtime);
    private static LLMResponse Draft(LLMRequest request, PlanningConstructionExtension extension) => Wire(request,
        new() { ["extension"] = PlanningConstruction.Node(extension, PlanningJsonContext.Default.PlanningConstructionExtension) });
    private static LLMResponse Patch(LLMRequest request, JsonArray slots, TaskValue value) => Wire(request,
        new() { ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
        {
            ["slot"] = slots.Single(slot => slot!["location"]!.ToString() == RowsPath)!["id"]!.DeepClone(),
            ["action"] = "replace", ["value"] = PlanningConstruction.Node(value, PlanningJsonContext.Default.TaskValue)
        }) } });
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
        { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
}
