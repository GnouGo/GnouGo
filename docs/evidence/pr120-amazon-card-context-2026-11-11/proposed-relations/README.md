# Inactive source-relationship inspector

Build the existing Release benchmark first, then this project with `-warnaserror`.
Run `--self-test` for controlled checks, or:

```sh
dotnet Relations.dll --inspect /private/retained-events.json /private/fresh-report.json
```

The input remains immutable and the output must be a fresh distinct path. The
inspector reuses the existing complete-observation decoder and original route
grammar, reads only stored source records and returns their references. It does
not create navigation authority, call a browser/model, activate a policy, verify
redirect proof, qualify source completion or run the business oracle.

Known-ASIN membership is explicitly a proposed alternative to exact HTTP alias
equality; empty-ASIN relationships still list every required own redirect record.
The [proposal](../proposal.md) defines the separate approval and remaining gates.
