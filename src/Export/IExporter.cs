using System.Collections.Generic;
using System.Threading.Tasks;
using SnapDoc.Models;

namespace SnapDoc.Export;

/// <summary>
/// Writes one or more captures to a documentation file. Implementations exist per format:
/// Markdown, PDF, Word (.docx), PowerPoint (.pptx). Each renders captures (flattened with
/// annotations) plus their titles/captions, and for step-guides, the numbered steps.
/// </summary>
public interface IExporter
{
    /// <summary>Format name for the UI ("Markdown", "PDF", "Word", "PowerPoint").</summary>
    string FormatName { get; }

    /// <summary>File extension including the dot (".md", ".pdf", ".docx", ".pptx").</summary>
    string Extension { get; }

    /// <summary>
    /// Export a document. <paramref name="captures"/> is in order; for a step guide this is
    /// the sequence the reader follows. <paramref name="outputPath"/> is the target file.
    /// </summary>
    Task ExportAsync(IReadOnlyList<Capture> captures, string outputPath, ExportOptions options);
}

/// <summary>Knobs shared across exporters.</summary>
public sealed class ExportOptions
{
    /// <summary>Document title / deck title / H1.</summary>
    public string DocumentTitle { get; set; } = "SnapDoc Export";

    /// <summary>Author line, where the format supports it.</summary>
    public string Author { get; set; } = "";

    /// <summary>
    /// If true, emit a numbered step guide: each capture is "Step N", using its Title as the
    /// step heading and Caption as the step text. If false, a plain gallery of images.
    /// </summary>
    public bool AsStepGuide { get; set; } = true;

    /// <summary>Include OCR text under each image where available.</summary>
    public bool IncludeOcrText { get; set; } = false;

    /// <summary>Folder for extracted image assets (Markdown needs this; others embed).</summary>
    public string? AssetFolder { get; set; }
}
