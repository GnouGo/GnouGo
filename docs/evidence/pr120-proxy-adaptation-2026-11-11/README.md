# Requests within the enterprise-proxy idle constraint

The operator confirmed an enterprise proxy that cuts idle streams at 300 seconds.
This is now an environment constraint; route-owner access is not a prerequisite
for request adaptation. The desired margin is a measured complete useful response
below 240 seconds, not a new rejection rule. No client deadline is increased.

[Baseline measurements](baseline-measurements.json) preserve the failed consumer's
57,117-byte prompt, 22,767-byte schema, `low` wire reasoning and 32,768-token output
allocation. Configured levels are low/medium/high/max: low is already the minimum.
The two initial SSE envelopes contained no useful output or usage. HTTP 200 did
not establish protocol completion. All three CI checks at `cd8951b7` passed.

The [14-call first-checkpoint breakdown](first-checkpoint-phases.json) contains one
intent call, one business selection, four implementation selections, five discovery
calls and three drafts. It took 662.82 seconds. Cold HTTP and response journals
agree on EUR 3.705705329153605015673981191 captured-tariff cost; the earlier milestone
snapshot of EUR 3.689513108614232209737827716 remains unchanged and is reported
separately. Legacy evidence does not locate the first nonempty content timestamp.

The trace repeatedly requests the empty root and later abandons an initial draft
because the selected operation cannot include the already inspected cleanup. The
implemented production changes address repeated schema shapes and root inspection;
no automatic operation-selection authority is added. The discovery/cleanup detour
remains a measured baseline for fresh qualification.

## Implemented preparation

Fresh Server/Desktop generation factors repeated shapes independently inside each
imported schema or embedded producer view using the existing compactor. Literal
data, descriptions, cardinalities and nullability are preserved. Existing references,
definitions and reference-scope identifiers stay exact. The generator supplies
empty-root structural facts through transient `scopeContext`; strict packing can
retain the original evidence link instead when necessary. No retained evidence,
checkpoint, original requirement or approval is created. Draft guidance describes
a coherent typed increment within its existing business block.

The existing native HTTP journal records `FirstOutputAtUtc` for nonempty assistant
text/refusal/tool payload, excluding role, empty envelope, comments and reasoning.
It does not establish semantic usefulness, completion or usage. New responses
still require the existing full protocol, compiler and business checks. Historical
records without the optional timestamp serialize unchanged.

A standalone probe's existing hash-bound amendment may explicitly include a strict
response schema and its exact source schema hash. Atomic validation precedes all
mutation; the independent request and before/after hashes are retained. This test
harness still applies no fragment and changes no provider, endpoint or tool grant.

[Controlled checks](controlled-validation.json) pass with `-warnaserror`: 2,969
planning, 390 AI-core and 435 affected benchmark tests; Release is warning-free.
[Captured presentation](consumer-presentation.json) reduces the retained consumer
prompt from 57,117 to 54,990 bytes. This reduction alone is not a latency or workflow
qualification. No paid call for these changes has yet been made.

The optional [fresh navigation policy](server-navigation-low-draft32k.json) captures
low navigation reasoning, low draft reasoning and the existing medium review
reasoning, while preserving every output allocation. It is prepared for authorized
new experiments; no old session or pin acquires it. Full Amazon qualification waits
for correct representative consumer responses and a fresh actual-profile source.
