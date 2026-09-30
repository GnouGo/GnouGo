# Artifact provenance and opaque exports

Session `a796183e55be4328a0b27103b6d75ac4` stopped at `81f7d11` after five
model calls and one repair, without a pending request. Its retained proposal
selected `repositoryRoot` for two inputs requiring the `workspace.directory`
artifact declared at `projectRootRelative`. Those bindings remain invalid.

The minimal reproduction also exposed two compiler defects: checked projections
lost the declared origin, and forwarding an unconstrained MCP response exported
an empty schema instead of the existing opaque representation.

The correction is generic:

- Present existing artifact metadata as business ports and kinds.
- Check semantic lineage before lowering, with exact consumer repair locations.
- Preserve origin through checked projections and identity validation; require
  every possible selection to retain origin. Availability, failure continuation
  and cycle checks remain independent.
- Forward opaque results as opaque, including group and iteration boundaries.
  Typed field access still needs an authoritative contract.

No TaskPlan rewrite, provider rule, new phase, public DTO, runtime language or
storage change is involved. Planning remains format 10; execution journals remain
schema 9. Existing saved workflows and approvals are not rewritten.

## Retained reproduction

[The sanitized fixture](../tests/GnOuGo.Agent.Server.Tests/Fixtures/ArtifactPlanning/README.md)
preserves all five responses, original request schemas, exact contracts, counters
and 13 session revisions. The encrypted source records remain unchanged.

Replaying those responses through `HybridWorkflowPlanner` now produces two
`TASK_ARTIFACT_BINDING` diagnostics at the precise `projectRoot` inputs, before
emitting graph nodes. A separately marked synthetic repair changes only the two
field selections and reaches review at six calls and two repairs. It does not
approve or execute the workflow. Unrelated objective changes remain rejected;
recovery, receipts, budgets and approval recompilation are covered.

## Deterministic checks

The focused tests cover renamed operations, nested/escaped field mappings,
scope exports and captures, reusable groups, bounded iteration, opaque forwarding,
wrong or invented origins, alternate projection paths, unsafe continuations and
cycles. Omission of an optional artifact remains distinct from supplying null.

The recorded review presentation stays within the existing discovery budget:
maximum complete generation estimate 21,578 tokens under 24,000. A synthetic
mandatory repair remains larger (26,878 tokens); admission still stops safely.
No token limits were raised and no live planning improvement is claimed.

The local solution run passed 3,486 .NET tests across 33 projects with no warnings
or failures; five opt-in Copilot tests and the temporary Windows probe were
skipped on macOS. It includes 554 planner and 475 Agent.Server tests. The 318
Python checks, five Flow package builds and macOS ARM64 planning Native AOT smoke
passed. Cmd's native Windows suite subsequently passed all 93 tests at `cabb4d9`.
The final solution/matrix results, including published Windows Cmd protocol
validation, are recorded on PR #113; platform skips are not native coverage.

After deployment, create a new planning session and explicitly approve its
workflow. The existing real Copilot execution limitation remains unresolved.
