# GnOuGo.Assets.Bears

<a href="https://www.nuget.org/packages/GnOuGo.Assets.Bears"><img src="https://img.shields.io/nuget/v/GnOuGo.Assets.Bears.svg" alt="NuGet version"></a>
<a href="https://www.nuget.org/packages/GnOuGo.Assets.Bears"><img src="https://img.shields.io/badge/.NET-10.0-blue.svg" alt=".NET 10.0"></a>
<a href="https://nugettrends.com/packages?ids=GnOuGo.Assets.Bears"><img src="https://img.shields.io/nuget/dt/GnOuGo.Assets.Bears.svg" alt="NuGet downloads"></a>

Dependency-free deterministic SVG generator for the GnOuGo mascot, GnouGnou,
and rounded gradient text artwork.

## Install

```bash
dotnet add package GnOuGo.Assets.Bears
```

## Usage

```csharp
using GnOuGo.Assets.Bears;

var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
{
    Seed = 42,
    Role = GnouGnouBearRole.Coder,
    Emotion = GnouGnouBearEmotion.Happy,
    Accessory = GnouGnouBearAccessory.Laptop,
    State = GnouGnouBearState.Running,
    Theme = GnouGnouBearTheme.Default,
    FurPalette = GnouGnouBearFurPalette.Classic,
    EyeStyle = GnouGnouBearEyeStyle.BigGlossy,
    NoseStyle = GnouGnouBearNoseStyle.Button,
    BeardStyle = GnouGnouBearBeardStyle.Cloud,
    HasBeard = true,
    HasHeadphones = true,
    HasBowTie = true,
    Animation = GnouGnouBearAnimation.Idle
});
```

### Rounded gradient text

Generate a static, automatically sized wordmark from text and a nominal text
height:

```csharp
var textSvg = GnouGnouTextSvgGenerator.Generate("GnouGo", 128);
```

Use `GnouGnouTextOptions` to customize the horizontal gradient, four-point
stars, accessible metadata, and optional animation:

```csharp
var animatedTextSvg = GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
{
    Text = "Hello GnOuGo",
    Size = 120,
    HorizontalMargin = 32, // Optional; null keeps automatic safe spacing.
    VerticalMargin = 24,
    GradientColors = ["#4F46E5", "#0EA5E9", "#2DD4BF"],
    StarCount = 3,
    StarColor = "#2DD4BF", // Defaults to the final gradient color.
    StarScale = 0.9,
    Animation = GnouGnouTextAnimation.Idle,
    SvgIdPrefix = "hero-wordmark"
});
```

`Size` controls the nominal letter height; the SVG `width`, `height`, and
`viewBox` are calculated from the text, stars, and animation clearance.
`GradientColors` accepts two to eight hexadecimal colors. `StarCount` accepts
zero to eight stars, with zero disabling the decoration. `HorizontalMargin`
and `VerticalMargin` configure the blank space on both sides of their axis in
SVG units; leave either value unset to retain animation-safe automatic spacing.

Text animation presets are `None`, `Idle`, `Wave`, and `Bounce`. `Idle` gives
each Unicode text element gentle independent movement and periodically sends a
stronger motion from left to right. `Wave` continuously travels through the
letters, while `Bounce` produces a playful sequential squash-and-pop followed
by a calm pause. All presets are script-free and automatically stop under
`prefers-reduced-motion`.

The generator uses a rounded system-font stack and deterministic per-character
fitting, so the canvas dimensions stay stable while the exact glyph design can
follow the fonts installed by the SVG viewer.

`Animation` controls self-playing, script-free SVG motion:

```csharp
// Legacy static SVG. This remains the default.
var staticSvg = GnouGnouBearSvgGenerator.Generate(new()
{
    Animation = GnouGnouBearAnimation.None
});

// A standalone animated GnOuGo.
var typingSvg = GnouGnouBearSvgGenerator.Generate(new()
{
    Animation = GnouGnouBearAnimation.Typing
});

// Concentrated AI work: narrowed moving eyes, warmer face, and sweat drops.
var thinkingSvg = GnouGnouBearSvgGenerator.Generate(new()
{
    Animation = GnouGnouBearAnimation.Thinking
});
```

Available presets are `None`, `Idle`, `Walk`, `Typing`, `Waiting`, `Pickup`,
`Handoff`, `Delivery`, `Clone`, `Merge`, `Celebration`, `Failure`, and
`Thinking`. `Thinking` adds a concentrated pose with changing eyes, inward
brows, a warm face flush, redder cheeks that vary independently in width and
height around fixed centers, animated sweat drops, and a foreground arm that
reuses the canonical right-arm geometry and rotates from the shoulder to rub
the forehead. `Walk` uses alternating arm and leg phases, while `Failure`
switches to a dedicated frown with lowered pupils, eyelids, ears, and brows.

## Notes

- Pure C# string generation.
- No runtime file reads, raster assets, base64 images, or external dependencies.
- AOT and trimming compatible.
- The same options produce the exact same SVG.
- Fur palettes, eye styles, emotion-driven eyebrows and mouths, five nose
  styles, five explicit beard silhouettes, headphones, bow ties, and
  accessories can be varied without raster assets.
- `BeardStyle = Random` preserves seeded beard selection. Choose `Classic`,
  `LongPoint`, `Cloud`, `Square`, or `Split` for an explicit silhouette.
- Static and animated rigs honor the same eye, emotion, nose, and beard
  selections.
- `Title` and `Description` are XML-escaped before being written into the SVG.
- `Size` must be between `64` and `1024`.
- Set `SvgIdPrefix` when embedding multiple mascots in one SVG document so every
  gradient, filter, title, and description ID remains unique.
- Set `EnableAnimationRig` to render opt-in semantic groups for the head,
  independently movable left/right ears, eyes, pupils, mouth, arms, hands,
  legs, bow tie, and action effects. Each movable group includes a stable
  `data-part` name and pivot coordinates for a host to animate. Use this with
  `Animation = None` when the host controls actions dynamically.
- The reusable browser controller lives in
  `Runtime/gnougnou-animation-controller.ts`. It owns walking, typing, handoff,
  delivery, clone/merge, celebration/failure, breathing, blinking, mouth,
  independent ear, and rare-yawn motion. Idle actors are balanced across six
  personalities: looking around, side-to-side swaying, stretching, toe-tapping,
  pondering, and an occasional small wave. Seeded per-actor clocks vary every
  gesture, blink, breath, and pause so a group does not move in sync. Hosts only
  choose when an action plays.
- Both animation mechanisms honor `prefers-reduced-motion`.
- Animation is disabled by default to preserve existing static output
  byte-for-byte.

For an event-driven browser host, generate the bear with
`EnableAnimationRig = true` and `Animation = None`, then use the packaged
controller:

```ts
import { GnouGnouAnimationController } from './Runtime/gnougnou-animation-controller'

const animations = new GnouGnouAnimationController(
  () => document.querySelector('#workflow-scene'),
)

animations.startAmbient()
animations.play('actor-master', 'walk', 1_200, 1)
animations.play('actor-master', 'type', 3_000)
// Call animations.cancelAll() when the host stops or replaces the scene.
```

The actor ID can identify either the semantic rig itself or an SVG wrapper
containing it.

## Build

```bash
dotnet build src/GnOuGo.Assets.Bears/GnOuGo.Assets.Bears.csproj
```

## Test

```bash
dotnet test tests/GnOuGo.Assets.Bears.Tests/GnOuGo.Assets.Bears.Tests.csproj
```

## Pack

```bash
dotnet pack src/GnOuGo.Assets.Bears/GnOuGo.Assets.Bears.csproj -c Release
```
