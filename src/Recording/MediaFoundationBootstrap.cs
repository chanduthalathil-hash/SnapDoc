using Vortice.MediaFoundation;

namespace SnapDoc.Recording;

/// <summary>One-time MFStartup for the process. MFStartup/MFShutdown are reference-counted, so
/// callers don't need to balance this with a shutdown -- the process exiting is shutdown enough,
/// same as every other Win32 handle SnapDoc doesn't explicitly release on exit.</summary>
internal static class MediaFoundationBootstrap
{
    private static readonly object _lock = new();
    private static bool _started;

    public static void EnsureStarted()
    {
        lock (_lock)
        {
            if (_started) return;
            MediaFactory.MFStartup(false).CheckError();
            _started = true;
        }
    }
}
