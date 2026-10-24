using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning;

// These are non-executable views of retained producer contracts. Neither discovery
// descriptors nor a projected schema replaces the catalog used by the compiler.
internal static partial class PlanningCompactConstruction
{
    private const int ContractDirectoryPageSize = 8;
    private const int ContractDescriptorPageSize = 2;
    private const int CompactContractDescriptorPageSize = 4;
    private const int SourceDirectoryPageSize = 2;
    private const int ContractDescriptionPageSize = 2048;

    internal static JsonObject SourceDescriptors(PlanningSession state, int offset = 0)
    {
        if (offset < 0 || offset > state.Discovery.Sources.Count)
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "The capability-source directory offset is outside the retained metadata.");
        return new JsonObject
        {
            ["offset"] = offset, ["total"] = state.Discovery.Sources.Count,
            ["items"] = new JsonArray(state.Discovery.Sources.Skip(offset).Take(SourceDirectoryPageSize).Select(source =>
            {
                var description = NavigationDescription(source.Description);
                return (JsonNode)new JsonObject
                {
                    ["id"] = source.Id, ["description"] = description,
                    ["descriptionComplete"] = description.Length == source.Description.Length,
                    ["descriptionEvidence"] = "source-description:" + Uri.EscapeDataString(source.Id) + "@" + PlanningGraphCompiler.Fingerprint(source.Description) + ":0"
                };
            }).ToArray()),
            ["next"] = offset + SourceDirectoryPageSize < state.Discovery.Sources.Count ? "page:sources:" + (offset + SourceDirectoryPageSize).ToString(CultureInfo.InvariantCulture) : null
        };
    }

    private static IEnumerable<CapabilitySummary> DescriptorCandidates(PlanningSession state, IEnumerable<CapabilityPage> pages) =>
        pages.SelectMany(p => p.Capabilities)
            .Concat((state.Catalog?.Capabilities ?? []).Where(c => c.Kind == "registered").Select(c =>
                new CapabilitySummary(c.Id, "runtime", c.Method ?? c.StepType, c.Description, c.StepType,
                    c.EffectKind, c.Version, c.Composition, PlanningCapabilityArguments.Editable(c), c.ArtifactContract)))
            .Where(c => c.Operation is not null && state.Catalog!.AllowedStepTypes.Contains(c.StepType) &&
                !state.Catalog.Policy.DeniedCapabilityIds.Contains(c.Id))
            .GroupBy(c => c.Operation!.Id, StringComparer.Ordinal)
            .Where(g => g.Select(c => (c.Id, c.Version)).Distinct().Count() == 1).Select(g => g.First());

    internal static JsonArray ContractDescriptors(PlanningSession state, int offset = 0, int count = ContractDescriptorPageSize, bool concise = false, bool retained = false)
    {
        var step = state.Construction!.Compact!.CurrentStep!;
        var query = CapabilityRelevance.Query(step.Objective);
        var pages = !retained && PlanningConstructionPhasePolicies.IsFresh(state.Request) && UsesVerifiedReview(state)
            ? state.Discovery.Pages.Where(page => page.Query == step.Objective && page.ProducedArtifactKind is null)
            : state.Discovery.Pages;
        var ranked = CapabilityRelevance.Rank(DescriptorCandidates(state, pages), query).ToArray();
        if (offset < 0 || offset > ranked.Length)
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "The operation directory offset is outside the retained metadata.");
        return new(ranked.Skip(offset).Take(count).Select(c =>
        {
            var operation = c.Operation!;
            var identity = ContractIdentity(operation.Id, operation.Version);
            var description = NavigationDescription(operation.Description);
            var descriptor = new JsonObject
            {
                ["id"] = operation.Id, ["version"] = operation.Version,
                ["name"] = c.Name, ["effect"] = c.EffectKind,
                ["description"] = description,
                ["descriptionComplete"] = description.Length == operation.Description.Length,
                ["descriptionEvidence"] = "description:" + identity + ":0"
            };
            if (concise) { descriptor.Remove("description"); descriptor.Remove("descriptionComplete"); }
            return (JsonNode)descriptor;
        }).ToArray());
    }

    internal static JsonObject ContractDescriptorPage(PlanningSession state, int offset = 0, bool legacy = false, bool retained = false)
    {
        if (!legacy && IsCompactPresentation(state))
            return new JsonObject { ["offset"] = offset,
                ["items"] = ContractDescriptors(state, offset, CompactContractDescriptorPageSize, concise: true, retained: retained),
                ["descriptionsOmitted"] = true };
        var items = ContractDescriptors(state, offset, retained: retained || legacy);
        var next = items.Count == ContractDescriptorPageSize && ContractDescriptors(state, offset + ContractDescriptorPageSize, retained: retained || legacy).Count > 0
            ? "page:operations:" + (offset + ContractDescriptorPageSize).ToString(CultureInfo.InvariantCulture) : null;
        return new JsonObject { ["offset"] = offset, ["items"] = items, ["next"] = next };
    }

    private static JsonObject? CompanionOperationNavigation(PlanningSession state)
    {
        if (!UsesConvergentConstruction(state) || State(state).CurrentStep is not { OperationIds.Count: > 0 } step)
            return null;
        var companions = state.Discovery.Pages.Where(page => page.Query == step.Objective && page.ProducedArtifactKind is null)
            .SelectMany(page => page.Capabilities).Where(capability => capability.Operation is not null &&
                state.Catalog!.AllowedStepTypes.Contains(capability.StepType) && !state.Catalog.Policy.DeniedCapabilityIds.Contains(capability.Id) &&
                !step.OperationIds.Contains(capability.Operation.Id, StringComparer.Ordinal))
            .Select(capability => (capability.Operation!.Id, capability.Operation.Version)).ToHashSet();
        if (companions.Count == 0) return null;
        var page = ContractDescriptorPage(state);
        var items = new JsonArray();
        var navigation = new JsonObject { ["items"] = items, ["directory"] = OperationPageAddress(state, 0),
            ["next"] = OperationDirectoryContinuation(state, page) };
        // A projected directory is navigation only. Preserve canonical pages and
        // whole row values; omitted rows remain behind their exact directory link.
        foreach (var item in page["items"]!.AsArray().Where(item => companions.Contains((item!["id"]!.ToString(), item["version"]!.ToString()))))
        {
            items.Add(item!.DeepClone());
            if (Encoding.UTF8.GetByteCount(navigation.ToJsonString()) > ContractDescriptionPageSize) items.RemoveAt(items.Count - 1);
        }
        return navigation;
    }

    internal static JsonObject? DescribedOperationChoices(PlanningSession state)
    {
        if (!IsCompactPresentation(state) || state.Construction!.Evidence is not { Count: > 0 } ||
            State(state).CurrentStep is not { } step) return null;
        var reads = state.Construction.EvidenceRequests.ToHashSet(StringComparer.Ordinal);
        var ranked = CapabilityRelevance.Rank(DescriptorCandidates(state, state.Discovery.Pages), CapabilityRelevance.Query(step.Objective)).Where(capability =>
            capability.Operation!.Version == capability.Version && reads.Contains(PlanningConstruction.EvidenceBatchFingerprint(state,
                ["description:" + ContractIdentity(capability.Operation.Id, capability.Operation.Version) + ":0"]))).ToArray();
        if (ranked.Length == 0) return null;
        // A successful description page is navigation evidence, not complete
        // contract inspection or authority to execute. Never resend its prose.
        return new JsonObject
        {
            ["total"] = ranked.Length, ["complete"] = ranked.Length <= 4,
            ["items"] = new JsonArray(ranked.Take(4).Select(capability => (JsonNode)new JsonObject
            {
                ["id"] = capability.Operation!.Id, ["name"] = capability.Name, ["effect"] = capability.EffectKind
            }).ToArray())
        };
    }

    internal static string OperationPageAddress(PlanningSession state, int offset) =>
        IsCompactPresentation(state)
            ? "page:operations:" + PlanningGraphCompiler.Fingerprint(ContractDescriptorPage(state, offset).ToJsonString()) + ":" + offset.ToString(CultureInfo.InvariantCulture)
            : "page:operations:" + offset.ToString(CultureInfo.InvariantCulture);

    internal static JsonArray ContractViews(PlanningSession state, bool review = false)
        => new(SelectedContracts(state).Select(c => (JsonNode)ContractView(state, c, review)).ToArray());

    private static IEnumerable<PlanningCapability> SelectedContracts(PlanningSession state)
    {
        foreach (var id in state.Construction!.Compact!.CurrentStep!.OperationIds.Distinct(StringComparer.Ordinal))
            yield return ExactContract(state, id);
    }

    private static PlanningCapability ExactContract(PlanningSession state, string operation, string? version = null)
    {
        var matches = (state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved)
            .Where(c => TaskOperations.Describe(c).Id == operation && (version is null || c.Version == version))
            .DistinctBy(c => (c.Id, c.Version)).ToArray();
        if (matches.Length != 1 || !state.Catalog!.AllowedStepTypes.Contains(matches[0].StepType) ||
            state.Catalog.Policy.DeniedCapabilityIds.Contains(matches[0].Id) || TaskOperations.Validate(matches[0]).Count != 0)
            PlanningConstruction.Fail("CONSTRUCTION_CONTRACT_UNAVAILABLE", "/construction/compact/currentStep/operationIds",
                "Select an unambiguous, resolved and policy-allowed operation contract at its exact retained version.");
        return matches[0];
    }

    private static HashSet<string> ImportedProducerIds(JsonArray interfaces) => interfaces.OfType<JsonObject>()
        .Select(item => (item["binding"] ?? item["value"])?.Deserialize(PlanningJsonContext.Default.TaskValue))
        .OfType<TaskValue>().SelectMany(TaskPlanCompiler.Values)
        .Where(value => value.Kind is "output" or "present" && value.Source is not null)
        .Select(value => value.Source!).ToHashSet(StringComparer.Ordinal);

    private static void AddAcceptedProducerPorts(PlanningSession state, JsonObject[] entries, HashSet<string> selected)
    {
        var accepted = new TaskPlanSymbols(state.Plan!);
        var bytes = 0;
        foreach (var entry in entries)
        {
            if (entry["task"]?.ToString() is not { } id || !selected.Contains(id) || !accepted.Tasks.TryGetValue(id, out var site) ||
                site.Task.Kind != "operation" || site.Task.Operation is null || entry["scope"]?.ToString() != site.Scope.Path) continue;
            var capability = ExactContract(state, site.Task.Operation);
            var operation = PlanningCapabilityArguments.Editable(capability);
            entry["operationVersion"] = operation.Version;
            entry["contractHash"] = ContractHash(capability);
            entry["outputDirectory"] = "port-page:" + ContractIdentity(operation.Id, operation.Version) + ":/output:0";
            var produces = TaskOperations.ArtifactPorts(capability)["produces"] as JsonArray ?? [];
            if (produces.Count == 0) continue;
            entry["producesCount"] = produces.Count;
            var length = Encoding.UTF8.GetByteCount(produces.ToJsonString());
            if (produces.Count <= ContractDirectoryPageSize && bytes + length <= ContractDescriptionPageSize)
            { entry["produces"] = produces.DeepClone(); bytes += length; }
            else entry["producesOmitted"] = true;
        }
    }

    private static bool AcceptedProducerOutput(PlanningSession state, PlanningCapability capability)
    {
        if (!UsesConvergentConstruction(state) || state.Plan is null) return false;
        var compact = State(state);
        var accepted = new TaskPlanSymbols(state.Plan);
        HashSet<string> selected;
        if (compact.CurrentStep is null)
        {
            selected = accepted.Tasks.Values.Skip(compact.InterfaceOffset).Take(4)
                .Where(site => !accepted.Scopes.Any(scope => scope.Owner?.Id == site.Task.Id &&
                    state.Construction!.OpenScopes.Contains(scope.Path, StringComparer.Ordinal)))
                .Select(site => site.Task.Id).ToHashSet(StringComparer.Ordinal);
            // Earlier host-generated interface pages remain in the issued evidence
            // navigation. Revalidate their producer identity rather than making
            // their exact output links disappear when the page cursor advances.
            foreach (var (address, page) in state.Construction!.Evidence ?? [])
                if (address.Split(':') is ["page", "interfaces", var offset] &&
                    int.TryParse(offset, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && page is JsonArray rows)
                    foreach (var row in rows.OfType<JsonObject>())
                        if (row["task"]?.ToString() is { } id && accepted.Tasks.TryGetValue(id, out var site) &&
                            row["scope"]?.ToString() == site.Scope.Path && row["operation"]?.ToString() == site.Task.Operation &&
                            row["operationVersion"]?.ToString() == capability.Version && row["contractHash"]?.ToString() == ContractHash(capability))
                            selected.Add(id);
        }
        else
        {
            selected = ImportedProducerIds(ImportedInterfaces(state, compact.CurrentStep));
            if (state.Construction!.Candidate is { Diagnostics.Count: 0 }) selected.UnionWith(ImportedProducerIds(ActualImportedInterfaces(state)));
        }
        return selected.Any(id => accepted.Tasks.TryGetValue(id, out var site) && site.Task.Kind == "operation" &&
            site.Task.Operation == TaskOperations.Describe(capability).Id &&
            ContractHash(ExactContract(state, site.Task.Operation)) == ContractHash(capability));
    }

    private static bool OutputEvidence(string kind, string tail) => kind switch
    {
        "schema" => tail == "/output" || tail.StartsWith("/output/", StringComparison.Ordinal),
        "schema-page" or "port-page" => tail.StartsWith("/output:", StringComparison.Ordinal) || tail.StartsWith("/output/", StringComparison.Ordinal),
        _ => false
    };

    private static JsonObject ContractView(PlanningSession state, PlanningCapability capability, bool review)
    {
        var operation = PlanningCapabilityArguments.Editable(capability);
        var identity = ContractIdentity(operation.Id, operation.Version);
        if (review) return ReviewContractView(state, capability, operation, identity);
        var selected = SelectedSchemaPointers(state, capability);
        var omitted = new List<string>();
        var input = ProjectCapabilityInput(state, capability, selected, omitted);
        var inputs = SelectedInputPorts(state, capability, input).ToArray();
        var node = new JsonObject
        {
            ["id"] = operation.Id, ["version"] = operation.Version, ["hash"] = ContractHash(capability),
            // Prose on an actually selected operation can contain normative restrictions.
            // It is retained completely; admission rejects an indivisible oversized view.
            ["description"] = operation.Description, ["effect"] = capability.EffectKind,
            ["inputSchema"] = input,
            ["editableInputs"] = new JsonArray(inputs.Select(p => (JsonNode)new JsonObject
                { ["name"] = p.Name, ["pointer"] = PortPointer("/input", p.Path), ["required"] = p.Required }).ToArray()),
            ["omittedOptionalInputCount"] = omitted.Count,
            ["omittedOptionalInputs"] = new JsonArray(omitted.Take(ContractDirectoryPageSize).Select(p => (JsonNode?)JsonValue.Create("schema:" + identity + ":" + p)).ToArray()),
            ["inputDirectory"] = "schema-page:" + identity + ":/input:0",
            ["outputs"] = PortDirectory(operation.Outputs, identity, "/output"),
            ["outputCount"] = operation.Outputs.Count,
            ["outputDirectory"] = "port-page:" + identity + ":/output:" + (operation.Outputs.Count > ContractDirectoryPageSize ? ContractDirectoryPageSize : 0).ToString(CultureInfo.InvariantCulture),
            ["outputSchemaDirectory"] = "schema-page:" + identity + ":/output:0",
            ["artifacts"] = TaskOperations.ArtifactPorts(capability),
            ["view"] = "Required and explicitly selected input branches with complete coupled constraints and referenced definitions. Other optional branches and output schemas are addressable originals; this view is not an executable contract."
        };
        var outputViews = selected.Where(p => p.StartsWith("/output", StringComparison.Ordinal)).ToArray();
        if (outputViews.Length > 0)
            node["selectedOutputs"] = new JsonArray(outputViews.Select(p => (JsonNode)ExactSchemaView(capability, p)).ToArray());
        if (IsCompactPresentation(state))
        {
            // Output navigation is retrieved when needed; selected schemas stay
            // in the view. The directory retains every producer port alias.
            node.Remove("outputs");
            node["outputsOmitted"] = operation.Outputs.Count > 0;
            node["outputDirectory"] = "port-page:" + identity + ":/output:0";
            // Requested originals replace optional input navigation, never the
            // projected constraints or selected output schemas.
            if (SuppliedContractEvidence(state, identity)) node.Remove("omittedOptionalInputs");
        }
        return node;
    }

    private static bool SuppliedContractEvidence(PlanningSession state, string identity) =>
        state.Construction!.Evidence?.Any(item => item.Key == "contract:" + identity ||
            new[] { "schema:", "schema-page:", "port-page:", "description:" }
                .Any(prefix => item.Key.StartsWith(prefix + identity + ":", StringComparison.Ordinal))) == true;

    private static JsonObject ReviewContractView(PlanningSession state, PlanningCapability capability, PlanningOperation operation, string identity)
    {
        var operationDescription = BoundedDescription(operation.Description, 384);
        var bindings = state.Construction!.Candidate is { } candidate
            ? TaskPlanRevisions.Tasks(new TaskScope { Tasks = candidate.Extension.Tasks, Always = candidate.Extension.Always })
                .Where(t => t.Operation == operation.Id).SelectMany(t => t.Inputs).Select(i => i.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        var boundPorts = operation.Inputs.Where(p => bindings.Contains(p.Name)).ToArray();
        // These explicitly incomplete descriptions are navigation aids. Keep their
        // aggregate size bounded even when the fragment binds many exact ports.
        var portDescriptionLimit = Math.Min(192, 768 / Math.Max(1, boundPorts.Length));
        var view = new JsonObject
        {
            ["id"] = operation.Id, ["version"] = operation.Version, ["hash"] = ContractHash(capability), ["effect"] = capability.EffectKind,
            ["description"] = operationDescription,
            ["descriptionComplete"] = operationDescription.Length == operation.Description.Length,
            ["descriptionEvidence"] = "description:" + identity + ":0",
            ["boundInputs"] = new JsonArray(boundPorts.Select(p =>
            {
                var description = p.Schema["description"]?.ToString() ?? "";
                var presented = BoundedDescription(description, portDescriptionLimit);
                return (JsonNode)new JsonObject { ["name"] = p.Name, ["required"] = p.Required, ["type"] = p.Schema["type"]?.DeepClone(),
                    ["description"] = presented, ["descriptionComplete"] = presented.Length == description.Length,
                    ["schemaEvidence"] = "schema:" + identity + ":" + PortPointer("/input", p.Path) };
            }).ToArray()),
            ["artifacts"] = TaskOperations.ArtifactPorts(capability),
            ["deterministicValidation"] = "The candidate passed complete original-contract validation. Descriptions here are explicitly bounded discovery evidence, not complete normative contracts. Request original restrictions when needed; report unsupported business claims as unverified."
        };
        if (UsesVerifiedReview(state))
        {
            if (capability.Server is { } server) view["server"] = server;
            if (capability.Method is { } method) view["method"] = method;
        }
        if (IsCompactPresentation(state) && state.Construction.Evidence?.Any(item =>
            item.Key.StartsWith("description:" + identity + ":", StringComparison.Ordinal) &&
            item.Value is JsonObject original && original["id"]?.ToString() == operation.Id &&
            original["version"]?.ToString() == operation.Version &&
            original["descriptionHash"]?.ToString() == PlanningGraphCompiler.Fingerprint(operation.Description)) == true)
        {
            // The bounded teaser and validation reminder are navigation aids.
            // Keep the requested original, identity, effects and bound ports.
            view.Remove("description");
            view.Remove("descriptionComplete");
            view.Remove("deterministicValidation");
        }
        return view;
    }

    private static IEnumerable<OperationPort> SelectedInputPorts(PlanningSession state, PlanningCapability capability, JsonObject? projected = null)
    {
        projected ??= ProjectCapabilityInput(state, capability, SelectedSchemaPointers(state, capability), []);
        foreach (var port in PlanningCapabilityArguments.Editable(capability, projected).Inputs)
            if (PlanningSchemaReferences.Read(projected, PortPointer("", port.Path)) is JsonObject)
                yield return port;
    }

    private static HashSet<string> SelectedSchemaPointers(PlanningSession state, PlanningCapability capability)
    {
        var operation = TaskOperations.Describe(capability);
        var prefix = "schema:" + ContractIdentity(operation.Id, operation.Version) + ":";
        return state.Construction!.Compact!.CurrentStep!.SchemaPointers
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p[prefix.Length..]).ToHashSet(StringComparer.Ordinal);
    }

    private static JsonObject ProjectCapabilityInput(PlanningSession state, PlanningCapability capability, IReadOnlySet<string> selected, List<string> omitted)
    {
        var operation = PlanningCapabilityArguments.Editable(capability);
        // Keep required producer-declared business ports and tentative bindings,
        // including mappings whose ancestors are optional in the wire schema.
        // This does not ask to repeat every unused descendant of an object port.
        var bound = state.Construction!.Candidate is { } candidate
            ? TaskPlanRevisions.Tasks(new TaskScope { Tasks = candidate.Extension.Tasks, Always = candidate.Extension.Always })
                .Where(t => t.Operation == operation.Id).SelectMany(t => t.Inputs).Select(i => i.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        var mandatory = operation.Inputs.Where(p => p.Required || bound.Contains(p.Name)).Select(p => PortPointer("/input", p.Path)).ToHashSet(StringComparer.Ordinal);
        return ProjectInputSchema(capability.InputSchema, selected, omitted, mandatory);
    }

    // Object properties are the only independently removable branches. Any
    // coupling/conditional keyword keeps its whole containing subtree. A $ref
    // keeps its exact transitive target, never an inferred simplified equivalent.
    internal static JsonObject ProjectInputSchema(JsonObject original, IReadOnlySet<string> selected, List<string> omitted, IReadOnlySet<string>? mandatory = null)
    {
        var projected = ProjectSchemaBranch(original, "/input", selected, omitted, mandatory);
        RetainReferencedSchemas(original, projected);
        return projected;
    }

    private static JsonObject ProjectSchemaBranch(JsonObject source, string path, IReadOnlySet<string> selected,
        List<string> omitted, IReadOnlySet<string>? mandatory = null)
    {
        var result = source.DeepClone().AsObject();
        result.Remove("$defs"); result.Remove("definitions");
        if (selected.Contains(path) || HasCoupledConstraints(source))
        {
            // Copying a complete branch preserves every dependent constraint.
            return source.DeepClone().AsObject();
        }
        if (source["properties"] is JsonObject properties)
        {
            var required = (source["required"] as JsonArray ?? []).Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
            var retained = new JsonObject();
            foreach (var (name, value) in properties)
            {
                var childPath = path + "/properties/" + PlanningSchemaReferences.Escape(name);
                if (required.Contains(name) || selected.Any(p => p == childPath || p.StartsWith(childPath + "/", StringComparison.Ordinal)) ||
                    mandatory?.Any(p => p == childPath || p.StartsWith(childPath + "/", StringComparison.Ordinal)) == true)
                    retained[name] = value is JsonObject child ? ProjectSchemaBranch(child, childPath, selected, omitted, mandatory) : value?.DeepClone();
                else omitted.Add(childPath);
            }
            result["properties"] = retained;
        }
        if (source["items"] is JsonObject items) result["items"] = ProjectSchemaBranch(items, path + "/items", selected, omitted, mandatory);
        return result;
    }

    private static bool HasCoupledConstraints(JsonObject schema) => schema.Any(p => p.Key is
        "allOf" or "anyOf" or "oneOf" or "if" or "then" or "else" or "dependentRequired" or "dependentSchemas" or
        "dependencies" or "minProperties" or "maxProperties" or "patternProperties" or "propertyNames" or
        "unevaluatedProperties" or "not" or "contains" or "prefixItems" or "enum" or "const");

    private static void RetainReferencedSchemas(JsonObject original, JsonObject projected, JsonObject? selected = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (References(projected).Concat(selected is null ? [] : References(selected)).FirstOrDefault(r => !seen.Contains(r)) is { } reference)
        {
            seen.Add(reference);
            if (!reference.StartsWith("#/", StringComparison.Ordinal))
            {
                if (reference == "#")
                {
                    projected.Clear(); foreach (var (key, value) in original) projected[key] = value?.DeepClone();
                    return;
                }
                PlanningConstruction.Fail("CONSTRUCTION_SCHEMA_REFERENCE", "/construction/compact", "The original contract has an unsupported non-local schema reference.");
            }
            var segments = reference[2..].Split('/');
            var prefixLength = segments.Length > 1 && segments[0] is "$defs" or "definitions" or "properties" ? 2 : 1;
            var pointer = "/" + string.Join('/', segments.Take(prefixLength));
            var target = PlanningSchemaReferences.Read(original, pointer);
            if (target is null) PlanningConstruction.Fail("CONSTRUCTION_SCHEMA_REFERENCE", pointer, "The original schema reference is unresolved.");
            var destination = projected;
            for (var i = 0; i < prefixLength - 1; i++)
            {
                var name = DecodePointerPart(segments[i]);
                if (destination[name] is not JsonObject) destination[name] = new JsonObject();
                destination = destination[name]!.AsObject();
            }
            destination[DecodePointerPart(segments[prefixLength - 1])] = target!.DeepClone();
        }

        // Annotation/default payloads can themselves contain a property named
        // $ref. Only JSON Schema positions establish a schema dependency.
        static IEnumerable<string> References(JsonObject node) => PlanningSchemaReferences.Walk(node, "", 0)
            .Where(e => e.Schema["$ref"] is JsonValue).Select(e => e.Schema["$ref"]!.ToString());
    }

    internal static JsonNode? LoadContractEvidence(PlanningSession state, string address, bool recordSchemaSelection = true)
    {
        var separator = address.IndexOf(':');
        if (separator < 0) return null;
        var kind = address[..separator];
        if (kind == "source-description") return SourceDescription(state, address[(separator + 1)..]);
        if (kind is not ("contract" or "schema" or "schema-page" or "port-page" or "description")) return null;
        var rest = address[(separator + 1)..];
        var identityEnd = rest.IndexOf(':');
        var identity = identityEnd < 0 ? rest : rest[..identityEnd];
        var at = identity.IndexOf('@');
        if (at <= 0 || at == identity.Length - 1)
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Contract evidence needs its exact operation@version identity.");
        var operation = Uri.UnescapeDataString(identity[..at]);
        var version = Uri.UnescapeDataString(identity[(at + 1)..]);
        if (identity != ContractIdentity(operation, version))
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Use the issued canonical contract identity.");
        var tail = identityEnd < 0 ? "" : rest[(identityEnd + 1)..];
        if (kind == "description")
        {
            if (!int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
                PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Use the issued description address with its nonnegative integer offset.");
            return OperationDescription(state, operation, version, identity, offset);
        }
        var capability = ExactContract(state, operation, version);
        var producerOutput = recordSchemaSelection && state.Construction!.Compact!.CurrentStep?.OperationIds.Contains(operation, StringComparer.Ordinal) != true &&
            OutputEvidence(kind, tail) && AcceptedProducerOutput(state, capability);
        if (recordSchemaSelection && UsesConvergentConstruction(state) && state.Construction!.Compact!.CurrentStep is null && !producerOutput)
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Select a business step before inspecting its capability contracts.");
        if (kind == "contract" && tail.Length == 0) return ContractView(state, capability, review: false);
        if (kind == "schema")
        {
            var view = ExactSchemaView(capability, tail);
            if (recordSchemaSelection && !producerOutput)
            {
                var step = state.Construction!.Compact!.CurrentStep!;
                if (!step.OperationIds.Contains(operation, StringComparer.Ordinal))
                    PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Select the operation for this substep before requesting its schema branches.");
                if (!step.SchemaPointers.Contains(address, StringComparer.Ordinal)) step.SchemaPointers.Add(address);
            }
            return view;
        }
        if (kind is "schema-page" or "port-page")
        {
            var offsetSeparator = tail.LastIndexOf(':');
            if (offsetSeparator > 0 && int.TryParse(tail[(offsetSeparator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page >= 0)
                return kind == "schema-page" ? SchemaDirectory(capability, tail[..offsetSeparator], page) :
                    OperationPortDirectory(capability, tail[..offsetSeparator], page, UsesConvergentConstruction(state));
        }
        PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Use an issued schema, schema-page, port-page or description evidence address.");
        return null;
    }

    private static JsonObject OperationDescription(PlanningSession state, string operation, string version, string identity, int offset)
    {
        // Discovery needs the original decision evidence before exact contract
        // resolution. Reading retained prose does not admit an executable schema.
        var retained = state.Discovery.Pages.SelectMany(p => p.Capabilities)
            .Concat((state.Catalog?.Capabilities ?? []).Concat(state.Discovery.Resolved).Select(c =>
                new CapabilitySummary(c.Id, c.Server ?? "runtime", c.Method ?? c.StepType, c.Description,
                    c.StepType, c.EffectKind, c.Version, Operation: TaskOperations.Describe(c))))
            .Where(c => c.Operation?.Id == operation && c.Operation.Version == version && c.Version == version &&
                state.Catalog!.AllowedStepTypes.Contains(c.StepType) && !state.Catalog.Policy.DeniedCapabilityIds.Contains(c.Id))
            .Select(c => (c.Id, Description: c.Operation!.Description)).Distinct().ToArray();
        if (retained.Length != 1)
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "The retained operation description identity or version is missing, stale or ambiguous.");
        var description = retained[0].Description;
        var text = DescriptionPage(description, offset);
        return new JsonObject { ["id"] = operation, ["version"] = version,
            ["descriptionHash"] = PlanningGraphCompiler.Fingerprint(description),
            ["offset"] = offset, ["totalCharacters"] = description.Length, ["text"] = text,
            ["next"] = offset + text.Length < description.Length ? "description:" + identity + ":" + (offset + text.Length).ToString(CultureInfo.InvariantCulture) : null };
    }

    private static JsonObject SourceDescription(PlanningSession state, string address)
    {
        var offsetSeparator = address.LastIndexOf(':');
        var versionSeparator = address.IndexOf('@');
        if (offsetSeparator < 0 || versionSeparator <= 0 || versionSeparator >= offsetSeparator ||
            !int.TryParse(address[(offsetSeparator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Use the issued versioned capability-source description address.");
        // The earlier rejection always throws; parse again keeps definite assignment explicit.
        offset = int.Parse(address[(offsetSeparator + 1)..], CultureInfo.InvariantCulture);
        var sourceId = Uri.UnescapeDataString(address[..versionSeparator]);
        var version = address[(versionSeparator + 1)..offsetSeparator];
        var sources = state.Discovery.Sources.Where(s => s.Id == sourceId).ToArray();
        if (sources.Length != 1 || Uri.EscapeDataString(sourceId) != address[..versionSeparator] || PlanningGraphCompiler.Fingerprint(sources[0].Description) != version)
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "The source description identity or version is stale or ambiguous.");
        var description = sources[0].Description;
        var text = DescriptionPage(description, offset);
        return new JsonObject { ["id"] = sourceId, ["version"] = version, ["offset"] = offset, ["totalCharacters"] = description.Length,
            ["text"] = text, ["next"] = offset + text.Length < description.Length ? "source-description:" + address[..offsetSeparator] + ":" + (offset + text.Length).ToString(CultureInfo.InvariantCulture) : null };
    }

    private static string DescriptionPage(string description, int offset)
    {
        if (offset < 0 || offset > description.Length || offset > 0 && offset < description.Length && char.IsLowSurrogate(description[offset]) && char.IsHighSurrogate(description[offset - 1]))
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Description offsets must address retained Unicode boundaries.");
        // The helper includes twelve bytes of JSON string framing; preserve the
        // existing 2,048-character ASCII page while bounding escaped text bytes.
        var count = OriginalPageLength(description, offset, ContractDescriptionPageSize, maximumEscapedBytes: ContractDescriptionPageSize + 12);
        return description.Substring(offset, count);
    }

    private static string NavigationDescription(string description)
        => BoundedDescription(description, 192);

    private static string BoundedDescription(string description, int maximum)
        // Reserve enough space for one fully escaped Unicode scalar; an empty
        // navigation excerpt is valid when its exact original remains addressable.
        => maximum < 14 ? "" : description[..OriginalPageLength(description, 0, maximumCharacters: maximum, maximumEscapedBytes: maximum + 12)];

    private static JsonObject ExactSchemaView(PlanningCapability capability, string pointer)
    {
        var entries = PlanningSchemaReferences.Entries(capability).ToArray();
        var entry = entries.SingleOrDefault(e => e.Path == pointer);
        if (entry.Schema is null) PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Select an existing schema-bearing pointer from this exact contract.");
        var root = pointer.StartsWith("/input", StringComparison.Ordinal) ? capability.InputSchema : capability.OutputSchema;
        var ancestors = entries.Where(e => e.Path != pointer && pointer.StartsWith(e.Path + "/", StringComparison.Ordinal)).ToArray();
        var closure = new JsonObject();
        // Gather references in the original coordinate system without rebasing them.
        RetainReferencedSchemas(root, closure, entry.Schema);
        var operation = TaskOperations.Describe(capability);
        var ancestorViews = new JsonArray(ancestors.Select(a =>
        {
            var constraints = a.Schema.DeepClone().AsObject();
            if (!HasCoupledConstraints(a.Schema))
                foreach (var key in new[] { "properties", "items", "$defs", "definitions" }) constraints.Remove(key);
            RetainReferencedSchemas(root, closure, constraints);
            var view = new JsonObject { ["pointer"] = a.Path, ["constraints"] = constraints };
            // An input field can only be authored in the context of its required
            // siblings. Output field reads need the ancestor requiredness, not
            // unrelated sibling output trees already checked at runtime.
            if (pointer.StartsWith("/input/", StringComparison.Ordinal) && a.Schema["properties"] is JsonObject properties && a.Schema["required"] is JsonArray required)
            {
                var siblings = new JsonObject();
                var omitted = new List<string>();
                foreach (var name in required.Select(n => n!.ToString()))
                {
                    var siblingPath = a.Path + "/properties/" + PlanningSchemaReferences.Escape(name);
                    if (pointer == siblingPath || pointer.StartsWith(siblingPath + "/", StringComparison.Ordinal) || properties[name] is not JsonObject sibling) continue;
                    var projected = ProjectSchemaBranch(sibling, siblingPath, new HashSet<string>(StringComparer.Ordinal), omitted);
                    siblings[name] = projected; RetainReferencedSchemas(root, closure, projected);
                }
                if (siblings.Count > 0) view["requiredSiblings"] = siblings;
                if (omitted.Count > 0)
                {
                    var identity = ContractIdentity(operation.Id, operation.Version);
                    view["omittedOptionalSiblingCount"] = omitted.Count;
                    view["omittedOptionalSiblings"] = new JsonArray(omitted.Take(ContractDirectoryPageSize)
                        .Select(path => (JsonNode?)JsonValue.Create("schema:" + identity + ":" + path)).ToArray());
                    view["directory"] = "schema-page:" + identity + ":" + a.Path + ":0";
                }
            }
            return (JsonNode)view;
        }).ToArray());
        return new JsonObject
        {
            ["id"] = operation.Id, ["version"] = operation.Version, ["hash"] = ContractHash(capability), ["pointer"] = pointer,
            ["schema"] = entry.Schema!.DeepClone(), ["ancestors"] = ancestorViews, ["referenceClosure"] = closure,
            ["view"] = "Exact selected original and ancestor constraints with required sibling branches; independent optional sibling descendants are explicitly omitted and addressable. References remain relative to the retained original root. Not a replacement contract."
        };

    }

    private static JsonObject SchemaDirectory(PlanningCapability capability, string pointer, int offset)
    {
        var selected = PlanningSchemaReferences.Entries(capability).SingleOrDefault(e => e.Path == pointer);
        if (selected.Schema is null) PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "The requested schema directory does not exist.");
        var operation = TaskOperations.Describe(capability);
        var identity = ContractIdentity(operation.Id, operation.Version);
        var children = new List<(string Path, JsonObject Schema)>();
        foreach (var entry in PlanningSchemaReferences.Walk(selected.Schema!, pointer, 0).Skip(1))
            if (children.Count == 0 || !entry.Path.StartsWith(children[^1].Path + "/", StringComparison.Ordinal)) children.Add(entry);
        if (offset > children.Count) PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "The schema directory offset is outside the retained original.");
        return new JsonObject { ["id"] = operation.Id, ["version"] = operation.Version, ["hash"] = ContractHash(capability),
            ["pointer"] = pointer, ["offset"] = offset, ["total"] = children.Count,
            ["items"] = new JsonArray(children.Skip(offset).Take(ContractDirectoryPageSize).Select(e => (JsonNode)new JsonObject
                { ["pointer"] = e.Path, ["type"] = e.Schema["type"]?.DeepClone(), ["evidence"] = "schema:" + identity + ":" + e.Path,
                    ["directory"] = ChildSchemaDirectory(e.Schema, identity, e.Path) }).ToArray()),
            ["next"] = offset + ContractDirectoryPageSize < children.Count ? "schema-page:" + identity + ":" + pointer + ":" + (offset + ContractDirectoryPageSize).ToString(CultureInfo.InvariantCulture) : null };
    }

    private static JsonArray PortDirectory(IEnumerable<OperationPort> ports, string identity, string root) => new(ports.Take(ContractDirectoryPageSize)
        .Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["type"] = p.Schema["type"]?.DeepClone(), ["required"] = p.Required,
            ["schema"] = "schema:" + identity + ":" + PortPointer(root, p.Path),
            ["directory"] = ChildSchemaDirectory(p.Schema, identity, PortPointer(root, p.Path)) }).ToArray());

    private static string? ChildSchemaDirectory(JsonObject schema, string identity, string pointer)
        => PlanningSchemaReferences.Walk(schema, pointer, 0).Skip(1).Any() ? "schema-page:" + identity + ":" + pointer + ":0" : null;

    private static JsonObject OperationPortDirectory(PlanningCapability capability, string root, int offset, bool producerOutput = false)
    {
        var operation = PlanningCapabilityArguments.Editable(capability);
        if (root is not ("/input" or "/output"))
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "Operation port directories require /input or /output.");
        var ports = root == "/input" ? operation.Inputs : operation.Outputs;
        if (offset < 0 || offset > ports.Count)
            PlanningConstruction.Fail("CONSTRUCTION_EVIDENCE_INVALID", "/evidenceRequests", "The operation port offset is outside the retained original.");
        var identity = ContractIdentity(operation.Id, operation.Version);
        var result = new JsonObject { ["id"] = operation.Id, ["version"] = operation.Version, ["hash"] = ContractHash(capability),
            ["direction"] = root, ["offset"] = offset, ["total"] = ports.Count,
            ["items"] = PortDirectory(ports.Skip(offset), identity, root),
            ["next"] = offset + ContractDirectoryPageSize < ports.Count
                ? "port-page:" + identity + ":" + root + ":" + (offset + ContractDirectoryPageSize).ToString(CultureInfo.InvariantCulture) : null };
        if (producerOutput && root == "/output" && TaskOperations.ArtifactPorts(capability)["produces"] is JsonArray produces)
        {
            var names = ports.Skip(offset).Take(ContractDirectoryPageSize).Select(port => port.Name).ToHashSet(StringComparer.Ordinal);
            result["produces"] = new JsonArray(produces.Where(item => names.Contains(item!["port"]!.ToString())).Select(item => item!.DeepClone()).ToArray());
        }
        return result;
    }

    private static string PortPointer(string root, IReadOnlyList<string> path) => root + string.Concat(path.Select(p => "/properties/" + PlanningSchemaReferences.Escape(p)));
    private static string ContractIdentity(string operation, string version) => Uri.EscapeDataString(operation) + "@" + Uri.EscapeDataString(version);
    private static string ContractHash(PlanningCapability capability) => PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(capability, PlanningJsonContext.Default.PlanningCapability));
    private static string DecodePointerPart(string value) => value.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
