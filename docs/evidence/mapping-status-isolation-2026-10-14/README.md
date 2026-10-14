# Separate extracted facts from acquisition status

Baseline: PR #117, `e837e0ad`. This correction is offline. No paid provider call,
Amazon navigation, historical workflow execution, campaign mutation or approval
was performed. The saved Amazon20 workflow and its execution evidence are intact.

## Retained execution

The public encrypted-storage APIs identify execution
`509b103d5b5158a01feab0427d367171`, revision 98, as failed. Its journal records two
successful `browser_get_content` calls: search acquisition and the first product
acquisition. There is no Document invocation or cleanup invocation. The saved
workflow has no finalizer; this correction does not silently add one.

The failed product mapping received 17 complete pages / 673 records. Both committed
scripts computed `status.success` using `m.has`; `availability` used `m.test` and
`completeness` used further presence checks. Neither script copied the Browser's
declared success. Its result was not among the mapping's sources: only the
snapshot and original candidate were supplied. The scripts also used the original
URL as a status message, although that URL is not producer error evidence.

Unchanged-script replays both fail at `$/status/success` with
`CONTRACT_UNSATISFIED`, specifically an **ungrounded scalar**. This is a program
failure, not a missing product observation. The parent failures propagate that
same cause. Observed booleans are still accepted; computed/literal booleans do not
become observed facts simply because they happen to be correct.

The facts-only ablation preserves the original first script's row expression and
matches the exact three original record texts. It isolates the status failure;
it is neither a generic selection algorithm nor proof of Amazon business success.
No historical engine invocation was resumed.

See [aggregate replay evidence](replay.json) and the [read-only reproduction](replay/Program.cs).
Inputs, scripts, full observations and private receipt payloads are not added to
telemetry. Evidence contains hashes rather than copied page payloads.

## Minimal production correction

- Mapping export retains the same rejection, code, message and nonretryable
  behavior, and adds its result path and missing/ungrounded category.
- The existing two-attempt terminal error forwards only those bounded structural
  fields, binding and attempt count. Existing step telemetry uses the same fields.
- The existing chat formatter displays this cause without scripts or observations.
- Fresh planning guidance distinguishes fact extraction from deterministic status
  assembly. Historical prompts, issued schemas, compiler v15 and automatic repair
  authority are unchanged.

No new executor, validation rule, repair, retry, cache profile or limit was added.

## Disposable explicit revision

The replay uses a detached copy of the last review snapshot whose YAML
matches the executed artifact apart from the terminal newline trimmed by the host.
Both byte hashes are retained. It submits one existing structural `revise` with
its current revision/hash and `preserveRequirements: true`, then supplies one
deterministic proposal. It reaches review without automatic repair or approval.
The source session, saved approval and latest saved revision are not advanced.

The only edited region is the non-null product URL branch:

1. Assert the existing success/snapshot preconditions and both producer truncation
   flags before extraction.
2. Extract only `row` facts.
3. Assemble `status.success` from the producer result, availability from the
   snapshot's presence, completeness from its metadata, and message from the
   producer's declared error field, using existing typed bindings.
4. Export the same row/status contract. Keep nullable missing facts distinct from
   failed or incomplete acquisition.

[Original TaskPlan](retained-taskplan.json), [corrected TaskPlan](corrected-taskplan.json),
[original YAML](retained.yaml), [actual corrected v15 YAML](corrected.yaml).
The replay compares requirements and every external operation/argument in order.
The scoped correction changes 14 to 15 tasks and 89,472 to 113,176 YAML bytes. This is a correctness change, not a size optimization. The complete deterministic revision request serializes to 89,635 UTF-8 bytes; no provider request was dispatched.
The status value task retains the typed nullability guard in its own scope; v15
lowering handles its deterministic plumbing without a new profile.

This deliberately leaves other existing composition issues visible: interpreted
ID reconnection, row assembly, the missing-URL branch, and the absent finalizer
have not been redesigned. The corrected artifact is an unapproved, scoped
proposal, not a replacement for the saved agent or a claim that all later work
will succeed. Replacing/executing it requires separate approval.

## Server shutdown investigation

The supplied log contains the handled workflow error but no fatal exception,
process exit code or crash stack. The available Rider lifecycle log records its
host session ending later; it does not establish that the mapping error killed
the process. Earlier macOS crash reports belonged to separate invalid-configuration
smoke executions and do not explain this incident.

The new deterministic host test starts the normal Agent.Server over Kestrel,
reproduces both bounded mapping failures, receives the chat error and revision
offer, answers the offer, and verifies `/health`, Blazor negotiation and the host
stopping token. The server remains alive. Resuming the failed disposable run
reuses its committed result, with no additional inference, read or write.

The interactive Rider shutdown is **unresolved**: it has not been reproduced
under the user's debugger session. No global exception catcher or speculative
server lifecycle change was made. HTTP and streamed-chat checks do not claim to
reproduce every browser/IDE interaction.

## Validation

Focused validation results and independently inspected local workbooks are recorded
in [validation.json](validation.json) and [workbook-oracle.json](workbook-oracle.json).
The [PR validation report](https://github.com/GnouGo/GnouGo/pull/117#issuecomment-6167735665) tracks final serial-solution, published-host and candidate-CI completion.
Local copies: [complete](local-complete.xlsx), [permitted missing facts](local-missing.xlsx). Local tests use real Browser/Document
MCP processes, a loopback site and deterministic inference. They check exact
source texts, headers, blank positions, row order, writer path, complete source
coverage, Browser closure and encrypted receipt recovery. No inference assembles
status or rows.

The incomplete acquisition case fails before extraction/writing and performs only
the fixture's permitted cleanup. Unknown completion retains the existing replay
and cleanup prohibition. Historical approvals, oneOf validation and recovery are
covered by the full existing suites.

Future model compliance and live Amazon acceptance remain unproved. PR #117 stays
draft.

## Validation failures retained

The first candidate CI caught two fresh-guidance regressions: removal of the
explicit producer-success phrase and a retained discovery request estimated at
24,047 against its unchanged 24,000 ceiling. The replacement line is now shorter
than baseline, retains that phrase and passes all 1,338 planning checks. No budget
or discovery assertion changed. The first full run was cancelled while correcting
that candidate; it is not counted as a passing suite.

Initial new-test failures exposed fixture mistakes (missing explicit test budget,
incorrect expected existing precondition codes, nullable test setup, and using a
success-only host helper for expected acquisition failure). These were corrected
in the fixtures without changing production behavior or existing assertions.
A replay guard also detected the saved YAML's terminal newline versus the host's
trimmed journal copy; both byte hashes are retained and only terminal line endings
are excluded from that correspondence check.

The first published-host attempt stalled in its Chromium download subprocess.
It was stopped and retained; the retry uses the exact already-installed Chromium
1234/headless-shell 1234 and ffmpeg 1011 revisions declared by the shipped package,
copied into the disposable publication. The standard installer and publish checks
remain enabled. No Browser source or limit changed.

The unchanged live oracle passes the complete local scenario and continues to
reject unsupported absence claims in the missing-facts scenario. The controlled
site plus complete snapshots independently prove its two permitted blank cells;
no historical verdict or live-oracle rule was relaxed.
