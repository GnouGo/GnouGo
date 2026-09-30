using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using GnOuGo.Agent.Server.Components.Planning;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningDiscoveryInspectionTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task DesignerDisplaysIssuedHistoryAndCopiesOnlySanitizedMetadataWithoutDispatch()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var first = State(1, "alpha"); var second = State(2, "beta");
        second.Discovery.Inspections = [new("source", OperationIds: ["beta"])];
        Assert.True(await fixture.Store.TrySaveAsync(first, null, Ct));
        Assert.True(await fixture.Store.TrySaveAsync(second, 1, Ct));
        var current = State(3, "beta"); current.PendingCall = null; current.ModelCalls = 2;
        current.Discovery.Inspections = [new("source", OperationIds: ["alpha"])]; // Selected after the last issued request.
        current.Plan = new() { Root = new() { Tasks = [new() { Id = "use", Operation = "beta", Objective = "Private business objective" }] } };
        current.Diagnostics = [new("DISCOVERY_INCOMPLETE", "/", "PRIVATE_DIAGNOSTIC /Users/example/local Bearer fake")];
        Assert.True(await fixture.Store.TrySaveAsync(current, 2, Ct));
        var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), settings: new() { BackgroundProcessingEnabled = false });
        Services.AddSingleton(service); Services.AddLogging(); JSInterop.Mode = JSRuntimeMode.Loose;
        var before = await fixture.Records.ListAsync(EfPlanningSessionStore.Collection, "planning-tests", "test", Ct);
        var cut = Render<PlannerDiscovery>(p => p.Add(c => c.Session, current));
        Assert.DoesNotContain("Complete input estimate", cut.Markup);
        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Contains("Complete input estimate", cut.Markup));
        Assert.Equal(2, cut.FindAll("#discovery-request option").Count);
        Assert.Contains("2 / 8 calls used", cut.Markup);
        Assert.Contains("Requested for inspection", cut.Markup);
        Assert.Contains("Used by current TaskPlan", cut.Markup);
        Assert.Contains("Historical exclusion scores were not recorded", cut.Markup);
        Assert.DoesNotContain("PRIVATE_", cut.Markup);
        cut.Find("#discovery-request").Change("0");
        Assert.Contains("Full contract", cut.Markup);
        cut.FindAll("button").Single(b => b.TextContent == "Copy discovery report").Click();
        cut.WaitForAssertion(() => Assert.Contains("Copied sanitized report", cut.Markup));
        var clipboard = JSInterop.Invocations.Single(i => i.Identifier == "navigator.clipboard.writeText").Arguments[0]!.ToString()!;
        Assert.Contains("DISCOVERY_INCOMPLETE", clipboard); Assert.Contains("read_alpha", clipboard);
        foreach (var secret in new[] { "PRIVATE_", "/Users/", "Bearer", "https://", "password", "ghp_" }) Assert.DoesNotContain(secret, clipboard);
        var report = (await service.InspectDiscoveryAsync("inspection", false, Ct))!;
        Assert.Equal("alpha", Assert.Single(report.RequestedOperationIds));
        Assert.Equal("beta", Assert.Single(report.Requests[1].Added)); Assert.Equal("alpha", Assert.Single(report.Requests[1].Removed));
        Assert.True(report.Requests[1].Tools.Single(t => t.Id == "beta").Requested);
        Assert.True(report.Requests[0].Tools.Single(t => t.Id == "beta").Used); // Current intent, separately labelled.
        Assert.Equal(before, await fixture.Records.ListAsync(EfPlanningSessionStore.Collection, "planning-tests", "test", Ct));
        Assert.Empty(await fixture.Records.ListAsync(PlanningModelJournal.RequestCollection, "planning-tests", "test", Ct));
        Assert.Equal(2, (await fixture.Store.LoadAsync("planning-tests", "inspection", Ct))!.ModelCalls);
        await DisposeComponentsAsync();
    }

    [Fact]
    public async Task TenantOwnershipAndMissingSnapshotsDoNotTriggerFallbackDiscovery()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var current = State(2, "alpha"); current.PendingCall = null;
        var other = State(1, "beta"); other.Request.TenantId = "another-tenant";
        await fixture.Records.UpsertAsync(EfPlanningSessionStore.Collection, "planning-tests", "inspection:1:forged",
            JsonSerializer.Serialize(other, PlanningJsonContext.Default.PlanningSession), "test", Ct);
        var report = await PlanningDiscoveryInspection.ReadAsync(fixture.Records, "planning-tests", current, false, Ct);
        Assert.Empty(report.Requests);
        await Assert.ThrowsAsync<PlanningConflictException>(() => PlanningDiscoveryInspection.ReadAsync(fixture.Records, "another-tenant", current, false, Ct));
        Assert.Empty(await fixture.Records.ListAsync(PlanningModelJournal.RequestCollection, "planning-tests", "test", Ct));
    }

    [Fact]
    public void ReportDistinguishesShownRetainedDeniedConflictingAndUnavailableTools()
    {
        var state = State(1, "alpha"); var page = state.Discovery.Pages[0];
        state.Catalog!.Policy.DeniedCapabilityIds.Add("denied");
        foreach (var id in new[] { "denied", "conflict", "unavailable", "omitted" }) page.Capabilities.Add(Tool(id));
        state.Discovery.Pages.Add(new("source", "next", [Tool("conflict") with { Version = "v2" }], null));
        state.Discovery.Limitations.Add("unavailable: PRIVATE_PROVIDER_ERROR");
        var report = PlanningDiscoveryInspection.Build(state, [state]);
        var tools = Assert.Single(report.Requests).Tools.ToDictionary(t => t.Id);
        Assert.Equal("Full contract", tools["alpha"].Status); Assert.Equal("Index only", tools["beta"].Status);
        Assert.Equal("Policy excluded", tools["denied"].Status); Assert.Equal("Version conflict", tools["conflict"].Status);
        Assert.Equal("Unavailable", tools["unavailable"].Status); Assert.Equal("Retained, omitted", tools["omitted"].Status);
        Assert.All(tools.Values, t => Assert.True(t.ContractBytes > 0));
        Assert.DoesNotContain("PRIVATE_", PlanningDiscoveryInspection.CopyText(report));
    }

    [Fact]
    public void RepairProducerContractsAreNotPresentedAsFullOperationContracts()
    {
        var state = State(1, "alpha"); var request = state.PendingCall!.Request;
        var context = JsonNode.Parse(request.Prompt[(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
        context["operations"]![0]!["contextRole"] = "producer_outputs";
        context["operations"]![0]!.AsObject().Remove("inputs");
        request.Prompt = "Instructions\n" + context.ToJsonString();
        var report = PlanningDiscoveryInspection.Build(state, [state]);
        var tool = Assert.Single(report.Requests).Tools.Single(t => t.Id == "alpha");
        Assert.Equal("Producer outputs", tool.Status); Assert.Contains("input contracts were not sent", tool.Reason);
        Assert.Contains("Producer outputs", PlanningDiscoveryInspection.CopyText(report));
    }

    [Fact]
    public void ClipboardRejectsPathAndCredentialShapedToolNames()
    {
        var state = State(1, "alpha");
        foreach (var name in new[] { "/Users/example/private", "https://example.test/path?token=x", "ghp_test", "Bearer test", "sk-test" })
            state.Discovery.Pages[0].Capabilities.Add(Tool("safe" + state.Discovery.Pages[0].Capabilities.Count) with { Name = name });
        var report = PlanningDiscoveryInspection.CopyText(PlanningDiscoveryInspection.Build(state, [state]));
        Assert.Contains("[redacted]", report);
        foreach (var value in new[] { "/Users", "https://", "ghp_", "Bearer", "sk-test" }) Assert.DoesNotContain(value, report);
    }

    private static PlanningSession State(int revision, string shown)
    {
        var context = new JsonObject
        {
            ["request"] = "PRIVATE_PROMPT /Users/example/local password=secret",
            ["operations"] = new JsonArray(new JsonObject { ["id"] = shown, ["description"] = "PRIVATE_CONTRACT", ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray() }),
            ["coverage"] = new JsonArray(new JsonObject { ["sourceId"] = "source", ["pagesRead"] = 1, ["indexedCount"] = 2,
                ["hasMore"] = true, ["index"] = new JsonArray(new JsonObject { ["id"] = shown == "alpha" ? "beta" : "alpha" }) })
        };
        return new()
        {
            Request = new() { TenantId = "planning-tests", SessionId = "inspection", Prompt = "PRIVATE_PROMPT", Generation = new() { MaxInputTokensPerRequest = 24000 } },
            Revision = revision, Status = PlanningStatus.Stopped, ModelCalls = revision,
            Catalog = new() { AllowedStepTypes = ["mcp.call"] },
            Discovery = new() { Sources = [new("source", "PRIVATE_SOURCE_DESCRIPTION")], Pages = [new("source", null, [Tool("alpha"), Tool("beta")], "next")] },
            PendingCall = new() { Id = "inspection:" + revision, Request = new() { Prompt = "Instructions\n" + context.ToJsonString(), StructuredOutputSchema = new JsonObject { ["type"] = "object" } } }
        };
    }
    private static CapabilitySummary Tool(string id) => new(id, "source", "read_" + id, "PRIVATE_METADATA", "mcp.call", "read", "v1",
        Operation: new() { Id = id, Description = "PRIVATE_CONTRACT", Inputs = [new() { Name = "input", Schema = new() { ["type"] = "string" } }] });
}
