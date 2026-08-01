using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SnapDoc.Services;

namespace SnapDoc.Views;

/// <summary>
/// "3… 2… 1…" shown over the exact area about to be recorded, then closes and raises
/// <see cref="Finished"/>. Excluded from capture (belt-and-braces -- it's always closed before the
/// recorder's first frame anyway, per <see cref="Timer_Tick"/>).
/// </summary>
public partial class RecordingCountdown : Window
{
    public event Action? Finished;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remaining;

    public RecordingCountdown(Int32Rect regionPhysicalPixels, int seconds = 3)
    {
        InitializeComponent();
        _remaining = seconds;
        CountText.Text = _remaining.ToString();

        // Same DPI-conversion reasoning as CaptureOverlay: GetDpiForSystem/GetSystemMetrics are
        // uncached Win32 calls, safe to use before this window has ever been shown.
        double scale = GetDpiForSystem() / 96.0;
        Left = regionPhysicalPixels.X / scale;
        Top = regionPhysicalPixels.Y / scale;
        Width = regionPhysicalPixels.Width / scale;
        Height = regionPhysicalPixels.Height / scale;

        Loaded += (_, _) => CaptureExclusion.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        _timer.Tick += Timer_Tick;
    }

    public void Start()
    {
        Show();
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            Close();
            Finished?.Invoke();
            return;
        }
        CountText.Text = _remaining.ToString();
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}
