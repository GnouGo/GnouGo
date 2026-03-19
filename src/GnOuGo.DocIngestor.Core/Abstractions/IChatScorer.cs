namespace DocIngestor.Core.Abstractions;

/// <summary>
/// Scores the semantic relevance of a text passage to a query using an LLM chat API.
/// This abstraction decouples the Cross-Encoder reranker from any specific LLM provider
/// (OpenAI, Ollama, Azure, etc.).
/// </summary>
public interface IChatScorer
{
    /// <summary>Unique name identifying this scorer (e.g. "openai", "ollama").</summary>
    string Name { get; }

    /// <summary>
    /// Scores how relevant <paramref name="passage"/> is to <paramref name="query"/>.
    /// Returns a value in [0, 10].
    /// </summary>
    Task<double> ScoreAsync(string query, string passage, CancellationToken ct = default);
}

