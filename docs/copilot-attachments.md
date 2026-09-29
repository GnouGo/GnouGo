# Typed Copilot attachments

Baseline: `806ef0f79031a5a1621c50ade92e3005a286ea7e`, issue #112 / draft PR #113.

Run `748041de1513fb18397856c74fed801b` supplied an encoded object containing
`pullRequestUrl` and `reviewInstructions` to `attachmentsJson`. The published
`string | null` contract accepted that value. Deserialization as a list of file/blob
attachments failed before this invocation reached Copilot. Sanitized arguments,
generated workflow bindings and all three original tool schemas are retained in
[the regression fixtures](../tests/GnOuGo.Agent.Server.Tests/Fixtures/CopilotAttachments).
The encrypted original journal payload was read through KeyVault and remained
unchanged (SHA-256 `4c6760de1ec45a31fb0be094401e5868822200534e711b7011bc743d894dd82d`).

The producer now publishes `attachments`: an optional array of closed, discriminated
`file`/`blob` objects. Source-generated input contracts map to the existing Core
attachment type. Validation rejects malformed arguments before session creation or
send, with safe `INVALID_INPUT` locations. The old encoded-string parser is removed;
supplying `attachmentsJson`, even null, is an explicit error. Business context belongs
in `prompt`. See [the contract and migration instructions](../src/GnOuGo.GithubCopilot.Mcp/README.md#typed-attachments-breaking-contract).

The producer normalizes the generated JSON Schema's generic reference-type
nullability for array items and required string members to match runtime validation.
No Flow production logic changes: existing exact-contract discovery, semantic type
checks, scoped repair and approval hashes consume the new contract generically.
TaskPlan, planning format 10, execution journal 9 and permission policy are unchanged.

## Deterministic evidence

- The three new discovery assertions failed against the old contract before the fix.
- MCP tests call the actual transport with a mocked SDK. All three tools reject
  malformed shapes, null items, unknown fields/kinds, invalid base64 and legacy
  arguments with zero session creations or sends. Valid files/blobs, null/omitted/
  empty arrays and out-of-order discriminators preserve mapping and lifecycle.
- Core boundary tests retain file confinement, existence and read checks, blob
  defaults, and refusal of descriptive allowlist phrases as command permissions.
- Host tests reproduce the retained parsing exception, reject the context object
  before graph emission, and run a separately identified synthetic proposal through
  `HybridWorkflowPlanner`, recovery, compilation and execution with mocked effects.
  The fixture puts context in `prompt`, uses `deny`, and summarizes data only. It does
  not claim to perform the original dependency installation or test execution.
  Renamed capabilities behave identically; unrelated repairs and stale approval
  hashes remain rejected.
- The published-binary smoke checks actual discovery and malformed argument refusal
  on all three tools without creating a Copilot task. Full deterministic solution,
  package and Native AOT checks accompany delivery; exact revisions and CI outcomes
  are reported in PR #113.

The saved run separately requested installation/tests under a read-only allowlist
containing descriptive phrases. Fixing attachments does not authorize those actions.
Use a declared operation and the existing explicit permission mechanism; do not
automatically select `approve_all`. Sandbox restrictions, including dependency
downloads, remain unchanged. Real Copilot command completion remains unverified.

Deploy the updated MCP, refresh discovery, regenerate or explicitly revise the
workflow and approve its new artifact. Do not rewrite/restart the retained execution.
No paid inference, live benchmark or external workflow was used for this correction.
