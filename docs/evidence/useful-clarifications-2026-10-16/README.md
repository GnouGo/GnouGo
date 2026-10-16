# Useful clarification inside bounded correction

Baseline: `caa86f096c0d7e7f014ac93b38f8549518ea2d54`, PR #117. All work here is offline. No retained session, approved workflow, financial record or execution invocation was advanced.

## Retained execution failure

Session `fd471af6e0594916a202d6e3216b21e0` reached review from one structured-output response. The original response satisfies its original issued schema. The request offered discovery, but contained **zero inspected operations**. Its nine normal tasks and cleanup were transformations, including tasks described as cloning, running commands and publishing a review. The first extraction received caller inputs but claimed remote metadata and success/availability/completeness.

[Read-only replay](retained-replay.json) uses the public encrypted record/run APIs in [replay.cs](replay.cs). Both matching journals contain two committed mapping responses and zero MCP invocations. The first saved script fails with an unsupported operation; the second fails provenance at `$/success` (`ungrounded_scalar`). Cleanup was skipped. No clone, command or publication happened. The evidence establishes workflow failure; it does not establish an Agent.Server process crash. Original request, receipt and journal-head hashes are recorded and unchanged after reading.

The [sanitized proposal](retained-proposal.json) retains its task structure and contracts. URLs are replaced with a fixture URL; requirements and interfaces are not rewritten. [Saved generation settings](retained-generation.json) retain the original 24,000-unit admission ceiling. Remote capability availability and permissions remain unproved: no discovery or provider request was made to inspect the live host.

## Interaction correction

Fresh Agent.Server sessions capture private `clarification_policy: 1`. Existing pending requests and historical states keep their original behavior. A correction can return a question through its existing response envelope. The question suspends the same diagnosed scope and baseline. Answers checkpoint before dispatch and preserve accepted requirements, interfaces, diagnostics, correction progress and cumulative accounting. No answer adds an attempt or another structural fallback. Accepted-intent changes require a separate explicit revision.

Auto mode pauses for unresolved business choices; a recommendation alone does not select them. Recorded compatible choices remain reusable. Exact repeated answered questions stop; no semantic paraphrase classifier is introduced. Questions whose answer cannot be used within the remaining allowance stop with `CLARIFICATION_CONTINUATION_UNAVAILABLE`.

Normal host tests caught two continuation defects beyond the planner-only cases: a suspended structural literal correction lost access to its original authorized location, and a continuation wake-up could race command/owner teardown. The correction retains the diagnosed literal scope, publishes wake-ups after releasing command ownership, and serializes local lease acquisition with commands. Existing cross-process leases and receipt behavior remain authoritative.

| Deterministic scenario | Calls | Corrective attempts | Result |
|---|---:|---:|---|
| Initial question → answer → proposal | 2 | 0 | Unapproved review |
| Technical scoped patch, no question | 2 | 1 | Unapproved review |
| First corrective request asks a question → answer → patch | 3 | 2 | Unapproved review |
| Failed patch → question → answer → regeneration, configured allowance 3 | 4 | 3 | Unapproved review |
| Failed patch → question, unchanged default allowance 2 | 3 | 2 | Explicit stop; no unusable question shown |

Complete scripted request estimates range from **3,770 to 7,448**, below the unchanged admission ceiling. [Per-scenario evidence](interaction-failed_then_question-3.json) retains actions, candidate/diagnostic fingerprints and outcomes. Each synthetic response reports 15 tokens; these are accounting fixtures, not measured provider usage. Answer contents are excluded from trace events.

Designer/browser checks cover explicit recommended/custom/text-only submission, keyboard/mobile interaction, reload, saved-response consumption and separate approval. Component checks preserve text during identical-question polling, clear changed batches and reject duplicate submission. Host checks cover stale commands, tenant isolation, answer checkpoint/restart, unchanged committed receipts and no extra calls from polling.

## Corrected disposable review composition

The [corrected TaskPlan](corrected-proposal.json) and [actual compiled v15 YAML](corrected-v15.yaml) come from one explicit structural revision with deterministic adapters. All four accepted requirements, all three caller inputs and all six public output contracts are unchanged; the [explicit composition diff](composition-diff.json) records their comparison and every task. The draft is not approved or applied to the saved agent.

| Retained composition | Disposable correction |
|---|---|
| Extraction claims URL parsing plus remote metadata/status | Bounded local URI parsing, then a separate metadata operation |
| Transform claims clone/install/checks | One real local Git clone and four real shell checks in that clone |
| Transform claims review/publication | Deterministic review adapter, exact occurrence-preserving comments, explicit decision and publication operations |
| Transform claims report writing | Actual local JSON report file; returned path comes from its writer |
| Extraction claims cleanup | Conditional finalizer removes the acquired clone after known completion |
| Interpretation regenerates status/summary | Direct producer bindings supply summary, decision, state, review URL, report path and cleanup status |

The local fixture supplies its own discovered contracts; it is not proof that the retained host has equivalent remote tools. Review and remote publication use deterministic adapters; no real repository or external review is accessed. Runtime inference for typed copying/status/assembly is **zero**. The corrected YAML is **24,025 UTF-8 bytes**, and its complete revision request estimate is **16,715 / 24,000**. See [composition measurements](composition-metrics.json).

Seven execution variants independently inspect the local clone, command evidence, exact multiline/Unicode feedback, report path/content and cleanup: approval, findings, failed checks, incomplete review evidence, denied clone, confirmed publication rejection and uncertain publication. Failed checks produce REFUSE, not fabricated success. Failures prevent submission/report writing as appropriate. Unknown completion leaves the clone in place and prevents replay. Restart adds zero external operations in every variant.

## Validation

Owning gates passed: **1,473 Planning checks**, **37 host/UI/browser interaction checks**, and **seven corrected local review executions**, all with `-warnaserror`. Frontend production build passed. The final serial solution, package/published recovery checks and candidate CI are recorded separately in `validation.json` when complete; these owning results alone are not full acceptance.

Initial failures were retained in validation logs: continuation routing/ownership, prompt-size admission and fixture setup. Causes were corrected without raising ceilings/timeouts or weakening assertions. Historical request presentation stays on its captured behavior; fresh interaction guidance and schema annotations are versioned with the new policy. Compiler v15, runtime provenance, executor contracts, handwritten YAML/JS and repair envelopes are unchanged.

Future model compliance, recognition of every business ambiguity and remote/live business acceptance remain unproved. Clarification guidance cannot prove that an arbitrary question is useful; the tested guarantees are bounded continuation, preserved authority and exact deterministic execution. No paid call, live review, historical replay or financial change is included. PR #117 remains draft.
