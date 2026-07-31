# GnOuGo.Assets.Bears.Server

Small ASP.NET Core animation gallery and text SVG playground for
`GnOuGo.Assets.Bears`.

The home page contains two deterministic collections:

- **Static** — ten appearance studies covering eye shapes, emotion-driven
  eyebrows and mouths, five nose styles, and five beard silhouettes.
- **Animated** — every `GnouGnouBearAnimation` preset applied across the same
  diverse appearance library. The `AI Thinking` preset is featured first with
  concentrated eyes, face flush, and animated forehead sweat drops.

It includes seed controls, direct SVG links, and automatically respects
reduced-motion preferences.

The **Text SVG playground** at `/text` provides a live preview for the rounded
text generator. It exposes text size, two to eight gradient colors, sparkle
count/color/scale, automatic or explicit horizontal/vertical margins, four
animation modes, and preview background controls. Configurations are reflected
in the page URL so they can be shared, and the generated SVG can be opened,
copied, or downloaded directly.

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
- `/bear.svg?seed=42&appearance=split-beard&animation=Idle` returns a
  reproducible appearance from the gallery as an animated standalone SVG.
- `/text` renders the interactive rounded text SVG playground.
- `/text.svg?text=Hello%20GnOuGo&size=120&color=%234F46E5&color=%230EA5E9&color=%232DD4BF&stars=2&animation=Idle`
  returns a configured standalone text SVG. Repeat `color` two to eight times;
  optional parameters are `starColor`, `starScale`, `marginX`, `marginY`, and
  `idPrefix`. Text animations are `None`, `Idle`, `Wave`, and `Bounce`.

Supported animation values are `None`, `Idle`, `Walk`, `Typing`, `Waiting`,
`Pickup`, `Handoff`, `Delivery`, `Clone`, `Merge`, `Celebration`, and
`Failure`, and `Thinking`. Invalid values return HTTP `400`.

## Build

```bash
dotnet build src/GnOuGo.Assets.Bears.Server/GnOuGo.Assets.Bears.Server.csproj
```

## Test

```bash
dotnet test tests/GnOuGo.Assets.Bears.Server.Tests/GnOuGo.Assets.Bears.Server.Tests.csproj
```
