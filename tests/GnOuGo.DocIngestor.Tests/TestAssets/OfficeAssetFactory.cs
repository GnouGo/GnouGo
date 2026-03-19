using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;

namespace DocIngestor.Tests.TestAssets;

internal static class OfficeAssetFactory
{
    public static string CreateDocx(string folder, string fileName = "sample.docx")
    {
        var path = Path.Combine(folder, fileName);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);

        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(
            new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Hello DOCX"))),
            new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Second paragraph about cats."))),
            new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Third paragraph about cats and kittens.")))
        ));
        main.Document.Save();
        return path;
    }

    public static string CreatePptx(string folder, string fileName = "sample.pptx")
    {
        var path = Path.Combine(folder, fileName);
        using var pres = PresentationDocument.Create(path, PresentationDocumentType.Presentation);

        var presPart = pres.AddPresentationPart();
        presPart.Presentation = new P.Presentation(new P.SlideIdList());

        AddSlide(presPart, "Slide 1 title", "Hello PPTX");
        AddSlide(presPart, "Slide 2 title", "Another slide text");

        presPart.Presentation.Save();
        return path;

        static void AddSlide(PresentationPart presPart, string title, string bodyText)
        {
            var slidePart = presPart.AddNewPart<SlidePart>();
            slidePart.Slide = new P.Slide(
                new P.CommonSlideData(
                    new P.ShapeTree(
                        new P.NonVisualGroupShapeProperties(
                            new P.NonVisualDrawingProperties() { Id = 1, Name = "" },
                            new P.NonVisualGroupShapeDrawingProperties(),
                            new P.ApplicationNonVisualDrawingProperties()
                        ),
                        new P.GroupShapeProperties(new A.TransformGroup()),
                        CreateTextShape(2, title),
                        CreateTextShape(3, bodyText)
                    )
                )
            );
            slidePart.Slide.Save();

            var slideIdList = presPart.Presentation.SlideIdList!;
            uint maxId = 256;
            if (slideIdList.ChildElements.Count > 0)
                maxId = slideIdList.ChildElements.OfType<P.SlideId>().Max(s => s.Id!.Value) + 1;

            slideIdList.Append(new P.SlideId() { Id = maxId, RelationshipId = presPart.GetIdOfPart(slidePart) });

            static P.Shape CreateTextShape(uint id, string text)
            {
                return new P.Shape(
                    new P.NonVisualShapeProperties(
                        new P.NonVisualDrawingProperties() { Id = id, Name = $"TextBox{id}" },
                        new P.NonVisualShapeDrawingProperties(new A.ShapeLocks() { NoGrouping = true }),
                        new P.ApplicationNonVisualDrawingProperties()
                    ),
                    new P.ShapeProperties(),
                    new P.TextBody(
                        new A.BodyProperties(),
                        new A.ListStyle(),
                        new A.Paragraph(new A.Run(new A.Text(text)))
                    )
                );
            }
        }
    }

    public static string CreateXlsx(string folder, string fileName = "sample.xlsx")
    {
        var path = Path.Combine(folder, fileName);
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);

        var wbPart = doc.AddWorkbookPart();
        wbPart.Workbook = new Workbook();

        var wsPart = wbPart.AddNewPart<WorksheetPart>();
        wsPart.Worksheet = new Worksheet(new SheetData());

        var sheets = doc.WorkbookPart!.Workbook.AppendChild(new Sheets());
        var sheet = new Sheet()
        {
            Id = doc.WorkbookPart.GetIdOfPart(wsPart),
            SheetId = 1,
            Name = "Sheet1"
        };
        sheets.Append(sheet);

        var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>()!;
        sheetData.Append(
            new Row(
                new Cell() { CellValue = new CellValue("Hello"), DataType = CellValues.String },
                new Cell() { CellValue = new CellValue("XLSX"), DataType = CellValues.String }
            ),
            new Row(
                new Cell() { CellValue = new CellValue("Row2Col1"), DataType = CellValues.String },
                new Cell() { CellValue = new CellValue("Row2Col2"), DataType = CellValues.String }
            )
        );

        wbPart.Workbook.Save();
        wsPart.Worksheet.Save();
        return path;
    }
}
