# GnOuGo.Flow.Persistence

Independently publishable schema-9 execution storage for `IWorkflowRunStore`.
Authoritative journal payloads use the public encrypted KeyVault record API. EF Core and SQLite contain only tenant-owned, rebuildable indexes.

`EncryptedWorkflowRunStore.CreateWorkspace()` resolves paths through `GnOuGoWorkspace`. All hosts sharing a record store must use the same owner-lock directory. Owner locks are held for the lifetime of execution and released by the operating system after process exit. Revision checks protect resume, cancellation and reconciliation commands; cancellation can be requested while a run is owned.

Completed receipts are reused. An interrupted external invocation without a receipt requires reconciliation. Cleanup waits for external work to become quiescent. A managed agent's internal session is not automatically restored after a process crash.

```sh
dotnet build src/GnOuGo.Flow.Persistence/GnOuGo.Flow.Persistence.csproj
dotnet test tests/GnOuGo.Flow.Persistence.Tests/GnOuGo.Flow.Persistence.Tests.csproj
dotnet pack src/GnOuGo.Flow.Persistence/GnOuGo.Flow.Persistence.csproj -c Release
```

Schema-8 data is never migrated or deleted automatically. Regenerate and approve workflows before creating schema-9 runs.

`CreateWorkspace(databasePath, indexPath, logger, ownerPath)` accepts explicit host paths; omitted paths use workspace helpers. Run `scripts/verify-flow-v9-published.py` against published CLI/server binaries to check encryption, native EF queries, index rebuilding, restart and tenant/revision boundaries.
