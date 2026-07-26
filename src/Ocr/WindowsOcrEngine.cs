using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using BitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using BitmapFrame = System.Windows.Media.Imaging.BitmapFrame;

namespace SnapDoc.Ocr;

/// <summary>
/// OCR via the Windows.Media.Ocr API built into Windows 10/11. No native binaries to ship,
/// no model files to download. Language support depends on installed Windows language packs.
///
/// This is the default engine. Tesseract (TesseractOcrEngine, currently a stub) is the
/// optional fallback for languages Windows doesn't cover.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    public string Name => "Windows OCR";

    public bool IsAvailable
    {
        get
        {
            try { return OcrEngine.AvailableRecognizerLanguages.Count > 0; }
            catch { return false; }
        }
    }

    public async Task<string> RecognizeAsync(BitmapSource image)
    {
        if (image is null) return "";

        // WPF BitmapSource -> PNG bytes -> WinRT SoftwareBitmap, which the OCR API consumes.
        byte[] pngBytes = EncodeToPng(image);

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pngBytes.AsBuffer());
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        // Prefer the user's UI language; fall back to the first available recognizer.
        OcrEngine? engine =
            OcrEngine.TryCreateFromUserProfileLanguages()
            ?? (OcrEngine.AvailableRecognizerLanguages.Count > 0
                    ? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0])
                    : null);

        if (engine is null) return "";

        var result = await engine.RecognizeAsync(softwareBitmap);
        return result?.Text ?? "";
    }

    private static byte[] EncodeToPng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
