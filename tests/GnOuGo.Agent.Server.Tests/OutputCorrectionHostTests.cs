using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class OutputCorrectionHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BackgroundOwnerCorrectsOrStopsOnceAndRestartDoesNotRedispatch(bool repeatInvalid, bool literal)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var client = new OutputClient(repeatInvalid, literal);
        var expectedCalls = literal && repeatInvalid ? 3 : 2;
        var agents = PlanningSessionLifecycleTests.AgentCatalog();
        PlanningSession result;
        using (var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, client))
        {
            await service.StartAsync(Ct);
            try
            {
                var initial = await service.StartAsync("output-correction", "Return only the requested result", false, Ct);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                while (true)
                {
                    result = (await service.GetAsync(initial.Request.SessionId, deadline.Token))!;
                    if (PlanningStatus.IsWaiting(result.Status) || PlanningStatus.IsTerminal(result.Status)) break;
                    await Task.Delay(25, deadline.Token);
                }
                Assert.True(result.Status == (repeatInvalid ? PlanningStatus.Stopped : PlanningStatus.FinalReview), string.Join(';', result.Diagnostics));
                Assert.True(client.Requests.Count == expectedCalls, string.Join(';', result.Diagnostics));
                Assert.Equal(expectedCalls, client.Requests.Count); Assert.Equal(expectedCalls, result.ModelCalls); Assert.Equal(expectedCalls - 1, result.ReplanAttempts);
                Assert.Null(result.ApprovedHash); Assert.Null(result.SavedAgentId); Assert.Null(result.PendingCall);
                Assert.Equal(expectedCalls, result.Usage!.Calls); Assert.Equal(expectedCalls * 15, result.Usage.TotalTokens);
                for (var i = 0; i < 3; i++)
                    Assert.Equal(result.Revision, (await service.GetAsync(initial.Request.SessionId, Ct))!.Revision);
                Assert.Equal(expectedCalls, client.Requests.Count);
                if (repeatInvalid)
                {
                    Assert.Null(result.Yaml);
                    Assert.Contains(result.Diagnostics, d => d.Code == (literal ? "REPLAN_NO_PROGRESS" : "PLANNING_RESPONSE_INVALID"));
                    if (!literal) Assert.Contains(result.Diagnostics, d => d.Location == "/plan/root/outputs/1/name");
                }
                else PlanningArtifactApproval.Verify(result);
            }
            finally { await service.StopAsync(Ct); }
        }
        var snapshot = JsonSerializer.Serialize(result, PlanningJsonContext.Default.PlanningSession);
        using var restarted = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, client);
        await restarted.StartAsync(Ct);
        try
        {
            Assert.Equal(snapshot, JsonSerializer.Serialize(await restarted.GetAsync(result.Request.SessionId, Ct), PlanningJsonContext.Default.PlanningSession));
            Assert.Equal(expectedCalls, client.Requests.Count);
        }
        finally { await restarted.StopAsync(Ct); }
        Assert.Equal(expectedCalls, client.Requests.Count);
        Assert.Equal(expectedCalls, (await fixture.Records.ListAsync(PlanningModelJournal.Collection, "planning-tests", EfPlanningSessionStore.Author, Ct)).Count);
        Assert.Null(await fixture.Store.LoadAsync("other-tenant", result.Request.SessionId, Ct));
    }

    private sealed class OutputClient(bool repeatInvalid, bool literal) : ILLMClient
    {
        internal readonly List<LLMRequest> Requests = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request); Assert.InRange(Requests.Count, 1, literal && repeatInvalid ? 3 : 2);
            var plan = PlanningCorpus.LiteralResult();
            if (!literal && (Requests.Count == 1 || repeatInvalid)) plan.Root.Outputs.Add(new("extra", new() { Kind = "string", Text = "unrequested" }));
            if (literal)
            {
                plan.Inputs = [new() { Name = "supplied", Type = new() { Kind = "string" } }];
                plan.Root.Outputs = [new("result", new() { Kind = "string", Text = "${supplied}" })];
            }
            var proposal = new PlanningProposal { Plan = plan, Requirements = new()
            {
                Summary = "Return only the requested result", Inputs = plan.Inputs, Outcomes = [new("result", "Return the requested number")],
                Outputs = [new() { Name = "result", Type = new() { Kind = literal ? "string" : "number" } }]
            } };
            var response = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal),
                request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())!;
            if (literal && request.StructuredOutputSchema!["properties"]?["patch"] is not null)
            {
                var context = JsonNode.Parse(request.Prompt[(request.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
                var slot = Assert.Single(context["repair"]!["slots"]!.AsArray())!;
                Assert.Equal("/root/outputs/result", slot["location"]!.ToString());
                response = new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null,
                    ["patch"] = new JsonObject { ["edits"] = repeatInvalid ? new JsonArray() : new JsonArray(new JsonObject
                    { ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace", ["value"] = new JsonObject { ["kind"] = "input", ["source"] = "supplied" } }) } };
            }
            if (!literal && Requests.Count == 2)
            {
                Assert.Contains("Remove only the diagnosed unaccepted exports", request.Prompt);
                Assert.Contains("Preserve all operation arguments verbatim", request.Prompt);
            }
            var findings = PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema);
            if (!literal && repeatInvalid && Requests.Count == 2)
                Assert.NotEmpty(findings); // Fresh accepted-output domains reject this response before semantic correction.
            else Assert.Empty(findings);
            return Task.FromResult(new LLMResponse { Json = response, Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
    }
}
