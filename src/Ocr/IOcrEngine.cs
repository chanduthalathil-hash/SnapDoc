using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace SnapDoc.Ocr;

/// <summary>Extracts text from a bitmap. Two backends ship: Windows built-in, and (optional) Tesseract.</summary>
public interface IOcrEngine
{
    /// <summary>Human name shown in the UI ("Windows OCR", "Tesseract").</summary>
    string Name { get; }

    /// <summary>True if this engine can run on the current machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Extract text. Returns "" if none found. Never throws for empty input.</summary>
    Task<string> RecognizeAsync(BitmapSource image);
}
