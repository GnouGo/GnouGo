# Agent.Server mapping request allowance

This correction adds `Flow:Execution:MaxMappingInputTokens` to the existing host
configuration. Shipped appsettings selects 96,000; omitted configuration keeps
Core's historical 12,000. Model capacity remains an independent, stricter ceiling.
New roots persist the selected limit; recovery uses the stored value. No compiler,
Browser, mapping semantics, sandbox ceiling, approval or journal format changes.

The preceding authorized appsettings change also ships 50 cumulative calls,
1,000,000 total tokens and 30 minutes. Those cumulative limits remain unchanged
by the mapping correction. They are separate from per-request admission and do
not configure a monetary ceiling or alter the live campaign ledger.

## Retained execution and exact admission reproduction

The public encrypted storage APIs identify run `a62f22a7673158b16d209abdbd25ed57`,
revision 22, as failed at `n_45947512fc71c0d2`. Its limit is omitted (effective
12,000). URL preparation and the initial Browser acquisition completed. There is
no mapping-generation child receipt, product visit, writer call or recorded
Browser cleanup in this journal. The retained workflow itself has no finalizer;
this change neither adds one nor claims cleanup succeeded.

The committed mapping input contains **55 pages and 1,649 records**. The replay
loads that exact input and the original target schema, without resuming the engine
or consulting a live model/MCP server. Its deterministic capability fixture supplies
a 96,000 model allowance; a stricter real model remains subject to the unchanged
minimum calculation.

| Measurement | Conservative request bound |
| --- | ---: |
| Original request without examples, including framing | 10,446 |
| Smallest complete example request tried at the 12,000 ceiling | 15,996 |
| Admitted original-contract request at the 96,000 ceiling | 78,001 |

At 12,000 all 55 items are examined and no adapter call occurs. At 96,000 one
request reaches the offline capture adapter. `OFFLINE_ADMISSION_CAPTURE` is the
intentional test stop before generation, not a production failure or a claim that
the complete approved workflow executed successfully.

A **separate reference-coverage fixture** uses the same original inputs and a
reference-only output contract. One deterministic script processes every page
and preserves all 1,649 original references exactly, in order. This proves input
coverage and mapping execution; it does not prove business candidate selection,
the original `observedIndex` field, product extraction or Amazon workbook success.

The replay reports zero provider and MCP calls, and compares the encrypted journal
head before and after. No historical request, receipt, approval, reservation or
workflow is changed. Only aggregate measurements are retained here; no observation
payloads are added to diagnostics or telemetry.

## Regression and integration gates

Regressions cover host/model minimum selection, historical omission, valid and
invalid explicit settings, configuration overrides, Unicode/escaping, context
overflow, required-example overflow, still-oversized inputs and count-only traces.
Errors preserve their code, location and nonretryable behavior while explaining
why admission failed. Existing complete-example selection and request serialization
are unchanged.

The normal host tests exercise adaptive execution and committed-result recovery
under changed configuration. Existing local Browser/Document fixtures independently
check ten visits, original descriptions, exact workbook cells/headers/blank positions,
output paths and Browser closure, with deterministic inference and local networking.
Their fake model now supplies explicit test usage so token accounting remains active.

Focused, complete-solution, published-host and candidate-CI results are recorded
after completion; pending checks are not counted as successful. No paid inference,
Amazon navigation, historical replay or campaign mutation is included. PR #117
remains draft and live business acceptance remains unproved.
