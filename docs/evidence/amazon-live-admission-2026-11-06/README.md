# Fresh Amazon admission attempt

On 2026-11-06, the requested fresh Amazon test was launched through the actual
Agent.Server protected operator endpoint and existing Limited campaign guard.
It used qualified implementation `2a83c9c0`; current branch `a7f381a7` adds only
documentation. The clean isolated checkout preserves the user's staged solution
and `.vscode` changes. Provider/model/configuration comparison passed unchanged:
`OpenAi / gpt-6.1-sol-2026-09-29`.

- Run: `amazon-2a83-live-20261106-2001`.
- Session: `11386753f6ce4c4eb456deb20376e057`, revision 2.
- Result: **stopped during `construction_intent`: `LLM_BUDGET_EXCEEDED`**.
- One logical request prepared; **zero physical provider HTTP attempts**.
- Output allocation: 8,192; reasoning: medium; estimated input: 2,617 tokens.
- No response, accepted checkpoint, compiled artifact, execution or oracle result.

This is an actual Server admission failure, not a successful real-LLM Amazon trial.
The exact request/HTTP-preparation journals and structured failure were inspected
through protected reads. [Result](amazon-result.json), [inspection summary](inspection-summary.json),
[command/timestamps](attempt.json) and [configuration comparison](configuration.json).

| Financial admission, EUR | Exact amount |
| --- | --- |
| Unchanged global ceiling | 740.0301038327063045424449473 |
| Existing campaign estimated charges and reservations | 698.00453661827155318192155264 |
| Headroom before external-session accounting | 42.02556721443475136052339466 |
| Current request plus mandatory review reservation | 57.72967170587709097414795606 |
| **Shortfall even without external consumption** | **15.70410449144233961362456140** |
| Additional provisional external hold passed to the existing guard | 731.9247059871703492516037064 |
| Guard's total required exposure, including that hold | 1487.6589143113189934076732151 |

The extra hold uses the original nominal reservation estimates retained for the
325 unpriced completed operator calls, their pending reservations, the already
recorded priced estimates and the prior Server-local hold. Its exact sum is
731.9247059871703492516037063354; the decimal admission value rounds upward.
This is a provisional financial hold, **not an invoice or a claim that those calls
actually cost that amount**. Missing historical cache-write prices remain unavailable.
No historical charge was repriced, settled or released. The request/review exposure
alone already exceeds the campaign headroom, independently of this provisional hold.

The diagnostic financial policy reserves against captured physical input allowances
plus mandatory review. The EUR 57.73 is an allocation reservation, not the estimated
cost of the 2,617-token prompt or a provider-reported charge. Configured phase output
allocations were preserved; no silent allocation reduction or Unlimited bypass occurred.

[Exact budget comparison](budget-result.json) proves that campaign transport totals
remain unchanged: 7,190 attempts, EUR 475.28093332377412455040481684 estimated known
cost and EUR 222.7236032944974286315167358 reserved. The evidence hash changes because
new run/request/failure records were retained. Both historical operator budget
journals retain identical hashes [before and after](historical-preservation.json).
The new Server session also retains its EUR 1.9655709807433945364979847738 application
reservation; it was not released. **Verified available global funding remains unavailable.**

The initial browser observer failed because the isolated checkout lacked generated
frontend assets (`GnOuGo.Agent.markdown.enhance` was undefined). The unchanged frontend
was then built successfully using installed dependencies. This did not change the
independent Server financial refusal. No screenshots are presented as successful
live UI evidence, and no post-build browser revalidation or second model attempt was
performed. [Browser limitation](browser-limitation.json).

No provider inference, Amazon operation or external business effect was dispatched.
The stopped session and all reservations are preserved without replay. Continuing
under the unchanged ceiling requires financial reconciliation and admission covering
the current request/review allocations. No ceiling increase, reservation reset,
manual plan correction, merge or release was performed.
