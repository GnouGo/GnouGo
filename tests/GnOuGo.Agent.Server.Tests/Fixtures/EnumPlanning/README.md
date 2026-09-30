# Enum and discovery failure

`retained-enums.json` is a sanitized read-only capture of session
`7e5bbed190234d6e99d2d0e31afd403e`: seven original responses, issued request schemas,
sixteen revision summaries, exact discovery/catalog receipts and accounting.
Workspace/user paths and concrete repository URLs are redacted. Encrypted originals
remain unchanged. The retained outcome has five input errors and three producer-enum
diagnostics, seven calls and two repairs counted before the first TaskPlan could be repaired.

`synthetic-corrected.json` is explicitly synthetic regeneration, not a recorded
response or an authorized repair of the stopped session. It declares producer enums,
uses the existing command-execution capability for project checks, and uses JSON
assembly plus the declared filesystem deletion operation for cleanup. The execution
permission remains `deny`; absent/refused command observations cannot establish success.

Replay tests preserve historical schemas, identities and accounting. Independent
assertions check project-check intent, repository/output bindings, comment fields and
cleanup; substituting an unrelated allowed command fails the oracle. No model call,
external review or real Copilot command execution is performed.
