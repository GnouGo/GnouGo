# Fresh-session profile assertion follow-up

The full solution on 8371eed1 reached existing local business cases whose setup
still requested/asserted v9. Fresh session creation correctly selects the current
approval-fingerprinted v10 profile. These cases stopped at the stale assertion,
before their execution oracle. The fixture now requests and asserts v10 explicitly;
historical compilation is still compared through its existing pinned APIs and
retained replay. This is a two-line test correction, with no production change.

All 18 local business variants pass after the correction: normal, repeated, empty,
unknown IDs, incomplete acquisition, policy denial, deliberately fabricated cells,
verbatim blocks, missing descriptions, explicit producer revision and the compact
union composition. The negative oracles retain their original expectations.
The full solution reruns on 5a8afbfc; interrupted superseded runs are not counted
as passing gates. Their known failure is retained here; no historical live run
was resumed. Published checks on 8371eed1 cover the identical production tree.
