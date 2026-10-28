using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessProjectionDeclarationsTests
{
    [Fact]
    public async Task DeclarationCompletionPresentsActualPublicInputsWithoutInventingAConsumer()
    {
        var (state, _) = await Ready();
        var (context, _) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        var input = Assert.Single(context["inputs"]!.AsArray())!;
        Assert.Equal("request", input["name"]!.ToString());
        Assert.Equal("string", input["type"]!["kind"]!.ToString());
        Assert.False(input["type"]!["nullable"]!.GetValue<bool>());
        Assert.True(input["required"]!.GetValue<bool>());
        Assert.False(input.AsObject().ContainsKey("default"));
        Assert.Equal("/root", input["scope"]!.ToString());
        Assert.Empty(context["actions"]!.AsArray());
    }

    [Theory]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("required")]
    [InlineData("default")]
    [InlineData("extra")]
    public async Task InterfaceCompletionShowsWrongActualDeclarationsRatherThanTheExpectedInterface(string change)
    {
        var (state, _) = await Ready();
        var candidate = state.Construction!.Candidate!;
        var actual = candidate.Plan.Inputs.Single();
        switch (change)
        {
            case "name": actual.Name = "different"; break;
            case "type": actual.Type.Kind = "number"; break;
            case "required": actual.Required = false; break;
            case "default": actual.Default = new() { Kind = "string", Text = "An unrequested default." }; break;
            case "extra": candidate.Plan.Inputs.Add(new() { Name = "extra", Type = new() { Kind = "number" } }); break;
        }
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        Assert.Equal(candidate.Plan.Inputs.Select(input => input.Name), context["inputs"]!.AsArray().Select(input => input!["name"]!.ToString()));
        var first = context["inputs"]![0]!;
        Assert.Equal(actual.Type.Kind, first["type"]!["kind"]!.ToString());
        Assert.Equal(actual.Required, first["required"]!.GetValue<bool>());
        if (actual.Default is { } value) Assert.Equal(value.Text, first["default"]!["text"]!.ToString());
        Assert.Contains("required text input named request", context["requirements"]![0]!["original"]!.ToString(), StringComparison.Ordinal);
        var finding = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "needs_change", ["reason"] = "The actual public interface differs from its original declaration.",
            ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray("/root") } };
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(finding, request.StructuredOutputSchema!));
    }

    [Fact]
    public async Task LinkedNamingClauseRemainsRelevantWhenItsInterfaceDispositionBelongsToAnotherRequirement()
    {
        var (state, _) = await Ready();
        var compact = state.Construction!.Compact!;
        var disposition = compact.InterfaceExtractions![0].Dispositions![0];
        var other = compact.Sources.Single(source => source.Id == "request:0:clause:1");
        other.InterfaceKind = "input";
        compact.Sources.Single(source => source.Id == disposition.SourceId).InterfaceKind = "none";
        compact.InterfaceExtractions[0].SourceIds = [other.Id];
        disposition.SourceId = other.Id; disposition.Version = other.Version;
        disposition.Evidence[other.Id] = other.Version;
        var projection = PlanningBusinessProjection.Build(state, global: false);
        Assert.Equal("request", Assert.Single(projection["inputs"]!.AsArray())!["name"]!.ToString());
        Assert.Equal(new[] { "work" }, projection["claims"]!["completedRequirements"]!.AsArray().Select(item => item!.ToString()));
    }

    [Fact]
    public async Task UnrelatedInputDispositionDoesNotExpandAnOrdinaryLocalReview()
    {
        var (state, _) = await Ready();
        var compact = state.Construction!.Compact!;
        compact.CurrentStep!.CloseRequirementIds = ["later"];
        compact.CurrentStep.RequirementIds = ["later"];
        state.Construction.Candidate!.Extension.RequirementIds = ["later"];
        Assert.Empty(PlanningBusinessProjection.Build(state, global: false)["inputs"]!.AsArray());
        Assert.Single(PlanningBusinessProjection.Build(state, global: true)["inputs"]!.AsArray());
    }

    [Fact]
    public async Task PublicDeclarationEvidenceDoesNotBecomeAnIsolatedGroupsInput()
    {
        var (state, _) = await Ready();
        const string scope = "/groups/process/body";
        state.Plan!.Groups = [new() { Id = "process", Inputs = [new() { Name = "request", Type = new() { Kind = "number" } }], Body = new() }];
        state.Construction!.OpenScopes.Add(scope);
        var step = state.Construction.Compact!.CurrentStep!;
        step.Scope = scope; step.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = scope, RequirementIds = ["work"] });
        var projection = PlanningBusinessProjection.Build(state, global: false);
        var root = Assert.Single(projection["inputs"]!.AsArray())!;
        Assert.Equal("string", root["type"]!["kind"]!.ToString());
        Assert.Equal("/root", root["scope"]!.ToString());
        Assert.False(root["availableInReviewedScope"]!.GetValue<bool>());
        var parameter = Assert.Single(Assert.Single(projection["groups"]!.AsArray())!["inputs"]!.AsArray())!;
        Assert.Equal("number", parameter["type"]!["kind"]!.ToString());
        var invalid = PlanningConstruction.Clone(state.Construction.Candidate.Plan);
        invalid.Groups[0].Inputs.Clear();
        invalid.Groups[0].Body.Tasks.Add(new() { Id = "invalid", Kind = "value", Objective = "Copy a forbidden root capture.",
            Outputs = [new("value", new() { Kind = "input", Source = "request" })] });
        Assert.NotEmpty(TaskPlanCompiler.ValidateConstruction(invalid, state.Catalog!, state.Request,
            state.Construction.Candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
    }

    [Fact]
    public async Task RelevantRecordDeclarationPreservesNestedDefaultsEnumsAndNullabilityWithoutUnrelatedSchemas()
    {
        var (state, _) = await Ready();
        const string original = "Accept a required record input named request whose rules have a mode and a nullable fallback. ";
        var compact = state.Construction!.Compact!;
        var clause = compact.Sources.Single(source => source.Id == "request:0:clause:0");
        clause.Text = original; clause.Version = Hash(original);
        var disposition = compact.InterfaceExtractions![0].Dispositions![0];
        disposition.Version = clause.Version; disposition.Evidence[clause.Id] = clause.Version;
        state.Requirements!.Outcomes[0] = new("work", original);
        var candidate = state.Construction.Candidate!;
        candidate.Plan.Inputs[0].Type = new() { Kind = "object", Fields = [new() { Name = "rules", Required = true,
            Type = new() { Kind = "array", Items = new() { Kind = "object", Fields =
            [new() { Name = "mode", Required = true, Type = new() { Kind = "string", Enum = ["all", "available"] } },
             new() { Name = "fallback", Required = false, Type = new() { Kind = "string", Nullable = true }, Default = new() { Kind = "null" } }] } } }] };
        candidate.Plan.Inputs.Add(new() { Name = "unrelated", Type = new() { Kind = "string", Enum = [new string('z', 40_000)] } });
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        Assert.Empty(TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics);
        var (context, request) = HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: false);
        var input = context["inputs"]![0]!;
        var rules = Assert.Single(input["type"]!["fields"]!.AsArray())!;
        Assert.Equal("rules", rules["name"]!.ToString());
        Assert.True(rules["required"]!.GetValue<bool>());
        var fields = rules["type"]!["items"]!["fields"]!.AsArray();
        Assert.Equal(new[] { "all", "available" }, fields[0]!["type"]!["enum"]!.AsArray().Select(value => value!.ToString()));
        Assert.True(fields[1]!["type"]!["nullable"]!.GetValue<bool>());
        Assert.False(fields[1]!["required"]!.GetValue<bool>());
        Assert.Equal("null", fields[1]!["default"]!["kind"]!.ToString());
        Assert.Equal("input:request", input["evidence"]!.ToString());
        Assert.Equal("unrelated", context["inputs"]![1]!["name"]!.ToString());
        Assert.DoesNotContain(new string('z', 100), request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("$defs", request.Prompt, StringComparison.Ordinal);
    }

    internal static async Task<(PlanningSession State, TestRuntime Runtime)> Ready(bool numeric = false)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        var declaration = numeric ? "Accept exactly one required number input named request, without a default. "
            : "Accept exactly one required text input named request, without a default. ";
        const string later = "Compute twice the caller number plus one and return the result.";
        var compact = state.Construction!.Compact!;
        state.Request.Prompt = declaration + later;
        compact.Sources = [new() { Id = "request:0", Text = declaration + later, Kind = "page", Indexed = true, InterfaceKind = "none", Version = Hash(declaration + later) },
            new() { Id = "request:0:clause:0", Text = declaration, Kind = "requirement", Indexed = true, InterfaceKind = "input", Version = Hash(declaration) },
            new() { Id = "request:0:clause:1", Text = later, Kind = "requirement", Indexed = true, InterfaceKind = "none", Version = Hash(later) }];
        compact.InterfaceExtractions = [new() { Id = "declaration", Complete = true, Inputs = true, SourceIds = ["request:0:clause:0"],
            Dispositions = [new() { SourceId = "request:0:clause:0", Version = Hash(declaration), Kind = "interfaces", Inputs = ["request"],
                Reason = "The original names one required public input.", Evidence = new(StringComparer.Ordinal) { ["request:0:clause:0"] = Hash(declaration) } }] }];
        state.Requirements!.Outcomes = [new("work", declaration), new("later", later)];
        state.Requirements.Inputs = [new() { Name = "request", Type = new() { Kind = numeric ? "number" : "string" }, Required = true }];
        state.Plan = new() { Inputs = state.Requirements.Inputs.ToList() };
        compact.CurrentStep = new() { Id = "close-interface", Scope = "/root", Kind = "close", Objective = "Confirm the declared public input.",
            RequirementIds = ["work"], CloseRequirementIds = ["work"], CloseScopes = [], BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/root", RequirementIds = ["work"] });
        return (state, runtime);
    }
    private static string Hash(string text) => PlanningGraphCompiler.Fingerprint(text);
    internal static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf("\n{", StringComparison.Ordinal) + 1)..])!.AsObject();
}
