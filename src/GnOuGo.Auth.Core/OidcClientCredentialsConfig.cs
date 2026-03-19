namespace GnOuGo.Auth.Core;

public sealed record OidcClientCredentialsConfig(
    string Issuer,
    string ClientId,
    string Scopes,
    string? ClientSecret = null,
    string? PrivateKeyPem = null
);

