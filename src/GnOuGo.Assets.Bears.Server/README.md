# GnOuGo.Assets.Bears.Server

Small ASP.NET Core animation gallery for `GnOuGo.Assets.Bears`.

The home page renders every `GnouGnouBearAnimation` preset side-by-side on
the same deterministic mascot. It includes static mode, seed controls, direct
SVG links, and automatically respects reduced-motion preferences.

## Run

```bash
dotnet run --project src/GnOuGo.Assets.Bears.Server/GnOuGo.Assets.Bears.Server.csproj
```

Open the displayed local URL to compare the animation presets. Use the seed
field to reproduce an appearance or select **Randomize** for another GnOuGo.

## Endpoints

- `/` renders the complete animation gallery with a random appearance.
- `/?seed=42` renders the gallery with a reproducible appearance.
- `/bear.svg` returns a static standalone SVG.
- `/bear.svg?seed=42&animation=Typing` returns one reproducible, self-playing
  animation SVG.

Supported animation values are `None`, `Idle`, `Walk`, `Typing`, `Waiting`,
`Pickup`, `Handoff`, `Delivery`, `Clone`, `Merge`, `Celebration`, and
`Failure`. Invalid values return HTTP `400`.

## Build

```bash
dotnet build src/GnOuGo.Assets.Bears.Server/GnOuGo.Assets.Bears.Server.csproj
```
