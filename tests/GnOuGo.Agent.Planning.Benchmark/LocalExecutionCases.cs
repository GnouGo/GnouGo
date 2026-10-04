using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;

// Real shipped commands, with an independent filesystem oracle. No planner-specific metadata.
internal sealed class LocalExecutionCases(string root, string executable) : IAsyncDisposable
{
    internal static readonly string[] Names = ["files_read", "files_copy", "files_loop_cleanup"];
    internal static string Prompt(string name) => name switch
    {
        "files_read" => "Read the UTF-8 file seed.txt in the configured workspace and return its full contents as output result. The file is supplied externally and its contents vary at execution time.",
        "files_copy" => "In the configured workspace, create workflows/copy, write the literal UTF-8 text hello to workflows/copy/source.txt, copy that file to workflows/copy/result.txt, read the copy and return its contents as output result. Keep both files on success. Do not modify seed.txt.",
        "files_loop_cleanup" => "In the configured workspace, create workflows/batch. Accept required boolean input enabled and required string-array input files (at most 3 names, each alpha.txt or beta.txt). If enabled, iterate over files in order, including duplicates, read each file from the workspace, and return the collected contents as string-array output result. If disabled, return an empty string array. Always remove workflows/batch, including when a file is missing. Do not modify the input files. Directory creation and cleanup must reuse the same literal location.",
        _ => throw new ArgumentException("Unknown real execution case.")
    };
    private ConfiguredMcpClientFactory Transport { get; } = new(new Dictionary<string, McpServerOptions>
    {
        ["filesystem"] = new() { Type = "stdio", Command = executable, Description = "Workspace files and directories", DiscoveryTimeoutSeconds = 30,
            EnvironmentVariables = new() { ["Cmd__DefaultWorkingDirectory"] = root, ["OpenTelemetry__Enabled"] = "false" } }
    });
    internal List<JsonObject> Calls { get; } = [];
    internal IMcpClientFactory Factory => new ObservedFactory(this);
    internal static string[] Variants(string name) => name == "files_read" ? ["nominal", "alternate", "missing"]
        : name == "files_copy" ? ["nominal", "failure", "denied", "permission_unavailable"]
        : ["nominal", "alternate", "disabled", "missing", "denied", "permission_unavailable"];
    internal JsonObject Prepare(string name, string variant)
    {
        Calls.Clear();
        // Reset only this disposable run directory, after the previous execution and oracle completed.
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
            if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
        File.WriteAllText(Path.Combine(root, "seed.txt"), variant == "alternate" ? "changed 東京\n" : "seed é\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "alpha.txt"), "alpha", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "beta.txt"), "beta", new UTF8Encoding(false));
        if (variant == "missing") File.Delete(Path.Combine(root, name == "files_read" ? "seed.txt" : "beta.txt"));
        if (variant == "failure") File.WriteAllText(Path.Combine(root, "workflows"), "blocking file", new UTF8Encoding(false));
        return name == "files_loop_cleanup" ? new() { ["enabled"] = variant != "disabled", ["files"] = variant == "alternate"
            ? new JsonArray("beta.txt", "alpha.txt", "beta.txt") : new JsonArray("alpha.txt", "beta.txt") } : new();
    }
    internal JsonObject Observe(string name, string variant, RunResult? result, string? error)
    {
        var snapshot = Snapshot(); var denied = variant is "denied" or "permission_unavailable";
        var correct = false;
        if (denied) correct = result?.Success != true && !File.Exists(Path.Combine(root, "workflows")) && !Directory.Exists(Path.Combine(root, "workflows"));
        else if (variant is "missing" or "failure") correct = result?.Success == false &&
            (name != "files_loop_cleanup" || !Directory.Exists(Path.Combine(root, "workflows/batch")));
        else if (name == "files_read") correct = result?.Success == true && result.Outputs?["result"]?.ToString() == File.ReadAllText(Path.Combine(root, "seed.txt"));
        else if (name == "files_copy") correct = result?.Success == true && result.Outputs?["result"]?.ToString() == "hello" &&
            Read("workflows/copy/source.txt") == "hello" && Read("workflows/copy/result.txt") == "hello";
        else
        {
            var expected = variant == "disabled" ? new JsonArray() : variant == "alternate" ? new JsonArray("beta", "alpha", "beta") : new JsonArray("alpha", "beta");
            correct = result?.Success == true && JsonNode.DeepEquals(expected, result.Outputs?["result"]) && !Directory.Exists(Path.Combine(root, "workflows/batch"));
        }
        if (name == "files_loop_cleanup" && !denied) correct &= Calls.Any(c => c["command"]?.ToString() == "create_directory" && c["path"]?.ToString() == "workflows/batch") &&
            Calls.Any(c => c["command"]?.ToString() == "delete_directory" && c["path"]?.ToString() == "workflows/batch");
        if (denied) correct &= Calls.Count == 0;
        var inputsPreserved = Read("alpha.txt") == "alpha" && (variant == "missing" && name == "files_loop_cleanup" ? !File.Exists(Path.Combine(root, "beta.txt")) : Read("beta.txt") == "beta") &&
            (variant == "missing" && name == "files_read" ? !File.Exists(Path.Combine(root, "seed.txt")) : Read("seed.txt") == (variant == "alternate" ? "changed 東京\n" : "seed é\n"));
        var allowed = new HashSet<string>(["seed.txt", "alpha.txt", "beta.txt"], StringComparer.Ordinal);
        if (name == "files_copy" && variant == "nominal") allowed.UnionWith(["workflows/copy/source.txt", "workflows/copy/result.txt"]);
        if (variant == "failure") allowed.Add("workflows");
        var confined = snapshot.All(p => allowed.Contains(p.Key));
        return new() { ["variant"] = variant, ["correct"] = correct && inputsPreserved && confined, ["runtime_success"] = result?.Success,
            ["error"] = error ?? result?.Error?.Code, ["files"] = snapshot, ["calls"] = new JsonArray(Calls.Select(c => c.DeepClone()).ToArray()), ["safety_violations"] = new JsonArray(
                (inputsPreserved && confined && (!denied || correct) ? Array.Empty<string>() : ["unexpected_filesystem_effect"]).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
    }
    private string? Read(string relative) => File.Exists(Path.Combine(root, relative)) ? File.ReadAllText(Path.Combine(root, relative)) : null;
    internal JsonObject Snapshot() => new(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(p => new KeyValuePair<string, JsonNode?>(Path.GetRelativePath(root, p).Replace('\\', '/'), JsonValue.Create(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))))));
    public async ValueTask DisposeAsync() { await Transport.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class ObservedFactory(LocalExecutionCases owner) : IMcpClientFactory, IMcpExecutionHooks
    {
        public IReadOnlyList<McpServerMetadata> ServerMetadata => owner.Transport.ServerMetadata;
        public async Task<IMcpSession> GetClientAsync(string name, CancellationToken ct) => new ObservedSession(owner, await owner.Transport.GetClientAsync(name, ct));
        public IDisposable BeginCall(McpCallExecutionContext context) => ((IMcpExecutionHooks)owner.Transport).BeginCall(context);
        public string FormatFailureDiagnostics(string serverName, Exception exception) => ((IMcpExecutionHooks)owner.Transport).FormatFailureDiagnostics(serverName, exception);
    }
    private sealed class ObservedSession(LocalExecutionCases owner, IMcpSession inner) : IMcpSession, ILiveMcpToolDiscoverySession
    {
        public string ServerName => inner.ServerName;
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => inner.ListToolsAsync(ct);
        public Task<IReadOnlyList<McpToolInfo>> EnsureToolsDiscoveredAsync(CancellationToken ct) => ((ILiveMcpToolDiscoverySession)inner).EnsureToolsDiscoveredAsync(ct);
        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => inner.ListResourcesAsync(ct);
        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => inner.ListPromptsAsync(ct);
        public Task<McpGetPromptResult> GetPromptAsync(string name, JsonNode? args, CancellationToken ct) => inner.GetPromptAsync(name, args, ct);
        public async Task<McpCallResult> CallToolAsync(string name, JsonNode? args, CancellationToken ct)
        {
            lock (owner.Calls) owner.Calls.Add(new() { ["tool"] = name, ["command"] = args?["commandName"]?.DeepClone(), ["path"] = args?["parameters"]?["path"]?.DeepClone() });
            return await inner.CallToolAsync(name, args, ct);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // The factory owns the transport lifetime.
    }
}
