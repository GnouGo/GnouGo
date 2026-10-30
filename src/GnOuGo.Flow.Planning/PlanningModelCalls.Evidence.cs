using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningModelCalls
{
    private static readonly HashSet<string> EvidenceFields = new(StringComparer.Ordinal)
    { "evidenceRequests", "contextRequests", "evidenceAddresses" };
    private static readonly HashSet<string> NavigationFields = new(StringComparer.Ordinal)
    {
        "address", "evidence", "next", "nextPorts", "nextRequirements", "nextScopes", "nextInterfaces", "objectiveEvidence", "remainingEvidence",
        "scopeEvidence", "taskEvidence", "descriptionEvidence", "schemaEvidence", "directory", "inputDirectory", "outputDirectory",
        "outputSchemaDirectory", "interfacesDirectory", "requirementsDirectory", "schema", "contract", "bindingEvidence",
        "evidenceRequests", "contextRequests", "evidenceAddresses", "findingEvidence", "scopes", "omittedOptionalInputs", "omittedOptionalSiblings"
    };
    private static readonly HashSet<string> ContextMetadata = new(StringComparer.Ordinal)
    {
        "sources", "requirements", "globalConstraints", "originals", "scopes", "scopeInterface", "scopeOwner", "scopeGroup", "interfaces",
        "reusableInterfaces", "tasks", "acceptedTasks", "producers", "operations", "knownOperations", "describedOperations", "descriptors",
        "providerRequests", "continuation", "repair", "availableInputs", "groups", "inputs", "publicOutputs", "actions", "step", "diagnostics", "originalDirectory",
        "taskEvidence", "groupEvidence", "rebuild", "reselection", "companionOperations"
    };
    private static readonly HashSet<string> MetadataLists = new(StringComparer.Ordinal)
    {
        "items", "inputs", "outputs", "groups", "tasks", "ancestors", "selectedOutputs", "selectedInputs", "producers", "scopes",
        "acceptedCheckpoints", "acceptedProgress", "descendants", "contracts", "ports", "originals", "prerequisites", "sources", "omittedOptionalInputs"
    };
    private static readonly HashSet<string> MetadataObjects = new(StringComparer.Ordinal)
    { "contract", "consumer", "prerequisite", "scopeOwner", "scopeGroup", "bindings", "interface", "resumption", "suspendedConsumer", "acceptedProgress", "acceptedExports" };

    private static PlanningEvidenceAddresses? PrepareEvidenceAddresses(PlanningSession state, LLMRequest request)
    {
        var (prefix, context) = PromptContext(request.Prompt);
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        VisitSchema(request.StructuredOutputSchema, value => { if (CanonicalAddress(value)) addresses.Add(value); return value; });
        var contextAddresses = new HashSet<string>(StringComparer.Ordinal);
        VisitContext(context, value => { if (CanonicalAddress(value)) contextAddresses.Add(value); return value; });
        var businessReview = HybridWorkflowPlanner.ConstructionReviewIssued(request.StructuredOutputSchema);
        if (businessReview)
            VisitReviewIdentifiers(context, request.StructuredOutputSchema, (kind, value) =>
            { contextAddresses.Add(kind + ":" + value); return value; });
        addresses.UnionWith(contextAddresses);
        if (addresses.Count == 0) return null;
        var mapping = new PlanningEvidenceAddresses { ReviewImplementationIds = businessReview };
        var scope = PlanningGraphCompiler.Fingerprint(state.Request.SessionId + "\n" +
            (state.ModelCalls + 1).ToString(CultureInfo.InvariantCulture) + "\n" + JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest))[..8];
        foreach (var address in addresses.Order(StringComparer.Ordinal))
            mapping.Addresses.Add("e" + scope + "_" + (mapping.Addresses.Count + 1).ToString(CultureInfo.InvariantCulture), address);
        mapping.MapHash = EvidenceMapHash(mapping.Addresses, mapping.ReviewImplementationIds);
        var reverse = mapping.Addresses.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        string Replace(string value) => reverse.GetValueOrDefault(value, value);
        VisitSchema(request.StructuredOutputSchema, Replace);
        VisitContext(context, Replace);
        if (businessReview) VisitReviewIdentifiers(context, request.StructuredOutputSchema, (kind, value) => Replace(kind + ":" + value));
        var navigation = new JsonArray(mapping.Addresses.Where(pair => !contextAddresses.Contains(pair.Value)).Select(pair => (JsonNode)new JsonObject
            { ["id"] = pair.Key, ["label"] = EvidenceLabel(pair.Value, context) }).ToArray());
        context ??= new JsonObject();
        if (navigation.Count > 0) context["evidenceNavigation"] = navigation;
        request.Prompt = prefix + "\n" + PlanningJsonTransport.Prompt(context);
        return mapping;
    }

    // Only the transient review view uses aliases for implementation identities.
    // Canonical TaskPlan references, public names, literals, originals and receipts stay exact.
    private static void VisitReviewIdentifiers(JsonObject? context, JsonNode? schema, Func<string, string, string> replace)
    {
        if (context is null) return;
        foreach (var action in (context["actions"] as JsonArray ?? []).OfType<JsonObject>())
            Action(action);
        foreach (var scope in (context["scopes"] as JsonArray ?? []).OfType<JsonObject>())
            Scope(scope);
        foreach (var input in (context["inputs"] as JsonArray ?? []).OfType<JsonObject>()) Field(input, "scope", "scope");
        foreach (var group in (context["groups"] as JsonArray ?? []).OfType<JsonObject>()) Field(group, "body", "scope");
        Values(context["publicOutputs"]); List(context["fragment"], "task");
        if (context["claims"] is JsonObject claims) { Field(claims, "scope", "scope"); List(claims["closedScopes"], "scope"); }
        foreach (var transition in (context["transitions"] as JsonArray ?? []).OfType<JsonObject>())
        { Field(transition, "scope", "scope"); Values(transition["outputs"]); }
        if (context["continuation"] is JsonObject continuation)
        {
            Values(continuation["binding"]); Consumer(continuation["consumer"]);
            foreach (var child in (continuation["descendants"] as JsonArray ?? []).OfType<JsonObject>()) Consumer(child["consumer"]);
        }
        foreach (var concern in (context["priorConcerns"] as JsonArray ?? []).OfType<JsonObject>())
        { Snapshot(concern["before"]); Snapshot(concern["current"]); }
        Targets(context["interpretationReconsideration"]?["priorReport"]?["decision"]?["targets"]);
        if (schema?["properties"]?["decision"]?["anyOf"] is JsonArray decisions)
            foreach (var decision in decisions) Targets(decision?["properties"]?["targets"]?["items"]?["enum"]);

        void Action(JsonObject action)
        {
            Field(action, "id", "task"); Field(action, "scope", "scope"); List(action["after"], "task");
            foreach (var name in new[] { "arguments", "outputs", "requires", "condition", "items" }) Values(action[name]);
        }
        void Scope(JsonObject scope)
        { Field(scope, "id", "scope"); List(scope["tasks"], "task"); List(scope["cleanup"], "task"); Values(scope["outputs"]); }
        void Snapshot(JsonNode? node)
        {
            if (node is JsonObject snapshot && snapshot["id"] is JsonValue)
            {
                if (snapshot["kind"] is JsonValue && snapshot["scope"] is JsonValue) { Action(snapshot); return; }
                if (snapshot["open"] is JsonValue && snapshot["tasks"] is JsonArray) { Scope(snapshot); return; }
                if (snapshot["body"] is JsonValue) Field(snapshot, "body", "scope");
            }
            Values(node);
        }
        void Field(JsonObject owner, string name, string kind)
        { if (owner[name] is JsonValue value && value.TryGetValue<string>(out var text)) owner[name] = replace(kind, text); }
        void List(JsonNode? node, string kind)
        {
            if (node is not JsonArray list) return;
            for (var index = 0; index < list.Count; index++)
                if (list[index] is JsonValue value && value.TryGetValue<string>(out var text)) list[index] = replace(kind, text);
        }
        void Targets(JsonNode? node)
        {
            if (node is not JsonArray targets) return;
            for (var index = 0; index < targets.Count; index++)
                if (targets[index] is JsonValue value && value.TryGetValue<string>(out var text) && !text.Contains(':', StringComparison.Ordinal))
                    targets[index] = replace(text.StartsWith('/') ? "scope" : "task", text);
        }
        void Consumer(JsonNode? node)
        { if (node is JsonObject consumer) { Field(consumer, "scope", "scope"); Values(consumer["inputs"]); } }
        void Values(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Values(item); return; }
            if (node is not JsonObject value) return;
            if (value["kind"]?.ToString() is "output" or "present") Field(value, "source", "task");
            // Literal data is represented by text/number/boolean, not traversed as metadata.
            foreach (var key in new[] { "value", "items", "members" }) Values(value[key]);
        }
    }

    private static string EvidenceLabel(string address, JsonObject? context)
    {
        var colon = address.IndexOf(':');
        var kind = address[..colon]; var target = address[(colon + 1)..];
        var at = target.IndexOf('@');
        if (at >= 0)
        {
            var end = target.IndexOf(':', at);
            target = target[..at] + (end < 0 ? "" : target[end..]);
        }
        var parts = target.Split(':');
        var identity = parts[0];
        var label = FindLabel(context, identity, 0) ?? identity;
        var remainder = parts.Length > 1 ? " · " + string.Join(" · ", parts.Skip(1).Where(part => !HashText(part))) : "";
        var value = kind switch
        {
            "source" or "source-description" => "Original source " + target,
            "requirement" => "Obligation " + label,
            "input" => "Input " + label, "output" => "Public output " + label, "choice" => "Choice " + label,
            "group" => "Reusable group " + label, "task" => "Task " + label, "task-objective" => "Original objective " + label,
            "scope" or "scope-exports" or "originalScope" => "Scope " + target,
            "description" => "Operation description " + label + remainder,
            "contract" or "original" => "Operation contract " + label + remainder,
            "schema" or "schema-page" or "port-page" => "Port/schema " + label + remainder,
            "page" => "More " + string.Join(" · ", parts.Where(part => !HashText(part))),
            "rejected" => "Rejected work " + target, _ => kind + " " + target
        };
        return value.Length <= 240 ? value : value[..237] + "...";

        static bool HashText(string text) => text.Length == 64 && text.All(char.IsAsciiHexDigit);
        static string? FindLabel(JsonNode? node, string id, int depth)
        {
            if (depth > 8) return null;
            if (node is JsonArray array) return array.Select(item => FindLabel(item, id, depth + 1)).FirstOrDefault(item => item is not null);
            if (node is not JsonObject obj) return null;
            if (new[] { "id", "task", "name" }.Any(key => obj[key]?.ToString() == id))
                foreach (var key in new[] { "name", "objective", "original" })
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text)) return id + " · " + text;
            return obj.Where(pair => ContextMetadata.Contains(pair.Key) || MetadataLists.Contains(pair.Key) || MetadataObjects.Contains(pair.Key))
                .Select(pair => FindLabel(pair.Value, id, depth + 1)).FirstOrDefault(item => item is not null);
        }
    }

    private static (string Prefix, JsonObject? Context) PromptContext(string prompt)
    {
        var offset = prompt.LastIndexOf("\n{", StringComparison.Ordinal);
        if (offset < 0) return (prompt, null);
        try { return (prompt[..offset], JsonNode.Parse(prompt[(offset + 1)..]) as JsonObject); }
        catch (JsonException) { return (prompt, null); }
    }

    private static bool CanonicalAddress(string value) => PlanningCompactConstruction.NavigationEvidence([JsonValue.Create(value)]).Count == 1;
    private static bool ShortAddress(string value) => value.Length > 10 && value[0] == 'e' && value[9] == '_' &&
        value.AsSpan(1, 8).IndexOfAnyExcept("0123456789abcdef") < 0 && value[10] is >= '1' and <= '9' &&
        value.AsSpan(10).IndexOfAnyExceptInRange('0', '9') < 0;

    // Only navigation slots are rewritten. Original schemas, TaskValue literals,
    // references and descriptions retain their exact bytes and coordinates.
    private static void VisitContext(JsonNode? node, Func<string, string> replace)
    {
        if (node is not JsonObject context) return;
        foreach (var pair in context.ToArray())
        {
            if (pair.Key == "evidence" && pair.Value is JsonObject evidence)
            {
                foreach (var item in evidence.ToArray())
                {
                    var key = replace(item.Key);
                    if (key != item.Key) { evidence.Remove(item.Key); evidence.Add(key, item.Value); }
                    Metadata(item.Value);
                }
            }
            else if (pair.Key == "directoryEvidence" && pair.Value is JsonObject directory)
                foreach (var item in directory.ToArray()) Links(directory, item.Key, item.Value);
            else if (ContextMetadata.Contains(pair.Key))
            {
                if (pair.Value is JsonValue) Links(context, pair.Key, pair.Value);
                else Metadata(pair.Value);
            }
            else if (NavigationFields.Contains(pair.Key)) Links(context, pair.Key, pair.Value);
        }
        void Links(JsonObject owner, string name, JsonNode? value)
        {
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) owner[name] = replace(text);
            else if (value is JsonArray links)
                for (var index = 0; index < links.Count; index++)
                    if (links[index] is JsonValue link && link.TryGetValue<string>(out var address)) links[index] = replace(address);
        }
        void Metadata(JsonNode? value)
        {
            if (value is JsonArray entries) { foreach (var entry in entries) Metadata(entry); return; }
            if (value is not JsonObject metadata || metadata.ContainsKey("properties") || metadata.ContainsKey("$ref")) return;
            foreach (var pair in metadata.ToArray())
            {
                if (NavigationFields.Contains(pair.Key)) Links(metadata, pair.Key, pair.Value);
                if (MetadataLists.Contains(pair.Key) && pair.Value is JsonArray || MetadataObjects.Contains(pair.Key) && pair.Value is JsonObject)
                    Metadata(pair.Value);
            }
        }
    }

    private static void VisitSchema(JsonNode? root, Func<string, string> replace)
    {
        Visit(root, false, new HashSet<string>(StringComparer.Ordinal));
        void Visit(JsonNode? node, bool evidence, HashSet<string> references)
        {
            if (node is JsonArray array) { foreach (var item in array) Visit(item, evidence, references); return; }
            if (node is not JsonObject obj) return;
            if (obj["$ref"]?.GetValue<string>() is { } reference && reference.StartsWith("#/$defs/", StringComparison.Ordinal) && references.Add(reference))
            {
                if (PlanningConstruction.TryPointer(root!, reference[1..], out var target) && target is JsonObject resolved &&
                    (evidence || ContainsEvidence(resolved, new HashSet<string>(StringComparer.Ordinal))))
                {
                    // A shared definition may also describe original operation input
                    // data. Specialize the navigation use, never mutate that original.
                    var copy = resolved.DeepClone().AsObject();
                    Visit(copy, evidence, references);
                    obj.Remove("$ref"); foreach (var pair in copy.ToArray()) { copy.Remove(pair.Key); obj[pair.Key] = pair.Value; }
                    references.Remove(reference);
                    return; // The specialized subtree was already walked with its ancestor guard.
                }
                references.Remove(reference);
            }
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "enum" && evidence && pair.Value is JsonArray choices)
                {
                    for (var index = 0; index < choices.Count; index++)
                        if (choices[index] is JsonValue value && value.TryGetValue<string>(out var text)) choices[index] = replace(text);
                }
                else if (pair.Key is not ("$defs" or "extension" or "patch" or "interfaces"))
                    Visit(pair.Value, evidence || EvidenceFields.Contains(pair.Key), references);
            }
        }
        bool ContainsEvidence(JsonNode? node, HashSet<string> references)
        {
            if (node is JsonArray array) return array.Any(item => ContainsEvidence(item, references));
            if (node is not JsonObject obj) return false;
            if (obj["$ref"]?.GetValue<string>() is { } reference && reference.StartsWith("#/$defs/", StringComparison.Ordinal) && references.Add(reference))
            {
                var found = PlanningConstruction.TryPointer(root!, reference[1..], out var target) && ContainsEvidence(target, references);
                references.Remove(reference);
                if (found) return true;
            }
            return obj.Any(pair => pair.Key is not ("$defs" or "extension" or "patch" or "interfaces") &&
                (EvidenceFields.Contains(pair.Key) || ContainsEvidence(pair.Value, references)));
        }
    }

    private static string RequestHash(LLMRequest request) => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
    private static string EvidenceMapHash(Dictionary<string, string> addresses, bool reviewImplementationIds = false) => PlanningGraphCompiler.Fingerprint(new JsonObject(
        addresses.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)))).ToJsonString() +
        (reviewImplementationIds ? "\nreview-implementation-identities" : ""));

    private static string IssuedIdentityHash(LLMRequest request, PlanningEvidenceAddresses? mapping)
    {
        var id = request.ClientRequestId;
        try
        {
            request.ClientRequestId = null;
            var bytes = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
            var basis = PlanningGraphCompiler.Fingerprint(bytes);
            if (mapping is null) return basis;
            if (mapping.RequestBasisHash.Length == 0) mapping.RequestBasisHash = basis;
            else if (mapping.RequestBasisHash != basis) throw new PlanningConflictException("The issued evidence request basis changed.");
            // Production journals require this exact-request fingerprint at
            // the end of ClientRequestId. The preceding host-only map tag
            // binds navigation authority without changing that shared rule.
            return basis;
        }
        finally { request.ClientRequestId = id; }
    }

    internal static void ValidateEvidenceAddresses(PlanningModelCall call)
    {
        if (call.Id != call.Request.ClientRequestId) throw new PlanningConflictException("The issued evidence request identity changed.");
        var mapping = call.EvidenceAddresses;
        ValidateEvidenceMap(mapping, RequestHash(call.Request));
        var offered = new HashSet<string>(StringComparer.Ordinal);
        string Check(string value)
        {
            if (CanonicalAddress(value)) throw new PlanningConflictException("The issued request contains unmapped evidence navigation.");
            if (ShortAddress(value)) offered.Add(value);
            return value;
        }
        VisitSchema(call.Request.StructuredOutputSchema?.DeepClone(), Check);
        var context = PromptContext(call.Request.Prompt).Context;
        VisitContext(context, Check);
        if (mapping?.ReviewImplementationIds == true)
        {
            if (!HybridWorkflowPlanner.ConstructionReviewIssued(call.Request.StructuredOutputSchema))
                throw new PlanningConflictException("Implementation aliases require an exact issued business review.");
            VisitReviewIdentifiers(context, call.Request.StructuredOutputSchema?.DeepClone(), (_, value) =>
            {
                if (ShortAddress(value)) offered.Add(value);
                return value;
            });
        }
        if (offered.Any(id => mapping?.Addresses.ContainsKey(id) != true) || mapping is not null &&
            !call.Id.EndsWith(":" + mapping.MapHash + ":" + IssuedIdentityHash(call.Request, mapping), StringComparison.Ordinal))
            throw new PlanningConflictException("The issued evidence map does not match the exact request.");
    }

    private static void ValidateEvidenceMap(PlanningEvidenceAddresses? mapping, string requestHash)
    {
        if (mapping is null) return;
        if (mapping.RequestHash != requestHash || mapping.Addresses is null || mapping.Addresses.Count == 0 ||
            mapping.MapHash != EvidenceMapHash(mapping.Addresses, mapping.ReviewImplementationIds) || mapping.RequestBasisHash is not { Length: 64 } ||
            !mapping.RequestBasisHash.All(char.IsAsciiHexDigit) ||
            mapping.Addresses.Keys.Any(key => !ShortAddress(key)) || mapping.Addresses.Values.Any(value => !CanonicalAddress(value)) ||
            mapping.Addresses.Values.Distinct(StringComparer.Ordinal).Count() != mapping.Addresses.Count)
            throw new PlanningConflictException("The saved evidence mapping is missing, stale or conflicting.");
    }

    internal static JsonNode NormalizeEvidenceResponse(JsonNode response, PlanningEvidenceAddresses? mapping)
    {
        var normalized = response.DeepClone();
        VisitResponse(normalized);
        if (mapping?.ReviewImplementationIds == true && normalized["decision"]?["targets"] is JsonArray targets)
            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index]!.GetValue<string>();
                if (!ShortAddress(target)) continue;
                if (mapping is null || !mapping.Addresses.TryGetValue(target, out var canonical) ||
                    !(canonical.StartsWith("task:", StringComparison.Ordinal) || canonical.StartsWith("scope:", StringComparison.Ordinal)))
                    throw new PlanningConflictException("The review used an unknown implementation ID.");
                targets[index] = canonical[(canonical.IndexOf(':') + 1)..];
            }
        return normalized;
        void VisitResponse(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) VisitResponse(item); return; }
            if (node is not JsonObject obj) return;
            foreach (var pair in obj)
                if (EvidenceFields.Contains(pair.Key) && pair.Value is JsonArray addresses)
                {
                    for (var index = 0; index < addresses.Count; index++)
                    {
                        var id = addresses[index]?.GetValue<string>() ?? throw new PlanningConflictException("An evidence ID is missing.");
                        if (mapping?.Addresses.TryGetValue(id, out var canonical) != true)
                            throw new PlanningConflictException("The response used an unknown evidence ID.");
                        addresses[index] = canonical;
                    }
                }
                else if (pair.Key is not ("extension" or "patch" or "interfaces")) VisitResponse(pair.Value);
        }
    }

    // Existing host validators consume canonical navigation; the provider and
    // receipt journals retain the original short-ID request unchanged.
    internal static LLMRequest CanonicalEvidenceRequest(PlanningModelCall call)
    {
        ValidateEvidenceAddresses(call);
        var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(call.Request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        if (call.EvidenceAddresses is not { } mapping) return request;
        string Resolve(string value) => mapping.Addresses.GetValueOrDefault(value, value);
        var (prefix, context) = PromptContext(request.Prompt);
        VisitContext(context, Resolve);
        context?.Remove("evidenceNavigation");
        if (context is not null) request.Prompt = prefix + "\n" + PlanningJsonTransport.Prompt(context);
        VisitSchema(request.StructuredOutputSchema, Resolve);
        if (mapping.ReviewImplementationIds)
        {
            VisitReviewIdentifiers(context, request.StructuredOutputSchema, (kind, value) =>
                mapping.Addresses.TryGetValue(value, out var canonical) &&
                    (canonical.StartsWith("task:", StringComparison.Ordinal) || canonical.StartsWith("scope:", StringComparison.Ordinal))
                    ? canonical[(canonical.IndexOf(':') + 1)..] : value);
            if (context is not null) request.Prompt = prefix + "\n" + PlanningJsonTransport.Prompt(context);
        }
        return request;
    }

    internal static bool ReviewEvidenceBindingValid(JsonObject row)
    {
        try
        {
            // Every current business review issues navigation, including clean
            // reports which did not request any of it. Its captured map remains
            // required receipt provenance; no-ID responses cannot waive it.
            if (row["evidenceAddresses"] is not JsonObject saved || row["requestHash"] is not JsonValue hash ||
                !hash.TryGetValue<string>(out var requestHash) ||
                saved.Deserialize(PlanningJsonContext.Default.PlanningEvidenceAddresses) is not { } mapping) return false;
            ValidateEvidenceMap(mapping, requestHash);
            if (row["requestId"]?.ToString().EndsWith(":" + mapping.MapHash + ":" + mapping.RequestBasisHash,
                StringComparison.Ordinal) != true) return false;
            return JsonNode.DeepEquals(NormalizeEvidenceResponse(row["rawResponse"]!, mapping), row["response"]);
        }
        catch (Exception error) when (error is PlanningConflictException or JsonException or InvalidOperationException) { return false; }
    }
}
