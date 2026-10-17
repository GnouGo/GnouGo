# Gate 3: join the SDK execution scope before fixture teardown

The logical partial-result checkpoint precedes terminal Tasks SDK bookkeeping.
The existing fixture could dispose its task store and delete storage in that gap.
A test-only scoped disposable now marks completion of the actual SDK execution
scope; teardown joins all registered owners before disposing transports, the
session manager, task store, services or storage. No production lifecycle change,
SDK fork, deletion retry, delay or timeout increase is involved.

Controlled barriers stop success, cancellation and failure after logical checkpoint
publication and before terminal bookkeeping. All three prove that teardown remains
pending and storage remains readable until the barrier is released and the scope
is disposed. Cancellation still retains partial evidence without claiming verified
completion. The original unknown-completion test still prohibits cleanup/replay.

All 59 owning attachment/Tasks checks pass with -warnaserror. This establishes the
ordering with synchronization rather than relying on a lucky cancellation rerun.
Full-suite verification remains required after the final guidance gate. Provider
and external MCP calls: zero.
