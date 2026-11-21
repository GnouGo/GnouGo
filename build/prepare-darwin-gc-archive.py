"""Disambiguate the two contractpointerdata objects in the .NET 11 macOS debug map."""
from pathlib import Path
import sys


def prepare(source: Path, output: Path) -> None:
    archive = bytearray(source.read_bytes())
    if not archive.startswith(b"!<arch>\n"):
        raise ValueError("Expected a Darwin ar archive")
    offset = 8
    occurrences = 0
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
            name = archive[start:start + length].rstrip(b"\0")
            if name == b"contractpointerdata.cpp.o":
                occurrences += 1
                if occurrences == 2:
                    # Same length. Every object byte and symbol-table offset stays intact.
                    archive[start:start + len(name)] = b"contractpointerdat2.cpp.o"
        offset = start + size + size % 2
    if occurrences > 2:
        raise ValueError("Unexpected duplicate runtime object layout")
    output.write_bytes(archive)


if __name__ == "__main__":
    prepare(Path(sys.argv[1]), Path(sys.argv[2]))
