using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

/// <summary>Published-binary compatible checks; no tenant or provider session is admitted.</summary>
public sealed class CopilotListStdioTests
{
    [Fact]
    public async Task PublishedSchemasAndBoundaryRejectMalformedAndRemovedLists()
    {
        var root = Directory.CreateTempSubdirectory("copilot-list-stdio-").FullName;
        try
        {
            var executable = Environment.GetEnvironmentVariable("GNOUGO_COPILOT_LIST_SMOKE_EXECUTABLE")
                ?? Path.Combine(AppContext.BaseDirectory, "GnOuGo.GithubCopilot.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
            {
                Command = executable, Name = "list-contract-smoke", WorkingDirectory = root,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["Code__DefaultWorkingDirectory"] = root, ["Code__AllowedWorkingRoots__0"] = root,
                    ["KeyVault__DatabasePath"] = Path.Combine(root, "isolated.db")
                }
            }), cancellationToken: TestContext.Current.CancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            foreach (var pair in CopilotListContractTests.Parameters)
            {
                var tool = pair.Data.Item1; var parameter = pair.Data.Item2;
                var schema = System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(tools, t => t.Name == tool).JsonSchema.GetRawText())!;
                Assert.Null(schema["properties"]![parameter + "Json"]);
                var items = schema["properties"]![parameter]!["items"]!;
                Assert.Equal("string", items["type"]!.ToString()); Assert.NotNull(items["pattern"]);
                Assert.Empty(PlanningContractValidation.ValidateInstance(System.Text.Json.Nodes.JsonNode.Parse("[\"readme.md\"]"), schema["properties"]![parameter]!));
                foreach (var (key, value) in new[] { (parameter, "{}"), (parameter, "\"[]\""), (parameter, "[null]"), (parameter, "[\" \" ]"), (parameter + "Json", "null") })
                {
                    var result = await client.CallToolAsync(tool, new Dictionary<string, object?> { [key] = JsonSerializer.Deserialize<JsonElement>(value) }, cancellationToken: TestContext.Current.CancellationToken);
                    Assert.True(result.IsError);
                    Assert.Equal("INVALID_INPUT", result.StructuredContent!.Value.GetProperty("code").GetString());
                    Assert.StartsWith(key, result.StructuredContent.Value.GetProperty("message").GetString());
                }
                // Valid native arrays bind in the trimmed host. Missing host-owned tenant
                // then refuses execution before the real SDK can create a session.
                var valid = await client.CallToolAsync(tool, new Dictionary<string, object?>
                { [parameter] = Array.Empty<string>(), ["projectRoot"] = ".", ["prompt"] = "No provider call", ["task"] = "No provider call" }, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(valid.IsError);
                Assert.Contains("tenant", string.Join(";", valid.Content.OfType<TextContentBlock>().Select(t => t.Text)), StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
