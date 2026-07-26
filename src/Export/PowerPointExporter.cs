using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using SnapDoc.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace SnapDoc.Export;

/// <summary>
/// Exports captures to a .pptx, one capture per slide, image centred with the title on top.
/// Minimal but valid OpenXML presentation. Extend the slide layout here for fancier decks.
///
/// NOTE: full PPTX OpenXML is lengthy; this builds a clean minimal deck. If you find slide
/// scaling fiddly, the pragmatic alternative is to export PDF and let users import that.
/// </summary>
public sealed class PowerPointExporter : IExporter
{
    public string FormatName => "PowerPoint";
    public string Extension => ".pptx";

    // 16:9 slide in EMU
    private const long SlideW = 12192000;
    private const long SlideH = 6858000;

    public Task ExportAsync(IReadOnlyList<Capture> captures, string outputPath, ExportOptions options)
    {
        using var doc = PresentationDocument.Create(outputPath, PresentationDocumentType.Presentation);
        var presPart = doc.AddPresentationPart();
        presPart.Presentation = new Presentation();

        var slideMasterPart = AddSlideMaster(presPart);
        var slideLayoutPart = slideMasterPart.SlideLayoutParts.First();

        var slideIdList = new SlideIdList();
        uint slideId = 256;

        for (int i = 0; i < captures.Count; i++)
        {
            var c = captures[i];
            var slidePart = presPart.AddNewPart<SlidePart>();
            slidePart.Slide = BuildSlide(slidePart, c, i, options);
            slidePart.AddPart(slideLayoutPart);

            string rId = presPart.GetIdOfPart(slidePart);
            slideIdList.Append(new SlideId { Id = slideId++, RelationshipId = rId });
        }

        presPart.Presentation.Append(slideIdList);
        presPart.Presentation.Append(new SlideSize { Cx = (int)SlideW, Cy = (int)SlideH });
        presPart.Presentation.Append(new NotesSize { Cx = 6858000, Cy = 9144000 });
        presPart.Presentation.Save();
        return Task.CompletedTask;
    }

    private static Slide BuildSlide(SlidePart slidePart, Capture c, int index, ExportOptions options)
    {
        var tree = new ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()));

        // title text box
        string heading = string.IsNullOrWhiteSpace(c.Title) ? $"Step {index + 1}" : c.Title;
        string title = options.AsStepGuide ? $"Step {index + 1}: {heading}" : heading;
        tree.Append(TextBox(title, 400050, 200000, SlideW - 800100, 800000, 2U));

        // embed the flattened image, fit into the area below the title
        byte[] png = CaptureFlattener.FlattenToPng(c);
        var imagePart = slidePart.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream(png)) imagePart.FeedData(ms);
        string rId = slidePart.GetIdOfPart(imagePart);

        long areaX = 400050, areaY = 1100000, areaW = SlideW - 800100, areaH = SlideH - 1400000;
        double scale = System.Math.Min((double)areaW / c.PixelWidth, (double)areaH / c.PixelHeight);
        long imgW = (long)(c.PixelWidth * scale), imgH = (long)(c.PixelHeight * scale);
        long imgX = areaX + (areaW - imgW) / 2, imgY = areaY + (areaH - imgH) / 2;

        tree.Append(PicShape(rId, imgX, imgY, imgW, imgH, 3U));

        return new Slide(new CommonSlideData(tree), new ColorMapOverride(new A.MasterColorMapping()));
    }

    private static P.Shape TextBox(string text, long x, long y, long cx, long cy, uint id) =>
        new(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Title" },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            new P.TextBody(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(new A.Run(
                    new A.RunProperties { Language = "en-US", FontSize = 2400, Bold = true },
                    new A.Text(text)))));

    private static P.Picture PicShape(string rId, long x, long y, long cx, long cy, uint id) =>
        new(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Capture" },
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                new ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(
                new A.Blip { Embed = rId },
                new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));

    /// <summary>Build a minimal valid slide master + layout (required or PowerPoint rejects the file).</summary>
    private static SlideMasterPart AddSlideMaster(PresentationPart presPart)
    {
        var masterPart = presPart.AddNewPart<SlideMasterPart>();
        var tree = new ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()));
        masterPart.SlideMaster = new SlideMaster(
            new CommonSlideData(tree),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1, Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2, Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1, Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3, Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5, Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
            },
            new SlideLayoutIdList());

        var themePart = masterPart.AddNewPart<ThemePart>();
        themePart.Theme = MinimalTheme();
        themePart.Theme.Save();

        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
        var ltree = new ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()));
        layoutPart.SlideLayout = new SlideLayout(
            new CommonSlideData(ltree),
            new ColorMapOverride(new A.MasterColorMapping())) { Type = SlideLayoutValues.Blank };

        masterPart.SlideMaster.SlideLayoutIdList!.Append(
            new SlideLayoutId { Id = 2147483649U, RelationshipId = masterPart.GetIdOfPart(layoutPart) });
        masterPart.SlideMaster.Save();

        presPart.Presentation.Append(new SlideMasterIdList(
            new SlideMasterId { Id = 2147483648U, RelationshipId = presPart.GetIdOfPart(masterPart) }));
        return masterPart;
    }

    private static A.Theme MinimalTheme()
    {
        // A theme is mandatory. This is the smallest one PowerPoint accepts.
        return new A.Theme(
            new A.ThemeElements(
                new A.ColorScheme(
                    new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText }),
                    new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window }),
                    new A.Dark2Color(new A.RgbColorModelHex { Val = "1F497D" }),
                    new A.Light2Color(new A.RgbColorModelHex { Val = "EEECE1" }),
                    new A.Accent1Color(new A.RgbColorModelHex { Val = "4F81BD" }),
                    new A.Accent2Color(new A.RgbColorModelHex { Val = "C0504D" }),
                    new A.Accent3Color(new A.RgbColorModelHex { Val = "9BBB59" }),
                    new A.Accent4Color(new A.RgbColorModelHex { Val = "8064A2" }),
                    new A.Accent5Color(new A.RgbColorModelHex { Val = "4BACC6" }),
                    new A.Accent6Color(new A.RgbColorModelHex { Val = "F79646" }),
                    new A.Hyperlink(new A.RgbColorModelHex { Val = "0000FF" }),
                    new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "800080" })) { Name = "Office" },
                new A.FontScheme(
                    new A.MajorFont(new A.LatinFont { Typeface = "Segoe UI" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" }),
                    new A.MinorFont(new A.LatinFont { Typeface = "Segoe UI" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" })) { Name = "Office" },
                new A.FormatScheme(
                    new A.FillStyleList(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })),
                    new A.LineStyleList(new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })), new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })), new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }))),
                    new A.EffectStyleList(new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList())),
                    new A.BackgroundFillStyleList(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }))) { Name = "Office" }),
            new A.ObjectDefaults()) { Name = "Office Theme" };
    }
}
