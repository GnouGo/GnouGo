"""Independent ZIP/XML check against retained local producer observations only."""
import gzip
import hashlib
import json
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

source, destination = map(Path, sys.argv[1:])
ns = {"s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
reports = []
for variant in ("complete", "one", "empty", "missing", "repeated"):
    path = source / f"workbook-{variant}.xlsx"
    with zipfile.ZipFile(path) as archive:
        strings = []
        if "xl/sharedStrings.xml" in archive.namelist():
            strings = ["".join(n.itertext()) for n in ET.fromstring(archive.read("xl/sharedStrings.xml"))]
        sheet = ET.fromstring(archive.read("xl/worksheets/sheet1.xml"))
        assert not sheet.findall(".//s:f", ns), "Observed strings must not become formulas"
        rows = []
        for row in sheet.findall(".//s:row", ns):
            cells = [""] * 3
            for ordinal, cell in enumerate(row.findall("s:c", ns)):
                reference = cell.get("r")
                index = ord(reference[0]) - ord("A") if reference else ordinal
                assert 0 <= index < 3
                if cell.get("t") == "inlineStr":
                    text = "".join(n.text or "" for n in cell.findall(".//s:t", ns))
                else:
                    value = cell.find("s:v", ns)
                    text = "" if value is None else value.text or ""
                    if cell.get("t") == "s":
                        text = strings[int(text)]
                cells[index] = text
            rows.append(cells)
    observation_path = source / f"observations-{variant}.json"
    events = json.loads(observation_path.read_text() if observation_path.exists()
                      else gzip.decompress(observation_path.with_suffix(".json.gz").read_bytes()))
    reads = [e for e in events if e["tool"] == "browser_get_content"]
    selected = [] if variant == "empty" else [0] if variant == "one" else list(range(9)) + [0] if variant == "repeated" else list(range(10))
    assert len(reads) == len(selected) + 1 and all(not e["error"] for e in reads)
    for event in reads:
        snapshot = event["result"]["observationSnapshot"]
        assert not snapshot["captureTruncated"] and not snapshot["manifestTruncated"]
        assert sum(len(p["records"]) for p in snapshot["pages"]) == snapshot["recordCount"]
    offered = [r for p in reads[0]["result"]["observationSnapshot"]["pages"] for r in p["records"] if r["tag"] == "a"]
    assert rows[0] == ["Nom", "Description", "Prix"] and len(rows) == len(selected) + 1
    assert [r["arguments"]["url"] for r in reads[1:]] == [offered[i]["href"] for i in selected]
    for row, event, product in zip(rows[1:], reads[1:], selected, strict=True):
        records = [r for p in event["result"]["observationSnapshot"]["pages"] for r in p["records"]]
        expected = []
        for tag in ("h1", "p", "price"):
            values = [r["text"] for r in records if r["tag"] == tag]
            # These tags describe this controlled site, not a production heuristic.
            absent = variant == "missing" and (product == 3 and tag == "p" or product == 2 and tag == "price")
            assert len(values) == (0 if absent else 1)
            expected.append("" if absent else values[0])
        assert row == expected
    writes = [e for e in events if e["tool"] == "document_write"]
    assert len(writes) == 1 and not writes[0]["error"]
    assert rows == [["" if c is None else c for c in row] for row in writes[0]["arguments"]["rows"]]
    assert writes[0]["result"]["filePath"].endswith("/workflows/observed-product-extraction/products.xlsx")
    closes = [e for e in events if e["tool"] == "browser_close"]
    assert len(closes) == 1 and not closes[0]["error"] and events[-1] == closes[0]
    reports.append({"variant": variant, "xlsx_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "rows_including_header": len(rows), "product_visits": len(selected), "complete_acquisitions": len(reads),
        "exact_original_cells": True, "original_navigation_order_and_arguments": True, "no_formulas": True,
        "blank_positions_preserved": True, "writer_path_verified": True, "cleanup_recorded": True,
        "absence_basis": "controlled local site and complete snapshots" if variant in ("empty", "missing") else None})
destination.write_text(json.dumps(reports, indent=2) + "\n")
print(json.dumps(reports, indent=2))
