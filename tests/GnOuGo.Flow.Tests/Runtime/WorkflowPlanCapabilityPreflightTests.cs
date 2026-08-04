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
        Assert.Equal(ErrorCodes.CapabilityPreflightUnavailable, result.Error!.Code);
        Assert.Contains("omitted", result.Error.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("invalid or incomplete", result.Error.Message, StringComparison.OrdinalIgnoreCase);
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

        var result = await ExecuteAsync(InferredPlan(), llm.Object, factory);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal("selector_value_limit_exceeded", result.Error.Details!["reason"]!.GetValue<string>());
        llm.Verify(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()), Times.Never);
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

        var result = await ExecuteAsync(InferredPlan(), llm.Object, factory);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.CapabilityPreflightInferenceFailed, result.Error!.Code);
        Assert.Equal("catalog_too_large", result.Error.Details!["reason"]!.GetValue<string>());
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
        Assert.Contains("provider selection, secret-vault lookup", prompts[0], StringComparison.Ordinal);
        Assert.Contains("persistence, registration, or provisioning", prompts[0], StringComparison.Ordinal);
        Assert.Contains("method=get_object", prompts[1], StringComparison.Ordinal);
        Assert.Contains("locked by preflight", prompts[2], StringComparison.Ordinal);
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
        var llm = new Mock<ILLMClient>();
        llm.Setup(client => client.CallAsync(It.IsAny<LLMRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LLMRequest request, CancellationToken _) =>
            {
                calls++;
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
        Assert.Equal(2, calls);
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
        Assert.Equal(2, prompts.Count);
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

    private static LLMResponse MatchResponse(params (string OperationId, string Resolution, string CatalogId)[] matches)
        => MatchResponseWithConstraints(matches, Array.Empty<(string ConstraintId, string[] CatalogIds)>());

    private static LLMResponse InventoryResponse(params (string Id, string Description, bool Required)[] operations)
        => new()
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["operations"] = new JsonArray(operations.Select(static operation => (JsonNode)new JsonObject
                {
                    ["id"] = operation.Id,
                    ["description"] = operation.Description,
                    ["required"] = operation.Required
                }).ToArray()),
                ["constraints"] = new JsonArray()
            }
        };

    private static LLMResponse MatchResponseWithConstraints(
        IReadOnlyList<(string OperationId, string Resolution, string CatalogId)> matches,
        IReadOnlyList<(string ConstraintId, string[] CatalogIds)> denials)
        => new()
        {
            Json = new JsonObject
            {
                ["complete"] = true,
                ["operation_matches"] = new JsonArray(matches.Select(static match => (JsonNode)new JsonObject
                {
                    ["operation_id"] = match.OperationId,
                    ["resolution"] = match.Resolution,
                    ["catalog_id"] = match.CatalogId
                }).ToArray()),
                ["constraint_denials"] = new JsonArray(denials.Select(static denial => (JsonNode)new JsonObject
                {
                    ["constraint_id"] = denial.ConstraintId,
                    ["catalog_ids"] = new JsonArray(denial.CatalogIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
                }).ToArray())
            }
        };

    private static string CatalogIdForMethod(string prompt, string method)
    {
        var marker = $" method={method} ";
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
                            }
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
