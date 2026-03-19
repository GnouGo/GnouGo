namespace GnOuGo.Auth.Core;

public interface IApiKeyProvider
{
    /// <summary>
    /// Returns a token to be used as "API key" (typically a JWT access_token).
    /// </summary>
    ValueTask<string> GetApiKeyAsync(CancellationToken ct = default);
}

