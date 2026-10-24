using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Agent.Server.Configuration;
public sealed class TypedWorkflowPlanningSettings
{
    public const string SectionName = "TypedWorkflowPlanning";
    public bool BackgroundProcessingEnabled { get; set; } = true;
    public bool AutomaticCorrectionEnabled { get; set; } = true;
    public int MaxReplanAttempts { get; set; } = 4;
    public int MaxModelCalls { get; set; } = 64;
    public string ConstructionPreset { get; set; } = PlanningConstructionPresets.Ordinary;
    public int LargeMaxModelCalls { get; set; } = PlanningConstructionPresets.LargeModelCalls;
    public int LargeMaxReplanAttempts { get; set; } = PlanningConstructionPresets.LargeCorrections;
    public int LargeMaxStepsTotal { get; set; } = PlanningConstructionPresets.LargeGraphNodes;
    public int LargeMaxStepsPerWorkflow { get; set; } = PlanningConstructionPresets.LargeGraphNodes;
    public int LargeMaxWorkflows { get; set; } = PlanningConstructionPresets.LargeWorkflows;
    public long MaxTotalTokens { get; set; } = 15_000_000;
    public long MaxActiveMilliseconds { get; set; } = 18_000_000;
    public string Reasoning { get; set; } = "medium";
    public int MaxInputTokensPerRequest { get; set; } = 32_000;
    public int MaxOutputTokens { get; set; } = 32_768;
    public int DraftOutputTokens { get; set; } = 8_192;
    public int ReviewOutputTokens { get; set; } = 2_048;
    public int SelectionInputTokens { get; set; } = 3_000;
    public int DraftInputTokens { get; set; } = 8_000;
    public int ReviewInputTokens { get; set; } = 4_000;
    public PlanningConstructionPhasePolicy? PhasePolicy { get; set; }
    public string DatabasePath { get; set; } = ".GnOuGo/data/gnougo-planning-v9.db";

    public string ResolvePreset(string? requested = null) => PlanningConstructionPresets.Validate(requested ?? ConstructionPreset);
    public int Calls(string preset) => ResolvePreset(preset) == PlanningConstructionPresets.Large ? LargeMaxModelCalls : MaxModelCalls;
    public int Corrections(string preset) => ResolvePreset(preset) == PlanningConstructionPresets.Large ? LargeMaxReplanAttempts : MaxReplanAttempts;

    public void Validate()
    {
        _ = ResolvePreset();
        if (MaxModelCalls is < 1 or > 1000 || MaxReplanAttempts is < 0 or > 10 ||
            LargeMaxModelCalls is < 1 or > 1000 || LargeMaxReplanAttempts is < 0 or > 64 ||
            LargeMaxStepsTotal < 1 || LargeMaxStepsPerWorkflow < 1 || LargeMaxWorkflows < 1 ||
            MaxTotalTokens < 1 || MaxActiveMilliseconds < 1 ||
            DraftOutputTokens is < 1 or > 65536 || ReviewOutputTokens is < 1 or > 65536 ||
            SelectionInputTokens is < 1 or > 3_000 || DraftInputTokens is < 1 or > 8_000 || ReviewInputTokens is < 1 or > 4_000)
            throw new InvalidOperationException("Invalid typed workflow planning configuration.");
        PlanningGenerationPolicy.Validate(new() { Reasoning = Reasoning, MaxInputTokensPerRequest = MaxInputTokensPerRequest, MaxOutputTokens = MaxOutputTokens });
        if (PhasePolicy is not null) PlanningConstructionPhasePolicies.Validate(PhasePolicy);
    }
}
