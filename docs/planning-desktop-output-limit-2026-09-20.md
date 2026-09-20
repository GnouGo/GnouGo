# Desktop reasoning-token exhaustion — 2026-09-20

## Finding

The fresh native Desktop attempt confirmed the preceding
[protocol correction](planning-desktop-protocol-2026-09-20.md): authentication and
`POST .../chat/completions` succeeded with HTTP 200. The returned receipt records
`output_limit` after consuming **all 8,192 completion tokens as reasoning**.
It returned **zero characters of plan text**. This was not a partially emitted JSON plan
or a demonstrated planner construction defect.

Source: clean `a5b1937` on `feat/deterministic-planner-v2`; production planner behavior
remains frozen at `65dc34a`. The request was submitted by the user through native Desktop
using the existing `desktop-planning-e2e` bootstrap. No direct planning API submission,
replacement workflow or synthetic execution evidence was used.

Classification: **provider/transport failure — configured output allowance exhausted**.
The safe behavior is to preserve the completed receipt and stop, without automatic
output escalation, duplicate dispatch or an intent repair against nonexistent JSON.

## Evidence and accounting

Session: `aa3ef46bc5635c7cfab261fe3bb1862cf1389a0ca839cb9cc58e0de1bced0da6`,
tenant `default`, revision 2, status `stopped`, diagnostic `MODEL_OUTPUT_LIMIT` at `$`.
Created `2026-09-20T12:09:29.405864Z`; updated `2026-09-20T12:11:45.000426Z`.
Trace: `d6273cf6e57515b4ee6dfc353cb8fa5c`; inference HTTP span ran from
`12:09:32.190Z` to `12:11:44.991Z`, approximately 132.8 seconds.

| Measurement | Observed value |
| --- | --- |
| Model / reasoning | `OpenAi` / `gpt-5.5-2026-04-24` / medium |
| Input / output request limits | 12,000 / 8,192 tokens |
| Calls / repairs | 1 / 0 |
| Reserved requests / completed receipts | 1 / 1 |
| Verified input tokens | 8,772 |
| Verified output tokens | 8,192 |
| Provider-reported reasoning tokens | 8,192 |
| Total tokens | 16,964 |
| Returned text | Empty; no parsed JSON |
| Session active time | Approximately 135.590 seconds |
| Catalog capabilities | 129 |
| Intent / graph / YAML / scenarios / approval | None produced |

Read-only inspection used public encrypted KeyVault records and the public telemetry API.
Evidence content SHA-256 values:

- Reservation: `947af6de02416cbeb337cd18ba33a413170b9fa0dbeadbfce0df5ed91f879643`.
- Completion receipt: `96fc928d3415874aba056732dd69332e337cb736c62f4808d37bac2e268a0f48`.
- Budget: `afeb147414c4e10b4f0b7e2604e6b8d204aafc355b9c576c8dca9349a3e28b42`.

The bootstrap's session budget has no monetary limit, so its cost counters do not measure
this charge. They must not be read as proof of free inference. Applying the existing
host `ModelMetadataUsageCostEstimator` to the verified tokens and effective provider
configuration gives **USD 0.289620 estimated cost**. This is a local catalog estimate,
not a provider invoice or a newly enforced EUR ceiling. No billing records were invented.

Across the two real Desktop attempts there are two reserved calls: this completed call
plus the earlier HTTP 404 attempt without a completion receipt. The latter's usage/cost
remain unknown. No Copilot inference occurred. The user's EUR 20 best-effort target and
waiver of hard provider-budget verification remain in effect; previous benchmark ledgers
and both Desktop attempts remain untouched.

## Approved configuration correction

The approved change is limited to this test agent's existing generator setting:

```diff
-            max_output_tokens: 8192
+            max_output_tokens: 32768
```

This is the explicit output ceiling used by the accepted live benchmark. It provides more
room for reasoning and returned JSON, but does not guarantee that the next plan will be
valid. The 12,000 input-token limit, medium reasoning, eight calls, two repairs, total-token
and active-time limits, transport policy and approval gates stay unchanged. There is no
planner refactor or automatic escalation mechanism.

At report revision `0041557`, this change was prepared but not applied because the approved
Desktop test plan expressly retained the bootstrap's request limits. The user subsequently
approved the increase. It is now applied to `desktop-planning-e2e` through the running host's
normal `agent_update` MCP operation and verified by `agent_get_by_name` read-back.

Only `max_output_tokens` changed. The stored bootstrap had omitted the example's final
newline; the update preserved that formatting and all other content, agent identity and
original prompt. The repository example and production code remain unchanged. Agent ID:
`158cb170-9f4f-4a54-a112-ab5a68a787be`. Updated stored-workflow SHA-256:
`966478c847defc9afed16b92f4023ff098174017187e553d232d04068d134ed7`.

The proposed workflow compiled successfully before writing. The temporary management probe
built in Release with zero warnings/errors and verified the persisted workflow after writing.
Before/after hashes and timestamps confirmed that both failed sessions, model reservations,
completion receipts and budget records were unchanged. No inference request was dispatched
by this configuration operation; a fresh native Desktop submission remains necessary.

A new request must have a new identity. Do not edit or resend either stopped request or
reset its accounting. There is no reason to open a business-workflow revision for this failure.

## Sanitized regressions and execution status

Deterministic tests use a generic greeting task and a synthetic provider receipt with
12 input tokens and 8,192 reasoning/completion tokens, without PR-specific wording.
They verify stopping after one call without repair or escalation, preserving an explicitly
configured ceiling, and replaying the encrypted receipt and original schema across restart
without another dispatch or another charge. Both 8,192 and 32,768 ceilings are exercised
through the existing planner. No production code was changed.

Validation on this working tree (production source unchanged from `a5b1937`):

```text
dotnet test tests/GnOuGo.Flow.Planning.Tests/GnOuGo.Flow.Planning.Tests.csproj -c Release -m:1 -warnaserror --verbosity quiet -p:SkipModelMetadataGeneration=true
PASS: 150 tests
dotnet test tests/GnOuGo.Flow.Integrations.Tests/GnOuGo.Flow.Integrations.Tests.csproj -c Release -m:1 -warnaserror --verbosity quiet -p:SkipModelMetadataGeneration=true
PASS: 67 tests
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -c Release --
PASS: eight offline cases, all 31 execution variants, zero safety violations
```

Focused checks also passed before those full affected suites. Both Release test builds
were warning-free. The offline runner used the existing Release build of unchanged
production/benchmark source and made no provider requests. No full solution publish or
new live benchmark campaign was needed for this test/documentation change. Document
links and `git diff --check` were checked before delivery.

No review workspace was created and no repository checks ran. There was no GitHub review,
source edit, commit, push, metadata change, merge or deployment on SmartGuide. Cleanup is
not applicable because `workflow.execute` was never reached. The complete real-review
acceptance criteria remain outstanding.
