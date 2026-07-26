using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SnapDoc.Models;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace SnapDoc.Export;

/// <summary>
/// Exports captures to a .docx step guide via OpenXML. Images are embedded as PNG parts.
/// OpenXML is verbose but has zero runtime dependency on Word being installed.
/// </summary>
public sealed class WordExporter : IExporter
{
    public string FormatName => "Word";
    public string Extension => ".docx";

    public Task ExportAsync(IReadOnlyList<Capture> captures, string outputPath, ExportOptions options)
    {
        using var doc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;

        body.Append(Heading(options.DocumentTitle, "Title"));
        if (!string.IsNullOrWhiteSpace(options.Author))
            body.Append(Para($"By {options.Author}", italic: true));

        for (int i = 0; i < captures.Count; i++)
        {
            var c = captures[i];
            string heading = string.IsNullOrWhiteSpace(c.Title) ? $"Step {i + 1}" : c.Title;
            string title = options.AsStepGuide ? $"Step {i + 1}: {heading}" : heading;
            body.Append(Heading(title, "Heading1"));

            if (!string.IsNullOrWhiteSpace(c.Caption))
                body.Append(Para(c.Caption));

            byte[] png = CaptureFlattener.FlattenToPng(c);
            body.Append(ImageParagraph(main, png, c.PixelWidth, c.PixelHeight));

            if (options.IncludeOcrText && !string.IsNullOrWhiteSpace(c.OcrText))
                body.Append(Para(c.OcrText!, italic: true));
        }

        main.Document.Save();
        return Task.CompletedTask;
    }

    private static Paragraph Heading(string text, string style) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = style }),
            new Run(new Text(text)));

    private static Paragraph Para(string text, bool italic = false)
    {
        var rp = new RunProperties();
        if (italic) rp.Append(new Italic());
        return new Paragraph(new Run(rp, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    /// <summary>Embed a PNG and return a paragraph containing it, scaled to a max width in EMUs.</summary>
    private static Paragraph ImageParagraph(MainDocumentPart main, byte[] png, int pxW, int pxH)
    {
        var imagePart = main.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream(png)) imagePart.FeedData(ms);
        string relId = main.GetIdOfPart(imagePart);

        // Scale to a max width of ~6 inches (5486400 EMU); 1 inch = 914400 EMU, assume 96 dpi.
        const long maxWidthEmu = 5486400;
        long wEmu = (long)(pxW / 96.0 * 914400);
        long hEmu = (long)(pxH / 96.0 * 914400);
        if (wEmu > maxWidthEmu) { double s = (double)maxWidthEmu / wEmu; wEmu = maxWidthEmu; hEmu = (long)(hEmu * s); }

        var element = new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = wEmu, Cy = hEmu },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = 1U, Name = "Capture" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = "Capture.png" },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(
                            new A.Blip { Embed = relId },
                            new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(
                                new A.Offset { X = 0, Y = 0 },
                                new A.Extents { Cx = wEmu, Cy = hEmu }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))
                    ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })
            ) { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });

        return new Paragraph(new Run(element));
    }
}
