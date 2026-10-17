using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class GenericPlanningPromptTests
{
    [Fact]
    public async Task UnrelatedRequestsShareGuidanceButKeepTheirOwnContractsAndIntent()
    {
        string? shared = null;
        var scenarios = new[] { ("amazon", "Describe Amazon products and export Excel using the browser"),
            ("review", "Clone GitHub code, run tests and publish a code review"),
            ("measure", "Read a calibrated sensor measurement") };
        foreach (var (method, intent) in scenarios)
        {
            var factory = new InMemoryMcpClientFactory();
            var description = intent + "; preserve requested YAML content literally.";
            factory.RegisterServer("fixture", new() { Tools = [new() { Name = method, Description = description, EffectKind = "read",
                InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
                OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"result":{"type":"string","description":"Original YAML document content"}},"required":["result"],"additionalProperties":false}""") }] });
            var runtime = new TestRuntime(new() { McpClientFactory = factory });
            var state = PlannerFixture.Session(); state.Request.Prompt = intent;
            state.Catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual);
            var contract = state.Catalog.Capabilities.Single(c => c.Method == method);
            var operation = TaskOperations.Describe(contract).Id;
            runtime.Proposal = new() { Requirements = new() { Summary = intent, Inputs = [],
                Outcomes = [new("requested", intent)], Outputs = [new() { Name = "result", Type = new() }] },
                Plan = new() { Root = new() { Tasks = [new() { Id = "perform", Objective = intent, Operation = operation }],
                    Outputs = [new("result", new() { Kind = "output", Source = "perform", Port = "result" })] } } };
            state = await PlannerFixture.RunAsync(runtime, state);
            Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
            var issued = Assert.Single(runtime.Calls); Assert.True(issued.StructuredOutputStrict); Assert.Equal(0, state.ReplanAttempts);
            var instructions = AssertGeneric(issued);
            shared ??= instructions; Assert.Equal(shared, instructions);
            var context = Context(issued);
            Assert.Equal(intent, context["request"]!.ToString());
            var presented = context["operations"]!.AsArray().Single(o => o!["id"]!.ToString() == operation)!;
            Assert.Equal(description, presented["description"]!.ToString());
            Assert.Contains("Original YAML document content", presented.ToJsonString());
            foreach (var (_, other) in scenarios.Where(s => s.Item1 != method)) Assert.DoesNotContain(other, issued.Prompt);
            Assert.Empty(PlanningContractValidation.ValidateInstance(TestRuntime.Response(issued, runtime.Proposal).Json, issued.StructuredOutputSchema!));
            var before = state.Yaml; var hash = state.ComputeArtifactHash();
            var revised = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "revise", PreserveRequirements = true,
                ExpectedRevision = state.Revision, ArtifactHash = hash, Text = "Preserve the same requested operation and exact output." }, runtime, PlannerFixture.Ct);
            revised = await PlannerFixture.RunAsync(runtime, revised);
            Assert.Equal(PlanningStatus.FinalReview, revised.Status); Assert.Equal(2, runtime.Calls.Count);
            AssertGeneric(runtime.Calls[1]); Assert.Equal(before, revised.Yaml);
            Assert.Null(revised.ApprovedHash);
        }
    }

    [Fact]
    public async Task ClarificationAnswersRemainDynamicAndTargetedRepairStaysGeneric()
    {
        var runtime = new TestRuntime { Proposal = new() { Clarifications = [new("format", "Which content is requested?", [], null)] } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Clarification, state.Status); AssertGeneric(Assert.Single(runtime.Calls));
        const string answer = "Return the literal YAML document for Amazon and GitHub; no browser actions.";
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = [new("format", Text: answer)] }, runtime, PlannerFixture.Ct);
        runtime.Proposal = new TestRuntime().Proposal;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, runtime.Calls.Count);
        AssertGeneric(runtime.Calls[1]); Assert.Contains(answer, Context(runtime.Calls[1])["userAnswers"]!.ToJsonString());

        // Issue an actual scoped repair, without granting edits to the objective.
        var original = state.Plan!.Root.Outputs[0];
        state.Plan.Root.Outputs[0] = new(original.Name, new() { Kind = "output", Source = "greet", Port = "missing" });
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        state.Status = PlanningStatus.Generating;
        runtime.Respond = (issued, _) =>
        {
            AssertGeneric(issued); Assert.True(PlanningRepairPatch.Issued(issued.StructuredOutputSchema!.AsObject()));
            var payload = new JsonObject { ["clarifications"] = null, ["discoveryRequests"] = null,
                ["patch"] = new JsonObject { ["edits"] = new JsonArray(RepairPatchTests.Edit(state, "/root/outputs/" + original.Name, "replace",
                    JsonSerializer.SerializeToNode(original.Value, PlanningJsonContext.Default.TaskValue))) } };
            return new() { Json = PlanningCorpus.Transport(payload, issued.StructuredOutputSchema!.AsObject(), issued.StructuredOutputSchema.AsObject()) };
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics)); Assert.Equal(3, runtime.Calls.Count);
        Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
    }

    internal static JsonNode Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;

    internal static string AssertGeneric(LLMRequest request)
    {
        var instructions = request.Prompt[..request.Prompt.IndexOf("\n{", StringComparison.Ordinal)];
        const string domains = @"\b(amazon|github|excel|xlsx|browser|products?|cookies?|workbook|headers?|writer|visits?|cloning|headings?|yaml)\b";
        Assert.False(Regex.IsMatch(instructions, domains, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), instructions);
        Assert.DoesNotContain("\"port\":\"success\"", instructions);
        Assert.Contains("Preserve original IDs and requested exact values", instructions);
        foreach (var (_, schema) in PlanningSchemaReferences.Walk(request.StructuredOutputSchema!.AsObject(), "", 0))
            if (schema["description"] is { } annotation)
                Assert.False(Regex.IsMatch(annotation.ToString(), domains, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), annotation.ToString());
        return instructions;
    }
}
