# Designer validation on a3f56bc

## Result

The single authorized Designer test **stopped during interpretation** with
`MODEL_OUTPUT_LIMIT`. It did not reach capability correction, graph construction,
scenarios or FinalReview. No workflow was approved or executed.

The provider returned HTTP 200, with a completed receipt marked `output_limit`.
Reported completion usage was 8,192 tokens, all reported as reasoning tokens;
there was no returned intent text or JSON. Classification: **provider/model
output-limit failure**, not an HTTP timeout, uncertain dispatch, or demonstrated
planner repair defect. No reasoning content was inspected or retained in this
report.

No production code was changed. Limits were not increased, no retry was requested,
and no replacement session was created. This result cannot confirm or disprove
the focused-repair correction because that phase was never reached.

## Reproduction and settings

Source: clean, synchronized `a3f56bc` on `feat/deterministic-planner-v2`.
This is the separately authorized follow-up to the
[recovery correction and earlier failed test](planning-designer-recovery-2026-09-21.md).

An isolated Release Server was published with warnings treated as errors:

```sh
dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true -m:1 -p:PublishTrimmed=true -p:PublishAot=false -p:SkipBundledServerTools=true -p:UseAppHost=true -warnaserror -o <isolated-host>
```

Publish completed successfully without build warnings. Existing real MCP tools
were staged, and provider configuration was resolved through public KeyVault
APIs without exposing credentials. Designer, Agent, telemetry and Files indexes
were isolated; only the fresh Designer index was eligible for background
planning. Existing session indexes and ledgers were not resumed.

The Blazor Designer was driven through Chromium, with working interactivity.
Its fresh session list was verified empty before submission. The exact original
680-byte prompt was read from `aa6971c5bf8140b5a2b752ebef93e1a2` through the public
session store and entered through the normal form, once. No planning REST write
or handwritten workflow was used.

Prompt SHA-256:
`8f9c138913d4fd8e136760df3b7e20fe4cf7623f02fa9fa4c46220bcaae1cf85`.

| Setting | Value |
| --- | --- |
| Provider / model | OpenAi / `gpt-5.5-2026-04-24` |
| Protocol | Chat Completions |
| Reasoning | Medium |
| Maximum model calls / repairs | 8 / 2 |
| Input / output limits | 12,000 / 8,192 tokens |
| Input prompt / response schema | 18,447 / 10,458 UTF-8 bytes |
| Approval / execution | Neither authorized for this run nor performed |

## Observations and accounting

| Field | Observed value |
| --- | --- |
| Session | `7321f76a7c574effb00ff21173b1a2cf` |
| Name | `designer-validation-20260921-a3f56bc` |
| Submission | 2026-09-21 17:23:45 UTC |
| Reservation | 2026-09-21 17:24:16 UTC |
| Completion receipt | 2026-09-21 17:26:23 UTC |
| Final state | Stopped, revision 3 |
| Model-call trace | `e85dcabb1393a96406e7445ea8810cd8` |
| Model calls / repairs | 1 / 0 |
| HTTP attempts | 1, HTTP 200 |
| Reported input / output / total tokens | 7,388 / 8,192 / 15,580 |
| Reported reasoning tokens | 8,192 |
| Estimated cost | EUR 0.246040; USD 0.282700 |
| Model-call duration | 126.10 seconds |
| Session active time | 132.28 seconds |
| Receipts / pending requests | One completed receipt / none |
| Intent / graph / YAML / scenarios | None produced |

The only final diagnostic is located at `$`:

> MODEL_OUTPUT_LIMIT: The model response was truncated; no output limit escalation is performed.

The normal Traces panel showed the truncated call, its usage, protocol, duration
and HTTP attempt. Request/response expansion loaded the encrypted logical
documents (32,869 and 560 bytes). Reopening the panel refreshed the journal index
to show the completed truncated receipt. The output document contains receipt
metadata and usage, not a usable business intent.

Usage shown as pending before completion was treated as unknown. The final
receipt supplies known usage; no uncertain dispatch or additional reservation
was created. The final cost above belongs only to this new session. The earlier
EUR 0.253673 and EUR 0.178599 estimates remain in their original sessions; the
combined estimate for these three identified sessions is EUR 0.678312.

## Preservation and cleanup

Public KeyVault reads verified that the earlier session/request/receipt/budget
digests were unchanged before and after this test. The new session's digest was
recorded after completion and checked again after inspection:

| Session | Records | Digest |
| --- | ---: | --- |
| `aa6971c5bf8140b5a2b752ebef93e1a2` | 16 | `c50e079f1d259d9653eaea51fc281949084eae6c4eedd655bf675e64d854d778` |
| `3fbbd97bd758412f9027e38caeeb3e17` | 8 | `da27aacf507108cce0ff609750dad3752c020dbd26c546418295ac0cc7e7ac66` |
| New `7321f76a7c574effb00ff21173b1a2cf` | 7 | `cce0a0197fd4d34566309019c083cbd585128cfcf28193ff3f9e40ec224775bd` |

The isolated host and browser were stopped after inspection. Their persistence
directories and the encrypted records were preserved. No repository clone,
repository command, Copilot review, GitHub write, approval, merge or deployment
occurred. Existing hosts and credentials were left unchanged.

## Validation limits

Fresh checks cover warning-free Server publish, actual Designer submission,
stored settings, trace expansion, receipt inspection and evidence preservation.
The prior 1,739 passing tests and eight Native AOT smoke cases remain documented
in the correction report; they were not rerun for this documentation-only delivery.
No synthetic benchmark campaign was started.

The unchanged 8,192-token output ceiling was insufficient for this particular
interpretation. The earlier attempt produced intent under the same ceiling, so
this observation does not establish that every request needs a higher limit.
Focused repair reliability and FinalReview remain unverified on the live model.
Any further attempt or settings change is outside this single-test delivery.
