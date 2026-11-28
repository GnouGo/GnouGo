"""Disambiguate colliding BSD archive debug-map keys in a publish-local .NET GC library."""
from pathlib import Path
import sys


def prepare(source: Path, output: Path) -> None:
    archive = bytearray(source.read_bytes())
    if not archive.startswith(b"!<arch>\n"):
        raise ValueError("Expected a Darwin ar archive")
    offset = 8
    members = []
    while offset < len(archive):
        header = archive[offset:offset + 60]
        if len(header) != 60 or header[58:60] != b"`\n":
            raise ValueError("Invalid ar member header")
        size = int(header[48:58].strip())
        start = offset + 60
        if size < 0 or start + size > len(archive):
            raise ValueError("Invalid ar member length")
        if header.startswith(b"#1/"):
            length = int(header[3:16].strip())
            if length < 0 or length > size:
                raise ValueError("Invalid BSD ar member name")
            name = bytes(archive[start:start + length]).rstrip(b"\0")
            members.append((start, name, bytes(header[16:28])))
        offset = start + size + size % 2

    names = {name for _, name, _ in members}
    seen = set()
    for start, name, timestamp in members:
        if (name, timestamp) in seen and name.endswith(b".o"):
            # Preserve the header length, every object byte and all symbol-table offsets.
            ordinal = 2
            while True:
                suffix = str(ordinal).encode("ascii") + b".o"
                if len(suffix) > len(name):
                    raise ValueError("No same-length archive member name available")
                replacement = name[:-len(suffix)] + suffix
                if replacement not in names:
                    break
                ordinal += 1
            names.add(replacement)
            archive[start:start + len(name)] = replacement
        seen.add((name, timestamp))
    output.write_bytes(archive)


if __name__ == "__main__":
    prepare(Path(sys.argv[1]), Path(sys.argv[2]))
