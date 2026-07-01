using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowPlanBadWorkflowCorpusTests
{
    private static readonly string CorpusDirectory = FindCorpusDirectory();
    private static readonly JsonObject Corpus = LoadCorpus();

    public static IEnumerable<object[]> AllCases()
        => LoadCases().Select(testCase => new object[] { testCase });

    public static IEnumerable<object[]> DryRunCases()
        => LoadCases()
            .Where(testCase => string.Equals(testCase.ExpectedStage, "dry_run", StringComparison.Ordinal))
            .Select(testCase => new object[] { testCase });

    public static IEnumerable<object[]> DocumentedRuntimeFailures()
        => LoadCases()
            .Where(testCase =>
                string.Equals(testCase.ExpectedStage, "runtime", StringComparison.Ordinal)
                && string.Equals(testCase.CurrentCoverage, "documented_runtime_failure", StringComparison.Ordinal))
            .Select(testCase => new object[] { testCase });

    [Theory]
    [MemberData(nameof(AllCases))]
    public void BadWorkflowCorpus_FixturesParseAndCompile(BadWorkflowCase testCase)
    {
        var yaml = ReadYaml(testCase);

        Assert.False(string.IsNullOrWhiteSpace(testCase.Id));
        Assert.False(string.IsNullOrWhiteSpace(testCase.Title));
        Assert.False(string.IsNullOrWhiteSpace(testCase.Source));
        Assert.DoesNotContain("```", yaml, StringComparison.Ordinal);

        var document = WorkflowParser.Parse(yaml);
        var compiled = new WorkflowCompiler().Compile(document);

        Assert.True(compiled.Workflows.ContainsKey(compiled.Entrypoint!));
    }

    [Theory]
    [MemberData(nameof(DryRunCases))]
    public async Task BadWorkflowCorpus_DryRunCasesStayRejected(BadWorkflowCase testCase)
    {
        var document = WorkflowParser.Parse(ReadYaml(testCase));

        var ex = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            WorkflowPlanDryRunValidator.ValidateAsync(
                document,
                mcpClientFactory: null,
                NullLogger.Instance,
                CancellationToken.None));

        foreach (var expected in testCase.ExpectedErrorContains)
            Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(DocumentedRuntimeFailures))]
    public void BadWorkflowCorpus_RuntimeFailuresStayDocumented(BadWorkflowCase testCase)
    {
        var yaml = ReadYaml(testCase);
        var document = WorkflowParser.Parse(yaml);

        Assert.Contains("mcp.call", yaml, StringComparison.Ordinal);
        Assert.NotEmpty(testCase.ReportedErrorContains);
        Assert.Contains(testCase.Notes, note => note.Contains("without adding server-specific Flow heuristics", StringComparison.Ordinal));
        Assert.Contains(
            document.Workflows.Values.SelectMany(workflow => workflow.Steps),
            step => step.Type == "mcp.call");
    }

    [Fact]
    public void BadWorkflowCorpus_IncludesUserReportedCopilotProjectRootFailure()
    {
        var testCase = LoadCases().SingleOrDefault(candidate =>
            string.Equals(candidate.Id, "copilot-suggest-change-nonexistent-project-root", StringComparison.Ordinal));

        Assert.NotNull(testCase);
        Assert.Equal("runtime", testCase.ExpectedStage);
        Assert.Equal("documented_runtime_failure", testCase.CurrentCoverage);
        Assert.Contains(testCase.ReportedErrorContains, value => value.Contains("code_suggest_change", StringComparison.Ordinal));
        Assert.Contains(testCase.ReportedErrorContains, value => value.Contains("does not exist", StringComparison.Ordinal));
    }

    private static JsonObject LoadCorpus()
    {
        var corpusPath = Path.Combine(CorpusDirectory, "corpus.json");
        var parsed = JsonNode.Parse(File.ReadAllText(corpusPath)) as JsonObject;
        return parsed ?? throw new InvalidOperationException($"Bad workflow corpus '{corpusPath}' is not a JSON object.");
    }

    private static IReadOnlyList<BadWorkflowCase> LoadCases()
    {
        var cases = Corpus["cases"] as JsonArray
            ?? throw new InvalidOperationException("Bad workflow corpus is missing a cases array.");

        var result = new List<BadWorkflowCase>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in cases.OfType<JsonObject>())
        {
            var testCase = BadWorkflowCase.FromJson(node);
            Assert.True(seenIds.Add(testCase.Id), $"Duplicate bad workflow corpus id '{testCase.Id}'.");
            result.Add(testCase);
        }

        return result;
    }

    private static string ReadYaml(BadWorkflowCase testCase)
    {
        var path = Path.Combine(CorpusDirectory, testCase.Yaml);
        Assert.True(File.Exists(path), $"Missing bad workflow corpus YAML fixture '{path}'.");
        return File.ReadAllText(path);
    }

    private static string FindCorpusDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "GnOuGo.Flow.Tests", "Runtime", "BadWorkflowCorpus");
            if (Directory.Exists(candidate))
                return candidate;

            candidate = Path.Combine(directory.FullName, "Runtime", "BadWorkflowCorpus");
            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate tests/GnOuGo.Flow.Tests/Runtime/BadWorkflowCorpus.");
    }

    public sealed record BadWorkflowCase(
        string Id,
        string Title,
        string Yaml,
        string Source,
        string ExpectedStage,
        string CurrentCoverage,
        IReadOnlyList<string> ExpectedErrorContains,
        IReadOnlyList<string> ReportedErrorContains,
        IReadOnlyList<string> Notes)
    {
        public override string ToString() => Id;

        public static BadWorkflowCase FromJson(JsonObject obj) => new(
            ReadRequiredString(obj, "id"),
            ReadRequiredString(obj, "title"),
            ReadRequiredString(obj, "yaml"),
            ReadRequiredString(obj, "source"),
            ReadRequiredString(obj, "expected_stage"),
            ReadRequiredString(obj, "current_coverage"),
            ReadStringArray(obj, "expected_error_contains"),
            ReadStringArray(obj, "reported_error_contains"),
            ReadStringArray(obj, "notes"));

        private static string ReadRequiredString(JsonObject obj, string name)
        {
            var value = obj[name]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(value)
                ? throw new InvalidOperationException($"Bad workflow corpus case is missing '{name}'.")
                : value;
        }

        private static IReadOnlyList<string> ReadStringArray(JsonObject obj, string name)
        {
            if (obj[name] is not JsonArray array)
                return Array.Empty<string>();

            return array
                .OfType<JsonValue>()
                .Select(value => value.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }
    }
}
