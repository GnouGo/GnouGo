# Gate C: observable input checks

Checked input declarations report ordered reference-only outcomes through the existing step spans. Each event names the local/business alias, original compiler origin, input pointer, literal contract pointer, ordinal, outcome and error code. It includes no observations, schema body, program or error-message content. Outcomes are buffered only until the normal step span opens, also on pre-dispatch input failure. No new step, public telemetry interface, journal field or checkpoint is introduced.

Committed resolved inputs emit `gnougo-flow.input.reused` during an unfinished run's recovery. They are neither reevaluated nor reported as newly successful checks. Fully completed runs retain their existing short-circuit recovery. Uncertain completion still prohibits replay and cleanup. Skipped and unreached checks emit no successful outcomes.

Validation: 79 owning expression, telemetry and recovery checks passed. Twenty host checks passed, including actual streaming and persisted OpenTelemetry step events, complete/incomplete local Browser/Document execution and exact workbook verification. Faults before/after receipt commit prove no duplicate external action, no repeated check and unchanged uncertainty protection. Complete results and raw measurements are in `gate-c-results.json`.

Reference-only event size is identical with zero or 1.2 MB of unrelated observations. Instrumented evaluation preserves statement/materialization/output counters and the first failing origin; trace timing and allocation measurements are reported separately rather than enforced as machine-dependent thresholds. Existing executor/output-contract events remain authoritative for their own validation boundaries.
