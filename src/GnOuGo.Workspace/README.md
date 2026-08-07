# GnOuGo.Workspace

Shared library that centralizes workspace directory resolution logic used across all GnOuGo components.

## Purpose

This library eliminates duplicated code for:

- **Desktop directory resolution** — robust fallback chain for Native AOT, sandboxed, and headless environments.
- **Default working directory** — resolves `Desktop/GnOuGo` (or a configured path) and ensures it exists.
- **Workspace root discovery** — walks up the directory tree looking for `.sln` or `.git` markers.
- **Path containment checks** — verifies a path is inside an allowed root directory.
- **Database path resolution** — resolves configured relative paths such as `.GnOuGo/data/app.db` under the default working directory.
- **Workflow workspace resolution** — keeps visible workflow-owned checkouts and artifacts below `workflows/` while classifying `.GnOuGo/` as reserved internal state.

## Workspace convention

`Desktop/GnOuGo/workflows/` is the visible root for workflow-owned working directories. Use purpose-specific children such as `workflows/github-review-123`; the directory is created lazily by the operation that materializes a workspace.

`Desktop/GnOuGo/.GnOuGo/` is reserved for GnOuGo-managed databases, agents, uploads, traces, and internal temporary files. Workflow-facing project and file paths must never target that subtree.

## API

All methods are on the static class `GnOuGoWorkspace`:

| Method | Description |
|---|---|
| `ResolveDesktopDirectory()` | Returns the current user's Desktop path with robust fallbacks. |
| `ResolveDefaultWorkingDirectory(configuredPath?)` | Resolves and creates the GnOuGo working directory. |
| `ResolveDefaultWorkingDirectorySafe(configuredPath?, contentRootPath?)` | Same as above, but never throws — falls back to HOME/tmp. |
| `ResolveDatabasePath(configuredPath?, baseDirectory, defaultRelativePath)` | Resolves a `.db` file path using the GnOuGo data convention. |
| `ResolveWorkflowWorkspacesDirectory(workspaceRoot)` | Resolves the visible `workflows/` root without creating it. |
| `IsReservedWorkspacePath(path, workspaceRoot)` | Returns `true` for `.GnOuGo` and its descendants. |
| `IsWorkflowWorkspacePath(path, workspaceRoot)` | Returns `true` for `workflows` and its descendants. |
| `DiscoverWorkspaceRoot(startPath)` | Finds the nearest parent with a `.sln` or `.git`. |
| `IsPathWithinRoot(path, root)` | Returns `true` if `path` is under `root`. |

## Build

```bash
dotnet build src/GnOuGo.Workspace/GnOuGo.Workspace.csproj
```

## Test

```bash
dotnet test tests/GnOuGo.Workspace.Tests/GnOuGo.Workspace.Tests.csproj
```

## Usage

```csharp
using GnOuGo.Workspace;

// Resolve Desktop/GnOuGo for generic working-directory scenarios
var workDir = GnOuGoWorkspace.ResolveDefaultWorkingDirectory();

// Check workspace root
var root = GnOuGoWorkspace.DiscoverWorkspaceRoot(AppContext.BaseDirectory);

// Resolve a database path under Desktop/GnOuGo/.GnOuGo/data by default
var dbPath = GnOuGoWorkspace.ResolveDatabasePath(null, baseDir, ".GnOuGo/data/my-app.db");

// Resolve the visible root for workflow-owned workspaces
var workflowRoot = GnOuGoWorkspace.ResolveWorkflowWorkspacesDirectory(workDir);
```
