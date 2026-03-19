using DocIngestor.Core.Abstractions;
using Microsoft.ML.Tokenizers;

namespace DocIngestor.Core.Tokenization;

public sealed class DefaultTokenCounter : ITokenCounter
{
    private readonly Tokenizer _tokenizer;

    public DefaultTokenCounter(string tiktokenModelName = "gpt-4")
    {
        // Uses cl100k_base (via Microsoft.ML.Tokenizers.Data.Cl100kBase) for GPT-4 family.
        _tokenizer = TiktokenTokenizer.CreateForModel(tiktokenModelName);
    }

    public int CountTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : _tokenizer.CountTokens(text);
}
