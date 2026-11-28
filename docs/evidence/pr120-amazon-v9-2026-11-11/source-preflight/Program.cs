using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Integrations;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Fresh source-only prerequisite under the separately approved workflow policy.
// Exercises its real host configuration and tool guard; creates no artifact review
// or execution approval and makes no model call.
const string approval = "user-amazon-workflow-product-context-20261111";
const string policy = "amazon-observed-resolved-product-context-v5";
const string query = "chaussure geox homme 45";
const string search = "https://www.amazon.fr/s?k=chaussure%20geox%20homme%2045";
if (args.Length != 4 || args[2] != "--approval" || args[3] != approval)
    throw new ArgumentException("Required: <workspace> <fresh-private-output-directory> --approval " + approval);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
await using var admission = new FileStream(Path.Combine(output, "admission.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
await JsonSerializer.SerializeAsync(admission, new { approval, startedUtc = DateTimeOffset.UtcNow, modelCalls = 0 });
await admission.FlushAsync();

var assembly = Assembly.Load("GnOuGo.Agent.Planning.Benchmark");
const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
var oracle = assembly.GetType("LiveWorkflowOracles", true)!;
JsonObject Verify(List<JsonObject> events) => (JsonObject)oracle.GetMethod("VerifyAmazonResolvedProductSelection", flags)!.Invoke(null, [events, 3, query])!;
IEnumerable<string> Entries(List<JsonObject> events) => (IEnumerable<string>)oracle.GetMethod("ResolvedSourceAllowedEntries", flags)!.Invoke(null, [events, policy])!;
var guardType = assembly.GetType("DelegatedTestReviewAuthority", true)!;
// Only the tool permission methods are exercised. No synthetic review/receipt is
// supplied or persisted: normal artifact validation remains mandatory for a run.
var guard = Activator.CreateInstance(guardType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
    null, [null, null, "pr120-live-20261107-400", Path.GetFileName(output), "amazon", args[0], "source-preflight", null, policy], null)!;
void Require(string tool, JsonObject arguments) => guardType.GetMethod("RequireTool", BindingFlags.Instance | BindingFlags.NonPublic)!
    .Invoke(guard, ["GnOuGo.Browser.Mcp", tool, arguments, false]);
void Observe(string tool, object result, JsonObject arguments) => guardType.GetMethod("ObserveTool", BindingFlags.Instance | BindingFlags.NonPublic)!
    .Invoke(guard, ["GnOuGo.Browser.Mcp", tool, arguments, result]);
async Task Save(string name, JsonNode value) => await File.WriteAllTextAsync(Path.Combine(output, name + ".json"), value.ToJsonString());

// KeyVault configuration is read through its public service; no saved setting changes.
var vault = Path.GetFullPath(Path.Combine(args[0], KeyVaultDatabasePathResolver.DefaultRelativePath));
var services = new ServiceCollection(); services.AddLogging();
services.AddDbContext<KeyVaultDbContext>(o => o.UseSqlite("Data Source=" + vault));
services.AddScoped<KeyVaultService>();
await using var provider = services.BuildServiceProvider();
var host = JsonNode.Parse(await File.ReadAllTextAsync("src/GnOuGo.Agent.Server/appsettings.json"))!;
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var bundled = host["BundledMcp"]!.Deserialize<BundledMcpSettings>(jsonOptions)!;
var config = new KeyVaultRuntimeConfigStore(provider.GetRequiredService<IServiceScopeFactory>(),
    NullLogger<KeyVaultRuntimeConfigStore>.Instance, Options.Create(bundled), Options.Create(new KeyVaultSettings { DatabasePath = vault }));
var options = await config.BuildEffectiveOptionsAsync(host["LLM"]!.Deserialize<LLMOptions>(jsonOptions)!, CancellationToken.None);
var builder = assembly.GetType("LiveWorkflowEvaluation", true)!.GetMethod("Configuration", flags)!;
var actual = (Dictionary<string, McpServerOptions>)builder.Invoke(null,
    [options.McpServers, "amazon", null, "Release", Directory.GetCurrentDirectory(), policy])!;
var settings = actual["GnOuGo.Browser.Mcp"];
var environment = settings.EnvironmentVariables!;
if (environment.Keys.Any(k => k.StartsWith("Browser__AllowedNavigationUrlPatterns", StringComparison.OrdinalIgnoreCase) && k != "Browser__AllowedNavigationUrlPatterns__0"))
    throw new InvalidOperationException("Unexpected route configuration; no replacement is authorized.");
var originalPattern = environment["Browser__AllowedNavigationUrlPatterns__0"];

await Save("profile", new JsonObject
{
    ["approval"] = approval, ["base_policy"] = policy, ["source_only"] = true,
    ["benchmark_builder"] = true, ["model_calls"] = 0, ["saved_configuration_modified"] = false,
    ["business_oracle"] = "amazon-business-v9", ["artifact_review_created"] = false,
    ["execution_tool_guard"] = true, ["route_pattern"] = originalPattern,
    ["benchmark_assembly_sha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly.Location))).ToLowerInvariant(),
    ["browser_assembly_sha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(settings.Args![0]))).ToLowerInvariant(),
    ["environment"] = new JsonObject(environment.Where(p => p.Key.StartsWith("Browser__Allowed", StringComparison.OrdinalIgnoreCase) ||
        p.Key.StartsWith("Browser__ObservationAncestorAttributes", StringComparison.OrdinalIgnoreCase) || p.Key == "Browser__KeepBrowserOpen")
        .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value))))
});

await using var factory = new ConfiguredMcpClientFactory(new() { ["GnOuGo.Browser.Mcp"] = settings });
var browser = await factory.GetClientAsync("GnOuGo.Browser.Mcp", CancellationToken.None);
List<JsonObject> events = [];
var issued = new JsonArray();
try
{
    async Task<JsonObject> Read(string exactUrl, string? sourceRecord)
    {
        var arguments = new JsonObject { ["url"] = exactUrl, ["waitUntil"] = "domcontentloaded", ["selector"] = "body", ["format"] = "observation_complete" };
        Require("browser_get_content", arguments);
        var issue = new JsonObject { ["source_record"] = sourceRecord, ["arguments"] = arguments.DeepClone(), ["issued_at_utc"] = DateTimeOffset.UtcNow.ToString("O") };
        issued.Add(issue);
        // Durable issue before dispatch. A crashed identity must not be replayed.
        await Save("issued", issued);
        var result = await browser.CallToolAsync("browser_get_content", arguments, CancellationToken.None);
        var read = new JsonObject { ["tool"] = "browser_get_content", ["arguments"] = arguments.DeepClone(),
            ["error"] = result.IsError, ["result"] = result.Content?.DeepClone(), ["completed_at_utc"] = DateTimeOffset.UtcNow.ToString("O") };
        Observe("browser_get_content", result, arguments);
        events.Add(read);
        await Save("events", new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray()));
        Console.WriteLine(new JsonObject { ["read"] = events.Count, ["source_record"] = sourceRecord,
            ["error"] = result.IsError, ["navigation_complete"] = result.Content?["navigationEvidence"]?["complete"]?.DeepClone() }.ToJsonString());
        return read;
    }

    var source = await Read(search, null);
    var selection = Verify([source]);
    await Save("source-selection", selection);
    var snapshot = source["result"]!["observationSnapshot"]!;
    var records = snapshot["pages"]!.AsArray().SelectMany(p => p!["records"]!.AsArray()).OfType<JsonObject>().ToArray();
    while (!selection["source_prerequisite_qualified"]!.GetValue<bool>())
    {
        var allowed = Entries(events).ToArray();
        if (allowed.Length != 1) throw new InvalidOperationException("Fresh source unresolved: " + selection["findings"]!.ToJsonString());
        var href = allowed[0];
        var priorOccurrences = events.Skip(1).Count(e => e["arguments"]?["url"]?.ToString() == href);
        var candidate = records.Where(r => r["kind"]?.ToString() == "link" && r["href"]?.ToString() == href).Skip(priorOccurrences).First();
        await Read(href, candidate["reference"]!.ToString());
        selection = Verify(events);
        await Save("resolution", selection);
    }
    await Save("resolution", Verify(events));
}
catch (Exception error)
{
    await Save("failure", new JsonObject { ["type"] = error.GetType().Name, ["message"] = error.Message, ["reads_completed"] = events.Count });
    Environment.ExitCode = 1;
}
finally
{
    // No dependent request is possible after this close, and no second search is issued.
    Require("browser_close", new JsonObject());
    var close = await browser.CallToolAsync("browser_close", new JsonObject(), CancellationToken.None);
    Observe("browser_close", close, new JsonObject());
    var probe = await browser.CallToolAsync("browser_get_content", new JsonObject(), CancellationToken.None);
    await Save("close", new JsonObject { ["close_error"] = close.IsError, ["close"] = close.Content?.DeepClone(),
        ["probe_error"] = probe.IsError, ["probe"] = probe.Content?.DeepClone() });
}
