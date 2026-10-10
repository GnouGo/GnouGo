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

New runs use private storage layout 1: a small authoritative root references immutable encrypted invocation, event and payload blocks in `flow-execution-blocks-v1`. Large JSON subtrees are shared within one tenant/run; arrays use fixed chunks so growing collections can reuse their prefixes. Hashing preserves numeric JSON tokens, nulls, property names and ordering. Blocks are written before the root is atomically replaced. Unreferenced blocks left by an interrupted write are not receipts and are not automatically deleted.

The lease's optional `SaveAsync(changedInvocationIds, ct)` overload checkpoints run metadata, appended events and affected invocations. Its default implementation calls ordinary `SaveAsync`, keeping custom stores compatible. Ordinary saves still support arbitrary edits. Owner saves merge durable commands under the existing write lock; cancellation polling reads only the root. Index failures after a committed root do not invalidate completion.

Container sizing uses a bounded traversal that stops at the inline threshold; forced containers skip sizing. Arrays encode chunks directly without cloning them, and invocation metadata is serialized separately from snapshots and receipt payloads. Only completed block definitions are hashed and serialized, never an expanded invocation to decide its storage layout.

Optional `IWorkflowRunLease.CaptureSnapshotAsync` and `RestoreSnapshotAsync` hooks default to clone-based behavior. The encrypted lease freezes snapshots as verified immutable block references and keeps active/recovered snapshots deferred. Restoration compares exact values, reuses unchanged mutable execution subtrees and replaces changed/removed values; it does not share mutable nodes with captured evidence. Public `DataBefore`/`DataAfter` access materializes detached editable JSON. Full saves honor such edits; incremental saves persist only the declared changed invocations. Caches belong to a tenant/run/lease, never a global observation cache. Capture writes alone do not establish dispatch or completion: the authoritative checkpoint must still commit synchronously.

Public inspection still reconstructs the complete schema-9 `WorkflowRun`; it can be large. This optimization reduces physical duplication and checkpoint I/O, not the logical inspection payload. Missing/corrupt/cross-owner blocks fail closed. Existing monolithic schema-9 records keep their original layout when read or resumed; no automatic migration, rewriting of saved workflows, or uncertain-invocation replay occurs. Upgrade all hosts that may own new runs: older binaries reject the new private layout. Public planning/artifact formats and mapping cache semantics are unchanged.

Schema-8 data is never migrated or deleted automatically. Regenerate and approve workflows before creating schema-9 runs.

`CreateWorkspace(databasePath, indexPath, logger, ownerPath)` accepts explicit host paths; omitted paths use workspace helpers. Run `scripts/verify-flow-v9-published.py` against published CLI/server binaries to check encryption, native EF queries, index rebuilding, restart and tenant/revision boundaries.
