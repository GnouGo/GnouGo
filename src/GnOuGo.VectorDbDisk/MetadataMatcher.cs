namespace GnOuGo.VectorDbDisk;

internal static class MetadataMatcher
{
    public static bool Matches(IMetadataFilter filter, IReadOnlyDictionary<string, string> metadata)
    {
        return filter switch
        {
            TrueFilter => true,
            EqualsFilter eq => metadata.TryGetValue(eq.Key, out var v) && string.Equals(v, eq.Value, StringComparison.Ordinal),
            InFilter inf => metadata.TryGetValue(inf.Key, out var v) && inf.Values.Contains(v, StringComparer.Ordinal),
            AndFilter and => Matches(and.Left, metadata) && Matches(and.Right, metadata),
            OrFilter or => Matches(or.Left, metadata) || Matches(or.Right, metadata),
            _ => true
        };
    }
}
