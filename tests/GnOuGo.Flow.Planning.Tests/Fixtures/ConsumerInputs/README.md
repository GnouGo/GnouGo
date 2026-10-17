# Consumer input regressions

`historical-plan.json` contains no flatten binding. Its three expected YAML hashes were collected independently from clean commit `93d1ffa87300f85f2236e0f40b37ef60a0bf951b` with omitted, v1 and v2 lowering. The current compiler must reproduce those bytes exactly.

`ConsumerInputContractTests` constructs sanitized reproductions at 52 pages / 1,474 records and 53 pages / 1,504 records. The retained live failure is preserved, without rewriting, in `docs/evidence/adaptive-mappings-2026-10-07/amazon-r4-plan.json` and `live-execution.json`: broad per-page facts produced 594,458 bytes; the global interpretation was rejected before dispatch. The corrected deterministic fixture narrows its extraction contract, then explicitly flattens candidates. Relevant duplicate observations occur outside mapping-generation examples; all source pages still execute. These tests do not claim that narrow schemas prove semantic completeness.
