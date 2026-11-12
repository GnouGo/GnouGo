# Approved isolated Code toolchain provisioning

The operator requested Node 24.20.0, pnpm 10.34.5 and Python 3.11.13 and explicitly
authorized downloading them. No provider, endpoint, transport, SDK network or
sandbox-bypass permission changes are authorized or made by this experiment.

The [captured host profile](profile.json) records an isolated Node archive verified
against the official SHA256 manifest, pnpm verified against the npm registry's
SHA512 integrity, and the already installed real Python 3.11.13 binary. Executable
links live in the isolated workspace directory; no global installation is changed.
All four host version commands exit zero. This establishes host provisioning only.

The fresh independently admitted experiment `code-toolchain-provisioned-20261112-02`
tests whether that real PATH reaches the unchanged SDK terminal. Its committed
[preparation](prepare.py) derives from the previous captured probe without editing
production readiness code. It captures the trusted environment profile alongside
existing MCP build/configuration hashes. The [read-only script](probe.py) requests
no install or network, executes once, and reports actual versions. The experiment
requires matching terminal/file evidence, an unchanged script, cold receipt reload
and cleanup. The completed `code-toolchain-environment-20261112-01` stays immutable.

No paid dispatch has occurred at preparation. The SDK version result, financial
settlement and any remaining prerequisites will be recorded separately. Matching
versions do not establish offline dependency installation, repository checks,
an accepted complete Code plan, execution or an independent workflow-oracle pass.

Read-only retrieval of the pinned SmartGuide manifest through the connected GitHub
app returned 404; the existing CLI route to the same API timed out. Neither result
justifies changing network routing, authentication or the pinned repository.
