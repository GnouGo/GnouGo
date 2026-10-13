# Validation evidence

Production candidate `322bbb0c` changes only `PlanningPrompt` and fresh `PlanningSchemas` presentation. A following test-only correction replaces old shorthand assertions in two existing host fixtures; their compilation, operation-order, contract, lookup and workbook assertions remain intact. The final full-solution and candidate-CI verdicts are recorded in the delivery comment on [PR #117](https://github.com/GnouGo/GnouGo/pull/117). This document does not infer model compliance from deterministic responses.

## Completed offline proofs

| Check | Result / evidence |
|---|---|
| Planning owner suite, `-m:1 -warnaserror` | 1,327 passed, zero failures/skips; [log](validation/planning.log). Final wording also passed the ten-check focused group below and is covered by the final solution run. |
| Final emitted requests, retained corpus, description generation/revision, discovery | Ten focused checks passed; [log](validation/focused.log). |
| Strengthened extraction fixture | Four checks passed: extract an observed label from text, retain the full original block for deterministic writing, and exclude the full block/action URLs from interpretation. [Log](validation/label-example.log). |
| Authored YAML / assertion semantics | Explicit inference executes only on its allowed path; cleanup remains effective. Opposite mandatory assertions fail sequentially and succeed on separate branches. Included in Planning suite. |
| Private repairs / issued requests | Historical envelope fingerprints, scoped authority, pending-request recovery and committed receipts remain covered by the Planning suite. Fresh hints are absent from private repair schemas. |
| Real local Browser/Document v15 scenarios | Eight passed: complete, repeated, missing, empty, unoffered, incomplete, denied, redirected. [Log](validation/local-workbooks.log), [independent ZIP/XML inspection](local-workbooks.json), [actual complete workbook](local/workbook-complete.xlsx). |
| Updated host presentation assertions | Three passed, including the real `union_initial` Browser/Document scenario; [log](validation/host-presentation.log). No operational assertion was removed. |
| Historical actual v15 recompilation | Identical 56,720-byte r2 YAML, SHA-256 `f30e764f40f3d90e33e29a1fb23cb7c166ffe2da6b7304728162d9c493be44da`; [read-only proof](historical-v15-compatibility.json). No session advancement or execution. |
| Release packages | Core, Planning, Integrations, Copilot and Persistence all passed with `-warnaserror`; [commands/results](validation/publish-results.json). |
| Planning Native AOT | osx-arm64 publish with `-warnaserror` and published smoke both passed. |
| CLI/Server published encrypted recovery | Both supported AOT publishes and the existing recovery script passed. [Four existing EF experimental notices](validation/publish-notices.txt): query precompilation and Native AOT support, once per publish. No suppression was added. These supported targets differ from the warning-as-error Planning smoke. |
| Skill | Existing `quick_validate.py` passed with `/usr/bin/python3` and its installed PyYAML. |
| Protected paid evidence | All 13 record hashes unchanged: three requests, two receipts, three transport histories, one failure, one session and three configurations; no closure. [Before hashes](protected-before.json), [verification](protected-verification.json). |

The local fixtures compare complete captures with their original records, check candidates outside mapping examples, exact navigation arguments, repeated selections, actual workbook headers/cells/order, nullable positions and writer paths. Restart reuses committed results with no repeated inference, visits or writes. Negative controls reject invented descriptions and unoffered IDs. The unchanged live oracle deliberately rejects unsupported absence and zero-product claims in the `missing`/`empty` fixtures; independent fixture ground truth verifies their nullable cells/header-only assembly. A green test is not a declaration that those negative business outcomes passed the live oracle.

## Request cost and discovery

The [actual fresh request, deterministic response and compiler output](example/) retain the executable counterpart of the worked guide. `each` is read from the emitted example; the fixture implements the complete typed composition. It reaches review with one deterministic proposal and zero repairs. The mapping parses a compact label from observed text; original full blocks remain separate. Complete runtime execution uses one generic mapping and one compact decision; typed copying, original-ID reconnection and header/row/status assembly use zero inference. Twenty source pages are processed; the selection `[19,0,19]` preserves order and repetition. Incomplete acquisition stops with zero runtime model calls; unknown IDs stop before visits/writing. These are fake-adapter logical-call counts, not paid calls.

Same-context comparisons hold packed context and operation admission constant:

| Retained presentation | Conservative estimate before → after | Prompt UTF-8 bytes before → after | Schema UTF-8 bytes before → after |
|---|---:|---:|---:|
| v13 | 19,587 → 19,559 | 33,629 → 33,302 | 24,364 → 24,607 |
| v14 | 17,244 → 17,215 | 28,570 → 28,238 | 22,394 → 22,637 |
| v15 | 21,151 → 21,151 | 37,843 → 37,600 | 24,842 → 25,085 |

These are the existing prompt-plus-schema admission estimates, including the existing framing allowance, not measured provider token usage. [Machine-readable measurements](request-measurements.json).

The full retained-catalog continuation discovers all 74 identities, keeps all ten requested exact contracts visible, and reaches review with seven scripted calls and zero repairs. Its final estimate is **23,952 / 24,000**, exactly the unchanged baseline; its first six calls remain under the existing **21,600** optional-admission target. Metadata reads/resolutions and admitted operation counts match the [baseline run](validation/baseline-discovery.log). No required contract, authoritative constraint or admission margin was removed.

Intermediate drafts were rejected at 24,052 and 24,741 tokens. Full inline JSON examples duplicated the schema and displaced required context. The final request uses the worked sequence and one shared annotation; complete executable JSON/YAML is retained in `example/`. The first solution run also exposed three host assertions tied to superseded prompt wording. These failures were preserved and corrected at their presentation assertions; no compiler/runtime change, timeout increase or weakened business oracle was used.

## Final gates and limits of the claim

The final commands are `dotnet test GnOuGo.Agent.sln -m:1 -warnaserror`, the independent package/AOT/recovery recipe above, skill validation and applicable candidate CI. The delivery comment records their terminal status and any failures rather than treating isolated reruns as full acceptance.

Future first-generation compliance and live business acceptance remain unproved. No paid request, live execution, financial change or uncertain-request replay occurred. The exhausted v15 request remains open and protected. PR #117 remains draft.
