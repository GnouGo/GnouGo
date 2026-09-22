# Workflow Designer: incompatible saved history

Implementation `5bceafd59e3f68f24af763c252958b3720087f65` fixes a historical schema-8 chat session containing `calculate.resultType`, which previously aborted the entire Designer history list.

Server-local history entries now preserve identity, origin, date and traces while marking unreadable sessions **Unavailable**. Strict executable deserialization still rejects obsolete fields. Incompatible entries cannot be resumed, approved or executed, and startup recovery skips them without changing encrypted content or accounting. Storage outages and cancellation still propagate.

Verification passed: **389 Agent.Server, 170 Planning, 857 Flow and 76 Flow.Integrations tests (1,492 total)**, including 15 new regressions. Three regression cases reproduced the failure before correction. The solution build and trimmed Server publish were warning-free; the published encrypted EF Core/SQLite smoke now covers incompatible history isolation as well as existing persistence checks.

Read-only browser verification against the existing encrypted history showed 18 entries. The original incompatible chat entry remained identifiable, its details showed the explanatory message, and its trace panel opened. Reload and polling succeeded, while a healthy historical FinalReview session remained readable. No approval controls appeared on the unavailable entry. Canonical content hashes and counts matched before and after for ten planning session, request, receipt and budget collections, including schema-7 evidence and uncertain reservations.

No new model dispatch, workflow approval, external execution or scenario replay occurred. The earlier six-run [planning campaign](planning-semantic-grounding-live-2026-09-21.md) and its unmet overall acceptance criterion remain unchanged. [Sanitized verification metadata](evidence/planning-history-compatibility-2026-09-22.json) records the implementation revision, checks and hashes; private payloads remain encrypted.
