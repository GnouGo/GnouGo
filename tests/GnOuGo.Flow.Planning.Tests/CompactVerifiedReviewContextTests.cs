using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactVerifiedReviewContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclarationClosureHasExactSourceLinkedTypesWithoutInventingOperands(bool accepted)
    {
        var state = await State(accepted);
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningCompactConstruction.Context(state, review: true);
        Assert.Empty(context["interfaces"]!.AsArray());
        var facts = context["constructionStage"]!["preflight"]!;
        Assert.Equal("passed", facts["status"]!.ToString());
        Assert.Equal("tentative_candidate", facts["basis"]!.ToString());
        Assert.Equal(1, facts["openScopeCount"]!.GetValue<int>());
        Assert.Equal("declare", Assert.Single(context["constructionStage"]!["closeRequirementIds"]!.AsArray())!.ToString());
        Assert.Null(context["constructionStage"]!["requirementCompletionClaimed"]);
        Assert.False(context["constructionStage"]!["planCompletionClaimed"]!.GetValue<bool>());
        var declarations = context["inputDeclarations"]!["inputs"]!.AsArray();
        Assert.Equal(new[] { "values", "label" }, declarations.Select(entry => entry!["input"]!.ToString()));
        foreach (var entry in declarations)
        {
            var name = entry!["input"]!.ToString();
            var input = state.Construction!.Candidate!.Plan.Inputs.Single(input => input.Name == name);
            Assert.True(entry["present"]!.GetValue<bool>());
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(input, PlanningJsonContext.Default.TaskInput), entry["declaration"]));
            Assert.Equal("source:declarations", Assert.Single(entry["sourceIds"]!.AsArray())!.ToString());
        }
        Assert.Equal("number", declarations[0]!["declaration"]!["type"]!["items"]!["kind"]!.ToString());
        Assert.Equal("null", declarations[1]!["declaration"]!["default"]!["kind"]!.ToString());
        Assert.False(declarations[1]!["declaration"]!["required"]!.GetValue<bool>());
        Assert.DoesNotContain("unused_payload", declarations.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task RelevantGiantDeclarationIsExplicitlyOmittedWithAnExactRetrievalAddress()
    {
        var state = await State(false);
        var original = state.Requirements!.Inputs!.Single(input => input.Name == "label");
        original.Type.Enum = [new string('z', 16000)];
        Stage(state);
        var context = PlanningCompactConstruction.Context(state, review: true);
        var entries = context["inputDeclarations"]!["inputs"]!.AsArray();
        Assert.NotNull(entries[0]!["declaration"]);
        Assert.Null(entries[1]!["declaration"]);
        Assert.True(entries[1]!["declarationOmitted"]!.GetValue<bool>());
        Assert.Equal("input:label", entries[1]!["evidence"]!.ToString());
        Assert.DoesNotContain(new string('z', 100), context.ToJsonString(), StringComparison.Ordinal);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(context["inputDeclarations"]!.ToJsonString()), 1, 5000);
        PlanningConstruction.LoadEvidence(state, ["input:label"]);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(original, PlanningJsonContext.Default.TaskInput),
            state.Construction!.Evidence!["input:label"]));
    }

    [Fact]
    public async Task StaleExtractionSupportCannotProduceAnExactDeclarationClaim()
    {
        var state = await State(false);
        state.Construction!.Compact!.InterfaceExtractions![0].Dispositions![0].Version = "stale";
        var context = PlanningCompactConstruction.Context(state, review: true);
        Assert.Empty(context["inputDeclarations"]!["inputs"]!.AsArray());
        Assert.Equal("page:interfaces:0", context["inputDeclarations"]!["directory"]!.ToString());
        Assert.Equal("passed", context["constructionStage"]!["preflight"]!["status"]!.ToString());
        Assert.Empty(context["interfaces"]!.AsArray());
    }

    [Fact]
    public async Task SplitDeclarationSupportKeepsBoundedNavigationWithoutGuessingAnAssociation()
    {
        var state = await State(false);
        var compact = state.Construction!.Compact!;
        var naming = compact.Sources[0];
        const string typeText = "The caller collection contains numbers. ";
        var type = new PlanningConstructionSource { Id = "types", Kind = "clause", Text = typeText,
            Version = PlanningGraphCompiler.Fingerprint(typeText), Indexed = true };
        var pageText = typeText + naming.Text;
        var page = new PlanningConstructionSource { Id = "original", Kind = "page", Text = pageText,
            Version = PlanningGraphCompiler.Fingerprint(pageText), Indexed = true };
        compact.Sources.AddRange([type, page]);
        compact.RequirementSources["declare"] = [type.Id];
        compact.InterfaceExtractions![0].Dispositions![0].Evidence = new() { [page.Id] = page.Version };
        var context = PlanningCompactConstruction.Context(state, review: true);
        Assert.Empty(context["inputDeclarations"]!["inputs"]!.AsArray());
        Assert.Equal("navigation_only", context["inputDeclarations"]!["basis"]!.ToString());
        Assert.Equal("page:interfaces:0", context["inputDeclarations"]!["directory"]!.ToString());
        Assert.DoesNotContain(new string('x', 100), context.ToJsonString(), StringComparison.Ordinal);
        PlanningConstruction.LoadEvidence(state, ["page:interfaces:0"]);
        Assert.Contains(state.Construction.Evidence!["page:interfaces:0"]!.AsArray(), entry => entry!["evidence"]!.ToString() == "input:values");
        PlanningConstruction.LoadEvidence(state, ["input:values"]);
        Assert.Equal("number", state.Construction.Evidence["input:values"]!["type"]!["items"]!["kind"]!.ToString());
    }

    [Fact]
    public async Task AggregatedGroupAndScopeFrontiersPreserveEachExistingInterfaceCapacity()
    {
        var state = await State(true);
        state.Plan!.Groups.Add(new() { Id = "closed", Body = new() { Outputs = Enumerable.Range(0, 128)
            .Select(index => new TaskOutput("port_" + index, new() { Kind = "number", Number = index })).ToList() } });
        state.Plan.Root.Outputs = [new("accepted", new() { Kind = "number", Number = 1 })];
        var step = state.Construction!.Compact!.CurrentStep!;
        step.Kind = "call"; step.GroupId = "closed"; step.CloseRequirementIds = [];
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { RequirementIds = ["declare"], Tasks =
            [new() { Id = "invoke", Kind = "call", Group = "closed", Objective = "Invoke the closed reusable group." }] });
        var context = PlanningCompactConstruction.Context(state, review: true);
        Assert.Equal(128, context["group"]!["outputs"]!.AsArray().Count);
        Assert.Equal("accepted", Assert.Single(context["scopeInterface"]!["outputs"]!.AsArray())!["name"]!.ToString());
        Assert.Equal("passed", context["constructionStage"]!["preflight"]!["status"]!.ToString());
        Assert.Empty(context["interfaces"]!.AsArray());
    }

    [Fact]
    public async Task IsolatedGroupKeepsItsExactParametersWithoutCallerDeclarations()
    {
        var state = await State(true);
        state.Plan!.Groups.Add(new() { Id = "isolated", Inputs = [new() { Name = "local", Type = new() { Kind = "number" } }] });
        state.Construction!.OpenScopes.Add("/groups/isolated/body");
        state.Construction.Compact!.CurrentStep!.Scope = "/groups/isolated/body";
        Stage(state);
        var context = PlanningCompactConstruction.Context(state, review: true);
        Assert.Null(context["inputDeclarations"]);
        Assert.Empty(context["interfaces"]!.AsArray());
        var local = Assert.Single(context["scopeGroup"]!["inputs"]!.AsArray());
        Assert.Equal("local", local!["name"]!.ToString());
        Assert.Equal("number", local["type"]!["kind"]!.ToString());
        Assert.True(context["scopeGroup"]!["open"]!.GetValue<bool>());
        Assert.Null(context["scopeGroup"]!["outputs"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyDiagnosticListCannotForgeACompilerPreflightFact(bool invalidDeclaration)
    {
        var state = await State(false);
        var candidate = state.Construction!.Candidate!;
        Assert.Empty(candidate.Diagnostics);
        if (invalidDeclaration) candidate.Plan.Inputs[0].Type.Items = null;
        else candidate.Plan.Root.Tasks.Add(new() { Id = "invalid", Kind = "value", Objective = "Read a missing binding.",
            Outputs = [new("result", new() { Kind = "input", Source = "missing" })] });
        Assert.Throws<PlanningResponseException>(() => PlanningCompactConstruction.Context(state, review: true));
        Assert.Null(state.Plan);
        Assert.Empty(state.Construction.Checkpoints);
    }

    [Fact]
    public async Task RevisionThreeRetainsNavigationOnlyDeclarationsAndNoNewProofFields()
    {
        var state = await State(false);
        state.Construction!.Compact!.PresentationVersion = 3;
        var context = PlanningCompactConstruction.Context(state, review: true);
        Assert.Null(context["constructionStage"]!["preflight"]);
        var declarations = context["inputDeclarations"]!.AsObject();
        Assert.Equal(new[] { "scope", "offset", "count", "directory", "inputs", "next" }, declarations.Select(property => property.Key));
        Assert.Equal(3, declarations["inputs"]!.AsArray().Count);
        Assert.All(declarations["inputs"]!.AsArray(), entry => Assert.Equal(new[] { "input", "typeKind", "evidence", "scope" },
            entry!.AsObject().Select(property => property.Key)));
        var original = context.ToJsonString();
        state.Construction.Compact.InterfaceExtractions = null;
        Assert.Equal(original, PlanningCompactConstruction.Context(state, review: true).ToJsonString());
    }

    private static async Task<PlanningSession> State(bool accepted)
    {
        const string text = "Accept a required numeric array named values and an optional nullable label with a null default.";
        var state = new PlanningSession { Request = new() { TenantId = "verified-review", Prompt = text } };
        PlanningSessionCreation.Capture(state);
        state.Construction!.Compact!.PresentationVersion = 4;
        var compact = state.Construction.Compact;
        var source = new PlanningConstructionSource { Id = "source:declarations", Kind = "clause", Text = text,
            Version = PlanningGraphCompiler.Fingerprint(text), Indexed = true, InterfaceKind = "input" };
        compact.Sources = [source];
        compact.RequirementSources["declare"] = [source.Id];
        compact.InterfaceExtractions = [new() { Id = "interfaces", SourceIds = [source.Id], Inputs = true, Complete = true,
            Dispositions = [new() { SourceId = source.Id, Version = source.Version, Kind = "interfaces", Inputs = ["values", "label"],
                Reason = "The original clause explicitly declares the caller types and default.", Evidence = new() { [source.Id] = source.Version } }] }];
        state.Requirements = new() { Inputs = [
            new() { Name = "values", Type = new() { Kind = "array", MinItems = 0, MaxItems = 32, Items = new() { Kind = "number" } } },
            new() { Name = "label", Required = false, Type = new() { Kind = "string", Nullable = true }, Default = new() { Kind = "null" } },
            new() { Name = "unused_payload", Type = new() { Kind = "string", Enum = [new string('x', 16000)] } }
        ], Outputs = [], Outcomes = [new("declare", text)] };
        state.Plan = accepted ? new() { Inputs = state.Requirements.Inputs } : null;
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Construction.Requirements = [new() { Id = "declare" }];
        state.Construction.OpenScopes = ["/root"];
        compact.CurrentStep = new() { Id = "close_declaration", Kind = "close", Scope = "/root",
            Objective = "Confirm the original caller declaration.", RequirementIds = ["declare"], CloseRequirementIds = ["declare"], DiscoveryComplete = true };
        Stage(state);
        return state;
    }

    private static void Stage(PlanningSession state)
    {
        var step = state.Construction!.Compact!.CurrentStep!;
        step.BaseHash = PlanningConstruction.Hash(state.Plan);
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = step.Scope, RequirementIds = ["declare"] });
    }
}
