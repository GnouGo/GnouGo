using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Moq;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowPlanCapabilityPreflightTests
{
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
    public async Task InferredPreflight_UsesDiscoveredNeutralCatalogBeforeGeneration()
    {
        var prompts = new List<string>();
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                prompts.Add(request.Prompt);
                if (request.Prompt.Contains("domain-neutral workflow capability analyst", StringComparison.Ordinal))
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
                                    ["required"] = true,
                                    ["resolution"] = "mcp",
                                    ["server"] = "object-storage",
                                    ["kind"] = "tool",
                                    ["method"] = "get_object"
                                },
                                new JsonObject
                                {
                                    ["id"] = "notify",
                                    ["description"] = "Optionally notify a consumer.",
                                    ["required"] = false,
                                    ["resolution"] = "unavailable",
                                    ["server"] = "",
                                    ["kind"] = "",
                                    ["method"] = ""
                                }
                            }
                        }
                    };
                }

                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, prompts.Count);
        Assert.Contains("tool: get_object", prompts[0], StringComparison.Ordinal);
        Assert.Contains("locked by preflight", prompts[1], StringComparison.Ordinal);
        Assert.DoesNotContain("pull request", prompts[0], StringComparison.OrdinalIgnoreCase);
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
                if (request.Prompt.Contains("domain-neutral workflow capability analyst", StringComparison.Ordinal))
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
                                    ["required"] = true,
                                    ["resolution"] = "mcp",
                                    ["server"] = "object-storage",
                                    ["kind"] = "tool",
                                    ["method"] = "get_object"
                                }
                            },
                            ["constraints"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = "never_delete",
                                    ["description"] = "Never delete stored objects.",
                                    ["required"] = true,
                                    ["denied_alternatives"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["server"] = "object-storage",
                                            ["kind"] = "tool",
                                            ["method"] = "delete_object"
                                        }
                                    }
                                }
                            }
                        }
                    };
                }

                return new LLMResponse { Text = ValidStorageWorkflow };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, prompts.Count);
        Assert.Contains("invariants, not operations", prompts[1], StringComparison.Ordinal);
        Assert.Contains("object-storage/delete_object", prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InferredPreflight_RejectsGeneratedCallDeniedByLockedConstraint()
    {
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                if (request.Prompt.Contains("domain-neutral workflow capability analyst", StringComparison.Ordinal))
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
                                    ["required"] = true,
                                    ["denied_alternatives"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["server"] = "object-storage",
                                            ["kind"] = "tool",
                                            ["method"] = "delete_object"
                                        }
                                    }
                                }
                            }
                        }
                    };
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
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
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
                                ["required"] = true,
                                ["resolution"] = "unavailable",
                                ["server"] = "",
                                ["kind"] = "",
                                ["method"] = ""
                            }
                        }
                    }
                };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Equal(1, calls);
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
                                ["required"] = true,
                                ["resolution"] = "unavailable",
                                ["server"] = "",
                                ["kind"] = "",
                                ["method"] = ""
                            }
                        }
                    }
                };
            });
        var plan = InferredPlan().Replace("mode: basic", "mode: pipeline", StringComparison.Ordinal);

        var result = await ExecuteAsync(plan, llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Single(prompts);
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
    public async Task InferredPreflight_IncompleteInventoryUsesDedicatedFailFastCode()
    {
        var calls = 0;
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                return new LLMResponse
                {
                    Json = new JsonObject
                    {
                        ["complete"] = false,
                        ["operations"] = new JsonArray()
                    }
                };
            });

        var result = await ExecuteAsync(InferredPlan(), llm.Object, CreateNeutralFactory());

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal(1, calls);
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
