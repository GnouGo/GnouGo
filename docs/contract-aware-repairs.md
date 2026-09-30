# Contract-aware scoped repairs

This deterministic follow-up starts from `5cae578` on `feat/flow-hybrid-planning-v9`
under issue #112 and draft PR #113. It changes repair validation and diagnostics,
not TaskPlan, the compiler pipeline, execution, approval, or storage formats.

## Retained failure

Session `9500c4f642834ecfb3fb1a3988e416fb` used six discovery calls, one proposal,
and one repair. Its first plan supplied nullable values to optional non-nullable
arguments, named ancestor tasks as local dependencies, and supplied an undeclared
permission-mode literal. Those semantic rejections remain correct.

The repair request estimated 25,567 input tokens, above the saved 24,000 allowance.
The user's increase to 44,000 admitted the eighth call. That response removed the
two invalid optional arguments, but also removed producer fields, changed unrelated
objectives, and selected a broader permission mode. It was rejected atomically;
the eight-call budget was then exhausted with one repair consumed.

All eight responses, issued prompts/schemas, discovery/contracts, twenty revision
summaries, request identities and retained accounting are preserved in
[`retained-optional-repair.json`](../tests/GnOuGo.Agent.Server.Tests/Fixtures/ContractAwareRepair/retained-optional-repair.json).
Local paths and concrete repository URLs are sanitized. The original encrypted
records are unchanged. Recording SHA-256:
`4c85fa74cb34611652dddb349d9bb2182957aa567468aabf87403e6dd3547e5b`.

## Generic correction

- Repair validation now consults the selected authoritative operation contract.
  A diagnosed optional input may be explicitly removed. Required inputs,
  ambiguous mappings/names, unrelated bindings and ordering remain protected.
  Producer metadata cannot override requiredness in the underlying schema.
- Full request validation still runs after compilation, including conditional
  requiredness. Omission permission does not establish that an entire request is
  valid. An optional argument that is supplied must satisfy its contract: null is
  not omission.
- Nullable types and nullable string enums expose only the incompatible producer
  `nullable` and/or `enum` slots, including nested arrays and objects. A correct
  non-null enum domain is not opened merely because its nullability is wrong.
  No values, defaults, types or permissions are changed automatically.
- Diagnostics distinguish omission from null, preserve declared enum/default
  information, and identify invalid dependencies and their actual semantic scope.
  There are no server, tool, command or permission-value special cases in Flow.

## Deterministic evidence

Four new regression cases failed on the parent: optional binding removal, the two
nullable-domain variants, and the omission diagnostic. Their original logs remain
under the ignored `artifacts/contract-aware-repair-2026-09-28/` directory.

Historical replay through `HybridWorkflowPlanner` still rejects the broad rewrite
and stops at eight calls. Its immutable baseline, discovery receipts, issued repair
scope and accounting survive rejection. The obsolete blanket rejection of the
input list disappears; the independent unauthorized changes remain rejected.

[`synthetic-minimal-repair.json`](../tests/GnOuGo.Agent.Server.Tests/Fixtures/ContractAwareRepair/synthetic-minimal-repair.json)
is explicitly synthetic, not a model success recording. It changes only the two
optional consumer bindings, invalid ancestor dependencies and invalid permission
literal. Producer fields, objectives and unrelated values remain intact. It reaches
final review through the real planner using the original eighth request/schema and
reservation, including recovery and approval recompilation checks.

That fixture uses the producer-declared `deny` mode. Static acceptance does **not**
prove permission to execute project checks, successful Copilot command execution,
or a completed external review. No workflow is approved or executed by this replay.

Focused tests also cover required and unrelated input removal, ordering, permission
changes outside scope, ambiguous names/contracts, conditional requirements, nested
constraints, renamed capabilities, unchanged defaults, the last available call,
serialization and approval invalidation. Existing business oracles are unchanged.
The exact full-suite, package, AOT and branch CI results are recorded in
[draft PR #113](https://github.com/GnouGo/GnouGo/pull/113).

## Limits and operational recovery

Repair-context compaction is excluded. The recorded 25,567-token request remains a
documented admission limitation; no token allowance or call ceiling was raised by
this change. New diagnostics may also contribute to newly issued request sizes.
Pending requests retain their original prompts, schemas, identities and budgets.

Deploy the updated planner, start a new planning session, then explicitly approve
its reviewed workflow. Never replenish or silently resume the exhausted session.
The existing Windows Cmd CI timeout and real Copilot sandbox limitation remain
separate. No paid inference, live benchmark, external repository workflow or merge
is included in this pass.
