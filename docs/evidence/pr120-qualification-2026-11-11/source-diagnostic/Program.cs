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

// One-off, source-only evidence runner. Not registered in the planner, delegated
// execution authority, Browser policy defaults, or business-oracle version switch.
const string approval = "user-amazon-source-only-20261111";
const string policy = "amazon-observed-product-context-v3";
const string query = "chaussure geox homme 45";
const string search = "https://www.amazon.fr/s?k=chaussure%20geox%20homme%2045";
const string storePattern = @"https://(?:www\.)?amazon\.fr/stores/page/[A-Fa-f0-9]{8}(?:-[A-Fa-f0-9]{4}){3}-[A-Fa-f0-9]{12}(?:\?[^#\s]+)?";
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
JsonObject Verify(List<JsonObject> events) => (JsonObject)oracle.GetMethod("VerifyAmazonSourceResolutionProposal", flags)!.Invoke(null, [events, query])!;
bool Entry(string url) => (bool)assembly.GetType("AmazonSponsoredHostPolicy", true)!.GetMethod("IsEntry", flags)!.Invoke(null, [url])!;
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
environment["Browser__AllowedNavigationUrlPatterns__1"] = storePattern;
await Save("profile", new JsonObject
{
    ["approval"] = approval, ["base_policy"] = policy, ["source_only"] = true,
    ["benchmark_builder"] = true, ["model_calls"] = 0, ["saved_configuration_modified"] = false,
    ["active_business_oracle_modified"] = false, ["original_route_pattern"] = originalPattern,
    ["additional_redirect_pattern"] = storePattern,
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
        var issue = new JsonObject { ["source_record"] = sourceRecord, ["arguments"] = arguments.DeepClone(), ["issued_at_utc"] = DateTimeOffset.UtcNow.ToString("O") };
        issued.Add(issue);
        // Durable issue before dispatch. A crashed identity must not be replayed.
        await Save("issued", issued);
        var result = await browser.CallToolAsync("browser_get_content", arguments, CancellationToken.None);
        var read = new JsonObject { ["tool"] = "browser_get_content", ["arguments"] = arguments.DeepClone(),
            ["error"] = result.IsError, ["result"] = result.Content?.DeepClone(), ["completed_at_utc"] = DateTimeOffset.UtcNow.ToString("O") };
        events.Add(read);
        await Save("events", new JsonArray(events.Select(e => (JsonNode)e.DeepClone()).ToArray()));
        Console.WriteLine(new JsonObject { ["read"] = events.Count, ["source_record"] = sourceRecord,
            ["error"] = result.IsError, ["navigation_complete"] = result.Content?["navigationEvidence"]?["complete"]?.DeepClone() }.ToJsonString());
        return read;
    }

    var source = await Read(search, null);
    var selection = Verify([source]);
    await Save("source-selection", selection);
    var findings = selection["findings"]!.AsArray().Select(f => f!.ToString()).ToArray();
    if (findings.Any(f => f is not ("source_resolutions_incomplete" or "no_unresolved_source_links")))
        throw new InvalidOperationException("Fresh source prerequisite rejected: " + string.Join(",", findings));
    var snapshot = source["result"]!["observationSnapshot"]!;
    var records = snapshot["pages"]!.AsArray().SelectMany(p => p!["records"]!.AsArray()).OfType<JsonObject>()
        .ToDictionary(r => r["reference"]!.ToString(), StringComparer.Ordinal);
    var candidates = selection["resolutions"]!.AsArray().Select(r => records[r!["source_record"]!.ToString()]).ToArray();
    // Validate the whole manifest before any dependent GET. Preserve occurrences,
    // including duplicate hrefs. Never decode r, construct dp, or normalize /clk.
    if (candidates.Any(r => !Entry(r["href"]!.ToString()))) throw new InvalidOperationException("A literal source entry is outside the approved scope.");
    await Save("candidates", new JsonArray(candidates.Select(r => (JsonNode)r.DeepClone()).ToArray()));
    foreach (var candidate in candidates)
    {
        var read = await Read(candidate["href"]!.ToString(), candidate["reference"]!.ToString());
        await Save("resolution", Verify(events));
        var navigation = read["result"]?["navigationEvidence"];
        // A missing navigation record cannot prove source authority survived.
        // Any new search (including a redirect) expires this manifest immediately.
        bool Search(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/s";
        if (navigation is null || Search(read["result"]?["url"]?.ToString()) || Search(navigation["finalUrl"]?.ToString()) ||
            navigation["hops"] is not JsonArray hops || hops.Any(h => Search(h?["url"]?.ToString()) ||
                h?["location"]?.ToString() is { } location && Uri.TryCreate(new Uri(h["url"]!.ToString()), location, out var redirect) && Search(redirect.AbsoluteUri)))
            throw new InvalidOperationException("Source authority expired or could not be verified; remaining entries are not dispatched.");
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
    var close = await browser.CallToolAsync("browser_close", new JsonObject(), CancellationToken.None);
    var probe = await browser.CallToolAsync("browser_get_content", new JsonObject(), CancellationToken.None);
    await Save("close", new JsonObject { ["close_error"] = close.IsError, ["close"] = close.Content?.DeepClone(),
        ["probe_error"] = probe.IsError, ["probe"] = probe.Content?.DeepClone() });
}
