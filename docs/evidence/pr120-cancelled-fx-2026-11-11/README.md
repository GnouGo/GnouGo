# Independent cancellation reservation estimates

At frozen HEAD `39d567bb473c8cbd37d072f069ae6302f9f22c1c`,
`amazon-v12-qualification-20261111-03` was cancelled through the existing API at
revision 76 after repeated unproductive implementation reselections. Its 30
physical responses completed with known usage. Two planning checkpoints were
accepted; no global review, approval, business execution or independent business
oracle was reached. The cancelled identity is not resumed.

The last issued request, call 31 of session
`25048d22727046a5aaea6f80789648b3`, has an empty campaign attempt journal and
an `OperationCanceledException` dispatch record. The Server retains its exact
request, reserved index entry and application hold, without transport or receipt.
These observations do not establish whether a provider was contacted.

The existing zero-attempt retention inspector rejects this evidence because the
campaign estimate is EUR **8.018987908643081056874160322**, while the exact
Server hold is EUR **8.039471555675384200593151793**. The campaign captured an
ECB quote of **0.8956560680698612**, dated November 6; the Server captured
**0.8987148377819717803540936461**, dated November 11. Both use the unchanged
configured providers. The captured-transport matcher returns true. The comparison
incorrectly requires one independent estimate to cover the other even though this
path retains the whole Server liability separately from campaign HTTP accounting.

The narrow correction removes only that numerical comparison. All existing
ownership, cancellation, lease, exact-request, input/output bounds, positive
EUR hold, quote, empty-attempt, absent-receipt/transport, unchanged deployment,
other-liability and evidence-hash checks remain. The original journals and
entire Server hold remain unchanged. Retention forbids replay/continuation and
releases no money. Physical-attempt admission is unchanged.

Two added controlled cases (interrupted and cancelled) first fail at the real
comparison when the Server hold exceeds the campaign estimate. After correction,
all **250** retention tests pass in Release with `-warnaserror`, no skips. The
cases verify cold restoration, unchanged original budget and campaign journals,
exact full external liability, no duplicate model dispatch or charging, and
refusal of understated or additional unverified external holds. Existing negative
ownership, received-evidence, altered-budget and execution cases remain covered.

At this commit, this is a tested inspector correction. Actual retention still
requires a fresh successful preview and its exact evidence-bound application.
It establishes neither a provider receipt nor workflow execution.
