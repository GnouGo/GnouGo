# Focused and recoverable capability discovery

New implementation selections use discovery protocol 2. The model selects an issued
focus identifying the immediate objective or an exact retained prerequisite, and
provides a short justification for each explored source. The host derives the query
from that focus. Source descriptions, original obligations, inspected contracts and
limitations remain visible. A justification is a model proposal, not proof that the
source is required. There is no scenario-specific allowlist or additional judge call.

Selected MCP sources paginate automatically. The complete canonical catalog is
captured once and published through the existing encrypted KeyVault planning journal
before its reference is checkpointed. References bind tenant, session, action, source,
configuration fingerprint and payload hash. Large payloads use existing authenticated
storage chunks. Runtime recreation reads that same snapshot instead of contacting
the provider again. Transition-local metadata reuse checks storage versions and
in-memory integrity; no progress cache, database service or synchronization worker
is added. Runtimes without persistent journal adapters retain the snapshot in their
existing checkpoint data.

Transport failures, metadata timeouts and catalog changes remain explicit source
outcomes. Ordinary exploratory failures stop that source's pagination and warn while
other sources continue. Failed sources cannot be automatically selected again for
the same focus; operator refresh is explicit. An incomplete inspection proves no
capability absence. Binding still requires exact resolved contracts, and the existing
compiler and business judges remain mandatory. Ownership violations, corrupt snapshots,
invalid authored requests and cyclic cursors do not become warnings. Diagnostic
threshold authority and phase allocations are unchanged.

## Local operator recovery

After restarting Agent.Server/Desktop on the updated binaries, open the protected
local operator window. Eligible blocked Designer sessions offer:

- **Continue with available sources**: retain the failed source as unavailable and
  return to discovery using preserved work and evidence.
- **Refresh source metadata**: start from its first page with a new snapshot; retain
  old pages, cursors and failures as history.

Both actions perform metadata recovery only and enter `discovery_recovery`. Reload
preserves this waiting state. **Resume planning after discovery recovery** is a
separate action; subsequent inference uses the original captured budgets and settings.
The user session `1a54de6fa5934c7aa9ddc3cd31da99cb` is not automatically modified.
If the process stops during metadata recovery, **Continue retained metadata recovery**
finishes the same action and pinned snapshot. Its failure and resolution remain
retained until completion; selecting a different resolution cannot replace that action.

The existing command endpoint accepts `kind: recover_discovery` with
`discoveryRecovery: { failureId, resolution }`, where `resolution` is
`continue_available` or `refresh_source`. `kind: resume_discovery` resumes the prepared
session. Both require authenticated local-operator access, antiforgery and the exact
current revision. Ordinary Strict planning remains available without this authority.

Eligibility verifies the lease, cancellation state, session/artifact state, existing
owner retention guards and the session's complete completed-call index plus exact
request/receipt record keys. Reads are bounded and session-scoped. Missing historical
evidence is unavailable, not reconstructed. Existing policies, accepted checkpoints,
models, allocations, tariffs, charges and reservations are preserved. Permanently
retained runs and unresolved model dispatches cannot use metadata recovery to bypass
their barriers. Workflow-owned sessions retain their owner's command path; ordinary
runtime factories do not acquire recovery authority.

## Inspection and validation

Engine timeline events identify the metadata source, focus, snapshot and safe outcome.
Protected inspection retains selection justifications and exact cursors with redaction.
OpenTelemetry adds events to the actual parent activity using correlation identities
and outcome categories; business content is not added to span attributes. The shared
LLM inspector remains the sole call-detail view. Metadata reads create no LLM exchange,
usage measurement or acceptance result.

Historical protocol-1 actions and already-issued requests keep their exact wire
contracts. Explicit recovery prepares a new metadata action without rewriting old
requests, receipts or exception details. No data migration or automatic session resume
is required.

The existing actual-Server metadata readiness command also accepts
`--snapshot-pagination`. It captures a real configured source, persists the snapshot,
recreates the reader every four pages and verifies exact coverage without inference.
Use fresh run identities and the existing campaign configuration; it neither raises
funding nor dispatches a model request. Paid continuation still requires a fresh
configuration, funding and replay preflight within the unchanged authorized ceiling.
