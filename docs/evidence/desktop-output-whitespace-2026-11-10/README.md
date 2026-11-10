# Desktop draft output exhaustion — 2026-11-10

## Demonstrated cause

Session `a4f2be1d43ea4fa98254ea9e4704203a`, trace
`b490faff6d8a1e5b32ab289f3060319b`, stopped at revision 30 during its
first draft (logical call 9, `block_1` / `step_1`, zero checkpoints).
This is distinct from the earlier tentative matrix-repair incident.

The captured Diagnostic policy did allow input/cumulative targets to warn.
The separately captured draft allocation remained **8,192 output tokens**.
The provider returned HTTP 200, complete SSE, a complete usage receipt and
`finish_reason=length`: 11,280 input, 8,192 output, including 320 reasoning tokens.
The response contains an unfinished JSON prefix followed by **24,202 trailing
whitespace characters** (27,811 raw characters; 3,609 after normalization).
Character counts do not establish the number of tokens charged for whitespace.
The retained raw HTTP/assembled evidence preserves this tail; normalization does
not discard that evidence. No fragment can be accepted from this response.

The timeline displays the occurrence's start at **22:09:24Z** and its 133.0-second
duration. The actual blocked event was **22:11:37.786510Z**. Physical HTTP ran from
22:09:25.167966Z to 22:11:35.904634Z, before its 22:19:25.167966Z deadline.
There is no demonstrated network cutoff, local cancellation, monetary refusal or
application crash in this incident. It is a deliberately stopped planning session.

The actual request used `response_format=json_schema`, `strict=true`, and already
included compact-JSON/no-padding guidance. An offline, manually supplied suffix
to the exact prefix validates against both the issued and actual HTTP schemas
(zero findings). This rules out a locally demonstrated unsatisfiable prefix,
not an upstream decoder defect. The witness was never applied to the session.
The sanitized [recursive type fixture](nested-type-reproduction.json) retains
the relevant reachable grammar and supplies a generic complete witness.

## Generic correction implemented

- Explain that Diagnostic mode makes optimization thresholds advisory while
  incomplete model output still stops planning.
- Remove the misleading implication that Diagnostic mode always allocates the
  model maximum. The creation page uses the same explanation as the timeline.
- Explain `MODEL_OUTPUT_LIMIT` at its blocked timeline occurrence, including
  historical occurrences, without rewriting their journal entries.
- Classify an exhausted response consistently in the existing call summary and
  expanded inspector. Show issued output, reported output and reasoning counts;
  missing measurements remain unavailable. Retain protected native documents.
- Preserve rejection, accounting, exact requests, provider configuration, timeouts,
  retries, output allocations, approval and replay barriers unchanged.

This corrects misleading presentation. It does **not** claim to fix the provider's
whitespace generation. No local generation defect has been established; converting
the completeness failure into a warning or accepting an inferred suffix would
manufacture a plan. A higher token allocation or another identical attempt is not
a demonstrated remedy.

## Validation and qualification

Five of six presentation cases failed before the correction; all six pass after it.
Two new host regressions use the actual HTTP parser, encrypted receipt/session
stores and a freshly constructed service for restoration. A controlled `length`
response stops the first draft under advisory targets, with no candidate or
checkpoint. This remains true even with schema-valid JSON text. Raw whitespace,
usage, exact captures and stopped state survive recovery without another dispatch.
The fixture model handler now carries its synthetic output-limit outcome onto the
HTTP response as `length` rather than incorrectly forcing `stop`.

Focused validation: **55 passed**, including inspector, policy and new regressions,
with `-warnaserror`. Full local validation results are recorded separately after
the source is frozen. These checks use controlled models; they establish no fresh
live accepted plan, execution or independent business-oracle qualification.

[Diagnosis](diagnosis.json) binds identifiers, timestamps and private evidence
hashes. Read-only public encrypted-record/compiled-EF inspection confirmed the
session and all nine calls' request/receipt/HTTP records byte-identical before and
after. No paid calls, financial mutations, stopped-session replay or permission
changes occurred. Full original workflow qualification remains outstanding.

## Remaining external action

The [route-owner message](route-owner-message.md) is prepared and **not sent**.
The owner must correlate the provider ID with routing and constrained-decoding logs
and identify where the whitespace repetition originates. A discriminating test
compares the reduced recursive schema with a semantically equivalent unhoisted
schema using the same authorized route/model/settings. This test has not been run
against the real route. Each paid variant requires separate admission and identity.
Complete, representative schema-valid output through the actual route is the
prerequisite for a fresh full planning attempt. Full success additionally requires
technical validation, business reviews, authorized execution and the independent
oracle. Existing sessions and receipts remain immutable.
