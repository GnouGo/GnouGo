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
	public async Task SuggestChangeAsync_ReadsContextAndDelegatesToAssistant()
	{
		var settings = CreateSettings();
		var assistant = new CapturingAssistantClient();
		var tools = new CodeTools(CreateService(settings), assistant, NullLogger<CodeTools>.Instance);

		var result = await tools.SuggestChangeAsync(_root, "Add a greeting method.", "[\"src/Program.cs\"]");

		var suggestion = Assert.IsType<CodeSuggestionResult>(result);
		Assert.Equal("Add a greeting method.", suggestion.Task);
		Assert.Equal("fake suggestion", suggestion.Suggestion);
		Assert.Contains(suggestion.ProgressEvents, e => e.Kind == "completed" && e.Message == "fake suggestion completed");
		Assert.Equal(_root, assistant.ProjectRoot);
		Assert.Null(assistant.ProviderName);
		var file = Assert.Single(assistant.ContextFiles);
		Assert.Equal("src\\Program.cs", file.Path.Replace('/', '\\'));
		Assert.Contains("Hello", file.Content, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SuggestChangeAsync_ForwardsOptionalProviderToAssistant()
	{
		var settings = CreateSettings();
		var assistant = new CapturingAssistantClient();
		var tools = new CodeTools(CreateService(settings), assistant, NullLogger<CodeTools>.Instance);

		var result = await tools.SuggestChangeAsync(_root, "Use a custom provider.", provider: "CustomCopilot");

		var suggestion = Assert.IsType<CodeSuggestionResult>(result);
		Assert.Equal("fake suggestion", suggestion.Suggestion);
		Assert.Equal("CustomCopilot", assistant.ProviderName);
	}

	[Fact]
	public async Task AgentEditAsync_ReadsContextAndDelegatesToAssistantInEditMode()
	{
		var settings = CreateSettings();
		settings.AllowWrites = true;
		var assistant = new CapturingAssistantClient();
		var tools = new CodeTools(CreateService(settings), assistant, NullLogger<CodeTools>.Instance);

		var result = await tools.AgentEditAsync(_root, "Implement the change.", "[\"src/Program.cs\"]", provider: "CustomCopilot");

		var edit = Assert.IsType<CodeAgentEditResult>(result);
		Assert.Equal("Implement the change.", edit.Task);
		Assert.Equal("fake edit summary", edit.Summary);
		Assert.Contains(edit.ProgressEvents, e => e.Kind == "file_modified" && e.File == "src/Program.cs");
		Assert.Equal(_root, assistant.ProjectRoot);
		Assert.Equal("CustomCopilot", assistant.ProviderName);
		Assert.True(assistant.AgentEditCalled);
		var file = Assert.Single(assistant.ContextFiles);
		Assert.Equal("src\\Program.cs", file.Path.Replace('/', '\\'));
	}

	[Fact]
	public void BuildClientOptions_ConfiguresSessionFsWhenEnabled()
	{
		var settings = CreateSettings();

		var options = GitHubCopilotCodeClient.BuildClientOptions(settings, _root, "ghp_test-token", enableSessionFs: true);

		Assert.NotNull(options.SessionFs);
		Assert.Equal(_root, options.SessionFs.InitialCwd);
		Assert.Equal(".gnougo/copilot-session-state", options.SessionFs.SessionStatePath);
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
		var assistant = new CapturingAssistantClient();
		var tools = new CodeTools(projectService, assistant, NullLogger<CodeTools>.Instance);

		var result = await tools.SuggestChangeAsync("workspace/oidc-client", "Plan this change.", "[\"src/Program.cs\"]");

		var suggestion = Assert.IsType<CodeSuggestionResult>(result);
		Assert.Equal("fake suggestion", suggestion.Suggestion);
		Assert.Equal(expectedProjectRoot, assistant.ProjectRoot);
		var file = Assert.Single(assistant.ContextFiles);
		Assert.Equal("src\\Program.cs", file.Path.Replace('/', '\\'));
		Assert.Contains("Desktop workspace", file.Content, StringComparison.Ordinal);
	}

	[Fact]
	public void BuildClientOptions_UsesExplicitGitHubTokenAndProjectRoot()
	{
		var settings = CreateSettings();
		settings.Copilot.UseLoggedInUser = false;
		settings.Copilot.LogLevel = "debug";
		settings.Copilot.Mode = "agent";

		var options = GitHubCopilotCodeClient.BuildClientOptions(settings, _root, "ghp_test-token");

		Assert.Equal(_root, options.Cwd);
		Assert.Equal("ghp_test-token", options.GitHubToken);
		Assert.False(options.UseLoggedInUser);
		Assert.Equal("debug", options.LogLevel);
		Assert.Equal("agent", GitHubCopilotCodeClient.NormalizeMessageMode(settings.Copilot.Mode));
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
		Assert.Equal(expected, GitHubCopilotCodeClient.NormalizeMessageMode(configured));
	}

	[Fact]
	public void NormalizeMessageMode_RejectsUnsupportedValue()
	{
		var ex = Assert.Throws<InvalidOperationException>(() => GitHubCopilotCodeClient.NormalizeMessageMode("review"));

		Assert.Contains("Unsupported Copilot mode", ex.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("ask, edit, agent", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void BuildClientOptions_AllowsMissingTokenWhenUseLoggedInUserIsEnabled()
	{
		var settings = CreateSettings();
		settings.Copilot.UseLoggedInUser = true;

		var options = GitHubCopilotCodeClient.BuildClientOptions(settings, _root, token: null);

		Assert.Equal(_root, options.Cwd);
		Assert.True(options.UseLoggedInUser);
		Assert.True(string.IsNullOrWhiteSpace(options.GitHubToken));
	}

	[Fact]
	public void BuildClientOptions_RequiresTokenWhenUseLoggedInUserIsDisabled()
	{
		var settings = CreateSettings();
		settings.Copilot.UseLoggedInUser = false;

		var ex = Assert.Throws<ArgumentException>(() =>
			GitHubCopilotCodeClient.BuildClientOptions(settings, _root, token: null));

		Assert.Contains("GitHub token is required", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData(null, "warning")]
	[InlineData("", "warning")]
	[InlineData("warn", "warning")]
	[InlineData("WARNING", "warning")]
	[InlineData("trace", "all")]
	[InlineData("debug", "debug")]
	[InlineData("default", "default")]
	public void NormalizeLogLevel_MapsConfiguredValueToCopilotCliValue(string? configured, string expected)
	{
		Assert.Equal(expected, GitHubCopilotCodeClient.NormalizeLogLevel(configured));
	}

	[Fact]
	public void NormalizeLogLevel_RejectsUnsupportedValue()
	{
		var ex = Assert.Throws<InvalidOperationException>(() => GitHubCopilotCodeClient.NormalizeLogLevel("verbose"));

		Assert.Contains("Unsupported Copilot log level", ex.Message, StringComparison.OrdinalIgnoreCase);
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

		var headers = GitHubCopilotCodeClient.BuildRequestHeaders(settings, accessor);

		Assert.NotNull(headers);
		Assert.Equal(context.TraceParent, headers["traceparent"]);
		Assert.Equal(context.TraceId, headers["x-gnougo-trace-id"]);
		Assert.Equal(context.ParentSpanId, headers["x-gnougo-parent-span-id"]);
		Assert.Equal(context.CorrelationId, headers["x-gnougo-correlation-id"]);
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

			var env = GitHubCopilotCodeClient.BuildClientEnvironment(settings);

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
	public void CopilotSdkProgressMapper_MapsToolProgressToStableGnOuGoProgressEvent()
	{
		var mapped = CopilotSdkProgressEventMapper.TryMap(
			"tool.execution_progress",
			new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
			{
				["ProgressMessage"] = "Reading project files.",
				["McpServerName"] = "filesystem",
				["McpToolName"] = "read_file"
			},
			"2026-05-20T10:15:30Z",
			out var progressEvent);

		Assert.True(mapped);
		Assert.Equal("sdk_tool_execution_progress", progressEvent.Kind);
		Assert.Equal("thinking", progressEvent.Level);
		Assert.Equal("Reading project files.", progressEvent.Message);
		Assert.Equal(DateTimeOffset.Parse("2026-05-20T10:15:30Z").ToUniversalTime(), progressEvent.Timestamp);
		Assert.Null(progressEvent.File);
	}

	[Fact]
	public void CopilotSdkProgressMapper_DoesNotExposeReasoningOrStreamingDeltas()
	{
		var reasoningDeltaMapped = CopilotSdkProgressEventMapper.TryMap(
			"assistant.reasoning_delta",
			new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
			{
				["DeltaContent"] = "private incremental reasoning"
			},
			"2026-05-20T10:15:30Z",
			out _);

		var reasoningMapped = CopilotSdkProgressEventMapper.TryMap(
			"assistant.reasoning",
			new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
			{
				["Content"] = "private complete reasoning"
			},
			"2026-05-20T10:15:30Z",
			out var progressEvent);

		Assert.False(reasoningDeltaMapped);
		Assert.True(reasoningMapped);
		Assert.Equal("sdk_assistant_reasoning", progressEvent.Kind);
		Assert.Equal("Copilot produced a reasoning milestone.", progressEvent.Message);
		Assert.DoesNotContain("private", progressEvent.Message, StringComparison.OrdinalIgnoreCase);
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

	private sealed class CapturingAssistantClient : ICodeAssistantClient
	{
		public string? ProjectRoot { get; private set; }
		public string? ProviderName { get; private set; }
		public bool AgentEditCalled { get; private set; }
		public IReadOnlyList<CodeFileContent> ContextFiles { get; private set; } = [];

		public Task<CodeSuggestionResult> SuggestChangeAsync(
			string task,
			string projectRoot,
			IReadOnlyList<CodeFileContent> contextFiles,
			string? providerName,
			CancellationToken cancellationToken)
		{
			ProjectRoot = projectRoot;
			ProviderName = providerName;
			ContextFiles = contextFiles;
			return Task.FromResult(new CodeSuggestionResult(
				task,
				contextFiles.Select(static file => file.Path).ToArray(),
				"fake suggestion",
				"fake-model",
				null,
				[new CodeProgressEvent("completed", "info", "fake suggestion completed", DateTimeOffset.UtcNow)]));
		}

		public Task<CodeAgentEditResult> AgentEditAsync(
			string task,
			string projectRoot,
			IReadOnlyList<CodeFileContent> contextFiles,
			string? providerName,
			CancellationToken cancellationToken)
		{
			AgentEditCalled = true;
			ProjectRoot = projectRoot;
			ProviderName = providerName;
			ContextFiles = contextFiles;
			return Task.FromResult(new CodeAgentEditResult(
				task,
				contextFiles.Select(static file => file.Path).ToArray(),
				["src/Program.cs"],
				"fake edit summary",
				"fake-model",
				null,
				[new CodeProgressEvent("file_modified", "info", "Modified src/Program.cs.", DateTimeOffset.UtcNow, "src/Program.cs")]));
		}
	}
}

