"""Independent XML/producer-evidence inspection; no inference or external I/O."""
from pathlib import Path
import gzip
import hashlib
import json
import sys
import xml.etree.ElementTree as ET
import zipfile

source = Path(sys.argv[1])
reports = []
for variant in ("complete", "missing", "repeated", "empty"):
    book = source / f"workbook-{variant}.xlsx"
    raw = gzip.decompress((source / f"observations-{variant}.json.gz").read_bytes())
    events = json.loads(raw)
    acquisitions = [e for e in events if e["tool"] == "browser_get_content"]
    visits = acquisitions[1:]
    assert len(visits) == (0 if variant == "empty" else 10)
    with zipfile.ZipFile(book) as archive:
        sheet = ET.fromstring(archive.read("xl/worksheets/sheet1.xml"))
        assert not sheet.findall(".//{*}f")
        rows = [["".join(cell.itertext()) for cell in row] for row in sheet.findall(".//{*}row")]
    assert rows[0] == ["Nom", "Description", "Prix"]
    assert len(rows) == len(visits) + 1
    originals = [r for p in acquisitions[0]["result"]["observationSnapshot"]["pages"] for r in p["records"]]
    offered_urls = [r["href"] for r in originals if r["tag"] == "a"]
    expected_urls = offered_urls[:9] + offered_urls[:1] if variant == "repeated" else offered_urls[:10]
    assert [e["arguments"]["url"] for e in visits] == expected_urls
    for row, event in zip(rows[1:], visits):
        snapshot = event["result"]["observationSnapshot"]
        assert not snapshot.get("captureTruncated", False)
        records = [r for p in snapshot["pages"] for r in p["records"]]
        expected = []
        for tag in ("h1", "p", "price"):
            matches = [r["text"] for r in records if r["tag"] == tag]
            assert len(matches) <= 1
            expected.append(matches[0] if matches else "")
        assert row == expected, (variant, row, expected)
    writer = next(e for e in events if e["tool"] == "document_write")
    assert writer["result"]["success"] and not writer["error"]
    assert writer["result"]["filePath"].endswith(writer["arguments"]["filePath"])
    assert writer["arguments"]["filePath"] == "workflows/schema-portability-20261002/initialrequirements20261015a-amazon-1/products.xlsx"
    assert events[-1]["tool"] == "browser_close" and events[-1]["result"]["closed"] and events[-1]["result"]["success"]
    if variant == "missing":
        assert rows[3][2] == "" and rows[4][1] == ""
    if variant == "repeated":
        assert rows[1] == rows[-1]
    reports.append({"variant": variant, "passed": True, "visits": len(visits), "rowsWithHeader": len(rows),
                    "exactObservedCells": True, "originalNavigationArguments": True, "cleanup": True,
                    "workbookSha256": hashlib.sha256(book.read_bytes()).hexdigest(),
                    "observationsSha256": hashlib.sha256(raw).hexdigest()})
print(json.dumps(reports, indent=2))
