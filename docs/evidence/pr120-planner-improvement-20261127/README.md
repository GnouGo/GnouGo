# Planner cost and normal Server qualification, 27 November 2026

The baseline ended at `f4140fcc` with 665 physical attempts in 15 planning sessions and no business execution. Drafting, discovery and block implementation accounted for 506 calls. The new qualification uses the same SlimFaas revisions and Amazon selection rules. It starts in the normal Server, records actual provider exchanges and opens the generated workflow through the authenticated form.

## Recorded normal Server runs

Each row is one fresh session. The linked receipt breaks planning down by phase. Business cost includes measured usage. Outstanding reservations are listed separately in the campaign ledger.

| Session | Planning calls, logical / physical | Planning tokens, input / output | Planning cost | Business cost | Elapsed | Business outcome |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| [SlimFaas 1](server-code-1.json) | 25 / 25 | 295,624 / 13,517 | €0.734984 | €0.000000 | 14m 15s | Four upstream tests passed. The provider rejected the first SDK call. |
| [Amazon 1](server-amazon-1.json) | 1 / 0 | 0 / 0 | €0.000000 | — | 0m 50s | The first planning request stopped on KeyVault access. Its full reservation is retained. |
| [SlimFaas 2](server-code-2.json) | 1 / 1 | 1,780 / 4,096 | €0.022715 | — | 3m 23s | The initial structured response stopped making progress. |
| [Amazon 2](server-amazon-2.json) | 46 / 46 | 829,792 / 28,777 | €2.021289 | — | 30m 09s | Business review stopped on the failure branch and row validation. |
| [SlimFaas 3](server-code-3.json) | 32 / 32 | 287,063 / 9,771 | €0.698417 | €0.105210 | 21m 57s | Two observed changed-file reads. The SDK wait interrupted execution. |
| [SlimFaas 4](server-code-4.json) | 13 / 13 | 117,771 / 7,085 | €0.300549 | €0.255221 | 24m 00s | Eight changed files read. The SDK token budget stopped the workflow before delivery. |
| [Amazon 3](server-amazon-3.json) | 41 / 41 | 692,595 / 20,496 | €1.671041 | — | 27m 43s | The operator stopped at the request for lossless XLSX readback. |
| [SlimFaas 5](server-code-5.json) | 16 / 16 | 231,961 / 12,507 | €0.585364 | €0.291021 | 20m 25s | Passed. Eight observed changed files, verified local JSON, four upstream tests and confirmed cleanup. |
| [Amazon 4](server-amazon-4.json) | 22 / 22 | 297,027 / 13,205 | €0.736759 | — | 13m 36s | The Server stopped on a nullable source flag before business execution. |

## Planner changes

The host now assembles a binding when accepted producers agree on the same explicit reference. It resolves a declared normal-root public output from its original operation and port and follows whole-object forwarding through value tasks to propagate the public output schema into an agent task. Conflicting bindings and ambiguous commitments stay open for review. The builder retains accepted blocks and applies edits only to the diagnosed scope.

An issued discovery menu can include a small directory of operation names. Names are accepted only when they identify a unique issued operation in the matching catalog and source policy. The full versioned contracts remain available for inspection. Required evidence takes precedence when an optional directory does not fit. New session limits also compact repeated authoring navigation metadata. Responses which end in a long run of structural whitespace after the provider reports truncation receive a distinct failure diagnosis.

The qualification profile limits each planning session to 48 logical and physical calls, 3.2 million tokens, one hour of active planning, and €3.50. Physical admission preserves a further €15 for execution and verification. The campaign's total ceiling is €70 under the original immutable deployment configuration. Its opening ledger carried €25.602182 in measured usage. The €40 extension has a single recorded authorization.

## Replays

Both captured candidates passed the full local compilation and workflow validation path with zero outstanding diagnostics. The SlimFaas candidate asserts successful deletion in the finalization scope. The Amazon candidate keeps the original observation position and checks the exact observed href before a visit. It fixes duplicate task IDs, the workbook row matrix and the common branch output. Its workbook readback checks the complete expected table. Full source, candidate and compiled-YAML hashes are in [the replay results](full-replays.json).

Replaying 94 archived request contexts reduced the serialized context in 65 of them, from 6,037,440 to 6,012,525 bytes overall (0.41%). That experiment excludes response schemas and transport framing. It supplies no live token or cost estimate. Three captured incomplete outputs ended in 13,601, 33,708 and 34,695 structural whitespace characters. [Capture analysis](capture-projections.json)

## Normal Server runs

The first new code session reached final review after 25 calls and one scoped repair. The previous comparison session used 32 calls and stopped before execution. Measured input fell from 351,056 to 295,624 tokens, output from 14,822 to 13,517, and planning cost from €0.867198 to €0.734984. These are two individual sessions; the fresh run used 21.9% fewer calls and cost 15.2% less for planning.

The workflow cloned [SlimFaas PR 519](https://github.com/SlimPlanet/SlimFaas/pull/519), confirmed the requested revisions, compared eight changed files and ran the four approved upstream tests successfully. A first read-only SDK inference with four native tools and medium reasoning received HTTP 400 before a file read. The provider returned no usage. The original execution remains in reconciliation, with €0.237339 reserved, no local review report and no workflow cleanup receipt. The physical request SHA-256 is `c2f5a746fcad2804668dee5eb756c088c1f9c9720fff802a39162f590914254e`. [Phase measurements, tool receipts and independent verdict](server-code-1.json)

The first new Amazon session stopped while issuing its initial planning request. The Server recorded a KeyVault access failure and reserved €0.046355. Inspection found no physical campaign attempt, search, product visit or workbook. [Run evidence](server-amazon-1.json)
The operator closed that individual request as inconclusive after the original KeyVault failure span, application reservation and absence of campaign dispatch records matched. The campaign carries the full €0.046355 reservation separately. The original request and session cannot be replayed. [Reconciliation evidence](server-amazon-1.json)

A separate command-readiness probe stopped before inference when the host could not enforce the required command isolation. It issued zero provider attempts. The subsequent normal code session captured the provider-suggested setting for tool calls but stopped during planning, before the code runner started. Its first structured response consumed 4,096 output tokens. The original wire text contained a 645-character prefix followed by 14,996 structural whitespace characters. Passing that exact body through the corrected OpenAI parser and planner classified the stalled generation with zero new model calls. [Command probe](sdk-command-probe.json) · [Second normal review](server-code-2.json) · [Original-response replay](structured-chat-replay.json)

The second Amazon session constructed the ordered search, exact original-href lookup, sequential visit loop, workbook write and independent read. The compiler caught a flat workbook input, and a scoped repair produced a matrix. Business review still found that an href mismatch could abort without returning the actual reason and that an eligible row could have the wrong number of cells. It stopped at 46 calls after a scoped operator revision. The run took 1,809 seconds and planning cost €2.021289. It received no execution approval, made zero business visits and produced no workbook. [Phase measurements and verdict](server-amazon-2.json)

The third normal SlimFaas session reached final review in 32 planning calls, using 287,063 input tokens and 9,771 output tokens at €0.698417. The typed form invoked the approved revision. Git verified the pinned revisions, returned all eight changed paths, and the four upstream tests passed. The direct SDK made four measured model requests (€0.105210). Independent readback of its durable tool trace confirms successful reads of `.bin/test-annotation-policy-cleanup.py` and `.bin/test-annotation-policy.py`. Other read requests were denied or still pending at interruption. The SDK stopped after 243 seconds under its internal 240-second wait, although the approved task allowed 600 seconds. The workflow journal records `needs_reconciliation`. The preserved checkout exists, the report file is absent, and cleanup is incomplete. The qualification observer also rejected a permission answer that arrived after the task ended. The terminal timestamp and status come from independent journal readback. [Third normal review](server-code-3.json) · [Navigation settings](code-navigation-low.json)

The read-only transport probe made two measured calls with native Chat tools and reasoning set to `none`. It reached the SDK's project-read permission gate and recorded a denial. The probe supplied no file-read evidence. [SDK probe](sdk-read-none.json)

The Windows x64 self-contained, partially trimmed Server at `fdbba759` passed its planning persistence and receipt-recovery smoke. Seven dependency warnings were recorded, six from Entity Framework Core experimental features and one IL2101 from ASP.NET Core component metadata. The executable and participating assembly hashes are recorded with the result. [Published Server](windows-published-server.json) · [Test receipts](validation.json)

Local replay at `4cb2c264` shared repeated declarations inside retained checkpoints. In 46 archived Amazon requests this reduced 2,659,640 serialized context bytes to 2,286,926 (14.0%). All 60 references across six construction-implementation requests reconstructed their exact original task, including nested controls and argument values. The replay made zero provider calls. [Checkpoint projection](checkpoint-projection.json)

Both original corrected candidates passed complete compilation and validation again on the same frozen planner binaries as the fourth review trial. [Revalidated candidates and binary hashes](revalidated-candidates.json)

The fourth SlimFaas session reached its first final review in 11 calls. The reviewer found that the generated clone used the default depth of one although the pinned base is five commits behind the head. An ordinary Server revision changed only the clone inputs at the existing task. Inspection confirmed that every other task, all requirements and the public report binding were preserved. The revised artifact reached final review at 13 calls, 117,771 input tokens and 7,085 output tokens for €0.300549. The preceding 32-call review session cost €0.698417 for planning. These figures compare two measured runs with the same business requirements.

The Windows x64 self-contained Server at `4cb2c264` also passed the persistence and receipt-recovery smoke. The publish and smoke took 813 seconds and emitted seven dependency warnings. Windows targeted Server tests passed 225/225 and the complete Copilot MCP suite passed 265/265 on the same revision. [Published executable and assembly hashes](windows-published-4cb2c264.json) · [Test receipts](validation.json)

The fourth workflow ran through the typed form, compared eight paths and passed all four upstream tests. Independent readback of the encrypted SDK trace confirms 29 successful tools, including reads of all eight changed files. The direct SDK used four measured calls (€0.255221). Its reservations reached 484,160 tokens. The next request needed 194,179 input tokens with 115,840 remaining. The workflow entered reconciliation, retained the checkout and produced no report. The normal verifier consequently returned a failing verdict. [Fourth workflow, costs, receipts and file coverage](server-code-4.json)

Replaying the four retained native Chat request bodies through the revised inference admission code preserved every tool result. In the first three calls the wire remained equivalent. On the fourth, with 26 tool observations in context, the host selected a final answer within the existing allowance. The replay exercised admission only and made zero provider calls. [Native final-turn replay](sdk-native-final-replay.json)

All 3,362 tests in the complete planning suite passed on `4cb2c264`. [Validation receipts](validation.json)

The third Amazon session used 41 planning calls, 692,595 input tokens and 20,496 output tokens for €1.671041. It produced the search and prefix-selection tasks, then asked for a reader that preserved worksheet cells and their positions. The frozen document tool supplied rendered text. The operator stopped this session at its clarification. It made no product visits and produced no workbook. [Third Amazon session](server-amazon-3.json)

The existing Document MCP now accepts `document_read(format=cells)`. It opens the stored workbook and returns an ordered matrix for each worksheet, preserving strings, nulls, line breaks and sparse coordinates. An excessive matrix returns an explicit error. Windows passed all 76 Document MCP tests including real stdio write and readback, and an independent Open XML inspection. The retail verifier and its ten-visit criteria are unchanged. [Document MCP](../../../src/GnOuGo.Document.Mcp/README.md) · [Validation receipts](validation.json)

At `e219a246`, the Windows self-contained Server passed its persistence and receipt-recovery smoke in 644 seconds with seven dependency warnings. The complete Copilot Core and Copilot MCP suites passed 179/179 and 265/265. The review qualification tests passed 43/43. At `afe16fbd`, the Document MCP Release suite passed 76/76 with compiler warnings treated as errors. [Windows Server binary and smoke](windows-published-e219a246.json) · [Validation receipts](validation.json)

Both original corrected plans passed complete compilation and runtime validation again on the `afe16fbd` Server and planner binaries, with zero diagnostics. The generated YAML was byte-identical to the previous full replays. [Source, binary hashes and candidate hashes](revalidated-afe16fbd.json)

The Windows x64 native Document MCP at `5e9be843` also passed the exact-cell stdio test against the published executable. Its first publish emitted two nullable-reference diagnostics. `afe16fbd` adds explicit checks for absent workbook and worksheet data. [Native executable and transport evidence](windows-document-native-aot.json)

At `c34e376a`, the published Document MCP read and write schemas explicitly allow both strings and nulls in the cell matrix. The earlier native build at `afe16fbd` compiled without warnings; the newer schema assertion failed against that older executable. The latest Windows x64 Native AOT publish completed without warnings in 252 seconds, and the real stdio test against that executable passed both schema assertions and the exact-cell workbook round trip. The complete workflow Copilot integration suite also passed 13/13 at `afe16fbd`. [Published Document MCP and stdio receipt](windows-document-native-aot-c34e376a.json) · [Intermediate native build](windows-document-native-aot-afe16fbd.json) · [Test receipts](validation.json)

The fifth SlimFaas session passed the normal Server oracle on `afe16fbd`. The Server repaired its generated write guard to recognize the SDK's `completed` status. The approved plan reached final review in 16 calls, 231,961 input tokens and 12,507 output tokens for €0.585364. The typed form opened the approved revision and the workflow cloned the exact head with full ancestry. It compared the pinned base and head, returned eight changed files and passed all four upstream unit tests. The direct SDK completed in five model calls and 29 actions. It read all eight changed files and returned zero findings with two explicit limitations. The original report remains in the local run workspace. Independent readback confirmed the JSON file equals the public report, the finalizer succeeded, and the disposable checkout is absent. [Full phase measurements](server-code-5.json) · [Independent report verification and local artifact path](slimfaas-review-verification.json)

The successful run cost €0.876385 in measured usage, including €0.291021 for the direct SDK. The fifth native request retained 29 tool messages and selected a final answer within the original allowance. The encrypted physical ledger measured 126,102 SDK input and 828 output tokens. The runner conservatively reserved 565,451 tokens over five calls. [Verified native request hashes](slimfaas-review-verification.json) · [Planning, SDK and deterministic operation timings](server-code-5.json)

The full Windows Server suite at `fce11263` finished with 2,747 passing, 33 failing and three skipped tests. The failing cases include workbook budget stops, missing records and timed host operations. The linked report lists every failing case and the TRX hash. [Broad test status and failed cases](broad-windows-validation.json)

Both captured candidate plans were revalidated against the frozen `c34e376a` planner and Server before the next normal Amazon session. The workflow compiler returned zero diagnostics. [Source and binary hashes](revalidated-c34e376a.json)

The cold campaign ledger immediately after the successful SlimFaas run records €32.307920 measured, €0.237339 reserved for the SDK attempt with unknown usage and €0.046355 held for the closed pre-dispatch request. The available balance before Amazon session four was €37.408385. [SlimFaas closing ledger](server-code-5.json)

The fourth Amazon session stopped after 22 measured planning calls. Three scoped repairs used the nullable Browser source flag directly in a Boolean guard. The compiler rejected all three. The search and product visits never ran, and the workspace contains no workbook. [Fourth Amazon session and phase measurements](server-amazon-4.json)

At `8a0f10c0`, the compiler identifies the exact nullable operand in a compound guard and shows its type. Only that operand contributes conditional-branch evidence to the diagnostic. Replaying the three actual revision patches recovered the same error and named the same Browser field each time. Comparing this field to literal true cleared the diagnostic in each partial candidate. The unbuilt tasks remain outside that replay. Both complete original candidates separately passed the full validation path with byte-identical YAML. The 17 first-products verifier tests passed on the same frozen assemblies. [Captured repair replays](nullable-guard-replay.json) · [Both full candidate validations and binary hashes](revalidated-8a0f10c0.json) · [Verifier tests](validation.json)

The Windows x64 self-contained Server at `8a0f10c0` passed its published planning persistence and receipt-recovery smoke. Its build recorded 7 dependency warnings. [Executable and assembly hashes](windows-published-8a0f10c0.json)

The complete planner suite at `8a0f10c0` passed all 3,363 tests in 19 minutes 33 seconds. [Validation receipts](validation.json)

After Amazon session four, the campaign ledger contains €33.044679 in measured usage, €0.237339 in reserved provider usage and €0.046355 in retained application reservations. The available balance before session five was €36.671626. [Audited campaign balance](funding.json)
