# Gate B: native checked expressions

The same TaskPlan now emits 261,126 UTF-8 YAML bytes (v10: 266,864), 61 steps and 27 sets. `before-v10.yaml` and `after-v11.yaml` are complete compiler outputs, not hand-edited illustrations. Contracts still occupy most of this fixture's YAML. All 117 ordered const declarations remain.

Names derive from declared fields, ports and available producer operations; reserved names and collisions have deterministic suffixes. Single-use checked captures, literal captures and immediately selected one-member object literals lose their redundant reconstruction. Direct physical paths remain authoritative. Necessary lexical nullable refinements and checked-object boundaries remain. Seven first-present `m.select` helpers remain; there are no `checkedMapping` program-string wrappers.

Validation: 82 compiler/physical-projection checks; 42 checked-expression/runtime checks. Eight local real Browser/Document scenarios plus compilation passed before the final one-member literal simplification. After that simplification, the complete and incomplete local scenarios and the same-plan comparison passed again. These inspect exact XLSX contents, complete source coverage, original visits, missing values, cleanup and durable recovery. Full final suites will exercise all variants on the final build.

A focused bounded evaluator fixture copied large JSON numbers and nullable nested arrays exactly. Adding eight unrelated 1.2 MB observations changed neither allocation (35,440 bytes), statements (60), materialized bytes (5,026) nor output bytes (2,688). Measured active evaluation time was 0.0417/0.052 ms in that run; this is a diagnostic measurement, not a timing guarantee. The unchanged memory ceiling was 50,000,000 bytes. Real workbook execution and journal measurements are retained in `gate-b-results.json`; no inference provider or live site was called.

Required `{value: …}` declarations remain because the existing checked-binding engine validates object-valued intermediates. Removing that contract boundary merely to achieve prettier scalar declarations would change semantics. Independent checks retain their original compiler origin even when not present in the final return.

Final emitted-YAML review also removed physical task hashes from lexical capture names using explicitly receiving fields. The unchanged physical addresses remain at the binding boundary. The 82 owning regressions and actual compiler comparison passed after that naming-only refinement; a regression rejects physical-hash lexical aliases.
