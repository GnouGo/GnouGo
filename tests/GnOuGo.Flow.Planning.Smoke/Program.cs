using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

// Exercise the native certificate-chain implementation after publish, including
// the .NET Apple crypto archive normalized by the Darwin publish boundary.
using (var key = RSA.Create(2048))
{
    var request = new CertificateRequest("CN=GnOuGo native smoke", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.DisableCertificateDownloads = true;
    if (chain.Build(certificate)) throw new InvalidOperationException("An untrusted certificate was accepted.");
    chain.ChainPolicy.CustomTrustStore.Add(certificate);
    if (!chain.Build(certificate)) throw new InvalidOperationException("Native certificate-chain validation failed.");
}

var collectionExpression = ArtifactCollectionExpression.Build("pages", ["source", "response", "records"]);
if (GeneratedFunctionDocumentation.Validate("function convert(value) { return String(value); }").Single().Code != "FUNCTION_JSDOC_MISSING" ||
    GeneratedFunctionDocumentation.Validate("/** @param {*} value Input\n * @returns {string} Text */ function convert(value) { return String(value); }").Count != 0)
    throw new InvalidOperationException("Published function documentation validation failed.");
if (!ArtifactCollectionExpression.TryRead(collectionExpression, out var collectionLoop, out _) || collectionLoop != "pages")
    throw new InvalidOperationException("Published collection expression parsing failed.");
var collectionValue = new ExpressionEvaluator().Evaluate(collectionExpression,
    JsonNode.Parse("""{"steps":{"pages":{"results":[{"source":{"response":{"records":"[{\"id\":9007199254740993}]"}}},{"source":{"response":{"records":"[2]"}}}]}}}"""));
if (collectionValue?.GetValue<string>() != """[{"id":9007199254740993},2]""")
    throw new InvalidOperationException("Published collection changed original artifact records.");

var graph = new PlanningGraph
{
    Summary = "Published typed compiler smoke",
    Workflows = [new()
    {
        Key = "main",
        Steps = [new() { Key = "value", Type = "set", Input = new() { Kind = "object", Members = [new("message", new() { Kind = "string", Text = "ready" })] } }],
        Outputs = [new() { Name = "message", Schema = new() { Type = "string" }, Value = new() { Kind = "output", Source = "value", Path = ["message"] } }]
    }]
};
var preparation = new PlanningPreparation { PolicyScopeVersion = 2, AllowedStepTypes = ["set"] };
var state = new PlanningSnapshot
{
    Graph = graph,
    Preparation = preparation,
    Request = new() { TenantId = "smoke", Prompt = "Compile the typed graph" },
    Construction = new() { Dataflow = new() { Bindings = [new("binding", "main", new() { Kind = "output", Source = "value", Path = ["message"] }, new JsonObject { ["type"] = "string" }, "unconditional")] } }
};
var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
var policyState = new PlanningSnapshot { ScopedPolicies = [new() { Id = "policy", Rule = "require_confirmation", ScopeKind = "operation", Target = "effect",
    TargetOperationIds = ["effect"], PermissionOperationId = "permission", ClauseReference = "clause", EvidenceFingerprint = "evidence", Status = "resolved", Applicability = "unless_explicit" }] };
policyState.Obligations.Add(new("governor", ["clause"], "workflow", "confirmation_forbidden", true)
{ Grounding = new(PlanningSourceAuthority.ConstraintsOnly, PlanningSourceSemanticRole.PolicyConstraint, "clause", null, "proof"),
    Disposition = "rejection_condition", AdjudicationFingerprint = "adjudicated", PolicyIds = ["policy"] });
policyState.ScopedPolicies[0].GoverningObligationIds = ["governor"]; policyState.ScopedPolicies[0].GoverningReferences = ["clause"];
policyState.DeclarationAssignments.Add(new("candidate", "distinct_input", null, "name", "main", "optional", null) { DeclarationReference = "clause", PresenceReference = "presence_clause" });
policyState.Declarations.Add(new("declaration", "input", "main", "name", null, false, "literal", ["candidate"], [], ["modifier"], ["clause"], "declaration_proof"));
policyState.DeclarationFingerprint = "adjudication_proof";
var restoredPolicies = JsonSerializer.Deserialize(JsonSerializer.Serialize(policyState, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
if (restoredPolicies.DeclarationFingerprint != "adjudication_proof" || restoredPolicies.Declarations.Single().DefaultReference != "literal" ||
    restoredPolicies.DeclarationAssignments.Single().Presence != "optional" || restoredPolicies.DeclarationAssignments[0].PresenceReference != "presence_clause") throw new InvalidOperationException("Published declaration serialization failed.");
if (restoredPolicies.ScopedPolicies.Single().PermissionOperationId != "permission" || restoredPolicies.ScopedPolicies[0].TargetOperationIds.Single() != "effect" ||
    restoredPolicies.ScopedPolicies[0].Applicability != "unless_explicit") throw new InvalidOperationException("Published scoped policy serialization failed.");
if (restoredPolicies.Obligations.Single().Grounding?.Authority != PlanningSourceAuthority.ConstraintsOnly ||
    restoredPolicies.Obligations[0].Disposition != "rejection_condition" || restoredPolicies.Obligations[0].AdjudicationFingerprint != "adjudicated" ||
    restoredPolicies.ScopedPolicies[0].GoverningObligationIds.Single() != "governor") throw new InvalidOperationException("Published source grounding serialization failed.");
if (restored.Construction.Dataflow?.Bindings.Count != 1) throw new InvalidOperationException("Published dataflow persistence failed.");
var choiceState = new PlanningSnapshot
{
    BusinessDecisions = [new() { Id = "decision", Status = "resolved", ResolutionOrigin = "intent", SelectedChoiceId = "choice", ValuePresence = "omitted",
        EvidenceReferences = ["reference"], Constraints = [new("require", "choice", "reference", "intent", "omitted")],
        Alternatives = [new() { Id = "choice", EvidenceReference = "reference", Label = "Retain the result" }] }],
    Outcome = new PlanningNeedUserClarification(new("decision", ["reference"], new JsonObject { ["type"] = "string" }, [])
    { Question = "Choose the result", DependencyFingerprint = "scope", Choices = [new("choice", "Retain the result", true, "Declared preference", ["reference"])] })
};
var restoredChoices = JsonSerializer.Deserialize(JsonSerializer.Serialize(choiceState, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
if (restoredChoices.BusinessDecisions.Single().Constraints.Single().Applicability != "omitted" ||
    restoredChoices.Outcome is not PlanningNeedUserClarification choiceOutcome || choiceOutcome.Decision.Choices.Single().PreferredReason != "Declared preference")
    throw new InvalidOperationException("Published typed clarification serialization failed.");
var compiler = new PlanningGraphCompiler();
var yaml = compiler.Compile(restored.Graph!, preparation);
var imported = PlanningGraphImporter.Import(yaml, preparation);
var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(compiler.Compile(imported, preparation)));
var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), CancellationToken.None);
if (!result.Success || result.Outputs?["message"]?.GetValue<string>() != "ready") throw new InvalidOperationException("Published typed workflow execution failed.");
var computationGraph = JsonSerializer.Deserialize(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
computationGraph.Workflows[0].Steps[0].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Schema = new() { Type = "string" } }] };
computationGraph.Workflows[0].Steps[0].Input.Members[0] = new("message", new()
{
    Kind = "compute",
    Text = "const result = source.toUpperCase(); return result;",
    Members = [new("source", new() { Kind = "string", Text = "ready" })]
});
var computation = new WorkflowCompiler().Compile(WorkflowParser.Parse(compiler.Compile(computationGraph, preparation)));
var computationResult = await new WorkflowEngine().ExecuteAsync(computation.Workflows[computation.Entrypoint!], new JsonObject(), CancellationToken.None);
if (!computationResult.Success || computationResult.Outputs?["message"]?.ToString() != "READY") throw new InvalidOperationException("Published named computation failed.");
var planner = new TypedWorkflowPlanner();
var session = new PlanningSnapshot { Request = new() { TenantId = "smoke", Prompt = "Return the ready message" } };
var runtime = new SmokeRuntime(graph, preparation);
for (var attempt = 0; attempt < 30 && session.Status != PlanningStatus.Approved; attempt++)
{
    var kind = session.Status == PlanningStatus.BehaviorReview ? "accept_behavior" : session.Status == PlanningStatus.FinalReview ? "approve" : "advance";
    session = await planner.AdvanceAsync(session, new() { Kind = kind, ExpectedRevision = session.Revision, ArtifactHash = session.ArtifactHash }, runtime, CancellationToken.None);
    session = JsonSerializer.Deserialize(JsonSerializer.Serialize(session, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
    if (session.Status is PlanningStatus.Failed or PlanningStatus.Unsupported or PlanningStatus.Stopped) throw new InvalidOperationException("Published planner failed: " + session.Diagnostics.FirstOrDefault()?.Message);
}
if (session.Status != PlanningStatus.Approved || session.ApprovedHash != PlanningGraphCompiler.Fingerprint(session.Yaml!)) throw new InvalidOperationException("Published planner did not reach exact revision approval.");
var recovery = new PlanningSnapshot { Request = new() { TenantId = "smoke", Prompt = "Recover an invalid assessment" } };
runtime.InvalidIntent = true;
recovery = await planner.AdvanceAsync(recovery, new() { ExpectedRevision = recovery.Revision }, runtime, CancellationToken.None);
if (recovery.Status != PlanningStatus.Stopped || recovery.Outcome is not null || recovery.Intent.Checked) throw new InvalidOperationException("Published intent recovery failed.");
recovery = JsonSerializer.Deserialize(JsonSerializer.Serialize(recovery, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
recovery = await planner.AdvanceAsync(recovery, new() { Kind = "edit_intent", Text = "Return the ready message", ExpectedRevision = recovery.Revision }, runtime, CancellationToken.None);
runtime.InvalidIntent = false;
recovery = await planner.AdvanceAsync(recovery, new() { ExpectedRevision = recovery.Revision }, runtime, CancellationToken.None);
if (!recovery.Intent.Checked || recovery.Intent.History.Count != 1 || recovery.Diagnostics.Count != 0) throw new InvalidOperationException("Published edited intent did not resume.");
var metadata = new PlanningSnapshot
{
    Preparation = new()
    {
        Capabilities = [new()
{
    CatalogId = "declared", Resolution = "mcp", Activation = new("all_on_value", "group", "decision", "APPLY")
    { AllowedValues = ["APPLY", "NO_EFFECT"], NoEffectValues = ["NO_EFFECT"], DecisionOutputPath = "/json/outcome" },
    ArtifactContract = new(1, [new("opaque.resource", "/value", "materialize", "json_array")], [])
}]
    },
    Diagnostics = [new("CONTRACT", "$", "Contract finding", ValidationStage: PlanningValidationStage.ConditionalActivation)]
};
metadata = JsonSerializer.Deserialize(JsonSerializer.Serialize(metadata, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
if (metadata.Preparation?.Capabilities[0].Resolution != "mcp" || metadata.Preparation.Capabilities[0].Activation?.DecisionOutputPath != "/json/outcome" ||
    metadata.Preparation.Capabilities[0].ArtifactContract?.Produces[0].Pointer != "/value" ||
    metadata.Preparation.Capabilities[0].ArtifactContract?.Produces[0].Encoding != "json_array" ||
    metadata.Diagnostics[0].ValidationStage != PlanningValidationStage.ConditionalActivation)
    throw new InvalidOperationException("Published activation and artifact metadata did not survive serialization.");
var continuationGraph = new PlanningGraph
{
    Workflows = [new() { Key = "main", Steps = [new()
{
    Key = "repeat", Type = "loop.sequential", Input = new() { Kind = "object", Members = [new("while", new()
    { Kind = "compute", Text = "previous == null || previous.more === true", Members = [new("previous", new() { Kind = "loop_previous", Source = "repeat", Path = ["observe"] })] })] },
    Steps = [new() { Key = "observe", Input = new() { Kind = "object", Members = [new("more", new() { Kind = "boolean", Boolean = false })] } }]
}], Outputs = [new() { Name = "count", Schema = new() { Type = "integer" }, Value = new() { Kind = "output", Source = "repeat", Path = ["count"] } }] }]
};
continuationGraph = JsonSerializer.Deserialize(JsonSerializer.Serialize(continuationGraph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
preparation.AllowedStepTypes.Add("loop.sequential");
var continuation = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(continuationGraph, preparation)));
var continuationResult = await new WorkflowEngine().ExecuteAsync(continuation.Workflows[continuation.Entrypoint!], new JsonObject(), CancellationToken.None);
if (!continuationResult.Success || continuationResult.Outputs?["count"]?.GetValue<int>() != 1)
    throw new InvalidOperationException("Published typed sequential continuation failed: " + continuationResult.Error?.Message);
var guardedFallback = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
    version: 1
    workflows:
      main:
        steps:
          - id: source
            type: llm.call
            input:
              model: fake
              prompt: No external client is configured in this smoke test.
              structured_output:
                schema_inline:
                  type: object
                  properties:
                    count: {type: integer}
                  required: [count]
                  additionalProperties: false
                strict: true
            on_error:
              cases:
                - action: continue
                  set_output: ${data.inputs.fallback}
          - id: after
            type: set
            input: {executed: true}
        finally:
          - id: cleanup
            type: set
            input: {cleaned: true}
    """));
foreach (var valid in new[] { true, false })
{
    var guardedResult = await new WorkflowEngine().ExecuteAsync(guardedFallback.Workflows[guardedFallback.Entrypoint!],
        new JsonObject { ["fallback"] = new JsonObject { ["json"] = new JsonObject { ["count"] = valid ? JsonValue.Create(2) : JsonValue.Create("wrong") } } }, CancellationToken.None);
    if (guardedResult.Success != valid || guardedResult.StepResults.Any(s => s.StepId == "after") != valid ||
        !guardedResult.StepResults.Any(s => s.StepId == "cleanup")) throw new InvalidOperationException("Published structured continuation guard failed.");
}
await RuntimePersistenceSmoke.RunAsync();
Console.WriteLine("Typed planning and encrypted runtime persistence AOT smoke passed.");

sealed class SmokeRuntime(PlanningGraph graph, PlanningPreparation preparation) : IPlanningRuntime
{
    private PlanningSnapshot? _snapshot;
    public bool InvalidIntent { get; set; }
    public async Task<PlanningPreparationProgress> PrepareAsync(PlanningSnapshot state, CancellationToken ct)
    {
        await PlanningDeclarations.ResolveAsync(state, this, ct);
        preparation.Capabilities = [new() { Id = "declared", Description = "Return the ready message", Required = true, Resolution = "available", StepType = "set",
            OperationIds = state.Obligations.Where(o => o.Kind == "local_processing").Select(o => o.Id).ToList(),
            FixedInput = new() { ["message"] = "ready" }, OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"],"additionalProperties":false}""")!.AsObject() }];
        return new(new(), preparation);
    }
    public Task CheckpointAsync(PlanningSnapshot state, CancellationToken ct) { _snapshot = state; return Task.CompletedTask; }
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningPreparation prepared, CancellationToken ct) => Task.FromResult<IReadOnlyList<PlanningDiagnostic>>([]);
    public Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct)
    {
        if (request.ClientRequestId is null || request.StructuredOutputSchema is null || request.StructuredOutputStrict != true)
            throw new InvalidOperationException("Every model call requires a durable identity and strict typed contract.");
        graph.Workflows[0].Steps[0].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Schema = new() { Type = "string" } }] };
        JsonNode? json = InvalidIntent ? new JsonObject() : phase switch
        {
            "intent" => new JsonObject(request.StructuredOutputSchema["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                new JsonArray(new[] { "local_processing", "declaration_candidate" }.Select(role => (JsonNode?)new JsonObject
                { ["start"] = p.Value!["items"]!["properties"]!["start"]!["enum"]![0]!.DeepClone(), ["end"] = p.Value!["items"]!["properties"]!["end"]!["enum"]!.AsArray()[^1]!.DeepClone(), ["kind"] = role, ["required"] = true }).ToArray())))),
            "intent_declarations" => DeclarationResponse(request),
            "construction_schema" => new JsonObject(request.StructuredOutputSchema["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                p.Value!["properties"]?["type"] is not null ? new JsonObject { ["type"] = "string", ["nullable"] = false }
                    : new JsonObject { ["members"] = new JsonArray(new JsonObject { ["name"] = "message", ["required"] = true }), ["more"] = false }))),
            "construction" => FillHoles(request),
            "semantic_review" => new JsonObject(request.StructuredOutputSchema["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject { ["status"] = "passed" }))),
            _ => throw new InvalidOperationException("Unexpected model phase: " + phase)
        };
        return Task.FromResult(new LLMResponse { Json = json });
    }
    private JsonObject DeclarationResponse(LLMRequest request)
    {
        // Synthetic semantic response for this smoke's explicitly named message.
        // Production still validates the issued lexical reference and full proof.
        return new(request.StructuredOutputSchema!["properties"]!.AsObject().Select(page =>
            new KeyValuePair<string, JsonNode?>(page.Key, new JsonObject(page.Value!["properties"]!.AsObject().Select(field =>
            {
                var obligation = _snapshot!.Obligations.Single(o => o.Id == field.Key);
                var clause = _snapshot.References.Single(r => r.Id == obligation.Grounding!.ClauseReference);
                var name = PlanningReferences.Lexical(_snapshot, clause, PlanningSourceDecisions.Sources(_snapshot)[clause.SourceId])
                    .Single(r => PlanningChoiceEvidence.Text(_snapshot, r.Id) == "message");
                return new KeyValuePair<string, JsonNode?>(field.Key, PlanningDeclarations.Assignment(new(field.Key, "distinct_output", null, name.Id, "main", "required", null) { DeclarationReference = clause.Id, PresenceReference = clause.Id }));
            })))));
    }
    private JsonObject FillHoles(LLMRequest request)
    {
        var textSchema = JsonNode.Parse("""{"kind":"inline","type":"string","nullable":false,"description":null,"enum":[],"items":null,"properties":[],"additionalProperties":null}""")!.AsObject();
        var resultSchema = textSchema.DeepClone().AsObject(); resultSchema["type"] = "object";
        resultSchema["properties"] = new JsonArray(new JsonObject { ["name"] = "message", ["schema"] = textSchema.DeepClone(), ["required"] = true, ["default"] = null });
        var assignments = new JsonObject();
        foreach (var id in request.StructuredOutputSchema!["properties"]!["assignments"]!["properties"]!.AsObject().Select(p => p.Key))
        {
            var hole = _snapshot!.Construction.Holes.Single(h => h.Id == id);
            assignments[id] = hole.Kind == "schema" ? (hole.Path.EndsWith("/outputSchema", StringComparison.Ordinal) ? resultSchema : textSchema).DeepClone()
                : new JsonObject { ["kind"] = "literal", ["json"] = "ready" };
        }
        return new() { ["assignments"] = assignments };
    }
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct)
    {
        new WorkflowCompiler().Compile(WorkflowParser.Parse(request.Yaml));
        return Task.FromResult<IReadOnlyList<PlanningDiagnostic>>([]);
    }
    public async Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct)
    {
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(request.Yaml));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], request.Inputs, ct);
        return [new("nominal", result.Success && result.Outputs?.AsObject().Single().Value?.ToString() == "ready" ? "passed" : "failed", "Execute the published greeting workflow", [])];
    }
}
