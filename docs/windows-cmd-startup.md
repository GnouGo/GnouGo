# Windows Cmd module loading

The five repeated Windows Cmd failures at `81f7d11` were timeouts before the
filesystem operations ran. Native Windows probes isolated PowerShell module
autoloading under the filtered child environment.

| Control | Result |
| --- | --- |
| Actual filtered Windows PowerShell command | 10-second timeout; no output |
| Text/encoded/file/stdin invocation, console mode, BOM-free encoding, raw pipe closure | Same timeout |
| Additional standard OS variables | Same timeout; approximately 10 CPU seconds |
| Restore inherited `PSModulePath` in a diagnostic control | 313 ms; command succeeds |
| Initialize only the built-in module path | Console write succeeds; ordinary command loading still times out |
| Reapply the built-in path before command loading | 720 ms; command succeeds |
| Explicit built-in module import | 235 ms; command succeeds |
| Disable autoload | Command-not-found error; not a correction |

Windows PowerShell [expands its module path at startup](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_psmodulepath?view=powershell-7.5#windows-powershell-startup).
On the native runner, searching the automatically added locations spins. This
explains why setting a restricted path only in `ProcessStartInfo` was insufficient.
The probes do not identify an internal PowerShell defect beyond that boundary.
EOF and encoding controls did not affect the failure.

The host now initializes and reapplies `<selected shell installation>/Modules`
before executing the allowlisted command when no explicitly allowlisted
`PSModulePath` was supplied. It does not inherit arbitrary module paths or the
full environment. Explicit environment configuration remains authoritative.
The selected shell, supported invocation options, private stdin, immediate EOF,
autoloading, output capture, exit codes, filesystem restrictions, timeouts and
process-tree termination remain unchanged.

[Native probe records](evidence/flow-v9-112/windows-cmd-startup.json) retain six
unsuccessful diagnostic runs, including the insufficient first fix, exact
revisions and job links, shell version, timings, CPU observations and bounded
output. They exclude environment values and credentials. Diagnostic-only test
code and temporary CI probe jobs have been removed; focused regressions remain.
The complete native Windows Cmd suite passed at `cabb4d9`, including all five
original failures and the new launch/termination tests.
Final native suite and published-protocol results are recorded in
[draft PR #113](https://github.com/GnouGo/GnouGo/pull/113).

Regression coverage includes repeated commands, private EOF, Unicode/quotes,
stdout/stderr, nonzero exits, timeout/cancellation tree cleanup, the effective
built-in module path, existing filesystem scripts and the published Native AOT
MCP protocol test. Non-Windows skips are not Windows coverage. No timeout was
increased, failing test skipped, shell substituted or retry introduced.
