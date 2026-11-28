# ADR — Files-backed artifacts and owner retention

**Status** Implemented on PR #120. **Decision date** 2026-11-28. **Scope** Local GnOuGo services, Desktop and Agent.Server.

## Context

Before this change, chat uploaded documents into Files. Workflow vision separately stored base64-encoded image bytes in encrypted local KeyVault records keyed by tenant, run and SHA-256. A planning session can accumulate hundreds of revisions and model records before an execution run exists. An execution checkpoint can still depend on a block written many revisions earlier. Applying the current temporary-upload timeout to either graph could destroy the only path to recovery. [Source audit and ownership map](files-and-owner-retention.md#current-ownership)

## Decision

1. Make Files responsible for image and document bytes, integrity and owner holds. Give each durable file an opaque ID. Use the five-field workflow image reference and existing provider request through an in-process Files implementation of `IWorkflowImageStore`. Bind tenant and owner at the trusted host boundary.
2. Keep planning state, execution journals, checkpoints, approvals and financial evidence encrypted with local GnOuGo KeyVault. Give each planning session and each execution run its own captured retention policy. A saved agent version or explicitly retained deliverable can hold a file independently.
3. Default the global operational duration to **two days**. Start the resume window at a durable business wait or the last meaningful checkpoint after loss of an owner. Start the terminal window at a proven terminal transition. An active owner remains protected. Operators can configure a longer window for newly created owners.
4. Logically expire under the existing owner lease and expected revision. Reclaim bytes later, after a recoverable dependency walk. Retain an encrypted tombstone and independent accounting/effect evidence. Keep uncertain reservations until explicit reconciliation. An operational expiry grants no model replay, spending or permanent campaign closure.

## Alternatives and consequences

| Alternative | Trade-off |
| --- | --- |
| Keep images in KeyVault and extend upload TTL | Smallest change. Chat and workflow retain separate copies and identity rules. Extending a temporary URL cannot express two independent owners. |
| Put journals and checkpoints in Files | A single store would have to implement revision fencing, encrypted recovery, partial reads and exact-receipt semantics already owned by planning and execution. |
| Expire each immutable block independently | Simple age scan. It can remove an old snapshot subtree still reachable from the current run. |
| Use a local HTTP Files client everywhere | Preserves the deployed endpoint. It couples local execution to request credentials, network availability and URL lifetime. A local component can use the same business service directly. |

The chosen design adds an owner-to-file relation, a recoverable cross-store publication step and an encrypted expiry fence. It avoids global byte deduplication in the first release. File access, authenticated encryption and crash recovery are release gates. The current multimodal layer continues to send the same pixels and incur the applicable model usage. [Contracts, breaking storage change and validation](files-and-owner-retention.md)
