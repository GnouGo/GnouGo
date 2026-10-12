# Retained materializations

V14 changes expression representation, not the v13 execution boundaries. No set is removed in either frozen comparison. These are current responsibilities and eligibility limits, not a claim that the sets are permanently irreducible.

## Retained live r8: all eight sets

| Physical identity | Consumers / responsibility | Why retained in this correction |
|---|---|---|
| `n_cc7e46480b3b5be5` | Asserts the submitted search operation's success before the next Browser acquisition. No business data output. | Moving it later would permit acquisition before the assertion. The next acquisition has literal/protected arguments; this lexical-expression change does not create an input-check anchor there. The independent pre-acquisition failure remains. |
| `n_c8dfc8f3b8101e22` | Checks the search acquisition, projects page record arrays; later `n_057d824b69e5f524` reconnects selected IDs. | The decision/inference occurs between preparation and reconnection. Delaying these checks to the lookup consumer changes failure order. The original producer association and checked projection remain available. |
| `n_7dd6ae600f9f86c8` | Validates writer results and assembles `products_xlsx_path`, `completion_status`, `observed_errors`. | All three public outputs share this pre-finalization result. Output evaluation occurs after Browser cleanup; moving the checks there changes timing. |
| `n_1443288fb4759b94` | Collects the visit loop's `product_row` and `product_error` outputs. | One checked object supplies two workflow outputs. Independent expressions would duplicate/reorder the coupled assembly/export checks; this pass does not create shared locals across output expressions. |
| `n_f63ccecf29465a25` | Checks the product acquisition and retains original record arrays for name, description and price resolution. | Three conditional consumers share it, with inference between acquisition validation and resolution. Moving or repeating the work is not equivalent. |
| `n_5bbb95172fc592b7` | Validates the selected name cell before description resolution; consumed by final row assembly. | Its failure precedes the next conditional branch. Fusing it into final row assembly would move it past that branch. |
| `n_e64e92c5531a0775` | Validates the selected description cell before price resolution; consumed by row assembly. | Same ordering boundary as the name check. |
| `n_d24e8f7dda99263d` | Validates the price cell, combines all cells and the observed error, exports `product_row` and `product_error`. | Shared, coupled two-port output assembly. No new cross-output evaluation or durability mechanism is introduced. |

The three remaining `m.select` calls belong to the name/description/price sets. They select the first **present** result of actual workflow-call alternatives. The called bodies retain a workflow structure outside the existing pure-call eligibility. Their present-null and invalid-value behavior cannot be replaced by `??`, `find`, or an assumed branch address. No eligibility expansion was made to hide them.

## Existing compiler comparison: all three sets

| Physical identity | Consumers / responsibility | Why retained |
|---|---|---|
| `n_d628b41d17a62006` | Checks the original search acquisition and retains `pageRecords`; later selected-product iteration uses them. | Validation occurs before intervening extraction/decision. Delaying it changes failure order. |
| `n_e63feabf14636d08` | Checks each product acquisition, retains `pageRecords`; checked nullable fact resolution consumes them. | Shared original records and early validation across inference. |
| `n_aad9144690867384` | Validates bounded products and row assembly, consumed by the writer and public `products` output. | Shared computation must fail before writing and cleanup, not during post-finalization output evaluation. |

## Remaining expression containers

- Pure-call argument objects still implement the original checked input contract, including required captured keys and their original diagnostic locations. Their physical keys are deterministic addresses. Functional local aliases avoid spreading those keys through the business computation.
- Meaningful multi-port return objects and checked-set object results remain. A generic object check cannot simply be applied to an unwrapped array/scalar. The rewrite only unwraps a proved singleton property schema; it does not replace that schema with a broader contract.
- Projection `results`/`count` containers retain existing collection/iteration checks. Removing them would need a separate equivalence proof for those checks, not just deleting an identity `map`.
- Conditional IIFEs establish selected-path validation scopes. V14 removes closed, unconditional IIFE nesting, while retaining the lexical blocks needed to place conditional checks.
- Strict offered lookup precedes original lookup even when its result is not returned. It remains both executable and observable.
