using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Assets.Animation;
using GnOuGo.Assets.Animation.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GnOuGo.Assets.Animation.Server.Tests;

public sealed class AnimationServerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string SimpleYaml = """
        version: 1
        name: Server test
        entrypoint: main
        workflows:
          main:
            steps:
              - id: think
                type: llm.call
              - id: finish
                type: emit
        """;

    private readonly HttpClient _client;

    public AnimationServerTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Health_ReturnsSuccess()
    {
        var response = await _client.GetAsync("/health", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await response.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_ReturnsPreviewDiagnosticsSummaryAndFailureTargets()
    {
        var response = await _client.PostAsJsonAsync("/api/simulations/validate", Request(SimpleYaml), CancellationToken.None);
        var body = await response.Content.ReadFromJsonAsync<ValidationResponse>(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.True(body.Valid);
        Assert.Equal("main", body.Entrypoint);
        Assert.Equal(2, body.FailureTargets.Count);
        Assert.Contains(body.Workflows, item => item is { Name: "main", StepCount: 2, IsEntrypoint: true });
    }

    [Fact]
    public async Task Stream_RejectsInvalidPreviewBeforeOpeningNdjson()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/simulations/stream",
            Request("version: 1\nworkflows: [broken"),
            CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("INVALID_PREVIEW", body, StringComparison.Ordinal);
        Assert.DoesNotContain("simulation.prepared", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_StartsWithDeterministicSceneThenOrdersNdjsonEvents()
    {
        var request = Request(SimpleYaml) with { Seed = 123, Scene = AnimationSceneKind.Office, Speed = 4 };
        var first = await ReadAllEnvelopes(request);
        var secondPrepared = await ReadFirstEnvelopeAndCancel(request);

        Assert.NotEmpty(first);
        Assert.Equal("simulation.prepared", first[0].Type);
        var prepared = Assert.IsType<SimulationPreparedData>(first[0].Prepared);
        Assert.Equal(123, prepared.Seed);
        Assert.Equal(AnimationSceneKind.Office, prepared.Scene);
        Assert.Equal(1, prepared.TaskObjectCount);
        Assert.True(prepared.CanvasWidth >= 1600);
        Assert.True(prepared.CanvasHeight >= 900);
        Assert.Equal(1, prepared.LaneCount);
        Assert.True(prepared.NodeCount >= 4);
        Assert.Contains("id=\"scene-office\"", prepared.Svg, StringComparison.Ordinal);
        Assert.Contains("data-node-kind=\"desk\"", prepared.Svg, StringComparison.Ordinal);
        Assert.Contains("data-station-kind=\"keyboarddesk\"", prepared.Svg, StringComparison.Ordinal);
        Assert.Contains("data-station-kind=\"deliverydock\"", prepared.Svg, StringComparison.Ordinal);
        Assert.Equal(prepared.Svg, secondPrepared.Prepared?.Svg);

        var events = first.Skip(1).Select(item => Assert.IsType<SimulationEvent>(item.Event)).ToArray();
        Assert.Equal(Enumerable.Range(0, events.Length), events.Select(item => item.Sequence));
        Assert.Equal(events.OrderBy(item => item.OffsetMs).ThenBy(item => item.Sequence), events);
        Assert.Equal(SimulationEventTypes.SimulationStarted, events[0].Type);
        Assert.Equal(SimulationEventTypes.SimulationCompleted, events[^1].Type);
    }

    [Fact]
    public void Prepare_ContainsOverlappingParallelEventsAndFailureEvents()
    {
        var yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - id: fork
                    type: parallel
                    branches:
                      - name: ai
                        steps:
                          - id: a
                            type: llm.call
                      - name: mcp
                        steps:
                          - id: b
                            type: mcp.call
                  - id: after
                    type: emit
            """;
        var service = new SimulationPreparationService();
        var successful = service.Prepare(Request(yaml) with { Seed = 7 });
        var failed = service.Prepare(Request(yaml) with
        {
            Seed = 7,
            FailAt = new SimulationFailureTarget("main", "a")
        });
        var a = successful.Events.Single(item => item.Type == SimulationEventTypes.StepStarted && item.StepId == "a");
        var b = successful.Events.Single(item => item.Type == SimulationEventTypes.StepStarted && item.StepId == "b");

        Assert.Equal(a.OffsetMs, b.OffsetMs);
        Assert.True(a.DurationMs > 0);
        Assert.Contains(successful.Events, item => item.Type == SimulationEventTypes.ActorCloned);
        Assert.Contains(successful.Events, item => item.Type == SimulationEventTypes.ActorMerged);
        Assert.Contains(failed.Events, item => item.Type == SimulationEventTypes.StepCompleted && item.StepId == "a" && item.Status == SimulationStatus.Failed);
        Assert.Contains(failed.Events, item => item.Type == SimulationEventTypes.StepSkipped && item.StepId == "after");
        Assert.Equal(SimulationStatus.Failed, failed.Events.Last(item => item.Type == SimulationEventTypes.SimulationCompleted).Status);
    }

    [Fact]
    public async Task Stream_CanBeCancelledImmediatelyAfterPreparedScene()
    {
        using var cancellation = new CancellationTokenSource();
        using var message = CreateStreamRequest(Request(SimpleYaml) with { Speed = 0.5 });
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(stream);

        var firstLine = await reader.ReadLineAsync(cancellation.Token);
        cancellation.Cancel();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(firstLine);
        Assert.Contains("simulation.prepared", firstLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_Returns422ForPayloadHardLimits()
    {
        var oversizedWorkflow = new string('x', SimulationPreparationService.MaxWorkflowBytes + 1);
        var response = await _client.PostAsJsonAsync(
            "/api/simulations/validate",
            Request(oversizedWorkflow),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("WORKFLOW_TOO_LARGE", await response.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);

        var service = new SimulationPreparationService();
        var inputResult = service.Validate(Request(SimpleYaml) with
        {
            Inputs = new JsonObject { ["payload"] = new string('y', SimulationPreparationService.MaxInputBytes + 1) }
        });
        Assert.Contains(inputResult.Diagnostics, item => item.Code == "INPUTS_TOO_LARGE");
    }

    [Fact]
    public void Prepare_EnforcesDefaultStepActorAndCloneHardLimits()
    {
        var service = new SimulationPreparationService();
        var manySteps = new StringBuilder("version: 1\nworkflows:\n  main:\n    steps:\n");
        for (var index = 0; index < 201; index++)
            manySteps.Append("      - id: step-").Append(index).Append("\n        type: set\n");

        var stepError = Assert.Throws<SimulationRequestException>(() => service.Prepare(Request(manySteps.ToString())));
        Assert.Equal("STEP_LIMIT", stepError.Code);

        var manyBranches = new StringBuilder("version: 1\nworkflows:\n  main:\n    steps:\n      - id: fork\n        type: parallel\n        branches:\n");
        for (var index = 0; index < 17; index++)
            manyBranches.Append("          - name: branch-").Append(index).Append("\n            steps:\n              - id: branch-step-").Append(index).Append("\n                type: set\n");

        var cloneError = Assert.Throws<SimulationRequestException>(() => service.Prepare(Request(manyBranches.ToString())));
        Assert.Equal("CLONE_LIMIT", cloneError.Code);

        var manyCalls = new StringBuilder("version: 1\nworkflows:\n  main:\n    steps:\n");
        for (var index = 0; index < 32; index++)
            manyCalls.Append("      - id: call-").Append(index).Append("\n        type: workflow.route\n");

        var actorError = Assert.Throws<SimulationRequestException>(() => service.Prepare(Request(manyCalls.ToString())));
        Assert.Equal("ACTOR_LIMIT", actorError.Code);
    }

    [Fact]
    public void ProjectsHaveNoFlowProjectPackageOrAssemblyDependency()
    {
        var assemblies = DependencyClosure(typeof(Program).Assembly, typeof(GnouGnouAnimationPlanner).Assembly);
        Assert.DoesNotContain(assemblies, name => name.StartsWith("GnOuGo.Flow", StringComparison.OrdinalIgnoreCase));

        var repository = FindRepositoryRoot();
        var projectFiles = new[]
        {
            Path.Combine(repository, "src", "GnOuGo.Assets.Animation", "GnOuGo.Assets.Animation.csproj"),
            Path.Combine(repository, "src", "GnOuGo.Assets.Animation.Server", "GnOuGo.Assets.Animation.Server.csproj")
        };
        Assert.All(projectFiles, path =>
            Assert.DoesNotContain("GnOuGo.Flow", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<SimulationStreamEnvelope>> ReadAllEnvelopes(SimulationRequest request)
    {
        using var response = await _client.SendAsync(
            CreateStreamRequest(request),
            HttpCompletionOption.ResponseContentRead,
            CancellationToken.None);
        response.EnsureSuccessStatusCode();
        var lines = (await response.Content.ReadAsStringAsync(CancellationToken.None))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Select(DeserializeEnvelope).ToArray();
    }

    private async Task<SimulationStreamEnvelope> ReadFirstEnvelopeAndCancel(SimulationRequest request)
    {
        using var cancellation = new CancellationTokenSource();
        using var response = await _client.SendAsync(
            CreateStreamRequest(request),
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync(cancellation.Token);
        cancellation.Cancel();
        return DeserializeEnvelope(Assert.IsType<string>(line));
    }

    private static HttpRequestMessage CreateStreamRequest(SimulationRequest request) => new(
        HttpMethod.Post,
        "/api/simulations/stream")
    {
        Content = new StringContent(
            JsonSerializer.Serialize(request, AnimationServerJsonContext.Default.SimulationRequest),
            Encoding.UTF8,
            "application/json")
    };

    private static SimulationStreamEnvelope DeserializeEnvelope(string line) =>
        JsonSerializer.Deserialize(line, AnimationServerJsonContext.Default.SimulationStreamEnvelope)
        ?? throw new InvalidOperationException("Expected an NDJSON envelope.");

    private static SimulationRequest Request(string yaml) => new()
    {
        Workflow = yaml,
        Seed = 42,
        Scene = AnimationSceneKind.Office,
        Speed = 4
    };

    private static IReadOnlySet<string> DependencyClosure(params Assembly[] roots)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>(roots);
        while (queue.TryDequeue(out var assembly))
        {
            var name = assembly.GetName().Name;
            if (name is null || !names.Add(name))
                continue;
            foreach (var reference in assembly.GetReferencedAssemblies().Where(item => item.Name?.StartsWith("GnOuGo.", StringComparison.Ordinal) == true))
                queue.Enqueue(Assembly.Load(reference));
        }
        return names;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GnOuGo.Agent.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
