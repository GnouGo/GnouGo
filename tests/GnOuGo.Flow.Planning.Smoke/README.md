# Native AOT planner smoke

Exercises the eight-case corpus, strict semantic/grounded transport, source-generated schema-8 restart after each advance, deterministic compiler, isolated scenarios, final approval and independently asserted outputs/effects.

```bash
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror
tests/GnOuGo.Flow.Planning.Smoke/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Flow.Planning.Smoke
```

The existing publish-local IL2026/IL2104/IL3053 exceptions cover Jint 4.16.0 CLR-interoperability and default JsonNode-conversion paths. Flow disables CLR access and performs its own JsonNode conversion. Library analyzers remain enabled. Set `AuditKnownTrimWarnings=true` to inspect dependency warnings. The published binary exercises computations rather than merely checking startup.
