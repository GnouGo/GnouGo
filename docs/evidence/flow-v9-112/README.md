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

These pilots fail acceptance. Later fixes do not rewrite them. The explicit
inconclusive closure preserves the original request and its full reserved cost;
it neither fabricates a completion receipt nor permits that identity to redispatch.
The parent file's `fixture` phase label is historical; each measured request records
`mode: live`. Its model and limits match the same EUR 50 campaign, `flow-v9-112`.
Historical `architecture_base` metadata predates this replacement; `source_commit`
identifies the actual checkout used for each row.

Current checks and remaining gates are recorded in the
[implementation report](../../flow-hybrid-v9-implementation.md).
