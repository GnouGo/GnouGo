using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class CodeToolsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-code-tools-tests-" + Guid.NewGuid().ToString("N"));

	public CodeToolsTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, "src"));
		File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "Console.WriteLine(\"Hello\");\n");
	}

	[Fact]
	public void ParseContextFiles_RemovesEmptyAndDuplicateValues()
	{
		var files = CodeTools.ParseContextFiles("[\"src/Program.cs\", \"\", \"src/Program.cs\", \"README.md\"]");

		Assert.Equal(["src/Program.cs", "README.md"], files);
	}

	[Fact]
	public void CodeMcpProjectRootParameters_AreRequiredStrings()
	{
		var parameters = typeof(CodeTools)
			.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
			.SelectMany(method => method.GetParameters())
			.Where(parameter => string.Equals(parameter.Name, "projectRoot", StringComparison.Ordinal))
			.ToArray();

		Assert.NotEmpty(parameters);
		Assert.All(parameters, parameter =>
		{
			Assert.Equal(typeof(string), parameter.ParameterType);
			Assert.False(parameter.HasDefaultValue);
		});
	}

	[Fact]
	public async Task SuggestChangeAsync_ReadsContextAndDelegatesToAssistant()
	{
		var settings = CreateSettings();
		var assistant = new CopilotTestHost(settings, _root);
		var tools = new CodeTools(CreateService(settings), assistant.Service, NullLogger<CodeTools>.Instance, assistant.Human);

		var result = await tools.SuggestChangeAsync(".", "Add a greeting method.", "[\"src/Program.cs\"]", tenantId: "test", cancellationToken: TestContext.Current.CancellationToken);

		var suggestion = Assert.IsType<CodeSuggestionResult>(result);
		Assert.Equal("Add a greeting method.", suggestion.Task);
		Assert.Equal("fake suggestion", suggestion.Suggestion);
		Assert.Contains(suggestion.ProgressEvents, e => e.Kind == "completed" && e.Message == "fake suggestion completed");
		Assert.Equal(_root, assistant.ProjectRoot);
		Assert.Null(assistant.ProviderName);
		Assert.Single(suggestion.Files);
		Assert.Contains("src/Program.cs", assistant.LastRequest!.Prompt.Replace('\\', '/'));
		Assert.Contains("Hello", assistant.LastRequest.Prompt);
	}

	[Fact]
	public async Task SuggestChangeAsync_ForwardsOptionalProviderToAssistant()
	{
		var settings = CreateSettings();
		var assistant = new CopilotTestHost(settings, _root);
		var tools = new CodeTools(CreateService(settings), assistant.Service, NullLogger<CodeTools>.Instance, assistant.Human);

		var result = await tools.SuggestChangeAsync(".", "Use a custom provider.", provider: "CustomCopilot", tenantId: "test", cancellationToken: TestContext.Current.CancellationToken);

		var suggestion = Assert.IsType<CodeSuggestionResult>(result);
		Assert.Equal("fake suggestion", suggestion.Suggestion);
		Assert.Equal("CustomCopilot", assistant.ProviderName);
	}

	[Fact]
	public async Task SuggestChangeAsync_WhenInputJsonFails_ReturnsStructuredFailure()
	{
		var settings = CreateSettings();
		var assistant = new CopilotTestHost(settings, _root);
		var tools = new CodeTools(CreateService(settings), assistant.Service, NullLogger<CodeTools>.Instance, assistant.Human);

		var result = await tools.SuggestChangeAsync(".", "Plan this change.", "{", tenantId: "test", cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(result.Success);
		Assert.False(result.Ok);
		Assert.Equal("INVALID_INPUT", result.ErrorCode);
		Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
		Assert.Empty(result.ProgressEvents);
	}

	[Fact]
	public async Task AgentEditAsync_ReadsContextAndDelegatesToAssistantInEditMode()
	{
		var settings = CreateSettings();
		settings.AllowWrites = true;
		var assistant = new CopilotTestHost(settings, _root);
		var tools = new CodeTools(CreateService(settings), assistant.Service, NullLogger<CodeTools>.Instance, assistant.Human);

		var result = await tools.AgentEditAsync(".", "Implement the change.", "[\"src/Program.cs\"]", provider: "CustomCopilot", tenantId: "test", cancellationToken: TestContext.Current.CancellationToken);

		var edit = Assert.IsType<CodeAgentEditResult>(result);
		Assert.Equal("Implement the change.", edit.Task);
		Assert.Equal("fake edit summary", edit.Summary);
		Assert.Contains("fake edit summary", edit.Output);
		Assert.Contains(edit.ProgressEvents, e => e.Kind == "file_modified" && e.File == "src/Program.cs");
		Assert.Equal(_root, assistant.ProjectRoot);
		Assert.Equal("CustomCopilot", assistant.ProviderName);
		Assert.True(assistant.AgentEditCalled);
		Assert.Single(edit.ContextFiles);
		Assert.Contains("Hello", assistant.LastRequest!.Prompt);
		Assert.Equal(0, assistant.DeleteCount);
        Assert.Equal(1, assistant.DisposedSessions);
        Assert.Throws<ObjectDisposedException>(() => assistant.Configuration!.FileSystem!.ValidateRead("src/Program.cs"));
	}

	[Fact]
	public void BuildRuntimeConfiguration_ConfiguresSessionFsWhenEnabled()
	{
		var settings = CreateSettings();

		var options = CopilotMcpConfiguration.BuildRuntimeConfiguration(settings, _root, "ghp_test-token");

		Assert.True(options.UseSessionFileSystem);
		Assert.Equal(_root, options.WorkingDirectory);
	}

	[Fact]
	public async Task SuggestChangeAsync_ResolvesRelativeProjectRootUnderDefaultWorkingDirectory()
	{
		var desktop = Path.Combine(_root, "Desktop");
		var expectedProjectRoot = Path.GetFullPath(Path.Combine(desktop, "GnOuGo", "workspace", "oidc-client"));
		Directory.CreateDirectory(Path.Combine(expectedProjectRoot, "src"));
		File.WriteAllText(Path.Combine(expectedProjectRoot, "src", "Program.cs"), "Console.WriteLine(\"Desktop workspace\");\n");
		var settings = CreateSettings();
		settings.DefaultWorkingDirectory = "GnOuGo";
		settings.AllowedWorkingRoots = [];
		var policy = new CodePolicy(settings, _root, desktop);
		var projectService = new CodeProjectService(policy, Options.Create(settings));
		var assistant = new CopilotTestHost(settings, _root, policy);
		var tools = new CodeTools(projectService, assistant.Service, NullLogger<CodeTools>.Instance, assistant.Human);

		var result = await tools.SuggestChangeAsync("workspace/oidc-client", "Plan this change.", "[\"src/Program.cs\"]", tenantId: "test", cancellationToken: TestContext.Current.CancellationToken);

		var suggestion = Assert.IsType<CodeSuggestionResult>(result);
		Assert.Equal("fake suggestion", suggestion.Suggestion);
		Assert.Equal(expectedProjectRoot, assistant.ProjectRoot);
		Assert.Single(suggestion.Files);
		Assert.Contains("Desktop workspace", assistant.LastRequest!.Prompt);
	}

	[Fact]
	public void BuildRuntimeConfiguration_UsesExplicitGitHubTokenAndProjectRoot()
	{
		var settings = CreateSettings();
		settings.Copilot.UseLoggedInUser = false;
		settings.Copilot.LogLevel = "debug";
		settings.Copilot.Mode = "agent";

		var options = CopilotMcpConfiguration.BuildRuntimeConfiguration(settings, _root, "ghp_test-token");

		Assert.Equal(_root, options.WorkingDirectory);
		Assert.Equal("ghp_test-token", options.GitHubToken);
		Assert.Equal<bool?>(false, options.UseLoggedInUser);
		Assert.Equal("debug", options.LogLevel?.ToString());
		Assert.Equal("agent", CopilotMcpConfiguration.NormalizeMessageMode(settings.Copilot.Mode));
		Assert.NotNull(options.Telemetry);
		Assert.Equal("http://127.0.0.1:4317", options.Telemetry.OtlpEndpoint);
	}

	[Theory]
	[InlineData(null, "ask")]
	[InlineData("", "ask")]
	[InlineData("Ask", "ask")]
	[InlineData("ask", "ask")]
	[InlineData("Agent", "agent")]
	[InlineData("agent", "agent")]
	[InlineData("Edit", "edit")]
	[InlineData("plan", "ask")]
	public void NormalizeMessageMode_MapsConfiguredValueToCopilotCliMode(string? configured, string expected)
	{
		Assert.Equal(expected, CopilotMcpConfiguration.NormalizeMessageMode(configured));
	}

	[Fact]
	public void NormalizeMessageMode_RejectsUnsupportedValue()
	{
		var ex = Assert.Throws<InvalidOperationException>(() => CopilotMcpConfiguration.NormalizeMessageMode("review"));

		Assert.Contains("Unsupported Copilot mode", ex.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("ask, edit, agent", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void BuildRuntimeConfiguration_AllowsMissingTokenWhenUseLoggedInUserIsEnabled()
	{
		var settings = CreateSettings();
		settings.Copilot.UseLoggedInUser = true;

		var options = CopilotMcpConfiguration.BuildRuntimeConfiguration(settings, _root, token: null);

		Assert.Equal(_root, options.WorkingDirectory);
		Assert.Equal<bool?>(true, options.UseLoggedInUser);
		Assert.True(string.IsNullOrWhiteSpace(options.GitHubToken));
	}

	[Fact]
	public void BuildRuntimeConfiguration_PreservesByokConfigurationWithoutGitHubToken()
	{
		var settings = CreateSettings();
		settings.Copilot.UseLoggedInUser = false;
		var options = CopilotMcpConfiguration.BuildRuntimeConfiguration(settings, _root, null);
		Assert.False(options.UseLoggedInUser);
		Assert.Null(options.GitHubToken);
	}

	[Fact]
	public void BuildRequestHeaders_ForwardsMcpTraceContextToCopilotSdk()
	{
		var settings = CreateSettings();
		var accessor = new CodeMcpTraceContextAccessor();
		var context = new CodeMcpTraceContext(
			TraceParent: "00-00112233445566778899aabbccddeeff-0123456789abcdef-01",
			TraceState: "tenant=local",
			TraceId: "00112233445566778899aabbccddeeff",
			SpanId: "0123456789abcdef",
			ParentSpanId: "0123456789abcdef",
			CorrelationId: "corr-1",
			RunId: "run-1",
			StepId: "step-1",
			StepType: "mcp.call",
			McpServer: "GnOuGo.GithubCopilot.Mcp",
			McpMethod: "code_suggest_change",
			McpKind: "tool");

		using var _ = accessor.Push(context);

		var headers = CopilotMcpConfiguration.BuildRequestHeaders(settings, accessor);

		Assert.NotNull(headers);
		Assert.Equal(context.TraceParent, headers["traceparent"]);
		Assert.Equal(context.TraceId, headers["x-gnougo-trace-id"]);
		Assert.Equal(context.ParentSpanId, headers["x-gnougo-parent-span-id"]);
		Assert.Equal(context.CorrelationId, headers["x-gnougo-correlation-id"]);
	}

	[Fact]
	public void Capture_WithCurrentActivity_PreservesExplicitBoundaryContext()
	{
		var accessor = new CodeMcpTraceContextAccessor();
		var requestContext = new CodeMcpTraceContext(
			TraceParent: null,
			TraceState: null,
			TraceId: null,
			SpanId: null,
			ParentSpanId: null,
			CorrelationId: "corr-context",
			RunId: "run-context",
			StepId: "step-context",
			StepType: "mcp.call",
			McpServer: "specialized",
			McpMethod: "analyze",
			McpKind: "tool")
		{
			TenantId = "tenant-context",
			Repository = "owner/resource",
			PullRequestNumber = 12,
			HeadSha = "abcdef"
		};
		using var scope = accessor.Push(requestContext);
		using var activity = new Activity("specialized-call").Start();

		var captured = CodeMcpTraceContext.Capture(accessor);

		Assert.NotNull(captured);
		Assert.Equal(activity.TraceId.ToString(), captured.TraceId);
		Assert.Equal("corr-context", captured.CorrelationId);
		Assert.Equal("tenant-context", captured.TenantId);
		Assert.Equal("owner/resource", captured.Repository);
		Assert.Equal(12, captured.PullRequestNumber);
		Assert.Equal("abcdef", captured.HeadSha);
	}

	[Fact]
	public void McpMetadata_UsesNestedGenericContextAtSpecializedBoundary()
	{
		var meta = new JsonObject
		{
			["gnougo"] = new JsonObject
			{
				["correlationId"] = "corr-nested",
				["executionId"] = "execution-1",
				["agentId"] = "agent-1",
				["agentName"] = "Reviewer",
				["context"] = new JsonObject
				{
					["repository"] = "owner/resource",
					["pullRequestNumber"] = 7,
					["headSha"] = "123456"
				}
			}
		};

		var context = CodeMcpTraceContext.FromMcpMeta(meta);

		Assert.NotNull(context);
		Assert.Equal("owner/resource", context.Repository);
		Assert.Equal(7, context.PullRequestNumber);
		Assert.Equal("123456", context.HeadSha);
		Assert.Equal("execution-1", context.ExecutionId);
		Assert.Equal("agent-1", context.AgentId);
		Assert.Equal("Reviewer", context.AgentName);
		var roundTripGnougo = Assert.IsType<JsonObject>(context.ToMcpMeta()["gnougo"]);
		var roundTripContext = Assert.IsType<JsonObject>(roundTripGnougo["context"]);
		Assert.Equal("owner/resource", roundTripContext["repository"]!.GetValue<string>());
		Assert.Equal("execution-1", roundTripGnougo["executionId"]!.GetValue<string>());
		Assert.Equal("agent-1", roundTripGnougo["agentId"]!.GetValue<string>());
		Assert.False(roundTripGnougo.ContainsKey("repository"));
		Assert.False(roundTripGnougo.ContainsKey("pullRequestNumber"));
		Assert.False(roundTripGnougo.ContainsKey("headSha"));
	}

	[Fact]
	public void BuildClientEnvironment_ForwardsEnvironmentTraceContextToCopilotCli()
	{
		var settings = CreateSettings();
		var previousTraceParent = Environment.GetEnvironmentVariable("GNouGo__TraceParent");
		var previousTraceId = Environment.GetEnvironmentVariable("GNouGo__TraceId");
		var previousSpanId = Environment.GetEnvironmentVariable("GNouGo__SpanId");
		try
		{
			Environment.SetEnvironmentVariable("GNouGo__TraceParent", "00-11112222333344445555666677778888-9999aaaabbbbcccc-01");
			Environment.SetEnvironmentVariable("GNouGo__TraceId", "11112222333344445555666677778888");
			Environment.SetEnvironmentVariable("GNouGo__SpanId", "9999aaaabbbbcccc");

			var env = CopilotMcpConfiguration.BuildClientEnvironment(settings);

			Assert.NotNull(env);
			Assert.Equal("00-11112222333344445555666677778888-9999aaaabbbbcccc-01", env["TRACEPARENT"]);
			Assert.Equal("11112222333344445555666677778888", env["GNouGo__TraceId"]);
			Assert.Equal("http://127.0.0.1:4317", env["OTEL_EXPORTER_OTLP_ENDPOINT"]);
			AssertPreservedEnvironmentVariable(env, "PATH");
			if (OperatingSystem.IsWindows())
			{
				AssertPreservedEnvironmentVariable(env, "SystemRoot");
				AssertPreservedEnvironmentVariable(env, "WINDIR");
				AssertPreservedEnvironmentVariable(env, "TEMP");
				AssertPreservedEnvironmentVariable(env, "TMP");
			}
		}
		finally
		{
			Environment.SetEnvironmentVariable("GNouGo__TraceParent", previousTraceParent);
			Environment.SetEnvironmentVariable("GNouGo__TraceId", previousTraceId);
			Environment.SetEnvironmentVariable("GNouGo__SpanId", previousSpanId);
		}
	}

    [Fact]
    public async Task LegacyCalls_RequireTenantAndKeepSuggestionToolsDisabled()
    {
        var settings = CreateSettings();
        var host = new CopilotTestHost(settings, _root);
        var tools = new CodeTools(CreateService(settings), host.Service, NullLogger<CodeTools>.Instance, host.Human);
        var missing = await tools.SuggestChangeAsync(".", "Suggest", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(missing.Success);
        Assert.Null(host.Configuration);
        var result = await tools.SuggestChangeAsync(".", "Suggest", tenantId: "tenant", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal(GnOuGo.GithubCopilot.Core.CopilotPermissionMode.Deny, host.Configuration!.Request.PermissionMode);
        Assert.Empty(host.Configuration.Request.Configuration.AvailableTools!);
        Assert.NotNull(host.Configuration.FileSystem);
        Assert.Equal(0, host.DeleteCount);
        Assert.Equal(1, host.DisposedSessions);
        Assert.Throws<ObjectDisposedException>(() => host.Configuration!.FileSystem!.ValidateRead("src/Program.cs"));
    }

	private CodeProjectService CreateService(CodeServerSettings settings)
	{
		var policy = new CodePolicy(settings, _root);
		return new CodeProjectService(policy, Options.Create(settings));
	}


	private CodeServerSettings CreateSettings() => new()
	{
		DefaultWorkingDirectory = _root,
		AllowedWorkingRoots = [_root],
		AllowedExtensions = [".cs", ".md"],
		MaxFileSizeBytes = 1024 * 1024,
		MaxPromptCharacters = 24_000,
		AllowWrites = false,
		Copilot = new CodeCopilotSettings
		{
			ApiKey = "ghp_test-token",
			Model = "gpt-4.1",
			Mode = "ask",
			ReasoningEffort = "high",
			RequestTimeoutSeconds = 30
		}
	};

	private static void AssertPreservedEnvironmentVariable(IReadOnlyDictionary<string, string> env, string name)
	{
		var value = Environment.GetEnvironmentVariable(name);
		if (string.IsNullOrWhiteSpace(value))
			return;

		Assert.True(env.TryGetValue(name, out var actual), $"Expected Copilot CLI environment to preserve {name}.");
		Assert.Equal(value, actual);
	}

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

}
