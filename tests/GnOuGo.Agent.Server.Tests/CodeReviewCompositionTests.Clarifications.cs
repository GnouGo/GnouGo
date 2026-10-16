using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class CodeReviewCompositionTests
{
    [Theory]
    [InlineData("approve")]
    [InlineData("findings")]
    [InlineData("failed_checks")]
    [InlineData("incomplete")]
    [InlineData("denied")]
    [InlineData("post_failure")]
    [InlineData("unknown")]
    public async Task RetainedTransformsBecomeRealOperationsWithoutChangingAcceptedInterfaces(string variant)
    {
        using var fixture = new Execution(variant, report: true);
        var store = new InMemoryWorkflowRunStore();
        var engine = new WorkflowEngine { McpClientFactory = fixture.Factory, RunStore = store,
            Limits = new() { TenantId = "fixture", RunId = Guid.NewGuid().ToString("N") } };
        var actual = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await actual.DiscoverAsync(new(), Ct);
        var discovery = new CapabilityDiscoveryState();
        foreach (var source in await actual.Capabilities.ListSourcesAsync(Ct))
        {
            discovery.Sources.Add(source);
            var page = await actual.Capabilities.ListAsync(source.Id, null, Ct); discovery.Pages.Add(page);
            foreach (var entry in page.Capabilities)
            {
                var capability = await actual.Capabilities.ResolveAsync(entry, Ct);
                catalog.Capabilities.Add(capability); discovery.Resolved.Add(capability);
            }
        }
        catalog.Policy.RequireExternalConfirmation = false; // Only deterministic, disposable local adapters.
        var original = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "UsefulClarifications", "retained-proposal.json")), PlanningJsonContext.Default.PlanningProposal)!;
        var corrected = Proposal(catalog);
        var tree = JsonSerializer.SerializeToNode(corrected.Plan, PlanningJsonContext.Default.TaskPlan)!;
        Rename(tree);
        corrected.Plan = tree.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        corrected.Plan.Inputs = original.Plan!.Inputs;
        corrected.Requirements = original.Requirements;
        var root = corrected.Plan.Root;
        root.Tasks.Insert(0, Op(catalog, "parse", "parse_change", new TaskOutput("url", Input("pullRequestUrl"))));
        root.Tasks.Single(t => t.Id == "inspect").Inputs = [new("url", Ref("parse", "url"))];
        root.Tasks.Add(Op(catalog, "report", "write_report", new("summary", Ref("submit", "summary")),
            new("decision", Ref("submit", "decision")), new("reviewUrl", Ref("submit", "url")), new("name", Input("name"))));
        var release = Assert.Single(root.Always);
        release.Body!.Outputs = [new("status", Ref("cleanup", "status"))];
        release.Otherwise!.Outputs = [new("status", Text("nothing_to_clean"))];
        root.Outputs = [new("summary", Ref("submit", "summary")), new("decision", Ref("submit", "decision")),
            new("pullRequestState", Ref("inspect", "state")), new("githubReviewUrl", Ref("submit", "url")),
            new("reportPath", Ref("report", "path")), new("cleanupStatus", Ref("release", "status"))];
        var runtime = new LocalProductOutcomeExecutionTests.ProposalRuntime(actual, original);
        var planner = new HybridWorkflowPlanner();
        var state = new PlanningSession { IntentVersion = 2, Catalog = catalog, Discovery = discovery, Plan = original.Plan, Requirements = original.Requirements,
            Request = new() { TenantId = "fixture", Prompt = "Perform the accepted review outcomes using real operations; preserve every public contract.",
                Generation = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                    "Fixtures", "UsefulClarifications", "retained-generation.json")), PlanningJsonContext.Default.PlanningGenerationOptions)!,
                Options = new() { ["compilation_profile"] = "compact-bindings-v15", ["clarification_policy"] = 1 } } };
        var accepted = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        RecordedPlanCompilation.InspectSelected(state, corrected.Plan);
        runtime.Patch = _ => JsonSerializer.SerializeToNode(corrected, PlanningJsonContext.Default.PlanningProposal)!.AsObject();
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision,
            ArtifactHash = state.ComputeArtifactHash(), PreserveRequirements = true,
            Text = "Replace simulated external work with discovered operations, preserving accepted outcomes and interfaces. Separate URL parsing, metadata acquisition, one clone, checks, exact feedback, publication, report writing and cleanup. Never invent producer status." }, runtime, Ct);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(accepted, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        Assert.Empty(fixture.Calls); PlanningArtifactApproval.Verify(state);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject
        { ["pullRequestUrl"] = Execution.ChangeUrl, ["reviewInstructions"] = Execution.Instructions, ["name"] = null }, Ct);
        var success = variant is "approve" or "findings" or "failed_checks";
        Assert.True(result.Success == success, result.Error?.Message);
        Assert.Equal(variant == "denied" ? 0 : 1, fixture.CloneCount);
        Assert.Equal(variant is "denied" or "unknown" ? 0 : 1, fixture.CleanupCount);
        Assert.Equal(variant == "unknown", Directory.Exists(fixture.Target));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(fixture.Root, "keep.txt")));
        Assert.Equal("original source\n", File.ReadAllText(Path.Combine(fixture.Source, "readme.txt")));
        if (variant != "denied") Assert.Equal(new[] { "install", "lint", "unit", "integration" }, fixture.Checks);
        if (success)
        {
            Assert.Equal(original.Requirements!.Outputs!.Select(o => o.Name), result.Outputs!.AsObject().Select(p => p.Key));
            Assert.Equal(variant == "approve" ? "ACCEPT" : "REFUSE", result.Outputs["decision"]!.ToString());
            Assert.Equal(Execution.Summary, result.Outputs["summary"]!.ToString());
            Assert.Equal("open", result.Outputs["pullRequestState"]!.ToString());
            Assert.Equal("cleaned", result.Outputs["cleanupStatus"]!.ToString());
            Assert.Equal(Execution.ChangeUrl + "&review=original", result.Outputs["githubReviewUrl"]!.ToString());
            var path = result.Outputs["reportPath"]!.GetValue<string>(); Assert.Equal(Path.Combine(fixture.Root, "report.json"), path);
            var report = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(Execution.Summary, report["summary"]!.ToString()); Assert.Null(report["name"]);
            Assert.Equal(result.Outputs["decision"]!.ToString(), report["decision"]!.ToString());
            Assert.Equal(new[] { "parse_change", "inspect", "clone", "checks", "review", "begin" }, fixture.Calls.Take(6));
            Assert.Equal(new[] { "submit", "write_report", "cleanup" }, fixture.Calls.TakeLast(3));
            Assert.Equal(variant == "findings" ? 3 : 0, fixture.Posted.Count);
            if (variant == "findings") Assert.Equal(new[] { Execution.InlineText, Execution.GlobalText, Execution.InlineText }, fixture.Posted.Select(p => p["text"]!.ToString()));
        }
        else { Assert.DoesNotContain("submit", fixture.Calls); Assert.False(File.Exists(Path.Combine(fixture.Root, "report.json"))); }
        var before = fixture.Calls.ToArray(); var journal = (await store.ReadAsync("fixture", engine.Limits.RunId, Ct))!;
        Assert.DoesNotContain(journal.Invocations.Values, i => i.StepType is "llm.call" or "mapping.dynamic");
        var recovered = await new WorkflowEngine { RunStore = store }.ResumeAsync("fixture", engine.Limits.RunId, journal.Revision, document.Workflows[document.Entrypoint!], Ct);
        Assert.Equal(result.Success, recovered.Success); Assert.True(JsonNode.DeepEquals(result.Outputs, recovered.Outputs)); Assert.Equal(before, fixture.Calls);
        if (variant == "unknown") Assert.Equal("RUN_NEEDS_RECONCILIATION", recovered.Error?.Code);
        if (variant == "approve" && Environment.GetEnvironmentVariable("GNOU_GO_CLARIFICATION_REVIEW_EVIDENCE") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "corrected-proposal.json"), JsonSerializer.Serialize(corrected, PlanningJsonContext.Default.PlanningProposal), Ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "corrected-v15.yaml"), state.Yaml, Ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "composition-metrics.json"), new JsonObject
            { ["planningCalls"] = 1, ["repairs"] = 0, ["providerCalls"] = 0, ["runtimeLlmCalls"] = 0, ["reviewAdapterCalls"] = 1,
                ["requestEstimate"] = runtime.InputEstimate, ["yamlBytes"] = Encoding.UTF8.GetByteCount(state.Yaml!),
                ["effects"] = JsonSerializer.SerializeToNode(before), ["acceptedRequirementsUnchanged"] = true,
                ["publicInterfacesUnchanged"] = true, ["recoveredAdditionalEffects"] = 0 }.ToJsonString(new() { WriteIndented = true }), Ct);
        }
        output.WriteLine($"{variant}: explicit revision=1; repairs=0; runtimeLLM=0; clone={fixture.CloneCount}; checks={fixture.Checks.Count}; cleanup={fixture.CleanupCount}; recovery added no effects.");
        static void Rename(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["kind"]?.ToString() == "input")
                    obj["source"] = obj["source"]?.ToString() switch { "changeUrl" => "pullRequestUrl", "instructions" => "reviewInstructions", var source => source };
                foreach (var child in obj.Select(p => p.Value).ToArray()) Rename(child);
            }
            else if (node is JsonArray array) foreach (var child in array) Rename(child);
        }
    }
}
