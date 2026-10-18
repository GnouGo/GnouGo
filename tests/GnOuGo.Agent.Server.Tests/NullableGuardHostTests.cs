using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class NullableGuardHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundOwnerCorrectsOrStopsOnceAndRestartDoesNotRedispatch(bool repeatInvalid)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var client = new GuardClient(repeatInvalid);
        var agents = PlanningSessionLifecycleTests.AgentCatalog();
        var expectedCalls = repeatInvalid ? 2 : 3; // Successful corrections also require the bounded local Judge.
        PlanningSession result;
        using (var service = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), agents, client))
        {
            await service.StartAsync(Ct);
            try
            {
                var initial = await service.StartAsync("nullable-guards", "Return the checked original result", false, Ct);
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
                Assert.Equal(expectedCalls, client.Requests.Count); Assert.Equal(expectedCalls, result.ModelCalls); Assert.Equal(1, result.ReplanAttempts);
                Assert.Null(result.ApprovedHash); Assert.Null(result.SavedAgentId); Assert.Null(result.PendingCall);
                Assert.Equal(expectedCalls, result.Usage!.Calls); Assert.Equal(expectedCalls * 15, result.Usage.TotalTokens);
                for (var i = 0; i < 3; i++)
                    Assert.Equal(result.Revision, (await service.GetAsync(initial.Request.SessionId, Ct))!.Revision);
                Assert.Equal(expectedCalls, client.Requests.Count);
                if (repeatInvalid)
                {
                    Assert.Null(result.Yaml);
                    Assert.Contains(result.Diagnostics, d => d.Code == "AUTOMATIC_GUARD_CORRECTION_UNSAFE");
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

    private sealed class GuardClient(bool repeatInvalid) : ILLMClient
    {
        internal readonly List<LLMRequest> Requests = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request); Assert.InRange(Requests.Count, 1, 3);
            TaskValue Field(string name) => new() { Kind = "output", Source = "declared_result", Port = name };
            var plan = PlanningCorpus.LiteralResult();
            plan.Root.Tasks.Insert(0, new() { Id = "declared_result", Kind = "transform", Mode = "interpret",
                Objective = "Decide using the supplied business input", Inputs = [new("text", new() { Kind = "string", Text = "observed" })],
                ResultType = new() { Kind = "object", Fields = [new() { Name = "payload", Type = new() { Kind = "string", Nullable = true } },
                    new() { Name = "ready", Type = new() { Kind = "boolean" } }] } });
            plan.Root.Tasks.Add(new() { Id = "use", Kind = "value", Objective = "Return only after the original assertion",
                Requires = Field("payload"), Outputs = plan.Root.Outputs });
            plan.Root.Outputs = [new("result", new() { Kind = "output", Source = "use", Port = "result" })];
            JsonNode response;
            if (Requests.Count == 1 || request.StructuredOutputSchema?["properties"]?["findings"] is not null)
            {
                var proposal = new PlanningProposal { Plan = plan, Requirements = PlanningCorpus.Requirements("local") };
                proposal.Requirements.Inputs = [];
                proposal.Requirements.Outputs = [new() { Name = "result", Type = new() { Kind = "number" } }];
                response = PlanningCorpus.OneDraftResponse(request, proposal)!;
            }
            else
            {
                Assert.Contains("nullable nonboolean", request.Prompt);
                var slot = Assert.Single(PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray())!;
                TaskValue guard = repeatInvalid ? new() { Kind = "boolean", Boolean = true } : new() {
                    Kind = "predicate", Predicate = "and", Items = [Field("ready"), new() { Kind = "predicate", Predicate = "not_equal", Items = [Field("payload"), new() { Kind = "null" }] }] };
                response = PlanningCorpus.Transport(new JsonObject { ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null,
                    ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject { ["slot"] = slot["id"]!.DeepClone(), ["action"] = "replace",
                        ["value"] = JsonSerializer.SerializeToNode(guard, PlanningJsonContext.Default.TaskValue) }) } },
                    request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())!;
            }
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema!));
            return Task.FromResult(new LLMResponse { Json = response, Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 } });
        }
    }
}
