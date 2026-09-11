using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime.Executors;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core;
using Expressions = GnOuGo.Flow.Core.Expressions;
using Parsing = GnOuGo.Flow.Core.Parsing;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;


namespace GnOuGo.Flow.Planning.Capabilities;

internal static partial class CapabilityContracts
{
    internal const int PhysicalCapabilityMaxPages = 64;
    internal const int PhysicalCapabilityMaxCandidatesPerInventoryItem = 24;
    internal const int PhysicalCapabilityDescriptionMaxCharacters = 384;

    internal sealed record PhysicalCapabilityEntry(
        string Id,
        string Server,
        string Kind,
        string Method,
        string Card,
        IReadOnlyList<string> SelectorIntentValues);

    internal sealed record PhysicalCapabilityCatalog(
        IReadOnlyList<PhysicalCapabilityEntry> Entries,
        int TotalCharacters);

    internal sealed record PhysicalCandidateSelection(
        IReadOnlyDictionary<string, IReadOnlyList<string>> OperationCandidates,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ConstraintCandidates,
        bool RepairAttempted);
    internal const int CapabilitySchemaMaxDepth = 4;
    internal const int CapabilitySelectorMaxValues = 64;
    internal const int CapabilityDescriptionMaxCharacters = 512;
    internal const int CapabilityCatalogMaxCharacters = 256_000;

    internal sealed record CapabilityEvidenceSource(
        string Id,
        string Kind,
        string Text);

    internal sealed record CapabilityEvidenceAnchor(
        string Id,
        string SourceId,
        int Start,
        int Length,
        string Excerpt);

    internal sealed record CapabilityInventoryContractIssue(
        string Code,
        string OperationId,
        string Field,
        int? Index,
        string SourceId,
        string EvidenceId);

    internal sealed record CapabilityInventoryOperation(
        string Id,
        string Description,
        bool Required,
        string ExecutionKind,
        string ExternalEffectKind,
        string DecisionSourceOperationId = "",
        string IntentOrigin = "requested_effect",
        string DerivationSourceOperationId = "",
        bool AllowNoEffectOutcome = false,
        string OptionalityEvidence = "")
    {
        public IReadOnlyList<string> InputOperationIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> CoverageRequirements { get; init; } = Array.Empty<string>();
        public IReadOnlyList<CapabilityEvidenceAnchor> CoverageRequirementEvidence { get; init; } = Array.Empty<CapabilityEvidenceAnchor>();
        public HashSet<string> WorkflowStructureCoverageRequirementIds { get; init; } = new(StringComparer.Ordinal);
        public CapabilityEvidenceAnchor? OptionalityEvidenceAnchor { get; init; }
        public CapabilityEvidenceAnchor? NoEffectOutcomeEvidenceAnchor { get; init; }
    }
    internal sealed record CapabilityInventoryConstraint(
        string Id,
        string Description,
        bool Required,
        string EnforcementKind = "exact_denial");
    internal sealed record CapabilityInventoryIncompleteReason(string Id, string Description);
    internal sealed record CapabilityInventory(
        bool Complete,
        IReadOnlyList<CapabilityInventoryOperation> Operations,
        IReadOnlyList<CapabilityInventoryConstraint> Constraints,
        IReadOnlyList<CapabilityInventoryIncompleteReason> IncompleteReasons,
        string ExternalWriteConfirmationPolicy = "unspecified",
        string ExternalWriteConfirmationEvidence = "")
    {
        public CapabilityEvidenceAnchor? ExternalWriteConfirmationEvidenceAnchor { get; init; }
    }

    internal sealed record CapabilityCatalogEntry(
        string Id,
        string Resolution,
        string? Server,
        string? Kind,
        string Method,
        string Description,
        IReadOnlyList<CapabilityRequestBinding> RequestBindings,
        string Card,
        IReadOnlyList<CapabilitySchemaField> RequiredInputs,
        IReadOnlyList<CapabilitySchemaField> Outputs,
        McpArtifactContract? ArtifactContract,
        McpCapabilityComposition? CompositionContract);

    internal sealed record CapabilitySchemaField(
        string Path,
        string Type,
        string Description,
        IReadOnlyList<string> EnumValues);

    internal sealed record CapabilityMatchingIssue(
        string OperationId,
        string Description,
        bool Required,
        string Status,
        string Reason,
        IReadOnlyList<string> CandidateCatalogIds)
    {
        public string ReasonCode { get; init; } = "";
        public string ValidationIssue { get; init; } = "";
        public string ReportedStatus { get; init; } = "";
        public int SelectedCatalogIdCount { get; init; }
        public int CandidateCatalogIdCount { get; init; }
        public IReadOnlyList<string> InvalidFields { get; init; } = Array.Empty<string>();
    }

    internal sealed record CapabilityMatchingShapeDiagnostic(
        string Code,
        string Reason,
        IReadOnlyList<string> InvalidFields);

    internal sealed record CapabilityOperationMatch(
        CapabilityInventoryOperation Operation,
        string Status,
        string Reason,
        IReadOnlyList<string> CatalogIds,
        IReadOnlyList<string> CandidateCatalogIds,
        string? DecisionOperationId = null,
        string? DecisionOutputPath = null,
        IReadOnlyList<string>? DecisionAllowedValues = null,
        IReadOnlyList<string>? DecisionNoEffectValues = null,
        string? DecisionContractSource = null,
        string? DecisionProducerCatalogId = null,
        string? DecisionGroundingFailureCode = null)
    {
        public string? NormalizationReasonCode { get; init; }
        public string? DecisionOutputPathNormalizationReasonCode { get; init; }
        public string ConditionalActivationMode { get; init; } = "";
    }

    internal sealed record CapabilityConstraintMatch(
        CapabilityInventoryConstraint Constraint,
        string Status,
        string Reason,
        IReadOnlyList<string> DeniedCatalogIds,
        IReadOnlyList<string> CandidateCatalogIds);

    internal sealed record CapabilityMatchingEvaluation(
        IReadOnlyList<CapabilityOperationMatch> OperationMatches,
        IReadOnlyList<CapabilityConstraintMatch> ConstraintMatches,
        IReadOnlyList<CapabilityMatchingIssue> Issues,
        bool ContractValid);

    internal sealed record CapabilityCoverageEvidence(
        string CatalogId,
        string RequirementId,
        string RequirementExcerpt,
        string CatalogExcerpt);

    internal sealed record CapabilityCoverageDiagnostic(
        string OperationId,
        string Status,
        string UnsupportedRequirementId,
        string UnsupportedRequirement,
        string SupportedWeakerBehavior,
        IReadOnlyList<string> CandidateCatalogIds,
        IReadOnlyList<CapabilityCoverageEvidence> Evidence,
        bool EvidenceQualified);

    internal sealed record CapabilityCoverageContractIssue(
        string Code,
        string OperationId,
        string Field,
        int? Index,
        string CatalogId = "",
        string RequirementId = "");

    internal sealed record CapabilityCoverageReview(
        IReadOnlyList<CapabilityCoverageDiagnostic> Diagnostics,
        bool ContractValid,
        IReadOnlyList<CapabilityCoverageContractIssue> Issues);

    internal sealed record CapabilityCoverageGapAdjudication(
        string OperationId,
        string RequirementId,
        string Classification,
        IReadOnlyList<string> StructuralFacets,
        string CatalogId,
        string CatalogExcerpt,
        bool EvidenceQualified);

    internal sealed record CapabilityCoverageGapAdjudicationReview(
        IReadOnlyList<CapabilityCoverageGapAdjudication> Adjudications,
        bool ContractValid,
        IReadOnlyList<CapabilityCoverageContractIssue> Issues);

    internal sealed record CapabilityCatalog(
        IReadOnlyList<CapabilityCatalogEntry> Entries,
        string Text)
    {

    }

    internal sealed record SelectorVariant(
        IReadOnlyList<CapabilityRequestBinding> Bindings,
        string? Description = null);
    internal const string PlatformExternalWriteConfirmationOperationDescription = "Require explicit human confirmation immediately before the first external write.";
    internal const string PlatformExternalWriteConfirmationConstraintDescription = "No external write may execute before explicit human confirmation.";
    internal const string SynthesizedEffectDecisionValue = "EFFECT";
    internal const string SynthesizedNoEffectDecisionValue = "NO_EFFECT";
    internal const string ConditionalExactlyOneActivationMode = "exactly_one";
    internal const string ConditionalAllOnValueActivationMode = "all_on_value";
    internal const string CapabilityContractCoverageEnforcementKind = "capability_contract";
    internal const string WorkflowStructureCoverageEnforcementKind = "workflow_structure";
    internal const string IntrinsicPrimitiveMissingCoverageClassification = "intrinsic_primitive_missing";
    internal const string WorkflowStructureOnlyCoverageClassification = "workflow_structure_only";
    internal const string CapabilityDecisionContractSource = "capability_output";
    internal const string StructuredDecisionContractSource = "structured_output";
    internal const string LocalDecisionContractSource = "local_decision";
    internal const string LocalDecisionStepType = "decision.evaluate";
    internal const int CapabilityInventoryRepairCandidateMaxCharacters = 128_000;
    internal const int CapabilityArtifactClosureMaxCatalogIds = 32;
    internal static readonly string[] CapabilityCoverageStructuralFacets =
    [
        "cardinality",
        "uniqueness",
        "scope_iteration",
        "ordering",
        "condition",
        "confirmation",
        "finalization",
        "failure_cancellation",
        "quality_threshold",
        "runtime_argument",
        "input_representation",
        "local_mapping"
    ];

    internal sealed record CapabilityClarificationConfig(bool Enabled, int TimeoutMs);

    internal sealed class CapabilityInventoryContractException(
        IReadOnlyList<CapabilityInventoryContractIssue> issues)
        : InvalidOperationException("Capability inventory evidence violated its deterministic contract.")
    {
        public IReadOnlyList<CapabilityInventoryContractIssue> Issues { get; } = issues;
    }

    internal sealed record CapabilityRequestBinding(string Path, JsonNode? Value);
    internal sealed record CapabilityArtifactRequirement(CapabilitySchemaField Field, string Kind);
    internal sealed record SharedWriteOccurrence(string OperationId, string CatalogId, bool IsOwnedSource);
    internal sealed record ArtifactMaterializerOccurrence(
        string OperationId,
        bool IsOwnedSource,
        IReadOnlySet<string> RequiredArtifactKinds);
    internal sealed record ArtifactClosureSearchResult(
        IReadOnlyList<IReadOnlyList<string>> Solutions,
        IReadOnlyList<string> CandidateCatalogIds,
        bool SawCycle,
        bool HitLimit);
    internal sealed record ConditionalDecisionGrounding(
        string OperationId,
        string CatalogId,
        string OutputPath,
        IReadOnlyList<string> AllowedValues,
        IReadOnlyList<string> NoEffectValues,
        string ContractSource);

    internal sealed record CapabilityAlternative(
        string Server,
        string Kind,
        string Method,
        IReadOnlyList<CapabilityRequestBinding> RequestBindings);

    internal sealed record CapabilityRequirement(
        string Id,
        string Description,
        bool Required,
        IReadOnlyList<CapabilityAlternative> Alternatives);

    internal sealed record CapabilityConstraint(
        string Id,
        string Description,
        bool Required,
        IReadOnlyList<CapabilityAlternative> DeniedAlternatives);

    internal sealed record ResolvedCapability(
        string Id,
        string Description,
        bool Required,
        string Resolution,
        string? Server,
        string? Kind,
        string? Method,
        IReadOnlyList<CapabilityRequestBinding> RequestBindings,
        string? OperationId = null,
        string? CatalogId = null,
        string MatchStatus = "matched",
        string? ExecutionKind = null,
        string? ExternalEffectKind = null,
        McpCapabilityActivation? Activation = null,
        string? CapabilityDescription = null,
        IReadOnlyList<string>? OperationIds = null)
    {
        public IReadOnlyList<string> InputOperationIds { get; init; } = Array.Empty<string>();
    }

    internal sealed record CapabilityPreflightResult(
        IReadOnlyList<McpServerDiscovery> DiscoveredServers,
        IReadOnlyList<ResolvedCapability> Capabilities,
        IReadOnlyList<CapabilityConstraint> Constraints)
    {
        public string EffectiveExternalWriteConfirmationPolicy { get; init; } = "unspecified";
        public string ExternalWriteConfirmationPolicySource { get; init; } = "none";

        public IReadOnlyList<ResolvedCapability> RequiredMcpCapabilities => Capabilities
            .Where(static capability => capability.Required
                                        && string.Equals(capability.Resolution, "mcp", StringComparison.Ordinal)
                                        && !string.IsNullOrWhiteSpace(capability.Server)
                                        && !string.IsNullOrWhiteSpace(capability.Kind)
                                        && !string.IsNullOrWhiteSpace(capability.Method))
            .ToArray();

        public IReadOnlyList<ResolvedCapability> RequiredNativeCapabilities => Capabilities
            .Where(static capability => capability.Required
                                        && string.Equals(capability.Resolution, "native", StringComparison.Ordinal)
                                        && !string.IsNullOrWhiteSpace(capability.Method))
            .ToArray();

        public IReadOnlyList<ResolvedCapability> RequiredLocalOperations => Capabilities
            .Where(static capability => capability.Required
                                        && string.Equals(capability.Resolution, "local", StringComparison.Ordinal))
            .ToArray();
    }

    internal sealed record ConditionalSwitchEvaluation(
        bool IsValid,
        string ValidationIssue,
        string RepairScope,
        string Message,
        string Workflow,
        string SwitchId,
        int ContainedGroupCallCount,
        int ValidationProgress);

    internal sealed record ConditionalSwitchCandidate(
        ConditionalSwitchEvaluation Evaluation,
        IReadOnlyList<StepDef> Calls);

    internal sealed record PlannedArtifactProducer(
        string Workflow,
        string StepId,
        string Kind,
        string Pointer,
        string? Encoding = null);

    internal sealed record ArtifactResolution(
        bool Proven,
        IReadOnlySet<PlannedArtifactProducer> Producers,
        bool UsesCallerInput)
    {
        public static ArtifactResolution Unproven { get; } = new(
            false,
            new HashSet<PlannedArtifactProducer>(),
            false);
    }
    // ── MCP discovery data ──────────────────────────────────────────────

    /// <summary>
    /// Holds the result of discovering tools and prompts from one MCP server.
    /// </summary>
    internal sealed class McpServerDiscovery
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public int? CallTimeoutSeconds { get; init; }
        public IReadOnlyList<McpToolInfo> Tools { get; init; } = Array.Empty<McpToolInfo>();
        public IReadOnlyList<McpPromptInfo> Prompts { get; init; } = Array.Empty<McpPromptInfo>();
        /// <summary>True when the server was reachable and listing succeeded.</summary>
        public bool Discovered { get; init; }
    }
    internal static readonly ConditionalWeakTable<object, PlannerStructuredOutputEvidence> PlannerStructuredOutputEvidenceByEngine = new();

    internal sealed class PlannerStructuredOutputEvidence
    {
        private readonly HashSet<string> _targets = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public bool Contains(string key)
        {
            lock (_gate)
                return _targets.Contains(key);
        }

        public void Add(string key)
        {
            lock (_gate)
                _targets.Add(key);
        }
    }
    internal const string TypedConfirmationMatchingGuidance = "A declared human-interaction decision is boolean permission, so its conditional_mode must be all_on_value. Permission does not choose an effect's business result. Keep runtime-dependent enum arguments unbound by selecting the appropriate whole-tool or partial-selector entry; compute their values from the declared business-result dependencies during construction. Never select mutually exclusive result variants as if confirmation chose between them, or execute all those alternatives together.";

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(PhysicalCandidateSelection))]
    [JsonSerializable(typeof(CapabilityInventory))]
    [JsonSerializable(typeof(List<McpServerDiscovery>))]
    [JsonSerializable(typeof(CapabilityMatchingEvaluation))]
    [JsonSerializable(typeof(CapabilityPreflightResult))]
    internal partial class TypedContractJsonContext : JsonSerializerContext;
    internal const int DefaultMcpDiscoveryTimeoutSeconds = 30;
    internal const int MinMcpDiscoveryTimeoutSeconds = 1;
    internal const int MaxMcpDiscoveryTimeoutSeconds = 300;
    internal const int McpDiscoveryMaxAttempts = 3;
    internal const int McpDiscoveryRetryBaseDelayMilliseconds = 500;
}
