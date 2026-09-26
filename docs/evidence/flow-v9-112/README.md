# Flow schema-9 validation evidence

The JSONL files contain frozen-corpus measurements, without model prompts,
responses, credentials or private workflow content. They preserve failed and
inconclusive runs. Encrypted campaign records retain the underlying requests,
receipts, HTTP accounting and session histories.

- Parent `46c2c77`: 24 live runs, 14 independently correct, median four calls.
- Pilot `f5ba32a`: one correct of three attempted; stopped inconclusively.
- Pilot `d6db53e`: three correct of eight, median 3.5 calls.
- Pilot `ec3e75f`: one correct of eight, median four calls.
- Pilot `1029d5d`: five correct of eight, median two calls.
- Pilot `5733aa3`: six correct of eight, median two calls; both complex cases
  exposed a missing proof for switch selector guards.

- Pilot `459b5a2`: seven correct of eight, median two calls. Both complex cases
  passed; the conditional case exposed an unchecked projection path, subsequently
  fixed with a deterministic producer-contract check.

- Pilot `6352146`: six correct of eight, median 2.5 calls. The French repair
  exhausted its output limit; the distractor workflow omitted cleanup because it
  compared the executor status to an undeclared value. Both failures are retained.

These pilots fail acceptance. Later fixes do not rewrite them. The explicit
inconclusive closure preserves the original request and its full reserved cost;
it neither fabricates a completion receipt nor permits that identity to redispatch.
The parent file's `fixture` phase label is historical; each measured request records
`mode: live`. Its model and limits match the same EUR 50 campaign, `flow-v9-112`.
Historical `architecture_base` metadata predates this replacement; `source_commit`
identifies the actual checkout used for each row.

Current checks and remaining gates are recorded in the
[implementation report](../../flow-hybrid-v9-implementation.md).

## Frozen three-repetition comparison

Candidate production source: `87acc5f0e41702e193795b45b4ddfab1055d2143`.
The 24 original candidate rows are in `candidate-live.jsonl`; both original batch
summaries remain in `candidate-batches.json`. Every approved workflow passed the
independent execution variants; both unsuccessful runs remain failed.

| Measurement | Parent | Candidate |
| --- | ---: | ---: |
| Independently correct outcomes | 14/24 | 22/24 |
| Median planning calls | 4 | 2.5 |
| Retained French review case | 0/3 | 2/3 |
| Review with 80 distractors | 0/3 | 2/3 |

There is no per-case correctness regression. The model, limits and shared campaign
are identical. `comparison-original.json` remains inconclusive because the original
runner labeled the request denied before HTTP dispatch as unknown usage and counted
its logical reservation as a ninth call. The HTTP journal admitted eight attempts.
`candidate-inconclusive-closure.json` permanently closes that failed session without
changing its original run, request, failure, receipt absence or spending reservation.

The final repetition used harness commit
`c0a2d216a3378a7b9831cee538de3fab59c5836d`: a cherry-pick of
`28f9cc27f40365a404835f11a313f0e3c0481230` onto the frozen candidate. Reproduce its
runner by checking out `87acc5f` and cherry-picking `28f9cc2`. Only the runner entry
point and README differ; production, corpus, oracles, accounting and model transport
are identical. `--evaluation-source` enforces that restriction. Collection continued
at repetition three after retaining the second repetition's exhausted session.
The failed session received no renewed allowance.

`campaign.json` retains final cumulative accounting: EUR 23.5169310315 known cost
plus EUR 5.1285251074 conservatively reserved, an upper bound of EUR 28.6454561388
under the unchanged EUR 50 ceiling. Four uncertain physical attempts retain their
full reservations. No further model requests were issued for measurement auditing.

`comparison-audited.json` passes the relative acceptance criteria after an explicit
read-only admission audit. Its proof hashes tie the unchanged original run/request
to its permanent closure and empty HTTP journal. The audit corrects that failed
row's call count from nine logical reservations/retries to eight admitted attempts
and establishes bounded usage; it does not change the outcome. The original
comparison and row are embedded alongside the audited result. The complete
campaign evidence hash and accounting are identical before and after the audit,
as recorded in `validation.json`.

The existing stricter pilot/measured live-release gates remain unpassed; this
relative comparison does not waive them. The real bounded command edit/test cycle
also remains blocked by host sandbox enforcement. The PR therefore remains draft.

To reproduce the read-only comparison using the committed audit harness:

```bash
dotnet run -c Release --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --campaign flow-v9-112 \
  --compare-parent 46c2c77fea19c952d3258d744d886fe52ef52d33 --parent-phase fixture \
  --candidate 87acc5f0e41702e193795b45b4ddfab1055d2143 --candidate-phase fixture \
  --retained-case review_french --audit-admission-denials
```

This command requires access to the original encrypted campaign records. It makes
no model request. The checked-in JSONL/JSON files provide sanitized review evidence.
