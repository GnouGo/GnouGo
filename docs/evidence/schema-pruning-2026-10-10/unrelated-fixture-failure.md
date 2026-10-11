# Full-suite Copilot fixture cleanup failure

The full solution run failed in the unchanged test
`GnOuGo.GithubCopilot.Mcp.Tests.CopilotTasksTests.CancellationReachesTaskOwnerAndRetainsPartialCheckpoint`.
The exception came from fixture disposal after the test body:

```text
System.IO.IOException: Directory not empty: <temporary fixture>/leases
System.IO.FileSystem.RemoveDirectoryRecursive
CopilotAttachmentTests.Fixture.DisposeAsync():214
CopilotTasksTests.CancellationReachesTaskOwnerAndRetainsPartialCheckpoint():202
```

The same test passed three consecutive isolated runs, unchanged:

```sh
dotnet test tests/GnOuGo.GithubCopilot.Mcp.Tests/GnOuGo.GithubCopilot.Mcp.Tests.csproj \
  --no-build --no-restore -m:1 -warnaserror \
  --filter FullyQualifiedName~CancellationReachesTaskOwnerAndRetainsPartialCheckpoint
```

The failure location and isolated passes suggest a fixture teardown race; they do
not establish its root cause or prove it fixed. No Copilot source, fixture,
assertion or timeout was changed. The full-suite failure remains a failure and is
not replaced by the isolated results. Resolving this separate issue is outside
the requested JSON Schema-only production correction.
