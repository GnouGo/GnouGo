namespace GnOuGo.Assets.Bears;

/// <summary>
/// Selects an optional self-playing animation for a generated GnOuGo SVG.
/// <see cref="None"/> preserves the static legacy output unless
/// <see cref="GnouGnouBearOptions.EnableAnimationRig"/> is enabled for a host
/// controlled animation runtime.
/// </summary>
public enum GnouGnouBearAnimation
{
    None,
    Idle,
    Walk,
    Typing,
    Waiting,
    Pickup,
    Handoff,
    Delivery,
    Clone,
    Merge,
    Celebration,
    Failure
}

internal static class GnouGnouBearAnimationNames
{
    public static string ToToken(GnouGnouBearAnimation animation) => animation switch
    {
        GnouGnouBearAnimation.None => "none",
        GnouGnouBearAnimation.Idle => "idle",
        GnouGnouBearAnimation.Walk => "walk",
        GnouGnouBearAnimation.Typing => "typing",
        GnouGnouBearAnimation.Waiting => "waiting",
        GnouGnouBearAnimation.Pickup => "pickup",
        GnouGnouBearAnimation.Handoff => "handoff",
        GnouGnouBearAnimation.Delivery => "delivery",
        GnouGnouBearAnimation.Clone => "clone",
        GnouGnouBearAnimation.Merge => "merge",
        GnouGnouBearAnimation.Celebration => "celebration",
        GnouGnouBearAnimation.Failure => "failure",
        _ => throw new ArgumentOutOfRangeException(nameof(animation), animation, "Unsupported GnOuGo animation.")
    };
}
