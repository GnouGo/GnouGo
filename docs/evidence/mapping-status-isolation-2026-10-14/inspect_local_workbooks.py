"""Independent ZIP/XML inspection of local fixture copies; never opens a live site."""
import hashlib
import json
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

source, destination = map(Path, sys.argv[1:])
ns = {"s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
reports = []
for variant in ("complete", "missing"):
    content = (source / f"workbook-{variant}.xlsx").read_bytes()
    with zipfile.ZipFile(source / f"workbook-{variant}.xlsx") as archive:
        strings = []
        if "xl/sharedStrings.xml" in archive.namelist():
            strings = ["".join(n.itertext()) for n in ET.fromstring(archive.read("xl/sharedStrings.xml"))]
        sheet = ET.fromstring(archive.read("xl/worksheets/sheet1.xml"))
        assert not sheet.findall(".//s:f", ns), "Observed strings must not become formulas"
        rows = []
        for row in sheet.findall(".//s:row", ns):
            cells = [""] * 4
            for ordinal, cell in enumerate(row.findall("s:c", ns)):
                reference = cell.get("r")
                index = ord(reference[0]) - ord("A") if reference else ordinal
                assert 0 <= index < 4
                if cell.attrib.get("t") == "inlineStr":
                    text = "".join(n.text or "" for n in cell.findall(".//s:t", ns))
                else:
                    value = cell.find("s:v", ns)
                    text = "" if value is None else value.text or ""
                    if cell.attrib.get("t") == "s":
                        text = strings[int(text)]
                cells[index] = text
            rows.append(cells)
    events = json.loads((source / f"observations-{variant}.json").read_text())
    reads = [e for e in events if e["tool"] == "browser_get_content"]
    assert len(reads) == 11 and all(not e["error"] for e in reads)
    for event in reads:
        snapshot = event["result"]["observationSnapshot"]
        assert not snapshot["captureTruncated"] and not snapshot["manifestTruncated"]
        assert sum(len(p["records"]) for p in snapshot["pages"]) == snapshot["recordCount"]
    offered_urls = {r["href"] for p in reads[0]["result"]["observationSnapshot"]["pages"] for r in p["records"] if r.get("href")}
    assert rows[0] == ["name", "description", "price", "error"] and len(rows) == 11
    for index, (row, event) in enumerate(zip(rows[1:], reads[1:], strict=True)):
        assert event["arguments"]["url"] in offered_urls
        records = [r for p in event["result"]["observationSnapshot"]["pages"] for r in p["records"]]
        expected = []
        for tag in ("h1", "p", "price"):
            candidates = [r["text"] for r in records if r["tag"] == tag]
            absent = variant == "missing" and (index == 3 and tag == "p" or index == 2 and tag == "price")
            assert len(candidates) == (0 if absent else 1)
            expected.append("" if absent else candidates[0])
        assert row == expected + [""]
    writes = [e for e in events if e["tool"] == "document_write"]
    assert len(writes) == 1 and not writes[0]["error"]
    assert rows == [["" if c is None else c for c in row] for row in writes[0]["arguments"]["rows"]]
    closes = [e for e in events if e["tool"] == "browser_close"]
    assert len(closes) == 1 and not closes[0]["error"] and events[-1] == closes[0]
    reports.append({"variant": variant, "xlsx_sha256": hashlib.sha256(content).hexdigest(),
        "rows_including_header": len(rows), "product_visits": 10, "complete_acquisitions": len(reads),
        "exact_original_cells": True, "original_navigation_arguments": True, "no_formulas": True,
        "blank_cells_preserved": True, "cleanup_recorded": True,
        "absence_basis": "controlled local site and complete snapshots" if variant == "missing" else None})
destination.write_text(json.dumps(reports, indent=2) + "\n")
print(json.dumps(reports, indent=2))
