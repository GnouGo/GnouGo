# Supplementary distribution texts

The locked crates omit these repository-root license files from their registry archives. The packaging step attaches the files below to the matching components.json records.

| File | Upstream source revision |
| --- | --- |
| iKeySort-MIT | [iKeySort LICENSE](https://github.com/iShape-Rust/iKeySort/blob/688424e09b7758d04e62e350160d164c7384f54b/LICENSE) |
| iOverlay-APACHE, iOverlay-MIT | [iOverlay Apache](https://github.com/iShape-Rust/iOverlay/blob/117637e3a65804973da2bfd7c59f42773f4dcc0d/LICENSE-APACHE) and [MIT](https://github.com/iShape-Rust/iOverlay/blob/117637e3a65804973da2bfd7c59f42773f4dcc0d/LICENSE-MIT) |
| Symphonia-MPL-2.0 | [Symphonia LICENSE](https://github.com/pdeljanov/Symphonia/blob/6d533f26150953a882a6a111ebd13f0abf7129d5/LICENSE) |

For each resolved Symphonia crate, the distribution also includes its original .crate source under sources/. Its SHA-256 must equal the registry checksum in Cargo.lock.
