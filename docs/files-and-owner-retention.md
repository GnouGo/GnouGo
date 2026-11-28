# Files, workflow images and owner retention

Agent.Server, Flow.Server and the Flow CLI capture a **two-day operational retention window** for new designer sessions, nested planning sessions and execution runs. The temporary HTTP upload grant defaults to 12 hours. A workflow that claims an upload before its temporary deadline can still read it after that deadline. [Architecture decision](adr-files-and-owner-retention.md)

## Configuration

```json
{
  "Retention": { "OperationalDays": 2 },
  "Files": { "DefaultTtlHours": 12, "PurgeIntervalSeconds": 60 }
}
```

Set `Retention__OperationalDays` to a positive whole number to override the default for newly created owners. Each owner captures its policy version and actual resume and terminal day counts. Polling, list queries, unrelated bookkeeping and a configuration change leave the captured deadline intact. The runtime uses UTC throughout. [Transitions](../src/GnOuGo.Flow.Core/Runtime/OwnerRetention.cs)

| Owner state | Deadline and admission |
| --- | --- |
| Active | The last meaningful checkpoint starts the crash-resume window. The running process holds an OS lease. A collector skips that owner. A new process must acquire the lease before the crash-resume deadline. |
| Waiting for human input, review or approved handoff | Entry into a particular wait starts its resume window. Repeating the same wait keeps its original deadline. A new answer or handoff requires the current owner and revision. |
| Terminal | The first committed terminal transition starts the terminal window. Repeated saves preserve it. |
| Needs reconciliation | The operational window still closes. Exact pending evidence, unresolved reservations and the execution prohibition survive collection. |

For example, a chat uploads a PNG at 08:00 UTC on April 1. Its run claims it at 09:00 and pauses for input at 10:00. The HTTP grant expires at 20:00 on April 1. The run can resume until 10:00 on April 3. At exactly 10:00 ordinary resumption is denied. A saved version that independently holds the same file can still use its authenticated bytes.

## Ownership and storage

| Component | Authoritative data | Lookup and lifetime |
| --- | --- | --- |
| Files.Core | AES-GCM ciphertext in `objects-v2`. Local KeyVault protects each object's random key, digest, length, original filename, media type and grants. | Upload IDs are opaque. Workflow-produced images use an idempotent owner-and-digest identity. Each grant includes the tenant, owner kind and owner ID. |
| Files.Server | EF SQLite indexes temporary IDs, sizes and UTC deadlines. | The HTTP API uploads, lists and downloads only temporarily available files. Tenant comes from the authenticated tenant claim or local host configuration. A request header or query parameter can narrow that selection. |
| Workflow vision and chat | The host supplies selected upload IDs. Chat validates actual image bytes and supplies the five-field `images` binding. Execution claims the selected files before publishing its root. | Consumers continue to bind `sha256`, `mimeType`, `width`, `height` and `size`. The image reader authenticates the file in the current tenant and run. PNG/JPEG inputs retain the eight-image, 4 MiB and dimension limits. |
| Agent designer | A stable encrypted head selects the committed immutable planning payload. SQLite projects its revision, status and retention. | The owner lease serializes planning work. The write lease serializes publication, expiry and index rebuild. |
| Nested workflow planner | Protected creation checkpoint, authored definition and current session with the parent run and invocation identity. | The planner takes the same process lease to recover or collect a nested session. Nested sessions have a distinct Files owner kind. |
| Execution | An encrypted split journal selects the header, current invocations, event chunks and shared snapshot blocks. | The root and block references are authoritative. The SQLite index is rebuildable. The block collector checks reachability under the same locks used for publication. |
| Saved versions and retained outputs | Explicit grants can keep an existing file independently of its producer. A saved version binds its catalog name and exact YAML digest. | Transfers select literal file IDs or digests already held by the verified source owner. A fresh run acquires its own grant. Host code can give a kept output its own deliverable grant. |

Files encrypts before writing staging bytes. Each object has a fresh key and each authenticated frame binds its sequence position. The manifest binds tenant, object ID, exact length and SHA-256. A durable write intent precedes the first ciphertext. Verified frames support streaming downloads. The workflow validates the complete image digest and dimensions before provider dispatch. The default upload limit is 64 MiB. Missing keys and authentication failures block reads. [Files.Core](../src/GnOuGo.Files.Core/README.md) · [HTTP storage](../src/GnOuGo.Files.Server/README.md)

## Expiry and collection

1. On admission, read the authoritative owner and check its captured deadline. At equality the resume window is closed. Ordinary reads hide an expired owner. Command paths check ownership and revision again under the write lock.
2. The collector obtains the owner's long lease and write lock where applicable, checks the committed state again, and publishes an encrypted tombstone before unlinking dependencies. The tombstone retains the original identity. Recreating the same owner cannot authorize another dispatch.
3. Preserve independent accounting and model/effect records. A run fence records its definition, usage and external receipts. Owners with unresolved work or unpriced consumption stay pinned. Pinned payloads and their dependencies remain available to restricted evidence handling. Existing campaign closures and financial journals retain their independent authority.
4. Release the applicable Files grants. Delete eligible operational payloads, blocks and projections. An interrupted pass can repeat the remaining operations. An idempotent upload intent releases provisional run grants if the first root never committed. Files unlinks bytes only when the upload grant has expired and the final owner has released its hold.
5. Release the session's immutable capability snapshots, cancellation markers and expired chat origin. Designer and nested sessions have separate snapshot partitions even when their IDs match.
6. Live split journals may reuse blocks much older than their head. For an idle live journal the orphan pass marks authenticated references from the header, every invocation and every event chunk before collecting unreachable blocks older than one day.

Agent.Server and Flow.Server run owner collection at startup and then hourly. The Files server checks expired uploads and abandoned writes at startup and at its configured purge interval. A read or command checks the deadline even between collector passes. Restore the journal, KeyVault, Files directory, grants and tombstones from a consistent workspace backup.

### Operator preview

Run the installed Agent.Server with `--inspect-retention` in the same configured workspace. Supply an optional UTC timestamp to simulate the cutoff, for example `GnOuGo.Agent.Server --inspect-retention 2026-12-01T08:00:00Z`. The local command starts no server or background collector. It streams JSON lines from bounded pages for the configured tenant's committed execution, designer and nested planning roots. Each entry reports its ID, expected revision, decision, deadline and Files ciphertext bytes. Shared holds and an unexpired temporary grant protect bytes when releasing one owner. Per-owner shared totals overlap. SQLite pages, KeyVault journals, previously purged roots and incomplete writes are outside these byte estimates. A missing or unreadable object yields an unknown reclaimable size. The production collector always checks the live clock and leases.

## Breaking storage change

Policy version 2 starts this storage generation. Historical plaintext uploads, legacy image records, EF-only designer sessions, monolithic runs and previous-generation operational owners have no migration or ordinary execution fallback. Collection retires them under their ownership locks. An unresolved financial or external-effect record keeps its evidence and replay fence. The offline accounting inspector can lock and read pinned evidence without obtaining a resumable session or a writable execution lease. Stop old writers before opening the workspace with this release. A kept user-authored workflow is a separate artifact.

## Validation

```powershell
dotnet test tests/GnOuGo.Files.Server.Tests -c Release -warnaserror
dotnet test tests/GnOuGo.Flow.Persistence.Tests -c Release -warnaserror
dotnet test tests/GnOuGo.Flow.Integrations.Tests -c Release -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -c Release -warnaserror
dotnet test GnOuGo.Agent.sln -c Release -m:1 -warnaserror
dotnet pack src/GnOuGo.Files.Core -c Release -warnaserror
dotnet pack src/GnOuGo.Flow.Persistence -c Release -warnaserror
```

The two-process Server retention smoke writes a designer wait and a split execution to a fresh scratch directory. Its second process uses a different default, checks both sides of the captured deadline and rebuilds projections. The Files, run and planning tests exercise cold read, sharing, tamper detection, missing manifests, logical expiry, lease contention, collection and recreation fences.

## Separate qualification

**Performance experiment.** Compare the pre-cutover encrypted KeyVault image path with encrypted Files on the same machine and storage, under equivalent owner checks, authenticated encryption and integrity checks. Use the checked-in 800×400 panels, native slide render, a large valid PNG/JPEG near the 4 MiB limit and eight distinct valid images. Record each input's hash, dimensions and byte count. Exercise upload/first attach, same-run repeat, cold/warm read, restart, shared holds and final release. Warm up each implementation, then run at least 30 observations per workload with identical concurrency, separate fresh stores and a repeated cold-process series. Report median/p95 latency, allocated bytes/operation, peak process memory, file/DB/WAL bytes at the same checkpoint, counts/bytes read and written, and bytes remaining after collection. Use .NET allocation counters or a profiler and OS I/O counters, with the same SQLite checkpoint policy. Record provider-bound image digests and request shape using a captured deterministic adapter. No storage or LLM-token saving has been measured here.

**Business regressions.** First replay deterministic fixtures and independently inspect the artifacts. PowerPoint must pass the packaged three-OS DeckCraft round trip, exact image handoff and native chart workbook checks. For later live qualification, use a fresh normally planned, approved eight-slide workflow and verify after reopen, visual review, public bytes and cleanup. Preserve the closed attempt. Amazon must reopen the retained capture by owner, preserve bounded projection, all 72 observation pages and 1,543 records, and verify the actual workbook/receipt before claiming business success. For SlimFaas, retain the existing independently verified review and test approved checkout scope, exact effects, report, and cleanup on the normal path. Only a separately scheduled live execution can prove a new live result. [Scenario evidence](evidence/pr120-deckcraft-vision-20261128/README.md)
