# Auto and Interactive planner decisions

> Historical pre-schema-9 evidence. Current architecture and migration: [schema 9](workflow-planning-v9.md).

The existing SemanticPlan → capability grounding → binding → validation/replanning → scenarios → FinalReview pipeline is unchanged. The planner now exposes one business-decision contract at eligible phase boundaries. New sessions default to Interactive.

## Contract and continuation

`PlanningDecision` contains a stable ID, question, business context, phase, exact scope, affected action IDs, options with labels/reasons and exactly one preferred option, and an optional custom-answer flag. Candidate payloads stay internal. Each offered candidate is validated against the existing phase schema and validators. Evidence must match an excerpt of the issued business request, scoped action purpose, or current custom answer. Matching quotation delimiters do not change that exact-match requirement.

`PlanningSession` retains the pending request, answered history, and a serializable continuation containing its operation, prompt/schema, validated results, and semantic/catalog/scope fingerprint. Auto checkpoints its preferred answer and reason before applying the result. Interactive checkpoints `waiting_for_decision` and does no further planning until an explicit command arrives.

Commands require the current `expectedRevision`:

```json
{"kind":"answer_decision","expectedRevision":2,"decisionAnswer":{"decisionId":"saved-id","optionId":"preferred-option-id"}}
```

```json
{"kind":"answer_decision","expectedRevision":2,"decisionAnswer":{"decisionId":"saved-id","text":"Use a warm professional tone."}}
```

An answer supplies exactly one option ID or nonempty text. Unknown options, stale revisions/IDs, and changed fingerprints are rejected. Selecting an option applies the saved result without regenerating the phase. Custom text becomes scoped model input and is validated normally. `configure_mode` changes `mode`; switching a waiting session to `auto` records its preferred option and continues. `cancel` closes planning without erasing its history.

Grounding offers decisions only after complete catalog coverage. Binding decisions retain accepted prefixes and apply to the current batch. Replanning preserves required output boundaries and unrelated actions/subflows. Equivalent outcomes or single valid choices proceed deterministically; malformed proposals receive bounded repair or stop. Host defects, technical repairs, permissions, and executor plumbing do not become decision cards. Budgets remain cumulative, and downstream validation and scenarios still precede FinalReview.

## Host and UI

Designer creation accepts `mode`; `workflow.plan` accepts `planning_mode`; chat requests accept `planningMode`. Designer commands use `/api/planning/{id}/commands`. Originating-chat inspection and commands use `/api/chat/conversations/{conversationId}/planning` and `/api/chat/conversations/{conversationId}/planning/{id}/commands`.

Designer and Chat share the same Blazor decision card and history. Preferred options are highlighted, but Interactive still requires Submit. The card includes alternatives/reasons, optional custom text, and Cancel planning. Automatic selections and their reasons remain visible in history.

Session payloads, model journals and conversation/execution origin links remain encrypted through public KeyVault APIs. Designer indexing retains EF Core/SQLite and optimistic revisions. Workflow sessions retain exclusive tenant/owner leases. Live answers are acknowledged after the owner persists them. After restart, the saved origin reconstructs only the planner owner and resumes its journal through FinalReview; preceding workflow steps are not replayed. Unfinished conversations are discoverable in a fresh browser. Workflow-owned sessions remain inspection-only at `/planning`.

Schema 8 is additive: missing mode defaults to Interactive, stored request definitions are normalized before comparison, and reserved legacy calls replay their original schemas. Legacy clarification remains readable. Planner decisions use `IPlanningDecisionProvider`, separately from runtime `IHumanInputProvider`. They grant no workflow approval, runtime permission, or execution authority.

## Local acceptance

Chromium drove the actual Blazor forms/cards against local Server processes and the configured OpenAi `gpt-5.5-2026-04-24` model. Chat used the existing `desktop-planning-e2e` bootstrap through its agent menu. Requests asked for a returned welcome message with formal/friendly alternatives; they required no external business actions. Answer and cancellation submissions used the UI. Read-only API inspection verified persisted outcomes.

| Surface | Case | Result | Planner calls |
| --- | --- | --- | ---: |
| Designer | Interactive option after Server restart | FinalReview; scenario passed | 4 |
| Designer | Auto | FinalReview; preferred reason persisted; scenario passed | 4 |
| Designer | Custom answer | FinalReview; scenario passed | 5 |
| Designer | Cancel | Cancelled at the pending decision | 1 |
| Chat | Option after owning Server restart, fresh browser | Original conversation; FinalReview; scenario passed | 3 |
| Chat | Auto | FinalReview; preferred reason persisted; scenario passed | 3 |
| Chat | Custom answer through live owner | FinalReview; scenario passed | 4 |
| Chat | Cancel | Cancelled in the originating conversation | 1 |

No generated workflow was approved or executed. Scenarios are simulated validation, not evidence of external business execution. Designer and Chat retained their existing catalog/settings; their call counts differ accordingly. Recovered option selection reused the saved semantic result without another semantic call.

Two unsuccessful preliminary attempts remain in [sanitized evidence](evidence/planner-decisions-live-2026-09-23.json). The first rejected otherwise valid alternatives because the model enclosed its evidence excerpt in quotation marks, then fell back to legacy clarification. The exact-excerpt normalization fix has deterministic coverage. A separate Designer custom-answer attempt stopped on an unavailable provider without a response receipt; its uncertain call was not replayed or replenished. A fresh case subsequently passed. A recovery submission while the old workflow owner was still alive was correctly rejected by its lease; it succeeded after that owner stopped.

Private prompts, responses, candidate payloads, and credentials are excluded from committed evidence. Original payloads remain in encrypted journals. Recorded cost/token figures are persisted estimates/receipts, not a complete billing statement for the uncertain provider attempt. The first successful live implementation was `a7c7ef2`; final recovery/custom checks used `f819d10`. `35d5277` adds compatibility coverage without changing runtime behavior.

## Deterministic and published validation

- Flow.Planning: 213 passed, including all four decision phases, later binding batches, mode changes, stale/invalid input, technical exclusions and legacy reserved-schema replay.
- Flow.Integrations: 79 passed, including encrypted receipts, schema-8 compatibility, leases, budgets and unchanged approval boundaries.
- Flow: 859 passed, including runtime `human.input` and protected execution.
- Agent.Server: 396 passed, including shared cards, originating-chat routing, durable acknowledgement, encrypted restart and tenant/conversation isolation.
- Solution build with `-m:1 -warnaserror`, frozen frontend install/build, and planner NuGet packaging passed.
- Native AOT planner smoke on macOS arm64: eight corpus cases plus Auto/Interactive decision restart cases passed.
- Trimmed, self-contained, single-file Server publish and encrypted EF/SQLite planning persistence smoke passed, including pending decisions and continuation payloads.

Commands are documented in the component READMEs. CI is attached to the linked PR; this report records local observations independently of CI.
