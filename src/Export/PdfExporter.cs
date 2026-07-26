using System.Collections.Generic;
using System.Threading.Tasks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SnapDoc.Models;

namespace SnapDoc.Export;

/// <summary>
/// Exports captures to a PDF step guide using QuestPDF. Images are embedded (no sidecar folder).
/// QuestPDF is MIT/community-licensed for this use; call QuestPDF.Settings.License once at startup
/// (done in App.xaml.cs) or it throws.
/// </summary>
public sealed class PdfExporter : IExporter
{
    public string FormatName => "PDF";
    public string Extension => ".pdf";

    public Task ExportAsync(IReadOnlyList<Capture> captures, string outputPath, ExportOptions options)
    {
        // pre-flatten so the document-build closure stays simple
        var pages = new List<(string heading, string caption, byte[] png, string? ocr)>();
        for (int i = 0; i < captures.Count; i++)
        {
            var c = captures[i];
            string heading = string.IsNullOrWhiteSpace(c.Title) ? $"Step {i + 1}" : c.Title;
            pages.Add((heading, c.Caption, CaptureFlattener.FlattenToPng(c),
                       options.IncludeOcrText ? c.OcrText : null));
        }

        Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Margin(40);
                page.Size(PageSizes.A4);
                page.DefaultTextStyle(t => t.FontFamily("Segoe UI").FontSize(11));

                page.Header().Column(col =>
                {
                    col.Item().Text(options.DocumentTitle).FontSize(20).Bold();
                    if (!string.IsNullOrWhiteSpace(options.Author))
                        col.Item().Text($"By {options.Author}").FontSize(10).Italic().FontColor(Colors.Grey.Medium);
                });

                page.Content().PaddingVertical(10).Column(col =>
                {
                    col.Spacing(18);
                    for (int i = 0; i < pages.Count; i++)
                    {
                        var (heading, caption, png, ocr) = pages[i];
                        col.Item().Column(step =>
                        {
                            step.Spacing(6);
                            string title = options.AsStepGuide ? $"Step {i + 1}: {heading}" : heading;
                            step.Item().Text(title).FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
                            if (!string.IsNullOrWhiteSpace(caption))
                                step.Item().Text(caption);
                            step.Item().Image(png).FitWidth();
                            if (!string.IsNullOrWhiteSpace(ocr))
                                step.Item().Background(Colors.Grey.Lighten4).Padding(6)
                                    .Text(ocr).FontSize(9).FontColor(Colors.Grey.Darken1);
                        });
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Page "); t.CurrentPageNumber(); t.Span(" / "); t.TotalPages();
                });
            });
        }).GeneratePdf(outputPath);

        return Task.CompletedTask;
    }
}
