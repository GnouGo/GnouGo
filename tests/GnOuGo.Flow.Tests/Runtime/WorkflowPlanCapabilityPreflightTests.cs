using System.Reflection;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Runtime.Executors;
using Moq;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowPlanCapabilityPreflightTests
{
    private const string ValidWorkspaceWorkflow = """
        version: 1
        name: generated-workspace
        skill:
          description: Materialize and analyze one workspace.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: materialize
                type: mcp.call
                input:
                  server: workspace-provider
                  kind: tool
                  method: create_workspace
                  request:
                    sourceUrl: https://example.invalid/source
              - id: inspect
                type: mcp.call
                input:
                  server: workspace-consumer
                  kind: tool
                  method: inspect_workspace
                  request:
                    projectRoot: ${data.steps.materialize.response.projectRootRelative}
              - id: verify
                type: mcp.call
                input:
                  server: workspace-consumer
                  kind: tool
                  method: verify_workspace
                  request:
                    projectRoot: ${data.steps.materialize.response.projectRootRelative}
        """;

    private const string ValidTemplateWorkflow = """
        version: 1
        name: generated-template
        skill:
          description: Produce a deterministic result.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: result
                type: set
                input:
                  status: complete
        """;

    private const string ValidStorageWorkflow = """
        version: 1
        name: generated-storage
        skill:
          description: Load a configured object.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: load
                type: mcp.call
                input:
                  server: object-storage
                  kind: tool
                  method: get_object
                  request:
                    key: sample
        """;

    private const string ValidConditionalWorkflow = """
        version: 1
        name: generated-conditional-review
        skill:
          description: Analyze and publish a runtime review decision.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: analyze
                type: mcp.call
                input:
                  server: reviewer
                  kind: tool
                  method: analyze_change
              - id: publish_decision
                type: switch
                expr: ${data.steps.analyze.response.decision}
                cases:
                  - value: APPROVE
                    steps:
                      - id: publish_approve
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request:
                            method: create
                            event: APPROVE
                            body: ${data.steps.analyze.response.justification}
                  - value: REQUEST_CHANGES
                    steps:
                      - id: publish_request_changes
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request:
                            method: create
                            event: REQUEST_CHANGES
                            body: ${data.steps.analyze.response.justification}
                default: []
        """;

    private const string ValidNoEffectConditionalWorkflow = """
        version: 1
        name: generated-conditional-review-with-abstention
        skill:
          description: Analyze and publish a reliable runtime review decision.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: analyze
                type: mcp.call
                input:
                  server: reviewer
                  kind: tool
                  method: analyze_change
              - id: publish_decision
                type: switch
                expr: ${data.steps.analyze.response.decision}
                cases:
                  - value: APPROVE
                    steps:
                      - id: publish_approve
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request:
                            method: create
                            event: APPROVE
                            body: Approved after analysis.
                  - value: REQUEST_CHANGES
                    steps:
                      - id: publish_request_changes
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request:
                            method: create
                            event: REQUEST_CHANGES
                            body: Changes requested after analysis.
                  - value: INCONCLUSIVE
                    steps:
                      - id: record_inconclusive
                        type: set
                        input:
                          status: inconclusive
                default: []
        """;

    private const string ValidStructuredNoEffectConditionalWorkflow = """
        version: 1
        name: generated-structured-conditional-review
        skill:
          description: Normalize, then publish a reliable runtime review decision.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: analyze
                type: mcp.call
                input:
                  server: reviewer
                  kind: tool
                  method: analyze_change
                  structured_output:
                    schema_inline:
                      type: object
                      properties:
                        decision:
                          type: string
                          enum: [APPROVE, REQUEST_CHANGES, NO_EFFECT]
                      required: [decision]
                      additionalProperties: false
                    strict: true
              - id: publish_decision
                type: switch
                expr: ${data.steps.analyze.json.decision}
                cases:
                  - value: APPROVE
                    steps:
                      - id: publish_approve
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request:
                            method: create
                            event: APPROVE
                            body: Approved after analysis.
                  - value: REQUEST_CHANGES
                    steps:
                      - id: publish_request_changes
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request:
                            method: create
                            event: REQUEST_CHANGES
                            body: Changes requested after analysis.
                  - value: NO_EFFECT
                    steps:
                      - id: record_no_effect
                        type: set
                        input:
                          status: inconclusive
                default: []
        """;

    private const string ValidRecoveredReviewDecisionWorkflow = """
        version: 1
        name: generated-recovered-review-decision
        skill:
          description: Materialize, compare, review, confirm, and submit one runtime decision.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: clone
                type: mcp.call
                input:
                  server: source-provider
                  kind: tool
                  method: git_clone
                  request: {}
              - id: compare
                type: mcp.call
                input:
                  server: payload-provider
                  kind: tool
                  method: git_compare_refs
                  request:
                    projectRoot: ${data.steps.clone.response.projectRootRelative}
              - id: review
                type: mcp.call
                input:
                  server: payload-consumer
                  kind: tool
                  method: copilot_review
                  request:
                    projectRoot: ${data.steps.clone.response.projectRootRelative}
                    filesJson: ${data.steps.compare.response.filesJson}
                  structured_output:
                    schema_inline:
                      type: object
                      properties:
                        decision:
                          type: string
                          enum: [APPROVE, REQUEST_CHANGES, COMMENT]
                      required: [decision]
                      additionalProperties: false
                    strict: true
              - id: confirm
                type: human.input
                input:
                  mode: confirm
                  prompt: Submit the prepared review?
                  choices: [confirm, cancel]
              - id: submit_decision
                type: switch
                expr: ${data.steps.review.json.decision}
                cases:
                  - value: APPROVE
                    steps:
                      - id: submit_approval
                        type: mcp.call
                        input:
                          server: review-writer
                          kind: tool
                          method: submit_review
                          request:
                            method: create
                            event: APPROVE
                            body: Approved after analysis.
                  - value: REQUEST_CHANGES
                    steps:
                      - id: submit_changes
                        type: mcp.call
                        input:
                          server: review-writer
                          kind: tool
                          method: submit_review
                          request:
                            method: create
                            event: REQUEST_CHANGES
                            body: Changes requested after analysis.
                  - value: COMMENT
                    steps:
                      - id: submit_comment
                        type: mcp.call
                        input:
                          server: review-writer
                          kind: tool
                          method: submit_review
                          request:
                            method: create
                            event: COMMENT
                            body: Review completed with comments.
                default: []
        """;

    private const string ValidCrossWorkflowConditionalWorkflow = """
        version: 1
        name: generated-cross-workflow-conditional-review
        skill:
          description: Analyze and publish a runtime review decision through independent workflows.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            inputs:
              forced_decision: { type: string, required: false }
            steps:
              - id: review
                type: workflow.call
                input:
                  ref: { kind: local, name: analyze_review }
                  args: {}
              - id: publish
                type: workflow.call
                input:
                  ref: { kind: local, name: publish_review }
                  args:
                    decision: ${data.steps.review.outputs.decision}
                    justification: ${data.steps.review.outputs.justification}
          analyze_review:
            steps:
              - id: analyze
                type: mcp.call
                input:
                  server: reviewer
                  kind: tool
                  method: analyze_change
            outputs:
              decision:
                expr: ${data.steps.analyze.response.decision}
                type: string
              justification:
                expr: ${data.steps.analyze.response.justification}
                type: string
          publish_review:
            inputs:
              decision: { type: string }
              justification: { type: string }
            steps:
              - id: publish_decision
                type: switch
                expr: ${data.inputs.decision}
                cases:
                  - value: APPROVE
                    steps:
                      - id: publish_approve
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request:
                            method: create
                            event: APPROVE
                            body: ${data.inputs.justification}
                  - value: REQUEST_CHANGES
                    steps:
                      - id: publish_request_changes
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request:
                            method: create
                            event: REQUEST_CHANGES
                            body: ${data.inputs.justification}
                default: []
        """;

    [Fact]
    public void CapabilityInventoryPrompt_DistinguishesLocalFallbackPolicyFromExternalFallbackAction()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "BuildCapabilityInventoryPrompt",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var prompt = Assert.IsType<string>(method.Invoke(null, [
            "Apply a fallback when the primary operation cannot produce a value.",
            ""
        ]));

        Assert.Contains("deterministic retry, backoff, fallback", prompt, StringComparison.Ordinal);
        Assert.Contains("separately requested runtime action performed by an AI, agent, service, or tool", prompt, StringComparison.Ordinal);
        Assert.Contains("do not classify the latter as local processing", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityInventoryPrompt_SeparatesExternalStateResolutionFromLocatorParsing()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "BuildCapabilityInventoryPrompt",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var prompt = Assert.IsType<string>(method.Invoke(null, [
            "Inspect a referenced external resource and process its current revision.",
            ""
        ]));

        Assert.Contains("external resource locator or identifier", prompt, StringComparison.Ordinal);
        Assert.Contains("state not literally encoded", prompt, StringComparison.Ordinal);
        Assert.Contains("one required external read", prompt, StringComparison.Ordinal);
        Assert.Contains("Never classify retrieval of external state as local parsing", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityOwnershipScoring_UsesPositiveActionFamiliesAndIgnoresProhibitions()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "CountPositiveCapabilityActionFamilyMatches",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        static int Score(MethodInfo methodInfo, string capability, string leaf) =>
            Assert.IsType<int>(methodInfo.Invoke(null, [capability, leaf]));

        Assert.True(Score(
            method!,
            "git_clone clone and materialize a repository",
            "Materialize the repository by cloning it into an isolated workspace.") > 0);
        Assert.Equal(0, Score(
            method!,
            "git_clone clone and materialize a repository",
            "Review changed code with AI; do not perform Git clone, fetch, or comparison."));
        Assert.True(Score(
            method!,
            "add_comment_to_pending_review publish an inline comment",
            "Publish review comments and submit the pending result.") > 0);
        Assert.True(Score(
            method!,
            "Ask Copilot to install or restore dependencies.",
            "Install project dependencies for every modified project.") > 0);
        Assert.True(Score(
            method!,
            "Ask Copilot to run relevant unit tests.",
            "Run project unit tests and report failures.") > 0);
        Assert.True(Score(
            method!,
            "Ask Copilot to run linters.",
            "Lint and format every modified project.") > 0);
    }

    [Fact]
    public void PipelineIntentClassification_TreatsCopilotInvocationAsExternalWork()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "ContainsExternalWorkIntent",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.True(Assert.IsType<bool>(method!.Invoke(
            null,
            ["Ask Copilot to install or restore dependencies for every modified project."])));
        Assert.True(Assert.IsType<bool>(method.Invoke(
            null,
            ["Ask Copilot to run all relevant unit tests and linters."])));
        Assert.False(Assert.IsType<bool>(method.Invoke(
            null,
            ["Qualify the supplied dependency, test, and lint result records locally."])));
        Assert.True(Assert.IsType<bool>(method.Invoke(
            null,
            ["Clone the pull request project into an isolated workspace, then return its project root."])));
        Assert.True(Assert.IsType<bool>(method.Invoke(
            null,
            ["Run every relevant unit test and linter with the configured assistant."])));
        Assert.True(Assert.IsType<bool>(method.Invoke(
            null,
            ["Inspect repository configuration files before deriving the command plan."])));
        Assert.False(Assert.IsType<bool>(method.Invoke(
            null,
            ["Filter high-confidence findings returned by the Copilot review."])));
        Assert.False(Assert.IsType<bool>(method.Invoke(
            null,
            ["Do not call external tools; filter file records and return clone_url locally."])));

        var positiveInvocation = typeof(WorkflowPlanExecutor).GetMethod(
            "ContainsPositiveExternalInvocationIntent",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True(Assert.IsType<bool>(positiveInvocation.Invoke(
            null,
            ["Ask Copilot to review all changed code."])));
        Assert.True(Assert.IsType<bool>(positiveInvocation.Invoke(
            null,
            ["Call the configured MCP review capability."])));
        Assert.False(Assert.IsType<bool>(positiveInvocation.Invoke(
            null,
            ["Filter high-confidence findings returned by the Copilot review."])));
    }

    [Fact]
    public void CapabilityInventorySchema_RequiresProviderNeutralClassifications()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "BuildCapabilityInventorySchema",
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
    public void CapabilityInventoryPrompt_DoesNotEmbedProviderSpecificDecisionRules()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "BuildCapabilityInventoryPrompt",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var prompt = Assert.IsType<string>(method!.Invoke(null, ["Transform a record.", ""]));
        Assert.DoesNotContain("GitHub", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pull request", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("APPROVE", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("REQUEST_CHANGES", prompt, StringComparison.Ordinal);
        Assert.Contains("independently observable requested effects", prompt, StringComparison.Ordinal);
        Assert.Contains("one atomic effect", prompt, StringComparison.Ordinal);
        Assert.Contains("input_operation_ids", prompt, StringComparison.Ordinal);
        Assert.Contains("never infer dependencies from descriptions", prompt, StringComparison.Ordinal);
        Assert.Contains("input identifiers or locator syntax", prompt, StringComparison.Ordinal);
        Assert.Contains("locally derivable parameter mapping", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not invent an eligibility, trust, or safety decision operation", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityInventoryEvidence_RejectsNoEffectEvidenceOutsideWorkflowStructure()
    {
        var matchingCalls = 0;
        var human = new RecordingHumanInputProvider(new JsonObject());
        var response = ConditionalInventoryResponse(
            "Analyze the runtime input.",
            "Publish the selected runtime decision.",
            allowNoEffectOutcome: true);
        var publish = Assert.IsType<JsonObject>(response.Json!["operations"]![1]);
        const string excerpt = "publish whichever decision is determined at runtime";
        var coverage = Evidence("user_request", excerpt);
        coverage["enforcement_kind"] = "capability_contract";
        publish["coverage_requirements"] = new JsonArray(coverage);
        publish["no_effect_outcome_evidence"] = Evidence("user_request", excerpt);

        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("workflow runtime analyst", StringComparison.Ordinal)
                    || request.Prompt.Contains("inventory repair analyst", StringComparison.Ordinal))
                {
                    return response;
                }
                if (request.Prompt.Contains("capability matcher", StringComparison.Ordinal))
                    matchingCalls++;
                throw new InvalidOperationException("No later planning stage may run.");
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateConditionalReviewFactory(),
            human);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal("model_contract_violation", result.Error.Details!["classification"]!.GetValue<string>());
        var issue = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Error.Details["contract_issues"])));
        Assert.Equal("no_effect_evidence_not_workflow_structure", issue["code"]!.GetValue<string>());
        Assert.Equal("publish", issue["operation_id"]!.GetValue<string>());
        Assert.Equal("no_effect_outcome_evidence", issue["field"]!.GetValue<string>());
        Assert.Empty(human.Requests);
        Assert.Equal(0, matchingCalls);
    }

    [Fact]
    public void CapabilityMatching_NormalizesOnlyUniqueInventoryIdSeparatorDrift()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "ResolveMatchingInventoryId",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.Equal(
            "operation-clone_exactly_once",
            method!.Invoke(null, [
                "operation-clone_exactly-once",
                new[] { "operation-clone_exactly_once", "operation-review" }
            ]));
        Assert.Equal(
            "operation-clone-exactly-once",
            method.Invoke(null, [
                "operation-clone-exactly-once",
                new[] { "operation-clone_exactly_once", "operation_clone-exactly_once" }
            ]));
        Assert.Equal(
            "unknown-operation",
            method.Invoke(null, [
                "unknown-operation",
                new[] { "operation-clone_exactly_once" }
            ]));
        Assert.Equal(
            "operation-op-9",
            method.Invoke(null, [
                "op-9",
                new[] { "operation-op-9", "operation-review" }
            ]));
        Assert.Equal(
            "op-9",
            method.Invoke(null, [
                "op-9",
                new[] { "operation-op-9", "constraint-op-9" }
            ]));
    }

    [Fact]
    public void CapabilityOwnershipFocus_UsesConcreteCapabilityToSplitReviewFromPublication()
    {
        var overlapMethod = typeof(WorkflowPlanExecutor).GetMethod(
            "CountFocusedCapabilityActionFamilyMatches",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var extraneousMethod = typeof(WorkflowPlanExecutor).GetMethod(
            "CountExtraneousFocusedCapabilityActionFamilies",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        const string operation = "Review all changed code and publish high-confidence findings as inline review comments.";
        const string reviewCapability = "copilot_review runs all bounded pull request review batches and returns validated findings.";
        const string reviewLeaf = "Review all changed code with Copilot and return validated findings.";
        const string publicationLeaf = "Publish validated findings as inline pull request review comments.";

        static int Invoke(MethodInfo method, string operationText, string capabilityText, string leafText)
            => Assert.IsType<int>(method.Invoke(null, [operationText, capabilityText, leafText]));

        Assert.True(Invoke(overlapMethod, operation, reviewCapability, reviewLeaf) > 0);
        Assert.True(Invoke(overlapMethod, operation, reviewCapability, publicationLeaf) > 0);
        Assert.True(
            Invoke(extraneousMethod, operation, reviewCapability, reviewLeaf)
            < Invoke(extraneousMethod, operation, reviewCapability, publicationLeaf));

        const string interactiveCapability = "copilot_interactive_one_shot may install dependencies, run commands, or edit files.";
        Assert.True(Invoke(
            overlapMethod,
            "Run all relevant unit tests and linters.",
            interactiveCapability,
            "Run project unit tests and linters with Copilot.") > 0);
        Assert.Equal(0, Invoke(
            overlapMethod,
            "Run all relevant unit tests and linters.",
            interactiveCapability,
            "Restore project dependencies with Copilot."));

        const string cloneOperation = "Clone the pull request project exactly once.";
        const string cloneCapability = "git_clone clones and materializes a repository workspace.";
        Assert.Equal(0, Invoke(
            overlapMethod,
            cloneOperation,
            cloneCapability,
            "Resolve pull request metadata and return clone_url: string."));
        Assert.True(Invoke(
            overlapMethod,
            cloneOperation,
            cloneCapability,
            "clone pull request project") > 0);
    }

    [Fact]
    public void CapabilityInvocationDetection_IgnoresDataFlowReferencesAndProhibitions()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "IsPositiveCapabilityInvocationClause",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        static bool IsInvocation(MethodInfo methodInfo, string clause, string capability) =>
            Assert.IsType<bool>(methodInfo.Invoke(null, [clause, capability]));

        Assert.True(IsInvocation(method!, "Call analyze_records with the normalized batch.", "analyze_records"));
        Assert.True(IsInvocation(method!, "Compare the revisions via compare_records.", "compare_records"));
        Assert.True(IsInvocation(method!, "method: copilot_review_publication_gate", "copilot_review_publication_gate"));
        Assert.True(IsInvocation(method!, "\"tool\": \"copilot_review_publication_gate\"", "copilot_review_publication_gate"));
        Assert.True(IsInvocation(
            method!,
            "`GnOuGo.Analysis.Mcp/copilot_review_publication_gate` with `publicationPolicy: interactive`.",
            "copilot_review_publication_gate"));
        Assert.False(IsInvocation(method!, "The normalized batch is required by analyze_records.", "analyze_records"));
        Assert.False(IsInvocation(method!, "Use compare_records output as the analyzer input.", "compare_records"));
        Assert.False(IsInvocation(method!, "Do not invoke analyze_records in this leaf.", "analyze_records"));
        Assert.False(IsInvocation(
            method!,
            "Create one local payload object compatible with a later add_record request. Do not call that tool here.",
            "add_record"));
        Assert.False(IsInvocation(
            method!,
            "Shape the output for the downstream send_record capability; never invoke it in this transform.",
            "send_record"));
        Assert.False(IsInvocation(
            method!,
            "Transform each record into a payload suitable for a later add_record call.",
            "add_record"));
        Assert.True(IsInvocation(
            method!,
            "Call add_record with the prepared payload request.",
            "add_record"));
    }

    [Fact]
    public void DirectMcpScalarInputPromotion_UsesAuthoritativeDiscoveredType()
    {
        const string yaml = """
            version: 1
            name: scalar-input-promotion
            skill:
              description: Invoke a typed external operation.
              tags: [test]
              inputs:
                count:
                  type: number
                  required: true
              outputs: {}
            workflows:
              main:
                inputs:
                  count:
                    type: number
                    required: true
                steps:
                  - id: invoke
                    type: mcp.call
                    input:
                      server: inventory
                      kind: tool
                      method: reserve
                      request:
                        count: ${data.inputs.count}
            """;
        var discoveryType = typeof(WorkflowPlanExecutor).GetNestedType(
            "McpServerDiscovery",
            BindingFlags.NonPublic);
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "PromoteGeneratedDirectMcpScalarInputSchemas",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(discoveryType);
        Assert.NotNull(method);

        var discovery = Activator.CreateInstance(discoveryType!, nonPublic: true)!;
        discoveryType!.GetProperty("Name")!.SetValue(discovery, "inventory");
        discoveryType.GetProperty("Discovered")!.SetValue(discovery, true);
        discoveryType.GetProperty("Tools")!.SetValue(discovery, new List<McpToolInfo>
        {
            new()
            {
                Name = "reserve",
                InputSchema = JsonNode.Parse("""
                    {
                      "type": "object",
                      "properties": { "count": { "type": "integer" } },
                      "required": ["count"],
                      "additionalProperties": false
                    }
                    """)
            }
        });
        var discoveryList = Assert.IsAssignableFrom<IList>(
            Activator.CreateInstance(typeof(List<>).MakeGenericType(discoveryType)));
        discoveryList.Add(discovery);

        var document = WorkflowParser.Parse(yaml);
        var result = Assert.IsAssignableFrom<ITuple>(method!.Invoke(null, [document, yaml, discoveryList]));
        var promotedYaml = Assert.IsType<string>(result[1]);
        var promoted = WorkflowParser.Parse(promotedYaml);

        Assert.Equal("integer", promoted.Skill!.Inputs!["count"].Type);
        Assert.Equal("integer", promoted.Workflows["main"].Inputs!["count"].Type);
    }

    [Fact]
    public void DirectWorkflowCallObjectInputPromotion_AcceptsAuthoritativeRicherSourceContract()
    {
        const string yaml = """
            version: 1
            name: workflow-call-input-promotion
            skill:
              description: Route a typed object between local leaves.
              tags: [test]
              inputs: {}
              outputs: {}
            workflows:
              main:
                steps:
                  - id: produce
                    type: workflow.call
                    input:
                      ref: { kind: local, name: producer }
                      args: {}
                  - id: consume
                    type: workflow.call
                    input:
                      ref: { kind: local, name: consumer }
                      args:
                        payload: ${data.steps.produce.outputs.payload}
              producer:
                steps:
                  - id: value
                    type: set
                    input:
                      payload:
                        name: sample
                        raw_json: '{}'
                outputs:
                  payload:
                    expr: ${data.steps.value.payload}
                    type: object
                    properties:
                      name: { type: string }
                      raw_json: { type: string }
                    required_properties: [name, raw_json]
              consumer:
                inputs:
                  payload:
                    type: object
                    required: true
                    properties:
                      name: { type: string }
                    required_properties: [name]
                steps:
                  - id: result
                    type: set
                    input:
                      name: ${data.inputs.payload.name}
            """;
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "PromoteGeneratedDirectWorkflowCallObjectInputSchemas",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var document = WorkflowParser.Parse(yaml);
        var result = Assert.IsAssignableFrom<ITuple>(method!.Invoke(
            null,
            [document, yaml, null, new StepExecutorRegistry()]));
        var promotedYaml = Assert.IsType<string>(result[1]);
        var promoted = WorkflowParser.Parse(promotedYaml);

        var payload = promoted.Workflows["consumer"].Inputs!["payload"];
        Assert.NotNull(payload.Properties);
        Assert.Contains("name", payload.Properties.Keys);
        Assert.Contains("raw_json", payload.Properties.Keys);
        Assert.True(payload.Required);
    }

    [Fact]
    public void OptionalCapabilityInvocationDetection_RecognizesExplicitOptionalityButNotFailureGuards()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "IsExplicitlyOptionalCapabilityInvocation",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        static bool IsOptional(MethodInfo methodInfo, string clause, string capability) =>
            Assert.IsType<bool>(methodInfo.Invoke(null, [clause, capability]));

        Assert.True(IsOptional(method!, "Optionally call delete_pending if cleanup is implemented.", "delete_pending"));
        Assert.True(IsOptional(method!, "A best-effort delete_pending call may be used.", "delete_pending"));
        Assert.False(IsOptional(method!, "Call delete_pending if the main operation fails.", "delete_pending"));
        Assert.False(IsOptional(method!, "Call submit_pending with event COMMENT.", "submit_pending"));
    }

    [Fact]
    public void LiteralSelectorAssignmentDetection_AcceptsMarkdownQuotedFieldAndValue()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "ContainsLiteralSelectorAssignment",
            BindingFlags.Static | BindingFlags.NonPublic);
        var bindingType = typeof(WorkflowPlanExecutor).GetNestedType(
            "CapabilityRequestBinding",
            BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(bindingType);

        var binding = Activator.CreateInstance(
            bindingType!,
            ["/method", JsonValue.Create("get_records")]);
        Assert.NotNull(binding);

        var detected = Assert.IsType<bool>(method!.Invoke(null,
            ["Invoke the capability with `request.method`: `get_records`.", binding]));

        Assert.True(detected);
    }

    [Fact]
    public void PositiveLiteralSelectorAssignmentDetection_RejectsProhibitedSelector()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "ContainsPositiveLiteralSelectorAssignment",
            BindingFlags.Static | BindingFlags.NonPublic);
        var bindingType = typeof(WorkflowPlanExecutor).GetNestedType(
            "CapabilityRequestBinding",
            BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(bindingType);

        var binding = Activator.CreateInstance(
            bindingType!,
            ["/event", JsonValue.Create("APPROVE")]);
        Assert.NotNull(binding);

        Assert.True(Assert.IsType<bool>(method!.Invoke(null,
            ["Submit the final review with event: APPROVE.", binding])));
        Assert.False(Assert.IsType<bool>(method.Invoke(null,
            ["This leaf must not submit event: APPROVE; final submission belongs to another leaf.", binding])));
    }

    [Fact]
    public void SelectorValueOwnershipDetection_UsesCohesiveBranchIdentity()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "ContainsPositiveSelectorValueMention",
            BindingFlags.Static | BindingFlags.NonPublic);
        var bindingType = typeof(WorkflowPlanExecutor).GetNestedType(
            "CapabilityRequestBinding",
            BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(bindingType);

        var binding = Activator.CreateInstance(
            bindingType!,
            ["/event", JsonValue.Create("REQUEST_CHANGES")]);
        Assert.NotNull(binding);

        Assert.True(Assert.IsType<bool>(method!.Invoke(null,
            ["submit_request_changes_review", binding])));
        Assert.False(Assert.IsType<bool>(method.Invoke(null,
            ["submit_approve_review", binding])));
        Assert.False(Assert.IsType<bool>(method.Invoke(null,
            ["Do not submit REQUEST_CHANGES from this branch.", binding])));
    }

    [Fact]
    public void StructuredPlannedToolBindings_ExpandOneVaryingSelectorPath()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "ParseStructuredRequestBindingVariants",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var bindings = new JsonArray
        {
            new JsonObject { ["path"] = "/method", ["value"] = "get" },
            new JsonObject { ["path"] = "/method", ["value"] = "get_diff" },
            new JsonObject { ["path"] = "/format", ["value"] = "json" }
        };

        var variants = Assert.IsAssignableFrom<IEnumerable>(method!.Invoke(null, [bindings, "planned tool"]))
            .Cast<object>()
            .Select(variant => Assert.IsAssignableFrom<IEnumerable>(variant).Cast<object>().ToArray())
            .ToArray();

        Assert.Equal(2, variants.Length);
        Assert.All(variants, variant => Assert.Equal(2, variant.Length));
        Assert.Equal(
            ["get", "get_diff"],
            variants.Select(variant => Assert.IsAssignableFrom<JsonNode>(
                        variant[0].GetType().GetProperty("Value")!.GetValue(variant[0]))
                    .GetValue<string>())
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void StructuredPlannedToolBindings_RejectMultipleVaryingSelectorPaths()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "ParseStructuredRequestBindingVariants",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var bindings = new JsonArray
        {
            new JsonObject { ["path"] = "/method", ["value"] = "get" },
            new JsonObject { ["path"] = "/method", ["value"] = "create" },
            new JsonObject { ["path"] = "/event", ["value"] = "APPROVE" },
            new JsonObject { ["path"] = "/event", ["value"] = "REQUEST_CHANGES" }
        };

        var exception = Assert.Throws<TargetInvocationException>(() =>
            method!.Invoke(null, [bindings, "planned tool"]));
        Assert.Contains("separate the exact selector combinations", exception.InnerException!.Message);
    }

    [Fact]
    public void CapabilityMatchingIds_DeduplicateRepeatedCatalogIdentity()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "ReadMatchingIds",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] arguments = [new JsonArray("cap-one", "cap-one"), 8, null];

        var result = Assert.IsAssignableFrom<IReadOnlyList<string>>(method!.Invoke(null, arguments));

        Assert.True(Assert.IsType<bool>(arguments[2]));
        Assert.Equal(["cap-one"], result);
    }

    [Fact]
    public void CompositionPruning_RemovesUnlockedMembersEncapsulatedBySelectedWrapper()
    {
        var executorType = typeof(WorkflowPlanExecutor);
        var method = executorType.GetMethod(
            "RemoveEncapsulatedUnlockedToolPlans",
            BindingFlags.Static | BindingFlags.NonPublic);
        var plannedToolType = executorType.GetNestedType("PipelinePlannedTool", BindingFlags.NonPublic);
        var resolvedCapabilityType = executorType.GetNestedType("ResolvedCapability", BindingFlags.NonPublic);
        var requestBindingType = executorType.GetNestedType("CapabilityRequestBinding", BindingFlags.NonPublic);
        var discoveryType = executorType.GetNestedType("McpServerDiscovery", BindingFlags.NonPublic);
        var contextType = executorType.GetNestedType("PipelineMcpContext", BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(plannedToolType);
        Assert.NotNull(resolvedCapabilityType);
        Assert.NotNull(requestBindingType);
        Assert.NotNull(discoveryType);
        Assert.NotNull(contextType);

        var emptyBindings = Array.CreateInstance(requestBindingType!, 0);
        object PlannedTool(string toolMethod, string[] operationIds, string[] catalogIds) =>
            Activator.CreateInstance(plannedToolType!,
            [
                "reviewer", "tool", toolMethod, true, "Review work",
                Array.Empty<string>(), Array.Empty<string>(), emptyBindings,
                operationIds, catalogIds, null
            ])!;

        var plannedTools = Assert.IsAssignableFrom<IList>(Activator.CreateInstance(
            typeof(List<>).MakeGenericType(plannedToolType!)));
        plannedTools.Add(PlannedTool("review_complete", ["op-review"], ["cap-wrapper"]));
        plannedTools.Add(PlannedTool("review_analyze", [], []));

        var wrapperCapability = Activator.CreateInstance(resolvedCapabilityType!,
        [
            "op-review::cap-wrapper", "Perform the complete review.", true, "mcp",
            "reviewer", "tool", "review_complete", emptyBindings,
            "op-review", "cap-wrapper", "matched", "external_effect", "execute", null,
            "Run every review phase as one complete operation.", null
        ]);
        Assert.NotNull(wrapperCapability);
        var lockedCapabilities = Array.CreateInstance(resolvedCapabilityType!, 1);
        lockedCapabilities.SetValue(wrapperCapability, 0);

        var wrapperTool = new McpToolInfo
        {
            Name = "review_complete",
            CompositionContract = new McpCapabilityCompositionResolution(
                new McpCapabilityComposition(
                    1,
                    McpCapabilityCompositionConventions.CompleteOperationKind,
                    [new McpEncapsulatedCapability("tool", "review_analyze")]),
                [])
        };
        var discovery = Activator.CreateInstance(discoveryType!, nonPublic: true)!;
        discoveryType!.GetProperty("Name")!.SetValue(discovery, "reviewer");
        discoveryType.GetProperty("Tools")!.SetValue(discovery, new[] { wrapperTool });
        discoveryType.GetProperty("Discovered")!.SetValue(discovery, true);
        var discoveries = Array.CreateInstance(discoveryType, 1);
        discoveries.SetValue(discovery, 0);
        var context = Activator.CreateInstance(
            contextType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [discoveries, null],
            culture: null);
        Assert.NotNull(context);

        method!.Invoke(null, [plannedTools, lockedCapabilities, context]);

        var remaining = plannedTools.Cast<object>().ToArray();
        Assert.Single(remaining);
        Assert.Equal("review_complete", plannedToolType!.GetProperty("Method")!.GetValue(remaining[0]));
    }

    [Fact]
    public void LockedComposition_RemovesMalformedIdentityClaimBeforeCanonicalRebuild()
    {
        var executorType = typeof(WorkflowPlanExecutor);
        var method = executorType.GetMethod(
            "RemoveClaimedOrMatchingLockedToolPlans",
            BindingFlags.Static | BindingFlags.NonPublic);
        var plannedToolType = executorType.GetNestedType("PipelinePlannedTool", BindingFlags.NonPublic);
        var resolvedCapabilityType = executorType.GetNestedType("ResolvedCapability", BindingFlags.NonPublic);
        var requestBindingType = executorType.GetNestedType("CapabilityRequestBinding", BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(plannedToolType);
        Assert.NotNull(resolvedCapabilityType);
        Assert.NotNull(requestBindingType);

        var emptyBindings = Array.CreateInstance(requestBindingType!, 0);
        object PlannedTool(string toolMethod, string[] operationIds, string[] catalogIds) =>
            Activator.CreateInstance(plannedToolType!,
            [
                "neutral", "tool", toolMethod, true, "External work",
                Array.Empty<string>(), Array.Empty<string>(), emptyBindings,
                operationIds, catalogIds, null
            ])!;

        var plannedTools = Assert.IsAssignableFrom<IList>(Activator.CreateInstance(
            typeof(List<>).MakeGenericType(plannedToolType!)));
        plannedTools.Add(PlannedTool("incorrect_member", ["op-work"], ["cap-wrapper"]));
        plannedTools.Add(PlannedTool("complete_work", [], []));
        plannedTools.Add(PlannedTool("unrelated_work", [], []));

        var wrapperCapability = Activator.CreateInstance(resolvedCapabilityType!,
        [
            "op-work::cap-wrapper", "Perform the complete operation.", true, "mcp",
            "neutral", "tool", "complete_work", emptyBindings,
            "op-work", "cap-wrapper", "composed", "external_effect", "execute", null,
            "Run every phase as one complete operation.", null
        ]);
        Assert.NotNull(wrapperCapability);
        var lockedCapabilities = Array.CreateInstance(resolvedCapabilityType!, 1);
        lockedCapabilities.SetValue(wrapperCapability, 0);

        method!.Invoke(null, [plannedTools, lockedCapabilities]);

        var remaining = plannedTools.Cast<object>()
            .Select(tool => plannedToolType!.GetProperty("Method")!.GetValue(tool)!.ToString()!)
            .ToArray();
        Assert.Equal(["unrelated_work"], remaining);
    }

    [Fact]
    public void NativeCapabilityToolPlan_IsRemovedBeforeMcpValidation()
    {
        var executorType = typeof(WorkflowPlanExecutor);
        var method = executorType.GetMethod(
            "RemoveClaimedNativeCapabilityToolPlans",
            BindingFlags.Static | BindingFlags.NonPublic);
        var plannedToolType = executorType.GetNestedType("PipelinePlannedTool", BindingFlags.NonPublic);
        var resolvedCapabilityType = executorType.GetNestedType("ResolvedCapability", BindingFlags.NonPublic);
        var requestBindingType = executorType.GetNestedType("CapabilityRequestBinding", BindingFlags.NonPublic);
        var discoveryType = executorType.GetNestedType("McpServerDiscovery", BindingFlags.NonPublic);
        var contextType = executorType.GetNestedType("PipelineMcpContext", BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(plannedToolType);
        Assert.NotNull(resolvedCapabilityType);
        Assert.NotNull(requestBindingType);
        Assert.NotNull(discoveryType);
        Assert.NotNull(contextType);

        var emptyBindings = Array.CreateInstance(requestBindingType!, 0);
        object PlannedTool(string server, string toolMethod, string[] operationIds, string[] catalogIds) =>
            Activator.CreateInstance(plannedToolType!,
            [
                server, "tool", toolMethod, true, "Planned execution",
                Array.Empty<string>(), Array.Empty<string>(), emptyBindings,
                operationIds, catalogIds, null
            ])!;

        var plannedTools = Assert.IsAssignableFrom<IList>(Activator.CreateInstance(
            typeof(List<>).MakeGenericType(plannedToolType!)));
        plannedTools.Add(PlannedTool("native", "llm.call", ["analyze"], ["cap-native-llm"]));
        plannedTools.Add(PlannedTool(string.Empty, "llm.call", [], []));
        plannedTools.Add(PlannedTool("documented", "llm.call", [], []));
        plannedTools.Add(PlannedTool("reviewer", "read", [], []));

        var nativeCapability = Activator.CreateInstance(resolvedCapabilityType!,
        [
            "analyze::cap-native-llm", "Analyze one runtime result.", true, "native",
            null, null, "llm.call", emptyBindings,
            "analyze", "cap-native-llm", "matched", "external_effect", "execute", null,
            "Produce a typed decision.", null
        ]);
        Assert.NotNull(nativeCapability);
        var lockedCapabilities = Array.CreateInstance(resolvedCapabilityType!, 1);
        lockedCapabilities.SetValue(nativeCapability, 0);

        var discovery = Activator.CreateInstance(discoveryType!, nonPublic: true)!;
        discoveryType!.GetProperty("Name")!.SetValue(discovery, "documented");
        discoveryType.GetProperty("Tools")!.SetValue(discovery, new[] { new McpToolInfo { Name = "llm.call" } });
        discoveryType.GetProperty("Discovered")!.SetValue(discovery, true);
        var discoveries = Array.CreateInstance(discoveryType, 1);
        discoveries.SetValue(discovery, 0);
        var context = Activator.CreateInstance(
            contextType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [discoveries, null],
            culture: null);
        Assert.NotNull(context);

        method!.Invoke(null, [plannedTools, lockedCapabilities, context]);

        var remaining = plannedTools.Cast<object>().ToArray();
        Assert.Equal(2, remaining.Length);
        Assert.Contains(remaining, item =>
            string.Equals("documented", plannedToolType!.GetProperty("Server")!.GetValue(item)?.ToString(), StringComparison.Ordinal)
            && string.Equals("llm.call", plannedToolType.GetProperty("Method")!.GetValue(item)?.ToString(), StringComparison.Ordinal));
        Assert.Contains(remaining, item =>
            string.Equals("reviewer", plannedToolType.GetProperty("Server")!.GetValue(item)?.ToString(), StringComparison.Ordinal)
            && string.Equals("read", plannedToolType.GetProperty("Method")!.GetValue(item)?.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void ConditionalComposition_ConsolidatesExtractorSplitVariantsIntoOneLeaf()
    {
        var executorType = typeof(WorkflowPlanExecutor);
        var method = executorType.GetMethod(
            "ConsolidateSplitConditionalVariantSpecs",
            BindingFlags.Static | BindingFlags.NonPublic);
        var specType = executorType.GetNestedType("WorkflowPipelineSubworkflowSpec", BindingFlags.NonPublic);
        var plannedToolType = executorType.GetNestedType("PipelinePlannedTool", BindingFlags.NonPublic);
        var nativeStepType = executorType.GetNestedType("PipelinePlannedNativeStep", BindingFlags.NonPublic);
        var resolvedCapabilityType = executorType.GetNestedType("ResolvedCapability", BindingFlags.NonPublic);
        var requestBindingType = executorType.GetNestedType("CapabilityRequestBinding", BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(specType);
        Assert.NotNull(plannedToolType);
        Assert.NotNull(nativeStepType);
        Assert.NotNull(resolvedCapabilityType);
        Assert.NotNull(requestBindingType);

        object Binding(string value) => Activator.CreateInstance(
            requestBindingType!, ["/outcome", JsonValue.Create(value)])!;
        object Bindings(string value)
        {
            var result = Array.CreateInstance(requestBindingType!, 1);
            result.SetValue(Binding(value), 0);
            return result;
        }
        var acceptActivation = new McpCapabilityActivation("exactly_one", "finalize", "classify", "accept");
        var rejectActivation = new McpCapabilityActivation("exactly_one", "finalize", "classify", "reject");
        object PlannedTool(string value, string catalogId, McpCapabilityActivation? activation)
            => Activator.CreateInstance(plannedToolType!,
            [
                "neutral", "tool", "finalize", true, $"Finalize {value}.",
                Array.Empty<string>(), Array.Empty<string>(), Bindings(value),
                new[] { "finalize" }, new[] { catalogId }, activation
            ])!;

        var acceptOriginalTool = PlannedTool("accept", "cap-accept", null);
        var rejectOriginalTool = PlannedTool("reject", "cap-reject", null);
        var acceptReassignedTool = PlannedTool("accept", "cap-accept", acceptActivation);
        var rejectReassignedTool = PlannedTool("reject", "cap-reject", rejectActivation);

        object Tools(params object[] values)
        {
            var result = Array.CreateInstance(plannedToolType!, values.Length);
            for (var index = 0; index < values.Length; index++)
                result.SetValue(values[index], index);
            return result;
        }
        var emptyNativeSteps = Array.CreateInstance(nativeStepType!, 0);
        object Spec(string name, object plannedTools, string content)
            => Activator.CreateInstance(specType!,
            [
                name, $"Execute {name}.", "A conditional variant.", "external_work", "external_action",
                "One finalized result.",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["decision"] = "string" },
                new Dictionary<string, string>(StringComparer.Ordinal) { ["result"] = "string" },
                new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["decision"] = JsonNode.Parse("{\"type\":\"string\"}") },
                new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["result"] = JsonNode.Parse("{\"type\":\"string\"}") },
                plannedTools, null, "Conditional external effect.", content, "Generate the variant.",
                Array.Empty<string>(), emptyNativeSteps
            ])!;

        var originalSpecs = Array.CreateInstance(specType!, 2);
        originalSpecs.SetValue(Spec("accept_leaf", Tools(acceptOriginalTool), "Accept the record."), 0);
        originalSpecs.SetValue(Spec("reject_leaf", Tools(rejectOriginalTool), "Reject the record."), 1);
        var reassignedSpecs = Array.CreateInstance(specType!, 2);
        reassignedSpecs.SetValue(Spec(
            "accept_leaf",
            Tools(acceptReassignedTool, rejectReassignedTool),
            "Accept the record."), 0);
        reassignedSpecs.SetValue(Spec("reject_leaf", Tools(), "Reject the record."), 1);

        object Capability(string value, string catalogId, McpCapabilityActivation activation)
            => Activator.CreateInstance(resolvedCapabilityType!,
            [
                $"finalize::{catalogId}", $"Finalize {value}.", true, "mcp",
                "neutral", "tool", "finalize", Bindings(value),
                "finalize", catalogId, "conditional", "external_effect", "write", activation,
                $"Finalize with {value}.", null
            ])!;
        var capabilities = Array.CreateInstance(resolvedCapabilityType!, 2);
        capabilities.SetValue(Capability("accept", "cap-accept", acceptActivation), 0);
        capabilities.SetValue(Capability("reject", "cap-reject", rejectActivation), 1);

        var result = method!.Invoke(null,
        [
            originalSpecs,
            reassignedSpecs,
            capabilities,
            "Call accept_leaf or reject_leaf according to the runtime decision."
        ])!;
        var consolidated = Assert.IsAssignableFrom<IEnumerable>(
            result.GetType().GetField("Item1")!.GetValue(result)).Cast<object>().ToArray();
        var mainPrompt = Assert.IsType<string>(result.GetType().GetField("Item2")!.GetValue(result));

        var owner = Assert.Single(consolidated);
        Assert.Equal("accept_leaf", specType!.GetProperty("Name")!.GetValue(owner));
        Assert.Contains("Reject the record.", specType.GetProperty("Content")!.GetValue(owner)!.ToString());
        Assert.DoesNotContain("reject_leaf", mainPrompt, StringComparison.Ordinal);
        Assert.Contains("accept_leaf", mainPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ConditionalComposition_SelectsEarliestExactVariantOwnerInsteadOfDecisionProducer()
    {
        var executorType = typeof(WorkflowPlanExecutor);
        var method = executorType.GetMethod(
            "SelectDeclaredLockedCapabilityOwnerIndex",
            BindingFlags.Static | BindingFlags.NonPublic);
        var resolvedCapabilityType = executorType.GetNestedType("ResolvedCapability", BindingFlags.NonPublic);
        var requestBindingType = executorType.GetNestedType("CapabilityRequestBinding", BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(resolvedCapabilityType);
        Assert.NotNull(requestBindingType);

        object Bindings(string value)
        {
            var result = Array.CreateInstance(requestBindingType!, 1);
            result.SetValue(Activator.CreateInstance(
                requestBindingType!, ["/outcome", JsonValue.Create(value)]), 0);
            return result;
        }

        object Capability(string value, string catalogId)
            => Activator.CreateInstance(resolvedCapabilityType!,
            [
                $"finalize::{catalogId}", $"Finalize {value}.", true, "mcp",
                "neutral", "tool", "finalize", Bindings(value),
                "finalize", catalogId, "conditional", "external_effect", "write",
                new McpCapabilityActivation("exactly_one", "finalize", "classify", value),
                $"Finalize with {value}.", null
            ])!;

        var capabilities = Array.CreateInstance(resolvedCapabilityType!, 2);
        capabilities.SetValue(Capability("accept", "cap-accept"), 0);
        capabilities.SetValue(Capability("reject", "cap-reject"), 1);

        var selected = method!.Invoke(null, [capabilities, new[] { 1, 2 }]);

        Assert.Equal(1, Assert.IsType<int>(selected));
    }

    [Fact]
    public void PipelineExtraction_UsesUniqueDeclaredOperationOwnerWhenPlannedToolWasOmitted()
    {
        var executorType = typeof(WorkflowPlanExecutor);
        var method = executorType.GetMethod(
            "FindDeclaredLockedCapabilityOwnerIndices",
            BindingFlags.Static | BindingFlags.NonPublic);
        var specType = executorType.GetNestedType("WorkflowPipelineSubworkflowSpec", BindingFlags.NonPublic);
        var plannedToolType = executorType.GetNestedType("PipelinePlannedTool", BindingFlags.NonPublic);
        var nativeStepType = executorType.GetNestedType("PipelinePlannedNativeStep", BindingFlags.NonPublic);
        var resolvedCapabilityType = executorType.GetNestedType("ResolvedCapability", BindingFlags.NonPublic);
        var bindingType = executorType.GetNestedType("CapabilityRequestBinding", BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(specType);
        Assert.NotNull(plannedToolType);
        Assert.NotNull(nativeStepType);
        Assert.NotNull(resolvedCapabilityType);
        Assert.NotNull(bindingType);

        object Spec(string name, string[] ownedOperationIds)
        {
            var spec = Activator.CreateInstance(specType!,
            [
                name,
                "An opaque leaf goal.",
                "No capability or operation wording is present.",
                "external_work",
                "external_action",
                "An opaque external outcome.",
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, JsonNode?>(StringComparer.Ordinal),
                new Dictionary<string, JsonNode?>(StringComparer.Ordinal),
                Array.CreateInstance(plannedToolType!, 0),
                null,
                "Opaque extraction reason.",
                "No physical tool identity is declared.",
                "",
                Array.Empty<string>(),
                Array.CreateInstance(nativeStepType!, 0)
            ])!;
            specType!.GetProperty("OwnedOperationIds")!.SetValue(spec, ownedOperationIds);
            return spec;
        }

        var capability = Activator.CreateInstance(resolvedCapabilityType!,
        [
            "opaque_operation::opaque_catalog", "Opaque external effect.", true, "mcp",
            "opaque_server", "tool", "opaque_method", Array.CreateInstance(bindingType!, 0),
            "opaque_operation", "opaque_catalog", "matched", "external_effect", "write",
            null, "Opaque capability card.", null
        ])!;
        var capabilities = Array.CreateInstance(resolvedCapabilityType!, 1);
        capabilities.SetValue(capability, 0);
        var specs = Array.CreateInstance(specType!, 2);
        specs.SetValue(Spec("first_leaf", []), 0);
        specs.SetValue(Spec("second_leaf", ["opaque_operation"]), 1);

        var owners = Assert.IsType<int[]>(method!.Invoke(null, [capabilities, specs]));

        Assert.Equal([1], owners);
    }

    [Fact]
    public void CapabilityCoverageReview_AcceptsOnlyEvidenceGroundedIncompleteMatch()
    {
        var executorType = typeof(WorkflowPlanExecutor);
        var method = executorType.GetMethod(
            "ParseCapabilityCoverageReview",
            BindingFlags.Static | BindingFlags.NonPublic);
        var operationType = executorType.GetNestedType("CapabilityInventoryOperation", BindingFlags.NonPublic);
        var evidenceType = executorType.GetNestedType("CapabilityEvidenceAnchor", BindingFlags.NonPublic);
        var matchType = executorType.GetNestedType("CapabilityOperationMatch", BindingFlags.NonPublic);
        var catalogType = executorType.GetNestedType("CapabilityCatalog", BindingFlags.NonPublic);
        var entryType = executorType.GetNestedType("CapabilityCatalogEntry", BindingFlags.NonPublic);
        var bindingType = executorType.GetNestedType("CapabilityRequestBinding", BindingFlags.NonPublic);
        var fieldType = executorType.GetNestedType("CapabilitySchemaField", BindingFlags.NonPublic);
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
        var executorType = typeof(WorkflowPlanExecutor);
        var method = executorType.GetMethod(
            "CanonicalizeSharedStructuredDecisionOutputPaths",
            BindingFlags.Static | BindingFlags.NonPublic);
        var operationType = executorType.GetNestedType("CapabilityInventoryOperation", BindingFlags.NonPublic);
        var matchType = executorType.GetNestedType("CapabilityOperationMatch", BindingFlags.NonPublic);
        var constraintMatchType = executorType.GetNestedType("CapabilityConstraintMatch", BindingFlags.NonPublic);
        var issueType = executorType.GetNestedType("CapabilityMatchingIssue", BindingFlags.NonPublic);
        var evaluationType = executorType.GetNestedType("CapabilityMatchingEvaluation", BindingFlags.NonPublic);
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
    public void ConditionalComposition_SeparatesExclusiveVariantsFromUnconditionalPrerequisites()
    {
        var executorType = typeof(WorkflowPlanExecutor);
        var method = executorType.GetMethod(
            "TryBuildConditionalCompositionBranchValues",
            BindingFlags.Static | BindingFlags.NonPublic);
        var entryType = executorType.GetNestedType("CapabilityCatalogEntry", BindingFlags.NonPublic);
        var bindingType = executorType.GetNestedType("CapabilityRequestBinding", BindingFlags.NonPublic);
        var fieldType = executorType.GetNestedType("CapabilitySchemaField", BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(entryType);
        Assert.NotNull(bindingType);
        Assert.NotNull(fieldType);

        object Bindings(params (string Path, string Value)[] values)
        {
            var result = Array.CreateInstance(bindingType!, values.Length);
            for (var index = 0; index < values.Length; index++)
            {
                result.SetValue(Activator.CreateInstance(
                    bindingType!, values[index].Path, JsonValue.Create(values[index].Value)), index);
            }
            return result;
        }

        object Entry(string id, string methodName, object bindings)
            => Activator.CreateInstance(entryType!,
            [
                id, "mcp", "neutral", "tool", methodName, "Neutral external operation.", bindings, string.Empty,
                Array.CreateInstance(fieldType!, 0), Array.CreateInstance(fieldType!, 0), null, null
            ])!;

        var entries = Array.CreateInstance(entryType!, 4);
        entries.SetValue(Entry("cap_prepare", "finalize_work", Bindings(("/method", "prepare"))), 0);
        entries.SetValue(Entry("cap_note", "add_note", Bindings()), 1);
        entries.SetValue(Entry("cap_accept", "finalize_work", Bindings(("/method", "submit"), ("/state", "accept"))), 2);
        entries.SetValue(Entry("cap_reject", "finalize_work", Bindings(("/method", "submit"), ("/state", "reject"))), 3);
        object?[] arguments = [entries, null];

        var success = Assert.IsType<bool>(method!.Invoke(null, arguments));

        Assert.True(success);
        var branches = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(arguments[1]);
        Assert.Equal(2, branches.Count);
        Assert.Equal("accept", branches["cap_accept"]);
        Assert.Equal("reject", branches["cap_reject"]);
        Assert.DoesNotContain("cap_prepare", branches.Keys);
        Assert.DoesNotContain("cap_note", branches.Keys);
    }

    [Fact]
    public void SharedWriteComposition_RetainsOneOwnedPreparationAndDistinctDependentActions()
    {
        var executorType = typeof(WorkflowPlanExecutor);
        var method = executorType.GetMethod(
            "SelectRetainedSharedWriteOccurrences",
            BindingFlags.Static | BindingFlags.NonPublic);
        var occurrenceType = executorType.GetNestedType("SharedWriteOccurrence", BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(occurrenceType);

        object Occurrence(string operationId, string catalogId, bool owned) =>
            Activator.CreateInstance(occurrenceType!, operationId, catalogId, owned)!;

        var occurrences = Array.CreateInstance(occurrenceType!, 6);
        occurrences.SetValue(Occurrence("op_prepare", "cap_prepare", true), 0);
        occurrences.SetValue(Occurrence("op_comment", "cap_prepare", false), 1);
        occurrences.SetValue(Occurrence("op_submit", "cap_prepare", false), 2);
        occurrences.SetValue(Occurrence("op_comment", "cap_comment", false), 3);
        occurrences.SetValue(Occurrence("op_submit", "cap_accept", true), 4);
        occurrences.SetValue(Occurrence("op_submit", "cap_reject", true), 5);

        var retained = Assert.IsAssignableFrom<IEnumerable>(method!.Invoke(null, [occurrences]))
            .Cast<ITuple>()
            .Select(static tuple => ($"{tuple[0]}", $"{tuple[1]}"))
            .ToHashSet();

        Assert.Contains(("op_prepare", "cap_prepare"), retained);
        Assert.DoesNotContain(("op_comment", "cap_prepare"), retained);
        Assert.DoesNotContain(("op_submit", "cap_prepare"), retained);
        Assert.Contains(("op_comment", "cap_comment"), retained);
        Assert.Contains(("op_submit", "cap_accept"), retained);
        Assert.Contains(("op_submit", "cap_reject"), retained);
    }

    [Theory]
    [InlineData(3, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(0, -1, false)]
    public void CapabilityOwnership_OverridesAdvisoryLocalClassificationOnlyForStrongerNamedOwner(
        int strongestOverall,
        int strongestExternal,
        bool expected)
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "ShouldOverrideAdvisoryLocalClassification",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.Equal(expected, Assert.IsType<bool>(method!.Invoke(null, [strongestOverall, strongestExternal])));
    }

    [Fact]
    public void CapabilityOwnership_DistinguishesCleanupActionFromSharedResourceNouns()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "CountFocusedCapabilityActionFamilyMatches",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var cleanup = Assert.IsType<int>(method!.Invoke(null,
            ["Delete every workflow-created directory.", "Run an allowlisted command.", "cleanup workflow paths"]));
        var materializer = Assert.IsType<int>(method.Invoke(null,
            ["Delete every workflow-created directory.", "Run an allowlisted command.", "clone shared checkout"]));

        Assert.True(cleanup > materializer);
    }

    [Theory]
    [InlineData("Run project verification but do not clone or create another checkout.", "git_clone", "external_effect", "Clone a checkout.", true)]
    [InlineData("Normalize findings locally with no side effects.", "publish_result", "write", "Publish the result.", true)]
    [InlineData("Do not invoke opaque_action in this leaf.", "opaque_action", "external_effect", "Perform an opaque action.", true)]
    [InlineData("Read current state with no external writes.", "read_state", "read", "Read current state.", false)]
    [InlineData("Publish the result without external reads.", "publish_result", "write", "Publish the result.", false)]
    [InlineData("Clone one checkout and run its checks.", "git_clone", "external_effect", "Clone a checkout.", false)]
    public void CapabilityOwnership_RejectsOnlyExplicitlyContradictedCapabilityClaims(
        string intent,
        string methodName,
        string effectKind,
        string capabilityText,
        bool expected)
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "ExplicitlyRejectsCapabilityOwnership",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.Equal(expected, Assert.IsType<bool>(method!.Invoke(
            null,
            [intent, methodName, effectKind, capabilityText])));
    }

    [Fact]
    public void LocalOperationOwnership_RecognizesTransformationActionFamily()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "CountLocalActionFamilyMatches",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var score = Assert.IsType<int>(method!.Invoke(null,
            ["transform normalized findings into typed messages", "transform_findings_to_messages"]));

        Assert.True(score > 0);
    }

    [Fact]
    public void CapabilityOwnership_RecognizesOnlyExactLockedOccurrenceIdentities()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "ContainsLockedCapabilityIdentity",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        static bool Contains(MethodInfo methodInfo, string text, string occurrenceId, string catalogId) =>
            Assert.IsType<bool>(methodInfo.Invoke(null, [text, occurrenceId, catalogId]));

        Assert.True(Contains(
            method!,
            "Call load_record for locked occurrence op_read_records::cap_000042.",
            "op_read_records::cap_000042",
            "cap_000042"));
        Assert.False(Contains(
            method!,
            "This leaf implements the broad op_read_records operation.",
            "op_read_records",
            "cap_000042"));
        Assert.False(Contains(
            method!,
            "Call a different catalog occurrence cap_000043.",
            "op_read_records::cap_000042",
            "cap_000042"));
        Assert.False(Contains(
            method!,
            """
            This leaf must not perform:
            - The final read for op_read_records::cap_000042.
            """,
            "op_read_records::cap_000042",
            "cap_000042"));
        Assert.True(Contains(
            method!,
            """
            This leaf owns exactly these locked capability occurrences:
            - op_read_records::cap_000042: direct MCP call to load_record.
            """,
            "op_read_records::cap_000042",
            "cap_000042"));
    }

    [Fact]
    public void ExternalCallClassification_DoesNotTreatReadOnlyOrWriteOnlyAsNoExternalCalls()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "DeclaresNoExternalCalls",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        static bool IsLocal(MethodInfo methodInfo, string description) =>
            Assert.IsType<bool>(methodInfo.Invoke(null, [description]));

        Assert.False(IsLocal(method!, "Read current state from the configured tool, with no external writes."));
        Assert.False(IsLocal(method!, "Publish the result without external reads."));
        Assert.True(IsLocal(method!, "Perform deterministic local processing with no external calls."));
        Assert.True(IsLocal(method!, "This transform must not call any MCP tool and performs no external work."));
    }

    private const string InvalidDeniedStorageWorkflow = """
        version: 1
        name: generated-storage-delete
        skill:
          description: Delete a configured object.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: remove
                type: mcp.call
                input:
                  server: object-storage
                  kind: tool
                  method: delete_object
                  request:
                    key: sample
        """;

    private const string ValidMultiActionWorkflow = """
        version: 1
        name: generated-inventory
        skill:
          description: Read inventory variants.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: list_items
                type: mcp.call
                input:
                  server: inventory
                  kind: tool
                  method: inventory_read
                  request:
                    method: list_items
              - id: get_status
                type: mcp.call
                input:
                  server: inventory
                  kind: tool
                  method: inventory_read
                  request:
                    method: get_status
        """;

    private const string ValidComposedStorageWorkflow = """
        version: 1
        name: generated-composed-storage
        skill:
          description: Read and remove a configured object.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: load
                type: mcp.call
                input:
                  server: object-storage
                  kind: tool
                  method: get_object
                  request:
                    key: sample
              - id: remove
                type: mcp.call
                input:
                  server: object-storage
                  kind: tool
                  method: delete_object
                  request:
                    key: sample
              - id: normalize
                type: set
                input:
                  status: complete
        """;

    private const string ValidGatedStorageWorkflow = """
        version: 1
        name: generated-gated-storage
        skill:
          description: Load a configured object after confirmation.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: confirm
                type: human.input
                input:
                  mode: confirm
                  prompt: Continue with the external operation?
                  choices: [confirm, cancel]
              - id: load
                type: mcp.call
                input:
                  server: object-storage
                  kind: tool
                  method: get_object
                  request:
                    key: sample
        """;

    private const string ValidGatedStorageWriteWorkflow = """
        version: 1
        name: generated-gated-storage-write
        skill:
          description: Change a configured object after confirmation.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: confirm
                type: human.input
                input:
                  mode: confirm
                  prompt: Continue with the external change?
                  choices: [confirm, cancel]
              - id: remove
                type: mcp.call
                input:
                  server: object-storage
                  kind: tool
                  method: delete_object
                  request:
                    key: sample
        """;

    [Fact]
    public async Task ExplicitPreflight_ResolvesExactAlternativeAndLocksGeneratedCall()
    {
        var llm = ConstantLlm(ValidStorageWorkflow);
        var result = await ExecuteAsync(ExplicitPlan("""
            - id: load_object
              description: Load an object from configured storage.
              required: true
              alternatives:
                - server: object-storage
                  kind: tool
                  method: get_object
            """), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("method: get_object", result.Outputs!["plan"]!["yaml"]!.GetValue<string>(), StringComparison.Ordinal);
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExplicitPreflight_PullRequestReviewReadsMetadataAndDiffBeforeConfirmedWrite()
    {
        const string generatedWorkflow = """
            version: 1
            name: generated-pull-request-review
            skill:
              description: Read and review one pull request.
              tags: [generated, review]
              inputs:
                pull_request_url:
                  type: string
                  default: https://example.invalid/sample/repository/pull/17
              outputs:
                state: string
                base_ref: string
                head_ref: string
                diff: string
            functions: |
              /**
               * Parses and validates an absolute pull request URL.
               *
               * @param {string} url - Pull request URL.
               * @returns {object} URL-derived owner, repository, pull number, and normalized URL.
               */
              function parsePullRequestUrl(url) {
                var normalized = String(url || "").replace(/\/$/, "");
                var parts = normalized.split("/");
                if (parts.length < 7 || parts[parts.length - 2] !== "pull") {
                  throw new Error("Invalid pull request URL.");
                }
                var pullNumber = Number(parts[parts.length - 1]);
                if (!Number.isInteger(pullNumber) || pullNumber < 1) {
                  throw new Error("Invalid pull request number.");
                }
                return {
                  owner: parts[parts.length - 4],
                  repository: parts[parts.length - 3],
                  pull_number: pullNumber,
                  normalized_url: normalized
                };
              }
            workflows:
              main:
                inputs:
                  pull_request_url:
                    type: string
                    default: https://example.invalid/sample/repository/pull/17
                steps:
                  - id: validate_url
                    type: set
                    input:
                      owner: "${functions.parsePullRequestUrl(data.inputs.pull_request_url).owner}"
                      repository: "${functions.parsePullRequestUrl(data.inputs.pull_request_url).repository}"
                      pull_number: "${functions.parsePullRequestUrl(data.inputs.pull_request_url).pull_number}"
                      normalized_url: "${functions.parsePullRequestUrl(data.inputs.pull_request_url).normalized_url}"
                    output_schema:
                      type: object
                      additionalProperties: false
                      required: [owner, repository, pull_number, normalized_url]
                      properties:
                        owner: { type: string }
                        repository: { type: string }
                        pull_number: { type: integer, minimum: 1 }
                        normalized_url: { type: string }
                  - id: read_metadata
                    type: mcp.call
                    input:
                      server: review-data
                      kind: tool
                      method: pull_request_read
                      request:
                        method: get
                        owner: ${data.steps.validate_url.owner}
                        repository: ${data.steps.validate_url.repository}
                        pull_number: ${data.steps.validate_url.pull_number}
                  - id: read_diff
                    type: mcp.call
                    input:
                      server: review-data
                      kind: tool
                      method: pull_request_read
                      request:
                        method: get_diff
                        owner: ${data.steps.validate_url.owner}
                        repository: ${data.steps.validate_url.repository}
                        pull_number: ${data.steps.validate_url.pull_number}
                  - id: build_context
                    type: set
                    input:
                      state: ${data.steps.read_metadata.response.state}
                      base_ref: ${data.steps.read_metadata.response.base_ref}
                      head_ref: ${data.steps.read_metadata.response.head_ref}
                      diff: ${data.steps.read_diff.response.diff}
                    output_schema:
                      type: object
                      additionalProperties: false
                      required: [state, base_ref, head_ref, diff]
                      properties:
                        state: { type: string }
                        base_ref: { type: string }
                        head_ref: { type: string }
                        diff: { type: string }
                  - id: confirm_write
                    type: human.input
                    input:
                      mode: confirm
                      prompt: Publish the validated review comment?
                      choices: [confirm, cancel]
                  - id: publish_comment
                    type: mcp.call
                    input:
                      server: review-data
                      kind: tool
                      method: pull_request_write
                      request:
                        method: comment
                        owner: ${data.steps.validate_url.owner}
                        repository: ${data.steps.validate_url.repository}
                        pull_number: ${data.steps.validate_url.pull_number}
                        body: ${data.steps.build_context.diff}
                outputs:
                  state:
                    expr: ${data.steps.build_context.state}
                    type: string
                  base_ref:
                    expr: ${data.steps.build_context.base_ref}
                    type: string
                  head_ref:
                    expr: ${data.steps.build_context.head_ref}
                    type: string
                  diff:
                    expr: ${data.steps.build_context.diff}
                    type: string
            """;
        var plan = ExplicitPlan("""
            - id: read_metadata
              description: Read pull request metadata and refs.
              required: true
              alternatives:
                - server: review-data
                  kind: tool
                  method: pull_request_read
                  request_bindings:
                    - path: /method
                      value: get
            - id: read_diff
              description: Read pull request diff content.
              required: true
              alternatives:
                - server: review-data
                  kind: tool
                  method: pull_request_read
                  request_bindings:
                    - path: /method
                      value: get_diff
            - id: publish_comment
              description: Publish the review comment after confirmation.
              required: true
              alternatives:
                - server: review-data
                  kind: tool
                  method: pull_request_write
                  request_bindings:
                    - path: /method
                      value: comment
            """)
            .Replace("validate:\n        max_repair_attempts: 3", "validate:\n        dry_run: true\n        max_repair_attempts: 1", StringComparison.Ordinal);

        var result = await ExecuteAsync(
            plan,
            ConstantLlm(generatedWorkflow).Object,
            CreatePullRequestReviewFactory());

        Assert.True(result.Success, result.Error?.Message);
        var yaml = result.Outputs!["plan"]!["yaml"]!.GetValue<string>();
        Assert.Single(Regex.Matches(yaml, "method: get$", RegexOptions.Multiline).Cast<System.Text.RegularExpressions.Match>());
        Assert.Single(Regex.Matches(yaml, "method: get_diff$", RegexOptions.Multiline).Cast<System.Text.RegularExpressions.Match>());
        var generated = WorkflowParser.Parse(yaml);
        var contextInput = Assert.IsType<JsonObject>(generated.Workflows["main"].Steps
            .Single(static step => step.Id == "build_context").Input);
        Assert.Equal("${data.steps.read_metadata.response.base_ref}", contextInput["base_ref"]!.GetValue<string>());
        Assert.Equal("${data.steps.read_metadata.response.head_ref}", contextInput["head_ref"]!.GetValue<string>());
        Assert.Equal("${data.steps.read_diff.response.diff}", contextInput["diff"]!.GetValue<string>());
        Assert.True(
            yaml.IndexOf("id: confirm_write", StringComparison.Ordinal)
            < yaml.IndexOf("id: publish_comment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitPreflight_UnavailableRequiredOperationFailsBeforeGeneration()
    {
        var llm = new Mock<ILLMClient>(MockBehavior.Strict);
        var result = await ExecuteAsync(ExplicitPlan("""
            - id: archive_object
              description: Archive an object permanently.
              required: true
              alternatives:
                - server: object-storage
                  kind: tool
                  method: archive_object
            """), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExplicitPreflight_OptionalUnavailableOperationDoesNotBlockGeneration()
    {
        var llm = ConstantLlm(ValidTemplateWorkflow);
        var result = await ExecuteAsync(ExplicitPlan("""
            - id: send_notification
              description: Send an optional completion notification.
              required: false
              alternatives:
                - server: messaging
                  kind: tool
                  method: send_message
            """), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExplicitPreflight_RejectsGeneratedWorkflowThatOmitsLockedCapability()
    {
        var llm = ConstantLlm(ValidTemplateWorkflow);
        var plan = ExplicitPlan("""
            - id: load_object
              description: Load an object from configured storage.
              required: true
              alternatives:
                - server: object-storage
                  kind: tool
                  method: get_object
            """).Replace("max_repair_attempts: 3", "max_repair_attempts: 1", StringComparison.Ordinal);

        var result = await ExecuteAsync(plan, llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Contains("omitted", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExplicitPreflight_TreatsRepeatedExactCapabilityAsDistinctInvocations()
    {
        var llm = ConstantLlm(ValidStorageWorkflow);
        var plan = ExplicitPlan("""
            - id: load_primary
              description: Load the primary object.
              required: true
              alternatives:
                - server: object-storage
                  kind: tool
                  method: get_object
            - id: load_secondary
              description: Load the secondary object separately.
              required: true
              alternatives:
                - server: object-storage
                  kind: tool
                  method: get_object
            """).Replace("max_repair_attempts: 3", "max_repair_attempts: 1", StringComparison.Ordinal);

        var result = await ExecuteAsync(plan, llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Contains("load_secondary", result.Error.Details!["unavailable_capabilities"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitPreflight_DoesNotCombinePartsFromDifferentAlternatives()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("storage-a", new MockMcpServerConfig
        {
            Tools = [new McpToolInfo { Name = "send_message" }]
        });
        factory.RegisterServer("messaging-b", new MockMcpServerConfig
        {
            Tools = [new McpToolInfo { Name = "get_object" }]
        });
        var llm = new Mock<ILLMClient>(MockBehavior.Strict);

        var result = await ExecuteAsync(ExplicitPlan("""
            - id: load_or_notify
              description: Perform one exact supported operation.
              required: true
              alternatives:
                - server: storage-a
                  kind: tool
                  method: get_object
                - server: messaging-b
                  kind: tool
                  method: send_message
            """), llm.Object, factory);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExplicitPreflight_SelectorBindingRequiresMatchingLiteralRequestValue()
    {
        var plan = ExplicitPlan("""
            - id: list_items
              description: List inventory items.
              required: true
              alternatives:
                - server: inventory
                  kind: tool
                  method: inventory_read
                  request_bindings:
                    - path: /method
                      value: list_items
            """)
            .Replace("Load an object and produce the requested result.", "List inventory items.", StringComparison.Ordinal)
            .Replace("max_repair_attempts: 3", "max_repair_attempts: 1", StringComparison.Ordinal);
        var workflowWithWrongSelector = ValidMultiActionWorkflow.Replace("method: list_items", "method: get_status", StringComparison.Ordinal);
        var llm = ConstantLlm(workflowWithWrongSelector);

        var result = await ExecuteAsync(plan, llm.Object, CreateMultiActionFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Contains("omitted", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitPreflight_DynamicSelectorCannotSatisfyLockedBinding()
    {
        var plan = ExplicitPlan("""
            - id: list_items
              description: List inventory items.
              required: true
              alternatives:
                - server: inventory
                  kind: tool
                  method: inventory_read
                  request_bindings:
                    - path: /method
                      value: list_items
            """).Replace("max_repair_attempts: 3", "max_repair_attempts: 1", StringComparison.Ordinal);
        var dynamicWorkflow = ValidMultiActionWorkflow.Replace("method: list_items", "method: \"${data.inputs.selector}\"", StringComparison.Ordinal);
        var llm = ConstantLlm(dynamicWorkflow);

        var result = await ExecuteAsync(plan, llm.Object, CreateMultiActionFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Contains("MCP_REQUEST_SELECTOR_NOT_LITERAL", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitPreflight_RejectsUndocumentedSelectorBinding()
    {
        var llm = new Mock<ILLMClient>(MockBehavior.Strict);
        var result = await ExecuteAsync(ExplicitPlan("""
            - id: unsupported_variant
              description: Invoke an undocumented inventory variant.
              required: true
              alternatives:
                - server: inventory
                  kind: tool
                  method: inventory_read
                  request_bindings:
                    - path: /method
                      value: remove_everything
            """), llm.Object, CreateMultiActionFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Contains("documented scalar selectors", result.Error.Message, StringComparison.Ordinal);
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExplicitPreflight_AllowsOneMaterializerToFeedMultipleConsumers()
    {
        var result = await ExecuteAsync(
            WorkspacePlan(includeSecondMaterializerRequirement: false),
            ConstantLlm(ValidWorkspaceWorkflow).Object,
            CreateArtifactFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task ExplicitPreflight_PreservesArtifactProvenanceAcrossTypedWorkflowBoundaries()
    {
        var result = await ExecuteAsync(
            WorkspacePlan(includeSecondMaterializerRequirement: false),
            ConstantLlm(CrossWorkflowWorkspaceWorkflow(
                "${data.steps.produce.outputs.project_root}",
                "${data.steps.produce.outputs.project_root}")).Object,
            CreateArtifactFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task ExplicitPreflight_ReportsUnprovenCrossWorkflowArtifactCallerBinding()
    {
        var result = await ExecuteAsync(
            WorkspacePlan(includeSecondMaterializerRequirement: false),
            ConstantLlm(CrossWorkflowWorkspaceWorkflow(
                "workspaces/invented-directory",
                "${data.steps.produce.outputs.project_root}")).Object,
            CreateArtifactFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Equal("mcp_artifact_dataflow", result.Error.Details!["phase"]!.GetValue<string>());
        var diagnostics = Assert.IsType<JsonArray>(result.Error.Details["diagnostics"]);
        var diagnostic = Assert.Single(
            diagnostics.OfType<JsonObject>(),
            static item => string.Equals(
                item["workflow"]?.GetValue<string>(),
                "inspect_workspace",
                StringComparison.Ordinal));
        var binding = Assert.Single(Assert.IsType<JsonArray>(diagnostic["caller_bindings"]));
        var bindingObject = Assert.IsType<JsonObject>(binding);
        Assert.Equal("main", bindingObject["caller_workflow"]!.GetValue<string>());
        Assert.Equal("inspect", bindingObject["caller_step"]!.GetValue<string>());
        Assert.Equal("project_root", bindingObject["argument_path"]!.GetValue<string>());
        Assert.Equal("workspaces/invented-directory", bindingObject["argument_value"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExplicitPreflight_RejectsInventedArtifactConsumerValue()
    {
        var workflow = ValidWorkspaceWorkflow.Replace(
            "${data.steps.materialize.response.projectRootRelative}",
            "workflows/invented-directory",
            StringComparison.Ordinal);
        var result = await ExecuteAsync(
            WorkspacePlan(includeSecondMaterializerRequirement: false),
            ConstantLlm(workflow).Object,
            CreateArtifactFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Equal("mcp_artifact_dataflow", result.Error.Details!["phase"]!.GetValue<string>());
        Assert.Equal("unproven_artifact_provenance", result.Error.Details["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExplicitPreflight_RejectsTransformedArtifactConsumerValue()
    {
        var workflow = ValidWorkspaceWorkflow.Replace(
            "${data.steps.materialize.response.projectRootRelative}",
            "${toString(data.steps.materialize.response.projectRootRelative)}",
            StringComparison.Ordinal);
        var result = await ExecuteAsync(
            WorkspacePlan(includeSecondMaterializerRequirement: false),
            ConstantLlm(workflow).Object,
            CreateArtifactFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Equal("mcp_artifact_dataflow", result.Error.Details!["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExplicitPreflight_RejectsIncompatibleArtifactKinds()
    {
        var result = await ExecuteAsync(
            WorkspacePlan(includeSecondMaterializerRequirement: false),
            ConstantLlm(ValidWorkspaceWorkflow).Object,
            CreateArtifactFactory(consumerArtifactKind: "workspace.archive"));

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TemplatePlan, result.Error!.Code);
        Assert.Equal("mcp_artifact_dataflow", result.Error.Details!["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExplicitPreflight_RejectsUnplannedSecondMaterializer()
    {
        var workflow = ValidWorkspaceWorkflow.Replace(
            "      - id: inspect",
            """
                  - id: materialize_again
                    type: mcp.call
                    input:
                      server: workspace-provider
                      kind: tool
                      method: create_workspace
                      request:
                        sourceUrl: https://example.invalid/source
                  - id: inspect
            """,
            StringComparison.Ordinal);
        var result = await ExecuteAsync(
            WorkspacePlan(includeSecondMaterializerRequirement: false),
            ConstantLlm(workflow).Object,
            CreateArtifactFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightRedundantArtifactProducer, result.Error!.Code);
        Assert.Equal("redundant_artifact_materializer", result.Error.Details!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExplicitPreflight_AllowsTwoLockedMaterializerOccurrences()
    {
        var workflow = ValidWorkspaceWorkflow.Replace(
            "      - id: inspect",
            """
                  - id: materialize_second_source
                    type: mcp.call
                    input:
                      server: workspace-provider
                      kind: tool
                      method: create_workspace
                      request:
                        sourceUrl: https://example.invalid/second-source
                  - id: inspect
            """,
            StringComparison.Ordinal);
        var result = await ExecuteAsync(
            WorkspacePlan(includeSecondMaterializerRequirement: true),
            ConstantLlm(workflow).Object,
            CreateArtifactFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task InferredPreflight_RejectsInvalidExplicitArtifactPointerBeforeGeneration()
    {
        var factory = CreateArtifactFactory(invalidProducerPointer: true);
        var llm = new Mock<ILLMClient>(MockBehavior.Strict);

        var result = await ExecuteAsync(InferredPlan(), llm.Object, factory);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal("mcp_artifact_contract", result.Error.Details!["phase"]!.GetValue<string>());
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InferredPreflight_ExpandsOneToolIntoDistinctSelectorCapabilities()
    {
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponse(
                        ("list_items", "List inventory items.", true),
                        ("get_status", "Read inventory status.", true));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return MatchResponse(
                        ("list_items", "mcp", CatalogIdForBinding(request.Prompt, "/method", "list_items")),
                        ("get_status", "mcp", CatalogIdForBinding(request.Prompt, "/method", "get_status")));
                }
                return new LLMResponse { Text = ValidMultiActionWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateMultiActionFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, prompts.Count);
        Assert.Contains("request_bindings=[/method=\"list_items\"]", prompts[1], StringComparison.Ordinal);
        Assert.Contains("request_bindings=[/method=\"get_status\"]", prompts[1], StringComparison.Ordinal);
        Assert.Contains("variant_of=inventory/tool/inventory_read", prompts[1], StringComparison.Ordinal);
        Assert.Single(Regex.Matches(prompts[1], "description=Perform one documented inventory read operation\\.")
            .Cast<System.Text.RegularExpressions.Match>());
        Assert.Contains("/method=\"list_items\"", prompts[2], StringComparison.Ordinal);
        Assert.Contains("/method=\"get_status\"", prompts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_CanonicalizesRedundantBaseAndExactSelectorWithoutIntentKeywords()
    {
        var matcherCalls = 0;
        var telemetry = new Mock<IWorkflowTelemetry>();
        var workflowSpan = new Mock<IWorkflowSpan>();
        var stepSpan = new Mock<IStepSpan>();
        var internalSpan = new Mock<ITelemetrySpan>();
        var normalizationEvents = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<ITelemetrySpan>(), It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.StepStart(It.IsAny<ITelemetrySpan>(), It.IsAny<StepTelemetryInfo>()))
            .Returns(stepSpan.Object);
        telemetry.Setup(value => value.SpanStart(It.IsAny<ITelemetrySpan>(), It.IsAny<TelemetrySpanInfo>()))
            .Returns(internalSpan.Object);
        internalSpan.Setup(value => value.AddEvent(
                "gnougo-flow.plan.capability_matching.normalization",
                It.IsAny<IReadOnlyList<KeyValuePair<string, object?>>?>()))
            .Callback((string _, IReadOnlyList<KeyValuePair<string, object?>>? attributes) =>
                normalizationEvents.Add(attributes ?? []));
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("cleanup", "Clean up the temporary checkout created for the review.", true, "external_effect", "lifecycle"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    Assert.Contains(
                        "request_bindings=[/action=\"delete_directory\"] description=Delete one existing directory recursively.",
                        request.Prompt,
                        StringComparison.Ordinal);
                    return MatchingResponse((
                        "cleanup",
                        "matched",
                        [
                            CatalogIdForWholeTool(request.Prompt, "run_allowed_action"),
                            CatalogIdForBinding(request.Prompt, "/action", "delete_directory")
                        ],
                        Array.Empty<string>(),
                        "The exact bound variant and its inherited physical card describe one operation."));
                }
                return new LLMResponse
                {
                    Text = """
                        version: 1
                        name: generated-cleanup
                        skill:
                          description: Clean up a temporary checkout.
                          tags: [generated]
                          inputs: {}
                          outputs: {}
                        workflows:
                          main:
                            steps:
                              - id: cleanup
                                type: mcp.call
                                input:
                                  server: command-runner
                                  kind: tool
                                  method: run_allowed_action
                                  request:
                                    action: delete_directory
                        """
                };
            });

        var plan = InferredPlan().Replace(
            "Load a configured object and optionally notify a consumer.",
            "Clean up the temporary checkout created for the review.",
            StringComparison.Ordinal);
        var result = await ExecuteAsync(
            plan,
            llm.Object,
            CreateCleanupSelectorFactory(),
            telemetry: telemetry.Object);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matcherCalls);
        var capability = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"])));
        var binding = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(capability["request_bindings"])));
        Assert.Equal("/action", binding["path"]!.GetValue<string>());
        Assert.Equal("delete_directory", binding["value"]!.GetValue<string>());
        var telemetryAttributes = Assert.Single(normalizationEvents)
            .ToDictionary(static item => item.Key, static item => item.Value);
        Assert.Equal("cleanup", telemetryAttributes["gnougo-flow.plan.capability_matching.operation_id"]);
        Assert.Equal(
            "selector_base_variant_canonicalized",
            telemetryAttributes["gnougo-flow.plan.capability_matching.reason_code"]);
        Assert.DoesNotContain(
            telemetryAttributes.Values,
            static value => string.Equals(value as string, "Clean up the temporary checkout created for the review.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InferredPreflight_RejectsUnknownCatalogId()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
                request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal)
                    ? InventoryResponse(("list_items", "List inventory items.", true))
                    : MatchResponse(("list_items", "mcp", "cap_999999")));

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateMultiActionFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Contains("ambiguous or invalid", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        var issue = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(result.Error.Details!["matching_issues"])));
        Assert.Equal("list_items", issue["operation_id"]!.GetValue<string>());
        Assert.Equal("invalid", issue["status"]!.GetValue<string>());
        Assert.DoesNotContain("cap_999999", issue.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_CatalogIncludesNestedAndComposedSelectors()
    {
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse();
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                    return MatchResponse();
                return new LLMResponse { Text = ValidTemplateWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateComposedSelectorFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("/request/action=\"lookup\"", prompts[1], StringComparison.Ordinal);
        Assert.Contains("/request/action=\"search\"", prompts[1], StringComparison.Ordinal);
        Assert.Contains("/mode=\"read\"", prompts[1], StringComparison.Ordinal);
        Assert.Contains("/mode=\"write\"", prompts[1], StringComparison.Ordinal);
        Assert.DoesNotContain("resolution=native method=loop.parallel", prompts[1], StringComparison.Ordinal);
        Assert.Contains("resolution=native method=human.input", prompts[1], StringComparison.Ordinal);
        Assert.Contains("description=Destination record locator.", prompts[1], StringComparison.Ordinal);
        Assert.Contains("when /mode=\"write\" require /payload", prompts[1], StringComparison.Ordinal);
        Assert.Contains("when /target is present require /offset", prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitPreflight_SelectorAwareConstraintDoesNotDenyOtherVariant()
    {
        var llm = ConstantLlm(ValidMultiActionWorkflow.Replace("method: list_items", "method: get_status", StringComparison.Ordinal));
        var result = await ExecuteAsync("""
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      mode: basic
                      capability_preflight:
                        mode: explicit
                        requirements:
                          - id: get_status
                            description: Read inventory status.
                            required: true
                            alternatives:
                              - server: inventory
                                kind: tool
                                method: inventory_read
                                request_bindings:
                                  - path: /method
                                    value: get_status
                        constraints:
                          - id: never_list
                            description: Do not list inventory items.
                            required: true
                            denied_alternatives:
                              - server: inventory
                                kind: tool
                                method: inventory_read
                                request_bindings:
                                  - path: /method
                                    value: list_items
                      generator:
                        model: gpt-4
                        prefilter: false
                        instruction: Read inventory status without listing items.
            """, llm.Object, CreateMultiActionFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task InferredPreflight_FailsBeforeLlmWhenSelectorValueLimitIsExceeded()
    {
        var values = new JsonArray();
        for (var index = 0; index < 65; index++)
            values.Add((JsonNode?)JsonValue.Create($"action_{index:D2}"));
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("inventory", new MockMcpServerConfig
        {
            Tools = [new McpToolInfo
            {
                Name = "inventory_action",
                InputSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["action"] = new JsonObject { ["type"] = "string", ["enum"] = values }
                    }
                }
            }]
        });
        var llm = new Mock<ILLMClient>(MockBehavior.Strict);
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(InventoryResponse());

        var result = await ExecuteAsync(InferredPlan(), llm.Object, factory);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal("selector_value_limit_exceeded", result.Error.Details!["reason"]!.GetValue<string>());
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InferredPreflight_FailsInsteadOfTruncatingOversizedCatalog()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("large-inventory", new MockMcpServerConfig
        {
            Tools = Enumerable.Range(0, 600).Select(index => new McpToolInfo
            {
                Name = $"inventory_action_{index:D4}",
                Description = new string('x', 512)
            }).ToList()
        });
        var llm = new Mock<ILLMClient>(MockBehavior.Strict);
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(InventoryResponse());

        var result = await ExecuteAsync(InferredPlan(), llm.Object, factory);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal("catalog_too_large", result.Error.Details!["reason"]!.GetValue<string>());
        Assert.True(result.Error.Details["total_characters"]!.GetValue<int>() > 256_000);
        Assert.Equal(600, result.Error.Details["selected_tool_count"]!.GetValue<int>());
        Assert.Equal(600, result.Error.Details["full_tool_count"]!.GetValue<int>());
        Assert.NotEmpty(Assert.IsType<JsonArray>(result.Error.Details["largest_contributors"]));
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InferredPreflight_FiltersOversizedIrrelevantCatalogBeforeSchemaExpansion()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("large-inventory", new MockMcpServerConfig
        {
            Tools = Enumerable.Range(0, 600).Select(index => new McpToolInfo
            {
                Name = $"inventory_action_{index:D4}",
                Description = new string('x', 512)
            }).ToList()
        });
        var storage = CreateNeutralFactory();
        foreach (var server in storage.ServerMetadata!)
        {
            var client = await storage.GetClientAsync(server.Name, CancellationToken.None);
            var tools = await client.ListToolsAsync(CancellationToken.None);
            factory.RegisterServer(server.Name, new MockMcpServerConfig
            {
                Description = server.Description,
                Tools = tools.ToList()
            });
        }

        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("load_object", "Load the requested object.", true));
                if (request.Prompt.Contains("physical capability candidate selector", StringComparison.Ordinal))
                {
                    var ids = request.Prompt.Contains(" method=get_object", StringComparison.Ordinal)
                        ? new[] { CatalogIdForMethod(request.Prompt, "get_object") }
                        : Array.Empty<string>();
                    return PhysicalCandidateResponse(("load_object", ids));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                    return MatchResponse(("load_object", "mcp", CatalogIdForMethod(request.Prompt, "get_object")));
                if (request.Prompt.Contains("tool-selection assistant", StringComparison.OrdinalIgnoreCase))
                    return McpPrefilterResponse(("object-storage", new[] { "get_object" }));
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(
            InferredPlan().Replace("prefilter: false", "prefilter: true", StringComparison.Ordinal),
            llm.Object,
            factory);

        Assert.True(result.Success, result.Error?.Message);
        var selectorPrompts = prompts.Where(prompt => prompt.Contains("physical capability candidate selector", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(selectorPrompts);
        Assert.DoesNotContain(selectorPrompts, prompt => prompt.Contains("request_bindings=", StringComparison.Ordinal));
        var matchingPrompt = Assert.Single(prompts, prompt => prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal));
        Assert.Contains("method=get_object", matchingPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("method=inventory_action_", matchingPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_PhysicalCardsExposeBoundedSelectorValues()
    {
        string? selectorPrompt = null;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("list_items", "List inventory items.", true));
                if (request.Prompt.Contains("physical capability candidate selector", StringComparison.Ordinal))
                {
                    selectorPrompt = request.Prompt;
                    return PhysicalCandidateResponse(("list_items", new[]
                    {
                        CatalogIdForMethod(request.Prompt, "inventory_read")
                    }));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return MatchResponse((
                        "list_items",
                        "mcp",
                        CatalogIdForBinding(request.Prompt, "/method", "list_items")));
                }
                return new LLMResponse
                {
                    Text = """
                        version: 1
                        name: generated-inventory-list
                        skill:
                          description: List inventory items.
                          tags: [generated]
                          inputs: {}
                          outputs: {}
                        workflows:
                          main:
                            steps:
                              - id: list_items
                                type: mcp.call
                                input:
                                  server: inventory
                                  kind: tool
                                  method: inventory_read
                                  request:
                                    method: list_items
                        """
                };
            });

        var result = await ExecuteAsync(
            InferredPlan().Replace("prefilter: false", "prefilter: true", StringComparison.Ordinal),
            llm.Object,
            CreateMultiActionFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(selectorPrompt);
        Assert.Contains("/method:string(required){allowed=\"list_items\"|\"get_status\"}", selectorPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_RecoversExactSelectorWhenCandidateModelOmitsIt()
    {
        var selectorCalls = 0;
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("list_items", "List inventory items.", true));
                if (request.Prompt.Contains("physical capability candidate selector", StringComparison.Ordinal))
                {
                    selectorCalls++;
                    return PhysicalCandidateResponse(("list_items", Array.Empty<string>()));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    return MatchResponse((
                        "list_items",
                        "mcp",
                        CatalogIdForBinding(request.Prompt, "/method", "list_items")));
                }
                return new LLMResponse
                {
                    Text = """
                        version: 1
                        name: generated-inventory-list
                        skill:
                          description: List inventory items.
                          tags: [generated]
                          inputs: {}
                          outputs: {}
                        workflows:
                          main:
                            steps:
                              - id: list_items
                                type: mcp.call
                                input:
                                  server: inventory
                                  kind: tool
                                  method: inventory_read
                                  request:
                                    method: list_items
                        """
                };
            });

        var result = await ExecuteAsync(
            InferredPlan().Replace("prefilter: false", "prefilter: true", StringComparison.Ordinal),
            llm.Object,
            CreateMultiActionFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, selectorCalls);
        Assert.Equal(1, matcherCalls);
    }

    [Fact]
    public async Task InferredPreflight_RepairsMultipleSelectorsWithoutTextualInference()
    {
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("list_items", "List inventory items.", true));
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    return matcherCalls == 1
                        ? MatchingResponse((
                            "list_items",
                            "matched",
                            new[]
                            {
                                CatalogIdForBinding(request.Prompt, "/method", "list_items"),
                                CatalogIdForBinding(request.Prompt, "/method", "get_status")
                            },
                            Array.Empty<string>(),
                            "Both selectors were placed in one composition."))
                        : MatchingResponse((
                            "list_items",
                            "matched",
                            [CatalogIdForBinding(request.Prompt, "/method", "list_items")],
                            Array.Empty<string>(),
                            "The repaired match retains only the exact documented selector."));
                }
                return new LLMResponse
                {
                    Text = """
                        version: 1
                        name: generated-inventory-list
                        skill:
                          description: List inventory items.
                          tags: [generated]
                          inputs: {}
                          outputs: {}
                        workflows:
                          main:
                            steps:
                              - id: list_items
                                type: mcp.call
                                input:
                                  server: inventory
                                  kind: tool
                                  method: inventory_read
                                  request:
                                    method: list_items
                        """
                };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateMultiActionFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, matcherCalls);
        var capability = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"])));
        Assert.Equal("list_items", Assert.Single(Assert.IsType<JsonArray>(
            capability["request_bindings"]))!.AsObject()["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_RepairsBaseToolCandidateToExactSelector()
    {
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("list_items", "List inventory items.", true));
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    return matcherCalls == 1
                        ? MatchingResponse((
                            "list_items",
                            "composed",
                            [CatalogIdForWholeTool(request.Prompt, "inventory_read")],
                            Array.Empty<string>(),
                            "The base physical tool was incorrectly returned as a one-member composition."))
                        : MatchingResponse((
                            "list_items",
                            "matched",
                            [CatalogIdForBinding(request.Prompt, "/method", "list_items")],
                            Array.Empty<string>(),
                            "The repaired match selects the exact documented variant."));
                }
                return new LLMResponse
                {
                    Text = """
                        version: 1
                        name: generated-inventory-list
                        skill:
                          description: List inventory items.
                          tags: [generated]
                          inputs: {}
                          outputs: {}
                        workflows:
                          main:
                            steps:
                              - id: list_items
                                type: mcp.call
                                input:
                                  server: inventory
                                  kind: tool
                                  method: inventory_read
                                  request:
                                    method: list_items
                        """
                };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateMultiActionFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, matcherCalls);
        var capability = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"])));
        Assert.Equal("list_items", Assert.Single(Assert.IsType<JsonArray>(
            capability["request_bindings"]))!.AsObject()["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_StillRejectsGenuinelyOversizedSelectedSchema()
    {
        static JsonArray Values(string prefix) => new(Enumerable.Range(0, 64)
            .Select(index => (JsonNode?)JsonValue.Create($"{prefix}_{index:D2}"))
            .ToArray());

        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("selected-service", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "selected_action",
                    Description = "Perform the selected operation.",
                    InputSchema = new JsonObject
                    {
                        ["type"] = "object",
                        ["oneOf"] = new JsonArray(new JsonObject
                        {
                            ["properties"] = new JsonObject
                            {
                                ["left"] = new JsonObject { ["type"] = "string", ["enum"] = Values("left") },
                                ["right"] = new JsonObject { ["type"] = "string", ["enum"] = Values("right") }
                            }
                        })
                    }
                }
            ]
        });

        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("selected_operation", "Perform the selected operation.", true));
                if (request.Prompt.Contains("physical capability candidate selector", StringComparison.Ordinal))
                {
                    return PhysicalCandidateResponse(("selected_operation", new[]
                    {
                        CatalogIdForMethod(request.Prompt, "selected_action")
                    }));
                }
                throw new InvalidOperationException("Matching must not run for an oversized selected schema.");
            });

        var result = await ExecuteAsync(
            InferredPlan().Replace("prefilter: false", "prefilter: true", StringComparison.Ordinal),
            llm.Object,
            factory);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal("catalog_too_large", result.Error.Details!["reason"]!.GetValue<string>());
        Assert.Equal(1, result.Error.Details["selected_tool_count"]!.GetValue<int>());
        Assert.Equal(1, result.Error.Details["full_tool_count"]!.GetValue<int>());
        Assert.True(result.Error.Details["variant_count"]!.GetValue<int>() >= 4_096);
    }

    [Fact]
    public async Task InferredPreflight_RepairsOneOmittedPhysicalCandidateAgainstCompactCatalog()
    {
        var selectorCalls = 0;
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("load_object", "Load the requested object.", true));
                if (request.Prompt.Contains("physical capability candidate selector", StringComparison.Ordinal))
                {
                    selectorCalls++;
                    return selectorCalls == 1
                        ? PhysicalCandidateResponse(("load_object", Array.Empty<string>()))
                        : PhysicalCandidateResponse(("load_object", new[] { CatalogIdForMethod(request.Prompt, "get_object") }));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                    return MatchResponse(("load_object", "mcp", CatalogIdForMethod(request.Prompt, "get_object")));
                if (request.Prompt.Contains("tool-selection assistant", StringComparison.OrdinalIgnoreCase))
                    return McpPrefilterResponse(("object-storage", new[] { "get_object" }));
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(
            InferredPlan().Replace("prefilter: false", "prefilter: true", StringComparison.Ordinal),
            llm.Object,
            CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, selectorCalls);
        Assert.Contains(prompts, prompt => prompt.Contains("single bounded repair pass", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InferredPreflight_RepairsOmittedRequiredExactDenialCandidateAgainstCompactCatalog()
    {
        var selectorCalls = 0;
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithConstraints(
                        [("load_object", "Load the requested object.", true)],
                        [("never_delete", "Never delete stored objects.", true, "exact_denial")]);
                }
                if (request.Prompt.Contains("physical capability candidate selector", StringComparison.Ordinal))
                {
                    selectorCalls++;
                    return selectorCalls == 1
                        ? PhysicalCandidateResponseWithConstraints(
                            [("load_object", [CatalogIdForMethod(request.Prompt, "get_object")])],
                            [("never_delete", Array.Empty<string>())])
                        : PhysicalCandidateResponseWithConstraints(
                            Array.Empty<(string, string[])>(),
                            [("never_delete", [CatalogIdForMethod(request.Prompt, "delete_object")])]);
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return MatchResponseWithConstraints(
                        [("load_object", "mcp", CatalogIdForMethod(request.Prompt, "get_object"))],
                        [("never_delete", [CatalogIdForMethod(request.Prompt, "delete_object")])]);
                }
                if (request.Prompt.Contains("tool-selection assistant", StringComparison.OrdinalIgnoreCase))
                    return McpPrefilterResponse(("object-storage", ["get_object"]));
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(
            InferredPlan().Replace("prefilter: false", "prefilter: true", StringComparison.Ordinal),
            llm.Object,
            CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, selectorCalls);
        var repairPrompt = Assert.Single(prompts, prompt => prompt.Contains("single bounded repair pass", StringComparison.Ordinal));
        Assert.Contains("never_delete", repairPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_DoesNotDemandPhysicalCandidateForWorkflowPolicyConstraint()
    {
        var selectorCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithConstraints(
                        [("load_object", "Load the requested object.", true)],
                        [("load_once", "Load the object exactly once.", true, "workflow_policy")]);
                }
                if (request.Prompt.Contains("physical capability candidate selector", StringComparison.Ordinal))
                {
                    selectorCalls++;
                    return PhysicalCandidateResponseWithConstraints(
                        [("load_object", [CatalogIdForMethod(request.Prompt, "get_object")])],
                        [("load_once", Array.Empty<string>())]);
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return MatchResponseWithConstraints(
                        [("load_object", "mcp", CatalogIdForMethod(request.Prompt, "get_object"))],
                        [("load_once", Array.Empty<string>())]);
                }
                if (request.Prompt.Contains("tool-selection assistant", StringComparison.OrdinalIgnoreCase))
                    return McpPrefilterResponse(("object-storage", ["get_object"]));
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(
            InferredPlan().Replace("prefilter: false", "prefilter: true", StringComparison.Ordinal),
            llm.Object,
            CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, selectorCalls);
    }

    [Fact]
    public async Task InferredPreflight_ReportsUnavailableCleanupOnlyAfterCompactCandidateRepair()
    {
        var selectorCalls = 0;
        var matchingCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponseWithEffects((
                        "cleanup_created_directories",
                        "Delete every directory successfully created by this workflow.",
                        true,
                        "external_effect",
                        "lifecycle"));
                if (request.Prompt.Contains("physical capability candidate selector", StringComparison.Ordinal))
                {
                    selectorCalls++;
                    Assert.Contains("method=get_object", request.Prompt, StringComparison.Ordinal);
                    return PhysicalCandidateResponse(("cleanup_created_directories", Array.Empty<string>()));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return MatchResponse(("cleanup_created_directories", "unavailable", string.Empty));
                }
                throw new InvalidOperationException("Generation must not run for an unavailable required operation.");
            });

        var result = await ExecuteAsync(
            InferredPlan()
                .Replace("prefilter: false", "prefilter: true", StringComparison.Ordinal)
                .Replace(
                    "Load a configured object and optionally notify a consumer.",
                    "Delete every directory successfully created by this workflow.",
                    StringComparison.Ordinal),
            llm.Object,
            CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal(2, selectorCalls);
        Assert.Equal(2, matchingCalls);
    }

    [Fact]
    public async Task InferredPreflight_AddsDeclaredArtifactProducerAfterPhysicalSelection()
    {
        string? matchingPrompt = null;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("inspect_workspace", "Inspect a materialized workspace.", true));
                if (request.Prompt.Contains("physical capability candidate selector", StringComparison.Ordinal))
                {
                    return PhysicalCandidateResponse(("inspect_workspace", new[]
                    {
                        CatalogIdForMethod(request.Prompt, "inspect_workspace")
                    }));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingPrompt = request.Prompt;
                    return MatchingResponse((
                        "inspect_workspace",
                        "composed",
                        new[]
                        {
                            CatalogIdForMethod(request.Prompt, "create_workspace"),
                            CatalogIdForMethod(request.Prompt, "inspect_workspace")
                        },
                        Array.Empty<string>(),
                        "The producer supplies the required workspace artifact."));
                }
                if (request.Prompt.Contains("tool-selection assistant", StringComparison.OrdinalIgnoreCase))
                {
                    return McpPrefilterResponse(
                        ("workspace-provider", new[] { "create_workspace" }),
                        ("workspace-consumer", new[] { "inspect_workspace", "verify_workspace" }));
                }
                return new LLMResponse { Text = ValidWorkspaceWorkflow };
            });

        var result = await ExecuteAsync(
            InferredPlan()
                .Replace("prefilter: false", "prefilter: true", StringComparison.Ordinal)
                .Replace("Load a configured object and optionally notify a consumer.", "Inspect one materialized workspace.", StringComparison.Ordinal),
            llm.Object,
            CreateArtifactFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(matchingPrompt);
        Assert.Contains("method=create_workspace", matchingPrompt, StringComparison.Ordinal);
        Assert.Contains("produces(workspace.directory:/projectRootRelative:materialize)", matchingPrompt, StringComparison.Ordinal);
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
        var method = typeof(WorkflowPlanExecutor).GetMethod(
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

    [Fact]
    public void AdvisorySelectorRefinement_UsesOneExplicitlyMentionedDocumentedValue()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["operation"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("inspect", "compare")
                }
            },
            ["required"] = new JsonArray("operation")
        };
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "SelectAdvisorySelectorVariants",
            BindingFlags.Static | BindingFlags.NonPublic);

        var variants = Assert.IsAssignableFrom<IEnumerable>(method!.Invoke(
            null,
            [schema, "Perform a fresh catalog read using inspect before publishing."]));
        var variant = Assert.Single(variants.Cast<object>());
        var bindings = Assert.IsAssignableFrom<IEnumerable>(
            variant.GetType().GetProperty("Bindings")!.GetValue(variant));
        var binding = Assert.Single(bindings.Cast<object>());

        Assert.Equal("/operation", binding.GetType().GetProperty("Path")!.GetValue(binding));
        Assert.Equal(
            "inspect",
            Assert.IsAssignableFrom<JsonValue>(binding.GetType().GetProperty("Value")!.GetValue(binding))
                .GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_RejectsPhaseSpecificCopiesOfOneSharedMaterializer()
    {
        var generated = ValidWorkspaceWorkflow.Replace(
            "      - id: inspect",
            """
                  - id: materialize_for_inspection
                    type: mcp.call
                    input:
                      server: workspace-provider
                      kind: tool
                      method: create_workspace
                      request:
                        sourceUrl: https://example.invalid/source
                  - id: materialize_for_verification
                    type: mcp.call
                    input:
                      server: workspace-provider
                      kind: tool
                      method: create_workspace
                      request:
                        sourceUrl: https://example.invalid/source
                  - id: inspect
            """,
            StringComparison.Ordinal);
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("materialize_source", "Materialize the one requested source workspace.", true, "external_effect", "lifecycle"),
                        ("inspect_source", "Inspect the materialized source workspace.", true, "external_effect", "execute"),
                        ("verify_source", "Verify the materialized source workspace.", true, "external_effect", "execute"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    var producer = CatalogIdForMethod(request.Prompt, "create_workspace");
                    return MatchingResponse(
                        ("materialize_source", "matched", [producer], Array.Empty<string>(), "The producer materializes the source."),
                        ("inspect_source", "composed", [producer, CatalogIdForMethod(request.Prompt, "inspect_workspace")], Array.Empty<string>(), "Inspection consumes the produced workspace."),
                        ("verify_source", "composed", [producer, CatalogIdForMethod(request.Prompt, "verify_workspace")], Array.Empty<string>(), "Verification consumes the produced workspace."));
                }
                return new LLMResponse { Text = generated };
            });

        var result = await ExecuteAsync(
            InferredPlan().Replace(
                "Load a configured object and optionally notify a consumer.",
                "Materialize one source workspace and reuse it for inspection and verification.",
                StringComparison.Ordinal),
            llm.Object,
            CreateArtifactFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowPlanRepairStalled, result.Error!.Code);
        var lastError = Assert.IsType<JsonObject>(result.Error.Details!["last_error"]);
        Assert.Equal(ErrorCodes.CapabilityPreflightRedundantArtifactProducer, lastError["code"]!.GetValue<string>());
        var details = Assert.IsType<JsonObject>(lastError["details"]);
        Assert.Equal("redundant_artifact_materializer", details["reason"]!.GetValue<string>());
        Assert.Equal(2, Assert.IsType<JsonArray>(details["redundant_calls"]).Count);
    }

    [Fact]
    public async Task InferredPreflight_AllowsTwoExplicitSourceMaterializerOccurrences()
    {
        var generated = ValidWorkspaceWorkflow.Replace(
            "      - id: inspect",
            """
                  - id: materialize_second_source
                    type: mcp.call
                    input:
                      server: workspace-provider
                      kind: tool
                      method: create_workspace
                      request:
                        sourceUrl: https://example.invalid/second-source
                  - id: inspect
            """,
            StringComparison.Ordinal);
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("materialize_first_source", "Materialize the first requested source workspace.", true, "external_effect", "lifecycle"),
                        ("materialize_second_source", "Materialize the second requested source workspace.", true, "external_effect", "lifecycle"),
                        ("inspect_first_source", "Inspect the first materialized source workspace.", true, "external_effect", "execute"),
                        ("verify_first_source", "Verify the first materialized source workspace.", true, "external_effect", "execute"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    var producer = CatalogIdForMethod(request.Prompt, "create_workspace");
                    return MatchingResponse(
                        ("materialize_first_source", "matched", [producer], Array.Empty<string>(), "The producer materializes the first source."),
                        ("materialize_second_source", "matched", [producer], Array.Empty<string>(), "The producer materializes the second source."),
                        ("inspect_first_source", "composed", [producer, CatalogIdForMethod(request.Prompt, "inspect_workspace")], Array.Empty<string>(), "Inspection consumes the first workspace."),
                        ("verify_first_source", "composed", [producer, CatalogIdForMethod(request.Prompt, "verify_workspace")], Array.Empty<string>(), "Verification consumes the first workspace."));
                }
                return new LLMResponse { Text = generated };
            });

        var result = await ExecuteAsync(
            InferredPlan().Replace(
                "Load a configured object and optionally notify a consumer.",
                "Materialize two distinct source workspaces and inspect and verify the first one.",
                StringComparison.Ordinal),
            llm.Object,
            CreateArtifactFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task InferredPreflight_ReusesStandaloneMaterializerSelectedAgainInUnrelatedComposition()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("materialize_source", "Materialize the one requested source workspace.", true, "external_effect", "lifecycle"),
                        ("inspect_source", "Inspect the materialized source workspace.", true, "external_effect", "execute"),
                        ("finalize_source", "Apply one independent final operation.", true, "external_effect", "execute"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    var producer = CatalogIdForMethod(request.Prompt, "create_workspace");
                    return MatchingResponse(
                        ("materialize_source", "matched", [producer], Array.Empty<string>(), "The producer materializes the source."),
                        ("inspect_source", "composed", [producer, CatalogIdForMethod(request.Prompt, "inspect_workspace")], Array.Empty<string>(), "Inspection consumes the produced workspace."),
                        ("finalize_source", "composed", [producer, CatalogIdForMethod(request.Prompt, "verify_workspace")], Array.Empty<string>(), "The repeated producer is not part of the independent final operation."));
                }
                return new LLMResponse { Text = ValidWorkspaceWorkflow };
            });

        var result = await ExecuteAsync(
            InferredPlan().Replace(
                "Load a configured object and optionally notify a consumer.",
                "Materialize one source workspace, inspect it, then apply one independent final operation without creating another workspace.",
                StringComparison.Ordinal),
            llm.Object,
            CreateArtifactFactory(verifyConsumesArtifact: false));

        Assert.True(result.Success, result.Error?.Message);
        var capabilities = Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        Assert.Single(capabilities.OfType<JsonObject>(), static capability =>
            capability["method"]?.GetValue<string>() == "create_workspace");
    }

    [Fact]
    public async Task InferredPreflight_UsesDiscoveredNeutralCatalogBeforeGeneration()
    {
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["complete"] = true,
                            ["operations"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "load_object",
                                    ["description"] = "Load the requested object.",
                                    ["required"] = true
                                },
                                new JsonObject
                                {
                                    ["id"] = "notify",
                                    ["description"] = "Optionally notify a consumer.",
                                    ["required"] = false,
                                    ["optionality_evidence"] = Evidence(
                                        "user_request",
                                        "optionally notify a consumer")
                                }
                            },
                            ["constraints"] = new JsonArray()
                        }
                    };
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return MatchResponse(
                        ("load_object", "mcp", CatalogIdForMethod(request.Prompt, "get_object")),
                        ("notify", "unavailable", ""));
                }

                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, prompts.Count);
        Assert.DoesNotContain("get_object", prompts[0], StringComparison.Ordinal);
        Assert.Contains("Exclude host configuration", prompts[0], StringComparison.Ordinal);
        Assert.Contains("declared workflow inputs supplied when execution starts", prompts[0], StringComparison.Ordinal);
        Assert.Contains("provider selection, secret-vault lookup", prompts[0], StringComparison.Ordinal);
        Assert.Contains("persistence, registration, or provisioning", prompts[0], StringComparison.Ordinal);
        Assert.Contains("reuses the original operation's capability", prompts[0], StringComparison.Ordinal);
        Assert.Contains("depends on a target, input value, resource instance", prompts[0], StringComparison.Ordinal);
        Assert.Contains("method=get_object", prompts[1], StringComparison.Ordinal);
        Assert.Contains("outputs=[/record:object, /record/content:string, /record/version:number]", prompts[1], StringComparison.Ordinal);
        Assert.Contains("locked by preflight", prompts[2], StringComparison.Ordinal);
        Assert.DoesNotContain("pull request", prompts[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InferredPreflight_RemovesPlannerRuntimeBoundaryArtifactBeforeMatching()
    {
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["complete"] = true,
                            ["incomplete_reasons"] = new JsonArray(),
                            ["operations"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "load_object",
                                    ["description"] = "Load the configured object.",
                                    ["required"] = true,
                                    ["execution_kind"] = "external_effect"
                                }
                            },
                            ["constraints"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "constraint_runtime_boundary",
                                    ["description"] = "Do not include host configuration, credential/provider resolution, secret-vault lookup, authentication, connection setup, or persistence and registration of the generated workflow as runtime operations.",
                                    ["required"] = true,
                                    ["enforcement_kind"] = "workflow_policy"
                                }
                            }
                        }
                    };
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain("constraint_runtime_boundary", request.Prompt, StringComparison.Ordinal);
                    return MatchResponse(("load_object", "mcp", CatalogIdForMethod(request.Prompt, "get_object")));
                }

                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, prompts.Count);
        Assert.Contains("never copy, paraphrase, or restate", prompts[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InferredPreflight_RemovesDerivedFailureHandlingFromPositiveCapabilityInventory()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["complete"] = true,
                            ["incomplete_reasons"] = new JsonArray(),
                            ["operations"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "load_object",
                                    ["description"] = "Load the requested object.",
                                    ["required"] = true,
                                    ["execution_kind"] = "external_effect",
                                    ["external_effect_kind"] = "read",
                                    ["decision_source_operation_id"] = "",
                                    ["intent_origin"] = "requested_effect",
                                    ["derivation_source_operation_id"] = ""
                                },
                                new JsonObject
                                {
                                    ["id"] = "report_load_failure",
                                    ["description"] = "Report an unsuccessful load through an additional external effect.",
                                    ["required"] = true,
                                    ["execution_kind"] = "external_effect",
                                    ["external_effect_kind"] = "write",
                                    ["decision_source_operation_id"] = "",
                                    ["intent_origin"] = "derived_failure_handling",
                                    ["derivation_source_operation_id"] = "load_object"
                                }
                            },
                            ["constraints"] = new JsonArray()
                        }
                    };
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain("report_load_failure", request.Prompt, StringComparison.Ordinal);
                    return MatchResponse(("load_object", "mcp", CatalogIdForMethod(request.Prompt, "get_object")));
                }
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task InferredPreflight_RemovesHostRuntimeInputCollectionArtifactBeforeMatching()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("op_collect_runtime_inputs", "Collect the declared workflow runtime inputs.", true, "local_processing", "none"),
                        ("load_object", "Load the configured object.", true, "external_effect", "read"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain("op_collect_runtime_inputs", request.Prompt, StringComparison.Ordinal);
                    return MatchResponse(("load_object", "mcp", CatalogIdForMethod(request.Prompt, "get_object")));
                }

                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task InferredPreflight_RemovesSpeculativeCleanupForUnselectedRuntimeResources()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("load_object", "Load the configured object.", true, "external_effect", "read"),
                        ("cleanup_runtime_resources", "Clean up any workflow-owned runtime resources.", true, "external_effect", "lifecycle"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain("cleanup_runtime_resources", request.Prompt, StringComparison.Ordinal);
                    return MatchResponse(("load_object", "mcp", CatalogIdForMethod(request.Prompt, "get_object")));
                }

                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task InferredPreflight_PreservesBoundaryConstraintExplicitlyRequestedByUser()
    {
        var matchingSawConstraint = false;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["complete"] = true,
                            ["incomplete_reasons"] = new JsonArray(),
                            ["operations"] = new JsonArray(),
                            ["constraints"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "keep_boundary",
                                    ["description"] = "Do not use host configuration, credentials, connection setup, or persist the generated workflow.",
                                    ["required"] = true,
                                    ["enforcement_kind"] = "workflow_policy"
                                }
                            }
                        }
                    };
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingSawConstraint = request.Prompt.Contains("keep_boundary", StringComparison.Ordinal);
                    return MatchResponseWithConstraints(
                        Array.Empty<(string OperationId, string Resolution, string CatalogId)>(),
                        [("keep_boundary", Array.Empty<string>())]);
                }

                return new LLMResponse { Text = ValidTemplateWorkflow };
            });
        var plan = InferredPlan().Replace(
            "Load a configured object and optionally notify a consumer.",
            "Do not use host configuration, credentials, connection setup, or persist the generated workflow.",
            StringComparison.Ordinal);

        var result = await ExecuteAsync(plan, llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(matchingSawConstraint);
    }

    [Fact]
    public async Task InferredPreflight_TreatsConditionalCapabilityDenialAsOrderingPolicy()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["complete"] = true,
                            ["incomplete_reasons"] = new JsonArray(),
                            ["operations"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "confirm_load",
                                    ["description"] = "Ask for confirmation before loading the object.",
                                    ["required"] = true,
                                    ["execution_kind"] = "human_interaction"
                                },
                                new JsonObject
                                {
                                    ["id"] = "load_object",
                                    ["description"] = "Load the configured object.",
                                    ["required"] = true,
                                    ["execution_kind"] = "external_effect"
                                }
                            },
                            ["constraints"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "load_only_after_confirmation",
                                    ["description"] = "Do not load the object before human confirmation.",
                                    ["required"] = true,
                                    ["enforcement_kind"] = "workflow_policy"
                                }
                            }
                        }
                    };
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    var get = CatalogIdForMethod(request.Prompt, "get_object");
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray
                            {
                                MatchingNode("confirm_load", "unavailable", [], "The model incorrectly claims no human interaction capability exists."),
                                MatchingNode("load_object", "matched", [get], "The read capability is sufficient.")
                            },
                            ["constraint_matches"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["constraint_id"] = "load_only_after_confirmation",
                                    ["status"] = "enforced",
                                    ["denied_catalog_ids"] = new JsonArray(get),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["reason"] = "The read is denied by the condition."
                                }
                            }
                        }
                    };
                }

                return new LLMResponse { Text = ValidGatedStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task InferredPreflight_InjectsLockedConfirmationForExternalWrite()
    {
        var matcherSawInjectedGate = false;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("remove_object", "Remove the configured object.", true, "external_effect", "write"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherSawInjectedGate = request.Prompt.Contains("platform_confirm_external_write", StringComparison.Ordinal)
                                             && request.Prompt.Contains("platform_external_write_after_confirmation", StringComparison.Ordinal);
                    var remove = CatalogIdForMethod(request.Prompt, "delete_object");
                    var confirm = CatalogIdForMethod(request.Prompt, "human.input");
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray
                            {
                                MatchingNode("remove_object", "matched", [remove], "The exact write is sufficient."),
                                MatchingNode("platform_confirm_external_write", "matched", [confirm], "The native confirmation is sufficient.")
                            },
                            ["constraint_matches"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["constraint_id"] = "platform_external_write_after_confirmation",
                                    ["status"] = "policy_only",
                                    ["denied_catalog_ids"] = new JsonArray(),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["reason"] = "This is an ordering invariant."
                                }
                            }
                        }
                    };
                }

                return new LLMResponse { Text = ValidGatedStorageWriteWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(matcherSawInjectedGate);
    }

    [Fact]
    public async Task InferredPreflight_CoalescesPlatformGateWithSameNativeConfirmationContract()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("confirm_change", "Obtain consent for the pending change.", true, "external_effect", "write"),
                        ("remove_object", "Remove the configured object.", true, "external_effect", "write"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    var remove = CatalogIdForMethod(request.Prompt, "delete_object");
                    var confirm = CatalogIdForMethod(request.Prompt, "human.input");
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray
                            {
                                MatchingNode("confirm_change", "matched", [confirm], "The native interaction implements the consent boundary."),
                                MatchingNode("remove_object", "matched", [remove], "The exact write is sufficient."),
                                MatchingNode("platform_confirm_external_write", "matched", [confirm], "The platform safety gate uses the same native interaction contract.")
                            },
                            ["constraint_matches"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["constraint_id"] = "platform_external_write_after_confirmation",
                                    ["status"] = "policy_only",
                                    ["denied_catalog_ids"] = new JsonArray(),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["reason"] = "This ordering invariant remains enforced by workflow topology."
                                }
                            }
                        }
                    };
                }

                return new LLMResponse { Text = ValidGatedStorageWriteWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        var capabilities = result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]!.AsArray();
        var nativeConfirmation = Assert.Single(capabilities, static capability =>
            capability!["resolution"]!.GetValue<string>() == "native"
            && capability["method"]!.GetValue<string>() == "human.input");
        var operationIds = nativeConfirmation!["operation_ids"]!.AsArray()
            .Select(static value => value!.GetValue<string>())
            .ToArray();
        Assert.Contains("confirm_change", operationIds);
        Assert.Contains("platform_confirm_external_write", operationIds);
    }

    [Fact]
    public async Task InferredPreflight_PlatformConfirmationIgnoresModelUnavailableClaim()
    {
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("remove_object", "Remove the configured object.", true, "external_effect", "write"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray
                            {
                                MatchingNode(
                                    "remove_object",
                                    "matched",
                                    [CatalogIdForMethod(request.Prompt, "delete_object")],
                                    "The exact write is sufficient."),
                                MatchingNode(
                                    "platform_confirm_external_write",
                                    "unavailable",
                                    [],
                                    "The model incorrectly claims no human capability exists.")
                            },
                            ["constraint_matches"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["constraint_id"] = "platform_external_write_after_confirmation",
                                    ["status"] = "policy_only",
                                    ["denied_catalog_ids"] = new JsonArray(),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["reason"] = "This is an ordering invariant."
                                }
                            }
                        }
                    };
                }

                return new LLMResponse { Text = ValidGatedStorageWriteWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matcherCalls);
        Assert.Contains("human.input", result.Outputs!["plan"]!["meta"]!["capability_preflight"]!.ToJsonString());
    }

    [Fact]
    public async Task InferredPreflight_DeclaredForbiddenConfirmationPolicyDoesNotInjectConfirmation()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    var response = InventoryResponseWithEffects(
                        ("remove_object", "Remove the configured object.", true, "external_effect", "write"));
                    response.Json!["external_write_confirmation_policy"] = "forbidden";
                    response.Json["external_write_confirmation_evidence"] = Evidence(
                        "user_request",
                        "Aucune confirmation humaine");
                    return response;
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain("platform_confirm_external_write", request.Prompt, StringComparison.Ordinal);
                    return MatchResponse(("remove_object", "mcp", CatalogIdForMethod(request.Prompt, "delete_object")));
                }

                return new LLMResponse { Text = InvalidDeniedStorageWorkflow };
            });
        var plan = InferredPlan().Replace(
            "Load a configured object and optionally notify a consumer.",
            "Supprimer l'objet configuré. Aucune confirmation humaine.",
            StringComparison.Ordinal);

        var result = await ExecuteAsync(plan, llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task InferredPreflight_NormalizesNativeOnlyExactDenialToPolicy()
    {
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    var response = InventoryResponseWithEffects(
                        ("remove_object", "Remove the configured object.", true, "external_effect", "write"));
                    response.Json!["external_write_confirmation_policy"] = "forbidden";
                    response.Json["external_write_confirmation_evidence"] = Evidence(
                        "user_request",
                        "without human confirmation");
                    response.Json["constraints"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "no_runtime_confirmation",
                        ["description"] = "Do not request a runtime confirmation.",
                        ["required"] = true,
                        ["enforcement_kind"] = "exact_denial"
                    });
                    return response;
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    var confirmation = CatalogIdForMethod(request.Prompt, "human.input");
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray(MatchingNode(
                                "remove_object",
                                "matched",
                                [CatalogIdForMethod(request.Prompt, "delete_object")],
                                "The exact write capability is selected.")),
                            ["constraint_matches"] = new JsonArray(new JsonObject
                            {
                                ["constraint_id"] = "no_runtime_confirmation",
                                ["status"] = "enforced",
                                ["denied_catalog_ids"] = new JsonArray(confirmation, confirmation),
                                ["candidate_catalog_ids"] = new JsonArray(),
                                ["reason"] = "The native candidate represents the prohibited interaction."
                            })
                        }
                    };
                }

                return new LLMResponse { Text = InvalidDeniedStorageWorkflow };
            });
        var plan = InferredPlan().Replace(
            "Load a configured object and optionally notify a consumer.",
            "Remove the configured object without human confirmation.",
            StringComparison.Ordinal);

        var result = await ExecuteAsync(plan, llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matcherCalls);
        Assert.DoesNotContain("human.input", result.Outputs!["plan"]!["yaml"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_AllowsComposedExternalAndLocalOperationMatches()
    {
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponseWithKinds(
                        ("read_then_remove", "Read and then remove a configured object.", true, "external_effect"),
                        ("normalize_result", "Normalize the operation result.", true, "local_processing"));
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return MatchingResponse(
                        ("read_then_remove", "composed",
                            new[] { CatalogIdForMethod(request.Prompt, "get_object"), CatalogIdForMethod(request.Prompt, "delete_object") },
                            Array.Empty<string>(), "Both complementary effects are required."),
                        ("normalize_result", "local", Array.Empty<string>(), Array.Empty<string>(), "This transformation is local."));
                }
                return new LLMResponse { Text = ValidComposedStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, prompts.Count);
        Assert.Contains("normalize_result: local", prompts[2], StringComparison.Ordinal);
        Assert.Contains("match_status", result.Outputs!["plan"]!["meta"]!["capability_preflight"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_AutomaticallyClosesCompositionWithUniqueArtifactProducer()
    {
        var matchingCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("analyze_content", "Analyze configured content.", true, "external_effect", "execute"));
                }

                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    var analyze = CatalogIdForMethod(request.Prompt, "analyze_workspace");
                    return MatchingResponse((
                        "analyze_content",
                        "matched",
                        new[] { analyze },
                        Array.Empty<string>(),
                        "The analyzer performs the requested operation."));
                }

                return new LLMResponse
                {
                    Text = """
                    version: 1
                    name: artifact-closed-analysis
                    skill:
                      description: Create and analyze an isolated workspace.
                      tags: [generated, storage, analysis]
                      inputs: {}
                      outputs:
                        result: string
                    workflows:
                      main:
                        steps:
                          - id: create
                            type: mcp.call
                            input:
                              server: storage
                              kind: tool
                              method: create_workspace
                              request:
                                sourceUrl: https://example.invalid/source
                          - id: analyze
                            type: mcp.call
                            input:
                              server: analyzer
                              kind: tool
                              method: analyze_workspace
                              request:
                                workspaceRoot: "${data.steps.create.response.workspaceRoot}"
                        outputs:
                          result:
                            expr: "${data.steps.analyze.response.result}"
                            type: string
                    """
                };
            });

        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("storage", new MockMcpServerConfig
        {
            Tools =
            {
                new McpToolInfo
                {
                    Name = "create_workspace",
                    Description = "Create an isolated workspace.",
                    InputSchema = JsonNode.Parse("""
                    {"type":"object","properties":{"sourceUrl":{"type":"string"}},"required":["sourceUrl"]}
                    """),
                    OutputSchema = JsonNode.Parse("""
                    {"type":"object","properties":{"workspaceRoot":{"type":"string","description":"Existing workspace root created by this capability."}},"required":["workspaceRoot"]}
                    """)
                }
            }
        });
        factory.RegisterServer("analyzer", new MockMcpServerConfig
        {
            Tools =
            {
                new McpToolInfo
                {
                    Name = "analyze_workspace",
                    Description = "Analyze an existing workspace.",
                    InputSchema = JsonNode.Parse("""
                    {"type":"object","properties":{"workspaceRoot":{"type":"string","description":"Required existing workspace root."}},"required":["workspaceRoot"]}
                    """),
                    OutputSchema = JsonNode.Parse("""
                    {"type":"object","properties":{"result":{"type":"string"}},"required":["result"]}
                    """)
                }
            }
        });
        var plan = InferredPlan().Replace(
            "Load a configured object and optionally notify a consumer.",
            "Analyze configured content.",
            StringComparison.Ordinal);

        var result = await ExecuteAsync(plan, llm.Object, factory);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matchingCalls);
        var capabilities = Assert.IsType<JsonArray>(result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        Assert.Equal(2, capabilities.Count);
    }

    [Fact]
    public async Task InferredPreflight_AutomaticallyClosesGitReviewMultiHopArtifactGraph()
    {
        var matchingCalls = 0;
        var telemetry = new Mock<IWorkflowTelemetry>();
        var workflowSpan = new Mock<IWorkflowSpan>();
        var stepSpan = new Mock<IStepSpan>();
        var internalSpan = new Mock<ITelemetrySpan>();
        var normalizationEvents = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<ITelemetrySpan>(), It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.StepStart(It.IsAny<ITelemetrySpan>(), It.IsAny<StepTelemetryInfo>()))
            .Returns(stepSpan.Object);
        telemetry.Setup(value => value.SpanStart(It.IsAny<ITelemetrySpan>(), It.IsAny<TelemetrySpanInfo>()))
            .Returns(internalSpan.Object);
        internalSpan.Setup(value => value.AddEvent(
                "gnougo-flow.plan.capability_matching.normalization",
                It.IsAny<IReadOnlyList<KeyValuePair<string, object?>>?>()))
            .Callback((string _, IReadOnlyList<KeyValuePair<string, object?>>? attributes) =>
                normalizationEvents.Add(attributes ?? []));
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("review_change", "Review the complete materialized change.", true, "external_effect", "execute"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return MatchingResponse((
                        "review_change",
                        "matched",
                        [CatalogIdForMethod(request.Prompt, "copilot_review")],
                        Array.Empty<string>(),
                        "The reviewer implements the requested operation."));
                }
                return new LLMResponse
                {
                    Text = """
                        version: 1
                        name: generated-multi-hop-review
                        skill:
                          description: Materialize, compare, and review one change.
                          tags: [generated]
                          inputs: {}
                          outputs: {}
                        workflows:
                          main:
                            steps:
                              - id: clone
                                type: mcp.call
                                input:
                                  server: source-provider
                                  kind: tool
                                  method: git_clone
                                  request: {}
                              - id: compare
                                type: mcp.call
                                input:
                                  server: payload-provider
                                  kind: tool
                                  method: git_compare_refs
                                  request:
                                    projectRoot: ${data.steps.clone.response.projectRootRelative}
                              - id: review
                                type: mcp.call
                                input:
                                  server: payload-consumer
                                  kind: tool
                                  method: copilot_review
                                  request:
                                    projectRoot: ${data.steps.clone.response.projectRootRelative}
                                    filesJson: ${data.steps.compare.response.filesJson}
                        """
                };
            });

        var plan = InferredPlan().Replace(
            "Load a configured object and optionally notify a consumer.",
            "Review the complete materialized change.",
            StringComparison.Ordinal);
        var result = await ExecuteAsync(
            plan,
            llm.Object,
            CreateMultiHopArtifactFactory(),
            telemetry: telemetry.Object);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matchingCalls);
        var capabilities = Assert.IsType<JsonArray>(result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        Assert.Equal(3, capabilities.Count);
        Assert.Equal(
            ["copilot_review", "git_clone", "git_compare_refs"],
            capabilities.OfType<JsonObject>()
                .Select(static capability => capability["method"]!.GetValue<string>())
                .Order(StringComparer.Ordinal)
                .ToArray());
        var closureEvent = Assert.Single(normalizationEvents)
            .ToDictionary(static item => item.Key, static item => item.Value);
        Assert.Equal("artifact_closure_resolved", closureEvent["gnougo-flow.plan.capability_matching.reason_code"]);
        Assert.Equal(3, closureEvent["gnougo-flow.plan.capability_matching.selected_count"]);
        Assert.DoesNotContain(closureEvent.Keys, static key => key.Contains("description", StringComparison.Ordinal));
        Assert.DoesNotContain(closureEvent.Keys, static key => key.Contains("reasoning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InferredPreflight_ReportsMultipleMinimalArtifactClosuresAsAmbiguous()
    {
        var matchingCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("inspect_source", "Inspect a materialized source.", true, "external_effect", "execute"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return MatchingResponse((
                        "inspect_source",
                        "matched",
                        [CatalogIdForMethod(request.Prompt, "inspect_source")],
                        Array.Empty<string>(),
                        "The inspector implements the requested operation."));
                }
                throw new InvalidOperationException("Generation must not run while artifact closure is ambiguous.");
            });

        var plan = InferredPlan().Replace(
            "Load a configured object and optionally notify a consumer.",
            "Inspect a materialized source.",
            StringComparison.Ordinal);
        var result = await ExecuteAsync(plan, llm.Object, CreateAmbiguousArtifactFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal(2, matchingCalls);
        var issue = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(result.Error.Details!["matching_issues"])));
        Assert.Equal("ambiguous", issue["status"]!.GetValue<string>());
        Assert.Equal("artifact_closure_multiple", issue["reason_code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("missing", "artifact_closure_unavailable")]
    [InlineData("cycle", "artifact_closure_cycle")]
    [InlineData("limit", "artifact_closure_limit")]
    public async Task InferredPreflight_FailsClosedForUnresolvableArtifactGraphs(
        string graph,
        string expectedReasonCode)
    {
        var matchingCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("consume_artifact", "Consume the required artifact.", true, "external_effect", "execute"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return MatchingResponse((
                        "consume_artifact",
                        "matched",
                        [CatalogIdForMethod(request.Prompt, "consume_artifact")],
                        Array.Empty<string>(),
                        "The consumer implements the requested operation."));
                }
                throw new InvalidOperationException("Generation must not run for an unresolvable artifact graph.");
            });

        var plan = InferredPlan().Replace(
            "Load a configured object and optionally notify a consumer.",
            "Consume the required artifact.",
            StringComparison.Ordinal);
        var result = await ExecuteAsync(plan, llm.Object, CreateUnresolvableArtifactFactory(graph));

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal(2, matchingCalls);
        var issue = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(result.Error.Details!["matching_issues"])));
        Assert.Equal("unavailable", issue["status"]!.GetValue<string>());
        Assert.Equal(expectedReasonCode, issue["reason_code"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonArray>(issue["candidate_capabilities"]));
    }

    [Fact]
    public async Task InferredPreflight_NormalizesMatchedCardinalityWhenMetadataProvesUniqueArtifactComposition()
    {
        var matchingCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("inspect_workspace", "Inspect one materialized workspace.", true));
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return MatchingResponse((
                        "inspect_workspace",
                        "matched",
                        new[]
                        {
                            CatalogIdForMethod(request.Prompt, "create_workspace"),
                            CatalogIdForMethod(request.Prompt, "inspect_workspace")
                        },
                        Array.Empty<string>(),
                        "The materializer supplies the artifact required by the inspector."));
                }

                return new LLMResponse { Text = ValidWorkspaceWorkflow };
            });

        var result = await ExecuteAsync(
            InferredPlan().Replace(
                "Load a configured object and optionally notify a consumer.",
                "Inspect one materialized workspace.",
                StringComparison.Ordinal),
            llm.Object,
            CreateArtifactFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matchingCalls);
        var capabilities = Assert.IsType<JsonArray>(result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        Assert.Equal(2, capabilities.Count);
        Assert.All(capabilities.OfType<JsonObject>(), static capability =>
            Assert.Equal("composed", capability["match_status"]!.GetValue<string>()));
    }

    [Fact]
    public async Task InferredPreflight_DoesNotNormalizeUnrelatedCapabilitiesAsAComposition()
    {
        var matchingCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("load_object", "Load one configured object.", true));
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return MatchingResponse((
                        "load_object",
                        "matched",
                        new[]
                        {
                            CatalogIdForMethod(request.Prompt, "get_object"),
                            CatalogIdForMethod(request.Prompt, "delete_object")
                        },
                        Array.Empty<string>(),
                        "The response incorrectly labels two unrelated effects as one match."));
                }

                throw new InvalidOperationException("Generation must not run for an invalid matching contract.");
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal(2, matchingCalls);
    }

    [Fact]
    public async Task InferredPreflight_RepairsOnlyUnresolvedMatchingDecision()
    {
        var matchingCalls = 0;
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("load_object", "Load a configured object.", true));
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    var get = CatalogIdForMethod(request.Prompt, "get_object");
                    return matchingCalls == 1
                        ? MatchingResponse(("load_object", "ambiguous", Array.Empty<string>(), new[] { get }, "The first pass was uncertain."))
                        : MatchingResponse(("load_object", "matched", new[] { get }, Array.Empty<string>(), "The documented read is sufficient."));
                }
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(4, prompts.Count);
        Assert.Contains("repairing a previous matching contract", prompts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_RepairMayOmitAlreadyLockedValidRows()
    {
        var matchingCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("load_object", "Load the primary configured object.", true, "external_effect", "read"),
                        ("load_secondary", "Load the secondary configured object separately.", true, "external_effect", "read"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    var get = CatalogIdForMethod(request.Prompt, "get_object");
                    return matchingCalls == 1
                        ? MatchingResponse(
                            ("load_object", "matched", new[] { get }, Array.Empty<string>(), "The documented read is sufficient."),
                            ("load_secondary", "ambiguous", Array.Empty<string>(), new[] { get }, "The second read implementation is uncertain."))
                        : MatchingResponse(
                            ("load_secondary", "matched", new[] { get }, Array.Empty<string>(), "The documented read is sufficient."));
                }
                return new LLMResponse
                {
                    Text = """
                        version: 1
                        name: generated-storage-pair
                        skill:
                          description: Load two configured objects.
                          tags: [generated]
                          inputs: {}
                          outputs: {}
                        workflows:
                          main:
                            steps:
                              - id: load_primary
                                type: mcp.call
                                input:
                                  server: object-storage
                                  kind: tool
                                  method: get_object
                                  request: { key: primary }
                              - id: load_secondary
                                type: mcp.call
                                input:
                                  server: object-storage
                                  kind: tool
                                  method: get_object
                                  request: { key: secondary }
                        """
                };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, matchingCalls);
    }

    [Fact]
    public async Task InferredPreflight_ClassifiesProhibitionAsConstraintWithoutAvailabilityFailure()
    {
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["complete"] = true,
                            ["operations"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "load_object",
                                    ["description"] = "Load the requested object.",
                                    ["required"] = true
                                }
                            },
                            ["constraints"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "never_delete",
                                    ["description"] = "Never delete stored objects.",
                                    ["required"] = true
                                }
                            }
                        }
                    };
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return MatchResponseWithConstraints(
                        [("load_object", "mcp", CatalogIdForMethod(request.Prompt, "get_object"))],
                        [("never_delete", [CatalogIdForMethod(request.Prompt, "delete_object")])]);
                }

                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, prompts.Count);
        Assert.Contains("invariants, not operations", prompts[2], StringComparison.Ordinal);
        Assert.Contains("object-storage/delete_object", prompts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_NormalizesNativeOnlyConstraintDenialToPolicy()
    {
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["complete"] = true,
                            ["incomplete_reasons"] = new JsonArray(),
                            ["operations"] = new JsonArray(),
                            ["constraints"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "do_not_prompt",
                                    ["description"] = "Do not request an additional interaction.",
                                    ["required"] = true,
                                    ["enforcement_kind"] = "workflow_policy"
                                }
                            }
                        }
                    };
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return MatchResponseWithConstraints(
                        [],
                        [("do_not_prompt", [CatalogIdForMethod(request.Prompt, "human.input")])]);
                }
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, prompts.Count);
        Assert.Contains("Native Flow catalog IDs are never denied_catalog_ids", prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_RejectsGeneratedCallDeniedByLockedConstraint()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["complete"] = true,
                            ["operations"] = new JsonArray(),
                            ["constraints"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "never_delete",
                                    ["description"] = "Never delete stored objects.",
                                    ["required"] = true
                                }
                            }
                        }
                    };
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return MatchResponseWithConstraints(
                        [],
                        [("never_delete", [CatalogIdForMethod(request.Prompt, "delete_object")])]);
                }

                return new LLMResponse { Text = InvalidDeniedStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowPlanRepairStalled, result.Error!.Code);
        Assert.Contains("repair", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InferredPreflight_UnavailableRequiredOperationFailsBeforeYamlGeneration()
    {
        var calls = 0;
        var requests = new List<LLMRequest>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                calls++;
                requests.Add(request);
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                    return MatchResponse(("remove_expired_record", "unavailable", ""));
                return new LLMResponse
                {
                    Json = new JsonObject
                    {
                        ["complete"] = true,
                        ["operations"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "remove_expired_record",
                                ["description"] = "Remove an expired record safely.",
                                ["required"] = true
                            }
                        },
                        ["constraints"] = new JsonArray()
                    }
                };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal(3, calls);
        Assert.All(requests, request =>
        {
            Assert.True(request.UseBackgroundMode);
            Assert.True(request.StructuredOutputStrict);
            Assert.NotNull(request.StructuredOutputSchema);
        });
    }

    [Fact]
    public async Task InferredPreflight_TimeoutUsesRetryableLlmTimeoutAndPreservesPhaseDetails()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("planner request timed out"));

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.LlmTimeout, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.Equal("capability_inventory_call", result.Error.Details!["inference_phase"]!.GetValue<string>());
        Assert.Equal("capability_inference", result.Error.Details["phase"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(401, "LLM_PROVIDER", false)]
    [InlineData(429, "LLM_NETWORK", true)]
    [InlineData(503, "LLM_NETWORK", true)]
    public async Task InferredPreflight_ProviderHttpFailureUsesStableLlmContract(
        int status,
        string expectedCode,
        bool retryable)
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(
                "provider rejected capability inventory",
                inner: null,
                statusCode: (System.Net.HttpStatusCode)status));

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Equal(retryable, result.Error.Retryable);
        Assert.Equal("capability_inventory_call", result.Error.Details!["inference_phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_UnavailableRequiredOperationFailsBeforePipelineDecomposition()
    {
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                    return MatchResponse(("dispatch_record", "unavailable", ""));
                return new LLMResponse
                {
                    Json = new JsonObject
                    {
                        ["complete"] = true,
                        ["operations"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "dispatch_record",
                                ["description"] = "Dispatch a record to an unavailable destination.",
                                ["required"] = true
                            }
                        },
                        ["constraints"] = new JsonArray()
                    }
                };
            });
        var plan = InferredPlan().Replace("mode: basic", "mode: pipeline", StringComparison.Ordinal);

        var result = await ExecuteAsync(plan, llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal(3, prompts.Count);
        Assert.DoesNotContain("preparing a raw user automation prompt", prompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("annotate normalized automation Markdown", prompts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_UsesIdentityCapableStructuredPipelineExtractionWithoutResolverMetadata()
    {
        var extractionRequests = new List<LLMRequest>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return InventoryResponseWithEffects(
                        ("load_object", "Load one configured object.", true, "external_effect", "read"));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return MatchingResponse((
                        "load_object",
                        "matched",
                        [CatalogIdForMethod(request.Prompt, "get_object")],
                        Array.Empty<string>(),
                        "The selected capability performs the required read."));
                }
                if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
                    return new LLMResponse { Text = "# Load\n\nLoad one configured object." };
                if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
                {
                    extractionRequests.Add(request);
                    return new LLMResponse { Json = new JsonObject(), Text = "{}" };
                }
                throw new InvalidOperationException("Unexpected LLM prompt: " + request.Prompt);
            });
        var plan = InferredPlan().Replace("mode: basic", "mode: pipeline", StringComparison.Ordinal);

        var result = await ExecuteAsync(plan, llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.NotEmpty(extractionRequests);
        Assert.All(extractionRequests, request =>
        {
            Assert.True(request.StructuredOutputStrict);
            Assert.NotNull(request.StructuredOutputSchema);
        });
    }

    [Fact]
    public async Task InferredPreflight_DiscoveryFailureUsesDedicatedFailFastCode()
    {
        var factory = new Mock<IMcpClientFactory>();
        factory.SetupGet(item => item.ServerMetadata).Returns(
        [
            new McpServerMetadata { Name = "inventory", Description = "Provides stock operations." }
        ]);
        factory.Setup(item => item.GetClientAsync("inventory", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("catalog unavailable"));
        var llm = new Mock<ILLMClient>(MockBehavior.Strict);

        var result = await ExecuteAsync(InferredPlan(), llm.Object, factory.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightDiscoveryFailed, result.Error!.Code);
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InferredPreflight_IncompleteInventoryRepairsOnceThenReturnsActionableReasons()
    {
        var calls = 0;
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                calls++;
                prompts.Add(request.Prompt);
                return new LLMResponse
                {
                    Json = new JsonObject
                    {
                        ["complete"] = false,
                        ["incomplete_reasons"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "missing_retention_intent",
                                ["description"] = "Clarify whether processed records must be retained after delivery."
                            }
                        },
                        ["operations"] = new JsonArray(),
                        ["constraints"] = new JsonArray()
                    }
                };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal(2, calls);
        Assert.True(result.Error.Details!["repair_attempted"]!.GetValue<bool>());
        Assert.Equal(2, result.Error.Details["attempts"]!.GetValue<int>());
        Assert.Equal("missing_retention_intent", result.Error.Details["incomplete_reasons"]![0]!["id"]!.GetValue<string>());
        Assert.Contains("inventory repair analyst", prompts[1], StringComparison.Ordinal);
        Assert.Contains("tool availability", prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_IncompleteInventoryCanBeRepairedBeforeMatching()
    {
        var calls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                calls++;
                if (calls == 1)
                {
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["complete"] = false,
                            ["incomplete_reasons"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "implementation_uncertainty",
                                    ["description"] = "The implementation capability is not yet known."
                                }
                            },
                            ["operations"] = new JsonArray(),
                            ["constraints"] = new JsonArray()
                        }
                    };
                }
                if (request.Prompt.Contains("inventory repair analyst", StringComparison.Ordinal))
                    return InventoryResponse(("load_record", "Load a configured record.", true));
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                    return MatchResponse(("load_record", "mcp", CatalogIdForMethod(request.Prompt, "get_object")));
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task PreflightOff_RemainsBackwardCompatible()
    {
        var llm = ConstantLlm(ValidTemplateWorkflow);
        var result = await ExecuteAsync("""
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      mode: basic
                      generator:
                        model: gpt-4
                        prefilter: false
                        instruction: Produce a deterministic result.
            """, llm.Object);

        Assert.True(result.Success, result.Error?.Message);
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RepeatedEmptyRequiredStringDiagnostics_StopAfterTwoRepairAttempts()
    {
        const string invalid = """
            version: 1
            name: invalid-required-string
            skill:
              description: Produce a result.
              tags: [generated]
              inputs: {}
              outputs: {}
            workflows:
              main:
                steps:
                  - id: normalize
                    type: set
                    output_schema:
                      type: object
                      properties:
                        handle:
                          type: string
                      required: [handle]
                      additionalProperties: false
                    input:
                      handle: ""
            """;
        var calls = 0;
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                calls++;
                prompts.Add(request.Prompt);
                return new LLMResponse { Text = invalid };
            });

        var result = await ExecuteAsync("""
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      mode: basic
                      generator:
                        model: gpt-4
                        prefilter: false
                        instruction: Produce a normalized result.
                      validate:
                        max_repair_attempts: 10
            """, llm.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.WorkflowPlanRepairStalled, result.Error!.Code);
        Assert.Equal(3, calls);
        Assert.Contains("SET_REQUIRED_STRING_EMPTY", prompts[1], StringComparison.Ordinal);
        Assert.Contains("make the property optional and omit it", prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_NormalizesCompleteOperationOverMalformedRelatedCandidate()
    {
        const string generated = """
            version: 1
            name: generated-review
            skill:
              description: Review a change.
              tags: [generated]
              inputs: {}
              outputs: {}
            workflows:
              main:
                steps:
                  - id: review
                    type: mcp.call
                    input:
                      server: reviewer
                      kind: tool
                      method: review_complete
            """;
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("review", "Perform the complete review.", true));
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    return MatchingResponse((
                        "review",
                        "matched",
                        new[]
                        {
                            CatalogIdForMethod(request.Prompt, "review_complete"),
                            CatalogIdForMethod(request.Prompt, "read_diff")
                        },
                        Array.Empty<string>(),
                        "The matched response incorrectly included a related read candidate."));
                }
                return new LLMResponse { Text = generated };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateCompositionFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matcherCalls);
        var capability = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"])));
        Assert.Equal("review_complete", capability["method"]!.GetValue<string>());
        Assert.Equal("matched", capability["match_status"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_NormalizesFinalIdPlacedInAdvisoryArray()
    {
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("load_object", "Load the requested object.", true));
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    return MatchingResponse((
                        "load_object",
                        "matched",
                        Array.Empty<string>(),
                        new[] { CatalogIdForMethod(request.Prompt, "get_object") },
                        "The final ID was placed in the advisory array."));
                }
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matcherCalls);
    }

    [Fact]
    public async Task InferredPreflight_ValidatesExactlyOneConditionalSelectorSwitch()
    {
        var llm = CreateConditionalLlm(ValidConditionalWorkflow);

        var result = await ExecuteAsync(ConditionalInferredPlan(), llm.Object, CreateConditionalReviewFactory());

        Assert.True(result.Success, result.Error?.Message);
        var capabilities = Assert.IsType<JsonArray>(result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        var conditional = capabilities.OfType<JsonObject>()
            .Where(static capability => capability["match_status"]?.GetValue<string>() == "conditional")
            .ToArray();
        Assert.Equal(2, conditional.Length);
        Assert.All(conditional, capability =>
        {
            var activation = Assert.IsType<JsonObject>(capability["activation"]);
            Assert.Equal("exactly_one", activation["mode"]!.GetValue<string>());
            Assert.Equal("publish", activation["group"]!.GetValue<string>());
            Assert.Equal("analyze", activation["decision_operation_id"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task InferredPreflight_GuardsOrderedConditionalCompositionWithNoEffectOutcome()
    {
        const string generated = """
            version: 1
            name: generated-conditional-composition
            skill:
              description: Analyze and conditionally apply one ordered effect composition.
              tags: [generated]
              inputs: {}
              outputs: {}
            workflows:
              main:
                steps:
                  - id: analyze
                    type: mcp.call
                    input:
                      server: reviewer
                      kind: tool
                      method: analyze_change
                      structured_output:
                        schema_inline:
                          type: object
                          properties:
                            decision:
                              type: string
                              enum: [EFFECT, NO_EFFECT]
                          required: [decision]
                          additionalProperties: false
                        strict: true
                  - id: apply_decision
                    type: switch
                    expr: ${data.steps.analyze.json.decision}
                    cases:
                      - value: EFFECT
                        steps:
                          - id: start_effect
                            type: mcp.call
                            input:
                              server: writer
                              kind: tool
                              method: lifecycle_write
                              request:
                                method: create
                          - id: add_effect_detail
                            type: mcp.call
                            input:
                              server: writer
                              kind: tool
                              method: add_detail
                          - id: finish_effect
                            type: mcp.call
                            input:
                              server: writer
                              kind: tool
                              method: lifecycle_write
                              request:
                                method: submit
                      - value: NO_EFFECT
                        steps:
                          - id: record_no_effect
                            type: set
                            input:
                              status: skipped
                    default: []
            """;
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return ConditionalInventoryResponse(
                        "Analyze the runtime input and prepare an effect decision.",
                        "Apply every necessary effect phase in order or perform no effect.",
                        allowNoEffectOutcome: true);
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray(
                                new JsonObject
                                {
                                    ["operation_id"] = "analyze",
                                    ["status"] = "matched",
                                    ["catalog_ids"] = new JsonArray(CatalogIdForMethod(request.Prompt, "analyze_change")),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = string.Empty,
                                    ["reason"] = "The selected capability provides the runtime analysis."
                                },
                                new JsonObject
                                {
                                    ["operation_id"] = "publish",
                                    ["status"] = "conditional",
                                    ["catalog_ids"] = new JsonArray(
                                        CatalogIdForBinding(request.Prompt, "/method", "create"),
                                        CatalogIdForMethod(request.Prompt, "add_detail"),
                                        CatalogIdForBinding(request.Prompt, "/method", "submit")),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = "analyze",
                                    ["conditional_mode"] = "all_on_value",
                                    ["reason"] = "Every selected phase is necessary for the effect outcome."
                                }),
                            ["constraint_matches"] = new JsonArray()
                        }
                    };
                }
                return new LLMResponse { Text = generated };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateConditionalCompositionFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matcherCalls);
        var capabilities = Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        var activations = capabilities.OfType<JsonObject>()
            .Where(static capability => capability["activation"] is JsonObject)
            .Select(static capability => (JsonObject)capability["activation"]!)
            .ToArray();
        Assert.Equal(3, activations.Length);
        Assert.All(activations, activation =>
        {
            Assert.Equal("all_on_value", activation["mode"]!.GetValue<string>());
            Assert.Equal("EFFECT", activation["branch_value"]!.GetValue<string>());
            Assert.Equal("NO_EFFECT", Assert.Single(
                Assert.IsType<JsonArray>(activation["no_effect_values"]))!.GetValue<string>());
            Assert.Equal("structured_output", activation["decision_contract_source"]!.GetValue<string>());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InferredPreflight_GuardsSingleConditionalEffectWithNoEffectOutcomeWithoutRepair(
        bool advisoryFieldPlacement)
    {
        const string generated = """
            version: 1
            name: generated-single-conditional-effect
            skill:
              description: Analyze and conditionally apply one effect.
              tags: [generated]
              inputs: {}
              outputs: {}
            workflows:
              main:
                steps:
                  - id: analyze
                    type: mcp.call
                    input:
                      server: reviewer
                      kind: tool
                      method: analyze_change
                      structured_output:
                        schema_inline:
                          type: object
                          properties:
                            decision:
                              type: string
                              enum: [EFFECT, NO_EFFECT]
                          required: [decision]
                          additionalProperties: false
                        strict: true
                  - id: apply_decision
                    type: switch
                    expr: ${data.steps.analyze.json.decision}
                    cases:
                      - value: EFFECT
                        steps:
                          - id: apply_effect
                            type: mcp.call
                            input:
                              server: writer
                              kind: tool
                              method: add_detail
                      - value: NO_EFFECT
                        steps:
                          - id: record_no_effect
                            type: set
                            input:
                              status: skipped
                    default: []
            """;
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return ConditionalInventoryResponse(
                        "Analyze the runtime input and prepare an effect decision.",
                        "Apply one effect when selected or perform no effect.",
                        allowNoEffectOutcome: true);
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    var effectCatalogId = CatalogIdForMethod(request.Prompt, "add_detail");
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray(
                                new JsonObject
                                {
                                    ["operation_id"] = "analyze",
                                    ["status"] = "matched",
                                    ["catalog_ids"] = new JsonArray(CatalogIdForMethod(request.Prompt, "analyze_change")),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = string.Empty,
                                    ["conditional_mode"] = string.Empty,
                                    ["reason"] = "The selected capability provides the runtime analysis."
                                },
                                new JsonObject
                                {
                                    ["operation_id"] = "publish",
                                    ["status"] = "conditional",
                                    ["catalog_ids"] = advisoryFieldPlacement
                                        ? new JsonArray()
                                        : new JsonArray(effectCatalogId),
                                    ["candidate_catalog_ids"] = advisoryFieldPlacement
                                        ? new JsonArray(effectCatalogId)
                                        : new JsonArray(),
                                    ["decision_operation_id"] = "analyze",
                                    ["conditional_mode"] = "all_on_value",
                                    ["reason"] = "The one effect executes only for the effect value."
                                }),
                            ["constraint_matches"] = new JsonArray()
                        }
                    };
                }
                return new LLMResponse { Text = generated };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateConditionalCompositionFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matcherCalls);
        var capabilities = Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        var activation = Assert.IsType<JsonObject>(Assert.Single(
            capabilities.OfType<JsonObject>(),
            static capability => capability["activation"] is JsonObject)["activation"]);
        Assert.Equal("all_on_value", activation["mode"]!.GetValue<string>());
        Assert.Equal("EFFECT", activation["branch_value"]!.GetValue<string>());
        Assert.Equal("NO_EFFECT", Assert.Single(Assert.IsType<JsonArray>(
            activation["no_effect_values"]))!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_RecoversReviewDecisionSourceFromDeclaredArtifactComposition()
    {
        var matchingCalls = 0;
        var generationCalls = 0;
        var human = new RecordingHumanInputProvider(new JsonObject());
        var telemetry = new Mock<IWorkflowTelemetry>();
        var workflowSpan = new Mock<IWorkflowSpan>();
        var stepSpan = new Mock<IStepSpan>();
        var internalSpan = new Mock<ITelemetrySpan>();
        var normalizationEvents = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<ITelemetrySpan>(), It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.StepStart(It.IsAny<ITelemetrySpan>(), It.IsAny<StepTelemetryInfo>()))
            .Returns(stepSpan.Object);
        telemetry.Setup(value => value.SpanStart(It.IsAny<ITelemetrySpan>(), It.IsAny<TelemetrySpanInfo>()))
            .Returns(internalSpan.Object);
        internalSpan.Setup(value => value.AddEvent(
                "gnougo-flow.plan.capability_matching.normalization",
                It.IsAny<IReadOnlyList<KeyValuePair<string, object?>>?>()))
            .Callback((string _, IReadOnlyList<KeyValuePair<string, object?>>? attributes) =>
                normalizationEvents.Add(attributes ?? []));

        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return RecoveredReviewDecisionInventoryResponse();
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return RecoveredReviewDecisionMatchingResponse(request.Prompt);
                }

                generationCalls++;
                return new LLMResponse { Text = ValidRecoveredReviewDecisionWorkflow };
            });

        var plan = ConditionalInferredPlan().Replace(
            "Review a change and publish whichever decision is determined at runtime without human confirmation.",
            "Review one materialized comparison and submit its runtime decision after one confirmation.",
            StringComparison.Ordinal);
        var result = await ExecuteAsync(
            plan,
            llm.Object,
            CreateRecoveredReviewDecisionFactory(),
            human,
            telemetry.Object);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matchingCalls);
        Assert.Equal(1, generationCalls);
        Assert.Empty(human.Requests);
        var capabilities = Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        Assert.Equal(1, capabilities.OfType<JsonObject>().Count(static capability =>
            capability["method"]?.GetValue<string>() == "git_clone"));
        var cloneCapability = Assert.Single(capabilities.OfType<JsonObject>(), static capability =>
            capability["method"]?.GetValue<string>() == "git_clone");
        Assert.Equal("materialize_change", cloneCapability["operation_id"]!.GetValue<string>());
        Assert.Equal(1, capabilities.OfType<JsonObject>().Count(static capability =>
            capability["method"]?.GetValue<string>() == "git_compare_refs"));
        Assert.Equal(1, capabilities.OfType<JsonObject>().Count(static capability =>
            capability["method"]?.GetValue<string>() == "copilot_review"));
        Assert.Equal(1, capabilities.OfType<JsonObject>().Count(static capability =>
            capability["method"]?.GetValue<string>() == "human.input"));
        Assert.Equal(3, capabilities.OfType<JsonObject>().Count(static capability =>
            capability["method"]?.GetValue<string>() == "submit_review"));
        var activations = capabilities.OfType<JsonObject>()
            .Where(static capability => capability["activation"] is JsonObject)
            .Select(static capability => (JsonObject)capability["activation"]!)
            .ToArray();
        Assert.Equal(3, activations.Length);
        Assert.All(activations, activation =>
        {
            Assert.Equal("review_change", activation["decision_operation_id"]!.GetValue<string>());
            Assert.Equal("structured_output", activation["decision_contract_source"]!.GetValue<string>());
        });
        var canonicalization = Assert.Single(normalizationEvents, attributes =>
            attributes.Any(item => item.Key == "gnougo-flow.plan.capability_matching.reason_code"
                                   && Equals(item.Value, "conditional_decision_source_canonicalized")))
            .ToDictionary(static item => item.Key, static item => item.Value);
        Assert.Equal("submit_review", canonicalization["gnougo-flow.plan.capability_matching.operation_id"]);
        Assert.Equal("review_change", canonicalization["gnougo-flow.plan.capability_matching.decision_operation_id"]);
        Assert.Equal("structured_output", canonicalization["gnougo-flow.plan.capability_matching.contract_source"]);
        Assert.NotEqual(string.Empty, canonicalization["gnougo-flow.plan.capability_matching.producer_catalog_id"]);
        Assert.DoesNotContain(canonicalization.Keys, static key => key.Contains("description", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InferredPreflight_RecoversTypedEnumDecisionThroughDeclaredLocalAlias()
    {
        var matchingCalls = 0;
        var human = new RecordingHumanInputProvider(new JsonObject());
        var generatedWorkflow = ValidNoEffectConditionalWorkflow.Replace(
            "      - id: publish_decision",
            "      - id: confirm_submission\n        type: human.input\n        input:\n          mode: confirm\n          prompt: Submit the prepared result?\n          choices: [confirm, cancel]\n      - id: publish_decision",
            StringComparison.Ordinal);
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return DecisionRecoveryInventoryResponse(
                        includeAlias: true,
                        includeSecondSource: false,
                        declaredPublishInputs: ["decision_alias", "confirm_submission"],
                        allowNoEffectOutcome: true);
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return DecisionRecoveryMatchingResponse(
                        request.Prompt,
                        sourceACatalogIds: [CatalogIdForMethod(request.Prompt, "analyze_change")],
                        includeAlias: true,
                        includeSecondSource: false,
                        branchValues: ["APPROVE", "REQUEST_CHANGES"]);
                }
                return new LLMResponse { Text = generatedWorkflow };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateConditionalReviewFactory(["APPROVE", "REQUEST_CHANGES", "INCONCLUSIVE"]),
            human);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matchingCalls);
        Assert.Empty(human.Requests);
        var capabilities = Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        var activations = capabilities.OfType<JsonObject>()
            .Where(static capability => capability["activation"] is JsonObject)
            .Select(static capability => (JsonObject)capability["activation"]!)
            .ToArray();
        Assert.Equal(2, activations.Length);
        Assert.All(activations, activation =>
        {
            Assert.Equal("analyze", activation["decision_operation_id"]!.GetValue<string>());
            Assert.Equal("capability_output", activation["decision_contract_source"]!.GetValue<string>());
            Assert.Equal("INCONCLUSIVE", Assert.Single(Assert.IsType<JsonArray>(activation["no_effect_values"]))!.GetValue<string>());
        });
    }

    [Fact]
    public async Task InferredPreflight_SynthesizesLocalDecisionForMultipleDeclaredSources()
    {
        const string decisionField = "conditional_decision_a5d47a4311d759db";
        var matchingCalls = 0;
        var generationCalls = 0;
        var human = new RecordingHumanInputProvider(new JsonObject());
        var normalizationEvents = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        var telemetry = CreateNormalizationTelemetry(normalizationEvents);
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return MultiSourceLocalDecisionInventoryResponse();
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return MultiSourceLocalDecisionMatchingResponse(request.Prompt);
                }

                generationCalls++;
                return new LLMResponse { Text = MultiSourceLocalDecisionWorkflow(decisionField) };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateMultiSourceLocalDecisionFactory(),
            human,
            telemetry);

        Assert.True(result.Success, $"{result.Error?.Code}: {result.Error?.Message} {result.Error?.Details}");
        Assert.Equal(1, matchingCalls);
        Assert.Equal(1, generationCalls);
        Assert.Empty(human.Requests);
        var capabilities = Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        var evaluator = Assert.Single(capabilities.OfType<JsonObject>(), capability =>
            capability["method"]?.GetValue<string>() == "decision.evaluate");
        Assert.Equal("compute_decisions", evaluator["operation_id"]!.GetValue<string>());
        Assert.Equal(2, Assert.IsType<JsonArray>(evaluator["input_operation_ids"]).Count);
        var activations = capabilities.OfType<JsonObject>()
            .Where(static capability => capability["activation"] is JsonObject)
            .Select(static capability => (JsonObject)capability["activation"]!)
            .ToArray();
        Assert.Equal(2, activations.Length);
        Assert.All(activations, activation =>
        {
            Assert.Equal("local_decision", activation["decision_contract_source"]!.GetValue<string>());
            Assert.Equal($"/{decisionField}", activation["decision_output_path"]!.GetValue<string>());
            Assert.Equal(2, Assert.IsType<JsonArray>(activation["decision_input_operation_ids"]).Count);
        });

        var synthesized = Assert.Single(normalizationEvents, attributes => attributes.Any(item =>
            item.Key == "gnougo-flow.plan.capability_matching.reason_code"
            && Equals(item.Value, "conditional_local_decision_contract_synthesized")));
        Assert.DoesNotContain(synthesized, static item =>
            item.Key.Contains("catalog_id", StringComparison.Ordinal)
            || item.Key.Contains("description", StringComparison.Ordinal)
            || item.Key.Contains("prompt", StringComparison.Ordinal)
            || item.Key.Contains("answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InferredPreflight_UsesOneLocalEvaluatorForSelectorAndOrderedEffectDecisions()
    {
        const string publishField = "conditional_decision_a5d47a4311d759db";
        const string notifyField = "conditional_decision_6cd6f41455d78245";
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return MultiFieldLocalDecisionInventoryResponse();
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                    return MultiFieldLocalDecisionMatchingResponse(request.Prompt);
                return new LLMResponse
                {
                    Text = MultiFieldLocalDecisionWorkflow(publishField, notifyField)
                };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateMultiFieldLocalDecisionFactory());

        Assert.True(result.Success, $"{result.Error?.Code}: {result.Error?.Message} {result.Error?.Details}");
        var capabilities = Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        Assert.Single(capabilities.OfType<JsonObject>(), capability =>
            capability["method"]?.GetValue<string>() == "decision.evaluate");
        var activations = capabilities.OfType<JsonObject>()
            .Where(static capability => capability["activation"] is JsonObject)
            .Select(static capability => (JsonObject)capability["activation"]!)
            .ToArray();
        Assert.Equal(4, activations.Length);
        Assert.Equal(
            new[] { $"/{notifyField}", $"/{publishField}" },
            activations.Select(static activation => activation["decision_output_path"]!.GetValue<string>())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        Assert.Equal(2, activations.Count(static activation =>
            activation["mode"]?.GetValue<string>() == "exactly_one"));
        Assert.Equal(2, activations.Count(static activation =>
            activation["mode"]?.GetValue<string>() == "all_on_value"));
        Assert.All(activations, activation => Assert.Equal(
            "local_decision",
            activation["decision_contract_source"]!.GetValue<string>()));
    }

    [Fact]
    public async Task InferredPreflight_ValidatesLocalDecisionThroughTypedWorkflowBoundaries()
    {
        const string decisionField = "conditional_decision_a5d47a4311d759db";
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return MultiSourceLocalDecisionInventoryResponse();
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                    return MultiSourceLocalDecisionMatchingResponse(request.Prompt);
                return new LLMResponse
                {
                    Text = CrossWorkflowLocalDecisionWorkflow(decisionField)
                };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateMultiSourceLocalDecisionFactory());

        Assert.True(result.Success, $"{result.Error?.Code}: {result.Error?.Message} {result.Error?.Details}");
    }

    [Fact]
    public async Task InferredPreflight_RejectsLocalDecisionThatOmitsADeclaredUpstreamOperation()
    {
        const string decisionField = "conditional_decision_a5d47a4311d759db";
        var unsafeWorkflow = CrossWorkflowLocalDecisionWorkflow(decisionField).Replace(
            "when: ${data.inputs.secondary_signal}",
            "when: ${data.inputs.primary_signal}",
            StringComparison.Ordinal);
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return MultiSourceLocalDecisionInventoryResponse();
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                    return MultiSourceLocalDecisionMatchingResponse(request.Prompt);
                return new LLMResponse { Text = unsafeWorkflow };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateMultiSourceLocalDecisionFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal(
            "conditional_local_decision_inputs_unproven",
            result.Error.Details!["validation_issue"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task InferredPreflight_OffersOnlyBehavioralReadOnlyRelaxationForConditionalWrites(
        int selectedOption,
        bool expectSuccess)
    {
        const string readOnlyAnswer = "Continue read-only without the unresolved external writes";
        var human = new OptionSelectingHumanInputProvider(selectedOption);
        var clarificationTelemetry = CreateStepAttributeTelemetry(out var telemetryAttributes);
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("workflow intent clarification analyst", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["outcome"] = "sufficient",
                            ["reason"] = "The requested behavior is initially clear.",
                            ["questions"] = new JsonArray()
                        }
                    };
                }

                var relaxed = request.Prompt.Contains(readOnlyAnswer, StringComparison.Ordinal);
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return relaxed ? ReadOnlyInventoryResponse() : MultiSourceLocalDecisionInventoryResponse();
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return relaxed || !request.Prompt.Contains("compute_decisions", StringComparison.Ordinal)
                        ? ReadOnlyMatchingResponse(request.Prompt)
                        : MultiSourceLocalDecisionMatchingResponse(request.Prompt);
                }

                return new LLMResponse { Text = ReadOnlyWorkflow() };
            });

        var result = await ExecuteAsync(
            ConditionalWriteRelaxationPlan(),
            llm.Object,
            CreateMultiSourceLocalDecisionFactory(),
            human,
            clarificationTelemetry);

        Assert.True(
            result.Success == expectSuccess,
            $"{result.Error?.Code}: {result.Error?.Message} {result.Error?.Details}");
        var request = Assert.Single(human.Requests);
        Assert.Contains("safe read-only result", request.Prompt, StringComparison.Ordinal);
        var field = Assert.Single(request.Fields!);
        Assert.Equal(
            new[]
            {
                "Preserve the requested write behavior and stop",
                readOnlyAnswer
            },
            field.Options);
        var serializedForm = HumanInputContract.BuildRequestPayload(request).ToJsonString();
        Assert.DoesNotContain("cap_", serializedForm, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog", serializedForm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server", serializedForm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool", serializedForm, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, telemetryAttributes["gnougo-flow.plan.intent_clarification.forms_used"]);
        Assert.Equal(1, telemetryAttributes["gnougo-flow.plan.intent_clarification.questions_used"]);
        Assert.Equal(
            "conditional_write_relaxation",
            telemetryAttributes["gnougo-flow.plan.intent_clarification.last_stage"]);

        if (expectSuccess)
        {
            var capabilities = Assert.IsType<JsonArray>(
                result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
            Assert.DoesNotContain(capabilities.OfType<JsonObject>(), capability =>
                string.Equals(capability["external_effect_kind"]?.GetValue<string>(), "write", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
            Assert.Equal(1, result.Error.Details!["clarification_rounds"]!.GetValue<int>());
            Assert.Equal(1, result.Error.Details["clarification_questions"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task InferredPreflight_FailsClosedWhenLocalDecisionEvaluatorIsPolicyDenied()
    {
        var human = new RecordingHumanInputProvider(new JsonObject());
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return MultiSourceLocalDecisionInventoryResponse();
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                    return MultiSourceLocalDecisionMatchingResponse(request.Prompt);
                throw new InvalidOperationException("Generation must not run without a policy-allowed decision evaluator.");
            });

        var result = await ExecuteAsync(
            PolicyDeniedLocalDecisionPlan(),
            llm.Object,
            CreateMultiSourceLocalDecisionFactory(),
            human);

        Assert.False(result.Success);
        Assert.True(
            string.Equals(result.Error!.Code, ErrorCodes.CapabilityPreflightUnavailable, StringComparison.Ordinal),
            $"{result.Error.Code}: {result.Error.Message} {result.Error.Details}");
        Assert.Equal("conditional_decision_contract_gap", result.Error.Details!["reason"]!.GetValue<string>());
        Assert.Empty(human.Requests);
    }

    [Fact]
    public async Task InferredPreflight_RecoversUniqueMaximalArtifactSemanticRoot()
    {
        const string generatedWorkflow = """
            version: 1
            name: generated-maximal-artifact-decision
            skill:
              description: Analyze two declared artifact views and publish one runtime result.
              tags: [generated]
              inputs: {}
              outputs: {}
            workflows:
              main:
                steps:
                  - id: materialize
                    type: mcp.call
                    input:
                      server: artifact-source
                      kind: tool
                      method: create_workspace
                      request: {}
                  - id: build_payload
                    type: mcp.call
                    input:
                      server: artifact-payload
                      kind: tool
                      method: create_payload
                      request:
                        projectRoot: ${data.steps.materialize.response.projectRoot}
                  - id: analyze_shallow
                    type: mcp.call
                    input:
                      server: reviewer
                      kind: tool
                      method: analyze_change
                      request:
                        projectRoot: ${data.steps.materialize.response.projectRoot}
                  - id: analyze_complete
                    type: mcp.call
                    input:
                      server: reviewer
                      kind: tool
                      method: analyze_alternative
                      request:
                        projectRoot: ${data.steps.materialize.response.projectRoot}
                        payload: ${data.steps.build_payload.response.payload}
                      structured_output:
                        schema_inline:
                          type: object
                          properties:
                            decision:
                              type: string
                              enum: [APPROVE, REQUEST_CHANGES]
                          required: [decision]
                          additionalProperties: false
                        strict: true
                  - id: confirm_submission
                    type: human.input
                    input:
                      mode: confirm
                      prompt: Submit the prepared result?
                      choices: [confirm, cancel]
                  - id: publish_decision
                    type: switch
                    expr: ${data.steps.analyze_complete.json.decision}
                    cases:
                      - value: APPROVE
                        steps:
                          - id: publish_approve
                            type: mcp.call
                            input:
                              server: github
                              kind: tool
                              method: publish_review
                              request:
                                method: create
                                event: APPROVE
                                body: Approved after analysis.
                      - value: REQUEST_CHANGES
                        steps:
                          - id: publish_changes
                            type: mcp.call
                            input:
                              server: github
                              kind: tool
                              method: publish_review
                              request:
                                method: create
                                event: REQUEST_CHANGES
                                body: Changes requested after analysis.
                    default: []
            """;
        var matchingCalls = 0;
        var human = new RecordingHumanInputProvider(new JsonObject());
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return DecisionRecoveryInventoryResponse(
                        includeAlias: true,
                        includeSecondSource: true,
                        declaredPublishInputs: ["decision_alias", "confirm_submission"],
                        allowNoEffectOutcome: false);
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return DecisionRecoveryMatchingResponse(
                        request.Prompt,
                        sourceACatalogIds: [CatalogIdForMethod(request.Prompt, "analyze_change")],
                        includeAlias: true,
                        includeSecondSource: true,
                        branchValues: ["APPROVE", "REQUEST_CHANGES"]);
                }
                return new LLMResponse { Text = generatedWorkflow };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateMaximalArtifactDecisionFactory(),
            human);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matchingCalls);
        Assert.Empty(human.Requests);
        var capabilities = Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        var activations = capabilities.OfType<JsonObject>()
            .Where(static capability => capability["activation"] is JsonObject)
            .Select(static capability => (JsonObject)capability["activation"]!)
            .ToArray();
        Assert.Equal(2, activations.Length);
        Assert.All(activations, activation =>
        {
            Assert.Equal("analyze_alternative", activation["decision_operation_id"]!.GetValue<string>());
            Assert.Equal("structured_output", activation["decision_contract_source"]!.GetValue<string>());
        });
    }

    [Theory]
    [InlineData("undeclared", "conditional_decision_source_unavailable")]
    [InlineData("missing", "conditional_decision_source_unavailable")]
    [InlineData("materializer", "conditional_decision_source_unavailable")]
    [InlineData("multiple_sources", "conditional_decision_source_ambiguous")]
    [InlineData("multiple_roots", "conditional_decision_source_ambiguous")]
    public async Task InferredPreflight_FailsClosedForUnprovableConditionalDecisionRecovery(
        string scenario,
        string expectedReasonCode)
    {
        var matchingCalls = 0;
        var human = new RecordingHumanInputProvider(new JsonObject { ["unused"] = "unused" });
        var includeSecondSource = scenario == "multiple_sources";
        var declaredInputs = scenario switch
        {
            "undeclared" => new[] { "confirm_submission" },
            "missing" => new[] { "analyze", "confirm_submission" },
            "multiple_sources" => new[] { "analyze", "analyze_alternative", "confirm_submission" },
            _ => new[] { "analyze", "confirm_submission" }
        };
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return DecisionRecoveryInventoryResponse(
                        includeAlias: false,
                        includeSecondSource,
                        declaredInputs,
                        allowNoEffectOutcome: false);
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    var sourceAIds = scenario == "multiple_roots"
                        ? new[]
                        {
                            CatalogIdForMethod(request.Prompt, "opaque_analysis_a"),
                            CatalogIdForMethod(request.Prompt, "opaque_analysis_b")
                        }
                        : new[]
                        {
                            CatalogIdForMethod(request.Prompt, scenario switch
                            {
                                "missing" => "human.input",
                                "materializer" => "create_workspace",
                                _ => "analyze_change"
                            })
                        };
                    return DecisionRecoveryMatchingResponse(
                        request.Prompt,
                        sourceAIds,
                        includeAlias: false,
                        includeSecondSource,
                        branchValues: ["APPROVE", "REQUEST_CHANGES"]);
                }
                throw new InvalidOperationException("Workflow generation must not run for an unprovable decision source.");
            });

        var factory = scenario switch
        {
            "multiple_roots" => CreateOpaqueDecisionRootsFactory(),
            "materializer" => CreateMaterializerDecisionSourceFactory(),
            _ => CreateDecisionRecoveryFactory(includeSecondSource)
        };
        var result = await ExecuteAsync(ClarifyingConditionalInferredPlan(), llm.Object, factory, human);

        Assert.False(result.Success);
        Assert.True(
            string.Equals(result.Error!.Code, ErrorCodes.CapabilityPreflightUnavailable, StringComparison.Ordinal),
            $"{result.Error.Code}: {result.Error.Message} {result.Error.Details}");
        Assert.Equal("conditional_decision_contract_gap", result.Error.Details!["reason"]!.GetValue<string>());
        Assert.Equal(2, matchingCalls);
        Assert.Empty(human.Requests);
        var issue = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Error.Details["matching_issues"])));
        Assert.Equal(expectedReasonCode, issue["reason_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_DoesNotCollapseMalformedSingleBranchConditional()
    {
        var matchingCalls = 0;
        var human = new RecordingHumanInputProvider(new JsonObject { ["unused"] = "unused" });
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return ConditionalInventoryResponse(
                        "Analyze the change and determine a decision.",
                        "Publish whichever decision was determined at runtime.");
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray(
                                new JsonObject
                                {
                                    ["operation_id"] = "analyze",
                                    ["status"] = "matched",
                                    ["catalog_ids"] = new JsonArray(CatalogIdForMethod(request.Prompt, "analyze_change")),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = string.Empty,
                                    ["reason"] = "The analysis capability is selected."
                                },
                                new JsonObject
                                {
                                    ["operation_id"] = "publish",
                                    ["status"] = "conditional",
                                    ["catalog_ids"] = new JsonArray(CatalogIdForBindings(
                                        request.Prompt,
                                        ("/event", "APPROVE"),
                                        ("/method", "create"))),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = "analyze",
                                    ["reason"] = "Only one branch was returned."
                                }),
                            ["constraint_matches"] = new JsonArray()
                        }
                    };
                }
                throw new InvalidOperationException("Workflow generation must not run for a malformed match.");
            });

        var result = await ExecuteAsync(
            ClarifyingConditionalInferredPlan(),
            llm.Object,
            CreateConditionalReviewFactory(),
            human);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal("model_contract_violation", result.Error.Details!["classification"]!.GetValue<string>());
        Assert.Equal("retry_or_change_planning_model", result.Error.Details["recommended_action"]!.GetValue<string>());
        Assert.Equal(2, matchingCalls);
        Assert.Empty(human.Requests);
    }

    [Fact]
    public async Task InferredPreflight_AllowsExplicitNonMutatingDecisionOutcome()
    {
        var llm = CreateConditionalLlm(
            ValidNoEffectConditionalWorkflow,
            allowNoEffectOutcome: true);

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateConditionalReviewFactory(["APPROVE", "REQUEST_CHANGES", "INCONCLUSIVE"]));

        Assert.True(result.Success, result.Error?.Message);
        var capabilities = Assert.IsType<JsonArray>(result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        var activations = capabilities.OfType<JsonObject>()
            .Where(static capability => capability["activation"] is JsonObject)
            .Select(static capability => (JsonObject)capability["activation"]!)
            .ToArray();
        Assert.Equal(2, activations.Length);
        Assert.All(activations, activation =>
        {
            Assert.Equal("capability_output", activation["decision_contract_source"]!.GetValue<string>());
            Assert.Equal("/decision", activation["decision_output_path"]!.GetValue<string>());
            Assert.Equal("INCONCLUSIVE", Assert.Single(Assert.IsType<JsonArray>(activation["no_effect_values"]))!.GetValue<string>());
        });
    }

    [Fact]
    public async Task InferredPreflight_EmitsSanitizedConditionalGroundingTelemetry()
    {
        var telemetry = new Mock<IWorkflowTelemetry>();
        var workflowSpan = new Mock<IWorkflowSpan>();
        var stepSpan = new Mock<IStepSpan>();
        var internalSpan = new Mock<ITelemetrySpan>();
        var groundingEvents = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<ITelemetrySpan>(), It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.StepStart(It.IsAny<ITelemetrySpan>(), It.IsAny<StepTelemetryInfo>()))
            .Returns(stepSpan.Object);
        telemetry.Setup(value => value.SpanStart(It.IsAny<ITelemetrySpan>(), It.IsAny<TelemetrySpanInfo>()))
            .Returns(internalSpan.Object);
        internalSpan.Setup(value => value.AddEvent(
                "gnougo-flow.plan.capability_matching.conditional_grounding",
                It.IsAny<IReadOnlyList<KeyValuePair<string, object?>>?>()))
            .Callback((string _, IReadOnlyList<KeyValuePair<string, object?>>? attributes) =>
                groundingEvents.Add(attributes ?? []));

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            CreateConditionalLlm(
                ValidNoEffectConditionalWorkflow,
                allowNoEffectOutcome: true).Object,
            CreateConditionalReviewFactory(["APPROVE", "REQUEST_CHANGES", "INCONCLUSIVE"]),
            telemetry: telemetry.Object);

        Assert.True(result.Success, result.Error?.Message);
        var attributes = Assert.Single(groundingEvents)
            .ToDictionary(static item => item.Key, static item => item.Value);
        Assert.Equal("initial", attributes["gnougo-flow.plan.capability_matching.attempt"]);
        Assert.Equal("capability_output", attributes["gnougo-flow.plan.capability_matching.contract_source"]);
        Assert.Equal("INCONCLUSIVE", attributes["gnougo-flow.plan.capability_matching.no_effect_values"]);
        Assert.NotEqual(string.Empty, attributes["gnougo-flow.plan.capability_matching.producer_catalog_id"]);
        Assert.DoesNotContain(attributes.Keys, static key => key.Contains("description", StringComparison.Ordinal));
        Assert.DoesNotContain(attributes.Keys, static key => key.Contains("reason", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InferredPreflight_SynthesizesStrictStructuredDecisionForOpaqueProducer()
    {
        var llm = CreateConditionalLlm(
            ValidStructuredNoEffectConditionalWorkflow,
            allowNoEffectOutcome: true);

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateConditionalReviewFactory(exposeDecisionOutput: false));

        Assert.True(result.Success, result.Error?.Message);
        var capabilities = Assert.IsType<JsonArray>(result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        var activations = capabilities.OfType<JsonObject>()
            .Where(static capability => capability["activation"] is JsonObject)
            .Select(static capability => (JsonObject)capability["activation"]!)
            .ToArray();
        Assert.Equal(2, activations.Length);
        Assert.All(activations, activation =>
        {
            Assert.Equal("structured_output", activation["decision_contract_source"]!.GetValue<string>());
            Assert.Equal("/json/decision", activation["decision_output_path"]!.GetValue<string>());
            Assert.Equal("NO_EFFECT", Assert.Single(Assert.IsType<JsonArray>(activation["no_effect_values"]))!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(activation["decision_producer_catalog_id"]!.GetValue<string>()));
        });
    }

    [Fact]
    public void StructuredDecisionProducerLeafContract_AcceptsOnlyUnchangedStrictEnumBoundary()
    {
        const string validYaml = """
            version: 1
            workflows:
              analysis_leaf:
                steps:
                  - id: analyze
                    type: llm.call
                    input:
                      prompt: Analyze the supplied value.
                      structured_output:
                        strict: true
                        schema_inline:
                          type: object
                          properties:
                            decision:
                              type: string
                              enum: [APPROVE, REQUEST_CHANGES, NO_EFFECT]
                          required: [decision]
                          additionalProperties: false
                  - id: project
                    type: set
                    input:
                      decision: ${data.steps.analyze.json.decision}
                    output_schema:
                      type: object
                      properties:
                        decision:
                          type: string
                          enum: [APPROVE, REQUEST_CHANGES, NO_EFFECT]
                      required: [decision]
                      additionalProperties: false
                outputs:
                  decision:
                    expr: ${data.steps.project.decision}
                    type: string
                    enum: [APPROVE, REQUEST_CHANGES, NO_EFFECT]
            """;
        var activation = new McpCapabilityActivation(
            "exactly_one",
            "publish",
            "analyze",
            "APPROVE")
        {
            DecisionOutputPath = "/json/decision",
            AllowedValues = ["APPROVE", "REQUEST_CHANGES", "NO_EFFECT"],
            NoEffectValues = ["NO_EFFECT"],
            DecisionContractSource = "structured_output",
            DecisionProducerCatalogId = "cap-analyze"
        };

        var valid = WorkflowParser.Parse(validYaml);
        var source = valid.Workflows["analysis_leaf"].Steps[0];
        Assert.True(WorkflowPlanExecutor.StructuredDecisionProducerLeafContractIsValid(
            valid,
            "analysis_leaf",
            source,
            activation));

        var normalized = WorkflowParser.Parse(validYaml.Replace(
            "expr: ${data.steps.project.decision}",
            "expr: ${coalesce(data.steps.project.decision, 'NO_EFFECT')}",
            StringComparison.Ordinal));
        Assert.False(WorkflowPlanExecutor.StructuredDecisionProducerLeafContractIsValid(
            normalized,
            "analysis_leaf",
            normalized.Workflows["analysis_leaf"].Steps[0],
            activation));

        var nonStrict = WorkflowParser.Parse(validYaml.Replace(
            "strict: true",
            "strict: false",
            StringComparison.Ordinal));
        Assert.False(WorkflowPlanExecutor.StructuredDecisionProducerLeafContractIsValid(
            nonStrict,
            "analysis_leaf",
            nonStrict.Workflows["analysis_leaf"].Steps[0],
            activation));

        var wrongPath = WorkflowParser.Parse(validYaml.Replace(
            "decision: ${data.steps.analyze.json.decision}",
            "decision: ${data.steps.analyze.json.summary}",
            StringComparison.Ordinal));
        Assert.False(WorkflowPlanExecutor.StructuredDecisionProducerLeafContractIsValid(
            wrongPath,
            "analysis_leaf",
            wrongPath.Workflows["analysis_leaf"].Steps[0],
            activation));
    }

    [Fact]
    public async Task InferredPreflight_TracesLocalDecisionChainToStructuredProducer()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return ConditionalInventoryWithLocalDecisionResponse();
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return ConditionalMatchingWithLocalDecisionResponse(
                        CatalogIdForMethod(request.Prompt, "analyze_change"),
                        CatalogIdForBindings(request.Prompt, ("/event", "APPROVE"), ("/method", "create")),
                        CatalogIdForBindings(request.Prompt, ("/event", "REQUEST_CHANGES"), ("/method", "create")));
                }
                return new LLMResponse { Text = ValidStructuredNoEffectConditionalWorkflow };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateConditionalReviewFactory(exposeDecisionOutput: false));

        Assert.True(result.Success, result.Error?.Message);
        var capabilities = Assert.IsType<JsonArray>(result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        var activations = capabilities.OfType<JsonObject>()
            .Where(static capability => capability["activation"] is JsonObject)
            .Select(static capability => (JsonObject)capability["activation"]!)
            .ToArray();
        Assert.Equal(2, activations.Length);
        Assert.All(activations, activation =>
        {
            Assert.Equal("analyze", activation["decision_operation_id"]!.GetValue<string>());
            Assert.Equal("structured_output", activation["decision_contract_source"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task InferredPreflight_RejectsMissingSynthesizedStructuredDecisionSchema()
    {
        var nonStrictWorkflow = ValidStructuredNoEffectConditionalWorkflow.Replace(
            "strict: true",
            "strict: false",
            StringComparison.Ordinal);
        var llm = CreateConditionalLlm(
            nonStrictWorkflow,
            allowNoEffectOutcome: true);

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateConditionalReviewFactory(exposeDecisionOutput: false));

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal("conditional_activation_invalid", result.Error.Details!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_UnlocksDecisionProducerDuringConditionalRepair()
    {
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return ConditionalInventoryResponse(
                        "Analyze the change and determine a decision.",
                        "Publish whichever review decision was determined at runtime.");
                }

                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    var approve = CatalogIdForBindings(request.Prompt, ("/event", "APPROVE"), ("/method", "create"));
                    var requestChanges = CatalogIdForBindings(request.Prompt, ("/event", "REQUEST_CHANGES"), ("/method", "create"));
                    var decisionIds = matcherCalls == 1
                        ? new[]
                        {
                            CatalogIdForMethod(request.Prompt, "prepare_review"),
                            CatalogIdForMethod(request.Prompt, "collect_review_evidence")
                        }
                        : new[] { CatalogIdForMethod(request.Prompt, "analyze_change") };
                    return ConditionalMatchingResponse(decisionIds, approve, requestChanges);
                }

                return new LLMResponse { Text = ValidConditionalWorkflow };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateRepairableConditionalReviewFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, matcherCalls);
        var capabilities = Assert.IsType<JsonArray>(result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        Assert.Contains(capabilities.OfType<JsonObject>(), capability =>
            capability["operation_id"]?.GetValue<string>() == "analyze"
            && capability["method"]?.GetValue<string>() == "analyze_change");
        Assert.DoesNotContain(capabilities.OfType<JsonObject>(), capability =>
            capability["operation_id"]?.GetValue<string>() == "analyze"
            && capability["method"]?.GetValue<string>() == "prepare_review");
    }

    [Fact]
    public async Task InferredPreflight_ReportsConditionalContractGapAsUnsupported()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return ConditionalInventoryResponse(
                        "Analyze the change and determine a decision.",
                        "Publish whichever review decision was determined at runtime.");
                }

                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    var approve = CatalogIdForBindings(request.Prompt, ("/event", "APPROVE"), ("/method", "create"));
                    var requestChanges = CatalogIdForBindings(request.Prompt, ("/event", "REQUEST_CHANGES"), ("/method", "create"));
                    return ConditionalMatchingResponse(
                        [
                            CatalogIdForMethod(request.Prompt, "prepare_review"),
                            CatalogIdForMethod(request.Prompt, "collect_review_evidence")
                        ],
                        approve,
                        requestChanges);
                }

                return new LLMResponse { Text = ValidConditionalWorkflow };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateRepairableConditionalReviewFactory(includeTypedAnalyzer: false));

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal("conditional_decision_contract_gap", result.Error.Details!["reason"]!.GetValue<string>());
        Assert.Equal(
            "configure_decision_contract_or_enable_structured_projection",
            result.Error.Details["recommended_action"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_ReportsMixedUnavailableAndDecisionContractGapAsUnsupported()
    {
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return ConditionalInventoryResponse(
                        "Analyze the change and determine a decision.",
                        "Apply whichever effect was determined at runtime.");
                }

                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    var firstBranch = CatalogIdForBindings(
                        request.Prompt,
                        ("/event", "APPROVE"),
                        ("/method", "create"));
                    var secondBranch = CatalogIdForBindings(
                        request.Prompt,
                        ("/event", "REQUEST_CHANGES"),
                        ("/method", "create"));
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray(
                                new JsonObject
                                {
                                    ["operation_id"] = "analyze",
                                    ["status"] = "unavailable",
                                    ["catalog_ids"] = new JsonArray(),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = string.Empty,
                                    ["reason"] = "No selected capability safely produces the required decision."
                                },
                                new JsonObject
                                {
                                    ["operation_id"] = "publish",
                                    ["status"] = "conditional",
                                    ["catalog_ids"] = new JsonArray(firstBranch, secondBranch),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = "analyze",
                                    ["reason"] = "The unavailable producer was declared as the decision source."
                                }),
                            ["constraint_matches"] = new JsonArray()
                        }
                    };
                }

                return new LLMResponse { Text = ValidConditionalWorkflow };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateConditionalReviewFactory());

        Assert.False(result.Success);
        Assert.Equal(2, matcherCalls);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal("unsupported", result.Error.Details!["planning_outcome"]!.GetValue<string>());
        Assert.Equal(
            "configure_capability_or_decision_contract_or_revise_request",
            result.Error.Details["recommended_action"]!.GetValue<string>());
        var issues = Assert.IsType<JsonArray>(result.Error.Details["matching_issues"])
            .OfType<JsonObject>()
            .Select(static issue => issue["status"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("unavailable", issues);
        Assert.Contains("contract_gap", issues);
    }

    [Fact]
    public async Task InferredPreflight_ValidatesConditionalDecisionAcrossLocalWorkflowContracts()
    {
        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            CreateConditionalLlm(ValidCrossWorkflowConditionalWorkflow).Object,
            CreateConditionalReviewFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("assert.non_null")]
    public async Task InferredPreflight_ValidatesConditionalDecisionThroughTransparentSameNamedProjection(
        string projectionType)
    {
        var projectedWorkflow = AddConditionalDecisionProjection(
            ValidCrossWorkflowConditionalWorkflow,
            projectionType,
            "decision",
            "${data.inputs.decision}");

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            CreateConditionalLlm(projectedWorkflow).Object,
            CreateConditionalReviewFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Theory]
    [InlineData("routed_decision", "${data.inputs.decision}")]
    [InlineData("decision", "APPROVE")]
    public async Task InferredPreflight_RejectsConditionalDecisionThroughUnprovenProjection(
        string projectedField,
        string projectedValue)
    {
        var projectedWorkflow = AddConditionalDecisionProjection(
            ValidCrossWorkflowConditionalWorkflow,
            "assert.non_null",
            projectedField,
            projectedValue);

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            CreateConditionalLlm(projectedWorkflow).Object,
            CreateConditionalReviewFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal("conditional_activation_invalid", result.Error.Details!["reason"]!.GetValue<string>());
        Assert.Equal("conditional_decision_lineage_unproven", result.Error.Details["validation_issue"]!.GetValue<string>());
        Assert.Equal("main_decision_routing", result.Error.Details["repair_scope"]!.GetValue<string>());
        Assert.Equal("publish_review", result.Error.Details["workflow"]!.GetValue<string>());
        Assert.Equal("publish_decision", result.Error.Details["switch_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_RejectsConditionalDecisionFromUnprovenCallerInput()
    {
        var unsafeWorkflow = AddConditionalDecisionProjection(
            ValidCrossWorkflowConditionalWorkflow.Replace(
            "decision: ${data.steps.review.outputs.decision}",
            "decision: ${data.inputs.forced_decision}",
            StringComparison.Ordinal),
            "assert.non_null",
            "decision",
            "${data.inputs.decision}");

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            CreateConditionalLlm(unsafeWorkflow).Object,
            CreateConditionalReviewFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal("conditional_activation_invalid", result.Error.Details!["reason"]!.GetValue<string>());
        Assert.Equal("conditional_decision_lineage_unproven", result.Error.Details["validation_issue"]!.GetValue<string>());
        Assert.Equal("main_decision_routing", result.Error.Details["repair_scope"]!.GetValue<string>());
    }

    private static string AddConditionalDecisionProjection(
        string workflow,
        string projectionType,
        string projectedField,
        string projectedValue)
        => workflow.Replace(
            "    steps:\n      - id: publish_decision\n        type: switch\n        expr: ${data.inputs.decision}",
            $"    steps:\n      - id: refine_decision\n        type: {projectionType}\n        input:\n          {projectedField}: {projectedValue}\n      - id: publish_decision\n        type: switch\n        expr: ${{data.steps.refine_decision.{projectedField}}}",
            StringComparison.Ordinal);

    [Fact]
    public async Task InferredPreflight_IgnoresRepeatedAdvisoryIdsForFinalConditionalMatches()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return ConditionalInventoryResponse(
                        "Analyze the change and determine a decision.",
                        "Publish whichever review decision was determined at runtime.");
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    var analyze = CatalogIdForMethod(request.Prompt, "analyze_change");
                    var approve = CatalogIdForBindings(request.Prompt, ("/event", "APPROVE"), ("/method", "create"));
                    var requestChanges = CatalogIdForBindings(request.Prompt, ("/event", "REQUEST_CHANGES"), ("/method", "create"));
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray(
                                new JsonObject
                                {
                                    ["operation_id"] = "analyze",
                                    ["status"] = "matched",
                                    ["catalog_ids"] = new JsonArray(analyze),
                                    ["candidate_catalog_ids"] = new JsonArray(analyze),
                                    ["decision_operation_id"] = string.Empty,
                                    ["reason"] = "The analysis operation is selected."
                                },
                                new JsonObject
                                {
                                    ["operation_id"] = "publish",
                                    ["status"] = "conditional",
                                    ["catalog_ids"] = new JsonArray(approve, requestChanges),
                                    ["candidate_catalog_ids"] = new JsonArray(approve, requestChanges),
                                    ["decision_operation_id"] = "analyze",
                                    ["reason"] = "Exactly one publication selector is chosen from the runtime analysis."
                                }),
                            ["constraint_matches"] = new JsonArray()
                        }
                    };
                }
                return new LLMResponse { Text = ValidConditionalWorkflow };
            });

        var result = await ExecuteAsync(ConditionalInferredPlan(), llm.Object, CreateConditionalReviewFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task InferredPreflight_CanonicalizesAdvisorySelectorFamilyWithWholeToolParent()
    {
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return ConditionalInventoryResponse(
                        "Analyze the input and determine a closed decision.",
                        "Apply whichever effect was determined at runtime.");
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    var analyze = CatalogIdForMethod(request.Prompt, "analyze_change");
                    var wholeTool = CatalogIdForWholeTool(request.Prompt, "publish_review");
                    var firstBranch = CatalogIdForBindings(request.Prompt, ("/event", "APPROVE"), ("/method", "create"));
                    var secondBranch = CatalogIdForBindings(request.Prompt, ("/event", "REQUEST_CHANGES"), ("/method", "create"));
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray(
                                new JsonObject
                                {
                                    ["operation_id"] = "analyze",
                                    ["status"] = "matched",
                                    ["catalog_ids"] = new JsonArray(analyze),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = string.Empty,
                                    ["reason"] = "The selected capability produces the decision."
                                },
                                new JsonObject
                                {
                                    ["operation_id"] = "publish",
                                    ["status"] = "matched",
                                    ["catalog_ids"] = new JsonArray(),
                                    ["candidate_catalog_ids"] = new JsonArray(wholeTool, firstBranch, secondBranch),
                                    ["decision_operation_id"] = "analyze",
                                    ["reason"] = "The physical capability and its exact alternatives implement the runtime choice."
                                }),
                            ["constraint_matches"] = new JsonArray()
                        }
                    };
                }
                return new LLMResponse { Text = ValidConditionalWorkflow };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateConditionalReviewFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matcherCalls);
        var capabilities = Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        var conditional = capabilities.OfType<JsonObject>()
            .Where(static capability => capability["operation_id"]?.GetValue<string>() == "publish")
            .ToArray();
        Assert.Equal(2, conditional.Length);
        Assert.All(conditional, static capability =>
        {
            Assert.Equal("conditional", capability["match_status"]!.GetValue<string>());
            Assert.NotEmpty(Assert.IsType<JsonArray>(capability["request_bindings"]));
        });
    }

    [Fact]
    public async Task InferredPreflight_CanonicalizesAdvisorySelectorAncestorChainToUniqueMaximum()
    {
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return ConditionalInventoryResponse(
                        "Analyze the input and determine a closed decision.",
                        "Apply the documented effect for the selected result.");
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    var analyze = CatalogIdForMethod(request.Prompt, "analyze_change");
                    var wholeTool = CatalogIdForWholeTool(request.Prompt, "publish_review");
                    var partialSelector = CatalogIdForBinding(request.Prompt, "/event", "REQUEST_CHANGES");
                    var combinedSelector = CatalogIdForBindings(
                        request.Prompt,
                        ("/event", "REQUEST_CHANGES"),
                        ("/method", "create"));
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray(
                                new JsonObject
                                {
                                    ["operation_id"] = "analyze",
                                    ["status"] = "matched",
                                    ["catalog_ids"] = new JsonArray(analyze),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = string.Empty,
                                    ["reason"] = "The selected capability produces the result."
                                },
                                new JsonObject
                                {
                                    ["operation_id"] = "publish",
                                    ["status"] = "matched",
                                    ["catalog_ids"] = new JsonArray(),
                                    ["candidate_catalog_ids"] = new JsonArray(
                                        wholeTool,
                                        partialSelector,
                                        combinedSelector),
                                    ["decision_operation_id"] = string.Empty,
                                    ["reason"] = "The most-specific selector implements the selected effect."
                                }),
                            ["constraint_matches"] = new JsonArray()
                        }
                    };
                }
                return new LLMResponse { Text = ValidConditionalWorkflow };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateConditionalReviewFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matcherCalls);
        var capabilities = Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        var selected = Assert.Single(
            capabilities.OfType<JsonObject>(),
            static capability => capability["operation_id"]?.GetValue<string>() == "publish");
        Assert.Equal("matched", selected["match_status"]!.GetValue<string>());
        Assert.Equal(2, Assert.IsType<JsonArray>(selected["request_bindings"]).Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InferredPreflight_ClassifiesInsufficientConditionalSelectorSetAsUnavailable(
        bool includeAncestor)
    {
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return ConditionalInventoryResponse(
                        "Analyze the input and determine a closed decision.",
                        "Apply whichever of the required effects was determined at runtime.");
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    var analyze = CatalogIdForMethod(request.Prompt, "analyze_change");
                    var wholeTool = CatalogIdForWholeTool(request.Prompt, "publish_review");
                    var oneBranch = CatalogIdForBindings(
                        request.Prompt,
                        ("/event", "REQUEST_CHANGES"),
                        ("/method", "create"));
                    var publishIds = includeAncestor
                        ? new JsonArray(wholeTool, oneBranch)
                        : new JsonArray(oneBranch);
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray(
                                new JsonObject
                                {
                                    ["operation_id"] = "analyze",
                                    ["status"] = "matched",
                                    ["catalog_ids"] = new JsonArray(analyze),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = string.Empty,
                                    ["reason"] = "The selected capability produces the decision."
                                },
                                new JsonObject
                                {
                                    ["operation_id"] = "publish",
                                    ["status"] = "conditional",
                                    ["catalog_ids"] = publishIds,
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = "analyze",
                                    ["reason"] = "The parent and descendant were presented as separate alternatives."
                                }),
                            ["constraint_matches"] = new JsonArray()
                        }
                    };
                }
                return new LLMResponse { Text = ValidConditionalWorkflow };
            });

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            llm.Object,
            CreateConditionalReviewFactory(publicationValues: ["REQUEST_CHANGES"]));

        Assert.False(result.Success);
        Assert.Equal(2, matcherCalls);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal("unsupported", result.Error.Details!["planning_outcome"]!.GetValue<string>());
        Assert.Equal(
            "configure_capability_or_revise_request",
            result.Error.Details["recommended_action"]!.GetValue<string>());
        var issue = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Error.Details["matching_issues"])));
        Assert.Equal("unavailable", issue["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_NormalizesExactRuntimeChoiceSelectorsToConditional()
    {
        var matcherCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return ConditionalInventoryResponse(
                        "Analyze all changed code and determine the review decision and findings.",
                        "Submit the matching result when checks pass or fail.");
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matcherCalls++;
                    var analyze = CatalogIdForMethod(request.Prompt, "analyze_change");
                    var approve = CatalogIdForBindings(request.Prompt, ("/event", "APPROVE"), ("/method", "create"));
                    var requestChanges = CatalogIdForBindings(request.Prompt, ("/event", "REQUEST_CHANGES"), ("/method", "create"));
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray(
                                new JsonObject
                                {
                                    ["operation_id"] = "analyze",
                                    ["status"] = "matched",
                                    ["catalog_ids"] = new JsonArray(analyze),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = string.Empty,
                                    ["reason"] = "The analysis capability returns the decision."
                                },
                                new JsonObject
                                {
                                    ["operation_id"] = "publish",
                                    ["status"] = "composed",
                                    ["catalog_ids"] = new JsonArray(approve, requestChanges),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = string.Empty,
                                    ["reason"] = "Both exact publication variants are required."
                                }),
                            ["constraint_matches"] = new JsonArray()
                        }
                    };
                }
                return new LLMResponse { Text = ValidConditionalWorkflow };
            });

        var result = await ExecuteAsync(ConditionalInferredPlan(), llm.Object, CreateConditionalReviewFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, matcherCalls);
        var capabilities = Assert.IsType<JsonArray>(result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        Assert.Equal(2, capabilities.OfType<JsonObject>().Count(static capability =>
            capability["match_status"]?.GetValue<string>() == "conditional"));
    }

    [Fact]
    public async Task InferredPreflight_ComposesRequiredReadVariantsWhenDecisionSourceIsUngrounded()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["complete"] = true,
                            ["incomplete_reasons"] = new JsonArray(),
                            ["operations"] = new JsonArray(
                                new JsonObject
                                {
                                    ["id"] = "choose_read",
                                    ["description"] = "Locally choose which required read to execute.",
                                    ["required"] = true,
                                    ["execution_kind"] = "local_processing",
                                    ["external_effect_kind"] = "none",
                                    ["decision_source_operation_id"] = string.Empty
                                },
                                new JsonObject
                                {
                                    ["id"] = "read_inventory",
                                    ["description"] = "Read both required inventory representations.",
                                    ["required"] = true,
                                    ["execution_kind"] = "external_effect",
                                    ["external_effect_kind"] = "read",
                                    ["decision_source_operation_id"] = "choose_read"
                                }),
                            ["constraints"] = new JsonArray()
                        }
                    };
                }

                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    var list = CatalogIdForBinding(request.Prompt, "/method", "list_items");
                    var status = CatalogIdForBinding(request.Prompt, "/method", "get_status");
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray(
                                new JsonObject
                                {
                                    ["operation_id"] = "choose_read",
                                    ["status"] = "local",
                                    ["catalog_ids"] = new JsonArray(),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = string.Empty,
                                    ["reason"] = "The choice is an invented local value."
                                },
                                new JsonObject
                                {
                                    ["operation_id"] = "read_inventory",
                                    ["status"] = "conditional",
                                    ["catalog_ids"] = new JsonArray(list, status),
                                    ["candidate_catalog_ids"] = new JsonArray(),
                                    ["decision_operation_id"] = "choose_read",
                                    ["reason"] = "The local choice selects one read."
                                }),
                            ["constraint_matches"] = new JsonArray()
                        }
                    };
                }

                return new LLMResponse { Text = ValidMultiActionWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateMultiActionFactory());

        Assert.True(result.Success, result.Error?.Message);
        var capabilities = Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"]);
        var readCapabilities = capabilities.OfType<JsonObject>()
            .Where(static capability => capability["operation_id"]?.GetValue<string>() == "read_inventory")
            .ToArray();
        Assert.Equal(2, readCapabilities.Length);
        Assert.All(readCapabilities, static capability =>
        {
            Assert.Equal("composed", capability["match_status"]!.GetValue<string>());
            Assert.Null(capability["activation"]);
        });
    }

    [Theory]
    [InlineData("unconditional")]
    [InlineData("duplicate")]
    [InlineData("mutating_default")]
    [InlineData("when_predicate")]
    [InlineData("wrong_case")]
    public async Task InferredPreflight_RejectsUnsafeConditionalSelectorTopology(string failure)
    {
        var invalid = failure switch
        {
            "unconditional" => ValidConditionalWorkflow.Replace(
                "      - id: publish_decision\n        type: switch",
                "      - id: publish_approve_unconditionally\n        type: mcp.call\n        input:\n          server: github\n          kind: tool\n          method: publish_review\n          request:\n            method: create\n            event: APPROVE\n            body: ${data.steps.analyze.response.justification}\n      - id: publish_decision\n        type: switch",
                StringComparison.Ordinal),
            "duplicate" => ValidConditionalWorkflow.Replace(
                "                    body: ${data.steps.analyze.response.justification}\n          - value: REQUEST_CHANGES",
                "                    body: ${data.steps.analyze.response.justification}\n              - id: publish_approve_again\n                type: mcp.call\n                input:\n                  server: github\n                  kind: tool\n                  method: publish_review\n                  request:\n                    method: create\n                    event: APPROVE\n                    body: ${data.steps.analyze.response.justification}\n          - value: REQUEST_CHANGES",
                StringComparison.Ordinal),
            "mutating_default" => ValidConditionalWorkflow.Replace(
                "        default: []",
                "        default:\n          - id: publish_default\n            type: mcp.call\n            input:\n              server: github\n              kind: tool\n              method: publish_review\n              request:\n                method: create\n                event: APPROVE\n                body: ${data.steps.analyze.response.justification}",
                StringComparison.Ordinal),
            "when_predicate" => ValidConditionalWorkflow.Replace(
                "          - value: APPROVE",
                "          - when: ${data.steps.analyze.response.decision == 'APPROVE'}",
                StringComparison.Ordinal),
            _ => ValidConditionalWorkflow.Replace(
                "          - value: REQUEST_CHANGES",
                "          - value: UNDECLARED_VALUE",
                StringComparison.Ordinal)
        };

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            CreateConditionalLlm(invalid).Object,
            CreateConditionalReviewFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal("conditional_activation_invalid", result.Error.Details!["reason"]!.GetValue<string>());
        Assert.Equal("leaf_topology", result.Error.Details["repair_scope"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(result.Error.Details["validation_issue"]!.GetValue<string>()));
        if (string.Equals(failure, "mutating_default", StringComparison.Ordinal))
            Assert.Equal("conditional_default_mutates", result.Error.Details["validation_issue"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_BatchesEveryInventoryIssueWithExhaustiveDesignQuestions()
    {
        var inventoryCalls = 0;
        var human = new RecordingHumanInputProvider(CompleteInventoryClarificationResponse());
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal)
                    || request.Prompt.Contains("inventory repair analyst", StringComparison.Ordinal))
                {
                    inventoryCalls++;
                    if (inventoryCalls <= 2)
                    {
                        return new LLMResponse
                        {
                            Json = new JsonObject
                            {
                                ["complete"] = false,
                                ["incomplete_reasons"] = new JsonArray(
                                    new JsonObject
                                    {
                                        ["id"] = "missing_scope",
                                        ["description"] = "Specify the required processing scope."
                                    },
                                    new JsonObject
                                    {
                                        ["id"] = "missing_failure_policy",
                                        ["description"] = "Specify how unsupported execution should terminate."
                                    }),
                                ["operations"] = new JsonArray(),
                                ["constraints"] = new JsonArray()
                            }
                        };
                    }

                    return InventoryResponse(("load_object", "Load the requested object.", true));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                    return MatchResponse(("load_object", "mcp", CatalogIdForMethod(request.Prompt, "get_object")));
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(ClarifyingInferredPlan(), llm.Object, CreateNeutralFactory(), human);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, inventoryCalls);
        var request = Assert.Single(human.Requests);
        Assert.Equal(7, request.Fields!.Count);
        Assert.Equal("unresolved_intent_1", request.Fields[0].Name);
        Assert.Equal("unresolved_intent_2", request.Fields[1].Name);
        Assert.Contains(request.Fields, static field => field.Name == "external_effect_boundaries");
    }

    [Fact]
    public async Task InferredPreflight_RequestsOneClarificationAndRerunsCompleteInference()
    {
        var inventoryCalls = 0;
        var matchingCalls = 0;
        var human = new RecordingHumanInputProvider(CompleteClarificationResponse());
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    inventoryCalls++;
                    return InventoryResponse(("load_object", "Load the requested object.", true));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    var candidate = CatalogIdForMethod(request.Prompt, "get_object");
                    return matchingCalls <= 2
                        ? MatchingResponse(("load_object", "ambiguous", new[] { candidate }, new[] { candidate }, "User intent remains ambiguous."))
                        : MatchResponse(("load_object", "mcp", candidate));
                }
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(ClarifyingInferredPlan(), llm.Object, CreateNeutralFactory(), human);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, inventoryCalls);
        Assert.Equal(3, matchingCalls);
        var request = Assert.Single(human.Requests);
        Assert.Equal(HumanInputContract.ModeForm, request.Mode);
        Assert.Equal(6, request.Fields!.Count);
        Assert.Equal("unresolved_choice_1", request.Fields[0].Name);
        Assert.Contains(request.Fields, static field => field.Name == "runtime_decision_rules");
        Assert.Contains(request.Fields, static field => field.Name == "failure_policy");
    }

    [Fact]
    public async Task IntentClarification_SharesBudgetWithCapabilityAmbiguityAndRestartsCompletePreflight()
    {
        var inventoryCalls = 0;
        var matchingCalls = 0;
        var human = new RecordingHumanInputProvider(new JsonObject
        {
            ["scope"] = "Use the recommended focused scope.",
            ["capability_choice"] = "Use the exact read behavior.",
            [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
        });
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("provider-neutral workflow intent clarification analyst", StringComparison.Ordinal))
                {
                    if (request.Prompt.Contains("Clarification stage: up_front", StringComparison.Ordinal))
                        return IntentQuestionsResponse("Clarify the intended scope.", "scope");
                    if (request.Prompt.Contains("Clarification stage: post_answer", StringComparison.Ordinal))
                        return IntentAssessmentResponse("sufficient", "The initial intent is complete.");
                    if (request.Prompt.Contains("Clarification stage: capability_matching", StringComparison.Ordinal))
                        return IntentQuestionsResponse("Clarify the remaining capability behavior.", "capability_choice");
                }
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    inventoryCalls++;
                    return InventoryResponse(("load_object", "Load the requested object.", true));
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    var candidate = CatalogIdForMethod(request.Prompt, "get_object");
                    return matchingCalls <= 2
                        ? MatchingResponse(("load_object", "ambiguous", new[] { candidate }, new[] { candidate }, "User intent remains ambiguous."))
                        : MatchResponse(("load_object", "mcp", candidate));
                }
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(IntentClarifyingInferredPlan(), llm.Object, CreateNeutralFactory(), human);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, human.Requests.Count);
        Assert.All(human.Requests, static request =>
        {
            Assert.True(request.AllowAbandon);
            Assert.All(request.Fields!, static field => Assert.True(field.AllowCustomAnswer));
        });
        Assert.Equal(2, inventoryCalls);
        Assert.Equal(3, matchingCalls);
    }

    [Fact]
    public async Task CapabilityCoverageReview_ExcludesDeclaredWorkflowStructureRequirements()
    {
        var coverageCalls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    var coverage = Evidence("user_request", "Load a configured object");
                    coverage["enforcement_kind"] = "workflow_structure";
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["complete"] = true,
                            ["external_write_confirmation_policy"] = "unspecified",
                            ["external_write_confirmation_evidence"] = Evidence(string.Empty, string.Empty),
                            ["incomplete_reasons"] = new JsonArray(),
                            ["operations"] = new JsonArray(new JsonObject
                            {
                                ["id"] = "load_object",
                                ["description"] = "Load a configured object.",
                                ["required"] = true,
                                ["execution_kind"] = "external_effect",
                                ["external_effect_kind"] = "read",
                                ["input_operation_ids"] = new JsonArray(),
                                ["coverage_requirements"] = new JsonArray(coverage),
                                ["optionality_evidence"] = Evidence(string.Empty, string.Empty),
                                ["decision_source_operation_id"] = string.Empty,
                                ["allow_no_effect_outcome"] = false,
                                ["intent_origin"] = "requested_effect",
                                ["derivation_source_operation_id"] = string.Empty
                            }),
                            ["constraints"] = new JsonArray()
                        }
                    };
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return MatchResponse((
                        "load_object",
                        "mcp",
                        CatalogIdForMethod(request.Prompt, "get_object")));
                }
                if (request.Prompt.Contains("provider-neutral capability coverage reviewer", StringComparison.Ordinal))
                {
                    coverageCalls++;
                    throw new InvalidOperationException("Workflow-structure requirements must not reach capability-card coverage review.");
                }
                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(0, coverageCalls);
    }

    [Fact]
    public async Task CapabilityCoverageReview_CanonicalizesStructuralOnlyGapWithoutRematchOrHumanInput()
    {
        const string request = "Delete every directory created by the workflow during finalization without human confirmation.";
        var matchingCalls = 0;
        var coverageCalls = 0;
        var adjudicationCalls = 0;
        var generationCalls = 0;
        var human = new RecordingHumanInputProvider(new JsonObject());
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest llmRequest, CancellationToken _) =>
            {
                if (llmRequest.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    var coverage = Evidence("user_request", "Delete every directory created by the workflow during finalization");
                    coverage["enforcement_kind"] = "capability_contract";
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["complete"] = true,
                            ["external_write_confirmation_policy"] = "forbidden",
                            ["external_write_confirmation_evidence"] = Evidence("user_request", "without human confirmation"),
                            ["incomplete_reasons"] = new JsonArray(),
                            ["operations"] = new JsonArray(new JsonObject
                            {
                                ["id"] = "cleanup",
                                ["description"] = "Delete every directory created by the workflow during finalization.",
                                ["required"] = true,
                                ["execution_kind"] = "external_effect",
                                ["external_effect_kind"] = "lifecycle",
                                ["input_operation_ids"] = new JsonArray(),
                                ["coverage_requirements"] = new JsonArray(coverage),
                                ["optionality_evidence"] = Evidence(string.Empty, string.Empty),
                                ["decision_source_operation_id"] = string.Empty,
                                ["allow_no_effect_outcome"] = false,
                                ["no_effect_outcome_evidence"] = Evidence(string.Empty, string.Empty),
                                ["intent_origin"] = "requested_effect",
                                ["derivation_source_operation_id"] = string.Empty
                            }),
                            ["constraints"] = new JsonArray()
                        }
                    };
                }
                if (llmRequest.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return MatchResponse((
                        "cleanup",
                        "mcp",
                        CatalogIdForBinding(llmRequest.Prompt, "/action", "delete_directory")));
                }
                if (llmRequest.Prompt.Contains("provider-neutral capability coverage reviewer", StringComparison.Ordinal))
                {
                    coverageCalls++;
                    return CapabilityCoverageResponseForOperation(
                        llmRequest.Prompt,
                        "cleanup",
                        supported: false,
                        "Run one configured action by its exact selector.");
                }
                if (llmRequest.Prompt.Contains("provider-neutral capability coverage gap adjudicator", StringComparison.Ordinal))
                {
                    adjudicationCalls++;
                    return CapabilityCoverageGapAdjudicationResponse(
                        llmRequest.Prompt,
                        "cleanup",
                        "workflow_structure_only",
                        ["cardinality", "scope_iteration", "finalization"],
                        "Run one configured action by its exact selector.");
                }

                generationCalls++;
                return new LLMResponse
                {
                    Text = """
                        version: 1
                        name: generated-cleanup
                        skill:
                          description: Delete generated directories during finalization.
                          inputs: {}
                          outputs: {}
                        workflows:
                          main:
                            steps:
                              - id: cleanup
                                type: mcp.call
                                input:
                                  server: command-runner
                                  kind: tool
                                  method: run_allowed_action
                                  request:
                                    action: delete_directory
                        """
                };
            });

        var plan = InferredPlan().Replace(
            "Load a configured object and optionally notify a consumer.",
            request,
            StringComparison.Ordinal);
        var result = await ExecuteAsync(
            plan,
            llm.Object,
            CreateCleanupSelectorFactory(),
            human);

        Assert.True(result.Success, $"{result.Error?.Code}: {result.Error?.Message} {result.Error?.Details}");
        Assert.Equal(1, matchingCalls);
        Assert.Equal(1, coverageCalls);
        Assert.Equal(1, adjudicationCalls);
        Assert.Equal(1, generationCalls);
        Assert.Empty(human.Requests);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task CapabilityCoverageRelaxation_PreservesOrExplicitlyRelaxesRequirement(
        int selectedOption,
        bool expectSuccess)
    {
        var inventoryCalls = 0;
        var coverageCalls = 0;
        var adjudicationCalls = 0;
        var matchingCalls = 0;
        string? coveragePrompt = null;
        var human = new OptionSelectingHumanInputProvider(selectedOption);
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("provider-neutral workflow intent clarification analyst", StringComparison.Ordinal))
                    return IntentAssessmentResponse("sufficient", "The initial intent is complete.");
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    inventoryCalls++;
                    return CoverageInventoryResponse(relaxed: inventoryCalls > 1);
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal)
                    || request.Prompt.Contains("repairing one provider-neutral capability matching contract", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return MatchResponse((
                        "publish_summary",
                        "mcp",
                        CatalogIdForMethod(request.Prompt, "add_record")));
                }
                if (request.Prompt.Contains("provider-neutral capability coverage reviewer", StringComparison.Ordinal))
                {
                    coverageCalls++;
                    coveragePrompt ??= request.Prompt;
                    return CapabilityCoverageResponse(
                        request.Prompt,
                        supported: inventoryCalls > 1);
                }
                if (request.Prompt.Contains("provider-neutral capability coverage gap adjudicator", StringComparison.Ordinal))
                {
                    adjudicationCalls++;
                    return CapabilityCoverageGapAdjudicationResponse(
                        request.Prompt,
                        "publish_summary",
                        "intrinsic_primitive_missing",
                        [],
                        "Create one new summary record.");
                }
                return new LLMResponse
                {
                    Text = """
                        version: 1
                        name: generated-summary
                        skill:
                          description: Create one summary record.
                          inputs: {}
                          outputs: {}
                        workflows:
                          main:
                            steps:
                              - id: publish
                                type: mcp.call
                                input:
                                  server: neutral-records
                                  kind: tool
                                  method: add_record
                        """
                };
            });

        var result = await ExecuteAsync(CoverageRelaxationPlan(), llm.Object, CreateCoverageFactory(), human);

        Assert.True(
            result.Success == expectSuccess,
            $"Unexpected result: {result.Error?.Code}: {result.Error?.Message} {result.Error?.Details}");
        Assert.NotNull(coveragePrompt);
        Assert.Contains("evaluate only its intrinsic primitive", coveragePrompt, StringComparison.Ordinal);
        Assert.Contains("locally derivable parameter mapping", coveragePrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHub", coveragePrompt, StringComparison.OrdinalIgnoreCase);
        var request = Assert.Single(human.Requests);
        var field = Assert.Single(request.Fields!);
        Assert.Equal("Preserve the original requirement and stop", field.Options![0]);
        Assert.True(field.OptionDefinitions![0].Recommended);
        Assert.False(field.OptionDefinitions[1].Recommended);
        if (expectSuccess)
        {
            Assert.Equal(2, inventoryCalls);
            Assert.Equal(3, matchingCalls);
            Assert.Equal(3, coverageCalls);
            Assert.Equal(2, adjudicationCalls);
            Assert.Contains("method: add_record", result.Outputs!["plan"]!["yaml"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
            Assert.Equal("incomplete_effect_coverage", result.Error.Details!["reason"]!.GetValue<string>());
            Assert.Equal(1, inventoryCalls);
            Assert.Equal(2, matchingCalls);
            Assert.Equal(2, coverageCalls);
            Assert.Equal(2, adjudicationCalls);
        }
    }

    [Fact]
    public async Task CapabilityCoverageReview_RepairsInvalidEvidenceWithPreciseDiagnostics()
    {
        var coverageCalls = 0;
        string? repairPrompt = null;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("provider-neutral workflow intent clarification analyst", StringComparison.Ordinal))
                    return IntentAssessmentResponse("sufficient", "The requested behavior is explicit.");
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return CoverageInventoryResponse(relaxed: false);
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return MatchResponse((
                        "publish_summary",
                        "mcp",
                        CatalogIdForMethod(request.Prompt, "add_record")));
                }
                if (request.Prompt.Contains("provider-neutral capability coverage reviewer", StringComparison.Ordinal))
                {
                    coverageCalls++;
                    var response = CapabilityCoverageResponse(
                        request.Prompt,
                        supported: true,
                        catalogExcerpt: "Create or update one unique summary record.");
                    if (coverageCalls == 1)
                    {
                        response.Json!["diagnostics"]![0]!["evidence"]![0]!["catalog_excerpt"]
                            = "create or update one unique summary record.";
                    }
                    else
                    {
                        repairPrompt = request.Prompt;
                    }
                    return response;
                }
                return new LLMResponse
                {
                    Text = """
                        version: 1
                        name: generated-summary
                        skill:
                          description: Create or update one unique summary record.
                          inputs: {}
                          outputs: {}
                        workflows:
                          main:
                            steps:
                              - id: publish
                                type: mcp.call
                                input:
                                  server: neutral-records
                                  kind: tool
                                  method: add_record
                        """
                };
            });

        var result = await ExecuteAsync(
            CoverageRelaxationPlan(),
            llm.Object,
            CreateExactCoverageFactory());

        Assert.True(result.Success, $"{result.Error?.Code}: {result.Error?.Message} {result.Error?.Details}");
        Assert.Equal(2, coverageCalls);
        Assert.NotNull(repairPrompt);
        Assert.Contains("evidence_excerpt_not_found", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("rejected_coverage_candidate", repairPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<capability_catalog>", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("method: add_record", result.Outputs!["plan"]!["yaml"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityInventoryEvidence_AcceptsDecodedUnicodeAndWhitespaceNormalizedClarification()
    {
        const string answer = "L’analyse d’une pull request.\r\nPublier un résumé.";
        var modelExcerpt = "L’analyse d’une pull request.   Publier un résumé."
            .Normalize(NormalizationForm.FormD);
        var inventoryCalls = 0;
        var matchingCalls = 0;
        var human = new RecordingHumanInputProvider(new JsonObject
        {
            ["review_scope"] = answer
        });
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("provider-neutral workflow intent clarification analyst", StringComparison.Ordinal))
                    return IntentQuestionsResponse("Préciser la portée de l’analyse.", "review_scope");
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    inventoryCalls++;
                    Assert.Contains("clarification_0001", request.Prompt, StringComparison.Ordinal);
                    return EvidenceInventoryResponse(
                        "op_analyze_pull_request",
                        "Analyser la modification demandée.",
                        "clarification_0001",
                        modelExcerpt);
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    matchingCalls++;
                    return MatchResponse(("op_analyze_pull_request", "unavailable", string.Empty));
                }
                throw new InvalidOperationException("Planning must stop at unavailable capability matching.");
            });

        var result = await ExecuteAsync(
            UnicodeClarificationPlan(),
            llm.Object,
            CreateNeutralFactory(),
            human);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal(1, inventoryCalls);
        Assert.Equal(2, matchingCalls);
        Assert.Single(human.Requests);
    }

    [Fact]
    public async Task CapabilityInventoryEvidence_RepairReceivesRejectedCandidateAndPreciseIssue()
    {
        var inventoryCalls = 0;
        string? repairPrompt = null;
        var human = new RecordingHumanInputProvider(new JsonObject());
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    inventoryCalls++;
                    return EvidenceInventoryResponse(
                        "load_object",
                        "Load the requested object.",
                        "user_request",
                        "load a configured object");
                }
                if (request.Prompt.Contains("inventory repair analyst", StringComparison.Ordinal))
                {
                    inventoryCalls++;
                    repairPrompt = request.Prompt;
                    return EvidenceInventoryResponse(
                        "load_object",
                        "Load the requested object.",
                        "user_request",
                        "Load a configured object");
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                    return MatchResponse(("load_object", "unavailable", string.Empty));
                throw new InvalidOperationException("Planning must stop at unavailable capability matching.");
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory(), human);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal(2, inventoryCalls);
        Assert.Empty(human.Requests);
        Assert.NotNull(repairPrompt);
        Assert.Contains("excerpt_not_found", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("load a configured object", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("user_request", repairPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityInventoryEvidence_RepeatedInvalidContractFailsWithoutClarifyingUser()
    {
        var matchingCalls = 0;
        var human = new RecordingHumanInputProvider(new JsonObject { ["unused"] = "unused" });
        var telemetry = new Mock<IWorkflowTelemetry>();
        var workflowSpan = new Mock<IWorkflowSpan>();
        var stepSpan = new Mock<IStepSpan>();
        var internalSpan = new Mock<ITelemetrySpan>();
        var contractEvents = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<ITelemetrySpan>(), It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.StepStart(It.IsAny<ITelemetrySpan>(), It.IsAny<StepTelemetryInfo>()))
            .Returns(stepSpan.Object);
        telemetry.Setup(value => value.SpanStart(It.IsAny<ITelemetrySpan>(), It.IsAny<TelemetrySpanInfo>()))
            .Returns(internalSpan.Object);
        internalSpan.Setup(value => value.AddEvent(
                "gnougo-flow.plan.capability_inventory.contract_issue",
                It.IsAny<IReadOnlyList<KeyValuePair<string, object?>>?>()))
            .Callback((string _, IReadOnlyList<KeyValuePair<string, object?>>? attributes) =>
                contractEvents.Add(attributes ?? []));
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("workflow runtime analyst", StringComparison.Ordinal)
                    || request.Prompt.Contains("inventory repair analyst", StringComparison.Ordinal))
                {
                    return EvidenceInventoryResponse(
                        "load_object",
                        "Load the requested object.",
                        "user_request",
                        "load a configured object");
                }
                if (request.Prompt.Contains("capability matcher", StringComparison.Ordinal))
                    matchingCalls++;
                throw new InvalidOperationException("No later planning stage may run.");
            });

        var result = await ExecuteAsync(
            InferredPlan(),
            llm.Object,
            CreateNeutralFactory(),
            human,
            telemetry.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal("model_contract_violation", result.Error.Details!["classification"]!.GetValue<string>());
        Assert.Equal("retry_or_change_planning_model", result.Error.Details["recommended_action"]!.GetValue<string>());
        var issue = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Error.Details["contract_issues"])));
        Assert.Equal("excerpt_not_found", issue["code"]!.GetValue<string>());
        Assert.Equal("load_object", issue["operation_id"]!.GetValue<string>());
        Assert.Equal("coverage_requirements", issue["field"]!.GetValue<string>());
        Assert.Equal("user_request", issue["source_id"]!.GetValue<string>());
        Assert.DoesNotContain("load a configured object", result.Error.Details.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty(human.Requests);
        Assert.Equal(0, matchingCalls);
        Assert.Equal(2, contractEvents.Count);
        Assert.All(contractEvents, attributes =>
        {
            var values = attributes.ToDictionary(static item => item.Key, static item => item.Value);
            Assert.Equal("excerpt_not_found", values["gnougo-flow.plan.capability_inventory.contract_issue.code"]);
            Assert.Equal("user_request", values["gnougo-flow.plan.capability_inventory.contract_issue.source_id"]);
            Assert.StartsWith(
                "evidence_",
                Assert.IsType<string>(values["gnougo-flow.plan.capability_inventory.contract_issue.evidence_id"]),
                StringComparison.Ordinal);
            Assert.DoesNotContain(values.Values, static value => string.Equals(
                value as string,
                "load a configured object",
                StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData("unknown_source", "Load a configured object", "source_unknown")]
    [InlineData("user_request", "load a configured object", "excerpt_not_found")]
    [InlineData("user_request", "Load a configured object!", "excerpt_not_found")]
    [InlineData("user_request", "Retrieve the configured object", "excerpt_not_found")]
    public async Task CapabilityInventoryEvidence_RejectsUnknownOrInexactEvidence(
        string sourceId,
        string excerpt,
        string expectedCode)
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EvidenceInventoryResponse(
                "load_object",
                "Load the requested object.",
                sourceId,
                excerpt));

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        var issue = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Error.Details!["contract_issues"])));
        Assert.Equal(expectedCode, issue["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task CapabilityInventoryEvidence_RejectsExcerptSpanningDifferentSources()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EvidenceInventoryResponse(
                "load_object",
                "Load the configured object.",
                "user_request",
                "Load a configured object"));

        var result = await ExecuteAsync(SplitEvidencePlan(), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        var issue = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Error.Details!["contract_issues"])));
        Assert.Equal("excerpt_not_found", issue["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Optionally notify a consumer", "without human confirmation", "optionality_evidence")]
    [InlineData("optionally notify a consumer", "Without human confirmation", "external_write_confirmation_evidence")]
    public async Task CapabilityInventoryEvidence_AppliesExactResolverToOptionalityAndConfirmation(
        string optionalityExcerpt,
        string confirmationExcerpt,
        string expectedField)
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EvidencePolicyInventoryResponse(optionalityExcerpt, confirmationExcerpt));

        var result = await ExecuteAsync(EvidencePolicyPlan(), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        var issue = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Error.Details!["contract_issues"])));
        Assert.Equal("excerpt_not_found", issue["code"]!.GetValue<string>());
        Assert.Equal(expectedField, issue["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_ClarificationFailsClosedWithoutProvider()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("load_object", "Load the requested object.", true));
                var candidate = CatalogIdForMethod(request.Prompt, "get_object");
                return MatchingResponse(("load_object", "ambiguous", Array.Empty<string>(), new[] { candidate }, "User intent remains ambiguous."));
            });

        var result = await ExecuteAsync(ClarifyingInferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal("clarification_provider_unavailable", result.Error.Details!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_ClarificationFailsClosedWhenAnyRequiredAnswerIsMissing()
    {
        var human = new RecordingHumanInputProvider(new JsonObject
        {
            ["unresolved_choice_1"] = "Use the documented read capability."
        });
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("load_object", "Load the requested object.", true));
                var candidate = CatalogIdForMethod(request.Prompt, "get_object");
                return MatchingResponse(("load_object", "ambiguous", Array.Empty<string>(), [candidate], "User intent remains ambiguous."));
            });

        var result = await ExecuteAsync(ClarifyingInferredPlan(), llm.Object, CreateNeutralFactory(), human);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal("clarification_invalid_response", result.Error.Details!["reason"]!.GetValue<string>());
        Assert.Equal("cannot_plan_safely", result.Error.Details["planning_outcome"]!.GetValue<string>());
        Assert.Equal("clarify_or_abandon", result.Error.Details["recommended_action"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_ClarificationTimeoutFailsClosed()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                    return InventoryResponse(("load_object", "Load the requested object.", true));
                var candidate = CatalogIdForMethod(request.Prompt, "get_object");
                return MatchingResponse(("load_object", "ambiguous", Array.Empty<string>(), new[] { candidate }, "User intent remains ambiguous."));
            });

        var result = await ExecuteAsync(
            ClarifyingInferredPlan(),
            llm.Object,
            CreateNeutralFactory(),
            new CancellingHumanInputProvider());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal("clarification_timeout", result.Error.Details!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_DoesNotClarifyMalformedMatchingContract()
    {
        var human = new RecordingHumanInputProvider(new JsonObject { ["clarification"] = "unused" });
        var telemetry = new Mock<IWorkflowTelemetry>();
        var workflowSpan = new Mock<IWorkflowSpan>();
        var stepSpan = new Mock<IStepSpan>();
        var internalSpan = new Mock<ITelemetrySpan>();
        var failureEvents = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<ITelemetrySpan>(), It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.StepStart(It.IsAny<ITelemetrySpan>(), It.IsAny<StepTelemetryInfo>()))
            .Returns(stepSpan.Object);
        telemetry.Setup(value => value.SpanStart(It.IsAny<ITelemetrySpan>(), It.IsAny<TelemetrySpanInfo>()))
            .Returns(internalSpan.Object);
        internalSpan.Setup(value => value.AddEvent(
                "gnougo-flow.plan.capability_matching.failure",
                It.IsAny<IReadOnlyList<KeyValuePair<string, object?>>?>()))
            .Callback((string _, IReadOnlyList<KeyValuePair<string, object?>>? attributes) =>
                failureEvents.Add(attributes ?? []));
        string? repairPrompt = null;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("repairing a previous matching contract", StringComparison.Ordinal))
                    repairPrompt = request.Prompt;
                return request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal)
                    ? InventoryResponse(("load_object", "Load the requested object.", true))
                    : MatchResponse(("load_object", "mcp", "cap_999999"));
            });

        var result = await ExecuteAsync(
            ClarifyingInferredPlan(),
            llm.Object,
            CreateNeutralFactory(),
            human,
            telemetry.Object);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal("model_repair_exhausted", result.Error.Details!["reason_code"]!.GetValue<string>());
        Assert.Equal("model_contract_violation", result.Error.Details!["classification"]!.GetValue<string>());
        Assert.Equal("retry_or_change_planning_model", result.Error.Details["recommended_action"]!.GetValue<string>());
        var matchingIssue = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Error.Details["matching_issues"])));
        Assert.Equal("model_repair_exhausted", matchingIssue["reason_code"]!.GetValue<string>());
        Assert.Equal("catalog_id_unknown", matchingIssue["validation_issue"]!.GetValue<string>());
        Assert.Equal("matched", matchingIssue["reported_status"]!.GetValue<string>());
        Assert.Equal(1, matchingIssue["selected_catalog_id_count"]!.GetValue<int>());
        Assert.NotNull(repairPrompt);
        Assert.Contains("\"validation_issue\":\"catalog_id_unknown\"", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("\"reported_status\":\"matched\"", repairPrompt, StringComparison.Ordinal);
        var exhaustionEvent = Assert.Single(failureEvents);
        var eventAttributes = exhaustionEvent.ToDictionary(static item => item.Key, static item => item.Value);
        Assert.Equal("model_repair_exhausted", eventAttributes["gnougo-flow.plan.capability_matching.reason_code"]);
        Assert.Equal(true, eventAttributes["gnougo-flow.plan.capability_matching.repair_attempted"]);
        Assert.DoesNotContain(eventAttributes.Keys, static key => key.Contains("description", StringComparison.Ordinal));
        Assert.DoesNotContain(eventAttributes.Keys, static key => key.Contains("reasoning", StringComparison.Ordinal));
        Assert.Empty(human.Requests);
    }

    [Fact]
    public void PipelineGraph_NormalizesBlockScalarSwitchDefaultStepList()
    {
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "TryParseGraphStepSequenceScalar",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var parsed = method.Invoke(null,
        [
            """
            - id: call_request_changes
              leaf: submit_request_changes
              args:
                decision: REQUEST_CHANGES
            """
        ]);

        Assert.NotNull(parsed);
        Assert.Equal("YamlSequenceNode", parsed.GetType().Name);
        Assert.Null(method.Invoke(null, ["do not mutate"]));
    }

    private static LLMResponse MatchResponse(params (string OperationId, string Resolution, string CatalogId)[] matches)
        => MatchResponseWithConstraints(matches, Array.Empty<(string ConstraintId, string[] CatalogIds)>());

    private static JsonObject Evidence(string sourceId, string excerpt) => new()
    {
        ["source_id"] = sourceId,
        ["excerpt"] = excerpt
    };

    private static LLMResponse PhysicalCandidateResponse(
        params (string OperationId, string[] CatalogIds)[] operations)
        => PhysicalCandidateResponseWithConstraints(
            operations,
            Array.Empty<(string ConstraintId, string[] CatalogIds)>());

    private static LLMResponse PhysicalCandidateResponseWithConstraints(
        IReadOnlyList<(string OperationId, string[] CatalogIds)> operations,
        IReadOnlyList<(string ConstraintId, string[] CatalogIds)> constraints)
        => new()
        {
            Json = new JsonObject
            {
                ["operation_candidates"] = new JsonArray(operations.Select(static operation => (JsonNode)new JsonObject
                {
                    ["operation_id"] = operation.OperationId,
                    ["catalog_ids"] = new JsonArray(operation.CatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
                }).ToArray()),
                ["constraint_candidates"] = new JsonArray(constraints.Select(static constraint => (JsonNode)new JsonObject
                {
                    ["constraint_id"] = constraint.ConstraintId,
                    ["catalog_ids"] = new JsonArray(constraint.CatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
                }).ToArray())
            }
        };

    private static LLMResponse McpPrefilterResponse(
        params (string Server, string[] Tools)[] servers)
        => new()
        {
            Json = new JsonObject
            {
                ["servers"] = new JsonArray(servers.Select(static server => (JsonNode)new JsonObject
                {
                    ["name"] = server.Server,
                    ["tools"] = new JsonArray(server.Tools.Select(static tool => (JsonNode?)JsonValue.Create(tool)).ToArray()),
                    ["prompts"] = new JsonArray()
                }).ToArray())
            }
        };

    private static LLMResponse InventoryResponse(params (string Id, string Description, bool Required)[] operations)
        => new()
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["incomplete_reasons"] = new JsonArray(),
                ["operations"] = new JsonArray(operations.Select(static operation => (JsonNode)new JsonObject
                {
                    ["id"] = operation.Id,
                    ["description"] = operation.Description,
                    ["required"] = operation.Required
                }).ToArray()),
                ["constraints"] = new JsonArray()
            }
        };

    private static LLMResponse InventoryResponseWithKinds(
        params (string Id, string Description, bool Required, string ExecutionKind)[] operations)
        => new()
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["incomplete_reasons"] = new JsonArray(),
                ["operations"] = new JsonArray(operations.Select(static operation => (JsonNode)new JsonObject
                {
                    ["id"] = operation.Id,
                    ["description"] = operation.Description,
                    ["required"] = operation.Required,
                    ["execution_kind"] = operation.ExecutionKind
                }).ToArray()),
                ["constraints"] = new JsonArray()
            }
        };

    private static LLMResponse InventoryResponseWithEffects(
        params (string Id, string Description, bool Required, string ExecutionKind, string ExternalEffectKind)[] operations)
        => new()
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["incomplete_reasons"] = new JsonArray(),
                ["operations"] = new JsonArray(operations.Select(static operation => (JsonNode)new JsonObject
                {
                    ["id"] = operation.Id,
                    ["description"] = operation.Description,
                    ["required"] = operation.Required,
                    ["execution_kind"] = operation.ExecutionKind,
                    ["external_effect_kind"] = operation.ExternalEffectKind
                }).ToArray()),
                ["constraints"] = new JsonArray()
            }
        };

    private static LLMResponse EvidenceInventoryResponse(
        string operationId,
        string description,
        string sourceId,
        string excerpt)
        => new()
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["external_write_confirmation_policy"] = "unspecified",
                ["external_write_confirmation_evidence"] = Evidence(string.Empty, string.Empty),
                ["incomplete_reasons"] = new JsonArray(),
                ["operations"] = new JsonArray(new JsonObject
                {
                    ["id"] = operationId,
                    ["description"] = description,
                    ["required"] = true,
                    ["execution_kind"] = "external_effect",
                    ["external_effect_kind"] = "execute",
                    ["input_operation_ids"] = new JsonArray(),
                    ["coverage_requirements"] = new JsonArray(Evidence(sourceId, excerpt)),
                    ["optionality_evidence"] = Evidence(string.Empty, string.Empty),
                    ["decision_source_operation_id"] = string.Empty,
                    ["allow_no_effect_outcome"] = false,
                    ["intent_origin"] = "requested_effect",
                    ["derivation_source_operation_id"] = string.Empty
                }),
                ["constraints"] = new JsonArray()
            }
        };

    private static LLMResponse EvidencePolicyInventoryResponse(
        string optionalityExcerpt,
        string confirmationExcerpt)
        => new()
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["external_write_confirmation_policy"] = "forbidden",
                ["external_write_confirmation_evidence"] = Evidence(
                    "user_request",
                    confirmationExcerpt),
                ["incomplete_reasons"] = new JsonArray(),
                ["operations"] = new JsonArray(new JsonObject
                {
                    ["id"] = "load_object",
                    ["description"] = "Optionally load the configured object.",
                    ["required"] = false,
                    ["execution_kind"] = "external_effect",
                    ["external_effect_kind"] = "execute",
                    ["input_operation_ids"] = new JsonArray(),
                    ["coverage_requirements"] = new JsonArray(Evidence(
                        "user_request",
                        "Load a configured object")),
                    ["optionality_evidence"] = Evidence(
                        "user_request",
                        optionalityExcerpt),
                    ["decision_source_operation_id"] = string.Empty,
                    ["allow_no_effect_outcome"] = false,
                    ["intent_origin"] = "requested_effect",
                    ["derivation_source_operation_id"] = string.Empty
                }),
                ["constraints"] = new JsonArray()
            }
        };

    private static LLMResponse MatchingResponse(
        params (string OperationId, string Status, string[] CatalogIds, string[] CandidateCatalogIds, string Reason)[] matches)
        => new()
        {
            Json = new JsonObject
            {
                ["operation_matches"] = new JsonArray(matches.Select(static match => (JsonNode)new JsonObject
                {
                    ["operation_id"] = match.OperationId,
                    ["status"] = match.Status,
                    ["catalog_ids"] = new JsonArray(match.CatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                    ["candidate_catalog_ids"] = new JsonArray(match.CandidateCatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                    ["decision_operation_id"] = string.Empty,
                    ["reason"] = match.Reason
                }).ToArray()),
                ["constraint_matches"] = new JsonArray()
            }
        };

    private static JsonObject MatchingNode(
        string operationId,
        string status,
        string[] catalogIds,
        string reason)
        => new()
        {
            ["operation_id"] = operationId,
            ["status"] = status,
            ["catalog_ids"] = new JsonArray(catalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["candidate_catalog_ids"] = new JsonArray(),
            ["decision_operation_id"] = string.Empty,
            ["reason"] = reason
        };

    private static LLMResponse MatchResponseWithConstraints(
        IReadOnlyList<(string OperationId, string Resolution, string CatalogId)> matches,
        IReadOnlyList<(string ConstraintId, string[] CatalogIds)> denials)
        => new()
        {
            Json = new JsonObject
            {
                ["operation_matches"] = new JsonArray(matches.Select(static match => (JsonNode)new JsonObject
                {
                    ["operation_id"] = match.OperationId,
                    ["status"] = match.Resolution == "unavailable" ? "unavailable" : "matched",
                    ["catalog_ids"] = string.IsNullOrWhiteSpace(match.CatalogId)
                        ? new JsonArray()
                        : new JsonArray(match.CatalogId),
                    ["candidate_catalog_ids"] = new JsonArray(),
                    ["decision_operation_id"] = string.Empty,
                    ["reason"] = match.Resolution == "unavailable"
                        ? "No catalog capability implements this operation."
                        : "The selected catalog capability is sufficient."
                }).ToArray()),
                ["constraint_matches"] = new JsonArray(denials.Select(static denial => (JsonNode)new JsonObject
                {
                    ["constraint_id"] = denial.ConstraintId,
                    ["status"] = denial.CatalogIds.Length == 0 ? "policy_only" : "enforced",
                    ["denied_catalog_ids"] = new JsonArray(denial.CatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                    ["candidate_catalog_ids"] = new JsonArray(),
                    ["reason"] = denial.CatalogIds.Length == 0
                        ? "The constraint remains a workflow policy."
                        : "The listed capabilities are exact denied effects."
                }).ToArray())
            }
        };

    private static LLMResponse CoverageInventoryResponse(bool relaxed)
    {
        var requirement = relaxed
            ? "Create one new summary record."
            : "Create or update one unique summary record";
        return new LLMResponse
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["external_write_confirmation_policy"] = "forbidden",
                ["external_write_confirmation_evidence"] = Evidence(
                    "user_request",
                    "without human confirmation"),
                ["incomplete_reasons"] = new JsonArray(),
                ["operations"] = new JsonArray(new JsonObject
                {
                    ["id"] = "publish_summary",
                    ["description"] = relaxed
                        ? "Create one new summary record."
                        : "Create or update one unique summary record.",
                    ["required"] = true,
                    ["execution_kind"] = "external_effect",
                    ["external_effect_kind"] = "write",
                    ["input_operation_ids"] = new JsonArray(),
                    ["coverage_requirements"] = new JsonArray(Evidence(
                        relaxed ? "clarification_0001" : "user_request",
                        requirement)),
                    ["optionality_evidence"] = Evidence(string.Empty, string.Empty),
                    ["decision_source_operation_id"] = string.Empty,
                    ["allow_no_effect_outcome"] = false,
                    ["intent_origin"] = "requested_effect",
                    ["derivation_source_operation_id"] = string.Empty
                }),
                ["constraints"] = new JsonArray()
            }
        };
    }

    private static LLMResponse CapabilityCoverageResponse(
        string prompt,
        bool supported,
        string catalogExcerpt = "Create one new summary record.")
        => CapabilityCoverageResponseForOperation(
            prompt,
            "publish_summary",
            supported,
            catalogExcerpt);

    private static LLMResponse CapabilityCoverageResponseForOperation(
        string prompt,
        string operationId,
        bool supported,
        string catalogExcerpt)
    {
        var catalogId = Regex.Match(
            prompt,
            "\\\"selected_catalog_ids\\\":\\[\\\"(?<id>cap_[0-9]+)\\\"",
            RegexOptions.CultureInvariant).Groups["id"].Value;
        Assert.False(string.IsNullOrWhiteSpace(catalogId));
        var requirementId = Regex.Match(
            prompt,
            "\\\"requirement_id\\\":\\\"(?<id>evidence_[a-f0-9]+)\\\"",
            RegexOptions.CultureInvariant).Groups["id"].Value;
        Assert.False(string.IsNullOrWhiteSpace(requirementId));
        return new LLMResponse
        {
            Json = new JsonObject
            {
                ["diagnostics"] = new JsonArray(new JsonObject
                {
                    ["operation_id"] = operationId,
                    ["status"] = supported ? "supported" : "incomplete",
                    ["unsupported_requirement_id"] = supported ? string.Empty : requirementId,
                    ["supported_weaker_behavior"] = supported
                        ? string.Empty
                        : catalogExcerpt,
                    ["candidate_catalog_ids"] = new JsonArray(catalogId),
                    ["evidence"] = new JsonArray(new JsonObject
                    {
                        ["catalog_id"] = catalogId,
                        ["requirement_id"] = requirementId,
                        ["catalog_excerpt"] = catalogExcerpt
                    })
                })
            }
        };
    }

    private static LLMResponse CapabilityCoverageGapAdjudicationResponse(
        string prompt,
        string operationId,
        string classification,
        string[] structuralFacets,
        string catalogExcerpt)
    {
        var catalogId = Regex.Match(
            prompt,
            "\\\"selected_catalog_ids\\\":\\[\\\"(?<id>cap_[0-9]+)\\\"",
            RegexOptions.CultureInvariant).Groups["id"].Value;
        Assert.False(string.IsNullOrWhiteSpace(catalogId));
        var requirementId = Regex.Match(
            prompt,
            "\\\"requirement_id\\\":\\\"(?<id>evidence_[a-f0-9]+)\\\"",
            RegexOptions.CultureInvariant).Groups["id"].Value;
        Assert.False(string.IsNullOrWhiteSpace(requirementId));
        return new LLMResponse
        {
            Json = new JsonObject
            {
                ["adjudications"] = new JsonArray(new JsonObject
                {
                    ["operation_id"] = operationId,
                    ["requirement_id"] = requirementId,
                    ["classification"] = classification,
                    ["structural_facets"] = new JsonArray(structuralFacets
                        .Select(static facet => (JsonNode?)JsonValue.Create(facet)).ToArray()),
                    ["catalog_id"] = catalogId,
                    ["catalog_excerpt"] = catalogExcerpt
                })
            }
        };
    }

    private static LLMResponse ConditionalInventoryResponse(
        string decisionDescription,
        string conditionalDescription,
        bool allowNoEffectOutcome = false)
        => new()
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["incomplete_reasons"] = new JsonArray(),
                ["external_write_confirmation_policy"] = "forbidden",
                ["external_write_confirmation_evidence"] = Evidence(
                    "user_request",
                    "without human confirmation"),
                ["operations"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = "analyze",
                        ["description"] = decisionDescription,
                        ["required"] = true,
                        ["execution_kind"] = "external_effect",
                        ["external_effect_kind"] = "execute",
                        ["decision_source_operation_id"] = string.Empty
                    },
                    new JsonObject
                    {
                        ["id"] = "publish",
                        ["description"] = conditionalDescription,
                        ["required"] = true,
                        ["execution_kind"] = "external_effect",
                        ["external_effect_kind"] = "write",
                        ["decision_source_operation_id"] = "analyze",
                        ["allow_no_effect_outcome"] = allowNoEffectOutcome
                    }),
                ["constraints"] = new JsonArray()
            }
        };

    private static LLMResponse InventoryResponseWithConstraints(
        IReadOnlyList<(string Id, string Description, bool Required)> operations,
        IReadOnlyList<(string Id, string Description, bool Required, string EnforcementKind)> constraints)
        => new()
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["incomplete_reasons"] = new JsonArray(),
                ["operations"] = new JsonArray(operations.Select(static operation => (JsonNode)new JsonObject
                {
                    ["id"] = operation.Id,
                    ["description"] = operation.Description,
                    ["required"] = operation.Required
                }).ToArray()),
                ["constraints"] = new JsonArray(constraints.Select(static constraint => (JsonNode)new JsonObject
                {
                    ["id"] = constraint.Id,
                    ["description"] = constraint.Description,
                    ["required"] = constraint.Required,
                    ["enforcement_kind"] = constraint.EnforcementKind
                }).ToArray())
            }
        };

    private static JsonObject CompleteClarificationResponse() => new()
    {
        ["unresolved_choice_1"] = "Use the documented object-read capability.",
        ["intended_outcome_and_scope"] = "Return the requested object and do not add unrelated behavior.",
        ["runtime_decision_rules"] = "Any future conditional choice must use the preceding runtime result; do not predict it now.",
        ["external_effect_boundaries"] = "Allow the requested read and no writes.",
        ["success_criteria"] = "Succeed only when the requested object is returned exactly once.",
        ["failure_policy"] = "Stop safely when the required capability is unavailable."
    };

    private static JsonObject CompleteInventoryClarificationResponse() => new()
    {
        ["unresolved_intent_1"] = "Process only the requested object.",
        ["unresolved_intent_2"] = "Stop safely when the request cannot be supported.",
        ["intended_outcome_and_scope"] = "Return the requested object and exclude unrelated records.",
        ["runtime_decision_rules"] = "No outcome is fixed at design time; any later choice uses its declared source result.",
        ["external_effect_boundaries"] = "Allow the requested read and no writes.",
        ["success_criteria"] = "Return exactly one requested result.",
        ["failure_policy"] = "Stop and report unsupported planning; do not guess."
    };

    private static LLMResponse IntentAssessmentResponse(string outcome, string reason)
        => new()
        {
            Json = new JsonObject
            {
                ["outcome"] = outcome,
                ["reason"] = reason,
                ["questions"] = new JsonArray()
            }
        };

    private static LLMResponse IntentQuestionsResponse(string reason, string id)
        => new()
        {
            Json = new JsonObject
            {
                ["outcome"] = "questions",
                ["reason"] = reason,
                ["questions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = id,
                        ["prompt"] = "Which observable behavior is intended?",
                        ["options"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["value"] = "Focused behavior",
                                ["description"] = "Apply only the explicitly requested behavior.",
                                ["recommended"] = true
                            },
                            new JsonObject
                            {
                                ["value"] = "Expanded behavior",
                                ["description"] = "Include related behavior beyond the explicit request.",
                                ["recommended"] = false
                            }
                        }
                    }
                }
            }
        };

    private static LLMResponse ConditionalMatchingResponse(
        string analyzeCatalogId,
        string approveCatalogId,
        string requestChangesCatalogId)
        => ConditionalMatchingResponse([analyzeCatalogId], approveCatalogId, requestChangesCatalogId);

    private static LLMResponse ConditionalMatchingResponse(
        IReadOnlyList<string> analyzeCatalogIds,
        string approveCatalogId,
        string requestChangesCatalogId)
        => new()
        {
            Json = new JsonObject
            {
                ["operation_matches"] = new JsonArray(
                    new JsonObject
                    {
                        ["operation_id"] = "analyze",
                        ["status"] = analyzeCatalogIds.Count == 1 ? "matched" : "composed",
                        ["catalog_ids"] = new JsonArray(analyzeCatalogIds
                            .Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                        ["candidate_catalog_ids"] = new JsonArray(),
                        ["decision_operation_id"] = string.Empty,
                        ["reason"] = "The analysis capability is sufficient."
                    },
                    new JsonObject
                    {
                        ["operation_id"] = "publish",
                        ["status"] = "conditional",
                        ["catalog_ids"] = new JsonArray(approveCatalogId, requestChangesCatalogId),
                        ["candidate_catalog_ids"] = new JsonArray(),
                        ["decision_operation_id"] = "analyze",
                        ["reason"] = "The runtime analysis selects exactly one publication event."
                    }),
                ["constraint_matches"] = new JsonArray()
            }
        };

    private static LLMResponse RecoveredReviewDecisionInventoryResponse()
        => new()
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["incomplete_reasons"] = new JsonArray(),
                ["external_write_confirmation_policy"] = "forbidden",
                ["external_write_confirmation_evidence"] = Evidence(
                    "user_request",
                    "Review one materialized comparison and submit its runtime decision after one confirmation."),
                ["operations"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = "materialize_change",
                        ["description"] = "Materialize one source for declared downstream operations.",
                        ["required"] = true,
                        ["execution_kind"] = "external_effect",
                        ["external_effect_kind"] = "execute",
                        ["input_operation_ids"] = new JsonArray(),
                        ["decision_source_operation_id"] = string.Empty,
                        ["allow_no_effect_outcome"] = false,
                        ["intent_origin"] = "requested_effect",
                        ["derivation_source_operation_id"] = string.Empty
                    },
                    new JsonObject
                    {
                        ["id"] = "review_change",
                        ["description"] = "Produce one structured decision from the complete materialized comparison.",
                        ["required"] = true,
                        ["execution_kind"] = "external_effect",
                        ["external_effect_kind"] = "execute",
                        ["input_operation_ids"] = new JsonArray("materialize_change"),
                        ["decision_source_operation_id"] = string.Empty,
                        ["allow_no_effect_outcome"] = false,
                        ["intent_origin"] = "requested_effect",
                        ["derivation_source_operation_id"] = string.Empty
                    },
                    new JsonObject
                    {
                        ["id"] = "confirm_submission",
                        ["description"] = "Obtain authorization to perform the external write.",
                        ["required"] = true,
                        ["execution_kind"] = "human_interaction",
                        ["external_effect_kind"] = "none",
                        ["input_operation_ids"] = new JsonArray("review_change"),
                        ["decision_source_operation_id"] = string.Empty,
                        ["allow_no_effect_outcome"] = false,
                        ["intent_origin"] = "requested_effect",
                        ["derivation_source_operation_id"] = string.Empty
                    },
                    new JsonObject
                    {
                        ["id"] = "submit_review",
                        ["description"] = "Submit exactly one branch after authorization.",
                        ["required"] = true,
                        ["execution_kind"] = "external_effect",
                        ["external_effect_kind"] = "write",
                        ["input_operation_ids"] = new JsonArray("review_change", "confirm_submission"),
                        ["decision_source_operation_id"] = "confirm_submission",
                        ["allow_no_effect_outcome"] = false,
                        ["intent_origin"] = "requested_effect",
                        ["derivation_source_operation_id"] = string.Empty
                    }),
                ["constraints"] = new JsonArray()
            }
        };

    private static LLMResponse RecoveredReviewDecisionMatchingResponse(string prompt)
        => new()
        {
            Json = new JsonObject
            {
                ["operation_matches"] = new JsonArray(
                    new JsonObject
                    {
                        ["operation_id"] = "materialize_change",
                        ["status"] = "matched",
                        ["catalog_ids"] = new JsonArray(CatalogIdForMethod(prompt, "git_clone")),
                        ["candidate_catalog_ids"] = new JsonArray(),
                        ["decision_operation_id"] = string.Empty,
                        ["reason"] = "The selected capability materializes the declared source."
                    },
                    new JsonObject
                    {
                        ["operation_id"] = "review_change",
                        ["status"] = "matched",
                        ["catalog_ids"] = new JsonArray(CatalogIdForMethod(prompt, "copilot_review")),
                        ["candidate_catalog_ids"] = new JsonArray(),
                        ["decision_operation_id"] = string.Empty,
                        ["reason"] = "The selected capability performs the requested analysis."
                    },
                    new JsonObject
                    {
                        ["operation_id"] = "confirm_submission",
                        ["status"] = "matched",
                        ["catalog_ids"] = new JsonArray(CatalogIdForMethod(prompt, "human.input")),
                        ["candidate_catalog_ids"] = new JsonArray(),
                        ["decision_operation_id"] = string.Empty,
                        ["reason"] = "The selected native capability obtains authorization."
                    },
                    new JsonObject
                    {
                        ["operation_id"] = "submit_review",
                        ["status"] = "conditional",
                        ["catalog_ids"] = new JsonArray(
                            CatalogIdForBindings(prompt, ("/event", "APPROVE"), ("/method", "create")),
                            CatalogIdForBindings(prompt, ("/event", "REQUEST_CHANGES"), ("/method", "create")),
                            CatalogIdForBindings(prompt, ("/event", "COMMENT"), ("/method", "create"))),
                        ["candidate_catalog_ids"] = new JsonArray(),
                        ["decision_operation_id"] = "confirm_submission",
                        ["reason"] = "The locked source was copied into the conditional match."
                    }),
                ["constraint_matches"] = new JsonArray()
            }
        };

    private static LLMResponse DecisionRecoveryInventoryResponse(
        bool includeAlias,
        bool includeSecondSource,
        IReadOnlyList<string> declaredPublishInputs,
        bool allowNoEffectOutcome)
    {
        var operations = new JsonArray(
            DecisionRecoveryOperation("analyze", "external_effect", "execute", []));
        if (includeSecondSource)
        {
            operations.Add(DecisionRecoveryOperation(
                "analyze_alternative",
                "external_effect",
                "execute",
                []));
        }
        operations.Add(DecisionRecoveryOperation(
            "confirm_submission",
            "human_interaction",
            "none",
            ["analyze"]));
        if (includeAlias)
        {
            var aliasInputs = includeSecondSource
                ? new[] { "analyze", "analyze_alternative", "confirm_submission" }
                : new[] { "analyze", "confirm_submission" };
            operations.Add(new JsonObject
            {
                ["id"] = "decision_alias",
                ["description"] = "Project the declared upstream analysis after confirmation.",
                ["required"] = true,
                ["execution_kind"] = "local_processing",
                ["external_effect_kind"] = "none",
                ["input_operation_ids"] = new JsonArray(aliasInputs
                    .Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["decision_source_operation_id"] = "confirm_submission",
                ["allow_no_effect_outcome"] = false,
                ["intent_origin"] = "requested_effect",
                ["derivation_source_operation_id"] = string.Empty
            });
        }
        operations.Add(new JsonObject
        {
            ["id"] = "publish",
            ["description"] = "Publish exactly one selected effect branch.",
            ["required"] = true,
            ["execution_kind"] = "external_effect",
            ["external_effect_kind"] = "write",
            ["input_operation_ids"] = new JsonArray(declaredPublishInputs
                .Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["decision_source_operation_id"] = "confirm_submission",
            ["allow_no_effect_outcome"] = allowNoEffectOutcome,
            ["intent_origin"] = "requested_effect",
            ["derivation_source_operation_id"] = string.Empty
        });
        return new LLMResponse
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["incomplete_reasons"] = new JsonArray(),
                ["external_write_confirmation_policy"] = "forbidden",
                ["external_write_confirmation_evidence"] = Evidence(
                    "user_request",
                    "without human confirmation"),
                ["operations"] = operations,
                ["constraints"] = new JsonArray()
            }
        };
    }

    private static LLMResponse MultiSourceLocalDecisionInventoryResponse()
        => new()
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["incomplete_reasons"] = new JsonArray(),
                ["external_write_confirmation_policy"] = "forbidden",
                ["external_write_confirmation_evidence"] = Evidence(
                    "user_request",
                    "without human confirmation"),
                ["operations"] = new JsonArray(
                    DecisionRecoveryOperation("analyze_primary", "external_effect", "execute", []),
                    DecisionRecoveryOperation("analyze_secondary", "external_effect", "execute", []),
                    new JsonObject
                    {
                        ["id"] = "compute_decisions",
                        ["description"] = "Compute finite runtime decisions from both declared analysis results.",
                        ["required"] = true,
                        ["execution_kind"] = "local_processing",
                        ["external_effect_kind"] = "none",
                        ["input_operation_ids"] = new JsonArray("analyze_primary", "analyze_secondary"),
                        ["decision_source_operation_id"] = string.Empty,
                        ["allow_no_effect_outcome"] = false,
                        ["intent_origin"] = "requested_effect",
                        ["derivation_source_operation_id"] = string.Empty
                    },
                    new JsonObject
                    {
                        ["id"] = "publish",
                        ["description"] = "Publish one selected runtime effect or perform no external effect.",
                        ["required"] = true,
                        ["execution_kind"] = "external_effect",
                        ["external_effect_kind"] = "write",
                        ["input_operation_ids"] = new JsonArray("compute_decisions"),
                        ["decision_source_operation_id"] = "compute_decisions",
                        ["allow_no_effect_outcome"] = true,
                        ["intent_origin"] = "requested_effect",
                        ["derivation_source_operation_id"] = string.Empty
                    }),
                ["constraints"] = new JsonArray()
            }
        };

    private static LLMResponse MultiSourceLocalDecisionMatchingResponse(string prompt)
        => new()
        {
            Json = new JsonObject
            {
                ["operation_matches"] = new JsonArray(
                    MatchedOperation("analyze_primary", CatalogIdForMethod(prompt, "analyze_primary")),
                    MatchedOperation("analyze_secondary", CatalogIdForMethod(prompt, "analyze_secondary")),
                    new JsonObject
                    {
                        ["operation_id"] = "compute_decisions",
                        ["status"] = "local",
                        ["catalog_ids"] = new JsonArray(),
                        ["candidate_catalog_ids"] = new JsonArray(),
                        ["decision_operation_id"] = string.Empty,
                        ["conditional_mode"] = string.Empty,
                        ["reason"] = "This is declared local processing."
                    },
                    new JsonObject
                    {
                        ["operation_id"] = "publish",
                        ["status"] = "conditional",
                        ["catalog_ids"] = new JsonArray(
                            CatalogIdForBindings(prompt, ("/event", "APPROVE"), ("/method", "create")),
                            CatalogIdForBindings(prompt, ("/event", "REQUEST_CHANGES"), ("/method", "create"))),
                        ["candidate_catalog_ids"] = new JsonArray(),
                        ["decision_operation_id"] = "compute_decisions",
                        ["conditional_mode"] = "exactly_one",
                        ["reason"] = "The local decision selects one effect or the declared no-effect result."
                    }),
                ["constraint_matches"] = new JsonArray()
            }
        };

    private static LLMResponse MultiFieldLocalDecisionInventoryResponse()
    {
        var response = MultiSourceLocalDecisionInventoryResponse();
        var operations = Assert.IsType<JsonArray>(response.Json!["operations"]);
        operations.Add(new JsonObject
        {
            ["id"] = "notify",
            ["description"] = "Execute both ordered publication details or perform no external effect.",
            ["required"] = true,
            ["execution_kind"] = "external_effect",
            ["external_effect_kind"] = "write",
            ["input_operation_ids"] = new JsonArray("compute_decisions"),
            ["decision_source_operation_id"] = "compute_decisions",
            ["allow_no_effect_outcome"] = true,
            ["intent_origin"] = "requested_effect",
            ["derivation_source_operation_id"] = string.Empty
        });
        return response;
    }

    private static LLMResponse MultiFieldLocalDecisionMatchingResponse(string prompt)
    {
        var response = MultiSourceLocalDecisionMatchingResponse(prompt);
        var operations = Assert.IsType<JsonArray>(response.Json!["operation_matches"]);
        operations.Add(new JsonObject
        {
            ["operation_id"] = "notify",
            ["status"] = "conditional",
            ["catalog_ids"] = new JsonArray(
                CatalogIdForMethod(prompt, "record_summary"),
                CatalogIdForMethod(prompt, "record_evidence")),
            ["candidate_catalog_ids"] = new JsonArray(),
            ["decision_operation_id"] = "compute_decisions",
            ["conditional_mode"] = "all_on_value",
            ["reason"] = "Both ordered effects execute for the finite effect value."
        });
        return response;
    }

    private static JsonObject MatchedOperation(string operationId, string catalogId)
        => new()
        {
            ["operation_id"] = operationId,
            ["status"] = "matched",
            ["catalog_ids"] = new JsonArray(catalogId),
            ["candidate_catalog_ids"] = new JsonArray(),
            ["decision_operation_id"] = string.Empty,
            ["conditional_mode"] = string.Empty,
            ["reason"] = "The selected capability satisfies the operation."
        };

    private static string MultiSourceLocalDecisionWorkflow(string field) => """
        version: 1
        name: generated-local-decision
        skill:
          description: Compute and publish one finite runtime decision.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: analyze_primary
                type: mcp.call
                input:
                  server: reviewer
                  kind: tool
                  method: analyze_primary
              - id: analyze_secondary
                type: mcp.call
                input:
                  server: reviewer
                  kind: tool
                  method: analyze_secondary
              - id: compute_decisions
                type: decision.evaluate
                input:
                  decisions:
                    DECISION_FIELD:
                      allowed_values: [APPROVE, REQUEST_CHANGES, NO_EFFECT]
                      cases:
                        - when: ${data.steps.analyze_primary.response.should_approve}
                          value: APPROVE
                        - when: ${data.steps.analyze_secondary.response.should_request_changes}
                          value: REQUEST_CHANGES
                      default: NO_EFFECT
              - id: publish
                type: switch
                expr: ${data.steps.compute_decisions.DECISION_FIELD}
                cases:
                  - value: APPROVE
                    steps:
                      - id: publish_approve
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request:
                            method: create
                            event: APPROVE
                            body: Approved.
                  - value: REQUEST_CHANGES
                    steps:
                      - id: publish_changes
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request:
                            method: create
                            event: REQUEST_CHANGES
                            body: Changes requested.
                  - value: NO_EFFECT
                    steps:
                      - id: record_no_effect
                        type: set
                        input: { status: no_effect }
                default: []
        """.Replace("DECISION_FIELD", field, StringComparison.Ordinal);

    private static string MultiFieldLocalDecisionWorkflow(string publishField, string notifyField) => """
        version: 1
        name: generated-multi-field-local-decision
        skill:
          description: Compute two finite decisions and conditionally execute their effects.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: analyze_primary
                type: mcp.call
                input:
                  server: reviewer
                  kind: tool
                  method: analyze_primary
              - id: analyze_secondary
                type: mcp.call
                input:
                  server: reviewer
                  kind: tool
                  method: analyze_secondary
              - id: compute_decisions
                type: decision.evaluate
                input:
                  decisions:
                    PUBLISH_FIELD:
                      allowed_values: [APPROVE, REQUEST_CHANGES, NO_EFFECT]
                      cases:
                        - when: ${data.steps.analyze_primary.response.should_approve}
                          value: APPROVE
                        - when: ${data.steps.analyze_secondary.response.should_request_changes}
                          value: REQUEST_CHANGES
                      default: NO_EFFECT
                    NOTIFY_FIELD:
                      allowed_values: [EFFECT, NO_EFFECT]
                      cases:
                        - when: ${data.steps.analyze_primary.response.should_approve}
                          value: EFFECT
                      default: NO_EFFECT
              - id: publish
                type: switch
                expr: ${data.steps.compute_decisions.PUBLISH_FIELD}
                cases:
                  - value: APPROVE
                    steps:
                      - id: publish_approve
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request: { method: create, event: APPROVE, body: Approved. }
                  - value: REQUEST_CHANGES
                    steps:
                      - id: publish_changes
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request: { method: create, event: REQUEST_CHANGES, body: Changes requested. }
                  - value: NO_EFFECT
                    steps:
                      - id: publish_no_effect
                        type: set
                        input: { status: no_effect }
                default: []
              - id: notify
                type: switch
                expr: ${data.steps.compute_decisions.NOTIFY_FIELD}
                cases:
                  - value: EFFECT
                    steps:
                      - id: record_summary
                        type: mcp.call
                        input:
                          server: audit
                          kind: tool
                          method: record_summary
                      - id: record_evidence
                        type: mcp.call
                        input:
                          server: audit
                          kind: tool
                          method: record_evidence
                  - value: NO_EFFECT
                    steps:
                      - id: notify_no_effect
                        type: set
                        input: { status: no_effect }
                default: []
        """
        .Replace("PUBLISH_FIELD", publishField, StringComparison.Ordinal)
        .Replace("NOTIFY_FIELD", notifyField, StringComparison.Ordinal);

    private static string CrossWorkflowLocalDecisionWorkflow(string field) => """
        version: 1
        name: generated-cross-workflow-local-decision
        skill:
          description: Route two typed analysis results through one local decision and one conditional effect.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: analysis
                type: workflow.call
                input:
                  ref: { kind: local, name: analyze_inputs }
                  args: {}
              - id: decision
                type: workflow.call
                input:
                  ref: { kind: local, name: evaluate_decision }
                  args:
                    primary_signal: ${data.steps.analysis.outputs.primary_signal}
                    secondary_signal: ${data.steps.analysis.outputs.secondary_signal}
              - id: publication
                type: workflow.call
                input:
                  ref: { kind: local, name: publish_decision }
                  args:
                    DECISION_FIELD: ${data.steps.decision.outputs.DECISION_FIELD}
          analyze_inputs:
            steps:
              - id: analyze_primary
                type: mcp.call
                input:
                  server: reviewer
                  kind: tool
                  method: analyze_primary
              - id: analyze_secondary
                type: mcp.call
                input:
                  server: reviewer
                  kind: tool
                  method: analyze_secondary
            outputs:
              primary_signal:
                expr: ${data.steps.analyze_primary.response.should_approve}
                type: boolean
              secondary_signal:
                expr: ${data.steps.analyze_secondary.response.should_request_changes}
                type: boolean
          evaluate_decision:
            inputs:
              primary_signal: { type: boolean }
              secondary_signal: { type: boolean }
            steps:
              - id: compute_decisions
                type: decision.evaluate
                input:
                  decisions:
                    DECISION_FIELD:
                      allowed_values: [APPROVE, REQUEST_CHANGES, NO_EFFECT]
                      cases:
                        - when: ${data.inputs.primary_signal}
                          value: APPROVE
                        - when: ${data.inputs.secondary_signal}
                          value: REQUEST_CHANGES
                      default: NO_EFFECT
            outputs:
              DECISION_FIELD:
                expr: ${data.steps.compute_decisions.DECISION_FIELD}
                type: string
                enum: [APPROVE, REQUEST_CHANGES, NO_EFFECT]
          publish_decision:
            inputs:
              DECISION_FIELD: { type: string }
            steps:
              - id: publish
                type: switch
                expr: ${data.inputs.DECISION_FIELD}
                cases:
                  - value: APPROVE
                    steps:
                      - id: publish_approve
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request: { method: create, event: APPROVE, body: Approved. }
                  - value: REQUEST_CHANGES
                    steps:
                      - id: publish_changes
                        type: mcp.call
                        input:
                          server: github
                          kind: tool
                          method: publish_review
                          request: { method: create, event: REQUEST_CHANGES, body: Changes requested. }
                  - value: NO_EFFECT
                    steps:
                      - id: publish_no_effect
                        type: set
                        input: { status: no_effect }
                default: []
        """.Replace("DECISION_FIELD", field, StringComparison.Ordinal);

    private static InMemoryMcpClientFactory CreateMultiSourceLocalDecisionFactory()
    {
        var booleanOutput = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "should_approve": { "type": "boolean" },
                "should_request_changes": { "type": "boolean" }
              },
              "required": ["should_approve", "should_request_changes"],
              "additionalProperties": false
            }
            """);
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("reviewer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo { Name = "analyze_primary", Description = "Produce the first boolean signals.", OutputSchema = booleanOutput!.DeepClone() },
                new McpToolInfo { Name = "analyze_secondary", Description = "Produce the second boolean signals.", OutputSchema = booleanOutput.DeepClone() }
            ]
        });
        RegisterDecisionWriter(factory);
        return factory;
    }

    private static InMemoryMcpClientFactory CreateMultiFieldLocalDecisionFactory()
    {
        var factory = CreateMultiSourceLocalDecisionFactory();
        factory.RegisterServer("audit", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "record_summary",
                    Description = "Record the first ordered effect."
                },
                new McpToolInfo
                {
                    Name = "record_evidence",
                    Description = "Record the second ordered effect."
                }
            ]
        });
        return factory;
    }

    private static IWorkflowTelemetry CreateNormalizationTelemetry(
        List<IReadOnlyList<KeyValuePair<string, object?>>> normalizationEvents)
    {
        var telemetry = new Mock<IWorkflowTelemetry>();
        var workflowSpan = new Mock<IWorkflowSpan>();
        var stepSpan = new Mock<IStepSpan>();
        var internalSpan = new Mock<ITelemetrySpan>();
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<ITelemetrySpan>(), It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.StepStart(It.IsAny<ITelemetrySpan>(), It.IsAny<StepTelemetryInfo>()))
            .Returns(stepSpan.Object);
        telemetry.Setup(value => value.SpanStart(It.IsAny<ITelemetrySpan>(), It.IsAny<TelemetrySpanInfo>()))
            .Returns(internalSpan.Object);
        internalSpan.Setup(value => value.AddEvent(
                "gnougo-flow.plan.capability_matching.normalization",
                It.IsAny<IReadOnlyList<KeyValuePair<string, object?>>?>()))
            .Callback((string _, IReadOnlyList<KeyValuePair<string, object?>>? attributes) =>
                normalizationEvents.Add(attributes ?? []));
        return telemetry.Object;
    }

    private static IWorkflowTelemetry CreateStepAttributeTelemetry(
        out Dictionary<string, object?> attributes)
    {
        attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        var captured = attributes;
        var telemetry = new Mock<IWorkflowTelemetry>();
        var workflowSpan = new Mock<IWorkflowSpan>();
        var stepSpan = new Mock<IStepSpan>();
        var internalSpan = new Mock<ITelemetrySpan>();
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.WorkflowStart(It.IsAny<ITelemetrySpan>(), It.IsAny<WorkflowTelemetryInfo>()))
            .Returns(workflowSpan.Object);
        telemetry.Setup(value => value.StepStart(It.IsAny<ITelemetrySpan>(), It.IsAny<StepTelemetryInfo>()))
            .Returns(stepSpan.Object);
        telemetry.Setup(value => value.SpanStart(It.IsAny<ITelemetrySpan>(), It.IsAny<TelemetrySpanInfo>()))
            .Returns(internalSpan.Object);
        stepSpan.Setup(value => value.SetAttribute(It.IsAny<string>(), It.IsAny<object?>()))
            .Callback((string key, object? value) => captured[key] = value);
        return telemetry.Object;
    }

    private static LLMResponse ReadOnlyInventoryResponse()
        => new()
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["incomplete_reasons"] = new JsonArray(),
                ["external_write_confirmation_policy"] = "unspecified",
                ["operations"] = new JsonArray(
                    DecisionRecoveryOperation("analyze_primary", "external_effect", "read", [])),
                ["constraints"] = new JsonArray()
            }
        };

    private static LLMResponse ReadOnlyMatchingResponse(string prompt)
        => new()
        {
            Json = new JsonObject
            {
                ["operation_matches"] = new JsonArray(
                    MatchedOperation("analyze_primary", CatalogIdForMethod(prompt, "analyze_primary"))),
                ["constraint_matches"] = new JsonArray()
            }
        };

    private static string ReadOnlyWorkflow() => """
        version: 1
        name: generated-read-only-result
        skill:
          description: Return the requested read-only analysis result.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: analyze_primary
                type: mcp.call
                input:
                  server: reviewer
                  kind: tool
                  method: analyze_primary
        """;

    private static JsonObject DecisionRecoveryOperation(
        string id,
        string executionKind,
        string externalEffectKind,
        IReadOnlyList<string> inputs)
        => new()
        {
            ["id"] = id,
            ["description"] = $"Execute the declared {id} operation.",
            ["required"] = true,
            ["execution_kind"] = executionKind,
            ["external_effect_kind"] = externalEffectKind,
            ["input_operation_ids"] = new JsonArray(inputs
                .Select(static input => (JsonNode?)JsonValue.Create(input)).ToArray()),
            ["decision_source_operation_id"] = string.Empty,
            ["allow_no_effect_outcome"] = false,
            ["intent_origin"] = "requested_effect",
            ["derivation_source_operation_id"] = string.Empty
        };

    private static LLMResponse DecisionRecoveryMatchingResponse(
        string prompt,
        IReadOnlyList<string> sourceACatalogIds,
        bool includeAlias,
        bool includeSecondSource,
        IReadOnlyList<string> branchValues)
    {
        var matches = new JsonArray(
            new JsonObject
            {
                ["operation_id"] = "analyze",
                ["status"] = sourceACatalogIds.Count == 1 ? "matched" : "composed",
                ["catalog_ids"] = new JsonArray(sourceACatalogIds
                    .Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["candidate_catalog_ids"] = new JsonArray(),
                ["decision_operation_id"] = string.Empty,
                ["reason"] = "The selected analysis capability set is complete."
            });
        if (includeSecondSource)
        {
            matches.Add(new JsonObject
            {
                ["operation_id"] = "analyze_alternative",
                ["status"] = "matched",
                ["catalog_ids"] = new JsonArray(CatalogIdForMethod(prompt, "analyze_alternative")),
                ["candidate_catalog_ids"] = new JsonArray(),
                ["decision_operation_id"] = string.Empty,
                ["reason"] = "The second declared analysis source is selected."
            });
        }
        if (includeAlias)
        {
            matches.Add(new JsonObject
            {
                ["operation_id"] = "decision_alias",
                ["status"] = "local",
                ["catalog_ids"] = new JsonArray(),
                ["candidate_catalog_ids"] = new JsonArray(),
                ["decision_operation_id"] = string.Empty,
                ["reason"] = "The alias is declared local processing."
            });
        }
        matches.Add(new JsonObject
        {
            ["operation_id"] = "confirm_submission",
            ["status"] = "matched",
            ["catalog_ids"] = new JsonArray(CatalogIdForMethod(prompt, "human.input")),
            ["candidate_catalog_ids"] = new JsonArray(),
            ["decision_operation_id"] = string.Empty,
            ["reason"] = "The native interaction capability obtains confirmation."
        });
        matches.Add(new JsonObject
        {
            ["operation_id"] = "publish",
            ["status"] = "conditional",
            ["catalog_ids"] = new JsonArray(branchValues.Select(value => (JsonNode?)JsonValue.Create(
                CatalogIdForBindings(prompt, ("/event", value), ("/method", "create")))).ToArray()),
            ["candidate_catalog_ids"] = new JsonArray(),
            ["decision_operation_id"] = "confirm_submission",
            ["reason"] = "The locked confirmation source was copied into the conditional response."
        });
        return new LLMResponse
        {
            Json = new JsonObject
            {
                ["operation_matches"] = matches,
                ["constraint_matches"] = new JsonArray()
            }
        };
    }

    private static LLMResponse ConditionalInventoryWithLocalDecisionResponse()
        => new()
        {
            Json = new JsonObject
            {
                ["external_write_confirmation_policy"] = "forbidden",
                ["external_write_confirmation_evidence"] = Evidence(
                    "user_request",
                    "without human confirmation"),
                ["operations"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = "analyze",
                        ["description"] = "Analyze the change and produce evidence.",
                        ["required"] = true,
                        ["execution_kind"] = "external_effect",
                        ["external_effect_kind"] = "execute",
                        ["input_operation_ids"] = new JsonArray(),
                        ["decision_source_operation_id"] = string.Empty,
                        ["allow_no_effect_outcome"] = false,
                        ["intent_origin"] = "requested_effect",
                        ["derivation_source_operation_id"] = string.Empty
                    },
                    new JsonObject
                    {
                        ["id"] = "validate_decision",
                        ["description"] = "Validate the analysis result into the closed decision set.",
                        ["required"] = true,
                        ["execution_kind"] = "local_processing",
                        ["external_effect_kind"] = "none",
                        ["input_operation_ids"] = new JsonArray("analyze"),
                        ["decision_source_operation_id"] = string.Empty,
                        ["allow_no_effect_outcome"] = false,
                        ["intent_origin"] = "requested_effect",
                        ["derivation_source_operation_id"] = string.Empty
                    },
                    new JsonObject
                    {
                        ["id"] = "publish",
                        ["description"] = "Publish one certain review decision and abstain otherwise.",
                        ["required"] = true,
                        ["execution_kind"] = "external_effect",
                        ["external_effect_kind"] = "write",
                        ["input_operation_ids"] = new JsonArray("validate_decision"),
                        ["decision_source_operation_id"] = "validate_decision",
                        ["allow_no_effect_outcome"] = true,
                        ["intent_origin"] = "requested_effect",
                        ["derivation_source_operation_id"] = string.Empty
                    }),
                ["constraints"] = new JsonArray(),
                ["complete"] = true,
                ["missing_intentions"] = new JsonArray(),
                ["reason"] = "The analysis, local validation, publication alternatives, and abstention outcome are explicit."
            }
        };

    private static LLMResponse ConditionalMatchingWithLocalDecisionResponse(
        string analyzeCatalogId,
        string approveCatalogId,
        string requestChangesCatalogId)
        => new()
        {
            Json = new JsonObject
            {
                ["operation_matches"] = new JsonArray(
                    new JsonObject
                    {
                        ["operation_id"] = "analyze",
                        ["status"] = "matched",
                        ["catalog_ids"] = new JsonArray(analyzeCatalogId),
                        ["candidate_catalog_ids"] = new JsonArray(),
                        ["decision_operation_id"] = string.Empty,
                        ["reason"] = "The analysis capability is sufficient."
                    },
                    new JsonObject
                    {
                        ["operation_id"] = "validate_decision",
                        ["status"] = "local",
                        ["catalog_ids"] = new JsonArray(),
                        ["candidate_catalog_ids"] = new JsonArray(),
                        ["decision_operation_id"] = string.Empty,
                        ["reason"] = "The closed-set validation is local processing."
                    },
                    new JsonObject
                    {
                        ["operation_id"] = "publish",
                        ["status"] = "conditional",
                        ["catalog_ids"] = new JsonArray(approveCatalogId, requestChangesCatalogId),
                        ["candidate_catalog_ids"] = new JsonArray(),
                        ["decision_operation_id"] = "validate_decision",
                        ["reason"] = "The validated runtime result selects exactly one effect or abstains."
                    }),
                ["constraint_matches"] = new JsonArray()
            }
        };

    private static string CatalogIdForMethod(string prompt, string method)
    {
        var marker = $" method={method}";
        var line = prompt.Split('\n').First(value => value.Contains(marker, StringComparison.Ordinal));
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string CatalogIdForWholeTool(string prompt, string method)
    {
        var marker = $" method={method}";
        var line = prompt.Split('\n').First(value => value.Contains(marker, StringComparison.Ordinal)
                                                    && !value.Contains(" variant_of=", StringComparison.Ordinal));
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string CatalogIdForBinding(string prompt, string path, string value)
    {
        var marker = $"request_bindings=[{path}=\"{value}\"]";
        var line = prompt.Split('\n').First(candidate => candidate.Contains(marker, StringComparison.Ordinal));
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string CatalogIdForBindings(
        string prompt,
        params (string Path, string Value)[] bindings)
    {
        var line = prompt.Split('\n').First(candidate => bindings.All(binding =>
            candidate.Contains($"{binding.Path}=\"{binding.Value}\"", StringComparison.Ordinal)));
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static Mock<ILLMClient> CreateConditionalLlm(
        string generatedWorkflow,
        bool allowNoEffectOutcome = false)
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal))
                {
                    return ConditionalInventoryResponse(
                        "Analyze the change and determine a decision.",
                        "Publish whichever review decision was determined at runtime.",
                        allowNoEffectOutcome);
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    return ConditionalMatchingResponse(
                        CatalogIdForMethod(request.Prompt, "analyze_change"),
                        CatalogIdForBindings(request.Prompt, ("/event", "APPROVE"), ("/method", "create")),
                        CatalogIdForBindings(request.Prompt, ("/event", "REQUEST_CHANGES"), ("/method", "create")));
                }
                return new LLMResponse { Text = generatedWorkflow };
            });
        return llm;
    }

    private static Mock<ILLMClient> ConstantLlm(string yaml)
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Text = yaml });
        return llm;
    }

    private static InMemoryMcpClientFactory CreateNeutralFactory()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("object-storage", new MockMcpServerConfig
        {
            Description = "Stores and retrieves objects.",
            Tools =
            [
                new McpToolInfo
                {
                    Name = "get_object",
                    Description = "Load an object by key.",
                    InputSchema = JsonNode.Parse("""
                        {
                          "type": "object",
                          "properties": { "key": { "type": "string" } },
                          "required": ["key"],
                          "additionalProperties": false
                        }
                        """),
                    OutputSchema = JsonNode.Parse("""
                        {
                          "type": "object",
                          "properties": {
                            "record": {
                              "type": "object",
                              "properties": {
                                "content": { "type": "string" },
                                "version": { "type": "number" }
                              }
                            }
                          }
                        }
                        """)
                },
                new McpToolInfo
                {
                    Name = "delete_object",
                    Description = "Permanently delete an object by key."
                }
            ]
        });
        factory.RegisterServer("messaging", new MockMcpServerConfig
        {
            Description = "Sends messages.",
            Tools = [new McpToolInfo { Name = "publish_event", Description = "Publish an event." }]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateMultiActionFactory()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("inventory", new MockMcpServerConfig
        {
            Description = "Reads inventory information.",
            Tools =
            [
                new McpToolInfo
                {
                    Name = "inventory_read",
                    Description = "Perform one documented inventory read operation.",
                    InputSchema = JsonNode.Parse("""
                        {
                          "type": "object",
                          "properties": {
                            "method": {
                              "type": "string",
                              "enum": ["list_items", "get_status"]
                            }
                          },
                          "required": ["method"],
                          "additionalProperties": false
                        }
                        """)
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateCleanupSelectorFactory()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("command-runner", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "run_allowed_action",
                    Description = "Run one configured action by its exact selector.",
                    InputSchema = JsonNode.Parse("""
                        {
                          "type": "object",
                          "properties": {
                            "action": {
                              "type": "string",
                              "enum": ["archive_directory", "delete_directory"]
                            }
                          },
                          "required": ["action"],
                          "oneOf": [
                            {
                              "description": "Archive one existing directory without deleting it.",
                              "properties": { "action": { "const": "archive_directory" } },
                              "required": ["action"]
                            },
                            {
                              "description": "Delete one existing directory recursively.",
                              "properties": { "action": { "const": "delete_directory" } },
                              "required": ["action"]
                            }
                          ],
                          "additionalProperties": false
                        }
                        """)
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateCoverageFactory()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("neutral-records", new MockMcpServerConfig
        {
            Description = "Stores summary records.",
            Tools =
            [
                new McpToolInfo
                {
                    Name = "add_record",
                    Description = "Create one new summary record."
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateExactCoverageFactory()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("neutral-records", new MockMcpServerConfig
        {
            Description = "Stores summary records.",
            Tools =
            [
                new McpToolInfo
                {
                    Name = "add_record",
                    Description = "Create or update one unique summary record."
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreatePullRequestReviewFactory()
    {
        var commonRequestProperties = """
            "method": { "type": "string" },
            "owner": { "type": "string" },
            "repository": { "type": "string" },
            "pull_number": { "type": "integer", "minimum": 1 }
            """;
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("review-data", new MockMcpServerConfig
        {
            Description = "Reads review data and publishes confirmed review comments.",
            Tools =
            [
                new McpToolInfo
                {
                    Name = "pull_request_read",
                    Description = "Read either metadata or diff content for one review target.",
                    InputSchema = JsonNode.Parse($$"""
                        {
                          "type": "object",
                          "properties": {
                            {{commonRequestProperties.Replace("\"method\": { \"type\": \"string\" }", "\"method\": { \"type\": \"string\", \"enum\": [\"get\", \"get_diff\"] }")}}
                          },
                          "required": ["method", "owner", "repository", "pull_number"],
                          "additionalProperties": false
                        }
                        """),
                    OutputSchema = JsonNode.Parse("""
                        {
                          "type": "object",
                          "properties": {
                            "state": { "type": "string" },
                            "base_ref": { "type": "string" },
                            "head_ref": { "type": "string" },
                            "diff": { "type": "string" }
                          },
                          "required": ["state", "base_ref", "head_ref", "diff"],
                          "additionalProperties": false
                        }
                        """)
                },
                new McpToolInfo
                {
                    Name = "pull_request_write",
                    Description = "Publish one confirmed review comment.",
                    InputSchema = JsonNode.Parse($$"""
                        {
                          "type": "object",
                          "properties": {
                            {{commonRequestProperties.Replace("\"method\": { \"type\": \"string\" }", "\"method\": { \"type\": \"string\", \"const\": \"comment\" }")}},
                            "body": { "type": "string", "minLength": 1 }
                          },
                          "required": ["method", "owner", "repository", "pull_number", "body"],
                          "additionalProperties": false
                        }
                        """)
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateCompositionFactory()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("reviewer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo { Name = "review_start", Description = "Start a multi-phase review." },
                new McpToolInfo { Name = "review_analyze", Description = "Analyze one review phase." },
                new McpToolInfo { Name = "review_finish", Description = "Finish a multi-phase review." },
                new McpToolInfo { Name = "read_diff", Description = "Read a change diff without reviewing it." },
                new McpToolInfo
                {
                    Name = "review_complete",
                    Description = "Run and finish every review phase as one complete operation.",
                    CompositionContract = new McpCapabilityCompositionResolution(
                        new McpCapabilityComposition(
                            1,
                            McpCapabilityCompositionConventions.CompleteOperationKind,
                            [
                                new McpEncapsulatedCapability("tool", "review_start"),
                                new McpEncapsulatedCapability("tool", "review_analyze"),
                                new McpEncapsulatedCapability("tool", "review_finish")
                            ]),
                        [])
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateConditionalReviewFactory(
        IReadOnlyList<string>? decisionValues = null,
        bool exposeDecisionOutput = true,
        IReadOnlyList<string>? publicationValues = null)
    {
        decisionValues ??= ["APPROVE", "REQUEST_CHANGES"];
        publicationValues ??= ["APPROVE", "REQUEST_CHANGES"];
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("reviewer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "analyze_change",
                    Description = "Analyze a change and return the final review decision and justification.",
                    OutputSchema = exposeDecisionOutput ? JsonNode.Parse($$"""
                        {
                          "type": "object",
                          "properties": {
                            "decision": { "type": "string", "enum": [{{string.Join(", ", decisionValues.Select(static value => $"\"{value}\""))}}] },
                            "justification": { "type": "string" }
                          },
                          "required": ["decision", "justification"],
                          "additionalProperties": false
                        }
                        """) : null
                }
            ]
        });
        factory.RegisterServer("github", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "publish_review",
                    Description = "Create a pull request review with one exact event and explanatory body.",
                    InputSchema = JsonNode.Parse($$"""
                        {
                          "type": "object",
                          "properties": {
                            "method": { "type": "string", "const": "create" },
                            "event": { "type": "string", "enum": [{{string.Join(", ", publicationValues.Select(static value => $"\"{value}\""))}}] },
                            "body": { "type": "string" }
                          },
                          "required": ["method", "event", "body"],
                          "additionalProperties": false
                        }
                        """)
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateConditionalCompositionFactory()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("reviewer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "analyze_change",
                    Description = "Analyze a runtime input and return an opaque result."
                }
            ]
        });
        factory.RegisterServer("writer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "lifecycle_write",
                    Description = "Apply one exact lifecycle phase.",
                    InputSchema = JsonNode.Parse("""
                        {
                          "type": "object",
                          "properties": {
                            "method": { "type": "string", "enum": ["create", "submit"] }
                          },
                          "required": ["method"],
                          "additionalProperties": false
                        }
                        """)
                },
                new McpToolInfo
                {
                    Name = "add_detail",
                    Description = "Add one necessary detail to the pending effect."
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateDecisionRecoveryFactory(bool includeSecondSource)
    {
        var typedDecisionOutput = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "decision": { "type": "string", "enum": ["APPROVE", "REQUEST_CHANGES"] }
              },
              "required": ["decision"],
              "additionalProperties": false
            }
            """);
        var tools = new List<McpToolInfo>
        {
            new()
            {
                Name = "analyze_change",
                Description = "Produce one typed decision.",
                OutputSchema = typedDecisionOutput!.DeepClone()
            }
        };
        if (includeSecondSource)
        {
            tools.Add(new McpToolInfo
            {
                Name = "analyze_alternative",
                Description = "Produce another typed decision.",
                OutputSchema = typedDecisionOutput.DeepClone()
            });
        }

        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("reviewer", new MockMcpServerConfig { Tools = tools });
        RegisterDecisionWriter(factory);
        return factory;
    }

    private static InMemoryMcpClientFactory CreateOpaqueDecisionRootsFactory()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("reviewer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "opaque_analysis_a",
                    Description = "Produce one opaque analysis result."
                },
                new McpToolInfo
                {
                    Name = "opaque_analysis_b",
                    Description = "Produce one complementary opaque analysis result."
                }
            ]
        });
        RegisterDecisionWriter(factory);
        return factory;
    }

    private static InMemoryMcpClientFactory CreateMaterializerDecisionSourceFactory()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("reviewer", new MockMcpServerConfig
        {
            Tools =
            [
                CreateArtifactProducer(
                    "create_workspace",
                    McpArtifactContractConventions.WorkspaceDirectoryKind,
                    "/projectRootRelative",
                    "projectRootRelative")
            ]
        });
        RegisterDecisionWriter(factory);
        return factory;
    }

    private static InMemoryMcpClientFactory CreateMaximalArtifactDecisionFactory()
    {
        const string workspaceKind = "test.workspace";
        const string payloadKind = "test.payload";
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("artifact-source", new MockMcpServerConfig
        {
            Tools = [CreateArtifactProducer("create_workspace", workspaceKind, "/projectRoot", "projectRoot")]
        });
        factory.RegisterServer("artifact-payload", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "create_payload",
                    Description = "Create a declared payload from one materialized workspace.",
                    ArtifactContract = new McpArtifactContractResolution(
                        new McpArtifactContract(
                            1,
                            [new McpProducedArtifact(payloadKind, "/payload", McpArtifactContractConventions.MaterializeMode)],
                            [new McpConsumedArtifact(workspaceKind, "/projectRoot", true)]),
                        []),
                    InputSchema = RequiredStringObjectSchema("projectRoot"),
                    OutputSchema = RequiredStringObjectSchema("payload")
                }
            ]
        });
        factory.RegisterServer("reviewer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "analyze_change",
                    Description = "Analyze one declared workspace artifact.",
                    ArtifactContract = new McpArtifactContractResolution(
                        new McpArtifactContract(
                            1,
                            [],
                            [new McpConsumedArtifact(workspaceKind, "/projectRoot", true)]),
                        []),
                    InputSchema = RequiredStringObjectSchema("projectRoot")
                },
                new McpToolInfo
                {
                    Name = "analyze_alternative",
                    Description = "Analyze a workspace together with its declared payload artifact.",
                    ArtifactContract = new McpArtifactContractResolution(
                        new McpArtifactContract(
                            1,
                            [],
                            [
                                new McpConsumedArtifact(workspaceKind, "/projectRoot", true),
                                new McpConsumedArtifact(payloadKind, "/payload", true)
                            ]),
                        []),
                    InputSchema = RequiredStringObjectSchema("projectRoot", "payload")
                }
            ]
        });
        RegisterDecisionWriter(factory);
        return factory;
    }

    private static void RegisterDecisionWriter(InMemoryMcpClientFactory factory)
    {
        factory.RegisterServer("github", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "publish_review",
                    Description = "Create one review with an exact event.",
                    InputSchema = JsonNode.Parse("""
                        {
                          "type": "object",
                          "properties": {
                            "method": { "type": "string", "const": "create" },
                            "event": { "type": "string", "enum": ["APPROVE", "REQUEST_CHANGES"] },
                            "body": { "type": "string" }
                          },
                          "required": ["method", "event", "body"],
                          "additionalProperties": false
                        }
                        """)
                }
            ]
        });
    }

    private static InMemoryMcpClientFactory CreateRepairableConditionalReviewFactory(
        bool includeTypedAnalyzer = true)
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("reviewer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "prepare_review",
                    Description = "Prepare one part of the review without a typed decision output."
                },
                new McpToolInfo
                {
                    Name = "collect_review_evidence",
                    Description = "Collect complementary review evidence without a typed decision output."
                },
                ..(includeTypedAnalyzer
                    ? new[]
                    {
                        new McpToolInfo
                        {
                            Name = "analyze_change",
                            Description = "Analyze a change and return a typed final review decision.",
                            OutputSchema = JsonNode.Parse("""
                                {
                                  "type": "object",
                                  "properties": {
                                    "decision": { "type": "string", "enum": ["APPROVE", "REQUEST_CHANGES"] },
                                    "justification": { "type": "string" }
                                  },
                                  "required": ["decision", "justification"],
                                  "additionalProperties": false
                                }
                                """)
                        }
                    }
                    : Array.Empty<McpToolInfo>())
            ]
        });
        factory.RegisterServer("github", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "publish_review",
                    Description = "Create a pull request review with one exact event and explanatory body.",
                    InputSchema = JsonNode.Parse("""
                        {
                          "type": "object",
                          "properties": {
                            "method": { "type": "string", "const": "create" },
                            "event": { "type": "string", "enum": ["APPROVE", "REQUEST_CHANGES"] },
                            "body": { "type": "string" }
                          },
                          "required": ["method", "event", "body"],
                          "additionalProperties": false
                        }
                        """)
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateComposedSelectorFactory()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("inventory", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "inventory_action",
                    Description = "Perform a composed inventory operation.",
                    InputSchema = JsonNode.Parse("""
                        {
                          "type": "object",
                          "properties": {
                            "request": {
                              "type": "object",
                              "properties": {
                                "action": { "type": "string", "enum": ["lookup", "search"] }
                              }
                            },
                            "mode": { "type": "string", "enum": ["read", "write"] },
                            "target": { "type": "string", "description": "Destination record locator." },
                            "offset": { "type": "integer", "description": "Position within the selected target." },
                            "payload": { "type": "string" }
                          },
                          "dependentRequired": {
                            "target": ["offset"]
                          },
                          "if": {
                            "properties": {
                              "mode": { "const": "write" }
                            },
                            "required": ["mode"]
                          },
                          "then": {
                            "required": ["payload"]
                          },
                          "discriminator": {
                            "propertyName": "mode",
                            "mapping": {
                              "read": "#/$defs/readRequest",
                              "write": "#/$defs/writeRequest"
                            }
                          },
                          "oneOf": [
                            {
                              "properties": {
                                "mode": { "const": "read" }
                              }
                            },
                            {
                              "properties": {
                                "mode": { "const": "write" }
                              }
                            }
                          ]
                        }
                        """)
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateArtifactFactory(
        bool invalidProducerPointer = false,
        string consumerArtifactKind = McpArtifactContractConventions.WorkspaceDirectoryKind,
        bool verifyConsumesArtifact = true)
    {
        var producerPointer = invalidProducerPointer ? "/missing" : "/projectRootRelative";
        var producerContract = new McpArtifactContract(
            1,
            [new McpProducedArtifact(
                McpArtifactContractConventions.WorkspaceDirectoryKind,
                producerPointer,
                McpArtifactContractConventions.MaterializeMode)],
            []);
        var producer = new McpToolInfo
        {
            Name = "create_workspace",
            Description = "Materialize a workspace from a source URL.",
            ArtifactContract = new McpArtifactContractResolution(
                producerContract,
                invalidProducerPointer
                    ? ["artifacts.produces[0].pointer '/missing' does not resolve to a schema property."]
                    : []),
            InputSchema = JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": { "sourceUrl": { "type": "string" } },
                  "required": ["sourceUrl"],
                  "additionalProperties": false
                }
                """),
            OutputSchema = JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": { "projectRootRelative": { "type": "string" } },
                  "required": ["projectRootRelative"],
                  "additionalProperties": false
                }
                """)
        };
        var consumerSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": { "projectRoot": { "type": "string" } },
              "required": ["projectRoot"],
              "additionalProperties": false
            }
            """);
        var consumerContract = new McpArtifactContractResolution(
            new McpArtifactContract(
                1,
                [],
                [new McpConsumedArtifact(consumerArtifactKind, "/projectRoot", true)]),
            []);
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("workspace-provider", new MockMcpServerConfig { Tools = [producer] });
        factory.RegisterServer("workspace-consumer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "inspect_workspace",
                    Description = "Inspect a materialized workspace.",
                    ArtifactContract = consumerContract,
                    InputSchema = consumerSchema!.DeepClone()
                },
                new McpToolInfo
                {
                    Name = "verify_workspace",
                    Description = "Verify a materialized workspace.",
                    ArtifactContract = verifyConsumesArtifact ? consumerContract : null,
                    InputSchema = consumerSchema.DeepClone()
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateMultiHopArtifactFactory()
    {
        const string sourceKind = "workspace.directory";
        const string payloadKind = "revision.comparison.files";
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("source-provider", new MockMcpServerConfig
        {
            Tools = [CreateArtifactProducer("git_clone", sourceKind, "/projectRootRelative", "projectRootRelative")]
        });
        factory.RegisterServer("payload-provider", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "git_compare_refs",
                    Description = "Create one exact comparison payload from a materialized source.",
                    ArtifactContract = new McpArtifactContractResolution(
                        new McpArtifactContract(
                            1,
                            [new McpProducedArtifact(payloadKind, "/filesJson", McpArtifactContractConventions.MaterializeMode)],
                            [new McpConsumedArtifact(sourceKind, "/projectRoot", true)]),
                        []),
                    InputSchema = RequiredStringObjectSchema("projectRoot"),
                    OutputSchema = RequiredStringObjectSchema("filesJson")
                }
            ]
        });
        factory.RegisterServer("payload-consumer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "copilot_review",
                    Description = "Review one exact comparison together with its materialized source.",
                    ArtifactContract = new McpArtifactContractResolution(
                        new McpArtifactContract(
                            1,
                            [],
                            [
                                new McpConsumedArtifact(sourceKind, "/projectRoot", true),
                                new McpConsumedArtifact(payloadKind, "/filesJson", true)
                            ]),
                        []),
                    InputSchema = RequiredStringObjectSchema("projectRoot", "filesJson")
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateRecoveredReviewDecisionFactory()
    {
        const string sourceKind = "workspace.directory";
        const string payloadKind = "revision.comparison.files";
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("source-provider", new MockMcpServerConfig
        {
            Tools = [CreateArtifactProducer("git_clone", sourceKind, "/projectRootRelative", "projectRootRelative")]
        });
        factory.RegisterServer("payload-provider", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "git_compare_refs",
                    Description = "Create one exact comparison payload from a materialized source.",
                    ArtifactContract = new McpArtifactContractResolution(
                        new McpArtifactContract(
                            1,
                            [new McpProducedArtifact(payloadKind, "/filesJson", McpArtifactContractConventions.MaterializeMode)],
                            [new McpConsumedArtifact(sourceKind, "/projectRoot", true)]),
                        []),
                    InputSchema = RequiredStringObjectSchema("projectRoot"),
                    OutputSchema = RequiredStringObjectSchema("filesJson")
                }
            ]
        });
        factory.RegisterServer("payload-consumer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "copilot_review",
                    Description = "Analyze one comparison using its complete materialized source.",
                    ArtifactContract = new McpArtifactContractResolution(
                        new McpArtifactContract(
                            1,
                            [],
                            [
                                new McpConsumedArtifact(sourceKind, "/projectRoot", true),
                                new McpConsumedArtifact(payloadKind, "/filesJson", true)
                            ]),
                        []),
                    InputSchema = RequiredStringObjectSchema("projectRoot", "filesJson")
                }
            ]
        });
        factory.RegisterServer("review-writer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "submit_review",
                    Description = "Create one review using an exact runtime event.",
                    InputSchema = JsonNode.Parse("""
                        {
                          "type": "object",
                          "properties": {
                            "method": { "type": "string", "const": "create" },
                            "event": { "type": "string", "enum": ["APPROVE", "REQUEST_CHANGES", "COMMENT"] },
                            "body": { "type": "string" }
                          },
                          "required": ["method", "event", "body"],
                          "additionalProperties": false
                        }
                        """)
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateAmbiguousArtifactFactory()
    {
        const string sourceKind = "source.materialized";
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("source-provider-a", new MockMcpServerConfig
        {
            Tools = [CreateArtifactProducer("materialize_source_a", sourceKind, "/sourceRoot", "sourceRoot")]
        });
        factory.RegisterServer("source-provider-b", new MockMcpServerConfig
        {
            Tools = [CreateArtifactProducer("materialize_source_b", sourceKind, "/sourceRoot", "sourceRoot")]
        });
        factory.RegisterServer("source-consumer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "inspect_source",
                    Description = "Inspect one materialized source.",
                    ArtifactContract = new McpArtifactContractResolution(
                        new McpArtifactContract(
                            1,
                            [],
                            [new McpConsumedArtifact(sourceKind, "/sourceRoot", true)]),
                        []),
                    InputSchema = RequiredStringObjectSchema("sourceRoot")
                }
            ]
        });
        return factory;
    }

    private static InMemoryMcpClientFactory CreateUnresolvableArtifactFactory(string graph)
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("artifact-consumer", new MockMcpServerConfig
        {
            Tools =
            [
                new McpToolInfo
                {
                    Name = "consume_artifact",
                    Description = "Consume one required artifact.",
                    ArtifactContract = new McpArtifactContractResolution(
                        new McpArtifactContract(
                            1,
                            [],
                            [new McpConsumedArtifact("artifact.0", "/input0", true)]),
                        []),
                    InputSchema = RequiredStringObjectSchema("input0")
                }
            ]
        });

        McpToolInfo Bridge(int producedIndex, int consumedIndex)
        {
            var outputProperty = $"output{producedIndex}";
            var inputProperty = $"input{consumedIndex}";
            return new McpToolInfo
            {
                Name = $"produce_artifact_{producedIndex}",
                Description = "Produce one artifact from another required artifact.",
                ArtifactContract = new McpArtifactContractResolution(
                    new McpArtifactContract(
                        1,
                        [new McpProducedArtifact(
                            $"artifact.{producedIndex}",
                            $"/{outputProperty}",
                            McpArtifactContractConventions.MaterializeMode)],
                        [new McpConsumedArtifact($"artifact.{consumedIndex}", $"/{inputProperty}", true)]),
                    []),
                InputSchema = RequiredStringObjectSchema(inputProperty),
                OutputSchema = RequiredStringObjectSchema(outputProperty)
            };
        }

        var producers = graph switch
        {
            "missing" => Array.Empty<McpToolInfo>(),
            "cycle" => [Bridge(0, 1), Bridge(1, 0)],
            "limit" => Enumerable.Range(0, 5)
                .Select(index => Bridge(index, index + 1))
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(graph), graph, "Unknown artifact graph test case.")
        };
        if (producers.Length > 0)
            factory.RegisterServer("artifact-producers", new MockMcpServerConfig { Tools = producers.ToList() });
        return factory;
    }

    private static McpToolInfo CreateArtifactProducer(
        string method,
        string artifactKind,
        string pointer,
        string outputProperty)
        => new()
        {
            Name = method,
            Description = "Materialize one source artifact.",
            ArtifactContract = new McpArtifactContractResolution(
                new McpArtifactContract(
                    1,
                    [new McpProducedArtifact(artifactKind, pointer, McpArtifactContractConventions.MaterializeMode)],
                    []),
                []),
            InputSchema = JsonNode.Parse("""
                { "type": "object", "properties": {}, "additionalProperties": false }
                """),
            OutputSchema = RequiredStringObjectSchema(outputProperty)
        };

    private static JsonNode RequiredStringObjectSchema(params string[] properties)
        => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(properties.ToDictionary(
                static property => property,
                static _ => (JsonNode?)new JsonObject { ["type"] = "string" },
                StringComparer.Ordinal)),
            ["required"] = new JsonArray(properties.Select(static property => (JsonNode?)JsonValue.Create(property)).ToArray()),
            ["additionalProperties"] = false
        };

    private static string WorkspacePlan(bool includeSecondMaterializerRequirement)
    {
        var second = includeSecondMaterializerRequirement
            ? """
              - id: materialize_second_workspace
                description: Materialize the second requested workspace.
                required: true
                alternatives:
                  - server: workspace-provider
                    kind: tool
                    method: create_workspace
              """
            : string.Empty;
        return ExplicitPlan($$"""
            - id: materialize_workspace
              description: Materialize the requested workspace.
              required: true
              alternatives:
                - server: workspace-provider
                  kind: tool
                  method: create_workspace
            {{second}}
            - id: inspect_workspace
              description: Inspect the materialized workspace.
              required: true
              alternatives:
                - server: workspace-consumer
                  kind: tool
                  method: inspect_workspace
            - id: verify_workspace
              description: Verify the materialized workspace.
              required: true
              alternatives:
                - server: workspace-consumer
                  kind: tool
                  method: verify_workspace
            """)
            .Replace(
                "Load an object and produce the requested result.",
                includeSecondMaterializerRequirement
                    ? "Materialize two requested workspaces and analyze the first one."
                    : "Materialize one requested workspace and reuse it for inspection and verification.",
                StringComparison.Ordinal)
            .Replace("max_repair_attempts: 3", "max_repair_attempts: 1", StringComparison.Ordinal);
    }

    private static string CrossWorkflowWorkspaceWorkflow(
        string inspectProjectRoot,
        string verifyProjectRoot) => $$"""
        version: 1
        name: generated-workspace-pipeline
        skill:
          description: Materialize and analyze one workspace.
          tags: [generated]
          inputs: {}
          outputs: {}
        workflows:
          main:
            steps:
              - id: produce
                type: workflow.call
                input:
                  ref: { kind: local, name: materialize_workspace }
                  args:
                    source_url: https://example.invalid/source
              - id: inspect
                type: workflow.call
                input:
                  ref: { kind: local, name: inspect_workspace }
                  args:
                    project_root: {{inspectProjectRoot}}
              - id: verify
                type: workflow.call
                input:
                  ref: { kind: local, name: verify_workspace }
                  args:
                    project_root: {{verifyProjectRoot}}
          materialize_workspace:
            inputs:
              source_url: { type: string, required: true }
            steps:
              - id: materialize
                type: mcp.call
                input:
                  server: workspace-provider
                  kind: tool
                  method: create_workspace
                  request:
                    sourceUrl: ${data.inputs.source_url}
            outputs:
              project_root:
                expr: ${data.steps.materialize.response.projectRootRelative}
                type: string
          inspect_workspace:
            inputs:
              project_root: { type: string, required: true }
            steps:
              - id: inspect_call
                type: mcp.call
                input:
                  server: workspace-consumer
                  kind: tool
                  method: inspect_workspace
                  request:
                    projectRoot: ${data.inputs.project_root}
          verify_workspace:
            inputs:
              project_root: { type: string, required: true }
            steps:
              - id: verify_call
                type: mcp.call
                input:
                  server: workspace-consumer
                  kind: tool
                  method: verify_workspace
                  request:
                    projectRoot: ${data.inputs.project_root}
        """;

    private static string ExplicitPlan(string requirements) => $$"""
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  capability_preflight:
                    mode: explicit
                    requirements:
        {{Indent(requirements, 14)}}
                  generator:
                    model: gpt-4
                    prefilter: false
                    instruction: Load an object and produce the requested result.
                  validate:
                    max_repair_attempts: 3
        """;

    private static string InferredPlan() => """
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  capability_preflight:
                    mode: infer
                  generator:
                    model: gpt-4
                    prefilter: false
                    instruction: Load a configured object and optionally notify a consumer.
        """;

    private static string ConditionalInferredPlan() => """
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  capability_preflight:
                    mode: infer
                  generator:
                    model: gpt-4
                    prefilter: false
                    instruction: Review a change and publish whichever decision is determined at runtime without human confirmation.
                  on_invalid:
                    max_attempts: 1
        """;

    private static string PolicyDeniedLocalDecisionPlan() => """
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  capability_preflight:
                    mode: infer
                  generator:
                    model: gpt-4
                    prefilter: false
                    instruction: Review a change and publish whichever decision is determined at runtime without human confirmation.
                  policy:
                    allowed_step_types: [mcp.call, switch, set]
                    denied_step_types: [decision.evaluate]
                    allow_remote_workflow_refs: false
                  on_invalid:
                    max_attempts: 1
        """;

    private static string ClarifyingConditionalInferredPlan()
        => ConditionalInferredPlan().Replace(
            "mode: infer\n                  generator:",
            "mode: infer\n                    clarification:\n                      enabled: true\n                      timeout_ms: 60000\n                  generator:",
            StringComparison.Ordinal);

    private static string ConditionalWriteRelaxationPlan() => """
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  raw_prompt: Analyze two runtime results and conditionally publish one outcome without human confirmation.
                  intent_clarification:
                    mode: when_needed
                    timeout_ms: 60000
                    max_rounds: 3
                    max_questions: 15
                    max_questions_per_round: 5
                  capability_preflight:
                    mode: infer
                  generator:
                    model: gpt-4
                    prefilter: false
                    instruction: Analyze two runtime results and conditionally publish one outcome without human confirmation.
                  policy:
                    allowed_step_types: [mcp.call, switch, set]
                    denied_step_types: [decision.evaluate, workflow.plan, workflow.execute]
                    allow_remote_workflow_refs: false
                  on_invalid:
                    max_attempts: 1
        """;

    private static string ClarifyingInferredPlan() => """
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  capability_preflight:
                    mode: infer
                    clarification:
                      enabled: true
                      timeout_ms: 60000
                  generator:
                    model: gpt-4
                    prefilter: false
                    instruction: Load the intended configured object.
        """;

    private static string IntentClarifyingInferredPlan() => """
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  raw_prompt: Load one configured object.
                  intent_clarification:
                    mode: always
                    timeout_ms: 60000
                    max_rounds: 2
                    max_questions: 8
                    max_questions_per_round: 5
                  capability_preflight:
                    mode: infer
                  generator:
                    model: gpt-4
                    prefilter: false
                    instruction: Load the intended configured object.
        """;

    private static string CoverageRelaxationPlan() => """
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  raw_prompt: Create or update one unique summary record without human confirmation.
                  intent_clarification:
                    mode: when_needed
                    timeout_ms: 60000
                    max_rounds: 3
                    max_questions: 15
                    max_questions_per_round: 5
                  capability_preflight:
                    mode: infer
                  generator:
                    model: gpt-4
                    prefilter: false
                    instruction: Create or update one unique summary record without human confirmation.
                  on_invalid:
                    max_attempts: 1
        """;

    private static string UnicodeClarificationPlan() => """
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  raw_prompt: Analyser automatiquement une modification et expliquer la décision.
                  intent_clarification:
                    mode: always
                    timeout_ms: 60000
                    max_rounds: 2
                    max_questions: 8
                    max_questions_per_round: 5
                  capability_preflight:
                    mode: infer
                  generator:
                    model: gpt-4
                    prefilter: false
                    instruction: Analyser automatiquement une modification et expliquer la décision.
                  on_invalid:
                    max_attempts: 1
        """;

    private static string SplitEvidencePlan() => """
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  raw_prompt: Load a
                  capability_preflight:
                    mode: infer
                  generator:
                    model: gpt-4
                    prefilter: false
                    context: configured object
                    instruction: This instruction is not selected while raw_prompt is present.
        """;

    private static string EvidencePolicyPlan() => """
        version: 1
        workflows:
          main:
            steps:
              - id: plan
                type: workflow.plan
                input:
                  mode: basic
                  raw_prompt: Load a configured object, optionally notify a consumer, and continue without human confirmation.
                  capability_preflight:
                    mode: infer
                  generator:
                    model: gpt-4
                    prefilter: false
                    instruction: Load a configured object, optionally notify a consumer, and continue without human confirmation.
        """;

    private static string Indent(string text, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join("\n", text.Trim().Split('\n').Select(line => prefix + line.TrimEnd()));
    }

    private static async Task<RunResult> ExecuteAsync(
        string yaml,
        ILLMClient llm,
        IMcpClientFactory? mcpFactory = null,
        IHumanInputProvider? humanInputProvider = null,
        IWorkflowTelemetry? telemetry = null)
    {
        var document = WorkflowParser.Parse(yaml);
        var compiled = new WorkflowCompiler().Compile(document);
        var workflow = compiled.Workflows[compiled.Entrypoint!];
        var engine = new WorkflowEngine
        {
            LLMClient = llm,
            McpClientFactory = mcpFactory,
            HumanInputProvider = humanInputProvider,
            Telemetry = telemetry ?? NullWorkflowTelemetry.Instance
        };
        return await engine.ExecuteAsync(workflow, new JsonObject(), CancellationToken.None);
    }

    private sealed class RecordingHumanInputProvider(JsonNode? response) : IHumanInputProvider
    {
        public List<HumanInputRequest> Requests { get; } = [];

        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(response?.DeepClone());
        }
    }

    private sealed class OptionSelectingHumanInputProvider(int optionIndex) : IHumanInputProvider
    {
        public List<HumanInputRequest> Requests { get; } = [];

        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var field = Assert.Single(request.Fields!);
            var options = Assert.IsAssignableFrom<IReadOnlyList<string>>(field.Options);
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                [field.Name] = options[optionIndex],
                [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
            });
        }
    }

    private sealed class CancellingHumanInputProvider : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
            => Task.FromCanceled<JsonNode?>(new CancellationToken(canceled: true));
    }
}
