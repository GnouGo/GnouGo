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

using static GnOuGo.Flow.Planning.Capabilities.CapabilityContracts;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverage;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityCoverageContext;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryEvidence;
using static GnOuGo.Flow.Planning.Capabilities.CapabilityInventoryValidation;

namespace GnOuGo.Flow.Planning.Capabilities;

internal static class CapabilityInventoryContext
{

    internal static JsonObject BuildCapabilityClarificationContext(
        CapabilityInventory inventory, CapabilityMatchingEvaluation? evaluation, CapabilityCatalog? catalog)
    {
        if (evaluation == null)
        {
            return new JsonObject
            {
                ["phase"] = "capability_inventory",
                ["planning_outcome"] = "clarification_required",
                ["issues"] = new JsonArray(inventory.IncompleteReasons.Select(static reason => (JsonNode)new JsonObject
                {
                    ["id"] = SanitizeCapabilityInferenceDiagnostic(reason.Id, 160),
                    ["description"] = SanitizeCapabilityInferenceDiagnostic(reason.Description, 1_000)
                }).ToArray())
            };
        }

        var entries = catalog!.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        return new JsonObject
        {
            ["phase"] = "capability_matching",
            ["planning_outcome"] = "clarification_required",
            ["issues"] = new JsonArray(evaluation.Issues
                .Where(static issue => issue.Required && issue.Status == "ambiguous")
                .Select(issue => (JsonNode)new JsonObject
                {
                    ["id"] = SanitizeCapabilityInferenceDiagnostic(issue.OperationId, 160),
                    ["description"] = SanitizeCapabilityInferenceDiagnostic(issue.Description, 1_000),
                    ["reason"] = SanitizeCapabilityInferenceDiagnostic(issue.Reason, 1_000),
                    ["candidate_capabilities"] = new JsonArray(issue.CandidateCatalogIds
                        .Where(entries.ContainsKey)
                        .Select(id => (JsonNode)BuildCapabilityCandidateCard(entries[id]))
                        .ToArray())
                }).ToArray())
        };
    }

    internal static string BuildCapabilityInventoryPromptWithEvidence(
        IReadOnlyList<CapabilityEvidenceSource> evidenceSources) => $$"""
        You are a domain-neutral workflow runtime analyst. Return only the requested structured JSON.

        Pass 1 has no tool catalog. Enumerate every distinct positive operation that the generated workflow itself must perform at runtime to satisfy the task. Include required external reads, external writes, resource creation, cleanup, recovery, and user interactions. Do not guess implementation names.

        Separately enumerate constraints: prohibitions, safety rules, ordering requirements, and invariants. A prohibition is never a positive operation.

        Inventory only intentions expressed in the user task. The runtime-boundary bullets below are analyst instructions, not user intentions: never copy, paraphrase, or restate them as operations or constraints.

        Completeness means that every runtime intention expressed by the user is represented as an operation or constraint. It does not mean that an implementation, tool, selector, or available capability is already known. Capability availability and exact matching belong exclusively to Pass 2. Unknown implementation details, tool availability, selector choice, or capability support must never make this inventory incomplete. Preserve an explicitly requested effect as a domain-neutral operation even when its implementation is unknown.

        Classify every operation by execution_kind:
        - external_effect: an external read, write, AI execution, or resource lifecycle effect that needs a discovered capability;
        - human_interaction: an explicit user confirmation or additional user input;
        - local_processing: parsing, validation, filtering, transformation, aggregation, control flow, or orchestration performed inside the workflow. Local processing is a planning obligation and must not be forced onto an arbitrary tool or native step here.

        Also classify external_effect_kind:
        - read: obtains external state without changing it;
        - write: creates, changes, publishes, sends, submits, or deletes state outside the workflow;
        - execute: invokes AI or another computation without itself publishing or mutating external state;
        - lifecycle: creates or cleans an isolated runtime resource owned by this workflow;
        - none: required for human_interaction and local_processing.
        Use write for every operation whose intended result changes an external system, even when a later confirmation is expected.

        Every evidence field is an object with source_id and excerpt. Use empty source_id and excerpt values when a singular evidence field is not applicable. For every external_effect or human_interaction operation, set coverage_requirements to one or more distinct evidence objects whose excerpt is copied from exactly one supplied evidence source. Classify each coverage requirement with enforcement_kind=capability_contract only when the selected capability card itself must document the intrinsic observable primitive (for example, reading, materializing, invoking, publishing, or deleting state). Use the shortest exact excerpt that states that primitive. Use enforcement_kind=workflow_structure for cardinality, uniqueness, per-item or complete-scope iteration, ordering, conditions, confirmation, finalization, failure/cancellation handling, quality thresholds, runtime argument values or instructions, input identifiers or locator syntax, and locally derivable parameter mapping; these guarantees are enforced by generated workflow structure rather than by requiring a generic parameterized capability card to repeat the task-specific rule or consume the caller's original input representation. Split one source sentence into separate overlapping exact excerpts when it contains both an intrinsic primitive and structural context. When they cannot be split without changing meaning, classify the combined excerpt as workflow_structure. A resource identifier or locator alone never proves a capability-contract obligation. Local-processing operations use an empty array. Do not paraphrase, change case or punctuation, combine text from different sources, or derive evidence from provider or tool knowledge.

        Set input_operation_ids to the IDs of every earlier operation whose declared runtime output this operation consumes. Use an empty array when it consumes no earlier operation output. A local validation, normalization, or projection of an external result must name that producer here. Declare only data flow stated by the task; never infer dependencies from descriptions, operation order, provider names, or likely implementations.
        Set decision_source_operation_id to the ID of the earlier operation whose declared runtime result exclusively selects this operation's outcome or selector branch. Use an empty string when the operation is unconditional. A conditional operation may point to a local validation operation, and that local operation must use input_operation_ids to declare the upstream producer whose result it validates. Do not invent a discriminator as a local parsing result and do not use an identifier or locator as evidence of a future choice. If the user requires a runtime-dependent choice but the deciding operation cannot be identified, set complete=false and request that relationship as user clarification.
        Set allow_no_effect_outcome=true only when the user's requested runtime behavior explicitly requires at least one result of that decision source to execute none of this operation's external-effect alternatives, such as abstaining, skipping publication, or performing no decision write when evidence is insufficient. Copy the shortest exact evidence for that outcome into no_effect_outcome_evidence and also include the same or an overlapping excerpt as a workflow_structure coverage requirement for this operation. Otherwise set allow_no_effect_outcome=false and use an empty no_effect_outcome_evidence object. A resource-safety rule, execution-environment restriction, availability check, permission check, or success/failure path does not create a no-effect decision for an otherwise required operation unless the supplied evidence explicitly says that operation is skipped or abstained from. Do not invent an eligibility, trust, or safety decision operation. allow_no_effect_outcome is invalid on unconditional operations. This field permits a safe non-mutating branch; it never permits omission of an independently required effect.
        `required` means that the capability or local obligation must exist in every valid generated plan; it does not mean that its runtime call is unconditional. A conditional operation remains required when any of its branches is required by the task, including when another branch is a no-effect outcome. Set required=false only for enrichment that the user or caller explicitly made optional. For required=false, set optionality_evidence to one non-empty evidence object that explicitly establishes that optionality. For required=true, use an evidence object with empty source_id and excerpt.

        Classify the user/caller policy for human confirmation immediately before external writes in external_write_confirmation_policy:
        - required: the supplied task explicitly requires that confirmation;
        - forbidden: the supplied task explicitly requires unattended execution or prohibits that confirmation;
        - unspecified: neither rule is explicit, so the platform safety default may add confirmation.
        For required or forbidden, set external_write_confirmation_evidence to one non-empty evidence object copied from exactly one supplied evidence source. For unspecified, use an evidence object with empty source_id and excerpt. This policy is about the generated workflow's external writes only; do not derive it from provider, tool, domain, or selector names.
        Represent all mutually exclusive outcomes of one runtime choice as one conditional external operation with that decision source. Never inventory one positive operation per possible branch value, because those branches are alternative implementations of one runtime effect rather than independently required effects.

        Classify every operation's intent_origin. Use requested_effect only for an independently observable runtime effect requested by the user or caller context. Use derived_failure_handling when an operation is introduced only as implementation handling for another operation's failure and is not an independently requested effect; set derivation_source_operation_id to that existing operation ID. Notifications, logging, escalation, compensation, retries, and fallback actions are not requested effects merely because the workflow must fail safely. Use an empty derivation_source_operation_id for requested_effect. Derived failure handling is not a positive capability requirement and will be removed from the locked inventory.

        Classify every constraint by enforcement_kind:
        - exact_denial: an unconditional prohibition that can safely ban exact external capabilities throughout the generated workflow;
        - workflow_policy: a conditional, ordering, cardinality, coverage, quality, confirmation, or other invariant that must be enforced by workflow structure rather than a document-wide capability denial.

        Runtime boundary rules:
        - When the task supplies only an external resource locator or identifier, separate literal local parsing from external state resolution. If a requested downstream effect requires current content, revisions, attributes, status, or other state not literally encoded in that locator, inventory one required external read that resolves the state. Never classify retrieval of external state as local parsing.
        - Preserve independently observable requested effects as distinct operations even when the same actor, AI, or external service may perform several of them. A user enumeration of preparation, execution, verification, analysis, publication, and cleanup outcomes must not be collapsed into one operation merely because a future capability could be prompted to attempt all of them.
        - Keep one operation when the task requests one atomic effect whose internal phases are not independently requested outcomes. Pass 2 may select a declared complete-operation wrapper or one prerequisite-closed composition; Pass 1 must neither decompose documented internals nor merge separate user-visible outcomes.
        - Exclude host configuration already supplied to the workflow runtime.
        - Exclude planning-time acceptance tests and synthetic validation fixtures from runtime operations. They are evidence for validating the generated workflow, not actions that the generated workflow should perform.
        - Treat declared workflow inputs supplied when execution starts as the public input contract, not as a separate human-interaction operation. Use human_interaction only when execution must pause after it starts for confirmation or additional information.
        - Exclude credentials, provider selection, secret-vault lookup, authentication, and connection setup performed internally by whichever runtime capability is selected later.
        - Exclude persistence, registration, or provisioning of the generated workflow/agent when that happens outside the generated workflow after planning.
        - Include cleanup only when the user explicitly requests cleanup as runtime behavior. Do not invent a generic cleanup operation merely because an unknown future implementation might allocate a resource; cleanup encapsulated inside a selected capability is not a separate workflow operation.
        - A resource handle, directory path, or materialization result does not contain the resource's contents. When a requested decision depends on those contents, include the necessary external observation as external_effect/read and declare its data-flow edge before the local decision. local_processing may transform supplied values; it cannot inspect files, query a service, or execute commands. Unknown runtime facts belong to observation and branching, not user-intent ambiguity.
        - When the task names one external source, inventory at most one owned resource-materialization operation for that source. Preparation, analysis, verification, and publication phases consume the same resource; they are not separate requests to materialize phase-specific copies. Inventory multiple materializations only when the user explicitly requests distinct source resources.
        - A deterministic retry, backoff, fallback, or failure-handling policy for an already inventoried external operation is local_processing or a workflow_policy and reuses the original operation's capability. However, a separately requested runtime action performed by an AI, agent, service, or tool during that fallback remains external_effect/execute. Distinguish the local rule that decides when fallback is needed from the external actor that must inspect, choose, analyze, or generate a new runtime value; do not classify the latter as local processing merely because it occurs on a fallback path.
        - A restriction whose applicability depends on a target, input value, resource instance, runtime condition, or selected branch is workflow_policy even when it uses words such as only or never. Use exact_denial only when the prohibited capability must be banned for every possible invocation throughout the workflow.
        - Mark optional enrichment required=false.
        - Set complete=true once every explicit runtime intention is represented, including conditional and optional intentions.
        - Set complete=false only when ambiguity in the user's requested runtime behavior prevents you from identifying the intended operation or constraint. When false, provide concise incomplete_reasons describing the missing user intent and what must be clarified. Do not cite tool or catalog uncertainty as a reason.
        - Return an empty incomplete_reasons array when complete=true.

        <evidence_sources>
        {{BuildCapabilityEvidenceSourcesJson(evidenceSources)}}
        </evidence_sources>
        """;

    internal static string BuildCapabilityInventoryRepairPrompt(
        IReadOnlyList<CapabilityEvidenceSource> evidenceSources, CapabilityInventory previous, JsonObject? rejectedCandidate, IReadOnlyList<CapabilityInventoryContractIssue> contractIssues) => $$"""
        You are a domain-neutral workflow runtime inventory repair analyst. Return only the requested structured JSON.

        A previous inventory was incomplete or violated the deterministic evidence contract. Repair it once by ensuring that every runtime intention expressed by the user is represented as a positive operation or a constraint and every reported contract issue is corrected.

        Completeness is about enumerating requested runtime intent only. It is not a claim that an implementation, tool, selector, credential, or available capability is known. Capability availability and exact matching happen later. Unknown implementation details, tool availability, selector choice, or capability support must never make this inventory incomplete. Represent the intended effect in domain-neutral language instead.

        Preserve the runtime boundary:
        - When the task supplies only an external resource locator or identifier, separate literal local parsing from external state resolution. If a downstream requested effect needs current content, revisions, attributes, status, or other state not literally encoded in that locator, preserve one required external read that resolves the state. Never replace that external read with local parsing.
        - Split independently observable requested effects into distinct operations even when one actor, AI, or service could attempt several. Do not collapse separate preparation, execution, verification, analysis, publication, or cleanup outcomes into one unmatchable compound operation.
        - Do not split one atomic requested effect into speculative implementation phases. Later capability metadata decides whether a complete-operation wrapper replaces internal phases.
        - Exclude host configuration already supplied to the workflow runtime.
        - Exclude planning-time acceptance tests and synthetic validation fixtures from runtime operations. They remain validation evidence outside the generated workflow.
        - Exclude credentials, provider selection, secret-vault lookup, authentication, and connection setup performed internally by a later capability.
        - Exclude persistence, registration, or provisioning performed outside the generated workflow after planning.
        - Preserve cleanup only when the user explicitly requested it as runtime behavior. Never invent generic cleanup for resources that are not part of the user's intention.
        - Preserve an explicit external observation when a decision needs resource contents. A handle, path, or materialization result is not those contents. Local processing may transform supplied values but cannot read files, query services, or execute commands. Missing runtime observations are construction defects, not missing user intent.
        - Preserve one owned materialization for one external source and let later operations consume it. Do not turn workflow phases into additional source-materialization intentions unless the user explicitly requested distinct source resources.
        - Classify deterministic retry, backoff, fallback, and failure-handling policies for an existing external operation as local_processing or workflow_policy. A separately requested fallback action performed at runtime by an AI, agent, service, or tool remains external_effect/execute. Preserve the distinction between the local rule that selects the fallback path and the external actor that inspects, chooses, analyzes, or generates a new runtime value.
        - Keep prohibitions, ordering requirements, safety rules, and invariants as constraints rather than positive operations.
        - Inventory only intentions expressed in the user task. Do not copy, paraphrase, or restate these repair or runtime-boundary instructions as operations or constraints.
        - Preserve execution_kind and external_effect_kind for every operation. External writes use external_effect/write; external reads use external_effect/read; AI or other non-mutating execution uses external_effect/execute; owned resource setup/cleanup uses external_effect/lifecycle; human and local work use none.
        - Every evidence value is an object with source_id and excerpt. Its excerpt must occur within exactly that source after Unicode NFC and whitespace normalization, while case, punctuation, accents, and word order remain exact. Never paraphrase or combine text from different sources.
        - Preserve coverage_requirements as one or more source-addressed evidence objects for every external or human operation. Preserve enforcement_kind=capability_contract only for the shortest exact excerpt that states an intrinsic primitive the selected capability card must document. Use workflow_structure for cardinality, uniqueness, per-item or complete-scope iteration, ordering, conditions, confirmation, finalization, failure/cancellation handling, quality thresholds, runtime argument values or instructions, input identifiers or locator syntax, and locally derivable parameter mapping. Split mixed sentences into separate exact excerpts when possible; otherwise classify the combined excerpt as workflow_structure. A resource identifier or locator alone never proves a capability-contract obligation. Local-processing operations use an empty array.
        - Preserve required=true for every planning obligation, including runtime-conditional operations and branches. required=false is valid only for explicitly optional enrichment and requires a non-empty optionality_evidence object. Required operations use empty source_id and excerpt values.
        - Preserve external_write_confirmation_policy and its source-addressed evidence. Use required or forbidden only when the evidence sources explicitly prove that policy; otherwise use unspecified with empty source_id and excerpt values.
        - Preserve input_operation_ids as the exact earlier-operation data-flow edges. A local validation, normalization, or projection of an external result must identify that producer. Use an empty array when no earlier output is consumed, and never reconstruct a dependency from descriptions, provider names, or operation order alone.
        - Preserve allow_no_effect_outcome=true only with exact no_effect_outcome_evidence that explicitly establishes skipping or abstention for that operation and overlaps one of its workflow_structure coverage requirements. Environment restrictions, availability, permissions, and ordinary success/failure handling do not create a no-effect branch. Otherwise use false with empty evidence. Never invent an eligibility, trust, or safety decision operation.
        - Preserve decision_source_operation_id for runtime-dependent operations. It identifies the earlier operation whose result selects the branch; use an empty string for unconditional operations. A local decision source declares its upstream producer through input_operation_ids. Preserve allow_no_effect_outcome=true only when the user explicitly requires a non-mutating outcome for that conditional operation; it must be false for unconditional operations. Merge mutually exclusive outcome-specific operations into one conditional operation instead of treating every possible branch value as an independently required effect.
        - Preserve intent_origin and derivation_source_operation_id. requested_effect requires an empty derivation source. derived_failure_handling requires the ID of the existing operation whose failure it handles and is never a substitute for a user-requested external effect.
        - Classify constraints with enforcement_kind=exact_denial only for unconditional document-wide prohibitions. Target-, input-, resource-instance-, data-, or branch-dependent restrictions and conditional, ordering, cardinality, coverage, quality, confirmation, and other structural invariants use workflow_policy.
        - Include conditional and optional runtime intentions and mark optional enrichment required=false.
        - Return complete=true and an empty incomplete_reasons array when all requested effects are represented.
        - If the user's requested runtime behavior itself remains genuinely under-specified, return complete=false and concise incomplete_reasons stating what user intent must be clarified. Never cite missing tools, catalogs, selectors, credentials, or implementation knowledge.

        <previous_inventory>
        {{BuildCapabilityInventoryJson(previous)}}
        </previous_inventory>

        <rejected_inventory_candidate>
        {{BuildRejectedCapabilityInventoryCandidate(rejectedCandidate, contractIssues)}}
        </rejected_inventory_candidate>

        <inventory_contract_issues>
        {{BuildCapabilityInventoryContractIssuesJson(contractIssues)}}
        </inventory_contract_issues>

        <evidence_sources>
        {{BuildCapabilityEvidenceSourcesJson(evidenceSources)}}
        </evidence_sources>
        """;

    internal static JsonObject BuildCapabilityInventorySchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["complete"] = new JsonObject { ["type"] = "boolean" },
            ["external_write_confirmation_policy"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("required", "forbidden", "unspecified")
            },
            ["external_write_confirmation_evidence"] = BuildCapabilityEvidenceReferenceSchema(),
            ["incomplete_reasons"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["description"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("id", "description"),
                    ["additionalProperties"] = false
                }
            },
            ["operations"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["description"] = new JsonObject { ["type"] = "string" },
                        ["required"] = new JsonObject { ["type"] = "boolean" },
                        ["execution_kind"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("external_effect", "human_interaction", "local_processing")
                        },
                        ["external_effect_kind"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("read", "write", "execute", "lifecycle", "none")
                        },
                        ["input_operation_ids"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" }
                        },
                        ["coverage_requirements"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["maxItems"] = 8,
                            ["items"] = BuildCapabilityCoverageEvidenceReferenceSchema()
                        },
                        ["optionality_evidence"] = BuildCapabilityEvidenceReferenceSchema(),
                        ["decision_source_operation_id"] = new JsonObject { ["type"] = "string" },
                        ["allow_no_effect_outcome"] = new JsonObject { ["type"] = "boolean" },
                        ["no_effect_outcome_evidence"] = BuildCapabilityEvidenceReferenceSchema(),
                        ["intent_origin"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("requested_effect", "derived_failure_handling")
                        },
                        ["derivation_source_operation_id"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("id", "description", "required", "execution_kind", "external_effect_kind", "input_operation_ids", "coverage_requirements", "optionality_evidence", "decision_source_operation_id", "allow_no_effect_outcome", "no_effect_outcome_evidence", "intent_origin", "derivation_source_operation_id"),
                    ["additionalProperties"] = false
                }
            },
            ["constraints"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["description"] = new JsonObject { ["type"] = "string" },
                        ["required"] = new JsonObject { ["type"] = "boolean" },
                        ["enforcement_kind"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("exact_denial", "workflow_policy")
                        }
                    },
                    ["required"] = new JsonArray("id", "description", "required", "enforcement_kind"),
                    ["additionalProperties"] = false
                }
            }
        },
        ["required"] = new JsonArray("complete", "external_write_confirmation_policy", "external_write_confirmation_evidence", "incomplete_reasons", "operations", "constraints"),
        ["additionalProperties"] = false
    };

    internal static string BuildCapabilityMatchingPrompt(CapabilityInventory inventory, CapabilityCatalog catalog)
    {
        var operations = new JsonArray(inventory.Operations.Select(static operation => (JsonNode)new JsonObject
        {
            ["id"] = operation.Id,
            ["description"] = operation.Description,
            ["required"] = operation.Required,
            ["execution_kind"] = operation.ExecutionKind,
            ["external_effect_kind"] = operation.ExternalEffectKind,
            ["input_operation_ids"] = new JsonArray(operation.InputOperationIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["coverage_requirements"] = new JsonArray(operation.CoverageRequirementEvidence.Select(evidence => (JsonNode)new JsonObject
            {
                ["requirement"] = evidence.Excerpt,
                ["enforcement_kind"] = operation.WorkflowStructureCoverageRequirementIds.Contains(evidence.Id)
                    ? WorkflowStructureCoverageEnforcementKind
                    : CapabilityContractCoverageEnforcementKind
            }).ToArray()),
            ["decision_source_operation_id"] = operation.DecisionSourceOperationId,
            ["allow_no_effect_outcome"] = operation.AllowNoEffectOutcome
        }).ToArray());
        var constraints = new JsonArray(inventory.Constraints.Select(static constraint => (JsonNode)new JsonObject
        {
            ["id"] = constraint.Id,
            ["description"] = constraint.Description,
            ["required"] = constraint.Required,
            ["enforcement_kind"] = constraint.EnforcementKind
        }).ToArray());
        return $$"""
            Match the validated operations to the declared capability contracts. Use only metadata, schemas and explicit intent evidence.
            {{TypedConfirmationMatchingGuidance}}
            Choose the smallest sufficient implementation. Composed entries are jointly necessary, never alternative implementations.
            A selector variant inherits its whole-tool contract; select the most specific sufficient fixed bindings.
            Select a whole tool when enum arguments are dynamic business data. A complete_operation wrapper replaces its encapsulated phases.
            A required argument needs a compatible business input, declared default/fixed selector or proven producer output.
            Ordinary scalar arguments may be parsed or derived locally from declared business inputs or reused observations.
            External artifacts require their original producer; local calculations cannot establish artifact provenance.
            Reuse a declared upstream producer instead of adding duplicate reads. Include necessary lifecycle and cleanup prerequisites.
            Conditional effects follow the locked decision source through declared dependencies. Exactly-one branches differ at one selector path.
            All-on-value executes a necessary composition in order for the effect value and nothing for the declared no-effect outcome.
            Human permission does not choose business outcomes. Opaque decision outputs require a validated structured projection before use.
            Report unavailable if these contracts cannot implement an operation. Give a concise reason without task content or hidden reasoning.

            <runtime_inventory>
            {{new JsonObject { ["operations"] = operations, ["constraints"] = constraints }.ToJsonString()}}
            </runtime_inventory>

            <capability_catalog>
            {{catalog.Text}}
            </capability_catalog>
            """;
    }
}
