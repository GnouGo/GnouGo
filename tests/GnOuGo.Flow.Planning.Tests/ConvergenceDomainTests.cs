using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConvergenceDomainTests
{
    private static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();
    internal static (PlanningSnapshot State, PlanningWorkflow Workflow, PlanningHole Hole) Input(string expected = "string")
    {
        var state = HoleSessionTests.Ready(); var workflow = state.Graph!.Workflows[0];
        workflow.Inputs = [new() { Name = "source", Required = true, Schema = new() { Type = expected } }];
        var node = workflow.Steps[0]; node.Type = "mcp.call"; node.CapabilityId = "consume";
        node.Input = Obj(("request", Obj(("argument", new() { Kind = "unresolved" }))));
        state.Preparation!.Capabilities.Add(new() { Id = "consume", StepType = "mcp.call", InputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["argument"] = new JsonObject { ["type"] = expected } }, ["required"] = new JsonArray("argument"), ["additionalProperties"] = false } });
        state.Construction.Holes.Clear();
        PlanningGraphSkeleton.Add(state, workflow, node, "/workflows/0/steps/0/input/members/0/value/members/0/value", "value", "Consume the declared source");
        state.Construction.Dataflow!.InputObligations[workflow.Key + "/" + node.Key] = ["source"];
        return (state, workflow, state.Construction.Holes.Single());
    }

    [Theory]
    [InlineData("{\"type\":\"number\"}", "{\"type\":\"integer\"}", false)]
    [InlineData("{\"type\":\"integer\"}", "{\"type\":\"number\"}", true)]
    [InlineData("{\"type\":\"number\"}", "{\"oneOf\":[{\"type\":\"number\"},{\"type\":\"integer\"}]}", false)]
    [InlineData("{\"type\":[\"string\",\"null\"]}", "{\"type\":\"string\"}", false)]
    [InlineData("{\"type\":\"number\"}", "{\"type\":\"number\",\"minimum\":0}", false)]
    [InlineData("{\"type\":\"number\",\"minimum\":1}", "{\"type\":\"number\",\"exclusiveMinimum\":0}", true)]
    [InlineData("{\"type\":\"number\",\"minimum\":0}", "{\"type\":\"number\",\"exclusiveMinimum\":0}", false)]
    [InlineData("{\"type\":\"string\",\"enum\":[\"a\",\"b\"]}", "{\"type\":\"string\",\"enum\":[\"a\"]}", false)]
    [InlineData("{\"type\":\"string\",\"enum\":[\"a\"]}", "{\"type\":\"string\",\"enum\":[\"a\",\"b\"]}", true)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\"}},\"additionalProperties\":false}", "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\"}},\"required\":[\"x\"],\"additionalProperties\":false}", false)]
    public void CompatibilityRequiresProof(string actual, string expected, bool fits) => Assert.Equal(fits, PlanningContractCompatibility.Fits(Schema(actual), Schema(expected)));

    [Fact]
    public void OnlyEligibleBindingsReachTheSchemaAndUniqueBindingResolves()
    {
        var (state, workflow, hole) = Input();
        workflow.Inputs.Add(new() { Name = "wrongType", Required = true, Schema = new() { Type = "number" } });
        workflow.Inputs.Add(new() { Name = "unrelated", Required = true, Schema = new() { Type = "string" } });
        var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
        Assert.Equal("source", Assert.Single(domain.Direct).Value.Source);
        Assert.False(domain.Literal);
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        Assert.DoesNotContain("wrongType", request.Prompt); Assert.DoesNotContain("unrelated", request.Prompt);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.Schema, true));
        var invalid = new JsonObject { ["assignments"] = new JsonObject { [hole.Id] = new JsonObject { ["kind"] = "literal", ["json"] = "fabricated" } } };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invalid, request.Schema));
        PlanningBindingResolution.Resolve(state, workflow);
        Assert.True(hole.Resolved); Assert.Equal("deterministic", hole.ResolutionOrigin);
        Assert.Empty(state.Construction.PendingCalls);
    }

    [Fact]
    public void PublicExecutionEvidenceDoesNotOfferAFabricatedLiteralOrItsExponentialSchema()
    {
        var (state, workflow, _) = Input();
        workflow.OperationIds = ["operation"];
        var node = workflow.Steps[0];
        node.Input = Obj(("request", Obj(("argument", new() { Kind = "input", Source = "source" }))));
        state.Preparation!.Capabilities.Single().OutputSchema = new() { ["type"] = "string" };
        workflow.Outputs = [new() { Name = "evidence", Schema = new() { Type = "object", Properties = Enumerable.Range(0, 10)
            .Select(i => new PlanningPort { Name = "field" + i, Required = false, Schema = new() { Type = "string" } }).ToList() }, Value = new() { Kind = "unresolved" } }];
        state.Construction.Holes.Clear();
        PlanningGraphSkeleton.Add(state, workflow, null, "/workflows/0/outputs/0/value", "value", "Observed execution evidence");
        var hole = state.Construction.Holes.Single();
        var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
        Assert.False(domain.Literal);
        Assert.NotEmpty(domain.Parameters);
        Assert.Throws<PlanningHoleUnavailableException>(() => PlanningLiteralSchemas.Create(domain.Contract));
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.Schema, true));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["assignments"] = new JsonObject
        { [hole.Id] = new JsonObject { ["kind"] = "literal", ["json"] = new JsonObject() } } }, request.Schema));
        Assert.NotNull(PlanningHoleEligibility.Validate(state, workflow, hole, new() { Kind = "compute", Text = "({})" }));
    }

    [Fact]
    public void OptionalWithoutDefaultIsUnavailableAndValidDefaultRestoresAvailability()
    {
        var (state, workflow, hole) = Input(); workflow.Inputs[0].Required = false;
        Assert.Empty(PlanningHoleEligibility.Analyze(state, workflow, hole).Direct);
        workflow.Inputs[0].Default = Str("default");
        Assert.Single(PlanningHoleEligibility.Analyze(state, workflow, hole).Direct);
        workflow.Inputs[0].Default = new() { Kind = "null" };
        Assert.Empty(PlanningHoleEligibility.Analyze(state, workflow, hole).Direct);
    }

    [Fact]
    public void SharedObligationsAccountForEstablishedArguments()
    {
        var (state, workflow, hole) = Input(); var node = workflow.Steps[0];
        workflow.Inputs.Add(new() { Name = "second", Required = true, Schema = new() { Type = "number" } });
        state.Construction.Dataflow!.InputObligations[workflow.Key + "/" + node.Key] = ["source", "second"];
        Assert.Empty(PlanningHoleEligibility.Analyze(state, workflow, hole).Direct);
        PlanningGraphValidation.Member(node.Input, "request")!.Members.Add(new("other", new() { Kind = "input", Source = "second" }));
        state.Preparation!.Capabilities.Single().InputSchema["properties"]!["other"] = new JsonObject { ["type"] = "number" };
        var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
        Assert.Equal(new[] { "input:source" }, domain.RequiredHere);
        Assert.Equal("source", Assert.Single(domain.Direct).Value.Source);
    }

    [Fact]
    public void ComputationParametersMayHaveADifferentResultTypeAndUnusedParametersProveNothing()
    {
        var (state, workflow, hole) = Input("number");
        workflow.Inputs[0].Schema = new() { Type = "array", Items = new() { Type = "string" } };
        var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
        Assert.Empty(domain.Direct); Assert.Single(domain.Parameters); Assert.False(domain.Literal);
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        var parameter = Assert.Single(request.Bindings).Key;
        var assignment = new JsonObject { ["kind"] = "compute", ["expression"] = parameter + ".length" };
        var response = new JsonObject { ["assignments"] = new JsonObject { [hole.Id] = assignment } };
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.Schema));
        assignment["bindings"] = new JsonArray(parameter);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, request.Schema));
        assignment.Remove("bindings");
        var value = PlanningHoleAssignments.Value(assignment, request.Bindings, request.ParameterScopes[hole.Id])!;
        Assert.Null(PlanningHoleEligibility.Validate(state, workflow, hole, value));
        var constant = PlanningHoleAssignments.Value(new() { ["kind"] = "compute", ["expression"] = "0" }, request.Bindings, request.ParameterScopes[hole.Id])!;
        Assert.Empty(PlanningDataflow.References(constant));
        Assert.NotNull(PlanningHoleEligibility.Validate(state, workflow, hole, constant));
    }

    [Fact]
    public void RepairContextKeepsRelevantParameterSchemasAndOmitsUnrelatedRetainedBindings()
    {
        var (state, workflow, hole) = Input("number");
        workflow.Inputs[0].Schema = new() { Type = "array", Items = new() { Type = "string" } };
        workflow.Inputs.Add(new() { Name = "unrelated", Schema = new() { Type = "string" } });
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        var parameter = Assert.Single(request.Bindings).Key;
        request.Bindings.Add("unrelated_retained", new() { Kind = "input", Source = "unrelated" });
        var context = PlanningHoleRepairContext.Create(state, new() { WorkflowKey = workflow.Key, Bindings = request.Bindings, ParameterScopes = request.ParameterScopes }, [hole]);
        Assert.Equal(parameter, Assert.Single(context["bindings"]!.AsObject()).Key);
        Assert.Equal("array", context["bindings"]![parameter]!["schema"]!["type"]!.ToString());
        Assert.DoesNotContain("unrelated", context.ToJsonString());
    }

    [Fact]
    public void EligibleScalarBindingCanBeTransformedUsingItsAlreadyIssuedIdentity()
    {
        var (state, workflow, hole) = Input("number");
        workflow.Inputs[0].Schema = new() { Type = "object", Properties = [new() { Name = "amount", Required = true, Schema = new() { Type = "number" } }] };
        var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
        var scalar = Assert.Single(domain.Direct);
        Assert.Equal(new[] { "amount" }, scalar.Value.Path);
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        var parameter = "p_" + scalar.Id[2..14];
        var assignment = new JsonObject { ["kind"] = "compute", ["expression"] = parameter + " * 2" };
        // The same scalar was already legal as a direct binding. Previously this
        // expression needed a repair solely because only the parent was a parameter.
        var value = PlanningHoleAssignments.Value(assignment, request.Bindings, request.ParameterScopes[hole.Id])!;
        PlanningComputations.Validate(value);
        Assert.Null(PlanningHoleEligibility.Validate(state, workflow, hole, value));
        Assert.Equal(new[] { "amount" }, Assert.Single(value.Members).Value.Path);
        Assert.Equal(2, domain.Parameters.Count); // Parent plus the already exposed scalar; no catalog expansion.
        var before = request.ParameterScopes[hole.Id].Where(p => p != parameter).ToList();
        Assert.Throws<InvalidOperationException>(() => PlanningHoleAssignments.Value(assignment, request.Bindings, before));
    }

    [Fact]
    public void UnquotedTextIsRejectedByTheExpressionResponseSchema()
    {
        var (state, workflow, hole) = Input();
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        var parameter = Assert.Single(request.ParameterScopes[hole.Id]);
        var text = "Process the observed value ${" + parameter + "}.";
        var assignment = new JsonObject { ["kind"] = "compute", ["expression"] = text };
        var response = new JsonObject { ["assignments"] = new JsonObject { [hole.Id] = assignment } };
        var previous = request.Schema.DeepClone().AsObject(); previous["$defs"]!["computationText"]!.AsObject().Remove("pattern");
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, previous)); // The retained live failure previously passed the response schema.
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, request.Schema));
        assignment["expression"] = "`" + text + "`";
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.Schema, true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.Schema));
        var value = PlanningHoleAssignments.Value(assignment, request.Bindings, request.ParameterScopes[hole.Id])!;
        Assert.Null(PlanningHoleEligibility.Validate(state, workflow, hole, value));
        Assert.Equal("Process the observed value verified.", new GnOuGo.Flow.Core.Expressions.ExpressionEvaluator()
            .Evaluate(value.Text!, new JsonObject { [parameter] = "verified" })!.GetValue<string>());
    }

    [Theory]
    [InlineData("JSON.stringify(p_012345abcdef)")]
    [InlineData("p_012345abcdef.length")]
    [InlineData("(() => { const n = p_012345abcdef.length; return n; })()")]
    [InlineData("const n = p_012345abcdef.length; return n;")]
    [InlineData("/* scoped computation */ Number(p_012345abcdef)")]
    [InlineData("/^[a-z]+$/.test(p_012345abcdef)")]
    [InlineData("'quoted text'")]
    [InlineData("42")]
    [InlineData("true")]
    public void ExpressionPrefixRetainsSupportedExecutableForms(string expression)
        => Assert.Empty(PlanningContractValidation.ValidateInstance(JsonValue.Create(expression), PlanningComputationScopes.ExpressionSchema()));

    [Fact]
    public void SameBranchProducerIsAvailableButSiblingsAndFailureFinalizersAreNot()
    {
        var (state, workflow, hole) = Input(); var consumer = workflow.Steps[0];
        var producer = new PlanningNode { Key = "producer", Type = "set", Input = Obj(("value", Str("ok"))), OutputSchema = new() { Type = "object", Properties = [new() { Name = "value", Required = true, Schema = new() { Type = "string" } }] } };
        workflow.Steps = [new() { Key = "branch", Type = "switch", Cases = [new("a", null, [producer, consumer])], Default = [new() { Key = "other", Type = "set" }] }];
        var same = PlanningDataflow.Index(workflow, state.Preparation!, state.Graph!, consumer.Key);
        Assert.Contains(same.Values, b => b.Value.Source == "producer" && b.Availability == "unconditional");
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, state.Preparation!, state.Graph!, "other").Values, b => b.Value.Source == "producer");
        workflow.Steps = [producer]; workflow.Finally = [consumer];
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, state.Preparation!, state.Graph!, consumer.Key).Values, b => b.Value.Source == "producer");
    }

    [Fact]
    public void ArtifactFieldsOfferOnlyTheOriginalMatchingProducer()
    {
        var (state, workflow, hole) = Input(); var consumer = workflow.Steps[0]; consumer.OperationIds = ["consume"];
        var capability = state.Preparation!.Capabilities.Single(); capability.InputOperationIds = ["produce"];
        capability.ArtifactContract = new(1, [], [new("artifact", "/argument", true)]);
        var producer = new PlanningNode { Key = "producer", Type = "mcp.call", CapabilityId = "producer", OperationIds = ["produce"], Input = Obj() };
        workflow.Steps.Insert(0, producer); hole.Path = "/workflows/0/steps/1/input/members/0/value/members/0/value";
        state.Preparation.Capabilities.Add(new() { Id = "producer", StepType = "mcp.call", OperationIds = ["produce"], OutputSchema = Schema("""{"type":"object","properties":{"id":{"type":"string"},"unrelated":{"type":"string"}},"required":["id","unrelated"],"additionalProperties":false}"""), ArtifactContract = new(1, [new("artifact", "/id", "reference")], []) });
        state.Construction.Dataflow!.InputObligations[workflow.Key + "/" + consumer.Key] = [];
        var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
        Assert.Equal(new[] { "id" }, Assert.Single(domain.Direct).Value.Path);
        Assert.Empty(domain.Parameters); Assert.False(domain.Literal);
    }

    [Fact]
    public void ArtifactOriginSurvivesInterveningChecksWithoutAdmittingAnotherMaterializer()
    {
        var (state, workflow, hole) = Input(); var consumer = workflow.Steps[0];
        state.Construction.Dataflow!.InputObligations.Clear();
        var consume = state.Preparation!.Capabilities.Single(); consume.InputOperationIds = ["check"];
        consume.ArtifactContract = new(1, [], [new("artifact", "/argument", true)]);
        consume.InputSchema["properties"]!["checked"] = new JsonObject { ["type"] = "boolean" };
        consumer.Input.Members[0].Value.Members.Add(new("checked", new() { Kind = "unresolved" }));
        var artifactSchema = Schema("""{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}""");
        state.Preparation.Capabilities.Add(new() { Id = "original", StepType = "mcp.call", OperationIds = ["materialize"], OutputSchema = artifactSchema,
            ArtifactContract = new(1, [new("artifact", "/id", "reference")], []) });
        state.Preparation.Capabilities.Add(new() { Id = "check", StepType = "mcp.call", OperationIds = ["check"], InputOperationIds = ["materialize"],
            InputSchema = Schema("""{"type":"object","properties":{"source":{"type":"string"}},"required":["source"]}"""), OutputSchema = Schema("""{"type":"boolean"}""") });
        state.Preparation.Capabilities.Add(new() { Id = "other", StepType = "mcp.call", OperationIds = ["other_materialization"], InputOperationIds = ["check"], OutputSchema = artifactSchema.DeepClone().AsObject(),
            InputSchema = Schema("""{"type":"object","properties":{"check":{"type":"boolean"}},"required":["check"]}"""),
            ArtifactContract = new(1, [new("artifact", "/id", "reference")], []) });
        workflow.Steps.InsertRange(0, [new() { Key = "original", Type = "mcp.call", CapabilityId = "original", OperationIds = ["materialize"], Input = Obj() },
            new() { Key = "check", Type = "mcp.call", CapabilityId = "check", OperationIds = ["check"], Input = Obj(("request", Obj(("source", new() { Kind = "output", Source = "original", Path = ["id"] })))) },
            new() { Key = "other", Type = "mcp.call", CapabilityId = "other", OperationIds = ["other_materialization"], Input = Obj(("request", Obj(("check", new() { Kind = "output", Source = "check" })))) }]);
        hole.Path = hole.Path.Replace("/steps/0/", "/steps/3/", StringComparison.Ordinal);
        PlanningGraphSkeleton.Add(state, workflow, consumer, "/workflows/0/steps/3/input/members/0/value/members/1/value", "value", "Verification evidence");
        var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
        Assert.Equal("original", Assert.Single(domain.Direct).Value.Source);
        Assert.Empty(domain.Parameters); Assert.False(domain.Literal);
        Assert.Equal("original", PlanningBindingResolution.Unique(state, workflow, hole)!.Value.Source);
        Assert.DoesNotContain("operation:check", DependenciesOfOriginal());
        HashSet<string> DependenciesOfOriginal() => PlanningHoleEligibility.Dependencies(state, workflow, consumer, domain.Direct[0].Value);
    }

    [Fact]
    public void AComposedOperationConsumesItsEarlierOriginalArtifactWithoutAModelChoice()
    {
        var (state, workflow, hole) = Input(); var consumer = workflow.Steps[0];
        state.Construction.Dataflow!.InputObligations.Clear(); consumer.OperationIds = ["composed"];
        var consume = state.Preparation!.Capabilities.Single(); consume.OperationIds = ["composed"];
        consume.ArtifactContract = new(1, [], [new("artifact", "/argument", true)]);
        var schema = Schema("""{"type":"object","properties":{"payload":{"type":"string"}},"required":["payload"],"additionalProperties":false}""");
        state.Preparation.Capabilities.Add(new() { Id = "produce", StepType = "mcp.call", OperationIds = ["composed"], OutputSchema = schema,
            ArtifactContract = new(1, [new("artifact", "/payload", "reference")], []) });
        var producer = new PlanningNode { Key = "producer", Type = "mcp.call", CapabilityId = "produce", OperationIds = ["composed"], Input = Obj() };
        workflow.Steps.Insert(0, producer);
        hole.Path = hole.Path.Replace("/steps/0/", "/steps/1/", StringComparison.Ordinal);
        Assert.Equal("producer", PlanningBindingResolution.Unique(state, workflow, hole)!.Value.Source);
        // The same contract after the consumer is not an available origin.
        workflow.Steps.Remove(producer); workflow.Steps.Add(producer);
        hole.Path = hole.Path.Replace("/steps/1/", "/steps/0/", StringComparison.Ordinal);
        Assert.Empty(PlanningHoleEligibility.Analyze(state, workflow, hole).Direct);
    }

    [Theory]
    [InlineData(1, "materialize", true)]
    [InlineData(2, "materialize", false)]
    [InlineData(1, "reference", false)]
    public void RequiredArtifactContractCanIdentifyOneOwnedMaterializer(int count, string mode, bool resolves)
    {
        var (state, workflow, hole) = Input(); var consumer = workflow.Steps[0];
        consumer.OperationIds = ["consume"]; workflow.OperationIds = ["consume", "make"];
        var consume = state.Preparation!.Capabilities.Single(); consume.OperationIds = ["consume"];
        consume.ArtifactContract = new(1, [], [new("artifact", "/argument", true)]);
        state.Preparation.Capabilities.Add(new() { Id = "produce", StepType = "mcp.call", OperationIds = ["make"],
            OutputSchema = Schema("""{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}"""),
            ArtifactContract = new(1, [new("artifact", "/id", mode)], []) });
        for (var i = 0; i < count; i++) workflow.Steps.Insert(i, new() { Key = "producer" + i, Type = "mcp.call", CapabilityId = "produce", OperationIds = ["make"],
            Input = Obj(("request", Obj(("source", new() { Kind = "input", Source = "source" })))) });
        hole.Path = hole.Path.Replace("/steps/0/", "/steps/" + count + "/", StringComparison.Ordinal);
        var before = JsonSerializer.Serialize(state.Preparation, PlanningJsonContext.Default.PlanningPreparation);
        var binding = PlanningBindingResolution.Unique(state, workflow, hole);
        Assert.Equal(resolves, binding is not null);
        if (resolves)
        {
            Assert.Equal("producer0", binding!.Value.Source);
            PlanningBindingResolution.Resolve(state, workflow);
            Assert.True(hole.Resolved); // Previously paused despite the unique contract-proven materialization.
            Assert.Empty(state.Construction.PendingCalls);
        }
        Assert.Equal(before, JsonSerializer.Serialize(state.Preparation, PlanningJsonContext.Default.PlanningPreparation));
    }

    [Fact]
    public void UnavailableExplicitArtifactOriginDoesNotAuthorizeAnAvailableReplacement()
    {
        var (state, workflow, hole) = Input(); var consumer = workflow.Steps[0];
        consumer.OperationIds = ["consume"]; workflow.OperationIds = ["consume", "expected", "replacement"];
        var consume = state.Preparation!.Capabilities.Single(); consume.OperationIds = ["consume"]; consume.InputOperationIds = ["expected"];
        consume.ArtifactContract = new(1, [], [new("artifact", "/argument", true)]);
        foreach (var key in new[] { "expected", "replacement" })
            state.Preparation.Capabilities.Add(new() { Id = key, StepType = "mcp.call", OperationIds = [key],
                OutputSchema = Schema("""{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}"""),
                ArtifactContract = new(1, [new("artifact", "/id", "materialize")], []) });
        workflow.Steps.Insert(0, new() { Key = "replacement", Type = "mcp.call", CapabilityId = "replacement", OperationIds = ["replacement"], Input = Obj() });
        workflow.Steps.Add(new() { Key = "expected", Type = "mcp.call", CapabilityId = "expected", OperationIds = ["expected"], Input = Obj() });
        hole.Path = hole.Path.Replace("/steps/0/", "/steps/1/", StringComparison.Ordinal);
        Assert.Empty(PlanningHoleEligibility.Analyze(state, workflow, hole).Direct);
    }

    [Fact]
    public void LiteralResponseSchemaRejectsNestedTypesAndBounds()
    {
        var contract = Schema("""{"type":"object","properties":{"amount":{"type":"number","minimum":0},"status":{"type":"string","enum":["a","b"]}},"required":["amount","status"],"additionalProperties":false}""");
        var schema = PlanningHoleRequests.Object(("json", PlanningLiteralSchemas.Create(contract)));
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Schema("""{"json":{"amount":1,"status":"a"}}"""), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Schema("""{"json":{"amount":-1,"status":"invented"}}"""), schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Schema("""{"json":{"amount":"1","status":"a"}}"""), schema));
    }

    [Fact]
    public void UnsupportedLiteralContractKeepsItsRecoveryLocation()
    {
        var error = Assert.Throws<PlanningHoleUnavailableException>(() => PlanningLiteralSchemas.Create(
            Schema("""{"type":"object","additionalProperties":true}"""), "/workflows/@main/inputs/@settings/default"));
        Assert.Equal("/workflows/@main/inputs/@settings/default", error.Location);
    }

    [Fact]
    public void CalleeInputRequirementsRemainInvocationObligationsWithoutInventingProducerProof()
    {
        var (state, workflow, hole) = Input("number");
        workflow.Inputs[0].Schema = new() { Type = "array", Items = new() { Type = "number" } };
        var capability = state.Preparation!.Capabilities.Single(); capability.InputOperationIds = ["upstream"];
        var call = new PlanningNode { Key = "invoke", Type = "workflow.call", Input = Obj(("ref", new() { Kind = "workflow", Source = workflow.Key }),
            ("args", Obj(("source", new() { Kind = "array", Items = [new() { Kind = "number", Number = 1 }] })))) };
        state.Graph!.Workflows.Add(new() { Key = "caller", Steps = [call] });
        var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
        Assert.Equal("operation:upstream", Assert.Single(domain.BoundaryObligations));
        Assert.Single(domain.Parameters); Assert.False(domain.Literal);
        Assert.DoesNotContain("operation:upstream", PlanningHoleEligibility.Dependencies(state, workflow, workflow.Steps[0], new() { Kind = "input", Source = "source" }));
        Assert.NotEmpty(PlanningWorkflowProvenance.InputFindings(state.Graph, workflow, "upstream", new HashSet<string>(["source"], StringComparer.Ordinal), state.Preparation)!);
    }

    [Fact]
    public void ExactLiteralRepairsObeyBoundsBeforeMaterialization()
    {
        var (state, workflow, hole) = Input("number");
        state.Construction.Dataflow!.InputObligations.Clear();
        state.Preparation!.Capabilities.Single().InputSchema["properties"]!["argument"]!["minimum"] = 0;
        PlanningGraphValidation.Member(workflow.Steps[0].Input, "request")!.Members[0].Value.Kind = "number";
        PlanningGraphValidation.Member(workflow.Steps[0].Input, "request")!.Members[0].Value.Number = -1;
        var scope = PlanningPatches.Scope(state.Graph!, [new("BOUND", hole.Path + "/number", "Fix bound")]);
        var request = PlanningPatches.CreateRequest(state.Graph!, state.Preparation, scope, state.Construction.Dataflow);
        var target = Assert.Single(PlanningPatches.Targets(state.Graph, scope));
        var patch = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = target.Id, ["value"] = -2 }) };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(patch, request.Schema));
        patch["patches"]![0]!["value"] = 0;
        Assert.Empty(PlanningContractValidation.ValidateInstance(patch, request.Schema));
    }

    [Fact]
    public void MultipleValidSourcesRemainChoicesAndConstantsNeedNoModel()
    {
        var (state, workflow, hole) = Input();
        workflow.Inputs[0].Schema = new() { Type = "object", Properties = [new() { Name = "a", Required = true, Schema = new() { Type = "string" } }, new() { Name = "b", Required = true, Schema = new() { Type = "string" } }] };
        Assert.Equal(2, PlanningHoleEligibility.Analyze(state, workflow, hole).Direct.Count);
        Assert.Null(PlanningBindingResolution.Unique(state, workflow, hole));
        state.Construction.Dataflow!.InputObligations.Clear();
        state.Preparation!.Capabilities.Single().InputSchema["properties"]!["argument"]!["enum"] = new JsonArray("fixed");
        PlanningBindingResolution.Resolve(state, workflow);
        Assert.True(hole.Resolved); Assert.Equal("deterministic", hole.ResolutionOrigin);
        Assert.Empty(state.Construction.PendingCalls);
    }

    [Fact]
    public void UnknownAndReferenceSiblingConstraintsCannotBecomeProof()
    {
        Assert.False(PlanningContractCompatibility.Fits(Schema("""{"type":"number"}"""), Schema("""{"$ref":"#/$defs/n","minimum":10,"$defs":{"n":{"type":"number"}}}""")));
        Assert.False(PlanningContractCompatibility.Fits(Schema("""{"type":"array","items":{"type":"string"}}"""), Schema("""{"type":"array","items":{"type":"string"},"contains":{"const":"required"}}""")));
        Assert.True(PlanningContractCompatibility.Fits(Schema("""{"type":["number","null"]}"""), Schema("""{"anyOf":[{"type":"number"},{"type":"null"}]}""")));
        Assert.False(PlanningSchemaPropagation.Established(Schema("""{"type":"object","properties":{"unknown":{}},"additionalProperties":false}""")));
    }

    [Fact]
    public void ConvergenceTelemetryIsRedactedAndKeepsIdentitiesOffMetricDimensions()
    {
        var (state, workflow, hole) = Input(); state.Request.TenantId = "tenant"; state.Request.Prompt = "PRIVATE_CONTENT";
        var before = PlanningContext.Clone(state); PlanningHoleRequests.Create(state, workflow, [hole]);
        PlanningConvergence.Expose(state, [hole], "receipt");
        PlanningConvergence.Failure(state, workflow.Key, PlanningGates.Typed, "candidate", [new("BAD", hole.Path, "PRIVATE_CONTENT")]);
        var tags = new List<KeyValuePair<string, object?>>(); var names = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == PlanningConvergenceTelemetry.MeterName) l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<int>((_, _, dimensions, _) => { foreach (var dimension in dimensions) tags.Add(dimension); });
        listener.SetMeasurementEventCallback<long>((_, _, dimensions, _) => { foreach (var dimension in dimensions) tags.Add(dimension); });
        listener.Start();
        PlanningConvergenceTelemetry.Observe(before, state, (name, data) =>
        {
            names.Add(name); Assert.Contains(data, t => t.Key == "tenant.id" && t.Value?.ToString() == "tenant");
            Assert.DoesNotContain(data, t => t.Value?.ToString()?.Contains("PRIVATE_CONTENT", StringComparison.Ordinal) == true);
        });
        Assert.Contains("planning.convergence", names); Assert.Contains("planning.hole_choices", names); Assert.Contains("planning.gate_failure", names);
        Assert.NotEmpty(tags); Assert.DoesNotContain(tags, t => t.Key is "hole" or "workflow" or "session");
    }

    [Fact]
    public void ExposureAndFailuresAreDurableAndMessageIndependent()
    {
        var (state, workflow, hole) = Input(); PlanningHoleRequests.Create(state, workflow, [hole]);
        PlanningConvergence.Expose(state, [hole], "request-one"); PlanningConvergence.Expose(state, [hole], "request-one");
        var finding = new PlanningDiagnostic("BAD", hole.CanonicalLocation, "First message");
        PlanningConvergence.Failure(state, workflow.Key, PlanningGates.Typed, "candidate", [finding]);
        state = PlanningContext.Clone(state); hole = state.Construction.Holes.Single();
        PlanningConvergence.Expose(state, [hole], "request-one");
        PlanningConvergence.Failure(state, workflow.Key, PlanningGates.Typed, "candidate", [finding with { Message = "Different wording" }]);
        Assert.Equal(1, state.Construction.Workflows.Single().ModelHoleExposures);
        Assert.Equal(1, state.GateProgress.Single().Failures);
        PlanningConvergence.Expose(state, [hole], "request-two");
        Assert.Equal(1, state.Construction.Workflows.Single().ModelHoles);
        Assert.Equal(2, state.Construction.Workflows.Single().ModelHoleExposures);
        var events = new List<string>();
        PlanningConvergenceTelemetry.Observe(state, state, (name, _) => events.Add(name));
        Assert.Empty(events);
    }
}
