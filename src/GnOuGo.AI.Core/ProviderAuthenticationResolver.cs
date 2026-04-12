using GnOuGo.Auth.Core;

namespace GnOuGo.AI.Core;

internal static class ProviderAuthenticationResolver
{
    public static async ValueTask<string?> ResolveBearerTokenAsync(
        HttpClient http,
        ModelProviderOptions provider,
        Func<ModelProviderOptions, string?> fallbackResolver,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            return provider.ApiKey;

        if (HasOidcConfiguration(provider))
        {
            ValidateOidcConfiguration(provider);
            var tokenProvider = new OidcJwtApiKeyProvider(
                http,
                new OidcClientCredentialsConfig(
                    provider.Issuer!,
                    provider.ClientId!,
                    provider.Scopes!,
                    provider.ClientSecret));

            return await tokenProvider.GetApiKeyAsync(ct);
        }

        return fallbackResolver(provider);
    }

    private static bool HasOidcConfiguration(ModelProviderOptions provider)
        => !string.IsNullOrWhiteSpace(provider.Issuer)
           || !string.IsNullOrWhiteSpace(provider.ClientId)
           || !string.IsNullOrWhiteSpace(provider.Scopes)
           || !string.IsNullOrWhiteSpace(provider.ClientSecret);

    private static void ValidateOidcConfiguration(ModelProviderOptions provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Issuer))
            throw new InvalidOperationException("OIDC issuer is required when OIDC authentication is configured.");

        if (string.IsNullOrWhiteSpace(provider.ClientId))
            throw new InvalidOperationException("OIDC client_id is required when OIDC authentication is configured.");

        if (string.IsNullOrWhiteSpace(provider.Scopes))
            throw new InvalidOperationException("OIDC scopes are required when OIDC authentication is configured.");

        if (string.IsNullOrWhiteSpace(provider.ClientSecret))
            throw new InvalidOperationException("OIDC client_secret is required when OIDC authentication is configured.");
    }
}

