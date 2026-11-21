using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningPromptPresentationTests
{
    [Fact]
    public void RepairShowsIdenticalWitnessSnapshotsOnceAndPreservesChangedFindings()
    {
        var snapshot = new JsonObject { ["hash"] = "captured", ["bytes"] = 800, ["value"] = new string('x', 800) };
        JsonObject Finding() => new() { ["before"] = snapshot.DeepClone(), ["current"] = snapshot.DeepClone(),
            ["obligation"] = "Return the observation.", ["targets"] = new JsonArray("deliver") };
        var changed = Finding();
        changed["current"]!["value"] = new string('y', 800);
        var missing = Finding();
        missing.Remove("current");
        var context = new JsonObject { ["repair"] = new JsonObject(), ["defectProgress"] = new JsonArray(Finding(), changed, missing) };
        var prompt = "Check the cited observations.\n" + context.ToJsonString();
        var compact = PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt);
        var shown = JsonNode.Parse(compact[(compact.LastIndexOf('\n') + 1)..])!;
        var findings = shown["defectProgress"]!.AsArray();
        Assert.Equal("/defectProgress/0/current", findings[0]!["beforePresentedAt"]?.ToString());
        Assert.Null(findings[0]!["before"]);
        findings[0]!["before"] = findings[0]!["current"]!.DeepClone();
        findings[0]!.AsObject().Remove("beforePresentedAt");
        Assert.True(JsonNode.DeepEquals(context["defectProgress"], findings));
        Assert.Equal(compact, PlanningPromptPresentation.ShareDuplicateContractSchemas(compact));
    }

    [Fact]
    public void GlobalReconsiderationSharesOnlyUnchangedSnapshotsAlreadyShownInTheReview()
    {
        var action = new JsonObject { ["id"] = "measure", ["instruction"] = new string('x', 500) };
        var scope = new JsonObject { ["id"] = "/root", ["tasks"] = new JsonArray("measure"), ["outputs"] = new JsonArray() };
        JsonObject Concern(JsonObject before, JsonObject current) => new()
        {
            ["targets"] = new JsonArray(current["id"]!.DeepClone()), ["reason"] = "The observed result needs delivery.",
            ["before"] = before.DeepClone(), ["current"] = current.DeepClone()
        };
        var changed = action.DeepClone().AsObject(); changed["instruction"] = "Previous behavior";
        var concerns = new JsonArray(Concern(action, action), Concern(scope, scope), Concern(changed, action), Concern(changed, changed));
        var context = new JsonObject { ["review"] = "completed_workflow", ["actions"] = new JsonArray(action),
            ["scopes"] = new JsonArray(scope), ["priorConcerns"] = concerns };
        var prompt = "Inspect the original concern.\n" + context.ToJsonString();
        var compact = PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt);
        var shown = JsonNode.Parse(compact[(compact.LastIndexOf('\n') + 1)..])!;
        var rows = shown["priorConcerns"]!.AsArray();
        for (var i = 0; i < 2; i++)
        {
            Assert.True(JsonNode.DeepEquals(concerns[i]!["current"], PlanningSchemaReferences.Read(shown,
                rows[i]!["unchangedAt"]!.ToString())));
            Assert.Null(rows[i]!["before"]); Assert.Null(rows[i]!["current"]);
            Assert.True(JsonNode.DeepEquals(concerns[i]!["targets"], rows[i]!["targets"]));
            Assert.Equal(concerns[i]!["reason"]!.ToString(), rows[i]!["reason"]!.ToString());
        }
        Assert.True(JsonNode.DeepEquals(concerns[2], rows[2]));
        Assert.True(JsonNode.DeepEquals(concerns[3], rows[3]));
        Assert.Equal(compact, PlanningPromptPresentation.ShareDuplicateContractSchemas(compact));
    }

    [Theory]
    [InlineData("construction_draft")]
    [InlineData("replan")]
    public void FreshDraftAndCorrectionExposeTheRuntimeContextBoundary(string purpose)
    {
        var state = PlannerFixture.Session();
        state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        var before = PlanningConstruction.Hash(state.Plan);
        var request = PlanningModelCalls.PrepareRequest(state, purpose, "Original obligations.\n{}",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, null, null);
        Assert.Contains("planning context and policy IDs are not runtime facts", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Bind needed rules explicitly", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(before, PlanningConstruction.Hash(state.Plan));
        Assert.Contains("Original obligations.", request.Prompt, StringComparison.Ordinal);
        Assert.Empty(state.Construction!.Checkpoints);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1000)]
    public async Task IndependentExtractionUsesTheLimitCapturedFromItsHost(int limit)
    {
        var state = PlannerFixture.Session();
        state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        var engine = new GnOuGo.Flow.Core.Runtime.WorkflowEngine { Limits = new() { MaxLoopIterations = limit } };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state = PlannerFixture.Clone(state);
        engine.Limits.MaxLoopIterations++;
        var request = PlanningModelCalls.PrepareRequest(state, "construction_draft", "Use all the observations.\n{}",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, null, null);
        Assert.Contains($"each extraction accepts at most {limit} items per invocation", request.Prompt);
        Assert.Contains("ordered groups", request.Prompt);
        Assert.DoesNotContain($"at most {engine.Limits.MaxLoopIterations} items", request.Prompt);
        state.Catalog = new();
        var legacy = PlanningModelCalls.PrepareRequest(state, "construction_draft", "Use all the observations.\n{}",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, null, null);
        Assert.DoesNotContain("each extraction accepts at most", legacy.Prompt);
    }

    [Fact]
    public void RepeatedImportedSchemaShapesFactorWithoutChangingCardinalityOrLiteralData()
    {
        var record = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            { ["href"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = new string('x', 2000) } },
            ["required"] = new JsonArray("href"), ["additionalProperties"] = false };
        var records = new JsonObject { ["type"] = "array", ["items"] = record, ["minItems"] = 0 };
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            { ["first"] = records, ["second"] = records.DeepClone() },
            ["required"] = new JsonArray("first", "second"), ["additionalProperties"] = false,
            ["default"] = new JsonObject { ["first"] = records.DeepClone(), ["second"] = records.DeepClone() } };
        var context = new JsonObject { ["interfaces"] = new JsonArray(new JsonObject
            { ["value"] = new JsonObject { ["kind"] = "output", ["source"] = "accepted" }, ["schema"] = schema }) };
        var prompt = "Keep all original obligations.\n" + context.ToJsonString();
        var compact = PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt);
        Assert.True(compact.Length < prompt.Length - 500);
        var shown = JsonNode.Parse(compact[(compact.LastIndexOf('\n') + 1)..])!["interfaces"]![0]!["schema"]!.AsObject();
        Assert.NotNull(shown["$defs"]);
        Assert.True(JsonNode.DeepEquals(schema["default"], shown["default"]));
        var expanded = PlanningSchemaReferences.ExpandLocalReferences(shown); expanded.Remove("$defs");
        Assert.True(JsonNode.DeepEquals(schema, expanded));
        foreach (var json in new[] { "{\"first\":[],\"second\":[]}", "{\"first\":[{\"href\":null}],\"second\":[]}",
            "{\"first\":[{\"href\":\"same\"},{\"href\":\"same\"}],\"second\":[{\"href\":null}]}" })
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(JsonNode.Parse(json), shown));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(JsonNode.Parse("{\"first\":[{}],\"second\":[]}"), shown));
        Assert.Equal(compact, PlanningPromptPresentation.ShareDuplicateContractSchemas(compact));
        Assert.Equal("Keep all original obligations.\n" + context.ToJsonString(), prompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InspectedDiscriminatedContractSharesRepeatedBranchesWithinItsSchemaRoot(bool selected)
    {
        var field = new JsonObject { ["type"] = "string", ["description"] = new string('x', 600), ["pattern"] = "^[a-z]+$" };
        var alternatives = new JsonArray();
        foreach (var name in new[] { "read", "write", "list" })
            alternatives.Add(new JsonObject { ["properties"] = new JsonObject {
                ["operation"] = new JsonObject { ["const"] = name }, ["path"] = field.DeepClone() },
                ["required"] = new JsonArray("operation", "path") });
        var schema = new JsonObject { ["type"] = "object", ["oneOf"] = alternatives };
        var contract = new JsonObject { ["id"] = "tool", ["version"] = "v1", ["hash"] = new string('a', 64),
            ["inputSchema"] = schema, ["inputDirectory"] = "schema-page:tool@v1:/input:0" };
        var context = selected ? new JsonObject { ["operations"] = new JsonArray(contract) } :
            new JsonObject { ["evidence"] = new JsonObject { ["contract:tool@v1"] = contract } };
        var original = context.ToJsonString();
        var prompt = PlanningPromptPresentation.ShareDuplicateContractSchemas("Inspect.\n" + original);
        var shown = JsonNode.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..])!;
        var view = selected ? shown["operations"]![0]! : shown["evidence"]!["contract:tool@v1"]!;
        Assert.NotNull(view["inputSchema"]!["$defs"]);
        var expanded = PlanningSchemaReferences.ExpandLocalReferences(view["inputSchema"]!.AsObject());
        expanded.Remove("$defs");
        Assert.True(JsonNode.DeepEquals(schema, expanded));
        Assert.Equal(contract["inputDirectory"]!.ToString(), view["inputDirectory"]!.ToString());
        Assert.Empty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""{"operation":"read","path":"valid"}"""), view["inputSchema"]!.AsObject()));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(JsonNode.Parse("""{"operation":"other","path":"valid"}"""), view["inputSchema"]!.AsObject()));
        Assert.Equal(original, context.ToJsonString());
        Assert.Equal(prompt, PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt));
    }

    private static JsonObject Context()
    {
        var schema = new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = new string('x', 2000),
            ["pattern"] = "^https://", ["default"] = null, ["allOf"] = new JsonArray(new JsonObject { ["maxLength"] = 20000 }) };
        return new() { ["operations"] = new JsonArray(new JsonObject { ["id"] = "operation", ["version"] = "exact-version", ["hash"] = new string('a', 64),
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["url"] = schema.DeepClone() }, ["required"] = new JsonArray("url") } }),
            ["evidence"] = new JsonObject { ["schema:operation@exact-version:/input/properties/url"] = new JsonObject { ["id"] = "operation", ["version"] = "exact-version", ["hash"] = new string('a', 64),
                ["pointer"] = "/input/properties/url", ["schema"] = schema, ["ancestors"] = new JsonArray(new JsonObject { ["pointer"] = "/input", ["constraints"] = new JsonObject { ["required"] = new JsonArray("url", "otherRequired") } }),
                ["referenceClosure"] = new JsonObject { ["$defs/retained"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 } } } },
            ["requirements"] = new JsonArray("Complete original requirement é 😀"), ["diagnostics"] = new JsonArray(new JsonObject { ["code"] = "RETAINED_DIAGNOSTIC", ["message"] = "Exact missing contract constraint" }) };
    }

    [Fact]
    public void DiscoveryUnionBranchesShareExactAncestorsThroughIssuedEvidenceAddresses()
    {
        var evidence = new JsonObject();
        var constraints = new JsonArray(new JsonObject { ["pointer"] = "/output", ["constraints"] = new JsonObject
            { ["oneOf"] = new JsonArray(new JsonObject { ["description"] = new string('x', 11_000) }) } });
        for (var index = 0; index < 9; index++)
        {
            var pointer = "/output/oneOf/" + index;
            var ancestors = constraints.DeepClone();
            if (index == 8) ancestors[0]!["constraints"]!["oneOf"]![0]!["description"] = "A different constraint";
            var id = index == 7 ? "other" : "read";
            evidence["schema:" + id + "@v1:" + pointer] = new JsonObject { ["id"] = id, ["version"] = "v1",
                ["hash"] = new string('a', 64), ["pointer"] = pointer, ["schema"] = new JsonObject { ["const"] = index },
                ["ancestors"] = ancestors, ["referenceClosure"] = new JsonObject() };
        }
        var prompt = "Inspect the original branches.\n" + new JsonObject { ["evidence"] = evidence }.ToJsonString();
        var request = PlanningModelCalls.PrepareRequest(PlannerFixture.Session(), "construction_discovery", prompt,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, null, null, out var map);
        var shown = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
        Assert.True(request.Prompt.Length < prompt.Length - 60_000);
        foreach (var (address, node) in shown["evidence"]!.AsObject())
        {
            var row = node!.AsObject();
            var expected = evidence[map!.Addresses[address]]!;
            var actual = row["ancestors"] ?? PlanningSchemaReferences.Read(shown, row["ancestorsPresentedAt"]!.ToString());
            Assert.Equal(expected["ancestors"]!.ToJsonString(), actual!.ToJsonString());
            Assert.Equal(expected["schema"]!.ToJsonString(), row["schema"]!.ToJsonString());
            if (expected["id"]!.ToString() == "other" || expected["pointer"]!.ToString().EndsWith("/8", StringComparison.Ordinal))
                Assert.Null(row["ancestorsPresentedAt"]);
        }
    }

    [Fact]
    public void DraftEvidenceReusesTheSelectedOperationsExactAncestorArray()
    {
        var ancestors = new JsonArray(new JsonObject { ["pointer"] = "/output", ["constraints"] = new JsonObject
            { ["oneOf"] = new JsonArray(new JsonObject { ["type"] = "null" }, new JsonObject { ["description"] = new string('x', 2400) }) } });
        var operation = new JsonObject { ["id"] = "inspect", ["version"] = "v1", ["hash"] = new string('a', 64),
            ["selectedOutputs"] = new JsonArray(new JsonObject { ["pointer"] = "/output/oneOf/1" }),
            ["selectedOutputsSharedSchemaMetadata"] = new JsonObject { ["ancestors"] = ancestors.DeepClone() } };
        var evidence = new JsonObject();
        foreach (var variant in new[] { "matching", "id", "version", "hash", "constraints" })
        {
            var row = new JsonObject { ["id"] = operation["id"]!.DeepClone(), ["version"] = operation["version"]!.DeepClone(),
                ["hash"] = operation["hash"]!.DeepClone(), ["pointer"] = "/output/oneOf/1", ["schema"] = new JsonObject { ["type"] = "integer" },
                ["ancestors"] = ancestors.DeepClone(), ["referenceClosure"] = new JsonObject { ["#/$defs/example"] = true } };
            if (variant == "constraints") row["ancestors"]![0]!["constraints"]!["additionalProperties"] = false;
            else if (variant != "matching") row[variant] = "other";
            evidence["schema:" + variant] = row;
        }
        var prompt = "Use the selected contract.\n" + new JsonObject { ["operations"] = new JsonArray(operation), ["evidence"] = evidence }.ToJsonString();
        var request = PlanningModelCalls.PrepareRequest(PlannerFixture.Session(), "construction_draft", prompt,
            new JsonObject { ["type"] = "object" }, null, null, out var aliases);
        var shown = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        foreach (var (alias, row) in shown["evidence"]!.AsObject())
        {
            var address = aliases!.Addresses[alias];
            if (address == "schema:matching")
            {
                Assert.Null(row!["ancestors"]);
                Assert.Equal("/operations/0/selectedOutputsSharedSchemaMetadata/ancestors", row["ancestorsPresentedAt"]!.ToString());
                Assert.True(JsonNode.DeepEquals(ancestors, PlanningSchemaReferences.Read(shown, row["ancestorsPresentedAt"]!.ToString())));
            }
            else Assert.Equal(evidence[address]!["ancestors"]!.ToJsonString(), row!["ancestors"]!.ToJsonString());
            Assert.True(JsonNode.DeepEquals(evidence[address]!["referenceClosure"], row["referenceClosure"]));
        }
    }

    [Fact]
    public void DiscoveredBranchUsesItsExactOriginalSchemaInsideTheDisplayedAncestor()
    {
        var schema = new JsonObject { ["type"] = "string", ["description"] = new string('x', 2000) };
        var row = new JsonObject { ["id"] = "read", ["version"] = "v1", ["hash"] = new string('a', 64),
            ["pointer"] = "/output/oneOf/1", ["schema"] = schema.DeepClone(),
            ["ancestors"] = new JsonArray(new JsonObject { ["pointer"] = "/output", ["constraints"] = new JsonObject
                { ["oneOf"] = new JsonArray(new JsonObject { ["type"] = "null" }, schema.DeepClone()) } }) };
        var other = row.DeepClone(); other["schema"]!["description"] = "Different original constraint.";
        var context = new JsonObject { ["evidence"] = new JsonObject { ["schema:read@v1:/output/oneOf/1"] = row,
            ["schema:read@v1:/output/oneOf/0"] = other } };
        var request = PlanningModelCalls.PrepareRequest(PlannerFixture.Session(), "construction_discovery",
            "Inspect the returned branches.\n" + context.ToJsonString(), new JsonObject { ["type"] = "object" }, null, null);
        var shown = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
        var evidence = shown["evidence"]!.AsObject().Select(p => p.Value!).ToArray();
        Assert.Null(evidence[0]["schema"]);
        Assert.Equal(schema.ToJsonString(), PlanningSchemaReferences.Read(shown, evidence[0]["schemaPresentedAt"]!.ToString())!.ToJsonString());
        Assert.Equal(other["schema"]!.ToJsonString(), evidence[1]["schema"]!.ToJsonString());
    }

    [Fact]
    public void SelectedResultBranchesAndDescendantsReuseTheirExactCommonAncestorPrefix()
    {
        var branch = new JsonObject { ["type"] = "object", ["description"] = new string('x', 1400),
            ["properties"] = new JsonObject { ["count"] = new JsonObject { ["type"] = "integer" } } };
        var root = new JsonObject { ["pointer"] = "/output", ["constraints"] = new JsonObject
            { ["oneOf"] = new JsonArray(branch.DeepClone(), new JsonObject { ["type"] = "string" }) } };
        var inner = new JsonObject { ["pointer"] = "/output/oneOf/0", ["constraints"] = new JsonObject { ["required"] = new JsonArray("count") } };
        var rows = new JsonArray(new JsonObject { ["pointer"] = "/output/oneOf/0", ["schema"] = branch.DeepClone(),
            ["ancestors"] = new JsonArray(root.DeepClone()) },
            new JsonObject { ["pointer"] = "/output/oneOf/0/properties/count", ["schema"] = new JsonObject { ["type"] = "integer" },
                ["ancestors"] = new JsonArray(root.DeepClone(), inner.DeepClone()) });
        var closure = new JsonObject { ["#/$defs/example"] = new JsonObject { ["type"] = "string" } };
        var operation = new JsonObject { ["id"] = "read", ["version"] = "v1", ["hash"] = new string('a', 64),
            ["selectedOutputs"] = rows, ["selectedOutputsSharedSchemaMetadata"] = new JsonObject { ["referenceClosure"] = closure } };
        var context = new JsonObject { ["operations"] = new JsonArray(operation) };
        var prompt = "Use the exact selected branches.\n" + context.ToJsonString();
        var result = PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt);
        var shown = JsonNode.Parse(result[(result.LastIndexOf('\n') + 1)..])!;
        var selected = shown["operations"]![0]!["selectedOutputs"]!;
        var metadata = shown["operations"]![0]!["selectedOutputsSharedSchemaMetadata"]!;
        var prefix = Assert.IsType<JsonArray>(metadata["ancestorPrefix"]);
        Assert.True(JsonNode.DeepEquals(root, Assert.Single(prefix)));
        Assert.True(JsonNode.DeepEquals(closure, metadata["referenceClosure"]));
        for (var i = 0; i < rows.Count; i++)
        {
            var restored = prefix.Concat(selected[i]!["ancestors"]!.AsArray()).ToArray();
            Assert.Equal(rows[i]!["ancestors"]!.AsArray().Select(x => x!.ToJsonString()), restored.Select(x => x!.ToJsonString()));
        }
        Assert.True(JsonNode.DeepEquals(branch, PlanningSchemaReferences.Read(shown, selected[0]!["schemaPresentedAt"]!.ToString())));
        Assert.Equal(result, PlanningPromptPresentation.ShareDuplicateContractSchemas(result));
        rows[1]!["version"] = "v2";
        var mixed = "Keep original owners.\n" + context.ToJsonString();
        Assert.Equal(mixed, PlanningPromptPresentation.ShareDuplicateContractSchemas(mixed));
    }

    [Fact]
    public void DraftReusesExactSelectedBranchesAlreadyInsideTheirSharedAncestor()
    {
        var branch = new JsonObject { ["type"] = "string", ["description"] = new string('x', 1800) };
        var different = new JsonObject { ["type"] = "string", ["description"] = new string('y', 1800) };
        var closure = new JsonObject { ["#/$defs/value"] = new JsonObject { ["type"] = "string" } };
        var operation = new JsonObject
        {
            ["id"] = "read", ["version"] = "v1", ["hash"] = new string('a', 64),
            ["selectedOutputs"] = new JsonArray(
                new JsonObject { ["pointer"] = "/output/oneOf/0", ["schema"] = branch.DeepClone() },
                new JsonObject { ["pointer"] = "/output/oneOf/1", ["schema"] = different.DeepClone() },
                new JsonObject { ["id"] = "another", ["pointer"] = "/output/oneOf/1", ["schema"] = branch.DeepClone() }),
            ["selectedOutputsSharedSchemaMetadata"] = new JsonObject
            {
                ["ancestors"] = new JsonArray(new JsonObject { ["pointer"] = "/output",
                    ["constraints"] = new JsonObject { ["oneOf"] = new JsonArray(branch.DeepClone(), branch.DeepClone()) } }),
                ["referenceClosure"] = closure.DeepClone()
            }
        };
        var context = new JsonObject { ["operations"] = new JsonArray(operation) };
        var prompt = "Draft the selected operation.\n" + context.ToJsonString();
        var request = PlanningModelCalls.PrepareRequest(PlannerFixture.Session(), "construction_draft", prompt,
            new JsonObject { ["type"] = "object" }, null, null);
        var shown = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        var selected = shown["operations"]![0]!["selectedOutputs"]!;
        Assert.Null(selected[0]!["schema"]);
        Assert.Equal("/output/oneOf/0", selected[0]!["pointer"]!.ToString());
        Assert.Equal(branch.ToJsonString(), PlanningSchemaReferences.Read(shown, selected[0]!["schemaPresentedAt"]!.ToString())!.ToJsonString());
        Assert.Equal(different.ToJsonString(), selected[1]!["schema"]!.ToJsonString());
        Assert.Equal(branch.ToJsonString(), selected[2]!["schema"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(closure, shown["operations"]![0]!["selectedOutputsSharedSchemaMetadata"]!["referenceClosure"]));
        Assert.True(shown.ToJsonString().Length < context.ToJsonString().Length - 1000);
    }

    [Fact]
    public void ImportedArgumentsReuseTheirExactRequiredSiblingSchemaAtItsOriginalCoordinate()
    {
        var schema = new JsonObject { ["type"] = "string", ["description"] = new string('x', 1600) };
        var different = new JsonObject { ["type"] = "number", ["description"] = new string('x', 1600) };
        var siblings = new JsonObject { ["kind/~"] = schema.DeepClone(), ["flag"] = different.DeepClone() };
        var ancestor = new JsonObject { ["pointer"] = "/input", ["constraints"] = new JsonObject
            { ["required"] = new JsonArray("kind/~", "flag") }, ["requiredSiblings"] = siblings };
        var selected = new JsonArray(
            new JsonObject { ["pointer"] = "/input/properties/kind~1~0", ["schema"] = schema.DeepClone() },
            new JsonObject { ["pointer"] = "/input/properties/flag", ["schema"] = new JsonObject { ["type"] = "boolean" } },
            new JsonObject { ["pointer"] = "/input/properties/data", ["schema"] = new JsonObject { ["type"] = "string" },
                ["ancestors"] = new JsonArray(ancestor) });
        var contract = new JsonObject { ["id"] = "operation", ["version"] = "v1", ["hash"] = new string('a', 64),
            ["selectedInputs"] = selected };
        var root = new JsonObject { ["interfaces"] = new JsonArray(new JsonObject
            { ["producers"] = new JsonArray(new JsonObject { ["contract"] = contract }) }) };
        var prompt = "Use the recorded producer.\n" + root.ToJsonString();
        var presented = PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt);
        var shown = JsonNode.Parse(presented[(presented.LastIndexOf('\n') + 1)..])!;
        var actual = shown["interfaces"]![0]!["producers"]![0]!["contract"]!["selectedInputs"]![2]!["ancestors"]![0]!;
        Assert.Null(actual["requiredSiblings"]!["kind/~"]);
        Assert.True(JsonNode.DeepEquals(schema, PlanningSchemaReferences.Read(shown, actual["requiredSiblingsPresentedAt"]!["kind/~"]!.ToString())));
        Assert.True(JsonNode.DeepEquals(different, actual["requiredSiblings"]!["flag"]));
        Assert.True(JsonNode.DeepEquals(ancestor["constraints"], actual["constraints"]));
        Assert.Equal(presented, PlanningPromptPresentation.ShareDuplicateContractSchemas(presented));
        Assert.True(presented.Length < prompt.Length - 900);

        selected[0]!["version"] = "v2";
        var conflicting = "Use the recorded producer.\n" + root.ToJsonString();
        Assert.Equal(conflicting, PlanningPromptPresentation.ShareDuplicateContractSchemas(conflicting));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("different_source")]
    [InlineData("different_scope")]
    [InlineData("narrower")]
    [InlineData("relative_reference")]
    public void DiscoveryCanReadAnImportedResultTypeFromItsExactObjectMember(string variant)
    {
        var property = new JsonObject { ["type"] = "string", ["description"] = new string('a', 600) };
        var child = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["first"] = property, ["second"] = property.DeepClone() } };
        if (variant == "relative_reference") child["$id"] = "local.json";
        var parent = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["a/b"] = child.DeepClone() } };
        if (variant == "narrower") child["additionalProperties"] = false;
        JsonObject Binding(string id) => new() { ["kind"] = "output", ["source"] = id, ["port"] = "out" };
        JsonObject Row(string id, JsonObject schema, string scope, JsonArray outputs) => new()
        {
            ["value"] = Binding(id), ["schema"] = schema,
            ["producers"] = new JsonArray(new JsonObject { ["id"] = id, ["scope"] = scope, ["phase"] = "tasks",
                ["task"] = new JsonObject { ["id"] = id, ["kind"] = "value", ["outputs"] = outputs } })
        };
        var member = new JsonObject { ["name"] = "a/b", ["value"] = Binding(variant == "different_source" ? "another" : "source") };
        var wrapper = Row("wrapper", parent, "/root", new JsonArray(new JsonObject { ["name"] = "out",
            ["value"] = new JsonObject { ["kind"] = "object", ["members"] = new JsonArray(member) } }));
        var source = Row("source", child, variant == "different_scope" ? "/root/body" : "/root", []);
        var context = new JsonObject { ["interfaces"] = new JsonArray(wrapper, source) };
        var prompt = "Bind the selected results.\n" + context.ToJsonString();
        var request = PlanningModelCalls.PrepareRequest(PlannerFixture.Session(), "construction_discovery", prompt, new JsonObject { ["type"] = "object" }, null, null);
        var shown = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        var row = shown["interfaces"]![1]!;
        Assert.True(JsonNode.DeepEquals(source["value"], row["value"]));
        Assert.True(JsonNode.DeepEquals(source["producers"], row["producers"]));
        if (variant != "same")
        {
            Assert.NotNull(row["schema"]);
            Assert.Null(row["composedFieldAt"]);
            return;
        }
        Assert.Null(row["schema"]);
        Assert.Equal("/interfaces/0/schema/properties/a~1b", row["composedFieldAt"]!.ToString());
        var root = shown["interfaces"]![0]!["schema"]!.AsObject();
        var field = PlanningSchemaReferences.Read(shown, row["composedFieldAt"]!.ToString())!.AsObject();
        Assert.True(JsonNode.DeepEquals(child, PlanningSchemaReferences.ExpandLocalReferences(root, field)));
        Assert.Equal(request.Prompt, PlanningPromptPresentation.ShareDuplicateContractSchemas(request.Prompt));
    }

    [Fact]
    public void DiscoveryShowsTheTypeOfTwoIndependentResultsFromTheSameContractOnce()
    {
        var schema = new JsonObject { ["type"] = "string", ["description"] = new string('a', 1200) };
        var rows = new JsonArray();
        JsonObject Row(string result, string version, string? port)
        {
            var producer = new JsonObject { ["id"] = result, ["scope"] = "/root", ["phase"] = "tasks",
                ["task"] = new JsonObject { ["id"] = result, ["objective"] = "Produce " + result },
                ["contract"] = new JsonObject { ["id"] = "read", ["version"] = version, ["hash"] = new string('c', 64) } };
            return new JsonObject { ["value"] = new JsonObject { ["kind"] = "output", ["source"] = result, ["port"] = port },
                ["schema"] = schema.DeepClone(), ["producers"] = new JsonArray(producer) };
        }
        rows.Add(Row("read_first", "v1", null));
        rows.Add(Row("read_second", "v1", null));
        rows.Add(Row("other_version", "v2", null));
        rows.Add(Row("other_port", "v1", "records"));
        var narrowed = Row("narrower", "v1", null);
        narrowed["schema"]!["maxLength"] = 64;
        rows.Add(narrowed);
        var original = new JsonObject { ["interfaces"] = rows };
        var prompt = "Inspect these results.\n" + original.ToJsonString();
        var request = PlanningModelCalls.PrepareRequest(PlannerFixture.Session(), "construction_discovery", prompt,
            new JsonObject { ["type"] = "object" }, null, null);
        var shown = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        var interfaces = shown["interfaces"]!.AsArray();
        Assert.Null(interfaces[1]!["schema"]);
        var pointer = interfaces[1]!["schemaPresentedAt"]!.ToString();
        Assert.Equal("/interfaces/0/schema", pointer);
        Assert.Equal(schema.ToJsonString(), PlanningSchemaReferences.Read(shown, pointer)!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(rows[1]!["value"], interfaces[1]!["value"]));
        Assert.True(JsonNode.DeepEquals(rows[1]!["producers"], interfaces[1]!["producers"]));
        foreach (var index in new[] { 0, 2, 3, 4 })
            Assert.True(JsonNode.DeepEquals(rows[index]!["schema"], interfaces[index]!["schema"]));
    }

    [Theory]
    [InlineData("construction_draft")]
    [InlineData("replan")]
    public void FreshAuthoringSeparatesBoundedInterpretationFromLosslessPayloadTransfer(string phase)
    {
        var state = PlannerFixture.Session();
        state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        var request = PlanningModelCalls.PrepareRequest(state, phase, "Use the observed records.\n{}",
            new JsonObject { ["type"] = "object" }, null, null);
        Assert.Contains("An interpretation must fit in one bounded model response", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("runtime applies the mapping to every original item", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("one pass over the collection", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("original producer", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("typed value bindings", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("opaque extraction can call the model", request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedSchemaRetainsExactSourceCoordinatesAncestorsClosuresAndDiagnostics()
    {
        var original = Context(); var frozen = original.ToJsonString();
        var prompt = "Original phase instructions.\n" + frozen;
        var compact = PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt);
        Assert.True(compact.Length < prompt.Length - 1000);
        var context = JsonNode.Parse(compact[(compact.LastIndexOf('\n') + 1)..])!.AsObject();
        var row = context["evidence"]!.AsObject().Single().Value!;
        Assert.Null(row["schema"]);
        Assert.Equal("/operations/0/inputSchema/properties/url", row["schemaPresentedAt"]!.ToString());
        row["schema"] = PlanningSchemaReferences.Read(context, row["schemaPresentedAt"]!.ToString())!.DeepClone(); row.AsObject().Remove("schemaPresentedAt");
        context.Remove("schemaPresentation");
        Assert.True(JsonNode.DeepEquals(original, context));
        Assert.Equal(frozen, original.ToJsonString());
        Assert.Equal(compact, PlanningPromptPresentation.ShareDuplicateContractSchemas(compact));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("pointer")]
    [InlineData("schema")]
    public void DifferentOwnerVersionCoordinateOrConstraintCannotShare(string defect)
    {
        var context = Context(); var row = context["evidence"]!.AsObject().Single().Value!;
        if (defect == "schema") row["schema"]!["pattern"] = "^different://";
        else row[defect] = "different";
        var prompt = "Exact instructions.\n" + context.ToJsonString();
        Assert.Equal(prompt, PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt));
    }

    [Fact]
    public void SelectedOutputSharingKeepsRequiredSiblingsAndOriginalReferenceClosure()
    {
        var context = Context(); var row = context["evidence"]!.AsObject().Single().Value!;
        row["pointer"] = "/output/properties/records";
        row["schema"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/retained" }, ["minItems"] = 1 };
        context["operations"]![0]!["selectedOutputs"] = new JsonArray(new JsonObject { ["pointer"] = row["pointer"]!.DeepClone(), ["schema"] = row["schema"]!.DeepClone() });
        var before = context.DeepClone(); var prompt = "Exact selected output.\n" + context.ToJsonString();
        var compact = PlanningPromptPresentation.ShareDuplicateContractSchemas(prompt);
        var after = JsonNode.Parse(compact[(compact.LastIndexOf('\n') + 1)..])!;
        var evidence = after["evidence"]!.AsObject().Single().Value!;
        Assert.Equal("/operations/0/selectedOutputs/0/schema", evidence["schemaPresentedAt"]!.ToString());
        Assert.True(JsonNode.DeepEquals(before["evidence"]!.AsObject().Single().Value!["ancestors"], evidence["ancestors"]));
        Assert.True(JsonNode.DeepEquals(before["evidence"]!.AsObject().Single().Value!["referenceClosure"], evidence["referenceClosure"]));
        Assert.True(JsonNode.DeepEquals(row["schema"], PlanningSchemaReferences.Read(after, evidence["schemaPresentedAt"]!.ToString())));
    }

    [Fact]
    public void NewIssuanceBindsSharedPresentationWhileSavedRequestAndRecoveryIdentityStayExact()
    {
        var state = PlannerFixture.Session(); var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        var request = PlanningModelCalls.PrepareRequest(state, "construction_draft", "Keep originals.\n" + Context().ToJsonString(), schema, null, null, out var map);
        Assert.Contains("schemaPresentedAt", request.Prompt, StringComparison.Ordinal);
        var call = new PlanningModelCall { Id = request.ClientRequestId!, Purpose = "construction_draft", Request = request, EvidenceAddresses = map };
        PlanningModelCalls.ValidateEvidenceAddresses(call);
        var frozen = System.Text.Json.JsonSerializer.Serialize(call, PlanningJsonContext.Default.PlanningModelCall);
        var restored = System.Text.Json.JsonSerializer.Deserialize(frozen, PlanningJsonContext.Default.PlanningModelCall)!;
        PlanningModelCalls.ValidateEvidenceAddresses(restored);
        Assert.Equal(frozen, System.Text.Json.JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningModelCall));
        restored.Request.Prompt += "modified";
        Assert.Throws<PlanningConflictException>(() => PlanningModelCalls.ValidateEvidenceAddresses(restored));
    }
}
