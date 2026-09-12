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
