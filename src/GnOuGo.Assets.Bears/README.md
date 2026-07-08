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
    HasBowTie = true
});
```

## Notes

- Pure C# string generation.
- No runtime file reads, raster assets, base64 images, or external dependencies.
- AOT and trimming compatible.
- The same options produce the exact same SVG.
- Fur palettes, eye styles, headphones, bow ties, and accessories can be varied without raster assets.
- `Title` and `Description` are XML-escaped before being written into the SVG.
- `Size` must be between `64` and `1024`.

## Build

```bash
dotnet build src/GnOuGo.Assets.Bears/GnOuGo.Assets.Bears.csproj
```

## Test

```bash
dotnet test tests/GnOuGo.Assets.Bears.Tests/GnOuGo.Assets.Bears.Tests.csproj
```
