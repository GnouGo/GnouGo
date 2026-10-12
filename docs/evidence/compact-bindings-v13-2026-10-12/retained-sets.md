# Set-removal ledger

Same 24-task TaskPlan; every original set and every original expression-contract origin remains validated. This ledger replaces the earlier assumption that each pure check needs its own receipt. Historical v12 evidence is unchanged.

| Original v12 set | v13 execution site | Responsibility and disposition |
|---|---|---|
| `n_d628b41d17a62006` | `/workflows/main/steps/3` | Boolean check now runs inside the fused acquisition view, followed by the selected predicate checks. This physical ID survives as the shared-group anchor; it no longer has a separate boolean-only responsibility. |
| `n_496ac96c9b178654` | `/workflows/main/steps/3` | Removed. Branch-local native conditional evaluation preserves short-circuit reachability, null behavior and its original check. Existing consumer input/result owns durability. |
| `n_341937c017c6f5e7` | `/workflows/main/steps/3` | Removed. Branch-local native conditional evaluation preserves short-circuit reachability, null behavior and its original check. Existing consumer input/result owns durability. |
| `n_e2bfee3ab9860925` | `/workflows/main/steps/3` | Independent node removed; its shared checked records remain in the earlier acquisition-group anchor. Validation precedes mapping and all later uses. |
| `n_41cd6b71debae8b1` | `/workflows/main/steps/4` | Removed. Branch-local native conditional evaluation preserves short-circuit reachability, null behavior and its original check. Existing consumer input/result owns durability. |
| `n_52a0a272ae809689` | `/workflows/main/steps/4` | Removed. Branch-local native conditional evaluation preserves short-circuit reachability, null behavior and its original check. Existing consumer input/result owns durability. |
| `n_aad9144690867384` | `/workflows/main/steps/8` | Retained. Bounded products and structured rows are shared by the writer and public outputs. Their checks must run before writing and cleanup; post-finalization output validation is too late. |
| `n_5c7e3f2bc929e084` | `/workflows/w_e6f9cb8eb6196cf4/outputs/products` | Removed. Sole checked output performs its original intermediate checks, then the final output contract. No finalizer, handler, subsequent effect or identity observer distinguishes the move; the enclosing workflow result commits it. |
| `n_e63feabf14636d08` | `/workflows/w_535e8699584d5596/steps/1` | Boolean check now runs inside the fused acquisition view, followed by the selected predicate checks. This physical ID survives as the shared-group anchor; it no longer has a separate boolean-only responsibility. |
| `n_e0de4a4c5cac4169` | `/workflows/w_535e8699584d5596/steps/1` | Removed. Branch-local native conditional evaluation preserves short-circuit reachability, null behavior and its original check. Existing consumer input/result owns durability. |
| `n_407cc01ca22d66a4` | `/workflows/w_535e8699584d5596/steps/1` | Removed. Branch-local native conditional evaluation preserves short-circuit reachability, null behavior and its original check. Existing consumer input/result owns durability. |
| `n_efe1e46880c884de` | `/workflows/w_535e8699584d5596/steps/1` | Independent node removed; its shared checked records remain in the earlier acquisition-group anchor. Validation precedes mapping and all later uses. |
| `n_9a5bf33bcf7f9a03` | `/workflows/w_535e8699584d5596/steps/2` | Removed. Branch-local native conditional evaluation preserves short-circuit reachability, null behavior and its original check. Existing consumer input/result owns durability. |
| `n_45347d413d16bf26` | `/workflows/w_535e8699584d5596/steps/2` | Removed. Branch-local native conditional evaluation preserves short-circuit reachability, null behavior and its original check. Existing consumer input/result owns durability. |
| `n_b4e5179a1d9fb063` | `/workflows/w_535e8699584d5596/steps/5` | Removed. Ordered lexical fact resolution preserves nullable alternatives and strict offered lookup before original lookup, then exact text. Consumer input commits the resolved result. |
| `n_3344089734c016f4` | `/workflows/w_535e8699584d5596/steps/5` | Removed. Ordered lexical fact resolution preserves nullable alternatives and strict offered lookup before original lookup, then exact text. Consumer input commits the resolved result. |
| `n_0084788c2e4864a7` | `/workflows/w_535e8699584d5596/outputs/products` | Removed. Sole checked output performs its original intermediate checks, then the final output contract. No finalizer, handler, subsequent effect or identity observer distinguishes the move; the enclosing workflow result commits it. |
| `n_1ceffb2218b49fad` | `/workflows/w_535e8699584d5596/steps/5` | Removed. Ordered lexical fact resolution preserves nullable alternatives and strict offered lookup before original lookup, then exact text. Consumer input commits the resolved result. |
| `n_90a41bfe231cf1c9` | `/workflows/w_535e8699584d5596/steps/5` | Removed. Ordered lexical fact resolution preserves nullable alternatives and strict offered lookup before original lookup, then exact text. Consumer input commits the resolved result. |
| `n_1c418633b05d84d1` | `/workflows/w_535e8699584d5596/steps/5` | Removed. Ordered lexical fact resolution preserves nullable alternatives and strict offered lookup before original lookup, then exact text. Consumer input commits the resolved result. |
| `n_1a6f2b75e1a45754` | `/workflows/w_535e8699584d5596/steps/5` | Removed. Ordered lexical fact resolution preserves nullable alternatives and strict offered lookup before original lookup, then exact text. Consumer input commits the resolved result. |
| `n_c3eb4f284524e898` | `/workflows/w_535e8699584d5596/steps/5` | Removed. Ordered lexical fact resolution preserves nullable alternatives and strict offered lookup before original lookup, then exact text. Consumer input commits the resolved result. |
| `n_f42c5f001418dee4` | `/workflows/w_535e8699584d5596/steps/5` | Removed. Ordered lexical fact resolution preserves nullable alternatives and strict offered lookup before original lookup, then exact text. Consumer input commits the resolved result. |
| `n_312f11b2aba25a3a` | `/workflows/w_c476f53859c4d316/outputs/products` | Removed. Sole checked output performs its original intermediate checks, then the final output contract. No finalizer, handler, subsequent effect or identity observer distinguishes the move; the enclosing workflow result commits it. |
| `n_2c0eada0c9c8aead` | `/workflows/w_66933591928238cf/outputs/products` | Removed. Sole checked output performs its original intermediate checks, then the final output contract. No finalizer, handler, subsequent effect or identity observer distinguishes the move; the enclosing workflow result commits it. |
| `n_e7a587b7baac7afb` | `/workflows/w_f77350ccb86829cf/outputs/products` | Removed. Sole checked output performs its original intermediate checks, then the final output contract. No finalizer, handler, subsequent effect or identity observer distinguishes the move; the enclosing workflow result commits it. |
| `n_bfe2032a4f34743e` | `/workflows/w_38efaf896cb79a80/outputs/products` | Removed. Sole checked output performs its original intermediate checks, then the final output contract. No finalizer, handler, subsequent effect or identity observer distinguishes the move; the enclosing workflow result commits it. |

## Three retained physical sets

| Set | Producers and consumers | Why input/output inlining is not equivalent here |
|---|---|---|
| `n_d628b41d17a62006` | Search acquisition → mapping preparation and later product-loop original-record capture | One validated original-record view is shared across inference and effects. Inlining into mapping input does not publish it for the later lookup; recomputing would repeat checks and potentially reread mutable state. |
| `n_e63feabf14636d08` | Per-product acquisition → mapping and the later three fact resolutions | Same shared association; validate completeness before inference and preserve the captured original records for all selected IDs. |
| `n_aad9144690867384` | Collected visits → writer rows and public products | Validate cardinality and assemble once before the write; reuse products after cleanup. Moving to output checks changes the first failure and can write invalid business results. |

No surviving set is justified solely by an existing receipt. Removing these three would require another proof of immutable shared sources and equivalent early validation, not hidden checkpoints.

## Retained loops

The two pure capture loops still enforce existing iteration bounds and cancellation points. Current native projection lowering cannot preserve those checks for their nested capture scopes. The effectful visit loop retains navigation, mapping and receipts. No loop limit was replaced with an unbounded `map`.

## Origin audit

All 113 distinct v12 expression-contract origins are present among 151 v13 origins. New origins cover the former child-input and final-output boundaries. The regression additionally checks all 27 primary set origins. Branch-local checks execute only when reached; presence in metadata does not claim execution.
