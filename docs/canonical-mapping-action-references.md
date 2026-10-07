# Canonical extraction items and observed action references

Independent extraction now exposes `item` (the current original collection element) and `context` (the other approved inputs). Generation examples and repair requests use those names. The historical `source.<input>` aliases and callback parameter names remain valid, sharing the same imported values and scalar-origin checks. Collection cache identity uses execution profile 2; issued requests and completed receipts keep their original identity and replay path. One generation and one repair remain shared by the entire invocation.

Browser observations now include opaque references and supported actions. Reference-based click declares `activate` or `follow`; fill, press and select derive their action from the operation. The producer resolves the exact captured element, checks current snapshot identity, unchanged element semantics and actionability, and applies existing navigation policy before interaction. A plain hyperlink cannot satisfy control activation. Expired, forged, undelivered, replaced or incompatible references fail before interaction. No LLM-authored selector runs on this path. Selector-based calls remain supported.

References are opaque strings: their wire format is not a producer field the planner needs to infer. Discovery publishes optional typed reference/intent parameters and their usage requirements; the producer enforces mutual exclusion and click intent at its boundary. The host validates actual identity, lifetime and supported actions. Validation of DOM semantics does not establish that a control has the intended business meaning.

Complete snapshots retain their captured element identities without issuing externally consumable page cursors. Reference resolution does not extend snapshot lifetime: any interaction, navigation, replacement or closure still expires the snapshot. Reacquire observations before subsequent reference-based actions.

## Reproductions and deterministic checks

The [previous failed live](evidence/conditional-interfaces-2026-10-07/amazon-result.md) is unchanged. Its [mapping script](evidence/conditional-interfaces-2026-10-07/mapping-source-replay/retained-script.json) uses the wrong named-input root. Its [execution evidence](evidence/conditional-interfaces-2026-10-07/amazon-execution.json) retains the observed footer link selected as consent. New regressions use arbitrary input names and a local information link/control pair; no site-specific production rules were added.

- Mapping tests cover canonical aliases, shared context, nulls, nesting, legacy scripts, unsampled repair, cache reuse, recovery and the unchanged cumulative allowances.
- Browser tests cover native/ARIA controls, link activation rejection, explicit following, disabled/read-only elements, exact identity, changed semantics, expired/cross-instance references, conflicting targets and undelivered pages.
- Host tests discover the actual MCP schemas, exercise reference actions through real transport, persist structured errors in encrypted receipts, and verify unchanged recovery and tenant isolation.
- Local Flow/Browser/Document fixtures use deterministic extraction/decision adapters, real page interactions and independently inspected XLSX values. They are deterministic integration evidence, not live-provider acceptance.
- The local observation fixtures close their listeners before awaiting their serving tasks, removing the demonstrated disposal race without suppressing errors or dropping assertions.

Run the affected checks with `dotnet test GnOuGo.Agent.sln -m:1 -warnaserror`. The validation host additionally skips optional remote metadata generation and frontend rebuilding (`-p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true`); neither frontend nor generated model metadata changed. Release package, planning Native AOT, Browser self-contained and published encrypted-receipt checks are recorded with the final evidence.

See [runtime mapping](runtime-mappings.md), [Browser contracts](../src/GnOuGo.Browser.Mcp/README.md) and [planning rules](../.agents/skills/gnougo-planning/SKILL.md). No executor, planning phase, public TaskPlan field, permission expansion or inference-limit increase was introduced.

## Live acceptance

Only one fresh Amazon evaluation, at most ten products, is authorized for this correction. It requires a clean frozen candidate, current campaign admission, and concrete requirement/artifact review before one execution. Historical invocations and unknown reservations remain untouched. Final measurements and any incomplete stages are retained separately; this correction alone cannot establish the six-run PR acceptance gate.

The [fresh execution report](evidence/canonical-items-and-actions-2026-10-07/amazon-result.md) records the completed deterministic gates and the failed single live attempt. Home acquisition succeeded; currency-quote admission failed before mapping provider dispatch, leaving an unresolved Flow invocation. No live mapping/action or XLSX success is claimed.
