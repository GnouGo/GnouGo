# Frozen effect-admission diagnostic rerun

Production remained at `107cbcdd1789cbefc6b67d53f3ab570499854245`. Campaign `schema5-effect-grounded-admission-diagnostics-rerun-1` started LOCAL exactly once. LOCAL stopped on a verified effect-mapping validation failure. MIXED and Stage 1 were not run. No production patch, provider retry or archive continuation occurred.

The unchanged model was `gpt-5.5-2026-04-24`, with all-low reasoning, 12,000 input / 9,600 dispatch target / 8,192 normal output, existing bounded singleton 16,384 escalation and 16 durable reservations per case. Canonical declarations and attachments were supplied fixture preconditions, not fresh live declaration evidence.

## First meaningful blocker

Interpretation completed with zero unresolved runtime scopes. Effect grounding returned a schema-valid response, then deterministic validation stopped with `INTENT_OPERATION_UNRESOLVED`: **Effect dataflow crosses an unestablished workflow boundary.**

The first rejected decision was `effect_runtime_c8c088269589e8d624438f87`, at `/operations/@runtime_c8c088269589e8d624438f87`. Its `realizes` assignment selected both invocation anchors:

| Candidate effect ID | Workflow scope |
|---|---|
| `operation_4c8824d6e7ebf40dffd8999c` | `main` |
| `operation_159ac911a814a7956ff0a69c` | `ob_287f0fb931791b81` |

Both inputs (`record`, `threshold`) and the output (`classifiedResult`) belong to `main`. The second selected effect therefore crossed an unestablished scope. These are staged candidate IDs, not admitted canonical operations. The returned rules and descriptive-local assignments used `governs` and selected the same two candidates; none of these attachments was committed.

Preservation evidence was excluded from operation admission by canonical output coverage. The supplied declarations retained required `record`, optional `threshold` with omission default `100`, and required `classifiedResult`. The canonical operation set and effect proof were not committed, so the LOCAL acceptance gate did not pass.

## Usage and decision attribution

| Class | Verified calls | Distinct exposed decisions | Input tokens | Output tokens | Reasoning tokens included in output |
|---|---:|---:|---:|---:|---:|
| Interpretation | 14 | 22 | 28,175 | 19,858 | 18,278 |
| Effect grounding | 1 | 3 | 3,846 | 1,210 | 512 |
| Occurrence identity | 0 | 0 | 0 | 0 | 0 |
| Total | 15 | 25 | 32,021 | 21,068 | 18,790 |

All 15 reservations have verified receipts; there were zero unverifiable dispatches. Final-answer usage derived as output minus reasoning is 2,278 tokens. One output-limit page split into two children; one separate singleton escalation completed. There were zero semantic corrections or repair reservations. The largest request was 4,423 estimated / 3,846 actual input tokens. No occurrence-identity assignment or governing attachment was committed. These results do not establish admission convergence or net call savings.

The effect request's fingerprints are:

- Request: `0d8a8ee9679d8e3fb605f9b5fec7bc653d8ab860224ce371ba1773225c800f30`
- Context: `119caad3b10297ef1c2038974a4b526464edbf59f641bb839463aca2c2f3906d`
- Schema: `2598b30d80c3d323f51dc88396c9e04f5b67690c78d8eb4fc23fd6606b795c46`
- Receipt: `69005584780058cd598bdc57de5eb454ba2d668da43d26040699a9a0744977d4`

## Verification

The harness build was warning-free; 26 focused harness tests and both synthetic reference selfchecks passed before dispatch. All 22 production DLLs matched the previous manifest; the newly frozen harness and production binaries remained unchanged throughout the run. Model configuration, catalog, policy, source and declaration-fixture fingerprints matched. Fresh session-owned decision IDs changed MIXED's preflight packing from 11 to 12 pages, with the same 22 source decisions and unchanged budgets; MIXED was never dispatched.

Read-only audit validated the retained completed response against its original schema with zero findings and reproduced the same deterministic stop from the saved completed page. It made zero provider calls and reused the persisted candidate without an additional receipt replay. The checkpoint, budget and archived accounting remained unchanged. Historical failed/unverifiable requests were untouched.

The [redacted report](planner-effect-admission-rerun-report.json) contains the frozen manifest, exact staged reference assignments, request/receipt fingerprints, split/escalation lineage, usage and offline audit. Private requests and receipts remain in encrypted storage.
