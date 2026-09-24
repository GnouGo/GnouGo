# Native AOT planner smoke

Exercises the eight-case corpus, strict executable-graph transport, source-generated schema-9 restart after each advance, deterministic compiler, contract validation, final approval and independently asserted outputs/effects.

```bash
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror
tests/GnOuGo.Flow.Planning.Smoke/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Flow.Planning.Smoke
```

The existing publish-local IL2026/IL2104/IL3053 exceptions cover Jint 4.16.3 CLR-interoperability and default JsonNode-conversion paths. Flow disables CLR access and performs its own JsonNode conversion. Library analyzers remain enabled. Set `AuditKnownTrimWarnings=true` to inspect dependency warnings. The published binary exercises registered transformations and independent execution assertions with the frozen campaign limits (96,000 input / 32,768 output tokens, eight calls, two repairs). Production defaults are unchanged; lifecycle tests separately enforce exhaustion and restart accounting. The large distractor catalog exceeds the ordinary 12,000-token default and correctly stops when run under that smaller allowance.
