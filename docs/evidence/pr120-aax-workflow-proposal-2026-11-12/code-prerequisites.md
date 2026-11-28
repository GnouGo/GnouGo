# Code execution prerequisites

SDK05 remains the positive reference: KeyVault OpenAi,
`gnougo-structured-tools-v1`, one real command, durable receipt reload and cleanup.
The representative Code24 planning probe also passes continuation and complete
technical validation. Neither establishes full Code execution/oracle success.

The read-only host check records Node `22.20.0`, pnpm `10.14.0`, no `python`
executable, and Python3 `3.14.4`. The effective configured MCP has no PATH override
and matches the parent path; its configured SDK transport remains
`gnougo-structured-tools-v1`. These are host/MCP-environment observations, not
SDK terminal receipts. No new SDK/model call is made.

The pinned SmartGuide checkout/oracle requires Node `24.20.0`, pnpm `10.34.5`
and Python `3.11.13`, actual dependency installation and its declared checks.
The exact releases exist: [Node checksums](https://nodejs.org/download/release/v24.20.0/SHASUMS256.txt),
[pnpm release](https://github.com/pnpm/pnpm/releases/tag/v10.34.5),
[Python release](https://www.python.org/downloads/release/python-31113/).
Their availability does not prove that the sandbox can acquire them.

The existing [bounded SDK sandbox](../../../src/GnOuGo.GithubCopilot.Core/GitHubCopilotSdkAdapter.cs)
sets `AllowOutbound = false`, `AllowLocalNetwork = false`, `AllowBypass = false`
and confines writes to the approved project. Its permissions remain unchanged.
The observed managed Python installations contain 3.10.18, 3.11.14 and 3.12.11,
not the required 3.11.13. No aliases, fake version outputs, global installations,
network workaround or weaker oracle is introduced.

A subsequent read-only check finds real Python `3.11.13` at
`/opt/homebrew/opt/python@3.11/bin/python3.11` and its existing
`libexec/bin/python` executable. This narrows the missing-toolchain finding:
Python is installed outside the effective MCP PATH; Node `24.20.0` and pnpm
`10.34.5` are not found in the standard installations/caches checked. No PATH or
saved profile is changed, and these host observations still are not SDK receipts.

Before funding another complete Code run, identify/provision an approved offline
toolchain and dependency environment accessible within the existing confinement.
Any required environment/configuration amendment must be explicit and immutable;
the provider/endpoint and working structured SDK transport must remain unchanged.
Then obtain actual SDK terminal evidence for the pinned versions and dependency
commands. Unknown or failed prerequisites remain truthful failures. Code23's
unreconciled old task/clone remains untouched.
