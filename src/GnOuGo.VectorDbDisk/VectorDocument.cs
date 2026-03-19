using System.Collections.ObjectModel;

namespace GnOuGo.VectorDbDisk;

public sealed record VectorDocument(
    string Id,
    string Text,
    float[] Vector,
    IReadOnlyDictionary<string, string> Metadata)
{
    public static VectorDocument Create(
        string? id,
        string text,
        float[] vector,
        IDictionary<string, string>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text must be non-empty.", nameof(text));
        if (vector is null || vector.Length == 0)
            throw new ArgumentException("Vector must be non-empty.", nameof(vector));

        var docId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id!;
        var md = (metadata is null)
            ? (IReadOnlyDictionary<string, string>)new ReadOnlyDictionary<string, string>(new Dictionary<string, string>())
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(metadata));

        return new VectorDocument(docId, text, vector, md);
    }
}
