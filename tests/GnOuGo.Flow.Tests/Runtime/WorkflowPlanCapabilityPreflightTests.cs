using System.Reflection;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Runtime.Executors;
using GnOuGo.Mcp.Core;
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
                                    ["required"] = false
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
                                    ["required"] = true
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
                                    ["required"] = true
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
                                    ["required"] = true
                                }
                            }
                        }
                    };
                }
                if (request.Prompt.Contains("domain-neutral capability matcher", StringComparison.Ordinal))
                {
                    var get = CatalogIdForMethod(request.Prompt, "get_object");
                    var confirm = CatalogIdForMethod(request.Prompt, "human.input");
                    return new LLMResponse
                    {
                        Json = new JsonObject
                        {
                            ["operation_matches"] = new JsonArray
                            {
                                MatchingNode("confirm_load", "matched", [confirm], "The native confirmation step is sufficient."),
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
    public async Task InferredPreflight_ExplicitUnattendedRequestDoesNotInjectConfirmation()
    {
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
                    Assert.DoesNotContain("platform_confirm_external_write", request.Prompt, StringComparison.Ordinal);
                    return MatchResponse(("remove_object", "mcp", CatalogIdForMethod(request.Prompt, "delete_object")));
                }

                return new LLMResponse { Text = InvalidDeniedStorageWorkflow };
            });
        var plan = InferredPlan().Replace(
            "Load a configured object and optionally notify a consumer.",
            "Run unattended and remove the configured object.",
            StringComparison.Ordinal);

        var result = await ExecuteAsync(plan, llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
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

    private static LLMResponse MatchResponse(params (string OperationId, string Resolution, string CatalogId)[] matches)
        => MatchResponseWithConstraints(matches, Array.Empty<(string ConstraintId, string[] CatalogIds)>());

    private static LLMResponse PhysicalCandidateResponse(
        params (string OperationId, string[] CatalogIds)[] operations)
        => new()
        {
            Json = new JsonObject
            {
                ["operation_candidates"] = new JsonArray(operations.Select(static operation => (JsonNode)new JsonObject
                {
                    ["operation_id"] = operation.OperationId,
                    ["catalog_ids"] = new JsonArray(operation.CatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
                }).ToArray()),
                ["constraint_candidates"] = new JsonArray()
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

    private static string CatalogIdForMethod(string prompt, string method)
    {
        var marker = $" method={method}";
        var line = prompt.Split('\n').First(value => value.Contains(marker, StringComparison.Ordinal));
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string CatalogIdForBinding(string prompt, string path, string value)
    {
        var marker = $"request_bindings=[{path}=\"{value}\"]";
        var line = prompt.Split('\n').First(candidate => candidate.Contains(marker, StringComparison.Ordinal));
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
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
        string consumerArtifactKind = McpArtifactContractMetadata.WorkspaceDirectoryKind)
    {
        static JsonObject Meta(string json) => new()
        {
            [McpArtifactContractMetadata.MetaPropertyName] = JsonNode.Parse(json)
        };

        var producerMetadata = invalidProducerPointer
            ? """{"artifacts":{"version":1,"produces":[{"kind":"workspace.directory","pointer":"/missing","mode":"materialize"}]}}"""
            : McpArtifactContractMetadata.WorkspaceDirectoryProducerProjectRootRelativeJson;
        var producer = new McpToolInfo
        {
            Name = "create_workspace",
            Description = "Materialize a workspace from a source URL.",
            Meta = Meta(producerMetadata),
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
        var consumerMetadata = McpArtifactContractMetadata.WorkspaceDirectoryConsumerProjectRootJson.Replace(
            McpArtifactContractMetadata.WorkspaceDirectoryKind,
            consumerArtifactKind,
            StringComparison.Ordinal);
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
                    Meta = Meta(consumerMetadata),
                    InputSchema = consumerSchema!.DeepClone()
                },
                new McpToolInfo
                {
                    Name = "verify_workspace",
                    Description = "Verify a materialized workspace.",
                    Meta = Meta(consumerMetadata),
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

    private static string Indent(string text, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join("\n", text.Trim().Split('\n').Select(line => prefix + line.TrimEnd()));
    }

    private static async Task<RunResult> ExecuteAsync(
        string yaml,
        ILLMClient llm,
        IMcpClientFactory? mcpFactory = null)
    {
        var document = WorkflowParser.Parse(yaml);
        var compiled = new WorkflowCompiler().Compile(document);
        var workflow = compiled.Workflows[compiled.Entrypoint!];
        var engine = new WorkflowEngine
        {
            LLMClient = llm,
            McpClientFactory = mcpFactory
        };
        return await engine.ExecuteAsync(workflow, new JsonObject(), CancellationToken.None);
    }
}
