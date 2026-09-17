# Joint clause qualification — fresh MIXED

## 1. Outcome

**VERIFIED BLOCKER — category C.** The single authorized campaign, `schema5-joint-clause-qualification-mixed-diagnostics-1:mixed`, stopped during canonical contribution qualification. No realization coverage, governing applicability, dependency proof or operation admission committed. MIXED did not pass.

Production remains frozen at **`d2ceac7cd85793baea732721f4e2b092c94be210`**. LOCAL was not rerun; its accepted evidence remains unchanged. Stage 1, replacement diagnostics, isolated provider requests and production patches were not run.

[Redacted evidence, exact qualification answers, accounting and integrity checks](planner-joint-clause-qualification-mixed.json).

## 2. First meaningful blocker

- Code: `INTENT_OPERATION_UNRESOLVED`
- Location: `/operations/@r_18bfa390e735cc81df86b716`
- Finding: **“Clause semantic units give contradictory authority to overlapping evidence.”**
- First invalid assignment: request **10**, decision `contribution_clause_0799b094930035113a140f1e`.
- Terminal detection: after request **11**. The frozen implementation collects the contribution pages before validating their assignments. There was no dispatch after the terminal stop.

The complete clause is:

> Read the record identified by sourceId once from the external record store.

Request 10 selected:

| Unit | Owned evidence | Selected authority |
|---|---|---|
| Requested execution | `r_7e18d297f0b007fde1354f96`, `[179,247)`: “Read the record identified by sourceId once from the external record” | `requested_owned_occurrence`, candidate `operation_1169739a01f8b1ee16abd853` |
| Governing property | `b6`–`b7`, resolving to `r_d6e6f485b36a24f5f68d858a`, `[218,222)`: “once” | Unbound governing property |

The execution unit supplied its support span both by reference and equivalent `b0`–`b11` coordinates. Existing normalization collapses those identical spans. The remaining failure is the governing span contained within the execution-support span, which the current cross-role overlap validator rejects.

Both contribution responses, and all nine interpretation responses, satisfy their **original** schemas. No receipt was reinterpreted under a new schema.

## 3. Root cause

The joint qualification schema permits each unit to choose owned evidence independently. The resulting array can therefore contain requested-execution support and governing-property evidence over intersecting coordinates. `ParseContributions` subsequently rejects every overlap between different contribution roles.

This establishes a mismatch between the issued response domain and the enforced joint authority contract. It does not establish a provider failure, budget failure, declaration-coverage failure or dependency defect. It also does not, by itself, establish that “once” is semantically irrelevant or that the read was not requested. The review must distinguish a property contained in a complete request from contradictory executable authority.

## 4. Authority analysis

The owner is **canonical joint clause contribution qualification**, specifically its unit-domain builder and deterministic overlap validation. The engine already possessed exact source coordinates, runtime provenance, possible effect ownership and its cross-role overlap rule. It could detect this combination without another semantic model decision. The current schema did not prevent the combination.

The current validator failed closed before coverage or atomic admission. Governing evidence was not promoted, no property created an operation, and no dependency was invented. Possible target IDs in the responses are **not admitted operations**:

- Read candidate: `operation_1169739a01f8b1ee16abd853`.
- Local result candidate: `operation_e226ff9ed819b5a645e7d9de`.

The second qualification answer selected local result production, but complete contribution validation failed first. Neither target obtained committed realization coverage. No current contribution, coverage, applicability, dependency or admission fingerprint authorizes this MIXED result.

This is a bounded, engine-detectable consistency failure within the recurring executable-contribution authority class. Under the retained convergence rule, the next gate is **architecture review**, rather than another targeted overlap filter, prompt change or retry.

## 5. Proposed action

Stop. Review the joint request/property overlap contract and its issued domains before choosing an implementation. Do not silently trim support, drop the property, broaden validation or reinterpret the retained answer.

No production or frozen harness change was made after live execution began. Read-only reporting used public KeyVault APIs and no provider transport.

## 6. Validation performed

**Fresh offline prerequisites:** 3,695 solution tests passed, one optional provider-test skip, zero failures across 29 assemblies. This includes 1,092 planner and 550 Agent.Server tests. All 152 focused harness checks passed. Harness/test builds used `BuildProjectReferences=false`, with zero warnings or errors.

The published Native AOT planning/encrypted-restart and trimmed Agent.Server persistence smokes passed. Synthetic LOCAL/MIXED selfchecks passed with zero provider calls and read-only restart validation. Six classifier/batch and eighteen frozen reference cases passed. Production build, frontend, package and publish evidence was retained and hash-verified; production was not rebuilt for this gate.

Authorization tests cover accepted-LOCAL gating, stale proofs, missing evidence, second-start refusal, LOCAL/Stage-1 refusal, current MIXED occurrence/dependency assertions and allowed relationship phases. The full suite also covers receipt reuse, unverifiable-reservation refusal and accounting behavior.

**Prerequisites:** the existing exchange-rate provider checked USD → EUR once and accepted a quote dated **2026-09-16**. No quote was injected. The frozen manifest retained `gpt-5.5-2026-04-24`, all-low reasoning, sixteen durable calls, 12,000 input ceiling, 9,600 dispatch target, normal 8,192 output and bounded 16,384 singleton escalation, plus existing monetary/token/deadline/repair budgets, policy, catalog and fixtures.

**Live evidence:** nine interpretation decisions produced 22 runtime records with zero unresolved scopes. Eight typed host clauses projected nine obligations with zero model interpretation; five baseline structural units projected five engine-owned contract records and no operations. The canonical fixture retained required `sourceId`, optional `threshold` with omission default 100 and nullability/default attachments, and required `classifiedResult` with category/preservation attachments. Output-declaration and preservation candidates generated no qualification request; only read and classification clauses were queried.

**Read-only replay:** all eleven responses have zero original-schema findings. Re-entry reproduces the same stop with zero provider dispatches and zero replayed receipts, using completed pages. Checkpoint and budget remain unchanged. Successful completed-admission restart is **not reached**, because there is no committed admission.

All 17 production-source hashes, 22 production DLL hashes, fixtures, prepared harness hashes and 82 historical report hashes still match. The accepted LOCAL audit is identical before and after. The archive aggregate remains `21bd71c8da95e7ce5799a69acbff2a62e02245834532dc503a10663b2c1bd718`. The stopped MIXED checkpoint fingerprint is `7a754d36d480f1a54dc68dfcc50d45344b698e030c67b6760dbf4b89460311c3`. Its report reloads identically.

## 7. Decision accounting

| Class | Model decisions | Verified calls | Input | Output | Reasoning |
|---|---:|---:|---:|---:|---:|
| Interpretation | 9 | 9 | 26,364 | 2,715 | 1,758 |
| Joint clause contribution qualification | 2 | 2 | 2,831 | 712 | 356 |
| Realization coverage | 0 | 0 | 0 | 0 | 0 |
| Governing applicability | 0 | 0 | 0 | 0 | 0 |
| Occurrence boundary/scope | 0 | 0 | 0 | 0 | 0 |
| Standalone occurrence identity | 0 | 0 | 0 | 0 | 0 |
| Admission dependencies | 0 | 0 | 0 | 0 | 0 |
| Downstream relationships | 0 | 0 | 0 | 0 | 0 |
| **Total** | **11** | **11** | **29,195** | **3,427** | **2,114** |

Interpretation had nine initial pages and nine embedded runtime-facet decisions; those facets do not add provider calls. The two qualification answers selected two requested-execution units and one property unit. They do **not** constitute a validated contribution set. There are zero committed canonical operation assignments, supporting contributions, governing attachments or data edges.

There were **11 coordinator reservations, 11 durable budget calls, 11 journal requests and 11 verified receipts**. Five of sixteen call slots remain at the stop; admission-completion headroom is not applicable. Output includes reasoning, leaving **1,313 final-answer tokens**. Usage is fully known. There were zero partitions, singleton escalations, semantic repairs, unverifiable dispatches or reservations without journal requests/receipts. Largest estimated input was **5,631** tokens; largest actual input was **4,127**.

The zero identity/dependency counts reflect the early stop, not convergence. Coverage, applicability, dependency and downstream relationship work was not reached. No provider-call savings are inferred from offline fixtures.

## 8. Next gate

**architecture review**

No further live execution is authorized by this result.
