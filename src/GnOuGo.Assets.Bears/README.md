# GnOuGo.Assets.Bears

Dependency-free deterministic SVG generator for the GnOuGo mascot, GnouGnou.

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
    HasHeadphones = true,
    HasBowTie = true,
    Animation = GnouGnouBearAnimation.Idle
});
```

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
```

Available presets are `None`, `Idle`, `Walk`, `Typing`, `Waiting`, `Pickup`,
`Handoff`, `Delivery`, `Clone`, `Merge`, `Celebration`, and `Failure`.
`Walk` uses alternating arm and leg phases, while `Failure` switches to a
dedicated frown with lowered pupils, eyelids, ears, and brows.

## Notes

- Pure C# string generation.
- No runtime file reads, raster assets, base64 images, or external dependencies.
- AOT and trimming compatible.
- The same options produce the exact same SVG.
- Fur palettes, eye styles, headphones, bow ties, and accessories can be varied without raster assets.
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
  independent ear, and rare-yawn motion. Hosts only choose when an action plays.
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
