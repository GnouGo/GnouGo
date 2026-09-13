# Singleton declaration output diagnostic

The one authorized diagnostic **completed** and satisfied the original response
schema. It used **1,274 input tokens, 74 output tokens and zero provider-reported
reasoning tokens**. Final-answer usage is **74 tokens**, derived as output tokens
minus the reported reasoning count.

Identity: `schema5-singleton-output-16384-low-1`. The source is the stopped
singleton declaration request from session `7a27ef2963ae49589c9b6c4196644fe7`,
revision 21. The original request, prompt and schema were retrieved from encrypted
storage and matched their retained fingerprints. The diagnostic kept
`gpt-5.5-2026-04-24`, `low`, strict output, all other generation fields and disabled
transport retries. Only the output ceiling changed from 8,192 to **16,384**;
the durable request identity is separate from the archived request.

## Exact returned assignment

```json
{
  "declarations_r_f62567b1da3aa9193fdaad62": {
    "ob_10482c39f2b5cfde": {
      "disposition": "modifier_of",
      "target": "ob_7c0dd31eb3577e6e",
      "presence": "unspecified",
      "default": null
    }
  }
}
```

The captured references establish that `ob_10482c39f2b5cfde` concerns category's
allowed values (`rejected`, `high`, `standard`) and `ob_7c0dd31eb3577e6e` is the
`classifiedResult` output declaration. The assignment attaches the enumeration
constraint as a modifier, without creating another port, asserting port presence
or introducing an omission default. Original-schema validation returned **zero
findings**. The assignment was not committed to the archived session or assessed
by advancing the planner.

## Isolation and evidence

- Production remains at `0f4ebfdbf733687c0dc4d1dc9c4b89ca702df95e`; all 22
  production DLLs and production source are unchanged.
- Harness-only implementation: `0d01662`; warning-free build and 61 passing
  focused diagnostic, persistence and campaign tests.
- One separate EF request reservation and one encrypted completed receipt exist.
  No planning session was created, resumed or approved.
- The diagnostic budget carries the source's consumed usage and permits only
  one additional dispatch. Its active deadline preserves the source's consumed
  active time rather than counting archived/human-wait time as execution.
- All 36 archived source records, including their timestamps, remained identical.
  The harness also compared the original EF call-index entries before and after.
- Missing-receipt restart stops; completed receipt replay cannot dispatch or
  increment usage again. No live replay or second diagnostic was invoked.

| Evidence | Fingerprint |
|---|---|
| Source request | `2d289a0514c308703e35bae582087c247d2e1b66b8f81097fcd42c978b7abba7` |
| Prompt/context | `e0f44c563c65ed59d0fb5cb672bcb36a89f59f34ea209daa707b3fdaa5bfb35e` |
| Original response schema | `6b07a8952c358aa5914da3068b42ea214a24324d11a77f4739df9a2299498f00` |
| Diagnostic request | `fe4bae2ceee0982d65acde945e9c6723d5946bfe8228424d73ee605cbddd4df7` |
| Diagnostic receipt | `3b0330afbd0e870834abf9fe9a42953e72f4629335758e4d86dd52e3aff80e67` |

The original observation exhausted 8,192 output tokens entirely in reported
reasoning; this independent observation finished with 74 output tokens. That
single comparison does not establish whether the larger ceiling was necessary
or distinguish its effect from model variability. No production optimization or
limit change follows from the experiment.

The [machine-readable report](planner-singleton-output-diagnostic-report.json)
retains the exact assignment, metrics, identities and verification results.
The campaign remains stopped. No Stage-1 start, Stage 2, semantic correction,
medium/high reasoning request or additional output-limit experiment followed.
