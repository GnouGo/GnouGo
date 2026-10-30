using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessProjectionApplicableInputsTests
{
    [Theory]
    [InlineData("requirement")]
    [InlineData("global")]
    [InlineData("both")]
    public async Task ApplicablePublicDeclarationRemainsVisibleWithoutConsumptionOrCompletion(string ownership)
    {
        var state = await Ready(ownership);
        var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        var input = Assert.Single(context["inputs"]!.AsArray())!;
        Assert.Equal("query", input["name"]!.ToString());
        Assert.True(input["required"]!.GetValue<bool>());
        Assert.Equal("string", input["type"]!["kind"]!.ToString());
        Assert.False(input["type"]!["nullable"]!.GetValue<bool>());
        Assert.False(input.AsObject().ContainsKey("default"));
        Assert.Equal("/root", input["scope"]!.ToString());
        Assert.Empty(context["claims"]!["completedRequirements"]!.AsArray());
        var action = Assert.Single(context["actions"]!.AsArray())!;
        Assert.False(action.AsObject().ContainsKey("arguments"));
        Assert.Empty(context["publicOutputs"]!.AsArray());
        Assert.DoesNotContain("unrelated", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('z', 100), request.Prompt, StringComparison.Ordinal);
        var finding = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "needs_change",
            ["reason"] = "The page read has no URL argument bound to the declared query.",
            ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray("read_page") } };
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(finding, request.StructuredOutputSchema!));
    }

    [Fact]
    public async Task ApplicableDeclarationKeepsActualAccessedFieldsSeparateFromItsCompleteType()
    {
        var state = await Ready("requirement");
        var candidate = state.Construction!.Candidate!;
        candidate.Plan.Inputs[0].Type = new() { Kind = "object", Fields =
        [new() { Name = "term", Required = true, Type = new() { Kind = "string" } },
         new() { Name = "locale", Required = false, Type = new() { Kind = "string", Nullable = true }, Default = new() { Kind = "null" } }] };
        candidate.Plan.Root.Tasks[0].Inputs = [new("url", new() { Kind = "field", Port = "term", Items = [new() { Kind = "input", Source = "query" }] })];
        var input = Assert.Single(PlanningBusinessProjection.Build(state, global: false)["inputs"]!.AsArray())!;
        Assert.Equal(new[] { "term", "locale" }, input["type"]!["fields"]!.AsArray().Select(field => field!["name"]!.ToString()));
        Assert.Equal("term", Assert.Single(Assert.Single(input["accessedFields"]!.AsArray())!["path"]!.AsArray())!.ToString());
        Assert.Equal("null", input["type"]!["fields"]![1]!["default"]!["kind"]!.ToString());
    }

    [Fact]
    public async Task ApplicablePublicDeclarationDoesNotGrantIsolatedGroupAccess()
    {
        var state = await Ready("global");
        var candidate = state.Construction!.Candidate!;
        var read = candidate.Plan.Root.Tasks.Single();
        candidate.Plan.Root.Tasks.Clear();
        candidate.Plan.Groups.Add(new() { Id = "process", Inputs = [new() { Name = "argument", Type = new() { Kind = "number" } }],
            Body = new() { Tasks = [read] } });
        candidate.Extension.Scope = "/groups/process/body";
        state.Construction.Compact!.CurrentStep!.Scope = candidate.Extension.Scope;
        var projection = PlanningBusinessProjection.Build(state, global: false);
        var input = Assert.Single(projection["inputs"]!.AsArray())!;
        Assert.Equal("query", input["name"]!.ToString());
        Assert.False(input["availableInReviewedScope"]!.GetValue<bool>());
        Assert.Equal("argument", Assert.Single(Assert.Single(projection["groups"]!.AsArray())!["inputs"]!.AsArray())!["name"]!.ToString());
        read.Inputs = [new("url", new() { Kind = "input", Source = "query" })];
        Assert.NotEmpty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("stale")]
    [InlineData("unindexed")]
    public async Task InapplicableOrUntrustedSourceDoesNotExposeAnUnconsumedInput(string reason)
    {
        var state = await Ready("requirement");
        var compact = state.Construction!.Compact!;
        var source = compact.Sources.Single(source => source.Id == "request:0:clause:0");
        if (reason == "unrelated")
        {
            source.Inputs = [];
            compact.Sources.Single(source => source.Id == "request:1:clause:0").Inputs = state.Requirements!.Inputs;
        }
        else if (reason == "stale") source.Version = "old";
        else source.Indexed = false;
        var projection = PlanningBusinessProjection.Build(state, global: false);
        Assert.Empty(projection["inputs"]!.AsArray());
        Assert.Equal(2, PlanningBusinessProjection.Build(state, global: true)["inputs"]!.AsArray().Count);
    }

    [Fact]
    public async Task MissingActualDeclarationIsNotSynthesizedFromTheOriginalObligation()
    {
        var state = await Ready("both");
        state.Construction!.Candidate!.Plan.Inputs.RemoveAt(0);
        Assert.Empty(PlanningBusinessProjection.Build(state, global: false)["inputs"]!.AsArray());
    }

    private static async Task<PlanningSession> Ready(string ownership)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        const string work = "Use the public query to navigate to the matching results page.";
        const string declaration = "Accept a required text input named query without a default.";
        const string later = "Produce an independent result in a later block.";
        state.Request.Prompt = work + declaration + later;
        var input = new TaskInput { Name = "query", Required = true, Type = new() { Kind = "string" } };
        var compact = state.Construction!.Compact!;
        compact.Sources =
        [new() { Id = "request:0", Kind = "page", Text = work + declaration, Indexed = true, Version = Hash(work + declaration) },
         new() { Id = "request:1", Kind = "page", Text = later, Indexed = true, Version = Hash(later) },
         new() { Id = "request:0:clause:0", Kind = "requirement", Text = work, Indexed = true, Version = Hash(work), Inputs = ownership is "requirement" or "both" ? [input] : [] },
         new() { Id = "request:0:clause:1", Kind = "global", Text = declaration, Indexed = true, Version = Hash(declaration), Inputs = ownership is "global" or "both" ? [input] : [] },
         new() { Id = "request:1:clause:0", Kind = "requirement", Text = later, Indexed = true, Version = Hash(later), Inputs = [] }];
        compact.RequirementSources["work"] = ["request:0:clause:0"];
        compact.RequirementSources["later"] = ["request:1:clause:0"];
        compact.GlobalSourceIds = ["request:0:clause:1"];
        state.Requirements!.Outcomes = [new("work", work), new("later", later)];
        state.Requirements.Inputs = [input];
        state.Plan = new() { Inputs = [input, new() { Name = "unrelated", Type = new() { Kind = "string", Enum = [new string('z', 40_000)] } }] };
        var capability = new PlanningCapability { Id = "read_page", Version = "original", StepType = "mcp.call", Kind = "tool", Server = "browser", Method = "read", EffectKind = "read",
            Description = "Read the supplied URL, or the current page when no URL is supplied.",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"url":{"type":"string"}},"additionalProperties":false}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""")!.AsObject() };
        state.Catalog!.Capabilities.Add(capability);
        state.Catalog.AllowedStepTypes.Add("mcp.call");
        compact.CurrentStep = new() { Id = "read", Scope = "/root", Kind = "operation", BaseHash = PlanningConstruction.Hash(state.Plan),
            RequirementIds = ["work"], OperationIds = [capability.Id], DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"],
            Tasks = [new() { Id = "read_page", Kind = "operation", Operation = capability.Id, Objective = "Read the requested results page." }] });
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(state.Construction.Candidate.Plan, state.Catalog, state.Request,
            state.Construction.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        return state;
    }

    private static string Hash(string value) => PlanningGraphCompiler.Fingerprint(value);
}
