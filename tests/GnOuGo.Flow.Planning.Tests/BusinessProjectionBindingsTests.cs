using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessProjectionBindingsTests
{
    [Fact]
    public async Task LocalReviewSelectsOriginalsFromActualProducersWhileGlobalReviewKeepsAllOriginals()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var candidate = state.Construction!.Candidate!;
        var compact = state.Construction.Compact!;
        const string global = "Never publish internal artifacts or duplicate external effects.";
        compact.Sources.Add(new() { Id = "negative", Kind = "global", Indexed = true, Text = global, Version = "v1" });
        compact.GlobalSourceIds.Add("negative");
        Assert.Equal(["work"], PlanningBusinessProjection.Build(state, global: false)["requirements"]!.AsArray()
            .Select(row => row!["id"]!.ToString()));
        candidate.Plan.Root.Tasks.Insert(0, new() { Id = "prior", Kind = "value", Objective = "Supply the original numeric result.",
            Outputs = [new("result", new() { Kind = "number", Number = 7 })] });
        candidate.Plan.Root.Tasks.Single(task => task.Id == "compute").Inputs = [new("supplied", Output("prior", "result"))];
        state.Construction.Checkpoints.Add(new() { Sequence = 1, TaskIds = ["prior"], RequirementIds = ["later"] });
        var local = PlanningBusinessProjection.Build(state, global: false);
        Assert.Equal(["work", "later"], local["requirements"]!.AsArray().Select(row => row!["id"]!.ToString()));
        Assert.Equal(global, Assert.Single(local["globalConstraints"]!.AsArray())!["text"]!.ToString());
        candidate.Plan.Root.Tasks.Single(task => task.Id == "compute").Inputs.Clear();
        Assert.Equal(["work"], PlanningBusinessProjection.Build(state, global: false)["requirements"]!.AsArray()
            .Select(row => row!["id"]!.ToString()));
        var complete = PlanningBusinessProjection.Build(state, global: true);
        Assert.Equal(["work", "later"], complete["requirements"]!.AsArray().Select(row => row!["id"]!.ToString()));
        Assert.Equal(global, Assert.Single(complete["globalConstraints"]!.AsArray())!["text"]!.ToString());
    }

    [Fact]
    public async Task ConsumingClosedControlRetainsReviewedNestedCallsExportsAndCleanup()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var inputs = Assert.IsType<List<TaskInput>>(state.Requirements!.Inputs);
        state.Plan = new() { Inputs = inputs.ToList(), Groups = [new()
        {
            Id = "shared", Inputs = [new() { Name = "argument", Type = new() { Kind = "number" } }],
            Body = new() { Tasks = [new() { Id = "group_value", Kind = "value", Objective = "Compute the supplied value.",
                Outputs = [new("result", new() { Kind = "arithmetic", Text = "multiply", Items = [new() { Kind = "input", Source = "argument" }, new() { Kind = "number", Number = 2 }] })] }],
                Outputs = [new("result", Output("group_value", "result"))] }
        }], Root = new() { Tasks = [
            new() { Id = "loop", Kind = "foreach", Objective = "Evaluate the collection.", Parallel = true,
                Items = new() { Kind = "array", Items = [new() { Kind = "input", Source = "x" }] },
                Body = new() { Tasks = [new() { Id = "nested", Kind = "sequence", Objective = "Own the reusable call.", Body = new()
                {
                    Tasks = [new() { Id = "invoke", Kind = "call", Group = "shared", Objective = "Invoke the reusable computation.", Inputs = [new("argument", new() { Kind = "item" })] }],
                    Outputs = [new("result", Output("invoke", "result"))],
                    Always = [new() { Id = "cleanup", Kind = "value", Objective = "Record local finalization.", Outputs = [new("value", new() { Kind = "boolean", Boolean = true })] }]
                } }], Outputs = [new("result", Output("nested", "result"))] } },
            new() { Id = "unrelated", Kind = "sequence", Objective = "Future independent work.", Body = new() { Tasks = [new()
                { Id = "future", Kind = "value", Objective = "Keep unrelated work outside this review.", Outputs = [new("value", new() { Kind = "number", Number = 9 })] }] } }] } };
        state.Construction!.OpenScopes = ["/root", "/tasks/unrelated/body"];
        state.Construction.Checkpoints.Add(new() { Sequence = 1, TaskIds = ["invoke"], RequirementIds = ["work"],
            Fragment = new() { ["businessReviewPending"] = false } });
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"],
            Outputs = [new("result", Output("loop", "result"))] });
        var candidate = state.Construction.Candidate;
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        var projection = PlanningBusinessProjection.Build(state, global: false);
        Assert.Equal(new[] { "loop", "nested", "invoke", "cleanup", "group_value" }, projection["actions"]!.AsArray().Select(action => action!["id"]!.ToString()));
        var call = Assert.Single(projection["actions"]!.AsArray(), action => action!["id"]!.ToString() == "invoke")!;
        Assert.Equal("item", call["arguments"]![0]!["value"]!["kind"]!.ToString());
        Assert.Equal("shared", call["group"]!.ToString());
        var body = Assert.Single(projection["scopes"]!.AsArray(), scope => scope!["id"]!.ToString() == "/tasks/nested/body")!;
        Assert.Equal("invoke", Assert.Single(body["tasks"]!.AsArray())!.ToString());
        Assert.Equal("cleanup", Assert.Single(body["cleanup"]!.AsArray())!.ToString());
        Assert.DoesNotContain(projection["actions"]!.AsArray(), action => action!["id"]!.ToString() is "unrelated" or "future");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectScopeExportsRetainTheOriginalMeaningOfConsumedOperationPorts(bool nested)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var capability = Contract(state);
        var candidate = state.Construction!.Candidate!;
        var producer = Operation(capability.Id);
        if (nested)
            candidate.Plan.Root.Tasks.Add(new() { Id = "boundary", Kind = "sequence", Objective = "Expose the observed document.",
                Body = new() { Tasks = [producer], Outputs = [new("document", Output("record", "content"))] } });
        else
        {
            candidate.Plan.Root.Tasks.Add(producer);
            candidate.Plan.Root.Outputs.Add(new("document", Output("record", "content")));
        }
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        var original = JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability);
        var projection = PlanningBusinessProjection.Build(state, global: true);
        var action = Assert.Single(projection["actions"]!.AsArray(), action => action!["id"]!.ToString() == "record")!;
        var port = Assert.Single(action["ports"]!.AsArray(), port => port!["direction"]!.ToString() == "output")!;
        Assert.Equal("content", port["name"]!.ToString());
        Assert.Equal("Complete original document contents; never a document identity.", port["description"]!.ToString());
        Assert.Equal("string", port["type"]!.ToString());
        Assert.Equal("doc/7", action["arguments"]![0]!["value"]!["text"]!.ToString());
        Assert.Equal(original, JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalExportClaimsIncludeTheExactOlderProducerAndItsArguments(bool transition)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var capability = Contract(state);
        state.Plan!.Root.Tasks.Add(Operation(capability.Id));
        var candidate = state.Construction!.Candidate!;
        candidate.Plan.Root.Tasks.Insert(0, Operation(capability.Id));
        if (transition)
            candidate.Extension.BoundaryTransitions = [new() { Scope = "/root", RequirementIds = ["work"],
                Outputs = [new("document", Output("record", "content"))] }];
        else
            candidate.Plan.Root.Outputs.Add(new("document", Output("record", "content")));
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        var projection = PlanningBusinessProjection.Build(state, global: false);
        var action = Assert.Single(projection["actions"]!.AsArray(), action => action!["id"]!.ToString() == "record")!;
        Assert.Equal("Read the requested original document; the identity and contents are distinct text values.", action["meaning"]!.ToString());
        Assert.Equal("doc/7", action["arguments"]![0]!["value"]!["text"]!.ToString());
        Assert.Equal("content", Assert.Single(action["ports"]!.AsArray(), port => port!["direction"]!.ToString() == "output")!["name"]!.ToString());
        Assert.DoesNotContain(projection["fragment"]!.AsArray(), item => item!.ToString() == "record");
        Assert.Equal("read", action["effect"]!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuationReviewExposesExactBusinessNeedsAndDescendantDispositions(bool supersedeNavigation)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var capability = Contract(state);
        var candidate = state.Construction!.Candidate!;
        candidate.Plan.Root.Tasks.Add(Operation(capability.Id));
        candidate.TaskIds.Add("record");
        var compact = state.Construction.Compact!;
        compact.SuspendedSteps = [Consumer("parent", null, "Write the original document contents to the report.", "Complete original document contents, not its identity."),
            Consumer("child", "parent", "Normalize the document contents without losing facts.", "The complete document text to normalize.")];
        candidate.Extension.ContinuationBinding = new() { ContinuationId = "parent", Binding = Output("record", "content"),
            DischargedContinuationIds = supersedeNavigation ? ["child"] : [] };
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        var continuation = context["continuation"]!;
        Assert.Equal("Write the original document contents to the report.", continuation["consumer"]!["objective"]!.ToString());
        Assert.Equal("Complete original document contents, not its identity.", continuation["consumer"]!["prerequisite"]!["need"]!.ToString());
        Assert.Equal("string", continuation["consumer"]!["prerequisite"]!["expectedType"]!["kind"]!.ToString());
        Assert.False(continuation["consumer"]!["prerequisite"]!["expectedType"]!["nullable"]!.GetValue<bool>());
        var child = Assert.Single(continuation["descendants"]!.AsArray())!;
        Assert.Equal("Normalize the document contents without losing facts.", child["consumer"]!["objective"]!.ToString());
        Assert.Equal(supersedeNavigation ? "prerequisite_navigation_superseded" : "business_consumer_retained", child["disposition"]!.ToString());
        Assert.Equal("parent", child["parentId"]!.ToString());
        Assert.Equal("content", continuation["binding"]!["port"]!.ToString());
        Assert.DoesNotContain("DO_NOT_PRESENT_AUTHORITY", request.Prompt, StringComparison.Ordinal);
        foreach (var key in new[] { "dependencyHashes", "sourceVersions", "requestId", "claimsHash", "responseHash" })
            Assert.DoesNotContain('"' + key + '"', request.Prompt, StringComparison.Ordinal);
        var evidence = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "needs_context", ["reason"] = "Inspect the prerequisite original.",
            ["contextRequests"] = new JsonArray("source:request:0:clause:0") } };
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(evidence, request.StructuredOutputSchema!));
        var producer = Assert.Single(context["actions"]!.AsArray(), action => action!["id"]!.ToString() == "record")!;
        Assert.Equal("Complete original document contents; never a document identity.",
            Assert.Single(producer["ports"]!.AsArray(), port => port!["direction"]!.ToString() == "output")!["description"]!.ToString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FreeTextPolicyConstraintsRemainExactAndGroundableWithoutInventedSourceLinks(bool global, bool catalogPolicy)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        const string instruction = "Do not read documents or create external audit records; use the supplied number only.";
        state.Catalog!.Policy = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Request.Policy,
            PlanningJsonContext.Default.PlanningPolicy), PlanningJsonContext.Default.PlanningPolicy)!;
        if (catalogPolicy) state.Catalog.Policy.Instructions = instruction;
        else state.Request.Policy.Instructions = instruction;
        var capability = Contract(state);
        var candidate = state.Construction!.Candidate!;
        candidate.Plan.Root.Tasks.Add(Operation(capability.Id));
        candidate.TaskIds.Add("record");
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global);
        var id = catalogPolicy ? "catalog-policy:instructions" : "policy:instructions";
        var original = Assert.Single(context["globalConstraints"]!.AsArray(), item => item!["id"]!.ToString() == id)!;
        Assert.Equal(instruction, original["text"]!.ToString());
        Assert.Null(original["evidence"]);
        var report = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "needs_change",
            ["reason"] = "The document read violates the exact no-document-read policy.",
            ["obligations"] = new JsonArray(id), ["targets"] = new JsonArray("record") } };
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(report, request.StructuredOutputSchema!));
        Assert.DoesNotContain("source:" + id, request.Prompt, StringComparison.Ordinal);
        var index = context["globalConstraints"]!.AsArray().IndexOf(original);
        var action = context["actions"]!.AsArray().Select((item, position) => (item, position)).Single(item => item.item!["id"]!.ToString() == "record").position;
        Assert.True(HybridWorkflowPlanner.ConstructionBusinessFindingSupported(context, new JsonObject
        { ["evidence"] = new JsonArray("/globalConstraints/" + index, "/actions/" + action) }));
    }

    [Fact]
    public async Task LocalGroupReviewPresentsItsIsolatedDeclarationAndAccessedFields()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var candidate = state.Construction!.Candidate!;
        const string path = "/groups/normalize/body";
        candidate.Plan.Root.Tasks.Clear();
        candidate.Plan.Inputs.Add(new() { Name = "document", Type = new() { Kind = "number" } });
        candidate.Plan.Groups = [new() { Id = "normalize", Inputs = [new() { Name = "document", Type = new() { Kind = "object",
            Fields = [new() { Name = "content", Type = new() { Kind = "string", Nullable = true } },
                new() { Name = "unused", Type = new() { Kind = "string", Enum = [new string('z', 40_000)] } }] } }],
            Body = new() { Tasks = [new() { Id = "normalize_content", Kind = "value", Objective = "Return the supplied document contents.",
                Outputs = [new("content", new() { Kind = "field", Port = "content", Items = [new() { Kind = "input", Source = "document" }] })] }],
                Outputs = [new("content", Output("normalize_content", "content"))] } }];
        candidate.TaskIds = ["normalize_content"];
        candidate.Extension.Scope = path;
        candidate.Extension.CloseScopes = [path];
        candidate.Extension.BoundaryTransitions = [new() { Scope = path, RequirementIds = ["work"],
            Outputs = [new("original", new() { Kind = "input", Source = "document" })] }];
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        var projection = PlanningBusinessProjection.Build(state, global: false);
        Assert.Empty(projection["inputs"]!.AsArray());
        Assert.DoesNotContain(projection["scopes"]!.AsArray(), scope => scope!["id"]!.ToString() == "/root");
        var group = Assert.Single(projection["groups"]!.AsArray())!;
        Assert.Equal("normalize", group["id"]!.ToString());
        var input = Assert.Single(group["inputs"]!.AsArray())!;
        Assert.Equal("document", input["name"]!.ToString());
        Assert.Equal("object", input["type"]!["kind"]!.ToString());
        var field = Assert.Single(input["accessedFields"]!.AsArray())!;
        Assert.Equal("content", field["path"]![0]!.ToString());
        Assert.Equal("string", field["type"]!["kind"]!.ToString());
        Assert.True(field["type"]!["nullable"]!.GetValue<bool>());
        Assert.Empty(projection["publicOutputs"]!.AsArray());
        Assert.DoesNotContain(new string('z', 100), projection.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransformReviewRetainsItsNestedExecutableResultShapeWithoutUnrelatedInputSchema()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var candidate = state.Construction!.Candidate!;
        candidate.Plan.Inputs.Add(new() { Name = "observation", Type = new() { Kind = "string" } });
        candidate.Plan.Inputs.Add(new() { Name = "unused", Type = new() { Kind = "string", Enum = [new string('z', 40_000)] } });
        candidate.Plan.Root.Tasks = [new() { Id = "extract", Kind = "transform", Mode = "extract",
            Objective = "Extract complete observed document contents separately from document identities; preserve missing contents as null.",
            Inputs = [new("data", new() { Kind = "input", Source = "observation" })],
            ResultType = new() { Kind = "object", Fields = [new() { Name = "documents", Type = new() { Kind = "array", Items = new() { Kind = "object", Fields =
            [new() { Name = "identity", Type = new() { Kind = "string" } }, new() { Name = "content", Type = new() { Kind = "string", Nullable = true } }] } } }] } }];
        candidate.TaskIds = ["extract"];
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        var projection = PlanningBusinessProjection.Build(state, global: false);
        var action = Assert.Single(projection["actions"]!.AsArray())!;
        Assert.Equal("extract", action["mode"]!.ToString());
        Assert.Equal(candidate.Plan.Root.Tasks[0].Objective, action["instruction"]!.ToString());
        var documents = Assert.Single(action["result"]!["fields"]!.AsArray())!;
        Assert.Equal("documents", documents["name"]!.ToString());
        Assert.True(documents["required"]!.GetValue<bool>());
        Assert.Equal("array", documents["type"]!["kind"]!.ToString());
        var fields = documents["type"]!["items"]!["fields"]!.AsArray();
        Assert.Equal(new[] { "identity", "content" }, fields.Select(field => field!["name"]!.ToString()));
        Assert.False(fields[0]!["type"]!["nullable"]!.GetValue<bool>());
        Assert.True(fields[1]!["type"]!["nullable"]!.GetValue<bool>());
        Assert.All(fields, field => Assert.True(field!["required"]!.GetValue<bool>()));
        Assert.DoesNotContain(new string('z', 100), projection.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("observation", Assert.Single(projection["inputs"]!.AsArray())!["name"]!.ToString());
        Assert.DoesNotContain("$defs", projection.ToJsonString(), StringComparison.Ordinal);
    }

    private static PlanningConstructionSuspendedStep Consumer(string id, string? parent, string objective, string need) => new()
    {
        Id = id, ParentId = parent, Status = "waiting", Consumer = new() { Id = id + "_step", Scope = "/root", Objective = objective,
            RequirementIds = ["work"], ExpectedOutputs = ["An exact document contribution."] },
        Prerequisite = new() { Need = need, ExpectedType = new() { Kind = "string" }, EvidenceAddresses = ["source:request:0:clause:0"] },
        DependencyHashes = new() { ["DO_NOT_PRESENT_AUTHORITY"] = new string('b', 64) },
        SourceVersions = new() { ["DO_NOT_PRESENT_AUTHORITY"] = new string('c', 64) }
    };
    private static TaskValue Output(string source, string port) => new() { Kind = "output", Source = source, Port = port };
    private static PlanTask Operation(string operation) => new() { Id = "record", Kind = "operation", Operation = operation,
        Objective = "Read the original contents.", Inputs = [new("document", new() { Kind = "string", Text = "doc/7" })] };
    private static PlanningCapability Contract(PlanningSession state)
    {
        var capability = new PlanningCapability
        {
            Id = "read_document", Version = "original", StepType = "mcp.call", Kind = "tool", Server = "documents", Method = "read", EffectKind = "read",
            Description = "Read the requested original document; the identity and contents are distinct text values.",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"document":{"type":"string","description":"Exact document identity."}},"required":["document"],"additionalProperties":false}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"identity":{"type":"string","description":"Document identity, never its contents."},"content":{"type":"string","description":"Complete original document contents; never a document identity."}},"required":["identity","content"],"additionalProperties":false}""")!.AsObject()
        };
        state.Catalog!.Capabilities.Add(capability);
        state.Catalog.AllowedStepTypes.Add("mcp.call");
        return capability;
    }
}
