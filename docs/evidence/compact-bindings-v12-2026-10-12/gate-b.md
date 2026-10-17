# Gate B — checked outputs

- 64 Flow expression/output/finalization checks and 51 Planning fusion checks pass with `-warnaserror`.
- OutputDef now supports omitted-by-default expression contracts for its complete expression. Validation rejects missing final contracts, invalid metadata pointers, and metadata without an expression.
- Output checks run after finalization and only after successful execution. Complete final JSON Schema validation remains mandatory; nullability is declared rather than inferred from expression text on the new path.
- Existing output-phase flatten/lookup expressions use native checked evaluation without transport sets. The large-observation fixture preserves exact selected records, repeated IDs, nulls and unknown-ID failure with no inference.
- No pre-finalization receipt-bearing set was moved. The retained-export example keeps its physical identity, checks before cleanup, and preserves the original failure.
- Source-generated JSON round-trip includes metadata when present and omits it from historical definitions and advertised output schemas. Changing metadata invalidates the durable definition hash; a completed run returns its committed result without a new revision or invocation.

The complete end-to-end and broad validation gates remain separate.

Final review added 96 passing expression/output/telemetry cases, including rejection of unreachable schema-descriptor metadata, nested metadata beside an authoritative parent schema, invalid sources and invalid final schemas. Output metadata failures use the existing fatal input-validation path before execution.

A final nested-output probe reproduced a successful result despite a child's `maxItems` failure when its parent supplied a broad authoritative schema. New checked child outputs now validate their own final contract before the next sibling, followed by the containing output's final contract. Metadata-free historical schema precedence remains unchanged. Regression cases compare both paths and verify that the failing child's event is last, with no later output evaluation or publication.
