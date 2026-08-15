using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace GnOuGo.Files.Server.Data.CompiledModels;

public partial class FileRecordEntityType
{
    static partial void Customize(RuntimeEntityType runtimeEntityType)
    {
        ConfigureDateTimeOffsetMapping(runtimeEntityType, "CreatedUtc");
        ConfigureDateTimeOffsetMapping(runtimeEntityType, "ExpiresUtc");
    }

    private static void ConfigureDateTimeOffsetMapping(RuntimeEntityType entityType, string propertyName)
    {
        var property = entityType.FindProperty(propertyName)
            ?? throw new InvalidOperationException($"The compiled Files model is missing property '{propertyName}'.");
        var comparer = new ValueComparer<DateTimeOffset>(
            (left, right) => left.Equals(right),
            value => value.GetHashCode(),
            value => value);
        var mapping = (RelationalTypeMapping)property.TypeMapping;
        property.TypeMapping = mapping.Clone(
            comparer: comparer,
            keyComparer: comparer,
            providerValueComparer: comparer,
            mappingInfo: new RelationalTypeMappingInfo(storeTypeName: "TEXT"));
    }
}
