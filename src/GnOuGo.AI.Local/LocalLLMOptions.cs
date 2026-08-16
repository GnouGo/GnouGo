namespace GnOuGo.AI.Local;

/// <summary>Runtime tuning for the embedded local model.</summary>
public sealed class LocalLLMOptions
{
    public const string SectionName = "LocalLLM";

    /// <summary>Maximum prompt and completion context. Qwen3 supports more, but 8192 is the portable default.</summary>
    public uint ContextSize { get; set; } = 8192;

    /// <summary>CPU generation threads. Zero selects a portable automatic value.</summary>
    public int Threads { get; set; }

    /// <summary>Maximum generated tokens when the request does not specify a lower bound.</summary>
    public int MaxOutputTokens { get; set; } = 1024;

    /// <summary>Deterministic seed used for structured planning and smoke tests.</summary>
    public uint Seed { get; set; } = 1337;
}
