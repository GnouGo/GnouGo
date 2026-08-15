# GnOuGo.Files.Server

Temporary streamed file upload/download API for GnOuGo.

## Features

- Streams request bodies directly to disk with a small fixed buffer.
- Stores metadata in SQLite via Entity Framework Core (`FilesDbContext`) with read-only projections for queries.
- Uses UTC timestamps for creation and expiration dates.
- Default TTL is 12 hours and can be configured via typed `Files` options.
- Per-upload TTL can be provided with the `ttl` query-string parameter.
- A background worker purges expired metadata and files every minute by default.
- React/Vite/TypeScript/SCSS ClientApp for manual API testing.

## Storage

By default, files and metadata are stored under:

```text
<Desktop>/GnOuGo/.GnOuGo/Files
```

The SQLite database defaults to:

```text
<Desktop>/GnOuGo/.GnOuGo/data/gnougo-files.db
```

Override these paths with `Files:StorageRootPath` and `Files:DatabasePath`.

The table schema is owned by the EF Core `FilesDbContext` model. Managed builds bootstrap it with `EnsureCreatedAsync`; Native AOT builds execute the equivalent pre-generated SQLite DDL because EF Core's design-time model is unavailable there. All runtime operations (upload, list, download, purge) still go through `FilesMetadataRepository` using `FilesDbContext`.

> **EF Core persistence is a required architectural contract for this component.** Do not replace `FilesDbContext`, its SQLite provider, compiled model, or repository with raw `Microsoft.Data.Sqlite` to silence trimming or Native AOT diagnostics. Keep the compiled model synchronized with the runtime model, document narrowly scoped framework suppressions, and exercise schema creation plus upload/list/download/purge against the published binary.

Native AOT publish uses `Microsoft.EntityFrameworkCore.Tasks` to precompile repository queries and the committed compiled model under `Data/CompiledModels`. The package currently emits two pinned experimental-feature notices; the warning verifier accepts only those exact messages and audit mode fails if their fingerprints or any other EF diagnostic expands.

## API

### Upload

```bash
curl -X POST "http://localhost:5000/api/files?fileName=sample.bin&ttl=12:00:00" \
  -H "Content-Type: application/octet-stream" \
  --data-binary "@sample.bin"
```

`ttl` accepts either a positive `TimeSpan` (`12:00:00`) or a positive number of hours (`1.5`).

### List

```bash
curl "http://localhost:5000/api/files"
```

### Download

```bash
curl -L "http://localhost:5000/api/files/{id}" -o sample.bin
```

## Build and run

```powershell
dotnet run --project src/GnOuGo.Files.Server/GnOuGo.Files.Server.csproj
```

Build without the frontend step:

```powershell
dotnet build src/GnOuGo.Files.Server/GnOuGo.Files.Server.csproj /p:SkipClientBuild=true
```

Build the ClientApp:

```powershell
corepack.cmd pnpm --dir src/GnOuGo.Files.Server/ClientApp install --frozen-lockfile
corepack.cmd pnpm --dir src/GnOuGo.Files.Server/ClientApp build
```

Run the unit tests:

```powershell
dotnet test tests/GnOuGo.Files.Server.Tests/GnOuGo.Files.Server.Tests.csproj /p:SkipClientBuild=true
```

Publish a Windows x64 self-contained Native AOT binary:

```powershell
dotnet publish src/GnOuGo.Files.Server/GnOuGo.Files.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishTrimmed=true -p:PublishSingleFile=true -o artifacts/publish/files-server-win-x64
```

Validate every supported warning-free publish and its published-binary smoke tests from the repository root:

```powershell
pwsh scripts/verify-warning-free-publishes.ps1 -RuntimeIdentifier win-x64
pwsh scripts/verify-warning-free-publishes.ps1 -RuntimeIdentifier win-x64 -AuditKnownTrimWarnings
```

Audit mode temporarily removes the project-local EF Core linker/Native AOT aggregate suppressions and rejects diagnostics outside their documented dependency fingerprints. Compile-time trim and AOT analyzers remain enabled in both modes.
