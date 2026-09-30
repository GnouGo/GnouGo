# Conditional cleanup compilation

Session `e85ae92338954cfe9059842bba70fe6d` stopped at `ff74167a` after five
model calls and one repair, with no pending request. Its unchanged final proposal
reproduced two `BINDING_UNAVAILABLE` compiler findings. A no-MCP TaskPlan reproduced
the same defects: a cleanup condition selected a producer field, then exported
the explicit result of either branch.

The compiler guarded the field projection but omitted the switch selector. The
success-availability proof also omitted switches; the independent YAML validator
did not recognize their union of object result envelopes.

## Correction

Cleanup guards cover input and selector reads, including captured inputs of
nested branch calls. Presence tests remain safe on absent producers, Boolean
short-circuit evaluation is preserved, and existing guards are retained. A false
condition selects the declared alternative; an unavailable required value skips
the conditional instead of reading missing data.

Graph and YAML validation recognize successful switch envelopes only with
supported branch/default coverage and proven producer availability. Payload
schemas, checked projections, error continuations, cycle checks and scope
boundaries remain authoritative. Envelope presence never proves successful
external cleanup. The change does not alter runtime execution, MCP contracts,
TaskPlan intent, repair permissions, budgets or storage formats.

## Evidence and validation

`RecordedConditionalCleanupTests` replays all five recorded responses through
`HybridWorkflowPlanner`, including the original bounded repair, and reaches final
review at five calls/one repair. Recovery reuses its schema and request identity;
the final TaskPlan and discovery receipts remain unchanged. Stale repair authority
and changed approval material are rejected.

The portable recording redacts local paths in producer descriptions and retains
public repository identifiers, schemas, responses and accounting. Its repair
fingerprint is recalculated only for the sanitized fixture and records the
original fingerprint explicitly. Encrypted originals are untouched.

Deterministic tests cover true/false branches, absent/invalid producers, failure,
cancellation, cleanup failure, primary/cleanup error separation, nested scopes,
groups, iterations, composed nullable outputs, captures and short-circuit guards.
Native AOT smoke exercises field-conditioned cleanup and both explicit outputs.
No paid inference, real Copilot task or external workflow is part of this evidence.

Deploy the updated planner/Core packages, then generate and explicitly approve a
new workflow. Do not resume or rewrite the retained stopped session automatically.
This correction establishes compilation and deterministic cleanup behavior, not
completion of the entire review workflow or resolution of Copilot sandbox and
dependency-download restrictions.
