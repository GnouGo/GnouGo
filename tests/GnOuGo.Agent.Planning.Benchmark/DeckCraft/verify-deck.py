"""Independently inspect both OOXML files of the eight-slide qualification."""
import json
import runpy
import sys
import zipfile
from decimal import Decimal
from pathlib import Path
from xml.etree import ElementTree as ET

chart = runpy.run_path(str(Path(__file__).with_name("verify-chart.py")))
ns, rid, related, require = (chart[key] for key in ("NS", "RID", "related", "require"))


def inspect(path, title, quantities):
    with zipfile.ZipFile(path) as deck:
        presentation = "ppt/presentation.xml"
        ids = ET.fromstring(deck.read(presentation)).findall("p:sldIdLst/p:sldId", ns)
        require(len(ids) == 8, "Expected eight slides")
        parts = [related(deck, presentation, item.attrib[rid], "slide") for item in ids]
        slides = [ET.fromstring(deck.read(p)) for p in parts]
        texts = lambda node: ["".join(t.itertext()) for t in node.findall(".//a:t", ns)]
        require(title in texts(slides[0]), "Expected editable title on slide one")
        require(len(slides[1].findall(".//p:sp", ns)) >= 3, "Expected editable diagram objects")
        table, = slides[2].findall(".//a:tbl", ns)
        rows = [["".join(texts(cell)) for cell in row.findall("a:tc", ns)] for row in table.findall("a:tr", ns)]
        expected = [["Étape", "Dossiers", "Vérifiés"], ["Planification", "12", "10"], ["Validation", "18", "16"], ["Exécution", "24", "21"]]
        require(rows == expected, "Wrong native table values or column order")
        picture, = slides[4].findall(".//p:pic/p:blipFill/a:blip", ns)
        image = related(deck, parts[4], picture.attrib["{" + ns["r"] + "}embed"], "image")
        require(deck.read(image).startswith(b"\x89PNG\r\n\x1a\n"), "Expected embedded illustration")
        for part in parts:
            folder, name = part.rsplit("/", 1)
            rels = ET.fromstring(deck.read(folder + "/_rels/" + name + ".rels"))
            notes, = (r for r in rels if r.get("Type") == ns["r"] + "/notesSlide")
            note = ET.fromstring(deck.read(related(deck, part, notes.get("Id"), "notesSlide")))
            bodies = [shape for shape in note.findall(".//p:sp", ns)
                      if shape.find("p:nvSpPr/p:nvPr/p:ph[@type='body']", ns) is not None]
            require(any(any(t.strip() for t in texts(b)) for b in bodies), "Missing speaker notes on " + part)
    result = chart["verify"](path, 3, None, "Dossiers", ["Nord", "Centre", "Sud"], [Decimal(x) for x in quantities])
    return dict(result, title=title, table=rows, illustration=image)


if __name__ == "__main__":
    root = Path(sys.argv[1])
    print(json.dumps({"before": inspect(root / "before.pptx", "GnOuGo en équipe", [12, 18, 24]),
                      "after": inspect(root / "after.pptx", "GnOuGo en équipe — édition révisée", [12, 20, 24])}, indent=2))
