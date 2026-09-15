# Direction-neutral declaration validation

Implemented from `9ffdd1d` on `feat/deterministic-planner-v2`. Validation is offline;
no campaign was started or resumed and no live provider request was dispatched.
Schema-5 storage, reasoning, token ceilings, confirmation logic and bounded output
partition/escalation behavior are unchanged.

## Retained failure and proof boundary

Session `4d45ca3dc8b5442b9742b98fc83554ed` stopped at revision 32. The clause
“category has exactly the values rejected, high, standard.” was interpreted as
`business_input`. That preliminary direction excluded the correct output target,
while the old declaration response schema allowed `distinct` with unspecified
presence. The receipt was schema-valid but canonical admission failed.

Before editing, strict replay from revision 27 reproduced
`DECLARATION_GROUNDING_UNRESOLVED` at `/declarations/@ob_60232a89ef5317dd`, using
one retained receipt and zero provider dispatches. The saved session, budget and
journal were verified unchanged.

Current strict replay rejects the old source proof with
`INTENT_SOURCE_AUTHORITY_UNPROVEN`, before consuming a receipt. This is the expected
proof-version boundary: historical directional classifications do not acquire neutral
candidate authority. Explicit revision/reassessment must establish current proof.
The archived session and accounting remain unchanged. No corrected synthetic answer
was substituted into its history.

## Representation changes

Interpretation now emits `declaration_candidate`. Within the existing
`intent_declarations` phase, root decisions establish `distinct_input` or
`distinct_output` using exact name, declaration and presence references. New roots
cannot have unspecified presence or declaration defaults. Pending attachments remain
in durable decision pages until canonical targets are available.

Attachment pages bind their requests to the validated root-set fingerprint. Aliases
and modifiers select canonical IDs directly and inherit established direction,
scope and presence. Member constraints can therefore attach to an output regardless
of the earlier evidence fragment. Omission defaults remain restricted to optional
inputs and exact omission-literal evidence; output defaults remain impossible in
the response domain. Partial roots never authorize behavior ports.

| Retained failure class | Result |
| --- | --- |
| Wrong preliminary direction blocks an output modifier | Removed: preliminary candidates carry no authoritative direction. |
| Distinct public port with unspecified presence | Rejected by the response schema. |
| Overlapping threshold fragments and descriptive extra outputs (`3f11a9cf`) | Updated synthetic fixtures retain two inputs and one output through canonical aliases/modifiers and exact behavior acceptance. |
| Runtime fallback or conditional `false` becomes an output default (`b8c6201e`) | Existing omission/fallback separation remains enforced against canonical target contracts. |
| Recursive truncation and singleton output exhaustion | Existing partition/escalation mechanisms remain unchanged and covered by published smokes. |
| Provider unavailability or missing executable threshold dependency | Outside this change; no claim of resolution. |

Source references prove ownership and structural consistency. Semantic adjudication
and existing review remain necessary to assess the meaning of natural-language
clauses; this change does not claim that arbitrary semantic misclassification is
impossible.

## Offline results

- **3,224 passing solution tests**, one optional live-provider test skipped.
- Included: **786 planner**, **855 Flow.Core**, **67 Flow.Integrations** and
  **385 Agent.Server** tests.
- Solution and harness builds pass without warnings. An initial parallel solution
  build emitted transient MSB3026 file-copy contention; the final serialized build
  and complete no-build test run are clean.
- Six classifier/batch reference cases and all 18 frozen CodeReview fixture cases
  pass selfcheck with zero model or business transport calls.
- Core, Planning, Integrations and Agent.Shared packages build without warnings.
- The `osx-arm64` Native AOT planning/encrypted persistence smoke passes, including
  restart between root decisions and attachments, no duplicate root dispatch,
  unchanged repair accounting and replay of the final proof.
- The trimmed `osx-arm64` Agent.Server EF persistence smoke passes. Both publishes
  are warning-free under their existing documented exceptions; no suppressions were
  added. Frontend code and benchmark scenario/catalog/policy logic are unchanged.

Regressions cover the captured member clause, renamed subjects, explicit presence
proof, cross-clause omission evidence, canonical direction/scope safety, forbidden
output defaults, exact names, overlapping declarations, independent subjects in one
clause, unresolved/stale/foreign evidence, baseline contracts, behavior acceptance,
encrypted serialization, staged restart and replay-safe accounting.

The corrected classifier assertions use labelled synthetic semantic assignments.
They demonstrate deterministic fixture convergence through the existing declaration
boundary. They do not constitute a new live Stage-1 result.

Machine-readable counts, replay evidence, unchanged fixture hashes and published
binary hashes are in [the offline report](planner-neutral-declarations-offline.json).
