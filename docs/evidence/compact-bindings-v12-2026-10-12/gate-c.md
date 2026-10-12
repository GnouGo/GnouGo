# Gate C — readable operands and reference-only output traces

- 109 initial Flow expression/output/telemetry/recovery checks, then 66 telemetry/recovery/output checks including direct-value receipt faults, pass with `-warnaserror`.
- All 51 Planning fusion checks pass. The complete Planning suite passes 1,290 tests with zero skips/failures.
- Actual host streaming/OTel tests pass for successful and failed output checks. The retained-plan compiler comparison also passes (three focused host cases).
- Native strict lookup no longer constructs an unvalidated capture envelope just to pass its two operands. Operand order and single evaluation remain unchanged.
- Existing workflow spans receive ordered output check events, including final schema validation. Alias events identify their actual source local. Events contain references, origins, pointers and outcome/error codes, not payloads or schemas.
- Complete-result recovery produces no new check events or invocations. Committed-input recovery reuses resolved inputs; uncertain effects still prohibit replay and cleanup.
- Serial host build passed after the initial parallel build hit the existing generated static-asset collision. No timeout or assertion was weakened.

Same retained TaskPlan: 61 steps / 27 sets in both profiles; YAML 261,126 → 240,061 bytes. All 113 intermediate checks remain; JavaScript declarations fall from 113 to 96. Every retained set still has an independent responsibility; output metadata is not permission to move it past finalization or discard a receipt.

Real local workbook runs and final package/AOT/full-solution/CI results are recorded separately.
