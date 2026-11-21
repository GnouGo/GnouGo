namespace GnOuGo.Flow.Core.Planning;

/// <summary>Explicit creation-time capacity selections; never applied to retained sessions.</summary>
public static class PlanningConstructionPresets
{
    public const string Ordinary = "ordinary";
    public const string Large = "large";
    public const int LargeModelCalls = 384;
    public const int LargeCorrections = 64;
    public const int LargeGraphNodes = 5_000;
    public const int LargeWorkflows = 1_024;

    public static string Validate(string preset)
        => preset is Ordinary or Large ? preset : throw new ArgumentException("Unknown construction capacity preset.");
}
