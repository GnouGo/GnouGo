using System.Reflection;
using System.Collections;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityEvidenceTests
{
    [Theory]
    [InlineData("Inspect the record.", "Inspect the records.")]
    [InlineData("Vérifier le résultat.", "Vérifier les résultats.")]
    [InlineData("Verify the record.", "verify the record.")]
    public void RepeatedTranscriptionChangesNeverBecomeExactIntentEvidence(string source, string altered)
    {
        var sources = CapabilityInventoryEvidence.BuildCapabilityEvidenceSources(source, "").ToDictionary(s => s.Id, StringComparer.Ordinal);
        var initial = new JsonObject { ["source_id"] = "user_request", ["excerpt"] = altered };
        var initialIssues = new List<CapabilityContracts.CapabilityInventoryContractIssue>();
        var repairedIssues = new List<CapabilityContracts.CapabilityInventoryContractIssue>();
        Assert.Null(CapabilityInventoryEvidence.ResolveCapabilityEvidenceReference(initial, sources, "effect", "coverage_requirements", 0, false, initialIssues));
        Assert.Null(CapabilityInventoryEvidence.ResolveCapabilityEvidenceReference(initial.DeepClone(), sources, "effect", "coverage_requirements", 0, false, repairedIssues));
        Assert.Equal("excerpt_not_found", Assert.Single(initialIssues).Code);
        Assert.Equal(initialIssues[0], Assert.Single(repairedIssues));
        Assert.Equal("effect", initialIssues[0].OperationId);
        Assert.Equal("coverage_requirements", initialIssues[0].Field);
        Assert.Equal(0, initialIssues[0].Index);

        var exact = new JsonObject { ["source_id"] = "user_request", ["excerpt"] = source };
        var validIssues = new List<CapabilityContracts.CapabilityInventoryContractIssue>();
        var anchor = CapabilityInventoryEvidence.ResolveCapabilityEvidenceReference(exact, sources, "effect", "coverage_requirements", 0, false, validIssues);
        Assert.Empty(validIssues);
        Assert.NotNull(anchor);
        Assert.Equal(source, anchor.Excerpt);
    }

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
