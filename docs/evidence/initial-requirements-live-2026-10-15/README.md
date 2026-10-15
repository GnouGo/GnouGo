# Initial requirements: authorized live planning result

**Planning stopped at revision 5. No approvable artifact or YAML was produced;
Amazon navigation, product extraction, workbook writing and live oracles did not
run.** PR #117 remains draft. This is retained failure evidence, not E2E acceptance.

The user authorized one fresh planning session with “go for a live test”, then
“go”. The frozen candidate was `83e60ff4a5f07d99eaef78b400aeddad703645e1`, run
`initialrequirements20261015a-amazon-1`, with `compact-bindings-v15`, automatic
correction enabled and at most ten products. The existing eight-call,
two-correction and cumulative time/token allowances were unchanged. Execution
still required a separate revision/hash-bound artifact approval; none was sought
or supplied for this failed proposal.

## Gates before dispatch

- All **32 applicable candidate CI checks passed**, four were skipped. The five
  workflow runs and every underlying job step were inspected, including the
  nonblocking host-test job: no failed step was concealed by a successful job.
- The unchanged production correction passed the previous full serial solution:
  **5,727 passed, 13 skipped**, plus package, Native AOT, published recovery and
  skill validation. The harness opt-in passed **117 focused tests** and a build
  with zero warnings/errors.
- All 710 frozen binary files, selected sources, configuration and prompt hashes
  matched. The independent `observed-products-v2` oracle is unchanged from the
  preceding live.
- Actual local Browser navigation, XLSX writing/independent readback and cleanup
  passed with zero inference. These are readiness results, not Amazon outcomes.
- Configured pricing/currency readiness passed. The initial campaign upper bound
  was **€149.555501 / €200**, with all unknown reservations retained.

The `freeze.json` status records preparation before CI finished. The later
`verification-before-planning.json`, complete compressed CI evidence and
`planning-start.json` establish that dispatch waited for green gates.

## What the real calls did

| Call | Purpose | Result |
| --- | --- | --- |
| 1 | Discovery | Requested Browser/Document contracts; no plan, requirements legitimately null. |
| 2 | Initial proposal | Declared requirements and interfaces, but used nonboolean values in two `requires` guards. |
| 3 | Automatic targeted revision | Proposed Boolean guards in exactly the two issued paths; assertion-preservation validation rejected the structural changes. |
| 4 | Automatic structural regeneration | Changed assertions, independent declarations and intermediate contracts; preservation validation rejected it. The two-correction allowance was exhausted. |

All four committed responses pass their **original issued strict JSON schemas**
in zero-inference readback. JSON shape compliance did not make the bindings
semantically valid. Original request/receipt hashes, separate prompt/schema byte
counts, usage and transport statuses are in [request-summary.json](request-summary.json).

The initial guards were:

```text
/tasks/accept_cookies/requires
    field(output(cookie_decision), "reference")     // nullable string

/tasks/extract_candidates_by_page/requires
    output(accept_cookies_if_present, "search_snapshot") // nullable object
```

Both triggered `TASK_CONDITION_TYPE`: conditions require a Boolean business value.
The actual diagnostics transmitted to the correction calls are retained in
[transmitted-diagnostics.json](transmitted-diagnostics.json).

The targeted patch changed those values into predicates. The current
`ValidateAutomaticCorrection`/`SameGuard` rule permits certain reference repairs
and additional checks, but rejects replacement of an existing nonboolean binding
with a predicate. Both changes were therefore rejected as
`REVISION_SCOPE_CHANGED`; no partial patch was published.

Regeneration then modified more of the plan. Some independent-declaration
findings came from changed objective text, which the current declaration
comparison includes. Other differences changed guards and collection contracts;
these cannot all be dismissed as prose changes. See
[regeneration-diff.json](regeneration-diff.json) and the complete rejected
proposal. This live exposes a limitation of correction routing/preservation; it
does not justify disabling preservation or accepting the regenerated proposal.

Requirements were present in the first full proposal. Consequently, this live
**did not exercise the new missing-initial-requirements regeneration path**. Its
deterministic evidence remains in the adjacent initial-requirements-correction
report.

## Independent composition review

The first proposal contains 17 tasks, including six interpretation transforms and
two extraction transforms. Besides the compiler errors, review found:

- An unrequested consent decision/action and a whole observation snapshot passed
  to that decision.
- `prepare_visits` asks interpretation to flatten, copy identities/records and
  construct an offered/original lookup. No deterministic strict lookup implements
  that promise.
- `assemble_rows`, the null-row branch and final status use inference for typed
  assembly/copying.
- The independent extraction's outer array is capped at ten source pages,
  conflating the product-selection bound with observation coverage.

These are proposal findings, not observed runtime failures. No business action
was executed to test them. Fixing only the two guard types would not establish
business acceptance.

## Accounting and preservation

- Four logical planning calls, five physical attempts, two corrections, two
  discovery reads; approximately **435.6 seconds** of planning.
- Known planning cost: **€0.4832573115105983364636436812**.
- Call 2 has transport history **[unknown, 200]** under the existing bounded
  transport policy. Its committed response is retained, and the unknown attempt
  keeps **€1.3085055004024684733029246043** reserved. There was no manual resend.
- Campaign upper bound afterward:
  **€151.34726402347829433064332741 / €200**; about **€48.65** remains.
- Zero execution model calls and zero workflow MCP invocation events. No visits,
  workbook or live cleanup verdict exists for this run.
- No stopped historical invocation was resumed, archived or closed. No allowance
  was reset. Readback through public encrypted-storage APIs confirmed the retained
  run was unchanged. The fresh stopped run will not be restarted automatically.

## Evidence and next boundary

- [Initial proposal](initial-proposal.json.gz), [targeted patch](targeted-patch.json)
  and [rejected regeneration](rejected-regeneration.json.gz).
- [Accepted requirements](requirements.json), [correction progression](correction-progress.json),
  [final diagnostics](diagnostics.json), [accounting/result](result.json) and
  [separate outcomes](outcomes.json).
- [Candidate CI](candidate-ci-complete.json.gz), [binary hashes](binary-hashes.json.gz),
  [local readiness](frozen-readiness.log) and [planning log](planning.log).

Raw issued prompts and transport bodies remain in encrypted storage/private
inspection evidence. No observation payloads or credentials are published here.

The next useful work is an offline reproduction of nonboolean-guard revision and
the independent-declaration comparison, together with composition regressions.
Another paid retry alone would not prove a fix. No production change or additional
paid session is part of this report. A future executable artifact needs fresh,
explicit review and approval.
