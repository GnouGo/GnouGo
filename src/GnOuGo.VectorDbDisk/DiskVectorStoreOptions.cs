namespace GnOuGo.VectorDbDisk;

public sealed record DiskVectorStoreOptions(
    string RootPath,
    bool NormalizeVectorsOnInsert = true,
    bool AutoCompactOnWrite = true,
    long MaxOpsBytesBeforeCompaction = 64L * 1024 * 1024,
    bool AutoCompactOnSearchIfOpsTooLarge = true,
    long MaxOpsBytesToScanOnSearch = 64L * 1024 * 1024)
{
    public static DiskVectorStoreOptions Default(string rootPath) => new(rootPath);
}
