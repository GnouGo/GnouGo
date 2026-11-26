# Local review of SlimPlanet/SlimFaas #519

**Verdict — request changes.** The smoke test can report success and leave the disposable cluster running. A cleanup regression also passes without reaching the failure it was written to exercise. [Pull request](https://github.com/SlimPlanet/SlimFaas/pull/519)

Reviewed head `458efb736ad6f4159281606da573d51c5115256c` against base `bc2213576e1f29f72f5dd2fcb50a6c22ac6a84e1`. The local-only benchmark run `win-local-pr519-20261126-174339` used `OpenAi / gpt-6.1-terra` to review all eight changed files. It matched eight observations to exact added source lines and rechecked the target head and base. The two findings below were reproduced separately against the pinned Python source. GitHub access for this review was read-only.

### Cluster deletion failure leaves the smoke test green

The cleanup block calls `kind delete cluster` with `check=False` and discards its return value. In a fixture with successful preceding checks and a simulated deletion exit code of 1, the actual `main()` returned normally. CI can therefore report a pass while leaving resources behind. Capture the deletion result and surface a cleanup failure. When the policy checks already failed, preserve their error alongside the cleanup failure. Add a deletion-failure case to the cleanup suite. [Smoke test, lines 166–170](https://github.com/SlimPlanet/SlimFaas/blob/458efb736ad6f4159281606da573d51c5115256c/.bin/test-annotation-policy.py#L166-L170)

### The creation-failure regression can pass before creation

The test helper accepts any of three exception classes. The creation-failure test then checks only that deletion was never called. In a focused run, an unrelated `SystemExit` before the first subprocess call satisfied the test. Assert that the expected create command ran and raised its specific `CalledProcessError`, then check the absence of deletion. [Test helper, lines 39–46](https://github.com/SlimPlanet/SlimFaas/blob/458efb736ad6f4159281606da573d51c5115256c/.bin/test-annotation-policy-cleanup.py#L39-L46) · [Creation-failure case, lines 53–57](https://github.com/SlimPlanet/SlimFaas/blob/458efb736ad6f4159281606da573d51c5115256c/.bin/test-annotation-policy-cleanup.py#L53-L57)

### Checks

| Evidence | Result |
| --- | --- |
| Windows, `python .bin/test-annotation-policy-cleanup.py` | All four existing tests passed. |
| Windows, two focused Python fixtures | Both findings reproduced with subprocesses mocked. No cluster was created. |
| Windows, `src/SlimFaasSite` documentation tests and TypeScript check | All 12 documentation tests passed. The type checker exited 0. |
| Windows, `git diff --check` between the reviewed commits | Exit 0. |
| GitHub Actions on the exact head | The admission-policy and documentation jobs passed. |

The Windows host lacked `kind` and a container runtime. The live cluster result comes from the PR's CI. [Admission-policy job](https://github.com/SlimPlanet/SlimFaas/actions/runs/45866717917/job/140071318590) · [Documentation build](https://github.com/SlimPlanet/SlimFaas/actions/runs/45866721415/job/140071332507)
