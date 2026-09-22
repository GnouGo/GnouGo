# GnOuGo.Auth.Core

Independent .NET 10 authentication abstractions and credential providers. The package has no GnOuGo project dependencies.

`IApiKeyProvider` is implemented by `StaticApiKeyProvider`, `CopilotApiKeyProvider`, and `OidcJwtApiKeyProvider`. Consumers own configuration binding, credential storage, and authorization-header selection.

```csharp
var provider = new OidcJwtApiKeyProvider(httpClient,
    new OidcClientCredentialsConfig(issuer, clientId, scopes, ClientSecret: secret));
string token = await provider.GetApiKeyAsync(cancellationToken);
```

For private-key authentication, supply `PrivateKeyPem` instead of `ClientSecret`. Discovery resolves the token endpoint; assertions use RS256, the client ID as issuer/subject, and the token endpoint as audience. Secret authentication uses HTTP Basic. Keep a provider instance per credential configuration to share its token cache and synchronized refresh.

Token lifetimes are never extended beyond the issuer's `expires_in`. The refresh margin is 10% of the lifetime, capped at 30 seconds, and acquisition time counts against it. Invalid lifetimes and tokens already expired during acquisition fail closed. Token endpoint failures report only the HTTP status, excluding response bodies and reason phrases. The original two-argument constructor is retained; an additional `TimeProvider` overload permits deterministic cache tests.

```bash
dotnet build src/GnOuGo.Auth.Core/GnOuGo.Auth.Core.csproj
dotnet test tests/GnOuGo.Auth.Core.Tests/GnOuGo.Auth.Core.Tests.csproj
dotnet pack src/GnOuGo.Auth.Core/GnOuGo.Auth.Core.csproj -c Release
```
