using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Code.Mcp.Tests;

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

		var options = GitHubCopilotCodeClient.BuildClientOptions(settings, _root, "ghp_test-token");

		Assert.Equal(_root, options.Cwd);
		Assert.Equal("ghp_test-token", options.GitHubToken);
		Assert.False(options.UseLoggedInUser);
		Assert.Equal("debug", options.LogLevel);
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



