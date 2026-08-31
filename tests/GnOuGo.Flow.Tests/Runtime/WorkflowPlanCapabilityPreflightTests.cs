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
        Assert.NotNull(operationProperties["decision_source_operation_id"]);
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

        response["diagnostics"]![0]!["evidence"]![0]!["catalog_excerpt"] = "Invented unsupported excerpt.";
        var invalidReview = method.Invoke(null, [response, catalog, matches])!;
        Assert.False(Assert.IsType<bool>(
            invalidReview.GetType().GetProperty("ContractValid")!.GetValue(invalidReview)));
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
            ConstantLlm("""
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
                            project_root: ${data.steps.produce.outputs.project_root}
                      - id: verify
                        type: workflow.call
                        input:
                          ref: { kind: local, name: verify_workspace }
                          args:
                            project_root: ${data.steps.produce.outputs.project_root}
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
                """).Object,
            CreateArtifactFactory());

        Assert.True(result.Success, result.Error?.Message);
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
    public async Task InferredPreflight_PrefersOneExactSelectorOverUnrelatedComposedCandidates()
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
                    return MatchingResponse((
                        "list_items",
                        "composed",
                        new[]
                        {
                            CatalogIdForBinding(request.Prompt, "/method", "list_items"),
                            CatalogIdForBinding(request.Prompt, "/method", "get_status")
                        },
                        Array.Empty<string>(),
                        "Both selectors were placed in one composition."));
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
        Assert.Equal(1, matcherCalls);
        var capability = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(
            result.Outputs!["plan"]!["meta"]!["capability_preflight"]!["capabilities"])));
        Assert.Equal("list_items", Assert.Single(Assert.IsType<JsonArray>(
            capability["request_bindings"]))!.AsObject()["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredPreflight_RefinesBaseToolCandidateToExactSiblingSelector()
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
                    return MatchingResponse((
                        "list_items",
                        "composed",
                        new[] { CatalogIdForWholeTool(request.Prompt, "inventory_read") },
                        Array.Empty<string>(),
                        "The base physical tool was incorrectly returned as a one-member composition."));
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
        Assert.Equal(1, matcherCalls);
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
    public async Task InferredPreflight_RepairsCompositionMissingRequiredArtifactProducer()
    {
        var matchingCalls = 0;
        var repairPromptContainedProducerCandidates = false;
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
                    var create = CatalogIdForMethod(request.Prompt, "create_workspace");
                    if (matchingCalls == 1)
                    {
                        return MatchingResponse((
                            "analyze_content",
                            "matched",
                            new[] { analyze },
                            Array.Empty<string>(),
                            "The analyzer performs the requested operation."));
                    }

                    repairPromptContainedProducerCandidates = request.Prompt.Contains(analyze, StringComparison.Ordinal)
                                                              && request.Prompt.Contains(create, StringComparison.Ordinal)
                                                              && request.Prompt.Contains("requires an existing operational artifact", StringComparison.Ordinal);
                    return MatchingResponse((
                        "analyze_content",
                        "composed",
                        new[] { create, analyze },
                        Array.Empty<string>(),
                        "The creator supplies the existing workspace required by the analyzer."));
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
        Assert.Equal(2, matchingCalls);
        Assert.True(repairPromptContainedProducerCandidates);
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
    public async Task InferredPreflight_ValidatesConditionalDecisionAcrossLocalWorkflowContracts()
    {
        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            CreateConditionalLlm(ValidCrossWorkflowConditionalWorkflow).Object,
            CreateConditionalReviewFactory());

        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public async Task InferredPreflight_RejectsConditionalDecisionFromUnprovenCallerInput()
    {
        var unsafeWorkflow = ValidCrossWorkflowConditionalWorkflow.Replace(
            "decision: ${data.steps.review.outputs.decision}",
            "decision: ${data.inputs.forced_decision}",
            StringComparison.Ordinal);

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            CreateConditionalLlm(unsafeWorkflow).Object,
            CreateConditionalReviewFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal("conditional_activation_invalid", result.Error.Details!["reason"]!.GetValue<string>());
    }

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
            _ => ValidConditionalWorkflow.Replace(
                "        default: []",
                "        default:\n          - id: publish_default\n            type: mcp.call\n            input:\n              server: github\n              kind: tool\n              method: publish_review\n              request:\n                method: create\n                event: APPROVE\n                body: ${data.steps.analyze.response.justification}",
                StringComparison.Ordinal)
        };

        var result = await ExecuteAsync(
            ConditionalInferredPlan(),
            CreateConditionalLlm(invalid).Object,
            CreateConditionalReviewFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal("conditional_activation_invalid", result.Error.Details!["reason"]!.GetValue<string>());
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

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task CapabilityCoverageRelaxation_PreservesOrExplicitlyRelaxesRequirement(
        int selectedOption,
        bool expectSuccess)
    {
        var inventoryCalls = 0;
        var coverageCalls = 0;
        var matchingCalls = 0;
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
                    return CapabilityCoverageResponse(
                        request.Prompt,
                        supported: inventoryCalls > 1);
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
            Assert.Contains("method: add_record", result.Outputs!["plan"]!["yaml"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
            Assert.Equal("incomplete_effect_coverage", result.Error.Details!["reason"]!.GetValue<string>());
            Assert.Equal(1, inventoryCalls);
            Assert.Equal(2, matchingCalls);
            Assert.Equal(2, coverageCalls);
        }
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
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
                request.Prompt.Contains("domain-neutral workflow runtime analyst", StringComparison.Ordinal)
                    ? InventoryResponse(("load_object", "Load the requested object.", true))
                    : MatchResponse(("load_object", "mcp", "cap_999999")));

        var result = await ExecuteAsync(ClarifyingInferredPlan(), llm.Object, CreateNeutralFactory(), human);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
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

    private static LLMResponse CapabilityCoverageResponse(string prompt, bool supported)
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
                    ["operation_id"] = "publish_summary",
                    ["status"] = supported ? "supported" : "incomplete",
                    ["unsupported_requirement_id"] = supported ? string.Empty : requirementId,
                    ["supported_weaker_behavior"] = supported
                        ? string.Empty
                        : "Create one new summary record.",
                    ["candidate_catalog_ids"] = new JsonArray(catalogId),
                    ["evidence"] = new JsonArray(new JsonObject
                    {
                        ["catalog_id"] = catalogId,
                        ["requirement_id"] = requirementId,
                        ["catalog_excerpt"] = "Create one new summary record."
                    })
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
        bool exposeDecisionOutput = true)
    {
        decisionValues ??= ["APPROVE", "REQUEST_CHANGES"];
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
        string consumerArtifactKind = McpArtifactContractConventions.WorkspaceDirectoryKind)
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
                    ArtifactContract = consumerContract,
                    InputSchema = consumerSchema.DeepClone()
                }
            ]
        });
        return factory;
    }

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
