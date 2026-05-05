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
		var tools = new CodeTools(CreateService(settings), CreateGitService(settings), assistant, NullLogger<CodeTools>.Instance);

		var result = await tools.SuggestChangeAsync(_root, "Add a greeting method.", "[\"src/Program.cs\"]");

		var suggestion = Assert.IsType<CodeSuggestionResult>(result);
		Assert.Equal("Add a greeting method.", suggestion.Task);
		Assert.Equal("fake suggestion", suggestion.Suggestion);
		Assert.Equal(_root, assistant.ProjectRoot);
		var file = Assert.Single(assistant.ContextFiles);
		Assert.Equal("src\\Program.cs", file.Path.Replace('/', '\\'));
		Assert.Contains("Hello", file.Content, StringComparison.Ordinal);
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
		}
		finally
		{
			Environment.SetEnvironmentVariable("GNouGo__TraceParent", previousTraceParent);
			Environment.SetEnvironmentVariable("GNouGo__TraceId", previousTraceId);
			Environment.SetEnvironmentVariable("GNouGo__SpanId", previousSpanId);
		}
	}

	private CodeProjectService CreateService(CodeServerSettings settings)
	{
		var policy = new CodePolicy(settings, _root);
		return new CodeProjectService(policy, Options.Create(settings));
	}

	private GitRepositoryService CreateGitService(CodeServerSettings settings)
	{
		var policy = new CodePolicy(settings, _root);
		return new GitRepositoryService(policy, Options.Create(settings));
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
			Mode = "plan",
			ReasoningEffort = "high",
			RequestTimeoutSeconds = 30
		}
	};

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); }
		catch { }
	}

	private sealed class CapturingAssistantClient : ICodeAssistantClient
	{
		public string? ProjectRoot { get; private set; }
		public IReadOnlyList<CodeFileContent> ContextFiles { get; private set; } = [];

		public Task<CodeSuggestionResult> SuggestChangeAsync(
			string task,
			string projectRoot,
			IReadOnlyList<CodeFileContent> contextFiles,
			CancellationToken cancellationToken)
		{
			ProjectRoot = projectRoot;
			ContextFiles = contextFiles;
			return Task.FromResult(new CodeSuggestionResult(task, contextFiles.Select(static file => file.Path).ToArray(), "fake suggestion", "fake-model", null));
		}
	}
}

