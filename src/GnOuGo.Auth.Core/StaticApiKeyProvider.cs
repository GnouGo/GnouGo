namespace GnOuGo.Auth.Core;

/// <summary>
/// Simple API Key provider qui retourne directement une clé statique.
/// </summary>
public class StaticApiKeyProvider : IApiKeyProvider
{
    private readonly string _apiKey;

    public StaticApiKeyProvider(string apiKey)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    public ValueTask<string> GetApiKeyAsync(CancellationToken ct = default)
    {
        return ValueTask.FromResult(_apiKey);
    }
}

