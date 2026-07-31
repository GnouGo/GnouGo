namespace GnOuGo.Assets.Bears;

/// <summary>
/// Script-free animation presets for generated GnOuGo text artwork.
/// </summary>
public enum GnouGnouTextAnimation
{
    /// <summary>
    /// Renders a static SVG without animation styles.
    /// </summary>
    None,

    /// <summary>
    /// Gives every letter subtle independent motion and periodically sends a
    /// stronger movement through the text from left to right.
    /// </summary>
    Idle,

    /// <summary>
    /// Sends a smooth, continuous traveling wave through the letters from left
    /// to right.
    /// </summary>
    Wave,

    /// <summary>
    /// Makes the letters squash, lift, and settle in a playful left-to-right
    /// sequence with a calm pause between passes.
    /// </summary>
    Bounce
}
