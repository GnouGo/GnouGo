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
The next candidate (`efdf57cc`) passed its distractor diagnostic and all 24 mocked
integration runs, but only **31/33** final execution oracles overall. One real loop
set its total item ceiling to one despite a requested bound of three. Another inserted
an unnecessary validation transform, lost a finite string domain, and stopped on an
aggregate conditional-input diagnostic. The complete [failed cohort](failed-final-efdf57cc.jsonl)
and [comparison](failed-comparison-efdf57cc.json) remain separate. No safety violation
occurred; cumulative spend reached EUR 5.628763 with no unknown usage.

Deterministic regressions reproduce total-item versus concurrency semantics and the
lost domain through a nested loop. When literal selectors prove one input-contract
branch, new diagnostics identify its incompatible binding and the producer's exact
constraint leaf; ambiguous or dynamic selectors never widen permissions. The complete
contract is still validated. Schema descriptions now distinguish iteration ceilings
from concurrency and LLM interpretation from direct wiring/validation. No runtime or
MCP contract change is required. Candidate `7405446b` then passed **32/33** final oracles: the loop's second repetition
still selected a total item ceiling of one. Its original issued schema described total
length independently of concurrency, and its retained response itself returned one;
transport did not change the value. No safety violation occurred. The
[failed cohort](failed-final-7405446b.jsonl), [comparison](failed-comparison-7405446b.json)
and [validation](validation-7405446b.json) are retained. A test-only replay assertion
was updated during collection; the production binary and harness hashes stayed fixed.

The next guidance explicitly says sequential execution uses one worker without
reducing the requested collection limit. This remains model guidance, not inferred
intent or a compiler override. The deterministic three-item/one-worker regression
continues to require all items, preserve duplicates and reject a ceiling of one.
Fresh evaluation is pending.
