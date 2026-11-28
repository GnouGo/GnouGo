"""Inspect the referenced native chart, its caches and the actual embedded worksheet."""
import argparse
import hashlib
import io
import json
import posixpath
import re
import zipfile
from decimal import Decimal
from pathlib import Path
from xml.etree import ElementTree as ET

NS = {"c": "http://schemas.openxmlformats.org/drawingml/2006/chart",
      "p": "http://schemas.openxmlformats.org/presentationml/2006/main",
      "a": "http://schemas.openxmlformats.org/drawingml/2006/main",
      "r": "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
      "s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main",
      "ct": "http://schemas.openxmlformats.org/package/2006/content-types"}
RID = "{" + NS["r"] + "}id"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def related(package, part, rid, kind):
    folder, name = posixpath.split(part)
    rels = ET.fromstring(package.read(posixpath.join(folder, "_rels", name + ".rels")))
    matches = [r for r in rels if r.get("Id") == rid and r.get("Type") == NS["r"] + "/" + kind]
    require(len(matches) == 1 and matches[0].get("TargetMode", "Internal") == "Internal", f"Missing internal {kind} relationship from {part}")
    target = matches[0].attrib["Target"]
    path = posixpath.normpath(posixpath.join(folder, target)).lstrip("/")
    require(not path.startswith("../") and path in package.namelist(), f"Missing package part {path}")
    return path


def cells(book, name):
    workbook = related(book, "", "rId1", "officeDocument") if "xl/workbook.xml" not in book.namelist() else "xl/workbook.xml"
    sheets = ET.fromstring(book.read(workbook)).findall("s:sheets/s:sheet", NS)
    sheet, = (s for s in sheets if s.get("name") == name)
    path = related(book, workbook, sheet.attrib[RID], "worksheet")
    strings = []
    if "xl/sharedStrings.xml" in book.namelist():
        strings = ["".join(si.itertext()) for si in ET.fromstring(book.read("xl/sharedStrings.xml"))]
    values = {}
    for cell in ET.fromstring(book.read(path)).findall(".//s:sheetData/s:row/s:c", NS):
        value = cell.findtext("s:v", namespaces=NS)
        if cell.get("t") == "inlineStr":
            value = "".join(t.text or "" for t in cell.findall(".//s:t", NS))
        elif cell.get("t") == "s":
            value = strings[int(value)]
        elif value is not None:
            value = Decimal(value)
        values[cell.attrib["r"]] = value
    return values


def verify_reference(series, field, kind, expected, book):
    ref = series.find(f"c:{field}/c:{kind}Ref", NS)
    require(ref is not None, f"Missing {field} worksheet reference")
    formula = ref.findtext("c:f", namespaces=NS) or ""
    match = re.fullmatch(r"('?[^'!]*'?)!\$?([A-Z]+)\$?(\d+)(?::\$?([A-Z]+)\$?(\d+))?", formula)
    require(match, f"Invalid {field} formula {formula}")
    name, first, start, last, end = match.groups()
    require(last in (None, first) and int(end or start) - int(start) + 1 == len(expected), f"Incorrect {field} range")
    sheet = cells(book, name.strip("'"))
    points = ref.findall(f"c:{kind}Cache/c:pt", NS)
    require(len(points) == len(expected), f"Incorrect {field} cache size")
    count = ref.find(f"c:{kind}Cache/c:ptCount", NS)
    require(count is not None and int(count.attrib["val"]) == len(expected), f"Incorrect {field} point count")
    for i, (point, wanted) in enumerate(zip(points, expected)):
        cached = point.findtext("c:v", namespaces=NS)
        if kind == "num":
            cached = Decimal(cached)
        require(int(point.attrib["idx"]) == i and cached == wanted and sheet[first + str(int(start) + i)] == wanted,
                f"{field} cache, workbook or expected value differs at index {i}")
    return formula


def verify(file, slide_index, expected_title, series_name, categories, values):
    with zipfile.ZipFile(file) as deck:
        pres = "ppt/presentation.xml"
        slides = ET.fromstring(deck.read(pres)).findall("p:sldIdLst/p:sldId", NS)
        slide = related(deck, pres, slides[slide_index].attrib[RID], "slide")
        tree = ET.fromstring(deck.read(slide))
        require(expected_title in [t.text for t in tree.findall(".//a:t", NS)], "Expected slide text missing")
        chart, = tree.findall(".//c:chart", NS)
        chart_part = related(deck, slide, chart.attrib[RID], "chart")
        chart_xml = ET.fromstring(deck.read(chart_part))
        series, = chart_xml.findall(".//c:barChart/c:ser", NS)
        external = chart_xml.find("c:externalData", NS)
        require(external is not None, "Native chart lacks c:externalData")
        embed = related(deck, chart_part, external.attrib[RID], "package")
        types = ET.fromstring(deck.read("[Content_Types].xml"))
        overrides = {p.get("PartName"): p.get("ContentType") for p in types.findall("ct:Override", NS)}
        defaults = {p.get("Extension"): p.get("ContentType") for p in types.findall("ct:Default", NS)}
        require(overrides.get("/" + embed, defaults.get("xlsx")) ==
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Incorrect workbook content type")
        data = deck.read(embed)
        with zipfile.ZipFile(io.BytesIO(data)) as book:
            formulas = [verify_reference(series, field, kind, expected, book) for field, kind, expected in
                        [("tx", "str", [series_name]), ("cat", "str", categories), ("val", "num", values)]]
    return {"pptx_sha256": hashlib.sha256(file.read_bytes()).hexdigest(), "slides": len(slides),
            "slide": slide, "chart": chart_part, "workbook": embed, "workbook_sha256": hashlib.sha256(data).hexdigest(),
            "formulas": formulas, "title": expected_title, "categories": categories, "values": [str(v) for v in values]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file", type=Path)
    parser.add_argument("--slide", type=int, default=0, help="zero-based slide position")
    parser.add_argument("--title", required=True)
    parser.add_argument("--series", required=True)
    parser.add_argument("--categories", nargs="+", required=True)
    parser.add_argument("--values", nargs="+", type=Decimal, required=True)
    args = parser.parse_args()
    print(json.dumps(verify(args.file, args.slide, args.title, args.series, args.categories, args.values), indent=2))
