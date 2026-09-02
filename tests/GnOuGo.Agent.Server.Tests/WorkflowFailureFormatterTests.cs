using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Agent.Server.Tests;

public sealed class WorkflowFailureFormatterTests
{
    [Fact]
    public void Format_RendersOnlySanitizedLlmRecoveryDiagnostics()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.LlmNetwork,
            Message = "The LLM provider temporarily rate-limited the request.",
            Details = new JsonObject
            {
                ["classification"] = "rate_limited",
                ["status_code"] = 429,
                ["attempt_count"] = 4,
                ["retry_exhausted"] = true,
                ["retry_after_ms"] = 5_000,
                ["provider_code"] = "rate_limit_exceeded",
                ["recommended_action"] = "retry",
                ["endpoint"] = "https://secret.example/responses",
                ["response_body"] = "private provider body"
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.Contains("LLM provider request outcome", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Classification: rate_limited", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("HTTP status: 429", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Attempts: 4", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Retry exhausted: yes", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Accepted Retry-After: 5000 ms", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Provider code: rate_limit_exceeded", presentation.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", presentation.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private provider body", presentation.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ListsUnavailableOperationsAndDiscoveryFailures()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.CapabilityPreflightUnavailable,
            Message = "Required operations are unavailable.",
            Details = new JsonObject
            {
                ["unavailable_capabilities"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "archive_record",
                        ["description"] = "Archive a record in durable storage."
                    }
                },
                ["unavailable_servers"] = new JsonArray("inventory-service")
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.Contains(ErrorCodes.CapabilityPreflightUnavailable, presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("archive_record: Archive a record in durable storage.", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("inventory-service", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Restore the failed MCP server's startup, connectivity, or configuration", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Configure a matching discovered capability", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Equal(1, presentation.UnavailableCapabilityCount);
        Assert.Equal(1, presentation.UnavailableServerCount);
    }

    [Fact]
    public void Format_DiscoveryFailureUsesCatalogRecoveryGuidanceOnly()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.CapabilityPreflightDiscoveryFailed,
            Message = "Capability discovery failed.",
            Details = new JsonObject
            {
                ["unavailable_servers"] = new JsonArray("inventory-service")
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.Contains("Restore the failed MCP server's startup, connectivity, or configuration", presentation.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Configure a matching discovered capability", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Equal(0, presentation.UnavailableCapabilityCount);
        Assert.Equal(1, presentation.UnavailableServerCount);
    }

    [Fact]
    public void Format_UnwrapsFinalPipelineCapabilityDiagnostics()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.TemplatePlan,
            Message = "Pipeline assembly failed.",
            Details = new JsonObject
            {
                ["terminal_error"] = new JsonObject
                {
                    ["code"] = ErrorCodes.CapabilityPreflightUnavailable,
                    ["details"] = new JsonObject
                    {
                        ["unavailable_capabilities"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "confirm_external_write",
                                ["description"] = "Require confirmation immediately before an external write.",
                                ["resolution"] = "native",
                                ["method"] = "human.input"
                            }
                        }
                    }
                }
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.Contains("confirm_external_write", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Require confirmation", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Equal(1, presentation.UnavailableCapabilityCount);
    }

    [Fact]
    public void Format_UnwrapsRepairStallConstraintViolation()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.WorkflowPlanRepairStalled,
            Message = "Workflow repair stalled.",
            Details = new JsonObject
            {
                ["last_error"] = new JsonObject
                {
                    ["code"] = ErrorCodes.CapabilityPreflightUnavailable,
                    ["details"] = new JsonObject
                    {
                        ["violated_constraints"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "deny_destructive_action",
                                ["description"] = "Do not destructively mutate an external record.",
                                ["server"] = "inventory",
                                ["kind"] = "tool",
                                ["method"] = "inventory_action",
                                ["request_bindings"] = new JsonArray
                                {
                                    new JsonObject { ["path"] = "/method", ["value"] = "delete" }
                                }
                            }
                        }
                    }
                }
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.Contains("Locked capability constraint violations", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("deny_destructive_action", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("inventory/inventory_action (tool)", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("/method=\"delete\"", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Equal(1, presentation.ViolatedConstraintCount);
    }

    [Fact]
    public void Format_RedactsSensitiveLookingDiagnosticLines()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.CapabilityPreflightUnavailable,
            Message = "Required operations are unavailable.",
            Details = new JsonObject
            {
                ["unavailable_capabilities"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "configure_sink",
                        ["description"] = "api_key: should-not-appear"
                    }
                }
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.DoesNotContain("should-not-appear", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("<redacted>", presentation.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ExplainsIncompleteCapabilityInventory()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.CapabilityPreflightInferenceFailed,
            Message = "Capability inference could not produce a complete runtime operation inventory after one repair attempt.",
            Details = new JsonObject
            {
                ["repair_attempted"] = true,
                ["incomplete_reasons"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "missing_retention_intent",
                        ["description"] = "Clarify whether processed records must be retained after delivery."
                    }
                }
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.Contains("Why the runtime operation inventory remained incomplete", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("missing_retention_intent: Clarify whether processed records must be retained", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Tool availability and exact capability matching are evaluated separately", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Equal(1, presentation.InferenceReasonCount);
    }

    [Fact]
    public void Format_ExplainsInventoryModelContractFailureWithoutRequestingIntentClarification()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.CapabilityPreflightInferenceFailed,
            Message = "Capability inventory inference violated its deterministic evidence contract after one repair attempt.",
            Details = new JsonObject
            {
                ["classification"] = "model_contract_violation",
                ["recommended_action"] = "retry_or_change_planning_model",
                ["contract_issues"] = new JsonArray(new JsonObject
                {
                    ["code"] = "excerpt_not_found",
                    ["operation_id"] = "op_analyze",
                    ["field"] = "coverage_requirements",
                    ["source_id"] = "clarification_0001",
                    ["evidence_id"] = "evidence_123"
                })
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.Contains("Capability inventory evidence contract issues", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("excerpt_not_found: op_analyze/coverage_requirements", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("does not need clarification", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("select a planning model", presentation.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Clarify the requested runtime behavior", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Equal(1, presentation.InferenceReasonCount);
    }

    [Fact]
    public void Format_ExplainsCoverageReviewContractFailureWithGroundingIds()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.CapabilityPreflightInferenceFailed,
            Message = "Capability coverage review remained invalid after one bounded repair attempt.",
            Details = new JsonObject
            {
                ["phase"] = "capability_coverage_review",
                ["classification"] = "model_contract_violation",
                ["recommended_action"] = "retry_or_change_planning_model",
                ["contract_issues"] = new JsonArray(new JsonObject
                {
                    ["code"] = "evidence_excerpt_not_found",
                    ["operation_id"] = "op_publish",
                    ["field"] = "evidence",
                    ["catalog_id"] = "cap_000123",
                    ["requirement_id"] = "evidence_456"
                })
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.Contains("Capability coverage review contract issues", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("evidence_excerpt_not_found: op_publish/evidence", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("catalog cap_000123", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("requirement evidence_456", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("does not need clarification", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Equal(1, presentation.InferenceReasonCount);
    }

    [Fact]
    public void Format_RedactsSensitiveIncompleteReasonValues()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.CapabilityPreflightInferenceFailed,
            Message = "Inventory incomplete.",
            Details = new JsonObject
            {
                ["incomplete_reasons"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "missing_runtime_intent",
                        ["description"] = "authorization: should-not-appear"
                    }
                }
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.DoesNotContain("should-not-appear", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("<redacted>", presentation.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ExplainsPerOperationMatchingIssuesWithBoundedCandidates()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.CapabilityPreflightInferenceFailed,
            Message = "Capability matching remained ambiguous.",
            Details = new JsonObject
            {
                ["matching_issues"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["operation_id"] = "read_feedback",
                        ["description"] = "Read both primary and nested feedback records.",
                        ["status"] = "ambiguous",
                        ["reason"] = "Two documented selector variants are plausible.",
                        ["candidate_capabilities"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["catalog_id"] = "cap_000123",
                                ["resolution"] = "mcp",
                                ["server"] = "feedback",
                                ["kind"] = "tool",
                                ["method"] = "feedback_read",
                                ["request_bindings"] = new JsonArray
                                {
                                    new JsonObject { ["path"] = "/method", ["value"] = "list_threads" }
                                }
                            }
                        }
                    }
                }
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.Contains("Capability matching issues", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("read_feedback [ambiguous]", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("cap_000123: feedback/feedback_read (tool)", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("/method=\"list_threads\"", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Clarify the observable behavior", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Equal(1, presentation.MatchingIssueCount);
    }

    [Fact]
    public void Format_DoesNotSendMalformedCapabilityMatchingToIntentClarification()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.CapabilityPreflightInferenceFailed,
            Message = "Capability matching remained invalid after one bounded repair attempt.",
            Details = new JsonObject
            {
                ["planning_outcome"] = "cannot_plan_safely",
                ["classification"] = "model_contract_violation",
                ["recommended_action"] = "retry_or_change_planning_model",
                ["matching_issues"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["operation_id"] = "cleanup",
                        ["description"] = "Clean up temporary resources.",
                        ["status"] = "invalid",
                        ["reason"] = "The selected capability cardinality is inconsistent.",
                        ["reason_code"] = "matching_cardinality_invalid"
                    }
                }
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.Contains("Classification: model_contract_violation", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Diagnostic: matching_cardinality_invalid", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("request itself does not need clarification", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("select a planning model", presentation.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Clarify the observable behavior", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Equal(1, presentation.MatchingIssueCount);
    }

    [Fact]
    public void Format_ExplainsStructuredInferenceContractPhase()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.CapabilityPreflightInferenceFailed,
            Message = "Capability inference returned an invalid contract.",
            Details = new JsonObject
            {
                ["inference_phase"] = "capability_matching_parse",
                ["inference_error"] = "operation_matches was missing from the structured response."
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.Contains("Capability inference contract failure", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Phase: capability_matching_parse", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("operation_matches was missing", presentation.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ExplainsCannotPlanSafelyWithoutExposingAnswers()
    {
        var error = new WorkflowError
        {
            Code = ErrorCodes.WorkflowPlanCannotPlanSafely,
            Message = "The request remains ambiguous.",
            Details = new JsonObject
            {
                ["planning_outcome"] = "cannot_plan_safely",
                ["clarification_stage"] = "capability_matching",
                ["clarification_rounds"] = 2,
                ["clarification_questions"] = 8,
                ["reason"] = "Two mutually exclusive design-time outcomes remain required.",
                ["recommended_action"] = "refine_request_or_abandon"
            }
        };

        var presentation = WorkflowFailureFormatter.Format(error);

        Assert.Contains("Intent clarification outcome", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Outcome: cannot_plan_safely", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Stage: capability_matching", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Two mutually exclusive", presentation.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("clarification_answers", presentation.UserMessage, StringComparison.Ordinal);
    }
}
