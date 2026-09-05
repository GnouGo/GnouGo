# Planner acceptance corpus, version 2

The corpus contains twenty supported request definitions and eight negative scenario
families. The artifact and assembly cases preserve the failure classes found in the
September 4–5 traces without copying user prompts, paths, outputs or credentials.
The language and renamed-catalog cases test equivalent contracts independently of
surface identifiers. Keep producer metadata identical when renaming identifiers.

Run both planners three times per supported case with the **same configured model**,
frozen MCP catalog, policies, budgets, input values and scripted clarification answers.
Use deterministic fake integrations for executable validation. Scenario checks are
evidence about simulated contract behavior; independent intent checks still need an
assertion or reviewer verdict. A model's own score is not an evaluation result.

This repository does not include fabricated live benchmark results. The request corpus
and comparison command define the acceptance experiment; real provider runs require a
configured model and a catalog supporting the stated operations. Do not use unrelated
historical traces as a matched baseline. The existing live-generation tests document
their dedicated provider-project and shared budget prerequisites in Agent.Server's README.

Write one JSON object per run in each version's JSONL file:

```json
{"caseId":"greeting","repetition":1,"plannerVersion":2,"promptHash":"sha256-of-the-exact-corpus-prompt","model":"configured-model","catalogFingerprint":"frozen-catalog-hash","policyFingerprint":"policy-hash","budgetFingerprint":"budget-hash","answersFingerprint":"scripted-answers-hash","outcome":"generated","activeMilliseconds":1000,"inputTokens":500,"unsupportedOperationAccepted":false,"unapprovedRelaxationAccepted":false,"checks":{"typed_input":"passed","correct_output":"passed"}}
```

The numbers above illustrate the format; they are **not measurements**. Active time
excludes human waits. Input tokens come from durable model receipts, once per request.
Keep measurement files content-free; store detailed private evidence in encrypted sessions.

```sh
python3 scripts/evaluate_planning_benchmarks.py --v1 baseline.jsonl --v2 candidate.jsonl
python3 -m unittest discover -s tests/planning_benchmarks -v
```

The comparator rejects incomplete cohorts, duplicate submissions, different prompts,
unmatched configurations, missing checks, and invalid counters. Required inconclusive
checks are not successes. The quality gate is at least 95%; the target ratios are at
most 0.75 for median active time and 0.70 for median input tokens. Performance is reported
on paired successful runs, requiring 95% paired coverage, so early failures cannot make
a planner appear faster. Safety failures always reject acceptance.

These benchmark gates supplement component regressions, negative scenarios, frontend
builds and publish smoke checks. Keep `TypedWorkflowPlanning:PlannerVersion=1` until
the complete release gates pass; explicit `/planning` sessions use version 2 throughout.
