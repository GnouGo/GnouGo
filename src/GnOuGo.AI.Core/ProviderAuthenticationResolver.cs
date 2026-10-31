using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GnOuGo.Auth.Core;

namespace GnOuGo.AI.Core;

internal static class ProviderAuthenticationResolver
{
    private static readonly ConditionalWeakTable<HttpClient,
        ConcurrentDictionary<OidcClientCredentialsConfig, OidcJwtApiKeyProvider>> TokenProviders = new();

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
            try
            {
                ValidateOidcConfiguration(provider);
                var credentials = new OidcClientCredentialsConfig(
                    provider.Issuer!, provider.ClientId!, provider.Scopes!, provider.ClientSecret, provider.PrivateKeyPem);
                var tokenProvider = TokenProviders.GetOrCreateValue(http).GetOrAdd(credentials,
                    static (configuration, client) => new OidcJwtApiKeyProvider(client, configuration), http);

                return await tokenProvider.GetApiKeyAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failure = LLMProviderFailureClassifier.Classify(exception);
                failure.Stage = LLMProviderFailureStage.Authentication;
                throw failure;
            }
        }

        return fallbackResolver(provider);
    }

    private static bool HasOidcConfiguration(ModelProviderOptions provider)
        => !string.IsNullOrWhiteSpace(provider.Issuer)
           || !string.IsNullOrWhiteSpace(provider.ClientId)
           || !string.IsNullOrWhiteSpace(provider.Scopes)
           || !string.IsNullOrWhiteSpace(provider.ClientSecret)
           || !string.IsNullOrWhiteSpace(provider.PrivateKeyPem);

    private static void ValidateOidcConfiguration(ModelProviderOptions provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Issuer))
            throw new InvalidOperationException("OIDC issuer is required when OIDC authentication is configured.");

        if (string.IsNullOrWhiteSpace(provider.ClientId))
            throw new InvalidOperationException("OIDC client_id is required when OIDC authentication is configured.");

        if (string.IsNullOrWhiteSpace(provider.Scopes))
            throw new InvalidOperationException("OIDC scopes are required when OIDC authentication is configured.");

        if (string.IsNullOrWhiteSpace(provider.ClientSecret)
            && string.IsNullOrWhiteSpace(provider.PrivateKeyPem))
        {
            throw new InvalidOperationException("OIDC client_secret or private_key_pem is required when OIDC authentication is configured.");
        }
    }
}

