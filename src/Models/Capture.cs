using System;
using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;

namespace SnapDoc.Models;

/// <summary>
/// One captured image plus everything layered on top of it.
/// This is the central document object: the editor edits it, the exporters read it,
/// the OCR engine fills in <see cref="OcrText"/>, and the workspace lists it.
/// </summary>
public sealed class Capture
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>When the capture was taken. Used for workspace sorting and default file names.</summary>
    public DateTime CreatedAt { get; } = DateTime.Now;

    /// <summary>User-editable title. Shows in the workspace and becomes the step heading on export.</summary>
    public string Title { get; set; } = "Untitled capture";

    /// <summary>Optional caption/description. Becomes the step body text on documentation export.</summary>
    public string Caption { get; set; } = "";

    /// <summary>
    /// The base bitmap, before annotations. Not mutated in place, but the reference can be
    /// replaced wholesale -- e.g. Crop swaps in a smaller <see cref="System.Windows.Media.Imaging.CroppedBitmap"/>.
    /// </summary>
    public required BitmapSource Image { get; set; }

    /// <summary>Pixel width/height of <see cref="Image"/>.</summary>
    public int PixelWidth => Image.PixelWidth;
    public int PixelHeight => Image.PixelHeight;

    /// <summary>
    /// Annotations drawn on top, in z-order (first = bottom). The editor renders
    /// Image then these; exporters flatten Image + these into one bitmap.
    /// </summary>
    public ObservableCollection<Annotation> Annotations { get; } = new();

    /// <summary>Text extracted by OCR, or null if OCR hasn't run. See <see cref="Ocr.IOcrEngine"/>.</summary>
    public string? OcrText { get; set; }

    /// <summary>Where the flattened PNG lives on disk once saved. Null until first save.</summary>
    public string? FilePath { get; set; }

    /// <summary>Suggested file name stem, e.g. "SnapDoc 2026-07-25 14-32-08".</summary>
    public string SuggestedFileName =>
        $"SnapDoc {CreatedAt:yyyy-MM-dd HH-mm-ss}";

    /// <summary>Freeform user labels, shown as chips in the workspace and searchable.</summary>
    public ObservableCollection<string> Tags { get; } = new();

    /// <summary>How this capture was taken (e.g. "Region capture"), for display only.</summary>
    public string CaptureKind { get; init; } = "Screenshot";
}
