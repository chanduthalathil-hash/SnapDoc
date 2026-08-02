using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SnapDoc.Services;

namespace SnapDoc.Views;

/// <summary>
/// A thin colored outline traced around the exact area being recorded, shown for the duration of
/// the recording so it's obvious on screen what's in frame and what isn't -- the same convention
/// other screen recorders use. Click-through (so it never blocks interaction with whatever's
/// underneath) and excluded from capture (so the outline itself never ends up baked into the
/// video). The recorder always captures a fixed region for the whole recording (see
/// MfScreenRecorder.StartAsync), so this never needs to move once positioned.
/// </summary>
public partial class RecordingBoundary : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80;

    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hwnd, int index, int newStyle);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    public RecordingBoundary(Int32Rect regionPhysicalPixels)
    {
        InitializeComponent();

        double scale = GetDpiForSystem() / 96.0;
        Left = regionPhysicalPixels.X / scale;
        Top = regionPhysicalPixels.Y / scale;
        Width = regionPhysicalPixels.Width / scale;
        Height = regionPhysicalPixels.Height / scale;

        Loaded += (_, _) =>
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            CaptureExclusion.ExcludeFromCapture(hwnd);
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED);
        };
    }
}
