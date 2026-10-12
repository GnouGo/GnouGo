"""Inspect disposable local fixture artifacts without the workflow's product outputs."""
from pathlib import Path
from urllib.parse import urlparse
import hashlib
import json
import shutil
import sys
import xml.etree.ElementTree as ET
import zipfile

source, destination = map(Path, sys.argv[1:])
destination.mkdir(exist_ok=True)
ns = {"s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
report = []
for variant in ["complete", "redirected", "repeated", "one", "missing", "empty"]:
    path = source / f"workbook-{variant}.xlsx"
    events = json.loads((source / f"observations-{variant}.json").read_text())
    with zipfile.ZipFile(path) as archive:
        sheet = ET.fromstring(archive.read("xl/worksheets/sheet1.xml"))
        assert not sheet.findall(".//s:f", ns)
        rows = [["".join(cell.itertext()) for cell in row.findall("s:c", ns)]
                for row in sheet.findall(".//s:row", ns)]
    selected = ([] if variant == "empty" else [0] if variant == "one" else
                list(range(9)) + [0] if variant == "repeated" else list(range(10)))
    expected = [["name", "description", "price", "error"]] + [
        [f"=Observed lamp {i % 3}",
         "" if variant == "missing" and i == 3 else f"Complete observed block {i} — café; all supplied words retained.",
         "" if variant == "missing" and i == 2 else f"{10 + i},00 €", ""] for i in selected]
    assert rows == expected, variant
    visits = [event for event in events if event["tool"] == "browser_get_content"][1:]
    assert [urlparse(event["result"]["url"]).path for event in visits] == [f"/entry/{i}" for i in selected]
    assert all(not event["error"] and event["result"]["success"]
               and not event["result"]["observationSnapshot"]["captureTruncated"]
               and not event["result"]["observationSnapshot"]["manifestTruncated"] for event in visits)
    if variant == "redirected":
        assert all(urlparse(event["arguments"]["url"]).path == "/relay" for event in visits)
    writer = next(event for event in events if event["tool"] == "document_write")
    result = writer["result"]
    assert result["success"] and result["relativePath"] == writer["arguments"]["filePath"]
    assert result["filePath"] == result["filePathAbsolute"]
    assert result["filePath"].endswith("/workspace/" + result["relativePath"])
    close = events[-1]
    assert close["tool"] == "browser_close" and close["result"]["closed"] and close["result"]["success"]
    shutil.copyfile(path, destination / path.name)
    report.append({"variant": variant, "xlsx_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                   "rows_including_header": len(rows), "visited_product_occurrences": len(visits),
                   "source_records_complete": True, "exact_expected_cells": True, "formulas": 0,
                   "writer_relative_and_absolute_paths_match": True, "closed": True,
                   "independent_live_oracle_expected": variant not in ["missing", "empty"]})
(destination / "inspection.json").write_text(json.dumps(report, indent=2) + "\n")
print(json.dumps(report, indent=2))
