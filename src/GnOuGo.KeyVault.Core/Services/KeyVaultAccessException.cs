namespace GnOuGo.KeyVault.Core.Services;

/// <summary>
/// Represents a storage or cryptography failure while accessing KeyVault data.
/// Consumers can handle this exception without depending on the persistence implementation.
/// </summary>
public sealed class KeyVaultAccessException : Exception
{
    /// <summary>The bounded contention retries were exhausted; callers must retain durable work for recovery.</summary>
    public bool IsTransient { get; }

    public KeyVaultAccessException(string message, Exception innerException)
        : this(message, innerException, false)
    {
    }

    public KeyVaultAccessException(string message, Exception innerException, bool isTransient)
        : base(message, innerException)
    {
        IsTransient = isTransient;
    }
}
