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

The [completed SDK result](outcome.json) verifies all requested versions, exactly
one `python3 probe.py`, matching terminal/file output, an unchanged script, durable
receipt reload and cleanup. It uses the SDK05 provider fingerprint and actual
`gnougo-structured-tools-v1` receipt transport. Three actual provider HTTP 200 JSON
responses end with `finish_reason=stop`; all usage is bounded. Existing SDK capture
has no per-HTTP start/end timestamps, so none are invented. Execution plus local
verification takes 207,244 ms; the separate campaign-wide evidence digest is not
reported as SDK execution time. The [financial audit](financial.json) retains every
historical hold and all six grants, with no supplement or reset.

Matching versions do not establish offline dependency installation, repository
checks, an accepted complete Code plan or an independent workflow-oracle pass.

Read-only retrieval of the pinned SmartGuide manifest through the connected GitHub
app returned 404; the existing CLI route to the same API timed out. Neither result
justifies changing network routing, authentication or the pinned repository.
Normal read-only Git smart HTTP subsequently succeeds in 1.363 seconds, without
cloning or changing network configuration. The API failure therefore does not
establish a general Git outage. Direct reads of the two pinned lockfiles return
404; no dependency cache or installation success is inferred from these probes.
