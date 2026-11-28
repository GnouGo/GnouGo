using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Integrations;
using Xunit;

namespace GnOuGo.Flow.Integrations.Tests;

public sealed class DeckCraftTests(ITestOutputHelper output)
{
    [Fact]
    public async Task HeadlessCliCreatesEditsReopensAndRenders()
    {
        var binary = Environment.GetEnvironmentVariable("GNOUGO_DECKCRAFT_CLI");
        Assert.SkipWhen(string.IsNullOrEmpty(binary), "Set GNOUGO_DECKCRAFT_CLI to the installed CLI.");
        var evidence = Environment.GetEnvironmentVariable("GNOUGO_DECKCRAFT_EVIDENCE");
        var root = evidence is null ? Directory.CreateTempSubdirectory("gnougo-deck-").FullName :
            Directory.CreateDirectory(Path.Combine(evidence, Guid.NewGuid().ToString("N"))).FullName;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await using var factory = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>
            {
                ["deckcraft"] = new() { Type = "stdio", Command = Path.GetFullPath(binary),
                    Args = ["mcp", "--automation-read-root", root, "--automation-write-root", root],
                    CallTimeoutSeconds = 60, DiscoveryTimeoutSeconds = 30 }
            });
            await using var client = await factory.GetClientAsync("deckcraft", deadline.Token);
            var protocol = Assert.IsType<McpSessionAdapter>(client).ProtocolVersion;
            Assert.False(string.IsNullOrWhiteSpace(protocol));
            async Task<JsonNode> Call(string tool, JsonObject args)
            {
                var receipt = await client.CallToolAsync(tool, args, deadline.Token);
                Assert.False(receipt.IsError, tool + " failed: " + receipt.Content);
return receipt.Content ?? new JsonObject();
            }
            var tools = await client.ListToolsAsync(deadline.Token);
            Assert.Contains(tools, t => t.Name == "insert_chart");
            await Call("new_presentation", []);
            var title = await Call("add_text_box", new() { ["x"] = 50, ["y"] = 20, ["w"] = 500, ["text"] = "Présentation initiale" });
            var chart = await Call("insert_chart", JsonNode.Parse("""{"type":"column","categories":["A","B"],"series":[{"name":"Volume","values":[4,7]}],"x":70,"y":100,"w":500,"h":300}""")!.AsObject());
            var before = Path.Combine(root, "before.pptx");
            var after = Path.Combine(root, "after.pptx");
            await Call("save_presentation", new() { ["path"] = before });
            await Call("set_text", new() { ["id"] = title["id"]!.DeepClone(), ["text"] = "Présentation révisée" });
            var commands = await Call("list_commands", new() { ["filter"] = "chart.data" });
            Assert.Contains("chart.data", commands.ToJsonString());
            await Call("run_command", new() { ["command"] = "chart.data", ["params"] = new JsonObject
            {
                ["id"] = chart["id"]!.DeepClone(), ["series"] = JsonNode.Parse("""[{"name":"Volume","values":[4,11]}]""")
            } });
            await Call("save_presentation", new() { ["path"] = after });
            await Call("new_presentation", []);
            await Call("open_presentation", new() { ["path"] = after });
            var slide = await Call("inspect_slide", new() { ["index"] = 0 });
            Assert.Contains("Présentation révisée", System.Text.RegularExpressions.Regex.Unescape(slide.ToJsonString()));
            var render = await client.CallToolAsync("render_slide", new JsonObject { ["slide"] = 0, ["scale"] = 0.75 }, deadline.Token);
            Assert.False(render.IsError);
            var block = Assert.Single(render.ContentBlocks!.OfType<JsonObject>(), b => b["type"]?.ToString() == "image");
            var png = Convert.FromBase64String(block["data"]!.GetValue<string>());
            Assert.True(png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            await File.WriteAllBytesAsync(Path.Combine(root, "reopened.png"), png, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(root, "reopened.json"), slide.ToJsonString(), deadline.Token);
            output.WriteLine($"Protocol {protocol}, {tools.Count} tools, render SHA-256 {Convert.ToHexStringLower(SHA256.HashData(png))}");
            await File.WriteAllTextAsync(Path.Combine(root, "transport.json"), new JsonObject { ["protocol"] = protocol,
                ["tools"] = tools.Count, ["cli_sha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, deadline.Token))) }.ToJsonString(), deadline.Token);
        }
        finally
        {
            if (evidence is null) Directory.Delete(root, recursive: true);
        }
    }
}
