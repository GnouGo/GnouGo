# GnOuGo.Assets.Bears.Server

Small ASP.NET Core demo server for `GnOuGo.Assets.Bears`.

Each page load generates a different deterministic SVG GnouGnou mascot by creating fresh options and passing them to the library. Variants can change fur color, eye style, accessory, state, theme, and whether GnouGnou wears headphones or a bow tie.

## Run

```bash
dotnet run --project src/GnOuGo.Assets.Bears.Server/GnOuGo.Assets.Bears.Server.csproj
```

Open the displayed local URL and reload the page to get another generated bear.

## Endpoints

- `/` renders a small web page with a freshly generated inline SVG.
- `/bear.svg` returns a freshly generated SVG.
- `/bear.svg?seed=42` returns an SVG with a fixed seed and random visual options.

## Build

```bash
dotnet build src/GnOuGo.Assets.Bears.Server/GnOuGo.Assets.Bears.Server.csproj
```
