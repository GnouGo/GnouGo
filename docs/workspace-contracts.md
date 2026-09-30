# Shared workspaces and producer-owned execution contracts

The retained `Youhoucoedeur` failure combines three independent mistakes. Clone
created `workflows/github-pr-auto-review/project`, while the approved agent scope
named `workflows/github-pr-copilot-review/SmartGuide`. The plan also requested a
business `tenantId`, and cleanup supplied Boolean parameters unsupported by Cmd.

## Changes

- TaskPlan workspace references can follow available `value` constants, literal
  object assembly and field selection. The compiler emits a literal workspace
  before approval and retains its dependencies. Runtime inputs, operations,
  transforms, choices, loop-derived values and ambiguous/cyclic references cannot
  define the scope. Other agent scope fields remain literal. No path is reconciled
  automatically, and constant compilation does not prove filesystem existence.
- Runner validation distinguishes invalid identity, invalid contract and unavailable
  workspace without disclosing private paths. Existing filesystem and sandbox
  policy still applies.
- Copilot tenant-scoped tools require `_meta.gnougo.tenantId`; business arguments no
  longer accept `tenantId`. Bounded envelopes must match transport ownership.
  Internal tenant-bearing contracts and records are unchanged. Missing metadata
  fails closed; neither environment nor activity nor a default supplies ownership.
- Cmd uses `parameters` as a structured object, replacing `parametersJson`.
  Producer-generated schemas declare exact string parameters, requiredness,
  patterns and bounds for every effective configured command. Undeclared fields,
  Booleans and implicit aliases are rejected. Optional omission behavior, explicit
  custom parameter names, scripts, timeouts and path restrictions are preserved.
- Semantic preflight checks the complete effective request against its authoritative
  schema, including catalog-owned values and conditional parameter contracts.
  Graph/runtime validation remains independent. No command names enter Flow rules.

## Evidence and deterministic checks

Planning session `be4d7c74118e4966a89294ac8858e0fa` used six calls and one repair;
execution `5ed11c676dd1c7ae9bf69da1b9f40718` retained the successful clone and both
failures. Sanitized responses, original issued schemas, metadata and relevant
execution receipts are preserved in
[the recording](../tests/GnOuGo.Agent.Server.Tests/Fixtures/WorkspaceContracts/retained-workspace.json).
Original encrypted records and the existing clone remain untouched.

[Historical replay](../tests/GnOuGo.Agent.Server.Tests/RecordedWorkspaceContractTests.cs)
uses all six recorded responses without inference. A separately identified
corrected proposal explicitly removes the tenant binding and invented cleanup
parameters, and shares one workspace declaration. It reaches final review through
the real planner, survives serialization and approval recompilation, and rejects
changed approval material. This is an offline correction, not a new live result.

[Local lifecycle coverage](../tests/GnOuGo.Agent.Server.Tests/SharedWorkspaceExecutionTests.cs)
uses a temporary repository, real Git cloning and Cmd cleanup, with a mocked agent.
It independently checks exact paths, source/unrelated-directory preservation and
cleanup after success, partial creation, failure before creation, cancellation and
permission refusal. No external repository or real Copilot task is executed.

Focused tests also cover transport tenant isolation, missing/mismatched owners,
discovery schemas, custom/renamed commands, strict parameters, constant scope
rejection, conditional requests and unchanged repair boundaries. The published
smokes exercise source-generated serialization, Cmd protocol/scripts and Copilot
scope refusal with metadata, without inference.

## Deployment and recovery

Deploy the planner, Copilot MCP and Cmd MCP together, restart their processes and
refresh capability discovery. Standalone Copilot callers must send tenant metadata.
Replace encoded command arguments with structured `parameters`, using only declared
fields; default `delete_directory` needs only the string `path` and already deletes
recursively and idempotently.

Regenerate or explicitly revise the saved workflow and approve the new artifact.
Do not automatically remove workflow inputs, rewrite approvals, resume the failed
execution or clean its existing clone. Planning format remains 10; journals remain 9.

The real Copilot execution limitation remains: these deterministic tests do not
establish successful model-driven command execution or dependency downloads under
the sandbox. No permissions were broadened to bypass it.
