using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private const int MaxPipelinePatchOperations = 12;

    private sealed record PipelineRepairApplication(
        WorkflowPipelineExtraction Extraction,
        string AnnotatedMarkdown,
        int OperationCount,
        string BaseFingerprint,
        IReadOnlySet<string> AddressedDiagnosticCodes);

    private static async Task<PipelineRepairApplication> RequestPipelineExtractionPatchAsync(
        ILLMClient llmClient,
        string normalizedMarkdown,
        PipelineMcpContext pipelineMcpContext,
        WorkflowPipelineExtraction bestCandidate,
        IReadOnlyList<string> diagnostics,
        string? provider,
        string model,
        string? reasoning,
        StepExecutionContext ctx,
        CancellationToken ct,
        int attempt,
        int maxAttempts)
    {
        var fingerprint = BuildPipelineExtractionFingerprint(bestCandidate);
        var prompt = BuildPipelineExtractionPatchPrompt(
            normalizedMarkdown,
            pipelineMcpContext,
            bestCandidate,
            diagnostics,
            fingerprint);
        var response = await ExecutePipelineLlmStructuredPhaseAsync(
            llmClient,
            "patch_pipeline_extraction",
            prompt,
            provider,
            model,
            ResolvePipelinePatchReasoning(reasoning, attempt),
            ctx,
            ct,
            attempt,
            maxAttempts,
            BuildPipelineExtractionPatchSchema(bestCandidate));

        return ApplyPipelineExtractionPatch(bestCandidate, response, fingerprint);
    }

    private static string BuildPipelineExtractionPatchPrompt(
        string normalizedMarkdown,
        PipelineMcpContext pipelineMcpContext,
        WorkflowPipelineExtraction bestCandidate,
        IReadOnlyList<string> diagnostics,
        string fingerprint)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are repairing one validated workflow pipeline extraction through bounded leaf-level operations.");
        sb.AppendLine("Return only the structured patch object. Do not regenerate the complete extraction.");
        sb.AppendLine();
        sb.AppendLine("Patch invariants:");
        sb.AppendLine("- base_fingerprint must exactly equal the supplied fingerprint.");
        sb.AppendLine("- addressed_diagnostic_codes must contain only exact codes from addressable_diagnostic_codes and must identify every blocker this patch claims to resolve. A code claim never overrides deterministic validation or a reviewer that still reports the defect.");
        sb.AppendLine("- Supported operations are add_leaf, replace_leaf, remove_leaf, merge_leaves, and replace_main_orchestration.");
        sb.AppendLine("- replace_leaf preserves all capability and native-step ownership of its target and must keep the same name.");
        sb.AppendLine("- replace_leaf may remove an obsolete public input only when a blocking diagnostic proves the leaf itself produces that value. Preserve every genuine caller/upstream input and every established output contract.");
        sb.AppendLine("- merge_leaves atomically replaces two or more named sources and inherits their complete immutable ownership multiset.");
        sb.AppendLine("- remove_leaf is valid only for a leaf with no immutable capability, native-step, or local-operation ownership.");
        sb.AppendLine("- add_leaf creates local algorithmic work only and cannot invent an external capability.");
        sb.AppendLine("- Never emit server names, tool names, catalog IDs, operation IDs, selectors, activation data, or planned tools in a leaf patch; the runtime reapplies them.");
        sb.AppendLine("- Immutable external ownership consists only of required planned calls carrying an operation ID or catalog ID. Unlocked extractor-proposed calls are advisory: an unchanged leaf retains them, while replace/merge keeps only immutable calls so an incomplete or duplicate advisory call cannot become a permanent lock.");
        sb.AppendLine("- A leaf reported as external work without planned_tools has zero immutable external ownership. Replacing it with another external_action cannot fix that defect because replacement retains the same zero ownership.");
        sb.AppendLine("- Resolve an unowned external leaf only by merging it into a compatible existing owner leaf, removing it when redundant or outside the requested intent, or replacing it with genuinely local algorithmic work. Update main orchestration in the same patch when removal or merge changes calls.");
        sb.AppendLine("- A deterministic fallback policy may reuse the capability of the external operation it governs, but a separately requested runtime fallback action performed by an AI, agent, service, or tool requires external ownership. If the action has no immutable owner, move it into a compatible existing owner leaf and update that leaf and main orchestration together; never leave it as prose in an unowned leaf or invent a new capability.");
        sb.AppendLine("- A cohesive locked external action may perform non-observable prerequisite inspection, selection, or preparation needed to execute that action when its declared schema and metadata support the required context. Do not invent a second capability solely for such an internal prerequisite. Keep it inside the existing owner and expose a typed result only when another workflow boundary consumes it or the request requires it independently.");
        sb.AppendLine("- Conditional decision_operation_id ownership is immutable. The leaf or main contract listed as that local operation's owner must derive the runtime decision. A different orchestration surface may only route the typed decision output; it must not claim to recompute or own it.");
        sb.AppendLine("- Main native-step ownership is exact. Preserve every entry in candidate_extraction.main_native_steps and remove any human confirmation or other native interaction described by main when no matching entry is locked there.");
        sb.AppendLine("- Address every compatible blocking diagnostic in this one patch; do not make cosmetic edits that leave a reported ownership or schema defect unchanged.");
        sb.AppendLine("- For every reported weak object or object-array schema, add nested properties only when the normalized request, locked capability contract, or an existing typed boundary proves those fields. Otherwise simplify an unused opaque object boundary to an evidence-supported scalar or array of scalars, or remove that unused output and update orchestration; never invent domain fields merely to satisfy validation.");
        sb.AppendLine("- Make the smallest cohesive correction that addresses the blocking diagnostics and preserve unrelated contracts.");
        sb.AppendLine("- Every leaf payload is complete and strongly typed. Use an empty string only for target/main_orchestration fields unused by that operation, an empty sources array when unused, and null leaf when unused.");
        sb.AppendLine();
        AppendPromptSection(sb, "base_fingerprint", fingerprint);
        AppendPromptSection(
            sb,
            "addressable_diagnostic_codes",
            BuildStringArrayJson(GetAddressablePipelineDiagnosticCodes(bestCandidate)).ToJsonString(PromptJsonOptions));
        AppendPromptSection(sb, "blocking_diagnostics", string.Join("\n", diagnostics.Select(static value => "- " + value)));
        AppendPromptSection(sb, "normalized_prompt", normalizedMarkdown);
        AppendPromptSection(
            sb,
            "candidate_extraction",
            BuildExtractionJson(bestCandidate with { QualityReview = null }).ToJsonString(PromptJsonOptions));
        AppendPromptSection(sb, "immutable_leaf_ownership", BuildPipelinePatchOwnershipSummary(bestCandidate).ToJsonString(PromptJsonOptions));
        AppendPromptSection(sb, "conditional_decision_ownership", BuildPipelineConditionalDecisionOwnershipSummary(bestCandidate).ToJsonString(PromptJsonOptions));
        AppendPromptSection(sb, "capability_contract", BuildPipelineMcpContextJson(pipelineMcpContext).ToJsonString(PromptJsonOptions));
        return sb.ToString();
    }

    private static string? ResolvePipelinePatchReasoning(string? configuredReasoning, int attempt)
    {
        var normalized = configuredReasoning?.Trim().ToLowerInvariant();
        if (normalized is "high" or "xhigh" or "max")
            return configuredReasoning;

        return attempt >= 3 ? "high" : configuredReasoning;
    }

    private static JsonArray BuildPipelinePatchOwnershipSummary(WorkflowPipelineExtraction extraction)
        => new(extraction.Subworkflows.Select(static leaf => (JsonNode)new JsonObject
        {
            ["leaf"] = leaf.Name,
            ["external_capability_count"] = leaf.PlannedTools.Count(IsImmutablePipelinePlannedTool),
            ["advisory_external_call_count"] = leaf.PlannedTools.Count(static tool => !IsImmutablePipelinePlannedTool(tool)),
            ["immutable_external_calls"] = BuildPlannedToolsJson(GetImmutablePipelinePlannedTools(leaf.PlannedTools)),
            ["native_capability_count"] = leaf.PlannedNativeSteps?.Count ?? 0,
            ["local_operation_count"] = leaf.LocalOperationIds?.Count ?? 0,
            ["local_operation_ids"] = BuildStringArrayJson(leaf.LocalOperationIds ?? Array.Empty<string>()),
            ["owned_operation_ids"] = BuildStringArrayJson(leaf.OwnedOperationIds ?? Array.Empty<string>()),
            ["removable"] = !leaf.PlannedTools.Any(IsImmutablePipelinePlannedTool)
                            && (leaf.PlannedNativeSteps?.Count ?? 0) == 0
                            && (leaf.LocalOperationIds?.Count ?? 0) == 0
        }).ToArray());

    private static JsonArray BuildPipelineConditionalDecisionOwnershipSummary(WorkflowPipelineExtraction extraction)
    {
        var localOwners = extraction.Subworkflows
            .SelectMany(static leaf => (leaf.LocalOperationIds ?? Array.Empty<string>())
                .Select(operationId => (OperationId: operationId, Owner: leaf.Name, OwnerKind: "leaf")))
            .Concat((extraction.MainLocalOperationIds ?? Array.Empty<string>())
                .Select(static operationId => (OperationId: operationId, Owner: "main", OwnerKind: "main")))
            .GroupBy(static item => item.OperationId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static item => (item.Owner, item.OwnerKind)).Distinct().ToArray(),
                StringComparer.Ordinal);
        var rows = new JsonArray();
        foreach (var leaf in extraction.Subworkflows)
        {
            foreach (var tool in leaf.PlannedTools.Where(static tool => tool.Activation != null))
            {
                var activation = tool.Activation!;
                localOwners.TryGetValue(activation.DecisionOperationId, out var owners);
                rows.Add((JsonNode)new JsonObject
                {
                    ["activation_owner_leaf"] = leaf.Name,
                    ["group"] = activation.Group,
                    ["branch_value"] = activation.BranchValue,
                    ["decision_operation_id"] = activation.DecisionOperationId,
                    ["decision_owners"] = new JsonArray((owners ?? [])
                        .Select(static owner => (JsonNode)new JsonObject
                        {
                            ["owner"] = owner.Owner,
                            ["owner_kind"] = owner.OwnerKind
                        })
                        .ToArray())
                });
            }
        }
        return rows;
    }

    private static bool IsImmutablePipelinePlannedTool(PipelinePlannedTool tool)
        => tool.Required
           && (tool.OperationIds.Count > 0 || tool.CatalogIds.Count > 0);

    private static IReadOnlyList<PipelinePlannedTool> GetImmutablePipelinePlannedTools(
        IEnumerable<PipelinePlannedTool> tools)
        => tools.Where(IsImmutablePipelinePlannedTool)
            .DistinctBy(static tool => BuildPlannedToolJson(tool).ToJsonString(), StringComparer.Ordinal)
            .ToArray();

    private static JsonNode BuildPipelineExtractionPatchSchema(WorkflowPipelineExtraction bestCandidate)
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["base_fingerprint", "addressed_diagnostic_codes", "operations"],
          "$defs": {
            "contract_field": {
              "type": "object",
              "additionalProperties": false,
              "required": ["name", "type", "description", "required", "nullable", "item_type", "properties"],
              "properties": {
                "name": { "type": "string" },
                "type": { "type": "string", "enum": ["string", "number", "boolean", "array", "object", "dictionary", "any"] },
                "description": { "type": "string" },
                "required": { "type": "boolean" },
                "nullable": { "type": "boolean" },
                "item_type": { "type": "string" },
                "properties": { "type": "array", "items": { "$ref": "#/$defs/contract_field" } }
              }
            },
            "leaf": {
              "type": "object",
              "additionalProperties": false,
              "required": ["name", "goal", "description", "work_kind", "contract_role", "concrete_outcome", "inputs", "outputs", "extract_reason", "content"],
              "properties": {
                "name": { "type": "string" },
                "goal": { "type": "string" },
                "description": { "type": "string" },
                "work_kind": { "type": "string", "enum": ["orchestration", "deterministic_shaping", "external_work"] },
                "contract_role": { "type": "string", "enum": ["external_action", "typed_data_producer", "algorithmic_transform", "deterministic_glue", "orchestration", "abstract_policy"] },
                "concrete_outcome": { "type": "string" },
                "inputs": { "type": "array", "items": { "$ref": "#/$defs/contract_field" } },
                "outputs": { "type": "array", "items": { "$ref": "#/$defs/contract_field" } },
                "extract_reason": { "type": "string" },
                "content": { "type": "string" }
              }
            }
          },
          "properties": {
            "base_fingerprint": { "type": "string" },
            "addressed_diagnostic_codes": {
              "type": "array",
              "minItems": 1,
              "items": { "type": "string" }
            },
            "operations": {
              "type": "array",
              "minItems": 1,
              "maxItems": 12,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["op", "target", "sources", "main_orchestration", "leaf"],
                "properties": {
                  "op": { "type": "string", "enum": ["add_leaf", "replace_leaf", "remove_leaf", "merge_leaves", "replace_main_orchestration"] },
                  "target": { "type": "string" },
                  "sources": { "type": "array", "items": { "type": "string" } },
                  "main_orchestration": { "type": "string" },
                  "leaf": { "anyOf": [{ "$ref": "#/$defs/leaf" }, { "type": "null" }] }
                }
              }
            }
          }
        }
        """)!;

        var schemaProperties = schema["properties"]!;
        schemaProperties["base_fingerprint"]!["enum"] = new JsonArray(
            JsonValue.Create(BuildPipelineExtractionFingerprint(bestCandidate)));
        schemaProperties["addressed_diagnostic_codes"]!["items"]!["enum"] = new JsonArray(
            GetAddressablePipelineDiagnosticCodes(bestCandidate)
                .Select(static value => (JsonNode?)JsonValue.Create(value))
                .ToArray());

        var operationProperties = schemaProperties["operations"]!["items"]!["properties"]!;
        var existingLeafNames = bestCandidate.Subworkflows
            .Select(static leaf => leaf.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        operationProperties["target"]!["enum"] = new JsonArray(
            new[] { string.Empty }
                .Concat(existingLeafNames)
                .Select(static value => (JsonNode?)JsonValue.Create(value))
                .ToArray());
        operationProperties["sources"]!["items"]!["enum"] = new JsonArray(
            existingLeafNames
                .Select(static value => (JsonNode?)JsonValue.Create(value))
                .ToArray());
        return schema;
    }

    private static PipelineRepairApplication ApplyPipelineExtractionPatch(
        WorkflowPipelineExtraction bestCandidate,
        LLMResponse response,
        string expectedFingerprint)
    {
        var root = response.Json?.DeepClone();
        if (root == null && LooksLikeJsonObject(StripMarkdownFences(response.Text).Trim()))
            root = JsonNode.Parse(StripMarkdownFences(response.Text).Trim());
        if (root is not JsonObject patch)
            throw BuildPipelinePatchFailure("Patch response must be a structured JSON object.");

        var actualFingerprint = GetStringProperty(patch, "base_fingerprint");
        if (!string.Equals(actualFingerprint, expectedFingerprint, StringComparison.Ordinal))
            throw BuildPipelinePatchFailure("Patch base_fingerprint does not match the current best candidate.");
        var addressableCodes = GetAddressablePipelineDiagnosticCodes(bestCandidate);
        var addressedCodes = ReadPatchSources(patch["addressed_diagnostic_codes"] as JsonArray);
        if (addressedCodes.Count != addressedCodes.Distinct(StringComparer.Ordinal).Count())
            throw BuildPipelinePatchFailure("Patch addressed_diagnostic_codes contains duplicates.");
        var unknownAddressedCodes = addressedCodes.Where(code => !addressableCodes.Contains(code, StringComparer.Ordinal)).ToArray();
        if (unknownAddressedCodes.Length > 0)
        {
            throw BuildPipelinePatchFailure(
                "Patch addressed_diagnostic_codes contains unknown or stale codes: "
                + string.Join(", ", unknownAddressedCodes) + ".");
        }
        if (addressableCodes.Count > 0 && addressedCodes.Count == 0)
            throw BuildPipelinePatchFailure("Patch must identify at least one exact addressed diagnostic code.");
        if (patch["operations"] is not JsonArray operations
            || operations.Count is < 1 or > MaxPipelinePatchOperations)
        {
            throw BuildPipelinePatchFailure($"Patch operations must contain between 1 and {MaxPipelinePatchOperations} entries.");
        }

        var leaves = bestCandidate.Subworkflows.ToList();
        var main = bestCandidate.MainWorkflowPrompt;
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var mainTouched = false;
        foreach (var node in operations)
        {
            if (node is not JsonObject operation)
                throw BuildPipelinePatchFailure("Every patch operation must be an object.");
            var op = GetStringProperty(operation, "op") ?? "";
            var target = GetStringProperty(operation, "target") ?? "";
            var sources = ReadPatchSources(operation["sources"] as JsonArray);
            var leafNode = operation["leaf"] as JsonObject;

            switch (op)
            {
                case "add_leaf":
                {
                    var added = ParsePatchedLeaf(
                        leafNode,
                        Array.Empty<PipelinePlannedTool>(),
                        Array.Empty<string>(),
                        Array.Empty<PipelinePlannedNativeStep>(),
                        Array.Empty<string>());
                    if (!string.Equals(added.WorkKind, PipelineWorkKindDeterministicShaping, StringComparison.Ordinal)
                        || !string.Equals(added.ContractRole, PipelineContractRoleAlgorithmicTransform, StringComparison.Ordinal))
                    {
                        throw BuildPipelinePatchFailure(
                            $"add_leaf '{added.Name}' must be local deterministic algorithmic work because a patch cannot invent external ownership.");
                    }
                    if (leaves.Any(candidate => string.Equals(candidate.Name, added.Name, StringComparison.Ordinal))
                        || !touched.Add(added.Name))
                    {
                        throw BuildPipelinePatchFailure($"add_leaf target '{added.Name}' already exists or is modified more than once.");
                    }
                    leaves.Add(added);
                    break;
                }
                case "replace_leaf":
                {
                    var index = FindPatchLeafIndex(leaves, target);
                    if (!touched.Add(target))
                        throw BuildPipelinePatchFailure($"Leaf '{target}' is modified more than once.");
                    var existing = leaves[index];
                    var replacement = ParsePatchedLeaf(
                        leafNode,
                        GetImmutablePipelinePlannedTools(existing.PlannedTools),
                        existing.LocalOperationIds ?? Array.Empty<string>(),
                        existing.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>(),
                        existing.OwnedOperationIds ?? Array.Empty<string>());
                    if (!string.Equals(replacement.Name, target, StringComparison.Ordinal))
                        throw BuildPipelinePatchFailure("replace_leaf must preserve the target leaf name.");
                    leaves[index] = PreserveTargetedPatchContractSchemas(replacement, existing);
                    break;
                }
                case "remove_leaf":
                {
                    var index = FindPatchLeafIndex(leaves, target);
                    if (!touched.Add(target))
                        throw BuildPipelinePatchFailure($"Leaf '{target}' is modified more than once.");
                    EnsurePatchLeafHasNoOwnership(leaves[index]);
                    leaves.RemoveAt(index);
                    break;
                }
                case "merge_leaves":
                {
                    if (sources.Count < 2 || sources.Distinct(StringComparer.Ordinal).Count() != sources.Count)
                        throw BuildPipelinePatchFailure("merge_leaves requires at least two distinct source names.");
                    if (sources.Any(source => !touched.Add(source)))
                        throw BuildPipelinePatchFailure("A merge source is modified more than once.");
                    var sourceLeaves = sources.Select(source => leaves[FindPatchLeafIndex(leaves, source)]).ToArray();
                    var plannedTools = GetImmutablePipelinePlannedTools(sourceLeaves
                        .SelectMany(static source => source.PlannedTools));
                    var localOperations = sourceLeaves
                        .SelectMany(static source => source.LocalOperationIds ?? Array.Empty<string>())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var nativeSteps = sourceLeaves
                        .SelectMany(static source => source.PlannedNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
                        .DistinctBy(static step => BuildPlannedNativeStepJson(step).ToJsonString(), StringComparer.Ordinal)
                        .ToArray();
                    var ownedOperations = sourceLeaves
                        .SelectMany(static source => source.OwnedOperationIds ?? Array.Empty<string>())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var merged = ParsePatchedLeaf(
                        leafNode,
                        plannedTools,
                        localOperations,
                        nativeSteps,
                        ownedOperations);
                    foreach (var sourceLeaf in sourceLeaves)
                        merged = PreserveTargetedPatchContractSchemas(merged, sourceLeaf);
                    if (leaves.Any(candidate => !sources.Contains(candidate.Name, StringComparer.Ordinal)
                                                && string.Equals(candidate.Name, merged.Name, StringComparison.Ordinal)))
                    {
                        throw BuildPipelinePatchFailure($"merge_leaves result '{merged.Name}' conflicts with an existing leaf.");
                    }
                    leaves.RemoveAll(candidate => sources.Contains(candidate.Name, StringComparer.Ordinal));
                    leaves.Add(merged);
                    break;
                }
                case "replace_main_orchestration":
                    if (mainTouched)
                        throw BuildPipelinePatchFailure("Main orchestration is modified more than once.");
                    main = GetStringProperty(operation, "main_orchestration")?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(main))
                        throw BuildPipelinePatchFailure("replace_main_orchestration requires non-empty content.");
                    mainTouched = true;
                    break;
                default:
                    throw BuildPipelinePatchFailure($"Unsupported patch operation '{op}'.");
            }
        }

        var extraction = bestCandidate with
        {
            Subworkflows = leaves,
            MainWorkflowPrompt = main,
            ValidationErrors = Array.Empty<string>(),
            RootCauses = Array.Empty<PipelineRootCause>(),
            QualityReview = null,
            QualityWarnings = null
        };
        return new PipelineRepairApplication(
            extraction,
            RenderPipelineExtractionAsAnnotatedMarkdown(extraction),
            operations.Count,
            expectedFingerprint,
            addressedCodes.ToHashSet(StringComparer.Ordinal));
    }

    private static IReadOnlyList<string> GetAddressablePipelineDiagnosticCodes(WorkflowPipelineExtraction extraction)
    {
        var codes = extraction.QualityReview?.Diagnostics
            .Where(static diagnostic => diagnostic.EvidenceQualified
                                        && string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal))
            .Select(static diagnostic => diagnostic.Code)
            .ToList() ?? [];
        codes.AddRange(extraction.ValidationErrors
            .Select(static error => error.Split(':', 2)[0].Trim())
            .Where(static code => code.Length > 0));
        return codes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static WorkflowPipelineSubworkflowSpec ParsePatchedLeaf(
        JsonObject? leaf,
        IReadOnlyList<PipelinePlannedTool> plannedTools,
        IReadOnlyList<string> localOperationIds,
        IReadOnlyList<PipelinePlannedNativeStep> nativeSteps,
        IReadOnlyList<string> ownedOperationIds)
    {
        if (leaf == null)
            throw BuildPipelinePatchFailure("This patch operation requires a complete leaf payload.");

        var errors = new List<string>();
        var name = GetStringProperty(leaf, "name")?.Trim() ?? "";
        var goal = GetStringProperty(leaf, "goal")?.Trim() ?? "";
        var extractReason = GetStringProperty(leaf, "extract_reason")?.Trim() ?? "";
        var content = GetStringProperty(leaf, "content")?.Trim() ?? "";
        if (!SnakeCaseNameRegex().IsMatch(name))
            errors.Add($"Patched leaf name '{name}' must use snake_case.");
        if (string.IsNullOrWhiteSpace(goal))
            errors.Add($"Patched leaf '{name}' is missing goal.");
        if (string.IsNullOrWhiteSpace(extractReason))
            errors.Add($"Patched leaf '{name}' is missing extract_reason.");
        if (string.IsNullOrWhiteSpace(content))
            errors.Add($"Patched leaf '{name}' is missing content.");
        if (SubworkflowCallMentionRegex().IsMatch(content))
            errors.Add($"Patched leaf '{name}' appears to call another subworkflow.");

        var inputSchemas = ParseStructuredContractFields(leaf["inputs"] as JsonArray, name, "inputs", errors);
        var outputSchemas = ParseStructuredContractFields(leaf["outputs"] as JsonArray, name, "outputs", errors);
        if (errors.Count > 0)
            throw BuildPipelinePatchFailure(string.Join("; ", errors));

        var inputs = inputSchemas.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value is JsonObject schema ? NormalizeWorkflowSchemaType(GetStringProperty(schema, "type") ?? "any") : "any",
            StringComparer.Ordinal);
        var outputs = outputSchemas.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value is JsonObject schema ? NormalizeWorkflowSchemaType(GetStringProperty(schema, "type") ?? "any") : "any",
            StringComparer.Ordinal);
        var result = new WorkflowPipelineSubworkflowSpec(
            name,
            goal,
            GetStringProperty(leaf, "description"),
            NormalizePipelineWorkKind(GetStringProperty(leaf, "work_kind")),
            NormalizePipelineContractRole(GetStringProperty(leaf, "contract_role")),
            GetStringProperty(leaf, "concrete_outcome"),
            inputs,
            outputs,
            inputSchemas,
            outputSchemas,
            plannedTools,
            ExtractionScore: null,
            extractReason,
            content,
            GenerationPrompt: "",
            localOperationIds,
            nativeSteps)
        {
            OwnedOperationIds = ownedOperationIds
        };
        return result with { GenerationPrompt = BuildSubworkflowGenerationPrompt(result) };
    }

    private static WorkflowPipelineSubworkflowSpec PreserveTargetedPatchContractSchemas(
        WorkflowPipelineSubworkflowSpec replacement,
        WorkflowPipelineSubworkflowSpec previous)
    {
        var inputSchemas = PreserveTargetedPatchInputContractSchemaSet(
            replacement.InputSchemas,
            previous.InputSchemas);
        var outputSchemas = PreserveTargetedPatchContractSchemaSet(
            replacement.OutputSchemas,
            previous.OutputSchemas);
        var preserved = replacement with
        {
            Inputs = BuildPipelineContractTypeMap(inputSchemas),
            Outputs = BuildPipelineContractTypeMap(outputSchemas),
            InputSchemas = inputSchemas,
            OutputSchemas = outputSchemas
        };
        return preserved with { GenerationPrompt = BuildSubworkflowGenerationPrompt(preserved) };
    }

    private static IReadOnlyDictionary<string, JsonNode?> PreserveTargetedPatchInputContractSchemaSet(
        IReadOnlyDictionary<string, JsonNode?> current,
        IReadOnlyDictionary<string, JsonNode?> previous)
        => PreservePreviouslyValidatedContractSchemas(current, previous)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value?.DeepClone(), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, JsonNode?> PreserveTargetedPatchContractSchemaSet(
        IReadOnlyDictionary<string, JsonNode?> current,
        IReadOnlyDictionary<string, JsonNode?> previous)
    {
        var preserved = PreservePreviouslyValidatedContractSchemas(current, previous)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value?.DeepClone(), StringComparer.Ordinal);
        foreach (var (name, schema) in previous)
        {
            if (!preserved.ContainsKey(name))
                preserved[name] = schema?.DeepClone();
        }
        return preserved;
    }

    private static IReadOnlyDictionary<string, string> BuildPipelineContractTypeMap(
        IReadOnlyDictionary<string, JsonNode?> schemas)
        => schemas.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value is JsonObject schema
                ? NormalizeWorkflowSchemaType(GetStringProperty(schema, "type") ?? "any")
                : "any",
            StringComparer.Ordinal);

    private static IReadOnlyList<string> ReadPatchSources(JsonArray? sources)
        => sources?.OfType<JsonValue>()
               .Select(static value => value.TryGetValue<string>(out var source) ? source?.Trim() : null)
               .Where(static source => !string.IsNullOrWhiteSpace(source))
               .Select(static source => source!)
               .ToArray()
           ?? Array.Empty<string>();

    private static int FindPatchLeafIndex(IReadOnlyList<WorkflowPipelineSubworkflowSpec> leaves, string name)
    {
        for (var index = 0; index < leaves.Count; index++)
        {
            if (string.Equals(leaves[index].Name, name, StringComparison.Ordinal))
                return index;
        }
        throw BuildPipelinePatchFailure($"Patch references unknown leaf '{name}'.");
    }

    private static void EnsurePatchLeafHasNoOwnership(WorkflowPipelineSubworkflowSpec leaf)
    {
        if (leaf.PlannedTools.Any(IsImmutablePipelinePlannedTool)
            || (leaf.PlannedNativeSteps?.Count ?? 0) > 0
            || (leaf.LocalOperationIds?.Count ?? 0) > 0)
        {
            throw BuildPipelinePatchFailure($"Leaf '{leaf.Name}' owns immutable capabilities or operations and cannot be removed.");
        }
    }

    private static WorkflowRuntimeException BuildPipelinePatchFailure(string message)
        => new(
            ErrorCodes.TemplatePlan,
            "workflow.plan targeted extraction patch failed: " + message,
            details: new JsonObject
            {
                ["classification"] = "contract_violation",
                ["stage"] = "patch_pipeline_extraction",
                ["recommended_action"] = "repair_plan_output"
            });

    private static string BuildPipelineExtractionFingerprint(WorkflowPipelineExtraction extraction)
    {
        var canonical = new JsonObject
        {
            ["subworkflows"] = new JsonArray(extraction.Subworkflows.Select(static spec => (JsonNode)BuildSpecJson(spec)).ToArray()),
            ["main_workflow_prompt"] = extraction.MainWorkflowPrompt,
            ["main_local_operation_ids"] = BuildStringArrayJson(extraction.MainLocalOperationIds ?? Array.Empty<string>()),
            ["main_native_steps"] = BuildPlannedNativeStepsJson(extraction.MainNativeSteps ?? Array.Empty<PipelinePlannedNativeStep>())
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToJsonString()))).ToLowerInvariant();
    }

    private static bool IsPipelineExtractionCandidateStrictlyBetter(
        WorkflowPipelineExtraction candidate,
        int candidatePatchOperations,
        WorkflowPipelineExtraction best,
        int bestPatchOperations)
    {
        var candidateRank = BuildPipelineExtractionCandidateRank(candidate, candidatePatchOperations);
        var bestRank = BuildPipelineExtractionCandidateRank(best, bestPatchOperations);
        return candidateRank.CompareTo(bestRank) < 0;
    }

    private static bool IsPipelineExtractionPatchableInvalidCandidate(
        WorkflowPipelineExtraction candidate)
        => candidate.Subworkflows.Count > 0
           && candidate.ValidationErrors.Count > 0
           && candidate.ValidationErrors.All(IsPipelineExtractionPatchableValidationError);

    private static bool IsPipelineExtractionPatchableValidationError(string error)
        => error.StartsWith("PIPELINE_EXTRACTION_", StringComparison.Ordinal)
           || error.StartsWith("WEAK_EXTRACTION_INPUT_SCHEMA:", StringComparison.Ordinal)
           || error.StartsWith("WEAK_EXTRACTION_OUTPUT_SCHEMA:", StringComparison.Ordinal);

    private static bool IsPipelineExtractionValidationStrictlyBetter(
        WorkflowPipelineExtraction candidate,
        int candidatePatchOperations,
        WorkflowPipelineExtraction best,
        int bestPatchOperations)
    {
        var comparison = candidate.ValidationErrors.Count.CompareTo(best.ValidationErrors.Count);
        if (comparison != 0)
            return comparison < 0;

        comparison = candidate.RootCauses.Count(static cause => cause.Primary)
            .CompareTo(best.RootCauses.Count(static cause => cause.Primary));
        if (comparison != 0)
            return comparison < 0;

        return candidatePatchOperations < bestPatchOperations;
    }

    private static WorkflowPipelineExtraction RevalidatePatchedPipelineExtraction(
        WorkflowPipelineExtraction extraction,
        PipelineMcpContext pipelineMcpContext)
    {
        var validationErrors = new List<string>();
        var rootCauses = new List<PipelineRootCause>();
        var subworkflows = extraction.Subworkflows.Select(spec =>
        {
            ValidatePlannedToolsAgainstMcpContext(
                spec.Name,
                spec.PlannedTools,
                pipelineMcpContext,
                validationErrors);
            var normalized = ApplyRequiredLeafToolContracts(
                spec,
                pipelineMcpContext,
                validationErrors,
                rootCauses);
            var score = ScorePipelineExtractionSpec(normalized, pipelineMcpContext);
            var scored = normalized with { ExtractionScore = score };
            ValidatePipelineExtractionQuality(
                scored,
                pipelineMcpContext,
                validationErrors,
                rootCauses);
            return scored with { GenerationPrompt = BuildSubworkflowGenerationPrompt(scored) };
        }).ToArray();

        return extraction with
        {
            Subworkflows = subworkflows,
            ValidationErrors = validationErrors.Distinct(StringComparer.Ordinal).ToArray(),
            RootCauses = rootCauses
                .DistinctBy(static cause => string.Join(
                    "|",
                    cause.Category,
                    cause.Phase,
                    cause.LeafName,
                    cause.OutputName,
                    cause.InvalidPath,
                    cause.Code,
                    cause.Message,
                    cause.Primary), StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static PipelineExtractionQualityReview QualifyPipelineExtractionQualityEvidence(
        string normalizedMarkdown,
        WorkflowPipelineExtraction extraction,
        PipelineMcpContext pipelineMcpContext,
        PipelineExtractionQualityReview review)
    {
        var extractionJson = BuildExtractionJson(extraction);
        var capabilityJson = BuildPipelineMcpContextJson(pipelineMcpContext);
        var diagnostics = review.Diagnostics.Select(diagnostic =>
        {
            var evidence = diagnostic.Evidence ?? Array.Empty<PipelineExtractionQualityEvidence>();
            var qualified = evidence.Any(item => IsPipelineExtractionQualityEvidenceValid(
                item,
                normalizedMarkdown,
                extractionJson,
                capabilityJson));

            if (qualified || !string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal))
                return diagnostic with { EvidenceQualified = qualified };

            return diagnostic with
            {
                Severity = "warning",
                EvidenceQualified = false,
                Message = diagnostic.Message + " The blocking claim was downgraded because its evidence references could not be verified."
            };
        }).ToArray();

        var hasBlocking = diagnostics.Any(static diagnostic => diagnostic.EvidenceQualified
                                                            && string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal));
        return review with
        {
            Verdict = hasBlocking ? "retry" : "pass",
            Diagnostics = diagnostics
        };
    }

    private static PipelineExtractionQualityReview StabilizePipelineExtractionQualityReviewAgainstBaseline(
        WorkflowPipelineExtraction baseline,
        WorkflowPipelineExtraction candidate,
        PipelineExtractionQualityReview review,
        IReadOnlySet<string> addressedDiagnosticCodes)
    {
        var changedLeaves = GetStructurallyChangedPipelineLeafNames(baseline, candidate);
        var mainChanged = !string.Equals(
            baseline.MainWorkflowPrompt,
            candidate.MainWorkflowPrompt,
            StringComparison.Ordinal);
        var baselineByCode = (baseline.QualityReview?.Diagnostics
                              ?? Array.Empty<PipelineExtractionQualityDiagnostic>())
            .GroupBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var stabilized = new List<PipelineExtractionQualityDiagnostic>();
        var observedCodes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var diagnostic in review.Diagnostics)
        {
            observedCodes.Add(diagnostic.Code);
            if (baselineByCode.TryGetValue(diagnostic.Code, out var baselineDiagnostic))
            {
                var affected = IsQualityDiagnosticAffectedByChangedExtractionSurface(
                                   baselineDiagnostic,
                                   baseline,
                                   changedLeaves,
                                   mainChanged,
                                   allowQualifiedGlobalEvidence: true)
                               || IsQualityDiagnosticAffectedByChangedExtractionSurface(
                                   diagnostic,
                                   candidate,
                                   changedLeaves,
                                   mainChanged,
                                   allowQualifiedGlobalEvidence: false);
                var deterministicallyResolved = IsBaselineQualityDiagnosticDeterministicallyResolved(
                    baselineDiagnostic,
                    candidate,
                    addressedDiagnosticCodes,
                    changedLeaves,
                    mainChanged);
                stabilized.Add(affected && deterministicallyResolved ? diagnostic : baselineDiagnostic);
                continue;
            }

            if (string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal)
                && !IsQualityDiagnosticAffectedByChangedExtractionSurface(
                    diagnostic,
                    candidate,
                    changedLeaves,
                    mainChanged,
                    allowQualifiedGlobalEvidence: false))
            {
                stabilized.Add(diagnostic with
                {
                    Severity = "info",
                    EvidenceQualified = false,
                    Message = diagnostic.Message
                              + " The new blocking claim was made against an extraction surface unchanged by the targeted patch and is advisory for this candidate comparison."
                });
                continue;
            }

            stabilized.Add(diagnostic);
        }

        foreach (var baselineDiagnostic in baselineByCode.Values)
        {
            if (observedCodes.Contains(baselineDiagnostic.Code)
                || IsBaselineQualityDiagnosticDeterministicallyResolved(
                    baselineDiagnostic,
                    candidate,
                    addressedDiagnosticCodes,
                    changedLeaves,
                    mainChanged))
            {
                continue;
            }

            stabilized.Add(baselineDiagnostic);
        }

        var hasBlocking = stabilized.Any(static diagnostic => diagnostic.EvidenceQualified
                                                                 && string.Equals(
                                                                     diagnostic.Severity,
                                                                     "critical",
                                                                     StringComparison.Ordinal));
        return review with
        {
            Verdict = hasBlocking ? "retry" : "pass",
            Diagnostics = stabilized
        };
    }

    private static bool IsBaselineQualityDiagnosticDeterministicallyResolved(
        PipelineExtractionQualityDiagnostic diagnostic,
        WorkflowPipelineExtraction candidate,
        IReadOnlySet<string> addressedDiagnosticCodes,
        IReadOnlySet<string> changedLeaves,
        bool mainChanged)
    {
        if (!addressedDiagnosticCodes.Contains(diagnostic.Code)
            || !IsQualityDiagnosticAffectedByChangedExtractionSurface(
                diagnostic,
                candidate,
                changedLeaves,
                mainChanged,
                allowQualifiedGlobalEvidence: true))
        {
            return false;
        }

        // A named extraction surface is itself the stable remediation path. Once the
        // patch explicitly addresses the code and changes that exact surface, the
        // subsequent deterministic extraction validation and delta review decide
        // whether the diagnostic remains. Unrelated leaf/main changes cannot clear it.
        if (!string.IsNullOrWhiteSpace(diagnostic.LeafName))
        {
            return string.Equals(diagnostic.LeafName, "main", StringComparison.Ordinal)
                ? mainChanged
                : changedLeaves.Contains(diagnostic.LeafName);
        }

        var extractionEvidence = (diagnostic.Evidence ?? Array.Empty<PipelineExtractionQualityEvidence>())
            .Where(static evidence => string.Equals(evidence.Source, "extraction", StringComparison.Ordinal))
            .ToArray();
        if (extractionEvidence.Length == 0)
        {
            // Request/capability evidence establishes the obligation, while the
            // extraction surface establishes whether its representation changed.
            // The follow-up deterministic validation and delta review must still
            // pass before the baseline diagnostic can disappear.
            return diagnostic.EvidenceQualified && (mainChanged || changedLeaves.Count > 0);
        }

        var candidateJson = BuildExtractionJson(candidate);
        return extractionEvidence.All(evidence =>
        {
            if (!TryResolveEvidenceJsonPointer(candidateJson, evidence.Reference, out var resolved)
                || resolved == null)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(evidence.Excerpt))
            {
                return (mainChanged && evidence.Reference.StartsWith("/main_workflow_prompt", StringComparison.Ordinal))
                       || EvidencePointerTargetsChangedLeaf(evidence.Reference, candidate, changedLeaves);
            }

            var canonicalValue = resolved is JsonValue scalar
                                 && scalar.TryGetValue<string>(out var text)
                ? text
                : resolved.ToJsonString(PromptJsonOptions);
            return !canonicalValue.Contains(evidence.Excerpt, StringComparison.Ordinal);
        });
    }

    private static bool EvidencePointerTargetsChangedLeaf(
        string reference,
        WorkflowPipelineExtraction extraction,
        IReadOnlySet<string> changedLeaves)
    {
        var segments = reference.Split('/').Skip(1)
            .Select(static segment => segment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal))
            .ToArray();
        return segments.Length >= 2
               && string.Equals(segments[0], "subworkflows", StringComparison.Ordinal)
               && int.TryParse(segments[1], out var index)
               && index >= 0
               && index < extraction.Subworkflows.Count
               && changedLeaves.Contains(extraction.Subworkflows[index].Name);
    }

    private static JsonObject BuildPipelineExtractionChangedSurfacesJson(
        WorkflowPipelineExtraction baseline,
        WorkflowPipelineExtraction candidate)
        => new()
        {
            ["changed_leaf_names"] = BuildStringArrayJson(
                GetStructurallyChangedPipelineLeafNames(baseline, candidate)
                    .Order(StringComparer.Ordinal)
                    .ToArray()),
            ["main_orchestration_changed"] = !string.Equals(
                baseline.MainWorkflowPrompt,
                candidate.MainWorkflowPrompt,
                StringComparison.Ordinal)
        };

    private static HashSet<string> GetStructurallyChangedPipelineLeafNames(
        WorkflowPipelineExtraction baseline,
        WorkflowPipelineExtraction candidate)
    {
        var baselineLeaves = baseline.Subworkflows.ToDictionary(
            static leaf => leaf.Name,
            static leaf => BuildSpecJson(leaf).ToJsonString(),
            StringComparer.Ordinal);
        var candidateLeaves = candidate.Subworkflows.ToDictionary(
            static leaf => leaf.Name,
            static leaf => BuildSpecJson(leaf).ToJsonString(),
            StringComparer.Ordinal);
        var changed = new HashSet<string>(baselineLeaves.Keys, StringComparer.Ordinal);
        changed.UnionWith(candidateLeaves.Keys);
        changed.RemoveWhere(name => baselineLeaves.TryGetValue(name, out var baselineJson)
                                    && candidateLeaves.TryGetValue(name, out var candidateJson)
                                    && string.Equals(baselineJson, candidateJson, StringComparison.Ordinal));
        return changed;
    }

    private static bool IsQualityDiagnosticAffectedByChangedExtractionSurface(
        PipelineExtractionQualityDiagnostic diagnostic,
        WorkflowPipelineExtraction extraction,
        IReadOnlySet<string> changedLeaves,
        bool mainChanged,
        bool allowQualifiedGlobalEvidence)
    {
        if (!string.IsNullOrWhiteSpace(diagnostic.LeafName)
            && changedLeaves.Contains(diagnostic.LeafName))
        {
            return true;
        }

        var hasExtractionEvidence = false;
        foreach (var evidence in diagnostic.Evidence ?? Array.Empty<PipelineExtractionQualityEvidence>())
        {
            if (!string.Equals(evidence.Source, "extraction", StringComparison.Ordinal)
                || !evidence.Reference.StartsWith("/", StringComparison.Ordinal))
            {
                continue;
            }
            hasExtractionEvidence = true;

            var segments = evidence.Reference.Split('/').Skip(1)
                .Select(static segment => segment.Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal))
                .ToArray();
            if (segments.Length == 0)
                continue;
            if (mainChanged && string.Equals(segments[0], "main_workflow_prompt", StringComparison.Ordinal))
                return true;
            if (segments.Length >= 2
                && string.Equals(segments[0], "subworkflows", StringComparison.Ordinal)
                && int.TryParse(segments[1], out var index)
                && index >= 0
                && index < extraction.Subworkflows.Count
                && changedLeaves.Contains(extraction.Subworkflows[index].Name))
            {
                return true;
            }
        }

        return allowQualifiedGlobalEvidence
               && diagnostic.EvidenceQualified
               && !hasExtractionEvidence
               && (mainChanged || changedLeaves.Count > 0);
    }

    private static bool IsPipelineExtractionQualityEvidenceValid(
        PipelineExtractionQualityEvidence evidence,
        string normalizedMarkdown,
        JsonNode extractionJson,
        JsonNode capabilityJson)
    {
        if (string.IsNullOrWhiteSpace(evidence.Reference))
            return false;

        if (evidence.Source == "request")
        {
            return evidence.Reference.Length >= 4
                   && normalizedMarkdown.Contains(evidence.Reference, StringComparison.Ordinal)
                   && (evidence.Excerpt == null
                       || string.Equals(evidence.Excerpt, evidence.Reference, StringComparison.Ordinal));
        }

        var root = evidence.Source switch
        {
            "extraction" => extractionJson,
            "capability_contract" => capabilityJson,
            _ => null
        };
        if (root == null
            || !TryResolveEvidenceJsonPointer(root, evidence.Reference, out var resolved)
            || resolved == null)
        {
            return false;
        }

        // A missing excerpt is accepted only for legacy non-structured reviewers. The
        // current strict schema always requires it, so live blocking claims must prove
        // both pointer existence and content at that pointer.
        if (evidence.Excerpt == null)
            return true;
        if (string.IsNullOrWhiteSpace(evidence.Excerpt))
            return false;

        var canonicalValue = resolved is JsonValue scalar
            && scalar.TryGetValue<string>(out var text)
                ? text
                : resolved.ToJsonString(PromptJsonOptions);
        return canonicalValue.Contains(evidence.Excerpt, StringComparison.Ordinal);
    }

    private static bool TryResolveEvidenceJsonPointer(JsonNode root, string pointer, out JsonNode? value)
    {
        value = root;
        if (pointer.Length == 0)
            return true;
        if (!pointer.StartsWith("/", StringComparison.Ordinal))
            return false;

        foreach (var encodedSegment in pointer.Split('/').Skip(1))
        {
            var segment = encodedSegment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            switch (value)
            {
                case JsonObject obj when obj.TryGetPropertyValue(segment, out var child):
                    value = child;
                    break;
                case JsonArray array when int.TryParse(segment, out var index)
                                          && index >= 0
                                          && index < array.Count:
                    value = array[index];
                    break;
                default:
                    value = null;
                    return false;
            }
        }

        return value != null;
    }

    private static IReadOnlyList<PipelineExtractionQualityEvidence> ParseExtractionQualityEvidence(JsonArray? evidence)
    {
        if (evidence == null)
            return Array.Empty<PipelineExtractionQualityEvidence>();

        var result = new List<PipelineExtractionQualityEvidence>();
        foreach (var node in evidence)
        {
            if (node is not JsonObject item)
                continue;
            var source = GetStringProperty(item, "source")?.Trim().ToLowerInvariant() ?? "";
            var reference = GetStringProperty(item, "reference")?.Trim() ?? "";
            var excerpt = GetStringProperty(item, "excerpt")?.Trim();
            if (source is "request" or "extraction" or "capability_contract"
                && !string.IsNullOrWhiteSpace(reference))
            {
                result.Add(new PipelineExtractionQualityEvidence(
                    source,
                    reference,
                    string.IsNullOrWhiteSpace(excerpt) ? null : excerpt));
            }
        }
        return result;
    }

    private static JsonArray BuildPipelineExtractionQualityEvidenceJson(
        IReadOnlyList<PipelineExtractionQualityEvidence>? evidence)
        => new((evidence ?? Array.Empty<PipelineExtractionQualityEvidence>())
            .Select(static item => (JsonNode)new JsonObject
            {
                ["source"] = item.Source,
                ["reference"] = item.Reference,
                ["excerpt"] = item.Excerpt ?? ""
            })
            .ToArray());

    private static WorkflowRuntimeException BuildPipelineQualityReviewContractFailure(Exception exception)
        => new(
            ErrorCodes.TemplatePlan,
            "workflow.plan extraction quality review violated its structured contract after one bounded repair.",
            details: new JsonObject
            {
                ["stage"] = "review_extraction_quality",
                ["classification"] = "contract_violation",
                ["recommended_action"] = "repair_plan_output",
                ["error_type"] = exception.GetType().Name
            });

    private static bool IsPipelineQualityReviewContractFailure(Exception exception)
        => exception is WorkflowRuntimeException { Details: JsonObject details }
           && string.Equals(GetStringProperty(details, "stage"), "review_extraction_quality", StringComparison.Ordinal)
           && string.Equals(GetStringProperty(details, "classification"), "contract_violation", StringComparison.Ordinal);

    private static PipelineExtractionCandidateRank BuildPipelineExtractionCandidateRank(
        WorkflowPipelineExtraction extraction,
        int patchOperations)
    {
        var diagnostics = extraction.QualityReview?.Diagnostics ?? Array.Empty<PipelineExtractionQualityDiagnostic>();
        var qualifiedCritical = diagnostics.Count(static diagnostic =>
            diagnostic.EvidenceQualified
            && string.Equals(diagnostic.Severity, "critical", StringComparison.Ordinal));
        var warnings = diagnostics.Count(static diagnostic =>
            string.Equals(diagnostic.Severity, "warning", StringComparison.Ordinal));
        return new PipelineExtractionCandidateRank(
            qualifiedCritical,
            warnings,
            -(extraction.QualityReview?.Score ?? 0),
            patchOperations);
    }

    private static WorkflowRuntimeException BuildPipelineExtractionRepairStalledException(
        int attempt,
        string reason,
        WorkflowPipelineExtraction bestCandidate,
        int qualityNonImprovingAttempts = 0,
        int deterministicRegressionAttempts = 0,
        int validationNonImprovingAttempts = 0,
        IReadOnlyList<string>? lastDeterministicValidationErrors = null)
    {
        var diagnostics = bestCandidate.QualityReview?.Diagnostics
            .Select(static diagnostic => (JsonNode)new JsonObject
            {
                ["code"] = diagnostic.Code,
                ["kind"] = diagnostic.Kind,
                ["severity"] = diagnostic.Severity,
                ["evidence_qualified"] = diagnostic.EvidenceQualified
            })
            .ToArray() ?? Array.Empty<JsonNode>();
        var validationCodes = bestCandidate.ValidationErrors
            .Select(static error => error.Split(':', 2)[0].Trim())
            .Where(static code => code.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(static code => (JsonNode?)JsonValue.Create(code))
            .ToArray();
        var diagnosticSummary = diagnostics.Length > 0
            ? string.Join(", ", diagnostics
                .OfType<JsonObject>()
                .Where(static diagnostic => diagnostic["evidence_qualified"]?.GetValue<bool>() == true
                                            && string.Equals(
                                                GetStringProperty(diagnostic, "severity"),
                                                "critical",
                                                StringComparison.Ordinal))
                .Select(static diagnostic => GetStringProperty(diagnostic, "code"))
                .Where(static code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.Ordinal))
            : string.Join(", ", validationCodes
                .OfType<JsonValue>()
                .Select(static value => value.GetValue<string>()));
        var lastDeterministicValidationCodes = (lastDeterministicValidationErrors ?? Array.Empty<string>())
            .Select(static error => error.Split(':', 2)[0].Trim())
            .Where(static code => code.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(static code => (JsonNode?)JsonValue.Create(code))
            .ToArray();
        var rejectionSummary = qualityNonImprovingAttempts == 0
                               && deterministicRegressionAttempts == 0
                               && validationNonImprovingAttempts == 0
            ? string.Empty
            : $" Rejections: quality non-improvement={qualityNonImprovingAttempts}, deterministic regression={deterministicRegressionAttempts}, validation non-improvement={validationNonImprovingAttempts}.";
        return new WorkflowRuntimeException(
            ErrorCodes.WorkflowPlanRepairStalled,
            "Workflow extraction repair stopped without emitting a regressed candidate. " + reason
            + rejectionSummary
            + (diagnosticSummary.Length == 0 ? string.Empty : " Remaining diagnostic codes: " + diagnosticSummary + "."),
            details: new JsonObject
            {
                ["phase"] = "pipeline_extraction",
                ["attempt"] = attempt,
                ["classification"] = "plan_defect",
                ["best_candidate_fingerprint"] = BuildPipelineExtractionFingerprint(bestCandidate),
                ["best_candidate_score"] = bestCandidate.QualityReview?.Score,
                ["best_candidate_diagnostics"] = new JsonArray(diagnostics),
                ["best_candidate_validation_codes"] = new JsonArray(validationCodes),
                ["quality_non_improving_attempts"] = qualityNonImprovingAttempts,
                ["deterministic_regression_attempts"] = deterministicRegressionAttempts,
                ["validation_non_improving_attempts"] = validationNonImprovingAttempts,
                ["last_deterministic_validation_codes"] = new JsonArray(lastDeterministicValidationCodes),
                ["leaf_ownership"] = BuildPipelinePatchOwnershipSummary(bestCandidate),
                ["recommended_action"] = "revise_request_contract_or_use_more_capable_model"
            });
    }

    private static bool IsPipelineExtractionRepairStalled(Exception exception)
        => exception is WorkflowRuntimeException { Code: ErrorCodes.WorkflowPlanRepairStalled };

    private readonly record struct PipelineExtractionCandidateRank(
        int CriticalCount,
        int WarningCount,
        int NegativeScore,
        int PatchOperationCount) : IComparable<PipelineExtractionCandidateRank>
    {
        public int CompareTo(PipelineExtractionCandidateRank other)
        {
            var comparison = CriticalCount.CompareTo(other.CriticalCount);
            if (comparison != 0)
                return comparison;
            comparison = WarningCount.CompareTo(other.WarningCount);
            if (comparison != 0)
                return comparison;
            comparison = NegativeScore.CompareTo(other.NegativeScore);
            if (comparison != 0)
                return comparison;
            return PatchOperationCount.CompareTo(other.PatchOperationCount);
        }
    }

    private static string RenderPipelineExtractionAsAnnotatedMarkdown(WorkflowPipelineExtraction extraction)
    {
        var sb = new StringBuilder("# Workflow pipeline\n\n");
        foreach (var leaf in extraction.Subworkflows)
        {
            sb.Append(":::subworkflow name=\"").Append(leaf.Name).AppendLine("\"");
            sb.Append("goal: ").AppendLine(leaf.Goal);
            sb.AppendLine("inputs:");
            foreach (var input in leaf.Inputs)
                sb.Append("  ").Append(input.Key).Append(": ").AppendLine(input.Value);
            sb.AppendLine("outputs:");
            foreach (var output in leaf.Outputs)
                sb.Append("  ").Append(output.Key).Append(": ").AppendLine(output.Value);
            sb.Append("extract_reason: ").AppendLine(leaf.ExtractReason);
            sb.AppendLine("content:");
            foreach (var line in leaf.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                sb.Append("  ").AppendLine(line);
            sb.AppendLine(":::").AppendLine();
        }
        if (!extraction.MainWorkflowPrompt.Contains("## Main workflow orchestration", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("## Main workflow orchestration").AppendLine();
        sb.AppendLine(extraction.MainWorkflowPrompt.Trim());
        return sb.ToString().Trim();
    }
}
