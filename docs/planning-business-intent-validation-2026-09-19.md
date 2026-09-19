# Business intent refactor validation

Baseline planner: `95c3b30`. Measurement harness: `f71a731`. Model configuration: OpenAI `gpt-5.5-2026-04-24`, medium reasoning, 96,000 input / 32,768 output tokens, eight calls and two repairs per session. External effects are mocked. The French request is frozen verbatim in the benchmark corpus.

The baseline campaign stopped after two requests. Local calculation reached the output limit (5,387 input tokens, 32,768 output tokens, EUR 0.8813045375 recorded). Read/transform ended without a durable completion receipt; its tokens and cost remain unknown. Neither reached FinalReview. Both used one reserved call and no repairs. Initial request sizes, including response schemas, were 25,573 and 25,604 bytes respectively.

The second request was not resent. The encrypted campaign ledger blocks further dispatch while that reservation is uncertain. Consequently the planned three repetitions per case and before/after live reliability comparison are incomplete. Known spend excludes the uncertain request; it must not be interpreted as the total campaign cost. Neither earlier stopped live planning session nor its accounting was modified.

The refactor introduces business operation variants, deterministic control-flow lowering and contract inference, separate literal scenario fixtures, and schema-7 storage. Execution regressions cover nested branches, parallel iteration, subflows, typed transformations, defaults/nulls, artifact relationships, confirmation rejection, finalizers, approval, restart and revision import. Offline fixtures are execution tests, not real-model reliability evidence.

Further evaluation and release results will be appended after the remaining refactoring steps.

The first implementation step was committed as `a553b00`. All 844 Flow, 43 planner, 66 integration and 314 Agent.Server tests passed. The three offline smoke workflows reached FinalReview and passed independent execution with one call and no repairs each. Their initial request sizes were 10,977, 11,008 and 11,034 bytes. The first two are approximately 57% smaller than the corresponding baseline requests; these are request-size measurements, not live-model success measurements.

The next step replaces whole-plan repair with issued local targets, introduces bounded capability retrieval, reflects business choices into intent, and requests literal fixtures only when deterministic sampling fails. Current host policy is revalidated independently of stored approval. Regression tests cover invalid selections/targets, singleton calls, full-catalog retrieval misses, scope-aware locations, incomplete fixture sampling and unchanged corrections.
