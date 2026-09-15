# Isolated interpretation singleton result

One diagnostic request under `schema5-effect-interpretation-singleton-16384-low-1`
completed successfully. Production remained frozen at
`107cbcdd1789cbefc6b67d53f3ab570499854245`.

The captured request for `interpret_18819469387f8281c01c9120` was cloned without
rebuilding its prompt or schema. Model `gpt-5.5-2026-04-24`, `low` reasoning,
16,384 output ceiling and all generation fields matched exactly. Only the
coordinator-owned request identity and journal authorization changed. Original
escalation authority stayed in the archived diagnostic.

| Measurement | Result |
|---|---:|
| Completion | `completed` |
| Input tokens, provider-reported | 582 |
| Output tokens, provider-reported | 336 |
| Reasoning tokens, included in output | 283 |
| Final-answer tokens, derived as output minus reasoning | 53 |
| Original schema valid | Yes; zero findings |
| Reservations / verified receipts | 1 / 1 |

Exact returned assignment:

```json
{
  "interpret_18819469387f8281c01c9120": {
    "obligations": [
      {
        "start": "b0",
        "end": "b7",
        "kind": "implementation_policy",
        "required": true
      }
    ]
  }
}
```

Nine harness regressions passed before dispatch. The isolated encrypted journal
retains the receipt and cumulative budget seed separately. Archived records,
indexes and budget fingerprints remained unchanged, as did all 22 production
DLLs and production source files. No LOCAL, MIXED or Stage-1 run started. The
assignment was neither repaired nor committed to the archived planner.

[Request and receipt fingerprints](planner-effect-singleton-interpretation-diagnostic.json)
include the exact source request, prompt/schema hashes and archive-integrity proof.
