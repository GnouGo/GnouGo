# Authorized archival and fresh Amazon attempt

Frozen source/harness: `fb07a92c888a07358ed12d1ce92fdcd24db83937`.
The user explicitly authorized retaining `gluev420261008b-amazon-1` as
inconclusive and starting a fresh Amazon evaluation, with at most ten products.
No production source, permissions, limits or execution oracle changed.

## Historical closure

The existing `retain-inconclusive` command committed the
[closure](archival-closure.json) at `2026-10-09T07:54:37.7689770Z`. It records the
original request/run hashes and accounting. An independent invocation returned
the identical persisted closure. No receipt was created and no old request was
replayed.

[Verification](archival-verification.json): the historical run's inspection was
byte-for-byte identical before/after archival. The command writes a separate
encrypted closure only; original runs, requests, failures and receipts remain
untouched. Accounting remained exactly EUR **114.62611591296104918692599868**,
including EUR **5.2033274578484720979030515354** reserved for four unknown physical
attempts. All three uncertain logical requests have retained closures; no unclosed
request remained to block campaign admission. Closure is not reconciliation and
does not release unknown usage.

## Fresh live result

Cohort `consumerbindings20261009b`, run `consumerbindings20261009b-amazon-1`.
[Manifest](manifest.json), [binary hashes](frozen-binaries.json),
[configured readiness](provider-readiness.json), [CI](ci-checks.json).
All 29 non-skipped CI checks passed before dispatch; pricing/currency readiness
passed with the existing quote and unchanged 96,000-token input allowance.

**Planning stopped; execution did not start.** Three logical planning calls,
four physical attempts, one repair, two discovery reads, 489,937 ms. The
[six-slot report](cohort-report.json) retains this failed attempt and leaves the
other five slots unexecuted. There was no cohort expansion or code-review run.

Exact rejection: `REQUIREMENTS_OUTPUTS_CHANGED` at `/outputs/products`.
The [retained contract mismatch](contract-mismatch.json) has one declared type
difference: accepted `products.items.nullable` is **false**, but the producing
`normalize_products` result declares it **true**. Nullable name/description/price
fields are allowed; a null product record is not. The automatic repair corrected
an iteration-output reference and did not change that immutable result contract.

The read-only [compiler replay](compile-replay.json) succeeds with zero inference
and no saved-session changes. This does not override the separate accepted-output
contract check. All seven existing accepted-output regressions pass with
`-warnaserror`. The validator correctly stopped the proposal.

Review also exposes three global interpretation tasks receiving whole
`observationSnapshot` inputs. They have not executed; this is a composition review
finding, not a measured overflow in this run.

[Business result](business-result.json): zero Browser actions, zero product visits,
no YAML/artifact approval, no Excel file. XLSX content and workflow cleanup remain
unexercised. There is no execution success to report, and PR #117 remains draft.

## Minimal proposed correction

Use the existing targeted revision mechanism to align the producing result
contract with the accepted output, preserving non-null records and nullable
missing fields. Preserve operations, required visits, writing and cleanup. Do not
weaken the validator or fill absent records with invented values.

Before approval, replace the broad interpretation inputs with the already
supported consumer-specific extraction/typed views. Use deterministic flattening
for already typed collection plumbing. This needs a corrected composition, not a
new executor, framework or runtime change. Any diagnostic improvement should point
to the nested incompatible contract path. No further paid revision or execution
was dispatched in this attempt.

## Accounting and retained evidence

Known new usage: **22,925 input / 9,393 output tokens**, EUR
**0.3517747803709290975241813826**. One uncertain transport attempt remains reserved
at EUR **1.2982873369420534208891649659**, even though all three logical requests
have completion receipts. The [final campaign upper bound](accounting-final.json)
is EUR **116.27617803027403170533934502 / 150**, leaving approximately EUR **33.72**.
All historical reservations remain; total unknown physical attempts are now five.
There is still no unclosed logical request blocking campaign admission.

Original proposals, the repair, transport attempts and usage receipts remain in
the existing encrypted campaign records. This directory contains sanitized
diagnostic excerpts and measurements only. No historical invocation was resumed.

## Reproducible commands

Commands that inspect or replay compilation are read-only. The archival and fresh
plan commands below document the actions already performed; do not repeat the
fresh run with the same identity.

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability retain-inconclusive --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --run gluev420261008b-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability plan --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --cohort consumerbindings20261009b \
  --case amazon --run consumerbindings20261009b-amazon-1 --max-products 10
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability replay-compile --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --run consumerbindings20261009b-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability report --workspace "$GNOU_GO_WORKSPACE" \
  --campaign schema-portability-20261002 --cohort consumerbindings20261009b
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror --no-build \
  --filter FullyQualifiedName~AcceptedOutputTests
```
