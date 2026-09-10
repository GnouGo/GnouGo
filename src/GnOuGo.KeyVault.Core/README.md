# GnOuGo.KeyVault.Core

Generic encrypted storage for tenant-owned secrets and records, with audit and
workspace path resolution. Consumers own their record collections, key names,
serialization contracts, configuration mapping, and concurrency policy.

Use the public secret abstractions for secret operations and `IKeyVaultRecordStore`
for encrypted record payloads. `KeyVaultRecordStoreFactory.CreateWorkspaceStore`
resolves the database location through the workspace conventions. Every record
operation requires a collection, tenant, key, and author. Payloads are encrypted
at rest; record values must not be written to logs.

The library owns its persistence details. Its EF Core services use SQLite; its
separately scoped record-store implementation supports Native AOT through the
public record API. Consumers must not query or modify the database directly.

```sh
dotnet build src/GnOuGo.KeyVault.Core
dotnet test tests/GnOuGo.KeyVault.Core.Tests
dotnet pack src/GnOuGo.KeyVault.Core -c Release
```
