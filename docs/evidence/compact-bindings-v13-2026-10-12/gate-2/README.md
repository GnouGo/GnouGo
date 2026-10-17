# Gate 2: terminal checked outputs

Same TaskPlan: 51 steps / 17 sets / 13 workflows / 232,319 YAML bytes, versus 61 / 27 / 13 / 240,061 at v12. Eight additional terminal sets move to their sole checked output. Finalizers, handlers, retries, multiple outputs and other observers exclude the move. Intermediate checks retain their ordered origins, instance paths and contract failure messages; execution-boundary enrichment correctly identifies the new output location.

A checked-output-only workflow performs real validation. The existing empty-workflow diagnostic now recognizes that work; workflows without steps or checked outputs retain the diagnostic. Runtime output validation remains mandatory.

The same-plan compiler comparison and real one-product local Browser/Document execution passed with `-warnaserror`; the latter independently verifies workbook cells, observed values, visits and closure. Positive output equivalence and retained-boundary counterexamples pass. No paid inference or live site was used.
