using System.Text;
using GnOuGo.ProxyCopilot.Server.Configuration;
using GnOuGo.ProxyCopilot.Server.Traffic;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class ConfigurationAndTrafficTests
{
    internal static ProxyOptions Options() => new()
    {
        Providers = new() { ["test"] = new() { Connection = new() { Type = "openai", Url = "https://example.test/v1",
            RequestPolicy = new() { BackgroundProtocol = GnOuGo.AI.Core.LLMBackgroundProtocolMode.ChatCompletions }, RetryPolicy = new() { MaxAttempts = 1, MaxUncertainRetries = 0 } },
            Models = new() { ["model"] = new() { UpstreamId = "upstream", Metadata = new() { MaxInputTokens = 10000, MaxOutputTokens = 1000, Capabilities = new() { SupportsTools = true } } } } } }
    };

    [Fact]
    public void ExactAliasesAndConflictingCredentialsAreValidated()
    {
        var options = Options();
        var registry = new ModelRegistry(options);
        Assert.Equal("upstream", registry.Resolve("test/model").Model.UpstreamId);
        Assert.Throws<ProxyException>(() => registry.Resolve("TEST/model"));
        options.Providers["test"].Connection.ClientSecret = "never-print-this";
        var error = Assert.Throws<InvalidOperationException>(() => new ModelRegistry(options));
        Assert.DoesNotContain("never-print-this", error.Message);
        options.Providers["test"].Connection.ClientSecret = null;
        options.Providers["TEST"] = options.Providers["test"];
        Assert.Throws<InvalidOperationException>(() => new ModelRegistry(options));
    }

    [Fact]
    public void IndependentTokenCeilingsMayOverlapWithinTheContextWindow()
    {
        var options = Options(); options.Providers["test"].Models["model"].Metadata.ContextWindowTokens = 10000;
        Assert.Single(new ModelRegistry(options).Models);
    }

    [Fact]
    public void EditorSetupReservesAPracticalOutputBudgetAndEnablesFileEditing()
    {
        var options = Options();
        var metadata = options.Providers["test"].Models["model"].Metadata;
        metadata.ContextWindowTokens = 128000;
        metadata.MaxInputTokens = 120000;
        metadata.MaxOutputTokens = 120000;
        var setup = ProxyApplication.Setup(new ModelRegistry(options), new("127.0.0.1:5087"));
        var model = setup["configuration"]![0]!["models"]![0]!;
        Assert.Equal("customendpoint", setup["configuration"]![0]!["vendor"]!.GetValue<string>());
        Assert.Equal("chat-completions", setup["configuration"]![0]!["apiType"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:5087/v1/chat/completions", model["url"]!.GetValue<string>());
        Assert.True(model["toolCalling"]!.GetValue<bool>());
        Assert.Equal(128000, model["contextWindow"]!.GetValue<int>());
        Assert.Equal(8192, model["maxOutputTokens"]!.GetValue<int>());
        Assert.Contains("find-replace", model["editTools"]!.AsArray().Select(v => v!.GetValue<string>()));
        options.Providers["test"].Connection.RequestPolicy.MaxOutputTokensCap = 2000;
        setup = ProxyApplication.Setup(new ModelRegistry(options), new("localhost:5087"));
        Assert.Equal(2000, setup["configuration"]![0]!["models"]![0]!["maxOutputTokens"]!.GetValue<int>());
        metadata.Capabilities.SupportsTools = false;
        setup = ProxyApplication.Setup(new ModelRegistry(options), new("localhost:5087"));
        Assert.Null(setup["configuration"]![0]!["models"]![0]!["editTools"]);
    }

    [Fact]
    public void CapturesAreTruncatedAndBudgetsEvictWithoutHoldingLiveCalls()
    {
        var options = Options(); options.Capture = new() { MaxCalls = 2, MaxBodyBytes = 16, MaxTotalBytes = 1024 };
        var route = new ModelRegistry(options).Models.Single();
        var store = new TrafficStore(options, new(options));
        var first = store.Start(route, Encoding.UTF8.GetBytes(new string('x', 40)));
        Assert.True(store.Detail(first)!.Bodies["clientRequest"].Truncated);
        Assert.Equal(16, store.Detail(first)!.Bodies["clientRequest"].Text.Length);
        store.Start(route, []); store.Start(route, []);
        Assert.Null(store.Detail(first));
        store.Append(first, "clientResponse", Encoding.UTF8.GetBytes("ignored"));
        Assert.Equal(2, store.Snapshot().Calls.Length);
        store.Clear(); Assert.Empty(store.Snapshot().Calls);
        options.Capture.MaxTotalBytes = 10;
        var evicted = store.Start(route, Encoding.UTF8.GetBytes("elevenbytes"));
        Assert.Null(store.Detail(evicted));
    }

    [Fact]
    public void CredentialsSplitAcrossReadsAndJsonFieldsAreRedacted()
    {
        var options = Options();
        options.Providers["test"].Authentication = ProxyAuthentication.ApiKey;
        options.Providers["test"].Connection.ApiKey = "secret-value-123";
        var route = new ModelRegistry(options).Models.Single();
        var store = new TrafficStore(options, new(options));
        var id = store.Start(route, []);
        store.AddCredential(id, "dynamic-token-456");
        store.Append(id, "upstreamResponse", Encoding.UTF8.GetBytes("data: {\"text\":\"secret-val"));
        Assert.DoesNotContain("secret-val", store.Detail(id)!.Bodies["upstreamResponse"].Text);
        store.Append(id, "upstreamResponse", Encoding.UTF8.GetBytes("ue-123 dynamic-token-456\",\"client_secret\":\"other-secret\"}\n\n"));
        var captured = store.Detail(id)!.Bodies["upstreamResponse"].Text;
        Assert.DoesNotContain("secret-value", captured); Assert.DoesNotContain("dynamic-token", captured); Assert.DoesNotContain("other-secret", captured);
        Assert.Contains("[REDACTED]", captured);
    }

    [Fact]
    public async Task SlowSubscribersCoalesceChangesAndReconnectReceivesCurrentVersion()
    {
        var options = Options(); var route = new ModelRegistry(options).Models.Single();
        var store = new TrafficStore(options, new(options));
        using var subscriber = store.Subscribe();
        var id = store.Start(route, []);
        for (var i = 0; i < 1000; i++) store.Append(id, "clientResponse", "x"u8);
        Assert.Equal(store.Snapshot().Version, await subscriber.Reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.False(subscriber.Reader.TryRead(out _));
        using var reconnected = store.Subscribe();
        Assert.Equal(store.Snapshot().Version, await reconnected.Reader.ReadAsync(TestContext.Current.CancellationToken));
    }
}
