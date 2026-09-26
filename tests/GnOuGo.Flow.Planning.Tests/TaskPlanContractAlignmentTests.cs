using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskPlanContractAlignmentTests
{
    [Fact]
    public void GeneratedSchemaPatternsCompileWithJavaScriptUnicodeSyntax()
    {
        // Retained HTTP 400: the replacement identity pattern ending in \z
        // was rejected as "not a 'regex'" even though .NET and RE2 accept it.
        var schema = PlanningSchemas.Proposal(PlannerFixture.Session());
        var engine = new Jint.Engine().SetValue("schemaJson", schema.ToJsonString());
        Assert.True(engine.Evaluate("""
            function check(node) {
                if (node === null || typeof node !== 'object') return;
                for (const [key, value] of Object.entries(node)) {
                    if (key === 'pattern') new RegExp(value, 'u');
                    else check(value);
                }
            }
            check(JSON.parse(schemaJson));
            true;
            """).AsBoolean());
    }

    [Fact]
    public void GeneratedSchemaPatternsDoNotRequireLookaroundOrBacktracking()
    {
        // Retained HTTP 400: invalid_json_schema at $defs.identities.items.pattern:
        // "Invalid JSON schema: regex lookaround is not supported."
        var schema = PlanningSchemas.Proposal(PlannerFixture.Session());
        var patterns = Patterns(schema).ToArray();
        Assert.NotEmpty(patterns);
        foreach (var pattern in patterns)
            _ = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
        static IEnumerable<string> Patterns(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var field in obj)
                {
                    if (field.Key == "pattern") yield return field.Value!.GetValue<string>();
                    else foreach (var pattern in Patterns(field.Value)) yield return pattern;
                }
            else if (node is JsonArray array)
                foreach (var child in array) foreach (var pattern in Patterns(child)) yield return pattern;
        }
    }

    [Fact]
    public void PortableIdentityPatternAndCompilerPreserveExactLexicalRules()
    {
        var pattern = PlanningSchemas.Proposal(PlannerFixture.Session())["$defs"]!["identities"]!["items"]!["pattern"]!.GetValue<string>();
        var regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
        foreach (var id in new[] { "_", "-", "0", "A", "z", "_a", "_-", "_0", "a__", "A-Z_09" })
        {
            Assert.True(regex.IsMatch(id), id);
            Assert.True(WireIdentityMatches(id), id);
            Assert.True(TaskPlanCompiler.ValidIdentity(id), id);
        }
        foreach (var id in new[] { "", "__", "__reserved", "a\n", "_\n", "a\r\n", "a\r", "a\u2028", "a\u2029", "a\0", "a/b", "é", "a.b", "a b" })
        {
            Assert.False(WireIdentityMatches(id), id);
            Assert.False(TaskPlanCompiler.ValidIdentity(id), id);
        }
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("choice")]
    [InlineData("present")]
    [InlineData("predicate")]
    public void ChoiceAlternativesRejectDynamicValuesEvenInsideLiteralContainers(string kind)
    {
        var choice = Choice();
        var value = JsonNode.Parse(kind == "predicate"
            ? """{"kind":"predicate","predicate":"not","items":[{"kind":"boolean","boolean":true}]}"""
            : kind == "output" ? """{"kind":"output","source":"task","port":null}"""
            : $$"""{"kind":"{{kind}}","source":"task"}""")!;
        choice["alternatives"]![0]!["value"] = new JsonObject { ["kind"] = "object", ["members"] = new JsonArray(new JsonObject
        { ["name"] = "nested", ["value"] = new JsonObject { ["kind"] = "array", ["items"] = new JsonArray(value) } }) };
        Assert.NotEmpty(Errors(choice, "choice"));
    }

    [Fact]
    public void LiteralAlternativesAndOptionalObjectFieldsRemainValid()
    {
        var choice = Choice();
        choice["alternatives"]![0]!["value"] = JsonNode.Parse("""{"kind":"object","members":[{"name":"nested","value":{"kind":"array","items":[{"kind":"null"},{"kind":"number","number":1},{"kind":"boolean","boolean":true}]}}]}""");
        Assert.Empty(Errors(choice, "choice"));
        var type = JsonNode.Parse("""{"kind":"object","nullable":false,"fields":[{"name":"optional","type":{"kind":"string","nullable":false},"required":false,"default":null}]}""")!;
        Assert.Empty(Errors(type, "businessType"));
    }

    [Theory]
    [InlineData("choice_count")]
    [InlineData("question")]
    [InlineData("objective")]
    [InlineData("parallel_count")]
    [InlineData("concurrency_low")]
    [InlineData("concurrency_high")]
    [InlineData("items_low")]
    [InlineData("items_high")]
    [InlineData("array_type")]
    [InlineData("not_arity")]
    [InlineData("binary_arity")]
    public void SchemaRejectsExistingSimpleSemanticViolations(string fault)
    {
        JsonNode value; var definition = "task";
        if (fault is "choice_count" or "question")
        {
            value = Choice(); definition = "choice";
            if (fault == "choice_count") value["alternatives"]!.AsArray().RemoveAt(1); else value["question"] = " \n\t";
        }
        else if (fault == "array_type")
        { definition = "businessType"; value = JsonNode.Parse("""{"kind":"array","nullable":false}""")!; }
        else if (fault is "not_arity" or "binary_arity")
        {
            definition = "value";
            value = JsonNode.Parse("""{"kind":"predicate","predicate":"equal","items":[{"kind":"number","number":1}]}""")!;
            if (fault == "not_arity") { value["predicate"] = "not"; value["items"]!.AsArray().Add(new JsonObject { ["kind"] = "boolean", ["boolean"] = true }); }
        }
        else
        {
            value = JsonNode.Parse("""{"id":"iterate","kind":"foreach","objective":"Collect values","dependsOn":[],"items":{"kind":"array","items":[]},"body":{"tasks":[],"always":[],"outputs":[]},"parallel":false,"maxItems":100,"maxConcurrency":4}""")!;
            if (fault == "objective") value["objective"] = " \n";
            else if (fault == "parallel_count") value = JsonNode.Parse("""{"id":"parallel","kind":"parallel","objective":"Work","dependsOn":[],"branches":[{"tasks":[],"always":[],"outputs":[]}],"maxConcurrency":1}""")!;
            else value[fault.StartsWith("items", StringComparison.Ordinal) ? "maxItems" : "maxConcurrency"] = fault.EndsWith("low", StringComparison.Ordinal) ? 0 : fault == "items_high" ? 10001 : 101;
        }
        Assert.NotEmpty(Errors(value, definition));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10000, 100)]
    public void SchemaAcceptsBoundsAndBothPredicateArities(int items, int concurrency)
    {
        var task = JsonNode.Parse($$"""{"id":"iterate","kind":"foreach","objective":"Collect","dependsOn":[],"items":{"kind":"array","items":[]},"body":{"tasks":[],"always":[],"outputs":[]},"parallel":true,"maxItems":{{items}},"maxConcurrency":{{concurrency}}}""")!;
        Assert.Empty(Errors(task, "task"));
        Assert.Empty(Errors(JsonNode.Parse("""{"kind":"predicate","predicate":"not","items":[{"kind":"boolean","boolean":true}]}""")!, "value"));
        Assert.Empty(Errors(JsonNode.Parse("""{"kind":"predicate","predicate":"equal","items":[{"kind":"number","number":1},{"kind":"number","number":1}]}""")!, "value"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a~b")]
    [InlineData("a.b")]
    [InlineData("a%2fb")]
    [InlineData("a\n")]
    [InlineData("a b")]
    [InlineData("é")]
    [InlineData("__reserved")]
    public void UnsafeIdentitiesFailInWireSchemaCompilerAndRepair(string id)
    {
        foreach (var kind in new[] { "task", "group", "choice" })
        {
            var plan = PlanningCorpus.Decision(); plan.Choices[0].Selected = "formal";
            if (kind == "task") plan.Root.Tasks[0].Id = id;
            else if (kind == "group") plan.Groups.Add(new() { Id = id });
            else plan.Choices[0].Id = id;
            var compiled = new TaskPlanCompiler().Compile(plan, new());
            Assert.Null(compiled.Graph); Assert.Empty(compiled.Sources);
            Assert.Contains(compiled.Diagnostics, d => d.Code == "TASK_IDENTITY_INVALID");
            Assert.Empty(TaskPlanRevisions.Scope(plan, compiled.Diagnostics));
            Assert.NotEmpty(TaskPlanRevisions.Validate(null, plan, []));
            // Exercise the wire regex with JavaScript rather than treating .NET's
            // permissive '$' behavior for a final newline as a provider contract.
            Assert.False(WireIdentityMatches(id));
        }
    }

    [Theory]
    [InlineData("task_group")]
    [InlineData("task_choice")]
    [InlineData("group_choice")]
    [InlineData("nested_task")]
    [InlineData("cleanup_task")]
    [InlineData("group_task")]
    public void DeclarationsShareOneNamespaceAcrossAllScopes(string collision)
    {
        var plan = PlanningCorpus.Decision(); plan.Choices[0].Selected = "formal";
        if (collision == "task_group") plan.Groups.Add(new() { Id = "greet" });
        if (collision == "task_choice") plan.Choices[0].Id = "greet";
        if (collision == "group_choice") plan.Groups.Add(new() { Id = "tone" });
        if (collision == "nested_task") plan.Root.Tasks.Add(new() { Id = "container", Objective = "Nest", Kind = "sequence", Body = PlanningCorpus.Greeting().Root });
        if (collision == "cleanup_task") plan.Root.Always.Add(PlanningCorpus.Greeting().Root.Tasks[0]);
        if (collision == "group_task") plan.Groups.Add(new() { Id = "group", Body = PlanningCorpus.Greeting().Root });
        plan.Inputs.Add(new() { Name = "independent", Required = false });
        var compiled = new TaskPlanCompiler().Compile(plan, new());
        Assert.Null(compiled.Graph); Assert.Empty(compiled.Sources);
        Assert.Contains(compiled.Diagnostics, d => d.Code == "TASK_IDENTITY_INVALID");
        Assert.Contains(compiled.Diagnostics, d => d.Location == "/inputs/independent" && d.Code == "TASK_DEFAULT_REQUIRED");
        Assert.Empty(TaskPlanRevisions.Scope(plan, compiled.Diagnostics));
    }

    [Fact]
    public void SafeCaseSensitiveIdsRepeatedCallsAndLocalAlternativesRemainValid()
    {
        var plan = PlanningCorpus.Decision(); plan.Choices[0].Selected = "formal";
        var second = PlanningCorpus.Decision().Choices[0]; second.Id = "Tone"; second.Selected = "casual"; plan.Choices.Add(second);
        plan.Root.Outputs.Add(new("other", PlanningCorpus.Business("choice", "Tone")));
        plan.Groups.Add(new() { Id = "_Group-9", Body = new() { Tasks = [new() { Id = "9_worker", Kind = "value", Objective = "Return", Outputs = [new("port.with/slash", PlanningCorpus.Number(1))] }] } });
        foreach (var id in new[] { "call1", "call2" }) plan.Root.Tasks.Add(new() { Id = id, Kind = "call", Group = "_Group-9", Objective = "Reuse" });
        Assert.Empty(new TaskPlanCompiler().Compile(plan, new()).Diagnostics);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("present")]
    [InlineData("choice")]
    [InlineData("dependency")]
    [InlineData("group")]
    public void UnsafeIdentityReferencesFailClosed(string kind)
    {
        var plan = PlanningCorpus.Greeting();
        if (kind == "dependency") plan.Root.Tasks[0].DependsOn = ["../bad"];
        else if (kind == "group") plan.Root.Tasks.Add(new() { Id = "call", Kind = "call", Objective = "Call", Group = "../bad" });
        else plan.Root.Outputs.Add(new("bad", PlanningCorpus.Business(kind, "../bad")));
        var compiled = new TaskPlanCompiler().Compile(plan, new());
        Assert.Contains(compiled.Diagnostics, d => d.Code == "TASK_IDENTITY_INVALID");
        Assert.Null(compiled.Graph); Assert.Empty(compiled.Sources);
        var scope = TaskPlanRevisions.Scope(plan, compiled.Diagnostics);
        Assert.Equal(new[] { kind == "dependency" ? "/tasks/greet/dependsOn" : kind == "group" ? "/tasks/call/group" : "/root/outputs/bad" }, scope);
        Assert.NotEmpty(TaskPlanRevisions.Validate(plan, plan, scope));
    }

    [Theory]
    [InlineData("tone")]
    [InlineData("group\n")]
    public async Task RecoveredMalformedRepairPreservesBaselineSelectionsReceiptsAndBudgets(string invalidGroupId)
    {
        var runtime = new TestRuntime { Proposal = new() { Requirements = PlannerFixture.Requirements(), Plan = PlanningCorpus.Decision() } };
        runtime.Proposal.Plan.Root.Outputs.Add(new("bad", PlanningCorpus.Business("output", "absent", "value")));
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(PlannerFixture.Session(), new(), runtime, PlannerFixture.Ct);
        state.Plan!.Choices[0].Selected = "formal";
        var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var receipts = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        runtime.Proposal.Plan.Groups.Add(new() { Id = invalidGroupId });
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_IDENTITY_INVALID");
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(receipts, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Equal(1, runtime.Discoveries);
        Assert.Null(state.ApprovedHash); Assert.Null(state.Yaml);
        Assert.NotEqual(runtime.Calls[0].ClientRequestId, runtime.Calls[1].ClientRequestId);
    }

    [Theory]
    [InlineData("greet")]
    [InlineData("group\n")]
    public async Task RecoveredApprovalRejectsInvalidDeclarationIdentity(string invalidGroupId)
    {
        var state = PlannerFixture.Clone(await PlannerFixture.RunAsync(new TestRuntime()));
        var before = state.ComputeArtifactHash(); state.Plan!.Groups.Add(new() { Id = invalidGroupId });
        Assert.NotEqual(before, state.ComputeArtifactHash());
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    [Fact]
    public void AllDeclarationConflictsAreReportedAndExcludedFromSymbols()
    {
        var plan = PlanningCorpus.Decision(); plan.Choices[0].Id = "greet";
        plan.Groups.Add(new() { Id = "greet" });
        plan.Root.Tasks.Add(new() { Id = null!, Objective = "Malformed saved declaration", Kind = "value" });
        var result = new TaskPlanCompiler().Compile(plan, new());
        Assert.Null(result.Graph); Assert.Empty(result.Sources);
        Assert.Equal(new[] { "/choices/greet/id", "/groups/greet/id", "/tasks/1/id", "/tasks/greet/id" },
            result.Diagnostics.Where(d => d.Code == "TASK_IDENTITY_INVALID").Select(d => d.Location));
        Assert.Empty(new TaskPlanSymbols(plan).Tasks);
    }

    [Fact]
    public async Task RecoveryStopsBeforeSelectingAnAmbiguousChoice()
    {
        var runtime = new TestRuntime { Proposal = new() { Requirements = PlannerFixture.Requirements(), Plan = PlanningCorpus.Decision() } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Clarification, state.Status);
        state.Plan!.Groups.Add(new() { Id = "tone" });
        var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var calls = state.ModelCalls;
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new()
        { Kind = "choose", ExpectedRevision = state.Revision, Selections = new() { ["tone"] = "formal" } }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(calls, state.ModelCalls);
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_IDENTITY_INVALID");
    }

    private static JsonNode Choice() => JsonNode.Parse("""{"id":"decision","question":"Choose a value","type":{"kind":"any","nullable":false},"alternatives":[{"id":"one","description":"First","value":{"kind":"string","text":"one"}},{"id":"two","description":"Second","value":{"kind":"null"}}],"recommended":"one"}""")!;
    private static bool WireIdentityMatches(string id)
    {
        var pattern = PlanningSchemas.Proposal(PlannerFixture.Session())["$defs"]!["identities"]!["items"]!["pattern"]!.GetValue<string>();
        return new Engine().SetValue("pattern", pattern).SetValue("id", id)
            .Evaluate("new RegExp(pattern, 'u').test(id)").AsBoolean();
    }
    private static IReadOnlyList<string> Errors(JsonNode value, string definition)
    {
        var schema = PlanningSchemas.Proposal(PlannerFixture.Session());
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var inner = PlanningSchemas.Ref(definition); inner["$defs"] = schema["$defs"]!.DeepClone();
        return PlanningContractValidation.ValidateInstance(value, inner);
    }
}
