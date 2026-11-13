# Approved owning-Browser observed entry scope

The operator authorized the exact AAX proposal at `f207d8b5`, including its two
optional trusted host configuration fields. This stage implements the generic
Browser capability only. The independently versioned v10/v14 workflow remains a
separate integration step; no old verifier or stopped session is modified.

Only explicit reads of exact fragment-free hrefs from a complete successful owned
source receive additional entry authority. The existing route/host settings are
unchanged. Each redirect is checked before contact; an unseen redirect is denied.
Closure, disconnect, new source navigation and failed reads expire the authority.
A generation token prevents an older acquisition from restoring expired entries.
The implementation contains no Amazon rules or product classification.

## Controlled evidence

The semantic red run against the original host passed 11 and failed 5 of 16 cases:
valid exact reads were denied and incomplete trusted configuration was accepted.
Dependency restore failures preceding that run are not counted as semantic reds.
The implementation passes all **215 Browser tests**, including 18 new cases,
Release, `--no-restore -m:1 -warnaserror`, zero skips. They cover separate hosts,
repeated reads, per-instance isolation, changed URL/query/fragment/path, source
expiry, failed/partial/selector captures, interaction denial, unseen redirects and
publication racing a new source navigation. No model or paid workflow was run.

Private logs are retained under `artifacts/pr120-approved-20261112/` as
`browser-observed-red.log`, `browser-observed-green.log` and `browser-all.log`.
These controlled tests do not establish actual-profile source qualification,
an accepted TaskPlan, execution, workbook output or an independent business pass.
