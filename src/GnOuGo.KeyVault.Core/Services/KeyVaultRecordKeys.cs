using System.Runtime.CompilerServices;

namespace GnOuGo.KeyVault.Core.Services;

public static class KeyVaultRecordKeys
{
    /// <summary>Enumerate one tenant and exact ordinal prefix in bounded metadata pages.</summary>
    public static async IAsyncEnumerable<string> EnumerateKeysAsync(this IKeyVaultRecordStore store,
        string collection, string tenant, string prefix, string author, [EnumeratorCancellation] CancellationToken ct)
    {
        if (store is IKeyVaultRecordReader reader)
        {
            KeyVaultRecordCursor? cursor = null;
            do
            {
                var page = await reader.QueryMetadataAsync(collection, tenant, prefix, author, cursor, 100, ct);
                foreach (var item in page.Records)
                    if (item.Key.StartsWith(prefix, StringComparison.Ordinal)) yield return item.Key;
                cursor = page.HasMore ? page.Next ?? throw new InvalidDataException("Missing record cursor.") : null;
            } while (cursor is not null);
        }
        else foreach (var record in await store.ListAsync(collection, tenant, author, ct))
            if (record.Key.StartsWith(prefix, StringComparison.Ordinal)) yield return record.Key;
    }
}
