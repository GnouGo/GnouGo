using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

// Private operator evidence, never a planner option or effect authorization. The caller
// supplies an independently reviewed hash; every referenced artifact is re-read on apply.
internal sealed class BenchmarkFrozenBaselineArm
{
    internal const string Baseline = "56942f5bdfad69ee6f05ccd793b712eaf9f33ca5";
    private readonly string _path, _hash;
    private BenchmarkFrozenBaselineArm(string path, string hash) { _path = path; _hash = hash; }

    internal static BenchmarkFrozenBaselineArm? FromArguments(string? path, string? expectedHash)
    {
        if (path is null && expectedHash is null) return null;
        if (path is null || !Digest(expectedHash)) throw Invalid("a proof file and its independently reviewed SHA-256");
        return new(Path.GetFullPath(path), expectedHash!);
    }

    internal JsonObject Read()
    {
        var proof = ReadJson(_path, _hash);
        if (proof["version"]?.GetValue<int>() != 1 || proof["baseline_source"]?.ToString() != Baseline ||
            proof["source"]?.ToString() is not { Length: 40 } source || !source.All(Uri.IsHexDigit) ||
            proof["references"] is not JsonObject references || proof["binding"] is not JsonObject)
            throw Invalid("the exact baseline, committed harness and bounded artifact references");
        var names = new[] { "freeze", "review", "source_inventory", "overlay", "validation", "build", "binary", "dependencies", "command", "mcp_manifest" };
        if (references.Count != names.Length || names.Any(name => references[name] is not JsonObject))
            throw Invalid("the complete fixed set of artifact references");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var reference = references[name]!.AsObject();
            var path = FilePath(reference);
            if (!paths.Add(path) || !Digest(reference["sha256"]?.ToString()) ||
                FileHash(path, name == "binary" ? 256 * 1024 * 1024 : 8 * 1024 * 1024) != reference["sha256"]!.ToString())
                throw Invalid("distinct, unchanged artifact bytes");
        }
        JsonObject Json(string name) => ReadJson(FilePath(references[name]!.AsObject()), references[name]!["sha256"]!.ToString());
        var freeze = Json("freeze"); var review = Json("review"); var inventory = Json("source_inventory"); var build = Json("build");
        var validation = Json("validation");
        var frozen = Timestamp(freeze["captured_at"]);
        var reviewed = Timestamp(review["reviewed_at_utc"]);
        if (reviewed < frozen || reviewed > DateTimeOffset.UtcNow ||
            review["pins"] is not JsonArray pins || !pins.OfType<JsonObject>().Any(pin =>
                pin["sha256"]?.ToString() == references["freeze"]!["sha256"]!.ToString() &&
                pin["path"]?.ToString() == references["freeze"]!["path"]!.ToString()) ||
            freeze["source"]?.ToString() != source || freeze["construction_version"]?.GetValue<int>() != 1 ||
            build["source"]?.ToString() != source || build["source_inventory_sha256"]?.ToString() != references["source_inventory"]!["sha256"]!.ToString() ||
            build["benchmark_binary_sha256"]?.ToString() != references["binary"]!["sha256"]!.ToString() ||
            build["benchmark_dependencies_sha256"]?.ToString() != references["dependencies"]!["sha256"]!.ToString() ||
            freeze["benchmark_binary_sha256"]?.ToString() != build["benchmark_binary_sha256"]?.ToString() ||
            freeze["mcp_manifest_sha256"]?.ToString() != references["mcp_manifest"]!["sha256"]!.ToString() ||
            validation["source"]?.ToString() != source)
            throw Invalid("pre-dispatch review and source/build/freeze identity links");
        ValidateQualification(build, validation);
        VerifyExecutedQualification(build, validation, inventory);
        var root = Path.GetFullPath(inventory["root"]?.ToString() ?? throw Invalid("the retained source checkout"));
        VerifySourceInventory(root, source, inventory);
        if (Git(root, "diff", "--no-ext-diff", "--no-textconv", "--binary", Baseline, source, "--").TrimEnd('\n', '\r') !=
            File.ReadAllText(FilePath(references["overlay"]!.AsObject())).TrimEnd('\n', '\r'))
            throw Invalid("the exact reviewed benchmark overlay against the baseline");
        var mcpRoot = Path.GetFullPath(proof["mcp_root"]?.ToString() ?? throw Invalid("the retained MCP root"));
        if (Json("mcp_manifest")["files"] is not JsonArray mcpFiles || mcpFiles.Count is < 1 or > 2000 ||
            mcpFiles.Count != freeze["mcp_files_verified"]?.GetValue<int>()) throw Invalid("the complete frozen MCP inventory");
        var mcpPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in mcpFiles)
        {
            var path = row?["path"]?.ToString() ?? throw Invalid("an exact MCP file path");
            if (!mcpPaths.Add(path) || !Digest(row?["sha256"]?.ToString()) ||
                FileHash(SourcePath(mcpRoot, path), 256 * 1024 * 1024, allowEmpty: true) != row!["sha256"]!.ToString())
                throw Invalid("unchanged frozen MCP bytes");
        }
        if (build["output_files"] is not JsonObject outputs || outputs.Count is < 2 or > 512 ||
            outputs.Any(pair => !Path.IsPathFullyQualified(pair.Key) || !Digest(pair.Value?.ToString()) ||
                FileHash(pair.Key, 256 * 1024 * 1024, allowEmpty: true) != pair.Value!.ToString()) ||
            outputs[references["binary"]!["path"]!.ToString()]?.ToString() != references["binary"]!["sha256"]!.ToString() ||
            outputs[references["dependencies"]!["path"]!.ToString()]?.ToString() != references["dependencies"]!["sha256"]!.ToString())
            throw Invalid("the complete retained build output inventory");
        if (freeze["oracle_and_input_files"] is not JsonObject originals || originals.Count == 0 ||
            originals.Any(pair => !Digest(pair.Value?.ToString()) || FileHash(SourcePath(root, pair.Key), 8 * 1024 * 1024) != pair.Value!.ToString()))
            throw Invalid("all frozen oracle and input files");
        var binding = proof["binding"]!.AsObject();
        if (freeze["campaign"]?.ToString() != binding["campaign"]?.ToString() ||
            freeze["cohort"]?.ToString() != binding["cohort"]?.ToString() ||
            freeze["model"]?.ToString() != binding["model"]?.ToString() ||
            freeze["configuration_hash"]?.ToString() != binding["host_configuration_hash"]?.ToString() ||
            !JsonNode.DeepEquals(freeze["planning_configuration"], binding["configuration"]) || binding["configuration"] is not JsonObject ||
            Json("command")["argv"] is not JsonArray argv || argv.Count is < 2 or > 128 || argv.Any(a => a is not JsonValue || a.ToString().Length > 4096) ||
            !Argument(argv, "--campaign", binding["campaign"]?.ToString()) || !Argument(argv, "--cohort", binding["cohort"]?.ToString()))
            throw Invalid("the original invocation, input and planning configuration");
        proof["reviewed_at_utc"] = reviewed.ToString("O");
        proof["proof_sha256"] = _hash;
        return proof;
    }

    // The complete git path set, modes and current bytes must agree. Comparing the src
    // tree object covers added/deleted files and modes as well as all 1,128 blob identities.
    internal static void VerifySourceInventory(string root, string source, JsonObject inventory)
    {
        if (inventory["head"]?.ToString() != source || Git(root, "rev-parse", "HEAD") != source ||
            Git(root, "status", "--porcelain").Length != 0 ||
            Git(root, "rev-parse", source + ":src") != Git(root, "rev-parse", Baseline + ":src") ||
            Git(root, "ls-tree", "-r", "--name-only", Baseline, "--", "src").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != 1128 ||
            inventory["files"] is not JsonObject files || files.Count is < 1128 or > 12000)
            throw Invalid("a clean truthful harness with the complete unchanged baseline production tree");
        var tree = Git(root, "ls-tree", "-rz", "--full-tree", source).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (tree.Length != files.Count) throw Invalid("the complete build source inventory");
        foreach (var entry in tree)
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0) throw Invalid("the committed source inventory");
            var metadata = entry[..tab].Split(' '); var path = entry[(tab + 1)..]; var full = SourcePath(root, path);
            if (metadata.Length != 3 || metadata[0] is not ("100644" or "100755" or "120000") || metadata[1] != "blob" ||
                metadata[2].Length != 40 || !metadata[2].All(Uri.IsHexDigit) || !Digest(files[path]?.ToString()))
                throw Invalid("exact committed file types and hashes");
            if (new FileInfo(full).Length > 64 * 1024 * 1024) throw Invalid("bounded source bytes");
            var bytes = metadata[0] == "120000"
                ? Encoding.UTF8.GetBytes(new FileInfo(full).LinkTarget ?? throw Invalid("the original symbolic link"))
                : File.ReadAllBytes(full);
            // Git's SHA-1 is its existing object identity, not the security digest of the proof.
            // Compare it independently: index flags/stat caches can conceal changes from status.
            using var blob = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            blob.AppendData(Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0"));
            blob.AppendData(bytes);
            if (Convert.ToHexStringLower(blob.GetHashAndReset()) != metadata[2] || Hash(bytes) != files[path]!.ToString())
                throw Invalid("source bytes matching both committed blobs and the reviewed inventory");
        }
    }

    private static void VerifyExecutedQualification(JsonObject build, JsonObject validation, JsonObject inventory)
    {
        if (validation["execution"] is not JsonObject executionReference ||
            validation["executed_source_inventory"] is not JsonObject executedReference ||
            !JsonNode.DeepEquals(build["execution"], executionReference))
            throw Invalid("the original executed validation and source snapshot references");
        var execution = ReadJson(FilePath(executionReference), executionReference["sha256"]?.ToString() ?? "");
        var executed = ReadJson(FilePath(executedReference), executedReference["sha256"]?.ToString() ?? "");
        if (execution["source_snapshot_sha256"]?.ToString() != executedReference["sha256"]?.ToString() ||
            executed["files"] is not JsonObject || !JsonNode.DeepEquals(executed["files"], inventory["files"]) ||
            !new[] { "status", "tests", "build", "stages" }.All(name => JsonNode.DeepEquals(validation[name], execution[name])) ||
            execution["stages"] is not JsonArray stages ||
            stages.OfType<JsonObject>().Where(stage => stage["stage"]?.ToString() == "benchmark-build").ToArray() is not [var built] ||
            !JsonNode.DeepEquals(build["command"], built["command"]) || !JsonNode.DeepEquals(build["exit_code"], built["exit_code"]))
            throw Invalid("exact successful execution fields and complete executed/committed source equality");
    }

    internal static void ValidateQualification(JsonObject build, JsonObject validation)
    {
        static bool Wae(JsonNode? command) => command is JsonArray args && args.Any(arg => arg?.ToString() == "-warnaserror");
        if (build["exit_code"]?.GetValue<int>() != 0 || build["source_unchanged"]?.GetValue<bool>() != true || !Wae(build["command"]) ||
            validation["status"]?.ToString() != "focused_and_build_passed" ||
            validation["tests"]?["passed"]?.GetValue<int>() is not > 0 || validation["tests"]?["failed"]?.GetValue<int>() != 0 ||
            validation["tests"]?["skipped"]?.GetValue<int>() != 0 || validation["build"]?["passed"]?.GetValue<bool>() != true ||
            validation["build"]?["warnings_as_errors"]?.GetValue<bool>() != true ||
            validation["build"]?["diagnostics"] is not JsonArray { Count: 0 } ||
            validation["stages"] is not JsonArray stages || stages.Count is < 2 or > 32 ||
            stages.Any(stage => stage?["exit_code"]?.GetValue<int>() != 0 || !Wae(stage?["command"])) ||
            validation["source_inventory_sha256"]?.ToString() != build["source_inventory_sha256"]?.ToString())
            throw Invalid("successful warning-free build and focused validation of the exact source inventory");
    }

    internal static JsonObject ValidateRun(JsonObject proof, string campaign, string collection, string runKey,
        string source, PlanningSession state, JsonObject originalPlanningRequest, JsonObject request, JsonObject policy, JsonObject journal, JsonObject retry,
        JsonObject campaignConfiguration, DateTimeOffset firstRequest)
    {
        var binding = proof["binding"]!.AsObject();
        var configuration = PlanningBenchmarkMeasurements.Configuration(state.Request);
        // The current policy DTO has added fields. Compare the original legacy policy
        // object, not a reserialization that injects current defaults into its hash domain.
        configuration["policy"] = originalPlanningRequest["policy"]?.DeepClone();
        if (proof["source"]?.ToString() != source || binding["campaign"]?.ToString() != campaign ||
            binding["run_collection"]?.ToString() != collection || binding["run_key"]?.ToString() != runKey ||
            binding["session_id"]?.ToString() != state.Request.SessionId || binding["case"]?.ToString() != state.Request.Name ||
            Timestamp(proof["reviewed_at_utc"]) >= firstRequest ||
            !JsonNode.DeepEquals(binding["configuration"], configuration) ||
            binding["rendered_input_sha256"]?.ToString() != PlanningGraphCompiler.Fingerprint(state.Request.Prompt) ||
            !JsonNode.DeepEquals(binding["retry_policy"], retry) ||
            binding["campaign_configuration_sha256"]?.ToString() != PlanningGraphCompiler.Fingerprint(campaignConfiguration.ToJsonString()) ||
            binding["provider"]?.ToString() != campaignConfiguration["provider"]?.ToString() ||
            binding["model"]?.ToString() != campaignConfiguration["model"]?.ToString())
            throw Invalid("the pre-dispatch arm and original run/configuration identity");
        ValidateRunKey(binding, campaign, source, collection, runKey, state);
        ValidateTransport(state, request, policy, journal, retry, binding["provider"]!.ToString(), binding["model"]!.ToString());
        return new() { ["proof_sha256"] = proof["proof_sha256"]!.DeepClone(), ["baseline_source"] = Baseline,
            ["source"] = source, ["reviewed_at_utc"] = proof["reviewed_at_utc"]!.DeepClone(),
            ["artifact_hashes"] = new JsonObject(proof["references"]!.AsObject().Select(pair =>
                new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value!["sha256"]!.DeepClone()))) };
    }

    private static void ValidateRunKey(JsonObject binding, string campaign, string source, string collection, string key, PlanningSession state)
    {
        var cohort = PlanningBenchmarkMeasurements.Cohort(["--cohort", binding["cohort"]!.ToString()]);
        var repetition = binding["repetition"]?.GetValue<int>() ?? 0;
        var valid = repetition is >= 1 and <= 20 && (collection == "planning-evaluation-runs"
            ? binding["mode"]?.ToString() is "fixture" or "pilot" or "measured" && binding["arm"]?.ToString() is null or "judge" or "defaults" &&
                key == PlanningBenchmarkMeasurements.CorpusRunKey(source, binding["mode"]!.ToString(), binding["arm"]?.ToString(), cohort, state.Request.Name, repetition) &&
                state.Request.SessionId == PlanningGraphCompiler.Fingerprint(campaign + ":" + key)
            : collection == SchemaPortabilityCampaign.Collection && repetition <= 3 &&
                state.Request.SessionId == cohort + "-" + state.Request.Name + "-" + repetition.ToString(CultureInfo.InvariantCulture) && key == "run:" + state.Request.SessionId);
        if (!valid) throw Invalid("the original corpus/live session derivation");
    }

    internal static void ValidateTransport(PlanningSession state, JsonObject request, JsonObject policy, JsonObject journal,
        JsonObject retry, string provider, string model)
    {
        var effective = journal["effective_transport_policy"] as JsonObject;
        var expectedRetry = retry.DeepClone().AsObject(); expectedRetry["MaxAttempts"] = 1;
        var dispatched = request.DeepClone().AsObject();
        dispatched["provider"] = provider; dispatched["model"] = model;
        dispatched["useBackgroundMode"] = false; dispatched["disableTransportRetries"] = true;
        if (state.Construction?.Version != 1 || state.Request.Options["planning_transport_policy"] is not null ||
            policy["planning_transport_policy"] is not null || policy["construction_presentation_version"] is not null ||
            request.ContainsKey("requiredReviewCalls") || policy["http_usage_accounting_version"]?.GetValue<int>() != 2 ||
            policy["construction_review_headroom"]?.GetValue<int>() != 2 ||
            effective?["version"]?.GetValue<int>() != 2 || effective["mode"]?.ToString() != "baseline_incremental_single_attempt" ||
            effective["disable_transport_retries"]?.GetValue<bool>() != true || effective["use_background_mode"]?.GetValue<bool>() != false ||
            effective["allow_http_recovery"]?.GetValue<bool>() != false ||
            effective["request_hash"]?.ToString() != PlanningGraphCompiler.Fingerprint(request.ToJsonString()) ||
            effective["dispatch_request_hash"]?.ToString() != PlanningGraphCompiler.Fingerprint(dispatched.ToJsonString()) ||
            journal["effective_transport_policy_hash"]?.ToString() != PlanningGraphCompiler.Fingerprint(effective.ToJsonString()) ||
            !JsonNode.DeepEquals(effective["effective_retry_policy"], expectedRetry) ||
            string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
            throw Invalid("the exact original baseline single-attempt dispatch policy");
    }

    internal static void RequireSame(JsonNode? retained, JsonObject? proof)
    {
        if (retained is null && proof is null) return;
        if (retained is not JsonObject saved || proof is null || saved["proof_sha256"]?.ToString() != proof["proof_sha256"]?.ToString() ||
            saved["source"]?.ToString() != proof["source"]?.ToString())
            throw Invalid("the same independently reviewed frozen proof for closure readback");
    }
    private static bool Argument(JsonArray args, string name, string? value) => value is { Length: > 0 } &&
        args.Select((item, index) => (item, index)).Count(pair => pair.item?.ToString() == name) == 1 &&
        args.Select((item, index) => (item, index)).Any(pair => pair.item?.ToString() == name && pair.index + 1 < args.Count && args[pair.index + 1]?.ToString() == value);
    private static string SourcePath(string root, string path)
    {
        if (Path.IsPathRooted(path) || path.Split('/').Any(p => p is "" or "." or "..")) throw Invalid("a relative source path");
        return Path.Combine(root, path);
    }
    private static string FilePath(JsonObject reference) => reference["path"]?.ToString() is { Length: > 0 } path && Path.IsPathFullyQualified(path)
        ? path : throw Invalid("an absolute retained artifact path");
    private static JsonObject ReadJson(string path, string hash)
    {
        if (!Digest(hash) || FileHash(path, 8 * 1024 * 1024) != hash) throw Invalid("the expected immutable artifact hash");
        return JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject ?? throw Invalid("an object artifact");
    }
    private static string FileHash(string path, long limit, bool allowEmpty = false)
    {
        using var file = File.OpenRead(path);
        if (!allowEmpty && file.Length == 0 || file.Length > limit) throw Invalid("bounded artifact bytes");
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static DateTimeOffset Timestamp(JsonNode? value) => DateTimeOffset.TryParse(value?.ToString(), CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var time) ? time : throw Invalid("an exact UTC evidence timestamp");
    private static string Git(string root, params string[] args)
    {
        using var process = new Process { StartInfo = new("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        process.StartInfo.ArgumentList.Add("-c"); process.StartInfo.ArgumentList.Add("core.fsmonitor=false");
        foreach (var argument in args) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw Invalid("bounded local Git verification");
        }
        if (process.ExitCode != 0) throw Invalid("readable committed source objects (" + process.ExitCode + ")");
        return output.Result.TrimEnd('\r', '\n');
    }
    private static InvalidOperationException Invalid(string detail) => new("Baseline retention requires " + detail + ".");
}
