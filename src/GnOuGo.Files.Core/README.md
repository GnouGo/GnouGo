# GnOuGo.Files.Core

`DurableFileStore` stores authenticated local file content and tenant-scoped grants for GnOuGo hosts. Construct it with the Files storage directory and the workspace's `IKeyVaultRecordStore`. All processes sharing those objects must use the same directory and vault.

- `UploadAsync` creates a temporary grant and returns the file ID, digest, length, media type and filename.
- `ClaimAsync` acquires an independent hold from a current temporary grant or an authorized source owner. Construct the tenant and `FileOwner` from the host's authenticated context.
- `CopyAsync` authenticates each frame and verifies the complete digest. `ReadImageAsync` resolves a digest within one owner and buffers up to 4 MiB.
- `TransferReferencesAsync` copies grants for referenced IDs and digests held by the source owner. `ReleaseAsync` releases that owner's grants.
- `SweepAsync` retries abandoned writes and unlinks unheld objects after temporary expiry. A matching vault manifest authorizes physical deletion.

File keys, names and grants are encrypted through the local KeyVault. File content is AES-256-GCM encrypted in 128 KiB frames beneath the storage directory. The HTTP adapter is distributed separately in GnOuGo.Files.Server.
