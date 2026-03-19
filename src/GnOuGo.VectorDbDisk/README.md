# GnOuGo.VectorDbDisk

Disk-first vector store for .NET:

- Metadata pre-filter is performed using on-disk postings lists (`meta/{key}/{value}.post`)
- Vectors are read lazily from disk for candidates (no full collection RAM load)
- Designed to be NativeAOT-friendly (no reflection-heavy dependencies)

## Files per collection

`{Root}/{Collection}/`

- `header.bin` : int32 vectorSize
- `docs.bin` : append-only records (id, text, metadata, vector)
- `offsets.bin` : int64 offsets, one per docId
- `meta/` : postings files, one per `(key,value)` -> delta-varint docIds

## NativeAOT

Example publish (consumer app):

```bash
dotnet publish -c Release -r win-x64 /p:PublishAot=true
```
