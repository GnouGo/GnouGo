# GnOuGo.Workspace

Shared library that centralizes workspace directory resolution logic used across all GnOuGo components.

## Purpose

This library eliminates duplicated code for:

- **Desktop directory resolution** — robust fallback chain for Native AOT, sandboxed, and headless environments.
- **Default working directory** — resolves `Desktop/GnOuGo` (or a configured path) and ensures it exists.
- **Workspace root discovery** — walks up the directory tree looking for `.sln` or `.git` markers.
- **Path containment checks** — verifies a path is inside an allowed root directory.
- **Database path resolution** — maps `data/` prefixed relative paths to the `Desktop/GnOuGo/data/` convention.

## API

All methods are on the static class `GnOuGoWorkspace`:

| Method | Description |
|---|---|
| `ResolveDesktopDirectory()` | Returns the current user's Desktop path with robust fallbacks. |
| `ResolveDefaultWorkingDirectory(configuredPath?)` | Resolves and creates the GnOuGo working directory. |
| `ResolveDefaultWorkingDirectorySafe(configuredPath?, contentRootPath?)` | Same as above, but never throws — falls back to HOME/tmp. |
| `ResolveDatabasePath(configuredPath?, baseDirectory, defaultRelativePath)` | Resolves a `.db` file path using the GnOuGo data convention. |
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

// Resolve Desktop/GnOuGo
var workDir = GnOuGoWorkspace.ResolveDefaultWorkingDirectory();

// Check workspace root
var root = GnOuGoWorkspace.DiscoverWorkspaceRoot(AppContext.BaseDirectory);

// Resolve a database path
var dbPath = GnOuGoWorkspace.ResolveDatabasePath(null, baseDir, "data/my-app.db");
```

