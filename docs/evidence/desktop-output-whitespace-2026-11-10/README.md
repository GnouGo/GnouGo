# Desktop draft output exhaustion — 2026-11-10

## Demonstrated cause

Session `a4f2be1d43ea4fa98254ea9e4704203a`, trace
`b490faff6d8a1e5b32ab289f3060319b`, stopped at revision 30 during its
first draft (logical call 9, `block_1` / `step_1`, zero checkpoints).
This is distinct from the earlier tentative matrix-repair incident. The Desktop
runtime build identity is unavailable in the inspected records; the source
investigation began at `ea8de646`.

The captured Diagnostic policy did allow input/cumulative targets to warn.
The separately captured draft allocation remained **8,192 output tokens**.
The provider returned HTTP 200, complete SSE, a complete usage receipt and
`finish_reason=length`: 11,280 input, 8,192 output, including 320 reasoning tokens.
The response contains an unfinished JSON prefix followed by **24,202 trailing
whitespace characters** (27,811 raw characters; 3,609 after normalization).
Character counts do not establish the number of tokens charged for whitespace.
The retained raw HTTP/assembled evidence preserves this tail; normalization does
not discard that evidence. Independent concatenation of all 7,864 native SSE
events (zero parse errors) exactly matches the raw retained content, including
the whitespace tail. The client assembler did not introduce the padding. No fragment can be accepted from this response.

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

Implementation commit: `d9038a53855138a09b84fd0e8638cebb28fd66de`.
The [validation record](validation.json) binds the frozen source and log hashes:

- Focused tests: **55 passed** with `-warnaserror`.
- Shared solution excluding Server: **6,696 passed, 12 existing skips** across
  32 projects, including all **2,967 planning tests**.
- Full Server inventory: **2,073 passed, one existing skip**, with exact coverage
  of all 2,074 discovered cases and unchanged binaries. All 217 workbook cases pass.
- Aggregate: **8,769 passed, 13 existing skips, zero failures**. Focused reruns are
  not added again. Skips are seven Windows command cases, five opt-in live Copilot
  cases and the existing Designer clarification browser case.
- Full solution build, including Desktop: **zero warnings, zero errors**, with
  `-warnaserror`. Agent.Server frontend production build also passes; the package
  declares no separate test, lint or type-check scripts. Razor interactions and
  existing browser flows are covered by the Server suite.

These checks use controlled models; they establish no fresh live accepted plan,
execution or independent business-oracle qualification. No native Desktop window
was driven and no deployment or push was performed.

[Diagnosis](diagnosis.json) binds identifiers, timestamps and private evidence
hashes. Read-only public encrypted-record/compiled-EF inspection confirmed the
session and all nine calls' request/receipt/HTTP records byte-identical before and
after. No paid calls, financial mutations, stopped-session replay or permission
changes occurred. Full original workflow qualification remains outstanding.

## Remaining external action

The [route-owner message](route-owner-message.md) is prepared and **not sent**.
The owner must correlate the provider ID with routing and constrained-decoding logs
and identify where the whitespace repetition originates. A discriminating test
compares the reduced recursive schema with the same grammar after inlining
nonrecursive shared definitions while retaining recursive references, using the
same authorized route/model/settings. This test has not been run
against the real route. Each paid variant requires separate admission and identity.
Complete, representative schema-valid output through the actual route is the
prerequisite for a fresh full planning attempt. Full success additionally requires
technical validation, business reviews, authorized execution and the independent
oracle. Existing sessions and receipts remain immutable.
