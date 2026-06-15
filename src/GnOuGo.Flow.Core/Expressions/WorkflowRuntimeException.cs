using GnOuGo.Flow.Core.Models;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Expressions;

/// <summary>
/// Runtime exception for workflow execution errors.
/// </summary>
public sealed class WorkflowRuntimeException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }
    public JsonNode? Details { get; }

    public WorkflowRuntimeException(string code, string message, bool retryable = false, Exception? inner = null, JsonNode? details = null)
        : base(message, inner)
    {
        Code = code;
        Retryable = retryable;
        Details = details;
    }

    public WorkflowError ToWorkflowError() => new()
    {
        Code = Code,
        Type = Code,
        Message = Message,
        Retryable = Retryable,
        Details = Details?.DeepClone()
    };
}
