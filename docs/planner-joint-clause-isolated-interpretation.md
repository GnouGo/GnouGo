# Captured interpretation — one isolated VPN-era retry

## 1. Outcome

**COMPLETED — original schema valid.** Exactly one isolated request ran as `schema5-joint-clause-interpretation-diagnostic-1`, after the user reported connecting to the VPN. Production remains `d2ceac7cd85793baea732721f4e2b092c94be210`.

This is provider-response evidence only. The stopped LOCAL was neither resumed nor accepted. No assignment was committed to a planner, and no MIXED or Stage 1 ran. [Exact response and integrity report](planner-joint-clause-isolated-interpretation.json).

## 2. First meaningful blocker

No blocker occurred in this isolated attempt. Its source LOCAL remains historically stopped on an unverifiable `LLMClientException` during `interpret_40997e14a2c6d889e553c1a9`, with one missing receipt. That historical usage remains unknown.

## 3. Root cause

The same captured request now completes at its original **8,192-token** output ceiling, with `low` reasoning and the same `gpt-5.5-2026-04-24` configuration. This is consistent with changed connectivity/provider availability, but does not prove the VPN was the underlying cause. No planner or output-limit change is justified by this observation.

## 4. Authority analysis

Only the coordinator-owned request identity changed. Prompt, original schema/context, model and every generation setting remained equal after normalization of that identity. Transport retries remained disabled. The source request had no escalation lineage; none was added.

A separate encrypted journal, EF index and budget record reserved before dispatch. The isolated budget retained the source's recorded one-call consumption and allowed at most one additional call within its existing token/cost limits. The source's missing token/cost usage is not converted to zero or repaired by this new receipt.

The new assignment is preliminary interpretation evidence. It has no contribution, realization, applicability or canonical-operation authority without the existing downstream validation, which was not run.

## 5. Proposed action

Stop after this completed request. Recommend **one fresh LOCAL, separately authorized**, to obtain complete fresh interpretation/admission evidence. Do not resume the unverifiable archive or import this isolated answer into a new campaign.

## 6. Validation performed

Harness-only changes adapted the captured-request diagnostic to the new source identity and normal output ceiling. Both harness/test builds passed with zero warnings or errors and production-reference rebuilding disabled. **151 focused checks passed**, including exact generation at normal and escalated ceilings, single reservation before transport, completed-receipt reuse, rejection of unverifiable redispatch, changed-request rejection and archive isolation. The preceding full offline production validation was retained; it was not rerun for this adaptation.

The result was validated directly against the original schema: **zero findings**. No semantic repair, normalization of the answer or planner commit occurred.

Before/after checks verified all **17 production-source hashes**, **22 production DLL hashes** and existing report hashes. The source checkpoint, budget, request, missing receipt and EF reservation remain unchanged, with source archive fingerprint `5edfa3f1acf560b6af6603b3a98fe1c13e2d4161dd3fc55a1bf7ff21461ffc5f`.

Source request fingerprint: `358686e7a0733e9c79b11495da02d4ed79be9212080e6ea7431f1ab202d9acee`.

Original schema fingerprint: `1218a91ea3b0c4707641fee38135182fe80fe3aa6bb12fbd5e6d0044c89e39e9`.

Completed isolated receipt fingerprint: `0d5f67cfdf0a2617c9b069e97c2384ed2b765801aec84a39ac4f094c995fe886`.

## 7. Decision accounting

| Measure | Isolated attempt |
|---|---:|
| Durable reservations / verified receipts | 1 / 1 |
| Input tokens | 4,007 |
| Output tokens | 113 |
| Reported reasoning tokens | 0 |
| Final-answer tokens | 113 |
| Schema findings | 0 |
| Partitions / escalations / repairs | 0 / 0 / 0 |
| New planner decisions / committed assignments | 0 / 0 |

Final-answer tokens are output minus provider-reported reasoning. Recorded cumulative calls are **2**: the historical unverifiable reservation plus this isolated reservation. The historical request's usage remains unknown; the recorded 4,120 total tokens cover only the new verified receipt.

Exact returned assignment:

```json
{
  "interpret_40997e14a2c6d889e553c1a9": {
    "obligations": [
      { "start": "b0", "end": "b17", "kind": "runtime_condition", "required": true }
    ],
    "runtime": [
      {
        "role": "local_behavior",
        "kind": "local_processing",
        "action": { "start": "b0", "end": "b17" },
        "execution": "generated_workflow",
        "boundary": null,
        "necessity": { "state": "required", "evidence": { "start": "b0", "end": "b17" } },
        "baseline": null
      }
    ]
  }
}
```

## 8. Next gate

**One fresh LOCAL, requiring separate authorization.** No further live execution.
