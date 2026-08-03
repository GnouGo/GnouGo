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
}
