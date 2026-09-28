# Retained artifact-binding failure

`retained-artifacts.json` preserves the five responses, original issued schemas,
exact catalog, discovery receipts, accounting and 13 revisions of session
`a796183e55be4328a0b27103b6d75ac4`. Local user paths and repository URLs are
sanitized. Original encrypted records were read through public KeyVault APIs and
verified unchanged. No inference or external execution occurred.

The original final proposal selects `repositoryRoot` for two consumers requiring
the artifact declared at `projectRootRelative`. It remains invalid. Preflight now
locates those errors at each task's `inputs/projectRoot`, before graph emission.

`synthetic-corrected.json` is explicitly synthetic: only those two field selections
change. It exercises bounded repair through the real planner and reaches review
with six calls and two repairs in deterministic replay. The opaque review response
is forwarded unchanged. This is not evidence of a live review execution.

Tests preserve original request identities/schemas and accounting on recovery,
reject unrelated objective edits atomically, verify approval recompilation, and
never approve or execute the external workflow.
