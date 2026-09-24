# Designer test with low reasoning

> Historical evidence: the Agent.Server review-publication subsystem described below was removed on 2026-09-24. Its draft, publication and replay checks describe the earlier implementation. Current workflows use configured MCP capabilities and generic approval mechanisms; see [migration details](github-mcp-workflow-execution.md).

## Result

The single authorized test returned a complete intent and exercised focused
repairs, but **did not reach FinalReview**. It stopped after one interpretation,
one capability-choice call and both permitted repairs. No output truncation,
input-limit failure, timeout or uncertain dispatch occurred.

The provider reported 70 reasoning tokens in the interpretation, compared with
8,192 and no intent in the preceding medium-reasoning attempt. This is one
successful interpretation at low reasoning, not evidence of reliable end-to-end
planning. Capability recovery, invalid references and a semantically unsuitable
cleanup choice still prevented a complete workflow.

No production code, global provider setting or budget was changed. No workflow
approval, repository execution or GitHub publication was performed.

## Setup and identity

The branch was clean and synchronized at `348026c`. Its only changes since
`a3f56bc` are documentation, so this test reused the same warning-free published
Server binaries. The production correction remains `e29ee32`.

The actual Blazor Designer was operated through Chromium. A fresh, empty planning
index and isolated Agent, Files, telemetry and browser storage prevented
resumption of previous sessions. Real integration contracts and the configured
provider were loaded through the existing public KeyVault boundaries.

The original 680-byte prompt was submitted unchanged through the Designer form,
once. Its SHA-256 remains
`8f9c138913d4fd8e136760df3b7e20fe4cf7623f02fa9fa4c46220bcaae1cf85`.

| Setting | Value |
| --- | --- |
| Session | `61f2b83cd708439e93085896b31174d8` |
| Name | `designer-low-20260921-348026c` |
| Submission | 2026-09-21 18:23:00 UTC |
| Last completion receipt | 2026-09-21 18:24:12 UTC |
| Provider / model | OpenAi / `gpt-5.5-2026-04-24` |
| Protocol | Chat Completions |
| Reasoning | Low, for this isolated host only |
| Maximum calls / repairs | 8 / 2 |
| Input / output limits | 12,000 / 8,192 tokens |
| Final state | Stopped, revision 10 |
| Active time | 46.36 seconds; approximately 72 seconds to the last receipt |

Stored session settings confirm the limits above. The initial catalog remains
129 entries with 24 exposed cards. Initial prompt and response schema sizes are
unchanged at 18,447 and 10,458 UTF-8 bytes.

## Calls and repairs

All four HTTP attempts returned 200. Each logical call has one attempt and one
completed durable receipt; no pending request remains.

| Call | Phase | Input tokens | Output tokens | Reasoning tokens | Span duration | Estimated USD |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | Interpretation | 7,388 | 3,328 | 70 | 25.29 s | 0.136780 |
| 2 | Capability choice | 1,162 | 230 | 199 | 4.14 s | 0.012710 |
| 3 | Repair 1 | 2,138 | 163 | 116 | 3.68 s | 0.015580 |
| 4 | Repair 2 | 3,883 | 556 | 322 | 8.62 s | 0.036095 |
| Total | | 14,571 | 4,277 | 707 | | 0.201165 |

Total reported usage is 18,848 tokens. The stored EUR estimate is **0.175078**.
Reasoning tokens are included in completion usage, not an additional charge.
No private reasoning content was inspected. Previous sessions' charges remain
unchanged.

The generated proposal reads PR metadata, files, diff and check status; clones
the repository; installs dependencies; runs parallel lint, unit and integration
checks; transforms the review findings; proposes publication and cleanup. These
are proposed operations only. Several have no resolved executable capability.

The two repairs were focused and within the existing input/output limits:

| Repair | Target | Shape | Prompt bytes | Schema bytes | Estimated input tokens | Deferred targets |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| 1 | `/operations/4/arguments/2/value` | Value | 7,820 | 2,026 | 3,538 | 7 |
| 2 | `/operations/5` | Operation | 9,314 | 6,921 | 5,668 | 6 |

These input estimates use the existing planner estimator and include the response
schema. They are distinct from reported provider usage. Each request issued one
target, preserving the three-target maximum and independent blocking diagnostics.

Repair 1 replaced an invalid `read_pr.head.ref` reference in the clone argument
with the literal `refs/pull/510/head`. The external producer still has no declared
output contract; no schema was inferred from examples or overwritten. The edit
removed that field-reference error without a workflow-wide replacement. It was
not executed or validated against alternate runtime inputs.

Repair 2 replaced the unresolved dependency-install operation. Its advisory
cards were `copilot_interactive_one_shot`, `git_clone`, `copilot_one_shot` and
`get_label`. `cmd_run` is in the full allowed catalog but was absent from both
the initial shortlist and this repair advice. The model returned the method name
`copilot_interactive_one_shot` as its capability reference rather than the issued
`cap_…` identifier. Exact reference and executable binding validation rejected it.

## Failure classification

- **Capability retrieval/recovery miss:** the allowed command executor was not
  exposed for the dependency-install repair. `review_evaluate` and `review_publish`
  were absent from initial context; publication remained unresolved and no
  `review_evaluate` invocation was generated.
- **Invalid intent repair:** the second repair used an unissued capability
  reference, producing `CAPABILITY_UNKNOWN` and `CAPABILITY_BINDING_INVALID`.
  Subsequent independent capability, binding and output-contract errors remained.
- **Semantic mismatch in capability choice:** the cleanup operation was bound to
  `copilot_workspace_read_file`. The four issued options were that file reader,
  `list_commits`, `get_file_contents` and `copilot_workspace_create_file`; none
  performs the requested cleanup. The current choice prompt requires one issued
  selection. Structural eligibility and a valid choice ID therefore did not
  establish a suitable business operation. The incorrect proposal was never
  approved or executed.

There is no demonstrated provider failure in this run. The remaining missing
schemas follow unresolved producers and structured-output requirements; this
test does not establish a separate deterministic type-inference defect. No new
production fix or architectural change was attempted.

The final blocking diagnostic counts are:

| Diagnostic | Count |
| --- | ---: |
| `HOLE_UNRESOLVED` | 14 |
| `STEP_TYPE_DENIED` | 5 |
| `CAPABILITY_UNKNOWN` | 1 |
| `BINDING_UNAVAILABLE` | 13 |
| `SCHEMA_INVALID` | 9 |
| `CAPABILITY_BINDING_INVALID` | 1 |
| `STRUCTURED_OUTPUT_INVALID` | 1 |
| `OUTPUT_REFERENCE_INVALID` | 13 |

The repair budget was exhausted with these errors still blocking. No executable
YAML or scenarios were produced. The run does not validate the complete PR-review
business behavior, cleanup, or publication safety through execution.

## Trace inspection and preservation

Designer details and all four model-call request/response documents were inspected
through the existing Pipeline / LLM calls panel. Repair target/deferred counts,
reported usage, HTTP attempts and completed journal entries were visible.

| Trace | Recorded model calls |
| --- | --- |
| `1eab99c77d032ca3d28e6c5a70aa09e1` | Interpretation and capability choice |
| `54d9f7f4056a8b08005b80fb6725de31` | First repair |
| `5dfb226eba8eab14a5ec186ea1402a19` | Second repair |
| `4fce3c010647f7bf0f371393d0db062c` | Final stop; no additional model call |

The new session's 20 encrypted session/request/receipt/budget records have digest
`0117c8ccaaa006c298f5fbfdcccf91bbd88aa1264dd79c6d3bedb17692a44a41`.
Public KeyVault reads verify that this digest and the earlier sessions' digests
remain unchanged after inspection:

| Earlier session | Digest |
| --- | --- |
| `aa6971c5bf8140b5a2b752ebef93e1a2` | `c50e079f1d259d9653eaea51fc281949084eae6c4eedd655bf675e64d854d778` |
| `3fbbd97bd758412f9027e38caeeb3e17` | `da27aacf507108cce0ff609750dad3752c020dbd26c546418295ac0cc7e7ac66` |
| `7321f76a7c574effb00ff21173b1a2cf` | `cce0a0197fd4d34566309019c083cbd585128cfcf28193ff3f9e40ec224775bd` |

The isolated Server and Chromium processes were stopped after inspection, keeping
their persistence directories and the encrypted evidence. No reviewed repository
was cloned, changed or pushed. No workflow was approved, saved or executed; no
GitHub review, metadata change, merge or deployment occurred.

This configuration-only test reuses the
[documented offline validation](planning-designer-recovery-2026-09-21.md).
No source changes required another component test run. Documentation links and
diff checks were verified. No synthetic campaign, additional live session or
silent limit increase followed this result.
