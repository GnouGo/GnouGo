# Frozen effect-admission diagnostic

Production stayed at `107cbcdd1789cbefc6b67d53f3ab570499854245`. One fresh
LOCAL case started under `schema5-effect-grounded-admission-diagnostics-1`.
MIXED and Stage 1 were **not run**.

LOCAL stopped in source interpretation, before effect grounding or canonical
operation admission. Its first terminal failure was `LLMClientException` on an
unverifiable provider dispatch. The underlying provider cause is unconfirmed;
the exception, request and budget remain in encrypted diagnostic storage.

The first request, containing two interpretation decisions, returned a verified
`output_limit`. The existing deterministic split produced two child pages. The
first singleton child also returned `output_limit`; its one permitted 16,384-token
escalation ended without a verifiable receipt. No request was retried, no new
case was started and no production patch was made.

| Measurement | LOCAL | MIXED |
|---|---:|---|
| Interpretation reservations / verified calls | 3 / 2 | Not run |
| Effect-grounding decisions / calls | 0 / 0; not reached | Not run |
| Occurrence-identity decisions / calls | 0 / 0; not reached | Not run |
| Committed deterministic operation assignments | 0 | Not run |
| Governing attachments | 0; admission not reached | Not run |
| Canonical operations | Unavailable; no committed set | Not run |
| Known input tokens | 1,443 | Not run |
| Known output tokens | 16,384 | Not run |
| Known reasoning tokens, included in output | 16,384 | Not run |
| Known final-answer tokens | 0 | Not run |
| Unverifiable requests with unknown usage | 1 | Not run |
| Output splits / child pages | 1 / 2 | Not run |
| Singleton escalations | 1 | Not run |
| Semantic repairs | 0 | Not run |
| Largest estimated / verified actual input | 1,695 / 861 | Not run |

The unavailable escalation targeted `interpret_18819469387f8281c01c9120`:
page `page_1a567eef6002e05aa7093ded0fd524dd6cbfdcacfbed0b0f34a1994217263a50`.
Exact request, schema/context fingerprints and split lineage are in the
[redacted report](planner-effect-admission-live-report.json). The pending sibling
was never dispatched. Unknown escalation usage is not included in the known
token totals above.

The model remained `gpt-5.5-2026-04-24`, with `low` reasoning, 16 durable
reservations per case, 12,000 input ceiling, 9,600 dispatch target, 8,192 normal
output ceiling and one bounded singleton escalation to 16,384. Frozen scenario,
declaration ports/attachments, host policy, catalog and budget settings matched
the prior campaign. These declarations remain fixture preconditions; this run
did not revalidate them through a fresh planning session.

The harness added acceptance checks for effect input/result ownership, producer
dependencies, rules, descriptive attachment and zero standalone identity calls.
Its 26 focused tests and synthetic preflight checks passed. All 22 production
DLLs, production source files and the frozen harness DLL matched their recorded
hashes after the run. Archived accounting remained unchanged.

This run provides no live evidence for or against effect-grounded admission:
it never reached that boundary. The two verified truncations and the unverifiable
escalation are retained as separate observations.
