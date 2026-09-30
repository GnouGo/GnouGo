# Planner stabilization execution evidence

Baseline production: `7288b6997c704b8ca098761c030071d61da1ce1a` (merged PR #113).
Frozen harness: `22bba1a5`. Campaign: `planner-stabilization-20260930`, pinned
OpenAi `gpt-5.5-2026-04-24`, medium reasoning, 96,000 input / 32,768 output tokens,
eight physical planning attempts, two repairs and EUR 50 cumulative allowance.

The [runner instructions](../../../tests/GnOuGo.Agent.Planning.Benchmark/README.md#planner-stabilization-comparison)
describe reproduction, manifest comparison, encrypted evidence and acceptance.
All eight original requests/oracles are unchanged. Three additional scenarios launch
the shipped Cmd MCP server and independently check real filesystem effects. These
local results do not establish Copilot sandbox execution or remote publication.

The frozen baseline ran three repetitions of all eleven scenarios: **26/33 correct**,
33 physical planning calls, no repairs, 182,282 input / 37,751 output tokens and
estimated EUR 1.800035 usage. No unknown usage is outstanding. The seven failed
outcomes remain in [baseline.jsonl](baseline.jsonl):

- Three optional-input cases omitted the requested input default; nominal execution
  failed while the explicit-input variant passed.
- One distractor-catalog case added an unrelated cleanup operation. The independent
  oracle rejected it despite nominal runtime success.
- Three real conditional/iteration cases failed compilation because an empty array
  output lacked an established concrete contract.

The empty-array compiler regression was reproduced before correction. Its constant
contract now survives semantic lowering and independent graph validation, with both
conditional branches exercised by a deterministic execution test. Request guidance
preserves explicitly requested optionality/defaults and avoids unrelated cleanup;
no scenario identifiers, operation names or fixture predicates enter production.

Initial local validation passed 3,719 tests across 33 projects, with 12 existing
platform/live skips and zero failures. The new harness passed 14 tests, including
an actual stdio read, comparison rejection and filesystem-oracle checks. Retained
log hashes and baseline identity are in [baseline-validation.json](baseline-validation.json).
One undispatched harness startup failed to resolve a Unix apphost's dotted assembly
name; the fixed harness was frozen before the first model reservation. No benchmark
identity or budget was reset.

The first candidate (`32afd9a2`) passed three diagnostic cases, then **32/33**
final execution oracles. The failed `review_distractors:3` run appended a generic
lifecycle operation after the required resource cleanup. Runtime success was true;
the independent oracle correctly failed the run. There were no safety violations.
Its [full failed cohort](failed-final-32afd9a2.jsonl),
[failed comparison](failed-comparison-32afd9a2.json) and
[validation](validation-32afd9a2.json) remain separate evidence. Cumulative spend was
EUR 3.734557, with no unknown usage.

The retained task objective incorrectly inferred a lifecycle relationship from a
publication operation's write effect. A deterministic execution regression preserves
that counterexample: contracts permit execution, but the oracle rejects the extra
operation. The next prompt removes a universal cleanup instruction and distinguishes
permission effect kinds from documented business/resource lifecycle relationships.
This is model guidance, not a new inferred contract or deterministic semantic guarantee.
Final evaluation of the corrected candidate is pending.
