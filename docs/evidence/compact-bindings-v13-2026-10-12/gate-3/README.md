# Gate 3: checked lexical branches and recovery

Pure predicate and nullable fact branches use the existing structural evaluator with static lexical scope qualifiers. Only reached declarations and aliases are validated. Offered membership precedes original lookup; text comes from original records. Pure child scopes retain their input and final output contracts. No external action, inference, or mapping budget boundary is inlined.

The final same-plan comparison is **24 steps / 3 sets / 7 workflows / 203,908 YAML bytes**, versus v12's 61 / 27 / 13 / 240,061. Small literal schemas use flow-style YAML under v13; this changes formatting, not schema assertions. All 113 original expression-contract origins and all 27 primary set origins remain represented. The final artifact is `branches-v13.yaml`.

A metadata audit caught an intermediate implementation copying an inlined expression without its nested branch contracts. Earlier provisional 194 KB artifacts were rejected. The consumer and output handoffs now retain those contracts, and the regression checks original origins. No validation was removed to obtain the final reduction.

The complete Planning run also caught a terminal identity shortcut referencing an already inlined producer in a nullable-capture scope. The v13 shortcut now excludes those aliases; a two-variant regression reproduces it. Historical lowering is unchanged.

Validation on the corrected source: **1,350 complete Flow Core cases, 1,308 complete Planning cases, and 12 local Browser/Document cases passed** with warning-as-error builds. Local cases independently inspect real workbook values, headers, order, blanks, visits, original arguments and cleanup. Unknown IDs, incomplete observations and denied actions reject affected work. Completed encrypted recovery repeats neither inference nor external calls. Full solution, packages and CI are reported separately in the final validation evidence.

Crash tests cover before/after branch-control and child-result publication: uncommitted pure work may rerun; committed external work does not. Check events contain references, origins, pointers, order and outcomes, without observations or schemas. A 1.2 MB unrelated observation does not change lexical statement/materialization counters. Unsupported lexical programs cannot import unrestricted context into Jint.

The three shared sets and two capture loops remain for specific early-validation, sharing and effective-limit reasons documented in `../retained-sets.md`; an existing technical receipt is not itself a justification.
