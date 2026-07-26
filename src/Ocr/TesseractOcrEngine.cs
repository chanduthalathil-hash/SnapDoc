using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace SnapDoc.Ocr;

/// <summary>
/// Optional Tesseract OCR backend, for languages Windows OCR doesn't cover, or offline machines
/// without language packs. STUB by default so the app has zero native dependencies out of the box.
///
/// TO ENABLE:
///   1. Uncomment the Tesseract PackageReference in SnapDoc.csproj.
///   2. Ship tessdata\ (the trained language files) beside the exe.
///   3. Implement RecognizeAsync using TesseractEngine: convert BitmapSource -> Pix, then GetText().
///   4. Register this instead of / alongside WindowsOcrEngine in App.xaml.cs.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    public string Name => "Tesseract (not configured)";
    public bool IsAvailable => false; // flip to true once tessdata is present and the package is enabled

    public Task<string> RecognizeAsync(BitmapSource image) => Task.FromResult("");
}
