namespace GnOuGo.VectorDbDisk;

public interface IMetadataFilter { }

public sealed class TrueFilter : IMetadataFilter
{
    public static readonly TrueFilter Instance = new();
    private TrueFilter() { }
}

public sealed record EqualsFilter(string Key, string Value) : IMetadataFilter;

public sealed record InFilter(string Key, IReadOnlyCollection<string> Values) : IMetadataFilter;

public sealed record AndFilter(IMetadataFilter Left, IMetadataFilter Right) : IMetadataFilter;

public sealed record OrFilter(IMetadataFilter Left, IMetadataFilter Right) : IMetadataFilter;

public static class MetadataFilter
{
    public static IMetadataFilter True() => TrueFilter.Instance;
    public static IMetadataFilter Eq(string key, string value) => new EqualsFilter(key, value);
    public static IMetadataFilter In(string key, params string[] values) => new InFilter(key, values);
    public static IMetadataFilter And(IMetadataFilter left, IMetadataFilter right) => new AndFilter(left, right);
    public static IMetadataFilter Or(IMetadataFilter left, IMetadataFilter right) => new OrFilter(left, right);
}
