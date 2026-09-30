# Constrained binding regression

This sanitized, reduced fixture comes from retained planning session
`d37d71ef4d7f478dae13743acb97bb3e` on revision `1cb406c`.

`plan.json` retains the affected consumer bindings and transform declarations.
Unrelated tasks and observations are replaced by fixture values to isolate the
four original type errors. This is a deterministic regression, not an unmodified
model replay or a claim of successful live generation.

`catalog.json` retains the three relevant MCP input/output schemas, including
enums, optional fields and the JSON-text parameter. Transport identifiers and
versions are anonymized. Built-in runtime contracts remain included. No live
service is invoked by these tests.

The tests explicitly add producer string domains and wrap the cleanup object in
the semantic JSON encoder. They verify runtime rejection and independent expected
arguments, including escaped text, permission refusal and cleanup. Context tests
add synthetic distractors; renamed-operation tests preserve the contracts.

Original encrypted records, requests, reservations and historical evidence are
unchanged. See the [diagnosis and validation report](../../../../docs/planning-constrained-bindings-2026-09-26.md).
