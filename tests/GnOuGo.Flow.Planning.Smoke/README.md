# Typed planner Native AOT smoke

This executable references the publishable planning package, serializes and restores
a typed session using its generated JSON context, exports and imports YAML, compiles
it, and executes its expressions without live provider calls. It also advances a full
typed planning session through behavior review, deterministic skeletons and exact
hole assignments,
scenario validation and exact revision approval, restoring serialized checkpoints
between phases using schema 5. The runtime integration also persists a reserved request, budget,
and completed receipt in a temporary encrypted KeyVault database, reopens the session,
and verifies that replay makes no extra model call. Temporary storage is removed afterward.
The published integration adapter also resolves declared local model capabilities
without registered transports and preserves unknown fields on partial declarations.
Typed business decisions, omission applicability, choice labels and preference evidence
also round-trip through the published source-generated serializer.
Canonical declaration assignments, declaration/presence evidence, exact names and omission-default references round-trip. The synthetic preparation transport exercises neutral declaration adjudication before behavior assembly. An encrypted restart between completed root pages and dependent attachments verifies output-member attachment, zero duplicate root dispatches, unchanged repair accounting and final proof replay.
Scoped confirmation subjects, permission operation IDs and default applicability
round-trip through that same Schema-5 serializer.
The encrypted declaration checkpoint uses attachment-only `declaration_constraint`
evidence for an output member. Restart resumes its attachment without another root
request, semantic correction or repair charge.

It also creates an ephemeral certificate, rejects its untrusted chain, and validates
it with an explicit in-memory trust root. No certificate is installed and certificate
downloads are disabled. This exercises native cryptography after publication.
The macOS desktop CI matrix runs this smoke for both architectures.

For .NET 10 macOS Release AOT publication, the shared
[Darwin target](../../build/GnOuGo.DarwinNativeCrypto.targets) copies only
`libSystem.Security.Cryptography.Native.Apple.a` into the project's intermediate
directory and removes that copy's debug information with `strip -S`. Distributed
runtime packs (verified for 10.0.3, 10.0.8 and 10.0.10) embed unavailable Clang
module-cache references; see [dotnet/runtime#124336](https://github.com/dotnet/runtime/issues/124336).
Executable code and link symbols are retained; NuGet cache files and other debug
information are unchanged. This is a publish-only framework workaround, with no
diagnostic suppression. Re-audit it when updating the runtime package.

```sh
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -o /tmp/planning-smoke
/tmp/planning-smoke/GnOuGo.Flow.Planning.Smoke
```

The publish uses the existing Flow.Cli exception for **Jint 4.16.0**. Its IL2104 and
IL3053 package summaries cover CLR reflection/interoperability paths. Four IL2026
sites are `Options.Apply`, its generated namespace-loading lambda,
`DefaultObjectConverter.ConvertSystemTextJsonValue`, and
`DefaultTypeConverter.BuildDelegate`. Flow's sandbox does not enable CLR namespaces,
converts JSON explicitly, and binds statically referenced delegates. The smoke
executes the actual published expression dispatcher and data conversion paths.
These suppressions apply only to this executable's publish; the planning package's
source analyzers remain enabled. No new planning-code warning is waived.

Audit dependency warnings explicitly after upgrades:

```sh
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 \
  -p:AuditKnownTrimWarnings=true -p:TrimmerSingleWarn=false
```

Re-audit the exception if the Jint version or sandbox binding implementation changes.

The duplicate-root fixture persists a targeted correction separately from the initial
root pages. Encrypted restart reuses its receipt, original decision lineage and one
repair reservation, then attaches the alias to a name/scope/direction-derived
canonical ID. A second restart dispatches nothing. Both fixtures run in the published
Native AOT binary, using synthetic transports rather than historical model receipts.

The operation fixture first parses three identical copies of each preliminary runtime
assignment into one normalized evidence item, then persists completed canonical contribution pages before admission
commits. The synthetic outputless action carries explicit owned invocation-boundary evidence. Encrypted restart reuses the realization receipts, attaches a governing rule to the
single canonical operation, and verifies a second restart adds no
calls or repair charges. The first action has unspecified necessity, the reused action has explicit required evidence, and the governing rule has unspecified necessity. Restart verifies one required canonical operation and its evidence. Canonical contribution proof 5 supplies support/unbound-property authority, independently of historical runtime labels. Occurrence-boundary proof 1, realization coverage proof 3, governing applicability proof 1, effect proof 7 and operation proof 15 remain in Schema-5 storage.

The admission dependency smoke journals synthetic, evidence-bound data decisions
between two established operations, closes before atomic admission, then reopens
twice. Canonical edges, per-edge origins and fingerprints survive encrypted
persistence without another request or a repair charge.

The coverage proof is encrypted with the existing admission record. Aggregate executable support and separately proved property applicability survive restart without a distinguished first assignment. The trimmed Agent.Server smoke also round-trips explicit optional omission evidence through generated serialization.

The trimmed persistence fixture also round-trips clause semantic units, owned request predicates, request-to-support links and multiple runtime provenance bindings using generated serialization.

[Nested request qualifiers](../../docs/planner-nested-qualifiers.md) preserve complete execution citations while projecting explicitly contained properties as governing-only contributions. Engine-derived parent links prove composition, never applicability or a new occurrence. The shared six-unit allowance counts parents and qualifiers; published restart checks retain these links without new calls or writes.
