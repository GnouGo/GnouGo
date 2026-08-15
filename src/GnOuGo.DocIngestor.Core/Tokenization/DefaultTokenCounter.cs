using DocIngestor.Core.Abstractions;
using Microsoft.ML.Tokenizers;

namespace DocIngestor.Core.Tokenization;

/// <summary>Counts tokens using the configured Microsoft ML tokenizer model.</summary>
public sealed class DefaultTokenCounter : ITokenCounter
{
    private readonly Tokenizer _tokenizer;

    /// <summary>Initializes the counter for a tiktoken-compatible model name.</summary>
    /// <param name="tiktokenModelName">Model name used to select the tokenizer vocabulary.</param>
    public DefaultTokenCounter(string tiktokenModelName = "gpt-4")
    {
        // Uses cl100k_base (via Microsoft.ML.Tokenizers.Data.Cl100kBase) for GPT-4 family.
        _tokenizer = TiktokenTokenizer.CreateForModel(tiktokenModelName);
    }

    /// <inheritdoc />
    public int CountTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : _tokenizer.CountTokens(text);
}
