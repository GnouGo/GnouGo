namespace GnOuGo.KeyVault.Core.Services;

/// <summary>
/// Represents a storage or cryptography failure while accessing KeyVault data.
/// Consumers can handle this exception without depending on the persistence implementation.
/// </summary>
public sealed class KeyVaultAccessException : Exception
{
    public KeyVaultAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
