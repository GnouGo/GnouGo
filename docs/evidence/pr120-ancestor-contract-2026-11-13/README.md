# Truthful nullable ancestor-context contract

## First demonstrated runtime boundary

A controlled prefix using the retained actual source stops before classification: the published Browser MCP schema declares each `ancestorAttributes` dictionary value as an object, while the producer's nullable CLR contract and description explicitly emit null for a missing ancestor. The actual 1,439-record source produces **253 instance findings**. The compiler preserves the original contract correctly. A missing ancestor remains different from a matched ancestor with an empty string. No product identity, classification or permission follows from either value.

## Generic producer correction

Publish `object` or `null` for that existing dictionary value in the owning Browser's record-schema projection, before copying it to both observation and complete-snapshot paths. Keep the context object's required value/tag/role/index and their types. Runtime payloads, record order, URL spellings, host/routes, source lifetime, business verifier v14, scopes, provider/transport/timeouts/retries/output allocations and money remain unchanged. The schema fingerprint changes, so fresh discovery and review are required. Historical catalogues and stopped sessions are immutable.

## Regression and actual retained data

[Reproduction](reproduction.json): a sanitized producer/descriptor regression is red for the serialized missing-ancestor null (one failure; empty/matching contexts pass). All **220 Browser tests pass** Release `-warnaserror`, zero skips, after the correction. The Browser unit project retains its component independence.

[Full instance validation](actual-schema-validation.json) against freshly published metadata accepts the entire retained source with zero findings. Six malformed context values remain rejected: primitive strings/numbers, null context.value, missing tag, numeric role and string index. This integration uses the existing Flow validator in a separate controlled harness; no production dependency is added to Browser.

## Scoped batching correction

The [read-only real-artifact correction](controlled-batch.json) uses nine explicitly issued typed slots after the [collection-binding correction](../pr120-explicit-loop-binding-2026-11-13/README.md). The original baseline stays unchanged; continuation compilation has zero findings. Classification consumes one complete source snapshot and returns a vector inside the existing object-root response grammar. Its native export/flatten binding preserves order, duplicates and nullable values. Reconciliation receives the complete source once plus original status/acquisition/navigation/snapshot metadata, while full destination observations remain at their producers. Copied rules use the authoritative captured source facts. No iteration limit or permission is raised.

Three **controlled prefix** executions exercise zero, one and three returned decisions, with all 1,439 original records forwarded once and one simulated model call per case. In-memory MCP responses replace source/close operations; paid model calls and actual navigation are zero. These check wiring/cardinality, not semantic selection. Fresh contracts are discovered through the existing lazy catalogue API before compilation. No old session is resumed, mutated or approved.

[Resource evidence](controlled-resources.json): 9.705 seconds, peak 446,080 KiB, captured v15 compilation and adaptive mapping. An earlier fixture mistakenly used the default compiler overload and was terminated; it is excluded from qualification and does not establish a production performance defect. Production profiles and timeouts did not change.

Full planning acceptance, business execution, actual Excel and independent oracle qualification remain pending: Amazon **0/2**, Code **0/2**. Shared solution checks and CI are required on the new source before a new full attempt. Complete raw payloads remain private, with their owned encrypted evidence and recorded hashes; the committed producer regression uses sanitized generic context.
