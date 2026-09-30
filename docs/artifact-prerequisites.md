# Artifact prerequisites and bounded repair

Session `a27d6926e4b94e938eb9e44e48c6fad7` supplied a transform's string to an input requiring a `revision.comparison.files` artifact. No task declared that origin. Its six responses and fourteen session revisions are retained in the sanitized [recording](../tests/GnOuGo.Agent.Server.Tests/Fixtures/ArtifactPrerequisites/retained-prerequisites.json); the encrypted original records and their accounting remain unchanged. The historical outcome remains six calls, two repairs and no executable workflow.

## Generic correction

Capability summaries retain validated artifact metadata. Compact indexes present business-port names, literal nested field segments and produced/required kinds, including when full contracts are omitted. Wire pointers remain compiler-owned. Existing declared metadata alone determines origin; neither relevance ranking nor matching values establish provenance.

`ICapabilityCatalog.ListAsync` adds an optional `producedArtifactKind` argument. Exact filtering uses complete metadata before pagination. The filter is saved with pages and discovery requests; continuation cursors include its query and metadata snapshot. Existing null-filter cursor semantics are unchanged. Catalog implementations must implement this optional argument; a result that does not satisfy the requested filter is recorded as unavailable, not trusted.

Before a newly issued generation request, missing prerequisite kinds may trigger at most one automatic filtered page per already-inspected source on each advance, caching each source/kind pair. Already-visible, policy-eligible producers need no extra search. Results, including empty/unavailable pages, are retained; no automatic traversal of continuation pages occurs. Each advance snapshots its prerequisites, preventing recursive expansion of metadata cycles. Additional pages/refinements use the existing bounded discovery action. Pending requests retain their original prompts, schemas, identities and discovery effects.

Matching filtered candidates precede unrelated optional contracts, with relevance and stable identity ordering retained inside that priority. Exact contracts resolve before compilation; optional presentation still fits the 90% token target and mandatory context still observes the hard limit. Searches never select an operation, insert a task, establish permissions or claim exhaustive availability.

Semantic preflight distinguishes an incorrect binding from a required producer absent from the plan. If every declared operation is resolved and valid, and none produces the required kind, `TASK_ARTIFACT_PREREQUISITE_MISSING` stops planning before another repair. It retains independent findings and explains the need for explicit semantic revision. Unresolved, ambiguous or excluded contracts do not establish absence. Optional artifact arguments remain eligible for omission when their input contracts permit it. Existing producers retain binding/scope/provenance repair checks; task insertion remains forbidden.

## Deterministic evidence

- Original responses and schemas are replayed without inference or external execution. The first proposal now stops at **4 calls / 0 repairs**, instead of spending two binding repairs on a missing task. All six historical responses remain evidence, including the two patches.
- Sanitization changes signed baseline material. Tests verify that the historical patch fingerprint rejects the redacted baseline, then bind a separately labelled, in-memory replay authority to that sanitized copy. Original fingerprints, schemas, responses and encrypted receipts remain unchanged. Production recovery never rebases a patch.
- A separately labelled explicit revision adds the declared comparison producer and changes the consumer binding. The real planner reaches final review, validates generated YAML and verifies approval recompilation, including recovery. An isolated execution test uses the actual retained clone/comparison/consumer contracts and mocked effects to assert exact evidence forwarding. It performs no filesystem, network or model operation.
- Tests cover far-tail producers among 1,000 tools, multiple sources/candidates, filtered continuation, cache/recovery, version changes, exclusions, uncertainty, optional omission, fabricated origin and existing scope/cycle protections.

The recorded proposal request and the scripted explicit revision use the same conservative estimator:

| Measure | Recorded proposal | Explicit revised proposal |
| --- | ---: | ---: |
| Prompt bytes | 26,228 | 25,509 |
| Response-schema bytes | 37,656 | 38,324 |
| Estimated complete input tokens | 21,551 | 21,534 |
| Additional prerequisite metadata pages | 0 | 7 |
| Additional exact-contract resolutions | — | 4 |
| New scripted proposal calls | 1 | 1 |

All seven metadata pages are from previously inspected sources; there is no additional inference call. Omitting empty discovery fields preserves the existing request-budget regressions without dropping authoritative schemas. Local validation passed 3,533 deterministic .NET tests and 318 Python tests; seven Windows-only tests are covered by Windows CI and five opt-in live tests remain disabled. Package, Native AOT and branch CI results are recorded with the PR. The comparison is a scripted continuation from the recorded prefix, not a live reliability claim. Token allowances remain **24,000 input / 32,768 output**, with **eight calls** maximum.

## Recovery and limits

Deploy the updated planner and discovery implementation, then generate a new workflow or explicitly revise semantics within remaining session ceilings. Review and approve the resulting artifact. Existing stopped sessions, counters, receipts and approvals are never rewritten or automatically restarted.

Metadata origin does not prove that revisions exist at execution time, that all comparison pages were collected, or that a full review succeeds. The real Copilot sandbox execution limitation remains documented; mocked effects do not resolve it. The Windows Cmd CI issue was fixed earlier and is separate from this change. No paid inference, external workflow or merge is part of this pass.
