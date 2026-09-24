using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core;
using GnOuGo.Workspace;
using GnOuGo.Flow.Core.Runtime;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace GnOuGo.GithubCopilot.E2E.Tests;

public sealed class LiveControlledEditingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Shell command: python3 -m unittest -v\nIntention: fixture\nSandbox bypass requested: no", true)]
    [InlineData("Shell command: python3 -m unittest -v\nrm -rf .\nIntention: fixture\nSandbox bypass requested: no", false)]
    [InlineData("Shell command: python3 -m unittest -v\nIntention: fixture\nSandbox bypass requested: yes", false)]
    [InlineData("Write file: calculator.py\nProject operation: project_write", true)]
    [InlineData("Read path: calculator.py\nProject operation: project_read\n\nIf allowed for this session: custom tool project_read until this Copilot session ends.", true)]
    [InlineData("Write file: test_calculator.py\nProject operation: project_write", false)]
    [InlineData("Write file: calculator.py\nProject operation: project_rename", false)]
    public void SmokeApprovals_AreBoundedToExactFixtureOperations(string description, bool allowed)
        => Assert.Equal(allowed, PermittedFixtureOperation(description, Path.GetFullPath(Path.GetTempPath())));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealModel_InspectsEditsRunsTestsAndObservesResults(bool managed, bool bounded)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GNOU_GO_LIVE_COPILOT_EDIT") == "1",
            "Set GNOU_GO_LIVE_COPILOT_EDIT=1 to run the local real-model fixture.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = timeout.Token;
        var root = RepositoryRoot();
        var workspace = GnOuGoWorkspace.ResolveDefaultWorkingDirectory();
        var relative = "workflows/e2e/copilot-edit-" + Guid.NewGuid().ToString("N");
        var project = Path.Combine(workspace, relative);
        Directory.CreateDirectory(project);
        var fixture = Path.Combine(project, "calculator.py");
        await File.WriteAllTextAsync(fixture, "def add(a, b):\n    return a - b\n", ct);
        await File.WriteAllTextAsync(Path.Combine(project, "test_calculator.py"), "import unittest\nfrom calculator import add\n\nclass ArithmeticTest(unittest.TestCase):\n    def test_add(self):\n        self.assertEqual(add(7, 5), 12)\n", ct);
        await File.WriteAllTextAsync(Path.Combine(project, "refused.py"), "# unchanged\n", ct);
        var approvals = 0;
        var refusals = 0;
        var refuseEverything = false;
        var defaultExecutable = Path.Combine(root, "src", "GnOuGo.GithubCopilot.Mcp", "bin", "Debug", "net10.0", "GnOuGo.GithubCopilot.Mcp");
        var executable = Environment.GetEnvironmentVariable("GNOU_GO_COPILOT_SMOKE_BINARY") ?? defaultExecutable;
        Assert.True(File.Exists(executable), "Build the Copilot MCP host before running this fixture.");
        var keyVault = KeyVaultDatabasePathResolver.Resolve(null, root);
        Assert.True(File.Exists(keyVault), "The configured encrypted KeyVault is required.");
        var progressKinds = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = executable,
            Name = "copilot-controlled-edit-smoke",
            WorkingDirectory = Path.GetDirectoryName(executable),
            StandardErrorLines = line =>
            {
                if (!line.StartsWith('{')) return;
                try
                {
                    var envelope = JsonNode.Parse(line);
                    var evt = envelope?["event"] ?? envelope?["Event"];
                    var progress = (evt?["kind"] ?? evt?["Kind"])?.GetValue<string>();
                    if (progress is not null) progressKinds.Enqueue(progress);
                }
                catch (JsonException) { }
            },
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["KeyVault__DatabasePath"] = keyVault,
                ["Code__DefaultWorkingDirectory"] = workspace,
                ["Code__AllowedWorkingRoots__0"] = workspace,
                ["Code__AllowWrites"] = "true",
                ["Code__Copilot__EnableApproveAll"] = "false",
                ["Code__Copilot__RequestTimeoutSeconds"] = "600",
                ["Code__Copilot__Telemetry__Enabled"] = "false",
                ["OpenTelemetry__Enabled"] = "false"
            }
        });
        try
        {
            await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new() { Name = "bounded-copilot-edit-smoke", Version = "1.0.0" },
                Handlers = new()
                {
                    ElicitationHandler = (request, _) =>
                    {
                        var description = (request?.RequestedSchema?.Properties?.TryGetValue("answer", out var field) == true ? field.Description : null) ?? "";
                        var permitted = !refuseEverything && PermittedFixtureOperation(description, project);
                        if (permitted) Interlocked.Increment(ref approvals); else Interlocked.Increment(ref refusals);
                        output.WriteLine("Permission {0}: {1}", permitted ? "allow once" : "refuse", description.Split('\n')[0].Replace(project, "<fixture>"));
                        return ValueTask.FromResult(new ElicitResult
                        {
                            Action = "accept",
                            Content = new Dictionary<string, JsonElement> { ["answer"] = JsonSerializer.SerializeToElement(permitted ? "Allow once" : "Refuse") }
                        });
                    }
                }
            }, cancellationToken: ct);
            var provider = Environment.GetEnvironmentVariable("GNOU_GO_COPILOT_SMOKE_PROVIDER") ?? "OpenAi";
            const string prompt = "Inspect calculator.py and test_calculator.py. Before editing, run exactly `python3 -m unittest -v` in the project root and observe the failing assertion. Fix only calculator.py, using a file-edit tool (not shell redirection), then run exactly `python3 -m unittest -v` again and observe the passing result. Do not install anything, use the network, or modify tests or refused.py. Finish only after observing the passing test.";
            string? handle = null;
            JsonObject result;
            try
            {
                if (bounded)
                {
                    var task = new AgentTaskDefinition
                    {
                        Runner = "coding", Objective = prompt, Workspace = relative,
                        Capabilities = ["project.read", "project.write", "command.execute"],
                        Budget = new() { MaxModelCalls = 8, MaxTotalTokens = 512000, MaxElapsedMilliseconds = 600000 },
                        OutputSchema = JsonNode.Parse("""{"type":"object","required":["summary"],"properties":{"summary":{"type":"string"}},"additionalProperties":false}""")!.AsObject(),
                        Verification = [
                            new("tests", "command.exit", "python3 -m unittest -v", JsonNode.Parse("""{"type":"object","required":["exit_code","tool_success"],"properties":{"exit_code":{"const":0},"tool_success":{"const":true}}}""")!.AsObject()),
                            new("edit", "file.content", "calculator.py", JsonNode.Parse("""{"type":"object","required":["exists","changed"],"properties":{"exists":{"const":true},"changed":{"const":true}}}""")!.AsObject())]
                    };
                    var context = new AgentTaskContext("copilot-live-smoke", Guid.NewGuid().ToString("N"), "main/repair", task);
                    Dictionary<string, object?> Arguments(AgentTaskContext value) => new() { ["contextJson"] = JsonSerializer.Serialize(value, AgentTaskJsonContext.Default.AgentTaskContext) };
                    Assert.Empty((await Call(client, "copilot_task_validate", Arguments(context), ct))["errors"]!.AsArray());
                    var receipt = await Call(client, "copilot_task_run", Arguments(context), ct);
                    var observed = JsonSerializer.Deserialize(receipt["result"], AgentTaskJsonContext.Default.AgentTaskResult)!;
                    Assert.True(observed.Status == "completed", observed.Status + ": " + observed.Message);
                    Assert.All(await new EvidenceAgentTaskVerifier().VerifyAsync(context, observed, ct), f => Assert.True(f.Passed, f.Message));
                    Assert.Contains("return a + b", await File.ReadAllTextAsync(fixture, ct));
                    var attempts = observed.Evidence.Single(e => e.Kind == "command.exit").Facts["attempts"]!.AsArray();
                    Assert.Contains(attempts, a => a!["exit_code"]!.GetValue<int>() != 0);
                    Assert.Equal(0, attempts[^1]!["exit_code"]!.GetValue<int>());
                    Assert.InRange(observed.Usage.ModelCalls, 1, task.Budget.MaxModelCalls);
                    Assert.InRange(observed.Usage.TotalTokens, 1, task.Budget.MaxTotalTokens);
                    Assert.True(JsonNode.DeepEquals(receipt, await Call(client, "copilot_task_inspect", Arguments(context), ct)));
                    Assert.True(JsonNode.DeepEquals(receipt, await Call(client, "copilot_task_run", Arguments(context), ct)));
                    var expanded = context with { Task = task with { Objective = prompt + " Also modify refused.py." } };
                    Assert.True((await client.CallToolAsync("copilot_task_run", Arguments(expanded), cancellationToken: ct)).IsError);
                    refuseEverything = true;
                    var refusedContext = context with { InvocationId = "main/refusal", Task = task with
                    {
                        Objective = "Append '# requested change' to refused.py using a file edit tool. Stop if permission is refused.",
                        Capabilities = ["project.read", "project.write"],
                        Verification = [task.Verification[1] with { Subject = "refused.py" }]
                    } };
                    var refused = JsonSerializer.Deserialize((await Call(client, "copilot_task_run", Arguments(refusedContext), ct))["result"], AgentTaskJsonContext.Default.AgentTaskResult)!;
                    Assert.Contains(await new EvidenceAgentTaskVerifier().VerifyAsync(refusedContext, refused, ct), f => !f.Passed);
                    Assert.Equal("# unchanged\n", await File.ReadAllTextAsync(Path.Combine(project, "refused.py"), ct));
                    Assert.True(approvals > 0 && refusals > 0);
                    output.WriteLine("PASS bounded task: observed failed/pass test cycle, verified file change, receipt reuse, rejected scope expansion and permission refusal");
                    return;
                }
                if (managed)
                {
                    var created = await Call(client, "copilot_session_create", new() { ["projectRoot"] = relative, ["provider"] = provider, ["tenantId"] = "copilot-live-smoke" }, ct);
                    handle = created["handle"]!.GetValue<string>();
                    result = await Call(client, "copilot_session_send", new() { ["handle"] = handle, ["prompt"] = prompt, ["tenantId"] = "copilot-live-smoke" }, ct);
                }
                else
                    result = await Call(client, "code_agent_edit", new() { ["projectRoot"] = relative, ["task"] = prompt, ["provider"] = provider, ["tenantId"] = "copilot-live-smoke" }, ct);
                Assert.Contains("return a + b", await File.ReadAllTextAsync(fixture, ct));
                var executions = result["toolExecutions"]!.AsArray().OfType<JsonObject>().ToArray();
                var commands = executions.Where(e => e["argumentsJson"]?.GetValue<string>().Contains("python3 -m unittest -v", StringComparison.Ordinal) == true).ToArray();
                var terminals = commands.SelectMany(e => e["terminals"]!.AsArray().OfType<JsonObject>()).ToArray();
                Assert.True(terminals.Any(t => t["exitCode"]?.GetValue<long>() != 0 && t["text"]?.GetValue<string>().Contains("FAIL", StringComparison.Ordinal) == true), "The initial failing command must be observed.");
                Assert.True(terminals.Any(t => t["exitCode"]?.GetValue<long>() == 0 && t["text"]?.GetValue<string>().Contains("OK", StringComparison.Ordinal) == true), "The successful rerun must be observed.");
                Assert.All(terminals, t => Assert.Equal(Path.GetFullPath(project), Path.GetFullPath(t["workingDirectory"]!.GetValue<string>())));
                Assert.True(commands.Select(e => e["toolCallId"]!.GetValue<string>()).Distinct().Count() >= 2);
                Assert.All(commands, e => { Assert.True(e["completionObserved"]!.GetValue<bool>()); Assert.False(e["conflictingCompletion"]!.GetValue<bool>()); });
                Assert.Contains(result["modifiedFiles"]!.AsArray(), f => f?.GetValue<string>() == "calculator.py");
                Assert.Contains(executions, e => e["toolName"]?.GetValue<string>() == "project_read" && e["toolSucceeded"]?.GetValue<bool>() == true);
                Assert.Contains(progressKinds, kind => kind.Contains("tool.execution", StringComparison.Ordinal));
                Assert.True(approvals > 0, "A permission request must round-trip through Core and MCP elicitation.");
                Assert.Equal("# unchanged\n", await File.ReadAllTextAsync(Path.Combine(project, "refused.py"), ct));
                output.WriteLine("PASS {0}: commands={1}, observedFailure=true, observedPass=true, approvals={2}, refusals={3}", managed ? "managed" : "code_agent_edit", commands.Length, approvals, refusals);
                if (handle is not null)
                {
                    await Call(client, "copilot_session_disconnect", new() { ["handle"] = handle, ["tenantId"] = "copilot-live-smoke" }, ct);
                    await Call(client, "copilot_session_resume", new() { ["handle"] = handle, ["tenantId"] = "copilot-live-smoke" }, ct);
                    refuseEverything = true;
                    var previousRefusals = refusals;
                    _ = await Call(client, "copilot_session_send", new() { ["handle"] = handle, ["prompt"] = "Use a file-edit tool to append '# requested change' to refused.py. Do not use shell commands. If permission is refused, stop without changing the file.", ["tenantId"] = "copilot-live-smoke" }, ct);
                    Assert.True(refusals > previousRefusals);
                    Assert.Equal("# unchanged\n", await File.ReadAllTextAsync(Path.Combine(project, "refused.py"), ct));
                    output.WriteLine("PASS managed reconnect and refused mutation: file unchanged");
                }
            }
            finally
            {
                if (handle is not null) await Call(client, "copilot_session_delete", new() { ["handle"] = handle, ["tenantId"] = "copilot-live-smoke" }, CancellationToken.None);
            }
        }
        finally { Directory.Delete(project, true); }
    }

    private static bool PermittedFixtureOperation(string description, string project)
    {
        if (description.Contains("Sandbox bypass requested: yes", StringComparison.Ordinal)) return false;
        if (description.StartsWith("Shell command: ", StringComparison.Ordinal))
        {
            var end = description.IndexOf("\nIntention: ", StringComparison.Ordinal);
            if (end < 0) return false;
            var command = description["Shell command: ".Length..end].Trim();
            return command == "python3 -m unittest -v" || command == $"cd {project} && python3 -m unittest -v" || command == $"cd '{project}' && python3 -m unittest -v";
        }
        var lines = description.Split('\n');
        if (lines.Length < 2 || !lines[1].StartsWith("Project operation: ", StringComparison.Ordinal)) return false;
        var operation = lines[1]["Project operation: ".Length..];
        if (lines.Length != 2 && (lines.Length != 4 || lines[2] != ""
            || lines[3] != $"If allowed for this session: custom tool {operation} until this Copilot session ends.")) return false;
        var prefix = lines[0].StartsWith("Read path: ", StringComparison.Ordinal) ? "Read path: "
            : lines[0].StartsWith("Write file: ", StringComparison.Ordinal) ? "Write file: " : null;
        if (prefix is null) return false;
        var path = Path.GetFullPath(lines[0][prefix.Length..], project);
        if (prefix == "Write file: ")
            return lines[1] is "Project operation: project_write" or "Project operation: project_append"
                && path == Path.Combine(project, "calculator.py");
        return lines[1] is "Project operation: project_read" or "Project operation: project_list" or "Project operation: project_stat"
            && (path == project || new[] { "calculator.py", "test_calculator.py" }.Any(f => path == Path.Combine(project, f)));
    }
    private static async Task<JsonObject> Call(McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var response = await client.CallToolAsync(tool, args, cancellationToken: ct);
        Assert.False(response.IsError == true, $"{tool} returned an error: {string.Join(" ", response.Content.OfType<TextContentBlock>().Select(t => t.Text))}");
        var result = response.StructuredContent is { } structured ? JsonNode.Parse(structured.GetRawText())!.AsObject()
            : JsonNode.Parse(response.Content.OfType<TextContentBlock>().Single().Text)!.AsObject();
        Assert.False(result["success"]?.GetValue<bool>() == false, result["error_message"]?.GetValue<string>());
        return result;
    }
    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "GnOuGo.Agent.sln"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
