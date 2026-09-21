using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;
namespace GnOuGo.Flow.Planning;

/// <summary>Ephemeral, engine-issued edit targets. No arbitrary paths or host fields can be changed.</summary>
internal static class PlanningCorrections
{
    internal sealed record Target(string Id, string Path, string Shape, JsonNode? Fragment);
    internal const int MaxTargets = 3, MaxInputTokens = 12_000, MaxOutputTokens = 8_192;

    internal static IReadOnlyList<Target> Batch(PlanningSession state)
    {
        var targets = Targets(state);
        // A reservation is authoritative, including requests issued before batching existed.
        if (state.PendingCall is not null || targets.Count == 0) return targets;
        var intent = PlanningJsonTransport.Intent(state.IntentPlan!);
        var dependencies = Dependencies(state.IntentPlan!, intent);
        string Owner(Target t) => dependencies.Keys.Where(p => Within(t.Path, p)).OrderByDescending(p => p.Length).FirstOrDefault() ?? t.Path;
        bool DependsOn(Target consumer, Target producer)
        {
            if (Owner(consumer) == Owner(producer)) return consumer.Shape == "type" && producer.Shape is "value" or "members";
            var seen = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<string>(); pending.Push(Owner(consumer));
            while (pending.TryPop(out var path))
            {
                if (!seen.Add(path)) continue;
                if (dependencies.TryGetValue(path, out var upstream)) foreach (var source in upstream)
                {
                    if (Within(source, Owner(producer))) return true;
                    pending.Push(source);
                }
            }
            return false;
        }
        var order = Walk(intent, "").Select((item, index) => (item.Path, index)).ToDictionary(i => i.Path, i => i.index, StringComparer.Ordinal);
        var ordered = targets.OrderBy(t => order.GetValueOrDefault(t.Path, int.MaxValue)).ThenBy(t => t.Path, StringComparer.Ordinal).ToArray();
        var ready = ordered.Where(t => !ordered.Any(other => other != t && DependsOn(t, other))).ToArray();
        // Cycles already have topology targets. Do not fabricate a broader target to evade limits.
        if (ready.Length == 0) ready = [ordered[0]];
        static bool Group(Target t) => t.Shape is "block" or "operations" or "subflow" || t.Shape == "operation" && t.Fragment?["kind"]?.ToString() is "choose" or "each" or "parallel" or "cleanup";
        var batch = Group(ready[0]) ? new List<Target> { ready[0] } : ready.Where(t => !Group(t)).Take(MaxTargets).ToList();
        var allowance = Math.Min(MaxInputTokens, state.Request.Generation.MaxInputTokensPerRequest);
        while (true)
        {
            var size = PlanningJsonTransport.EstimateInputTokens(Prompt(state, batch), Schema(batch, state.Catalog));
            if (size <= allowance) return batch;
            if (batch.Count == 1) throw new WorkflowRuntimeException("MODEL_INPUT_LIMIT", $"Repair target {batch[0].Id} at {batch[0].Path} needs approximately {size} input tokens including its response schema; the repair limit is {allowance}. No request was reserved.");
            batch.RemoveAt(batch.Count - 1);
        }
    }

    private static bool Within(string path, string parent) => path == parent || path.StartsWith(parent + "/", StringComparison.Ordinal);
    private static string FlowPath(string path) => path.StartsWith("/subflows/", StringComparison.Ordinal) ? string.Join('/', path.Split('/').Take(3)) : "";
    private static string Scope(WorkflowIntentPlan intent, string path) => IntentTraversal.Blocks(intent).Where(b => Within(path, b.Path))
        .OrderByDescending(b => b.Path.Length).Select(b => b.Path).FirstOrDefault() ?? FlowPath(path);
    private static IEnumerable<(JsonNode Node, string Path)> Walk(JsonNode? node, string path, bool skipOperations = false)
    {
        if (node is null) yield break;
        yield return (node, path);
        if (node is JsonObject obj)
            foreach (var (key, value) in obj)
            {
                if (skipOperations && key == "operations") continue;
                foreach (var child in Walk(value, path + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal), skipOperations)) yield return child;
            }
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++) foreach (var child in Walk(array[i], path + "/" + i, skipOperations)) yield return child;
    }
    private static string? Source(WorkflowIntentPlan intent, JsonNode json, JsonObject value, string path)
    {
        var kind = value["kind"]?.ToString(); var source = value["source"]?.ToString();
        if (kind == "input")
        {
            var root = FlowPath(path) + "/inputs";
            return (PlanningFieldPaths.ReadOptional(json, root) as JsonArray)?.Select((v, i) => (v, i)).Where(p => p.v?["name"]?.ToString() == source).Select(p => root + "/" + p.i).FirstOrDefault();
        }
        if (kind != "result") return null;
        var scope = Scope(intent, path);
        return IntentTraversal.Located(intent).Where(o => o.Operation.Id == source && FlowPath(o.Path) == FlowPath(path) && Within(scope, Scope(intent, o.Path)))
            .OrderByDescending(o => Scope(intent, o.Path).Length).Select(o => o.Path).FirstOrDefault();
    }
    private static Dictionary<string, HashSet<string>> Dependencies(WorkflowIntentPlan intent, JsonObject json)
    {
        var operations = IntentTraversal.Located(intent).ToArray();
        var paths = operations.Select(o => o.Path).Concat(IntentTraversal.Blocks(intent).Select(b => b.Path))
            .Concat(Walk(json, "").Where(p => p.Node is JsonObject && p.Path.Split('/').Reverse().Skip(1).FirstOrDefault() is "inputs" or "outputs").Select(p => p.Path));
        var result = paths.Distinct(StringComparer.Ordinal).ToDictionary(p => p, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (path, upstream) in result)
        {
            void Read(JsonNode? fragment, string location)
            {
                foreach (var (node, valuePath) in Walk(fragment, location, skipOperations: true))
                    if (node is JsonObject value && Source(intent, json, value, valuePath) is { } source && source != path) upstream.Add(source);
            }
            Read(PlanningFieldPaths.ReadOptional(json, path), path);
            // Captured values inherit control inputs, but not unrelated operations in sibling blocks.
            foreach (var parent in operations.Where(o => o.Path != path && Within(path, o.Path)))
                foreach (var field in new[] { "when", "items", "condition" }) Read(PlanningFieldPaths.ReadOptional(json, parent.Path + "/" + field), parent.Path + "/" + field);
            var operation = operations.FirstOrDefault(o => o.Path == path).Operation;
            if (operation is not null)
            {
                foreach (var id in operation.After)
                {
                    var predecessor = operations.FirstOrDefault(o => o.Operation.Id == id && Scope(intent, o.Path) == Scope(intent, path));
                    if (predecessor.Operation is not null) upstream.Add(predecessor.Path);
                }
                if (operation is CallIntentOperation call)
                {
                    var index = intent.Subflows.FindIndex(s => s.Name == call.Flow);
                    if (index >= 0) foreach (var output in result.Keys.Where(p => p.StartsWith($"/subflows/{index}/outputs/", StringComparison.Ordinal))) upstream.Add(output);
                }
            }
        }
        return result;
    }
    internal static IReadOnlyList<Target> Targets(PlanningSession state)
    {
        var intent = PlanningJsonTransport.Intent(state.IntentPlan!);
        if (state.PendingCall is { Purpose: "repair" } pending)
        {
            var original = JsonNode.Parse(pending.Request.Prompt[pending.Request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
            return original["targets"]!.AsArray().Select(t => new Target(t!["id"]!.GetValue<string>(), t["path"]!.GetValue<string>(), t["shape"]!.GetValue<string>(), t["fragment"]?.DeepClone())).ToArray();
        }
        var operations = IntentTraversal.Located(state.IntentPlan!).ToArray();
        var targets = new List<Target>();
        foreach (var diagnostic in PlanningDiagnosticLocations.ForIntent(state).Where(d => d.Required))
        {
            // Invalid edits leave the original intent and its outstanding targets intact.
            if (diagnostic.Location is "/changes" || diagnostic.Location.StartsWith("/changes/", StringComparison.Ordinal)) continue;
            var path = diagnostic.Location; string shape;
            if (diagnostic.ValidationStage == "fixtures")
            {
                var domain = PlanningFixtureSamples.Domains(state).FirstOrDefault(d => d.Path == path);
                if (domain.Schema is null) continue;
                var values = PlanningFixtureSamples.Values(state, path);
                targets.Add(new("target_" + PlanningGraphCompiler.Fingerprint(path)[..16], path, path == "/fixtures/inputs" ? "fixture_inputs" : "fixture_responses",
                    values is null ? null : path == "/fixtures/inputs" ? values[0]?.DeepClone() : new JsonArray(values.Select(v => v?.DeepClone()).ToArray())));
                continue;
            }
            var operation = operations.Where(o => path == o.Path || path.StartsWith(o.Path + "/", StringComparison.Ordinal)).OrderByDescending(o => o.Path.Length).FirstOrDefault();
            if (diagnostic.Code is "DEPENDENCY_CYCLE" or "WORKFLOW_CYCLE" || operation.Operation is null && !path.StartsWith("/inputs/", StringComparison.Ordinal) && !path.StartsWith("/outputs/", StringComparison.Ordinal) && !path.StartsWith("/subflows/", StringComparison.Ordinal))
            { path = operation.Operation is null ? "/operations" : operation.Path[..operation.Path.LastIndexOf("/operations/", StringComparison.Ordinal)] + "/operations"; shape = "operations"; }
            else if (operation.Operation is not null)
            {
                var suffix = path[operation.Path.Length..];
                if (suffix == "/arguments") shape = "members";
                else if (suffix == "/resultType") shape = "type";
                else if (suffix.Length > 0 && PlanningFieldPaths.ReadOptional(intent, path) is JsonObject value && value.ContainsKey("kind")) shape = "value";
                else { path = operation.Path; shape = "operation"; }
            }
            else
            {
                var segments = path.Split('/');
                var port = Array.FindIndex(segments, p => p is "inputs" or "outputs");
                if (port < 0 && segments.Length >= 3 && segments[1] == "subflows") { path = "/subflows/" + segments[2]; shape = "subflow"; }
                else if (port < 0 || port + 1 >= segments.Length) { path = "/operations"; shape = "operations"; }
                else { shape = segments[port] == "inputs" ? "input" : "output"; path = string.Join('/', segments.Take(port + 2)); }
            }
            if (PlanningFieldPaths.ReadOptional(intent, path) is not { } fragment) continue;
            // A value-only edit cannot introduce a checked conversion before an opaque
            // result crosses a typed boundary. Issue an existing topology target instead.
            if (diagnostic.Code is "SCHEMA_REFERENCE_INVALID" or "SCHEMA_INVALID" or "OUTPUT_TYPE_MISMATCH" or "OUTPUT_REFERENCE_INVALID"
                && ConversionTarget(state, intent, path) is { } conversion)
            {
                path = conversion.Path; shape = conversion.Shape;
                fragment = PlanningFieldPaths.Read(intent, path)!;
            }
            targets.Add(new("target_" + PlanningGraphCompiler.Fingerprint(path)[..16], path, shape, fragment.DeepClone()));
        }
        // A group replacement subsumes nested edits. Atomic responses may not overlap.
        return targets.DistinctBy(t => t.Path).Where(t => !targets.Any(parent => parent.Path != t.Path && t.Path.StartsWith(parent.Path + "/", StringComparison.Ordinal))).ToArray();
    }

    private static (string Path, string Shape)? ConversionTarget(PlanningSession state, JsonObject intent, string path)
    {
        var plan = state.IntentPlan!;
        var operations = IntentTraversal.Located(plan).ToArray();
        var blocks = IntentTraversal.Blocks(plan).ToArray();
        foreach (var (node, location) in Walk(PlanningFieldPaths.ReadOptional(intent, path), path))
        {
            // Argument computations already have a receiving contract. Only exported
            // results need a topology edit to declare a new local result contract.
            if (!blocks.Any(b => Within(location, b.Path + "/result"))
                && !location.StartsWith(FlowPath(location) + "/outputs/", StringComparison.Ordinal)) continue;
            if (node is not JsonObject value || value["kind"]?.ToString() != "result"
                || Source(plan, intent, value, location) is not { } source
                || operations.FirstOrDefault(o => o.Path == source).Operation is not InvokeIntentOperation invoke
                || state.Catalog!.Capabilities.FirstOrDefault(c => c.Id == invoke.Capability)?.OutputSchema.Count != 0) continue;
            var block = blocks.Where(b => Within(location, b.Path) && Within(source, b.Path))
                .OrderByDescending(b => b.Path.Length).FirstOrDefault();
            if (block.Block is not null) return (block.Path, "block");
            // The enclosing list allows retaining the producer and adding a calculation
            // under its former business ID, so existing consumers need no invented value.
            return (source[..source.LastIndexOf("/operations/", StringComparison.Ordinal)] + "/operations", "operations");
        }
        return null;
    }
    internal static JsonObject Schema(IReadOnlyList<Target> targets, PlanningCatalog? catalog = null)
    {
        var variants = targets.Select(t => (JsonNode)new JsonObject {
            ["type"] = "object", ["properties"] = new JsonObject {
                ["target"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(t.Id) },
                ["replacement"] = t.Shape switch {
                    "operations" => new JsonObject { ["type"] = "array", ["items"] = Ref("operation") },
                    "members" => new JsonObject { ["type"] = "array", ["items"] = Ref("member") },
                    "fixture_inputs" => Ref("literal_object"), "fixture_responses" => Ref("literal_array"),
                    _ => Ref(t.Shape) } },
            ["required"] = new JsonArray("target", "replacement"), ["additionalProperties"] = false }).ToArray();
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
            ["changes"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = targets.Count, ["items"] = new JsonObject { ["anyOf"] = new JsonArray(variants) } } },
            ["required"] = new JsonArray("changes"), ["additionalProperties"] = false, ["$defs"] = PlanningSchemas.Definitions(
                catalog?.Capabilities.Where(c => !catalog.Policy.DeniedCapabilityIds.Contains(c.Id)).Select(c => c.Id)) };
        PlanningJsonTransport.PruneDefinitions(schema); return schema;
        static JsonObject Ref(string shape) => new() { ["$ref"] = "#/$defs/" + shape };
    }
    internal static string Prompt(PlanningSession state, IReadOnlyList<Target> targets)
    {
        var plan = state.IntentPlan!; var intent = PlanningJsonTransport.Intent(plan);
        var operations = IntentTraversal.Located(plan).ToArray();
        var dependencies = Dependencies(plan, intent);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var owner = dependencies.Keys.Where(p => Within(target.Path, p)).OrderByDescending(p => p.Length).FirstOrDefault();
            if (owner is not null) selected.Add(owner);
            foreach (var path in dependencies.Keys.Where(p => Within(p, target.Path))) selected.Add(path);
        }
        var relevant = selected.Concat(selected.SelectMany(p => dependencies.GetValueOrDefault(p) ?? []))
            .Concat(dependencies.Where(p => p.Value.Overlaps(selected)).Select(p => p.Key)).ToHashSet(StringComparer.Ordinal);
        // A referenced cleanup group is useful context for diagnosing an invalid predecessor.
        foreach (var group in operations.Where(o => o.Operation is CleanupIntentOperation && relevant.Contains(o.Path)))
            foreach (var child in operations.Where(o => Within(o.Path, group.Path))) relevant.Add(child.Path);
        string Placement((IntentOperation Operation, string Path) item) => item.Operation is CleanupIntentOperation ? "cleanup_group" :
            operations.Any(o => o.Operation is CleanupIntentOperation && Within(item.Path, o.Path) && Scope(plan, o.Path) == Scope(plan, item.Path)) ? "finalizer" : "main";
        var diagnostics = PlanningDiagnosticLocations.ForIntent(state).Where(d => targets.Any(t => Within(d.Location, t.Path) || Within(t.Path, d.Location))).ToList();
        var dependencyTargets = new JsonArray();
        foreach (var diagnostic in diagnostics.Where(d => d.Code is "DEPENDENCY_UNKNOWN" or "DEPENDENCY_SCOPE" or "DEPENDENCY_CYCLE"))
        {
            var item = operations.Where(o => Within(diagnostic.Location, o.Path)).OrderByDescending(o => o.Path.Length).FirstOrDefault();
            if (item.Operation is null || dependencyTargets.Any(t => t!["path"]!.ToString() == item.Path)) continue;
            var workflow = Workflow(state, item.Path);
            var eligible = workflow is null ? [] : PlanningStructureValidation.EligibleDependencies(workflow, item.Operation.Id);
            var cleanup = operations.Where(o => o.Operation is CleanupIntentOperation && Scope(plan, o.Path) == Scope(plan, item.Path) && item.Operation.After.Contains(o.Operation.Id)).Select(o => o.Operation.Id).ToArray();
            dependencyTargets.Add((JsonNode)new JsonObject { ["operation"] = item.Operation.Id, ["path"] = item.Path, ["scope"] = Scope(plan, item.Path) is "" ? "/" : Scope(plan, item.Path), ["placement"] = Placement(item),
                ["eligibleAfter"] = new JsonArray(eligible.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
                ["explanation"] = (cleanup.Length == 0 ? "" : "Referenced cleanup group " + string.Join(", ", cleanup) + " is not an executable predecessor. ") +
                    "These IDs are structurally eligible in the current graph, not business recommendations or permissions. In cleanup, after expresses ordering only, not successful completion or result availability. The engine guards required resource bindings; when expresses an explicit business condition. Empty after is valid when no extra sequencing is needed. All replacements are fully revalidated." });
        }
        var bindings = new JsonArray(); var contracts = new JsonArray(); var alternatives = new JsonArray();
        foreach (var path in relevant)
        {
            foreach (var (node, location) in Walk(PlanningFieldPaths.ReadOptional(intent, path), path, skipOperations: true))
            {
                if (node is not JsonObject value || value["kind"]?.ToString() is not ("input" or "result" or "item" or "index")) continue;
                var source = Source(plan, intent, value, location);
                if (!selected.Contains(path) && (source is null || !selected.Contains(source))) continue;
                if (bindings.Any(b => b!["location"]!.ToString() == location)) continue;
                var fieldPath = (value["path"] as JsonArray ?? []).Select(p => p!.ToString()).ToArray();
                var root = source is null ? null : Contract(state, source);
                var binding = new JsonObject { ["location"] = location, ["value"] = value.DeepClone(), ["sourcePath"] = source,
                    ["contract"] = root is null ? null : PlanningCapabilityCards.ValueContract(root, fieldPath),
                    ["contractStatus"] = root is null ? "unresolved" : root.Count == 0 ? "absent" : "declared" };
                // For downstream consumers, expose the binding's expected contract, not every argument.
                var consumer = operations.FirstOrDefault(o => o.Path == path).Operation;
                if (consumer is InvokeIntentOperation invoke && state.Catalog!.Capabilities.FirstOrDefault(c => c.Id == invoke.Capability) is { } capability)
                {
                    var argument = invoke.Arguments.Select((a, i) => (a.Name, Path: path + "/arguments/" + i + "/value")).FirstOrDefault(a => Within(location, a.Path));
                    if (argument.Name is not null) binding["consumerArgument"] = PlanningCapabilityCards.ValueContract(PlanningCapabilityCards.EditableArguments(capability), [argument.Name]);
                }
                bindings.Add((JsonNode)binding);
            }
        }
        foreach (var item in operations.Where(o => selected.Contains(o.Path)))
        {
            if (item.Operation is not InvokeIntentOperation invoke) continue;
            var capability = state.Catalog!.Capabilities.FirstOrDefault(c => c.Id == invoke.Capability);
            if (capability is not null)
            {
                var contract = contracts.FirstOrDefault(c => c!["id"]!.ToString() == capability.Id)?.AsObject();
                if (contract is null)
                {
                    contract = new() { ["id"] = capability.Id, ["arguments"] = PlanningCapabilityCards.EditableArguments(capability) };
                    contracts.Add((JsonNode)contract);
                }
                if (invoke.Fallback is not null && targets.Any(t => Within(item.Path + "/fallback", t.Path) || Within(t.Path, item.Path + "/fallback")))
                    contract["result"] = capability.OutputSchema.DeepClone();
            }
            else
            {
                // Advisory retrieval is independent of malformed arguments and deterministic eligibility.
                var query = invoke.Id + " " + invoke.Purpose + " " + string.Join(" ", invoke.Arguments.Select(a => a.Name)) + " " +
                    string.Join(" ", IntentTraversal.Values([invoke]).Select(v => v.Source + " " + string.Join(" ", v.Path)));
                alternatives.Add((JsonNode)new JsonObject { ["path"] = item.Path, ["advisory"] = true,
                    // A rejected tool name is useful retrieval evidence, never an executable alias.
                    ["capabilities"] = new JsonArray(PlanningCapabilityCards.Rank(state.Catalog.Capabilities, query)
                        .OrderByDescending(c => !string.IsNullOrWhiteSpace(invoke.Capability) && c.Method == invoke.Capability)
                        .Take(4).Select(c => (JsonNode)PlanningCapabilityCards.Card(c)).ToArray()) });
            }
        }
        return "Correct only the issued business intent targets. Return changes with target IDs and typed replacements. Do not return a complete intent. " +
            "The engine owns transport, result channels, catalog schemas and permissions. Keep business result references and named computation parameters. " +
            "An absent producer contract does not declare any fields or imply JSON text. When a topology target permits it, keep the real invocation and add a local calculate with an explicit derived resultType before crossing a typed boundary. " +
            "Validate required fields and types and throw on invalid data; parse JSON only after checking for a string. Do not use templates, coercions or fallback values to manufacture missing evidence. A known contract remains authoritative. " +
            "Fix the exact diagnostics; unchanged replacements will stop. A group target permits topology changes. Fixture targets use recursive literal values only; never business references or computations. " +
            "Deferred targets remain blocking and will be reconsidered after complete validation. Advisory capabilities do not grant permissions or replace deterministic eligibility.\n" + PlanningModelCalls.BusinessExamples + "\n" + new JsonObject {
                ["request"] = state.Request.Prompt, ["hostInstructions"] = state.Request.Policy.Instructions,
                ["diagnostics"] = JsonSerializer.SerializeToNode(diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic),
                ["rejectedEdits"] = JsonSerializer.SerializeToNode(state.Diagnostics.Where(d => Within(d.Location, "/changes")).ToList(), PlanningJsonContext.Default.ListPlanningDiagnostic),
                ["deferredTargetCount"] = Targets(state).Count(t => !targets.Any(selectedTarget => selectedTarget.Id == t.Id)),
                ["targets"] = new JsonArray(targets.Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["path"] = t.Path, ["shape"] = t.Shape, ["fragment"] = t.Fragment?.DeepClone() }).ToArray()),
                ["bindings"] = new JsonObject {
                    ["inputs"] = new JsonArray(relevant.Where(p => p.Split('/').Reverse().Skip(1).FirstOrDefault() == "inputs").Select(p => (JsonNode)new JsonObject {
                        ["path"] = p, ["declaration"] = selected.Contains(p) ? null : PlanningFieldPaths.Read(intent, p)!.DeepClone(), ["contract"] = Contract(state, p) }).ToArray()),
                    ["operations"] = new JsonArray(operations.Where(o => relevant.Contains(o.Path)).Select(o => (JsonNode)new JsonObject {
                        ["id"] = o.Operation.Id, ["path"] = o.Path, ["purpose"] = selected.Contains(o.Path) ? o.Operation.Purpose : null,
                        ["kind"] = PlanningFieldPaths.Read(intent, o.Path)!["kind"]!.DeepClone(), ["scope"] = Scope(plan, o.Path) is "" ? "/" : Scope(plan, o.Path),
                        ["placement"] = Placement(o), ["after"] = PlanningFieldPaths.Read(intent, o.Path)!["after"]?.DeepClone(),
                        ["when"] = PlanningFieldPaths.Read(intent, o.Path)!["when"]?.DeepClone() }).ToArray()), ["values"] = bindings,
                    ["controls"] = new JsonArray(operations.Where(o => selected.Any(p => p != o.Path && Within(p, o.Path))).Select(o => (JsonNode)new JsonObject {
                        ["path"] = o.Path, ["id"] = o.Operation.Id,
                        ["when"] = PlanningFieldPaths.Read(intent, o.Path)!["when"]?.DeepClone(),
                        ["condition"] = PlanningFieldPaths.Read(intent, o.Path)!["condition"]?.DeepClone(),
                        ["items"] = PlanningFieldPaths.Read(intent, o.Path)!["items"]?.DeepClone() }).ToArray()) },
                ["dependencyTargets"] = dependencyTargets,
                ["fixtureContracts"] = new JsonArray(PlanningFixtureSamples.Domains(state).Where(d => targets.Any(t => t.Path == d.Path)).Select(d => (JsonNode)new JsonObject { ["path"] = d.Path, ["schema"] = d.Schema.DeepClone() }).ToArray()),
                ["contracts"] = contracts, ["alternatives"] = alternatives
            }.ToJsonString();
    }
    private static PlanningWorkflow? Workflow(PlanningSession state, string path)
    {
        if (state.Graph is null) return null;
        var owner = IntentTraversal.GraphOwner(state.IntentPlan!, path);
        return (owner == "main" ? state.Graph.Workflows.FirstOrDefault(w => w.Key == PlanningConfirmationGuards.Body) : null)
            ?? state.Graph.Workflows.FirstOrDefault(w => w.Key == owner);
    }
    private static JsonObject? Contract(PlanningSession state, string path)
    {
        var operation = IntentTraversal.Located(state.IntentPlan!).FirstOrDefault(o => o.Path == path).Operation;
        if (operation is InvokeIntentOperation invoke) return state.Catalog!.Capabilities.FirstOrDefault(c => c.Id == invoke.Capability)?.OutputSchema.DeepClone().AsObject();
        var workflow = Workflow(state, path); if (workflow is null) return null;
        try
        {
            if (operation is null)
            {
                var declaration = PlanningFieldPaths.ReadOptional(PlanningJsonTransport.Intent(state.IntentPlan!), path);
                var input = workflow.Inputs.FirstOrDefault(i => i.Name == declaration?["name"]?.ToString());
                return input is null ? null : PlanningGraphCompiler.ToJsonSchema(input.Schema, state.Catalog!);
            }
            var value = new PlanningValue { Kind = "output", Source = operation.Id, ResultChannel = operation is TransformIntentOperation ? "structured" : null,
                Path = operation is CalculateIntentOperation or EachIntentOperation or ChooseIntentOperation or ParallelIntentOperation ? ["value"] : [] };
            return PlanningGraphValidation.ResolveValueContract(state.Graph!, workflow, value, state.Catalog!).DeepClone().AsObject();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { return null; }
    }
    internal static WorkflowIntentPlan Apply(PlanningSession state, IReadOnlyList<Target> targets, JsonNode response)
    {
        var updated = PlanningJsonTransport.Intent(state.IntentPlan!);
        var fixtures = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.Fixtures ?? new(), PlanningJsonContext.Default.PlanningFixtures), PlanningJsonContext.Default.PlanningFixtures)!;
        var fixtureChanged = false; var changed = new HashSet<string>(StringComparer.Ordinal);
        var changedPaths = new List<string>();
        foreach (var edit in response["changes"]!.AsArray())
        {
            var id = edit!["target"]!.GetValue<string>(); var target = targets.SingleOrDefault(t => t.Id == id);
            if (target is null || !changed.Add(id) || changedPaths.Any(p => Within(p, target.Path) || Within(target.Path, p)))
                throw new PlanningResponseException([new("REPAIR_TARGET_INVALID", "/changes", "Select non-overlapping issued targets, each at most once.", ValidationStage: "intent")]);
            changedPaths.Add(target.Path);
            if (target.Shape.StartsWith("fixture_", StringComparison.Ordinal))
            {
                fixtureChanged = true;
                var literal = JsonSerializer.Deserialize(edit["replacement"], PlanningJsonContext.Default.PlanningValue)!;
                if (!PlanningGraphValidation.IsLiteral(literal)) throw new PlanningResponseException([new("SCENARIO_FIXTURE_INVALID", target.Path, "Only literal fixture values are accepted.", ValidationStage: "fixtures")]);
                var value = PlanningGraphValidation.Literal(literal);
                if (target.Shape == "fixture_inputs") fixtures.Inputs = value!.AsObject();
                else
                {
                    var parts = target.Path.Split('/'); var workflow = PlanningFixtureSamples.Unescape(parts[3]); var node = PlanningFixtureSamples.Unescape(parts[4]);
                    fixtures.Observations.RemoveAll(o => o.Workflow == workflow && o.Node == node);
                    fixtures.Observations.Add(new(workflow, node, value!.AsArray().Select(v => v?.DeepClone()).ToList()));
                }
            }
            else PlanningFieldPaths.Replace(updated, target.Path, edit["replacement"]);
        }
        var errors = PlanningContractValidation.ValidateInstanceFindings(updated, PlanningSchemas.Intent());
        if (errors.Count > 0) throw new PlanningResponseException(errors.Select(f => new PlanningDiagnostic("INTENT_SCHEMA_INVALID", f.InstancePointer, f.Message, ValidationStage: "intent")).ToList());
        var result = JsonSerializer.Deserialize(updated, PlanningJsonContext.Default.WorkflowIntentPlan)!;
        if (fixtureChanged) state.Fixtures = fixtures; return result;
    }
}
