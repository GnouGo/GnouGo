using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Expressions;

/// <summary>
/// Runtime exception for workflow execution errors.
/// </summary>
public sealed class WorkflowRuntimeException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }

    public WorkflowRuntimeException(string code, string message, bool retryable = false, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Retryable = retryable;
    }

    public WorkflowError ToWorkflowError() => new()
    {
        Code = Code,
        Type = Code,
        Message = Message,
        Retryable = Retryable
    };
}

