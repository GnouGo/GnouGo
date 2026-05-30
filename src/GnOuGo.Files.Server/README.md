# GnOuGo.Files.Server

Temporary streamed file upload/download API for GnOuGo.

## Features

- Streams request bodies directly to disk with a small fixed buffer.
- Stores metadata in SQLite via Entity Framework Core (`FilesDbContext`) with `AsNoTracking` for read queries.
- Uses UTC timestamps for creation and expiration dates.
- Default TTL is 12 hours and can be configured via typed `Files` options.
- Per-upload TTL can be provided with the `ttl` query-string parameter.
- A background worker purges expired metadata and files every minute by default.
- React/Vite/TypeScript/SCSS ClientApp for manual API testing.

## Storage

By default, files and metadata are stored under:

```text
<Desktop>/GnOuGo/Files/Temp
```

The SQLite database defaults to:

```text
<Desktop>/GnOuGo/Files/Temp/gnougo-files.db
```

Override these paths with `Files:StorageRootPath` and `Files:DatabasePath`.

The table schema is owned by the EF Core `FilesDbContext` model and bootstrapped at startup via `EnsureCreatedAsync`. All runtime operations (upload, list, download, purge) go through `FilesMetadataRepository` using `FilesDbContext`.

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

Publish a Windows x64 self-contained trimmed binary:

```powershell
dotnet publish src/GnOuGo.Files.Server/GnOuGo.Files.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true -p:PublishSingleFile=true -o artifacts/publish/files-server-win-x64
```

