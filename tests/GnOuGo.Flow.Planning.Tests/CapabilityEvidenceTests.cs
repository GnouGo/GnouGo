using System.Reflection;
using System.Collections;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityEvidenceTests
{

    [Theory]
    [InlineData("Release the created resource after execution.")]
    [InlineData("Nettoyer la ressource créée après l’exécution.")]
    [InlineData("実行後に作成したリソースを解放する。")]
    public void TypedInventoryPreservesLifecycleEvidenceWithoutEnglishKeywordFiltering(string intent)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var sources = typeof(CapabilityInventoryEvidence).GetMethod("BuildCapabilityEvidenceSources", flags)!.Invoke(null, [intent, ""]);
        var json = new JsonObject
        {
            ["complete"] = true,
            ["incomplete_reasons"] = new JsonArray(),
            ["constraints"] = new JsonArray(),
            ["operations"] = new JsonArray(new JsonObject
            {
                ["id"] = "release",
                ["description"] = "Release created resource",
                ["required"] = true,
                ["execution_kind"] = "external_effect",
                ["external_effect_kind"] = "lifecycle",
                ["intent_origin"] = "requested_effect",
                ["derivation_source_operation_id"] = "",
                ["coverage_requirements"] = new JsonArray(new JsonObject { ["source_id"] = "user_request", ["excerpt"] = intent, ["enforcement_kind"] = "capability_contract" })
            })
        };
        var inventory = typeof(CapabilityInventoryValidation).GetMethod("ParseCapabilityInventory", flags)!.Invoke(null, [json, sources]);
        var filtered = typeof(CapabilityInventoryValidation).GetMethod("RemovePlannerBoundaryArtifacts", flags)!.Invoke(null, [inventory, sources])!;
        Assert.Single(((System.Collections.IEnumerable)filtered.GetType().GetProperty("Operations")!.GetValue(filtered)!).Cast<object>());
    }

    [Fact]
    public void CapabilityInventorySchema_RequiresProviderNeutralClassifications()
    {
        var method = typeof(CapabilityInventoryContext).GetMethod("BuildCapabilityInventorySchema",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var schema = Assert.IsType<JsonObject>(method!.Invoke(null, null));
        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        var requiredProperties = Assert.IsType<JsonArray>(schema["required"]);
        var operationProperties = Assert.IsType<JsonObject>(properties["operations"]!["items"]!["properties"]);
        var requiredOperationProperties = Assert.IsType<JsonArray>(properties["operations"]!["items"]!["required"]);
        var constraintProperties = Assert.IsType<JsonObject>(properties["constraints"]!["items"]!["properties"]);

        Assert.Equal(
            ["required", "forbidden", "unspecified"],
            properties["external_write_confirmation_policy"]!["enum"]!.AsArray()
                .Select(static item => item!.GetValue<string>()));
        Assert.Contains(requiredProperties, static item => item?.GetValue<string>() == "external_write_confirmation_evidence");
        Assert.Equal("object", properties["external_write_confirmation_evidence"]!["type"]!.GetValue<string>());
        Assert.NotNull(operationProperties["input_operation_ids"]);
        Assert.Contains(requiredOperationProperties, static item => item?.GetValue<string>() == "input_operation_ids");
        Assert.NotNull(operationProperties["optionality_evidence"]);
        Assert.Equal("object", operationProperties["optionality_evidence"]!["type"]!.GetValue<string>());
        Assert.Contains(requiredOperationProperties, static item => item?.GetValue<string>() == "optionality_evidence");
        var coverageItemProperties = Assert.IsType<JsonObject>(
            operationProperties["coverage_requirements"]!["items"]!["properties"]);
        Assert.NotNull(coverageItemProperties["source_id"]);
        Assert.NotNull(coverageItemProperties["excerpt"]);
        Assert.Equal(
            ["capability_contract", "workflow_structure"],
            coverageItemProperties["enforcement_kind"]!["enum"]!.AsArray()
                .Select(static item => item!.GetValue<string>()));
        Assert.NotNull(operationProperties["decision_source_operation_id"]);
        Assert.NotNull(operationProperties["no_effect_outcome_evidence"]);
        Assert.Contains(requiredOperationProperties, static item =>
            item?.GetValue<string>() == "no_effect_outcome_evidence");
        Assert.Equal(
            ["exact_denial", "workflow_policy"],
            constraintProperties["enforcement_kind"]!["enum"]!.AsArray().Select(static item => item!.GetValue<string>()));
    }

    [Fact]
    public void CapabilityMatchingIds_DeduplicateRepeatedCatalogIdentity()
    {
        var method = typeof(CapabilityMatchRecovery).GetMethod("ReadMatchingIds",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] arguments = [new JsonArray("cap-one", "cap-one"), 8, null];

        var result = Assert.IsAssignableFrom<IReadOnlyList<string>>(method!.Invoke(null, arguments));

        Assert.True(Assert.IsType<bool>(arguments[2]));
        Assert.Equal(["cap-one"], result);
    }

    [Fact]
    public void CapabilityCoverageReview_AcceptsOnlyEvidenceGroundedIncompleteMatch()
    {
        var executorType = typeof(CapabilityCoverage);
        var method = typeof(CapabilityCoverageAssessment).GetMethod("ParseCapabilityCoverageReview",
            BindingFlags.Static | BindingFlags.NonPublic);
        var operationType = typeof(CapabilityContracts).GetNestedType("CapabilityInventoryOperation", BindingFlags.NonPublic);
        var evidenceType = typeof(CapabilityContracts).GetNestedType("CapabilityEvidenceAnchor", BindingFlags.NonPublic);
        var matchType = typeof(CapabilityContracts).GetNestedType("CapabilityOperationMatch", BindingFlags.NonPublic);
        var catalogType = typeof(CapabilityContracts).GetNestedType("CapabilityCatalog", BindingFlags.NonPublic);
        var entryType = typeof(CapabilityContracts).GetNestedType("CapabilityCatalogEntry", BindingFlags.NonPublic);
        var bindingType = typeof(CapabilityContracts).GetNestedType("CapabilityRequestBinding", BindingFlags.NonPublic);
        var fieldType = typeof(CapabilityContracts).GetNestedType("CapabilitySchemaField", BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(operationType);
        Assert.NotNull(evidenceType);
        Assert.NotNull(matchType);
        Assert.NotNull(catalogType);
        Assert.NotNull(entryType);
        Assert.NotNull(bindingType);
        Assert.NotNull(fieldType);

        const string requirement = "create or update one unique external record";
        const string card = "Adds one new record. Updating an existing record is not documented.";
        var operation = Activator.CreateInstance(operationType!,
        [
            "publish_summary", "Publish the requested summary.", true, "external_effect", "write",
            string.Empty, "requested_effect", string.Empty, false, string.Empty
        ])!;
        operationType!.GetProperty("CoverageRequirements")!.SetValue(operation, new[] { requirement });
        var requirementEvidence = Activator.CreateInstance(evidenceType!,
            ["requirement-1", "user_request", 0, requirement.Length, requirement])!;
        var requirementEvidenceArray = Array.CreateInstance(evidenceType!, 1);
        requirementEvidenceArray.SetValue(requirementEvidence, 0);
        operationType.GetProperty("CoverageRequirementEvidence")!.SetValue(operation, requirementEvidenceArray);

        var match = Activator.CreateInstance(matchType!,
        [
            operation, "matched", "One catalog entry was selected.", new[] { "cap-create" }, Array.Empty<string>(),
            null, null, null, null, null, null, null
        ])!;
        var matches = Array.CreateInstance(matchType!, 1);
        matches.SetValue(match, 0);

        var entry = Activator.CreateInstance(entryType!,
        [
            "cap-create", "mcp", "neutral", "tool", "add_record", "Adds one new record.",
            Array.CreateInstance(bindingType!, 0), card,
            Array.CreateInstance(fieldType!, 0), Array.CreateInstance(fieldType!, 0), null, null
        ])!;
        var entries = Array.CreateInstance(entryType!, 1);
        entries.SetValue(entry, 0);
        var catalog = Activator.CreateInstance(catalogType!, [entries, card])!;
        var response = JsonNode.Parse($$"""
            {
              "diagnostics": [
                {
                  "operation_id": "publish_summary",
                  "status": "incomplete",
                  "unsupported_requirement_id": "requirement-1",
                  "supported_weaker_behavior": "Adds one new record.",
                  "candidate_catalog_ids": ["cap-create"],
                  "evidence": [
                    {
                      "catalog_id": "cap-create",
                      "requirement_id": "requirement-1",
                      "catalog_excerpt": "Adds one new record."
                    }
                  ]
                }
              ]
            }
            """)!.AsObject();

        var review = method!.Invoke(null, [response, catalog, matches])!;
        var contractValid = Assert.IsType<bool>(review.GetType().GetProperty("ContractValid")!.GetValue(review));
        var diagnostics = Assert.IsAssignableFrom<IEnumerable>(
            review.GetType().GetProperty("Diagnostics")!.GetValue(review)).Cast<object>().ToArray();

        Assert.True(contractValid);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("incomplete", diagnostic.GetType().GetProperty("Status")!.GetValue(diagnostic));
        Assert.True(Assert.IsType<bool>(diagnostic.GetType().GetProperty("EvidenceQualified")!.GetValue(diagnostic)));

        response["diagnostics"]![0]!["supported_weaker_behavior"] = "Adds one\r\nnew record.";
        response["diagnostics"]![0]!["evidence"]![0]!["catalog_excerpt"] = "Adds one\r\nnew record.";
        var normalizedReview = method.Invoke(null, [response, catalog, matches])!;
        Assert.True(Assert.IsType<bool>(
            normalizedReview.GetType().GetProperty("ContractValid")!.GetValue(normalizedReview)));

        response["diagnostics"]![0]!["supported_weaker_behavior"] = "adds one new record.";
        response["diagnostics"]![0]!["evidence"]![0]!["catalog_excerpt"] = "adds one new record.";
        var caseDriftReview = method.Invoke(null, [response, catalog, matches])!;
        Assert.False(Assert.IsType<bool>(
            caseDriftReview.GetType().GetProperty("ContractValid")!.GetValue(caseDriftReview)));

        response["diagnostics"]![0]!["evidence"]![0]!["catalog_excerpt"] = "Invented unsupported excerpt.";
        var invalidReview = method.Invoke(null, [response, catalog, matches])!;
        Assert.False(Assert.IsType<bool>(
            invalidReview.GetType().GetProperty("ContractValid")!.GetValue(invalidReview)));
    }

    [Fact]
    public void ConditionalDecisionGrounding_AssignsDistinctStableFieldsWhenOneStructuredProducerOwnsMultipleDecisions()
    {
        var executorType = typeof(CapabilityDecisionGrounding);
        var method = executorType.GetMethod(
            "CanonicalizeSharedStructuredDecisionOutputPaths",
            BindingFlags.Static | BindingFlags.NonPublic);
        var operationType = typeof(CapabilityContracts).GetNestedType("CapabilityInventoryOperation", BindingFlags.NonPublic);
        var matchType = typeof(CapabilityContracts).GetNestedType("CapabilityOperationMatch", BindingFlags.NonPublic);
        var constraintMatchType = typeof(CapabilityContracts).GetNestedType("CapabilityConstraintMatch", BindingFlags.NonPublic);
        var issueType = typeof(CapabilityContracts).GetNestedType("CapabilityMatchingIssue", BindingFlags.NonPublic);
        var evaluationType = typeof(CapabilityContracts).GetNestedType("CapabilityMatchingEvaluation", BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(operationType);
        Assert.NotNull(matchType);
        Assert.NotNull(constraintMatchType);
        Assert.NotNull(issueType);
        Assert.NotNull(evaluationType);

        object Operation(string id) => Activator.CreateInstance(operationType!,
        [
            id, "Opaque conditional effect.", true, "external_effect", "write",
            "authorization", "requested_effect", string.Empty, false, string.Empty
        ])!;

        object Match(string id, string[] allowedValues)
        {
            var match = Activator.CreateInstance(matchType!,
            [
                Operation(id), "conditional", "The locked branches are runtime-dependent.",
                new[] { $"catalog-{id}-a", $"catalog-{id}-b" }, Array.Empty<string>(),
                "semantic_root", "/json/decision", allowedValues, Array.Empty<string>(),
                "structured_output", "catalog-semantic-root", null
            ])!;
            matchType!.GetProperty("NormalizationReasonCode")!
                .SetValue(match, "conditional_decision_source_canonicalized");
            return match;
        }

        var matches = Array.CreateInstance(matchType!, 2);
        matches.SetValue(Match("effect_alpha", ["A", "B"]), 0);
        matches.SetValue(Match("effect_beta", ["EFFECT", "NO_EFFECT"]), 1);
        var evaluation = Activator.CreateInstance(evaluationType!,
        [
            matches,
            Array.CreateInstance(constraintMatchType!, 0),
            Array.CreateInstance(issueType!, 0),
            true
        ])!;

        var canonicalized = method!.Invoke(null, [evaluation])!;
        var currentMatches = Assert.IsAssignableFrom<IEnumerable>(
                evaluationType!.GetProperty("OperationMatches")!.GetValue(canonicalized))
            .Cast<object>()
            .ToArray();
        var paths = currentMatches
            .Select(match => Assert.IsType<string>(matchType!.GetProperty("DecisionOutputPath")!.GetValue(match)))
            .ToArray();

        Assert.Equal(2, paths.Distinct(StringComparer.Ordinal).Count());
        Assert.All(paths, static path => Assert.Matches("^/json/conditional_decision_[0-9a-f]{16}$", path));
        Assert.All(currentMatches, match =>
        {
            Assert.Equal(
                "conditional_decision_source_canonicalized",
                matchType!.GetProperty("NormalizationReasonCode")!.GetValue(match));
            Assert.Equal(
                "conditional_decision_output_path_canonicalized",
                matchType.GetProperty("DecisionOutputPathNormalizationReasonCode")!.GetValue(match));
        });

        var repeated = method.Invoke(null, [canonicalized])!;
        var repeatedPaths = Assert.IsAssignableFrom<IEnumerable>(
                evaluationType.GetProperty("OperationMatches")!.GetValue(repeated))
            .Cast<object>()
            .Select(match => Assert.IsType<string>(matchType!.GetProperty("DecisionOutputPath")!.GetValue(match)))
            .ToArray();
        Assert.Equal(paths, repeatedPaths);
    }

    [Fact]
    public void ArtifactRequirements_PreserveDeclaredKindInsteadOfInferringItFromPointerNames()
    {
        const string artifactKind = "neutral.record.batch";
        var consumer = new McpToolInfo
        {
            Name = "consume_records",
            ArtifactContract = new McpArtifactContractResolution(
                new McpArtifactContract(
                    1,
                    [],
                    [new McpConsumedArtifact(artifactKind, "/payloadText", true)]),
                [])
        };
        var method = typeof(CapabilityInventoryValidation).GetMethod(
            "GetRequiredArtifactRequirements",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(McpToolInfo)],
            modifiers: null);

        var requirements = Assert.IsAssignableFrom<IEnumerable>(method!.Invoke(null, [consumer]));
        var requirement = Assert.Single(requirements.Cast<object>());
        var kind = requirement.GetType().GetProperty("Kind")?.GetValue(requirement);
        var field = requirement.GetType().GetProperty("Field")?.GetValue(requirement);
        var path = field?.GetType().GetProperty("Path")?.GetValue(field);

        Assert.Equal(artifactKind, kind);
        Assert.Equal("/payloadText", path);
    }
}
