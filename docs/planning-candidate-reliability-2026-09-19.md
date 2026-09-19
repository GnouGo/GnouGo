# Independent compact-intent candidate evaluation

Campaign: `compact-intent-candidate-20260919-2d15b1`. Architecture base: `1f15bec`. Previous campaigns and stopped sessions are untouched. All effects are mocked; no PR execution or GitHub publication occurs.

Configured provider/model resolved through public KeyVault configuration: `openai` / `gpt-5.5-2026-04-24`. Medium reasoning, eight calls, two repairs, 96,000 input and 32,768 output tokens, EUR 50 cumulative campaign ceiling. Encrypted records retain original request schemas, receipts, session checkpoints and diagnostic evidence. Redacted rows are reported separately from private reproduction evidence.

## Initial pilot at `8b63d5e`

Seven attempted runs: 5/7 FinalReview and independent execution success, 4/7 first-pass validity, median one call, zero observed policy/capability safety violations. Ten calls, three repairs, 24,156 input and 7,870 output tokens; estimated cost EUR 0.311414. The pilot failed; no measured runs were started on this revision.

| Case | FinalReview | First pass | Calls | Repairs | Root classification |
| --- | --- | --- | ---: | ---: | --- |
| local | yes | no | 2 | 1 | Invalid intent: undeclared calculation variable; repair succeeded |
| read_transform | yes | yes | 1 | 0 | None |
| conditional | yes | yes | 1 | 0 | None |
| collections | yes | yes | 1 | 0 | None |
| protected_cleanup | no | no | 3 | 2 | Invalid intent: dependency on a cleanup group, followed by an invalid cross-scope dependency and then the original invalid dependency |
| review_french | yes | yes | 1 | 0 | None |
| review_distractors | no | no | 1 | 0 | Deterministic builder defect: missing inference for a runtime input consumed only inside a parallel block |

Diagnostic code categories in raw rows are provisional. The local schema/binding errors are consequences of an undeclared computation variable, not separate inference failures. Protected-cleanup proposals were safely rejected. The distractor failure had all suitable allowed capabilities available; it was not a retrieval miss.

## Targeted correction

The builder considered only direct capability consumers when deriving runtime-input schemas. Nested branches capture the same enclosing flow inputs, but those consumers were ignored. A sanitized echo-capability regression reproduced the defect without review terminology in conditional, loop and nested parallel blocks. The small correction includes nested consumers for input inference while keeping operation-result inference scope-local. It adds no planning phase, contract or persistence state. Tests independently assert retained constraints/defaults, alternate execution values and rejection outside the contract.

The runner also avoids attributing preceding diagnostics to a newly reserved repair before validation. Existing evidence is unchanged; repeated reservation observations in the initial pilot are not additional failed responses.

The updated pilot and final release validation will be recorded below. Measured evaluation remains gated on a clean seven-case pilot on one revision.
