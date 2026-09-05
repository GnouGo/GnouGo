# Typed planner Native AOT smoke

This executable references the publishable planning package, serializes and restores
a typed session using its generated JSON context, exports and imports YAML, compiles
it, and executes its expressions without live integrations. It also advances a full
typed planning session through behavior review, schema-constrained fake responses,
scenario validation and exact revision approval, restoring serialized checkpoints
between phases.

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
