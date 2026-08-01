using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapDoc.Recording;
using SnapDoc.Services;

namespace SnapDoc.Views;

/// <summary>
/// A real, live camera feed shown at exactly the position/size it'll appear at in the recording --
/// "here's what your webcam will actually look like," not a placeholder box. Stays open and visible
/// for the whole recording (not just Setup), reading from the same <see cref="WebcamCapture"/> the
/// recorder composites into the video, so this window always accurately mirrors what's landing in
/// the file. Draggable to reposition at any time, including mid-recording -- the toolbar forwards
/// each drag straight into the live composite via IScreenRecorder.SetWebcamPosition, so the preview
/// and the actual output never disagree about where the box is. Doesn't own <paramref name="capture"/>
/// passed to the constructor -- the toolbar creates and disposes that, this window only reads from it.
/// </summary>
public partial class WebcamLivePreview : Window
{
    public event Action<double, double>? PositionChanged;

    private readonly WebcamCapture _capture;
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(66) }; // ~15fps is plenty for a preview

    private Int32Rect _regionPhysical;
    private double _sizeFraction;
    private double _currentAnchorX;
    private double _currentAnchorY;

    private WriteableBitmap? _bitmap;
    private int _camWidth, _camHeight;

    private double _minXDip, _maxXDip, _minYDip, _maxYDip;
    private bool _dragging;
    private Point _dragStartMouse;

    public WebcamLivePreview(WebcamCapture capture, Int32Rect regionPhysicalPixels, double sizeFraction, double anchorX, double anchorY)
    {
        InitializeComponent();
        _capture = capture;
        _regionPhysical = regionPhysicalPixels;
        _sizeFraction = sizeFraction;
        _currentAnchorX = anchorX;
        _currentAnchorY = anchorY;

        Loaded += (_, _) =>
        {
            CaptureExclusion.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
            Reposition();
        };
        Closed += (_, _) => _renderTimer.Stop();

        MouseLeftButtonDown += (_, e) => { _dragging = true; _dragStartMouse = e.GetPosition(this); CaptureMouse(); };
        MouseMove += Window_MouseMove;
        MouseLeftButtonUp += (_, _) => { _dragging = false; ReleaseMouseCapture(); ReportAnchor(); };

        _renderTimer.Tick += (_, _) => RenderLatestFrame();
        _renderTimer.Start();
    }

    /// <summary>Call when the chosen recording source (Region/Window/Full Screen) changes while
    /// this preview is open, so the box stays correctly placed relative to the new area.</summary>
    public void UpdateRegion(Int32Rect regionPhysicalPixels)
    {
        _regionPhysical = regionPhysicalPixels;
        Reposition();
    }

    /// <summary>Call when a corner-preset button is clicked instead of dragging.</summary>
    public void SetAnchor(double anchorX, double anchorY)
    {
        _currentAnchorX = anchorX;
        _currentAnchorY = anchorY;
        Reposition();
    }

    /// <summary>Call when the Small/Medium/Large size preset changes.</summary>
    public void UpdateSize(double sizeFraction)
    {
        _sizeFraction = sizeFraction;
        Reposition();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        // Position relative to THIS window's own current bounds -- as the window moves, re-reading
        // it each tick makes "current relative pos minus drag-start relative pos" a correct running
        // delta without any manual DPI conversion (Left/Top and GetPosition share the same DIP units).
        var mouseNow = e.GetPosition(this);
        Left = Math.Clamp(Left + (mouseNow.X - _dragStartMouse.X), _minXDip, _maxXDip);
        Top = Math.Clamp(Top + (mouseNow.Y - _dragStartMouse.Y), _minYDip, _maxYDip);
    }

    private void ReportAnchor()
    {
        _currentAnchorX = _maxXDip > _minXDip ? (Left - _minXDip) / (_maxXDip - _minXDip) : 0;
        _currentAnchorY = _maxYDip > _minYDip ? (Top - _minYDip) / (_maxYDip - _minYDip) : 0;
        PositionChanged?.Invoke(Math.Clamp(_currentAnchorX, 0, 1), Math.Clamp(_currentAnchorY, 0, 1));
    }

    private void RenderLatestFrame()
    {
        var frame = _capture.TryGetLatestFrame();
        if (frame == null) return;
        var (bytes, w, h) = frame.Value;

        if (_bitmap == null || _camWidth != w || _camHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            PreviewImage.Source = _bitmap;
            _camWidth = w;
            _camHeight = h;
            Reposition(); // now that the real aspect ratio is known, size the box to match it
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, w, h), bytes, w * 4, 0);
    }

    // Same placement math as the recorder's compositor (MfScreenRecorder.CompositeWebcamOverlay)
    // -- this window IS the WYSIWYG preview of that, so they have to agree.
    private void Reposition()
    {
        double scale = GetDpiForSystem() / 96.0;
        double regionLeftDip = _regionPhysical.X / scale;
        double regionTopDip = _regionPhysical.Y / scale;
        double regionWidthDip = _regionPhysical.Width / scale;
        double regionHeightDip = _regionPhysical.Height / scale;

        double aspect = _camWidth > 0 ? (double)_camHeight / _camWidth : 9.0 / 16.0; // placeholder until the first real frame arrives
        double pipWidth = Math.Clamp(regionWidthDip * _sizeFraction, 60, Math.Max(60, regionWidthDip - 20));
        double pipHeight = pipWidth * aspect;

        Width = pipWidth;
        Height = pipHeight;

        double margin = Math.Max(12, regionWidthDip / 100);
        _minXDip = regionLeftDip + margin;
        _maxXDip = Math.Max(_minXDip, regionLeftDip + regionWidthDip - pipWidth - margin);
        _minYDip = regionTopDip + margin;
        _maxYDip = Math.Max(_minYDip, regionTopDip + regionHeightDip - pipHeight - margin);

        Left = _minXDip + (_maxXDip - _minXDip) * Math.Clamp(_currentAnchorX, 0, 1);
        Top = _minYDip + (_maxYDip - _minYDip) * Math.Clamp(_currentAnchorY, 0, 1);
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}
