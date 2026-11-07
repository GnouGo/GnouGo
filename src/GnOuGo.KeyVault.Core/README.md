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

`IKeyVaultRecordReader` is an optional bounded capability alongside the unchanged
record API. `QueryMetadataAsync` scopes collection, tenant and exact key prefix,
returns at most 200 headers without decrypting bodies, and continues by update
ticks plus exact key. `GetMetadataAsync` addresses one record. Updates use monotonic
scope timestamps so an updated key remains visible after its cursor.

`ReadRangeAsync` takes a content version and UTF-16 offset/count (2–65,536). Follow
`NextOffset`: pages never split Unicode scalar pairs. Changed versions fail.
New large values use independently encrypted chunks; authenticated manifests/chunks
bind collection, tenant, key, record, version and offset. Publication/replacement is
transactional, including cancellation. `GetAsync`/`ListAsync` reconstruct the same
logical value and verify its hash. An old monolithic value converts once on explicit
range access without changing logical timestamps. Consumers enforce operator and
session permissions and redact before publishing diagnostic documents; this generic
component defines no application authority.

Full-value and range reads use consistent SQLite snapshots across the record header
and its chunks. Historical conversion obtains the write reservation before reading
the old value, so it cannot overwrite a value committed by another store instance
while waiting for that reservation. Audit writes occur after read-only snapshots
complete; they do not upgrade a stale read snapshot into a write transaction.
Full reads copy encrypted headers/chunks in the snapshot and decrypt after releasing
it. Normal range inspection also releases its snapshot before decryption/audit.
Writes prepare encryption before reserving the writer and verify the record identity
inside the atomic publication. Historical conversion retains its exclusive recheck.

SQLite BUSY/LOCKED failures retry the complete rolled-back operation at most five
times, with a one-second provider wait per attempt and cancellable 50/100/200/400 ms
delays. Other errors are not retried. Exhausted contention raises
`KeyVaultAccessException` with `IsTransient`; callers retain durable work and suspend
until storage recovers. Successful audited operations never return without their
audit write. No retry redispatches a model request or settles financial usage.

```sh
dotnet build src/GnOuGo.KeyVault.Core
dotnet test tests/GnOuGo.KeyVault.Core.Tests
dotnet pack src/GnOuGo.KeyVault.Core -c Release
```
