using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Agent.Server.Tests;

public sealed class WorkflowFailureFormatterTests
{
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
        Assert.Contains("Configure a matching discovered capability", presentation.UserMessage, StringComparison.Ordinal);
        Assert.Equal(1, presentation.UnavailableCapabilityCount);
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
}
