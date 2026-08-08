using System;
using System.IO;
using System.Windows;

namespace SnapDoc.Services;

/// <summary>
/// Last-resort diagnostics. SnapDoc is a tray app with no window on launch, so a failure during
/// startup previously had nothing to show it happened -- the process would just vanish with no
/// trace on either the user's machine or ours. This writes whatever exception would otherwise
/// crash it silently to a log file and tells the user where to find it, so a report like "it just
/// doesn't open anymore" comes with an actual reason attached next time.
/// </summary>
public static class CrashLogger
{
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapDoc", "crash.log");

    /// <summary>Appends to the same log a fatal crash would, without the "needs to close" dialog --
    /// for failures the app already recovers from on its own (e.g. a failed export) but that still
    /// need enough detail on disk to diagnose if they recur.</summary>
    public static void Log(string source, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}]{Environment.NewLine}{message}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}");
        }
        catch { /* if even logging fails, there's nothing more we can do */ }
    }

    public static void LogAndShow(Exception? ex, string source)
    {
        Log(source, ex?.ToString() ?? "(null exception)");

        try
        {
            MessageBox.Show(
                $"SnapDoc hit an unexpected error and needs to close.\n\nDetails were saved to:\n{LogPath}\n\nPlease share that file so this can be fixed.",
                "SnapDoc — Unexpected Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* MessageBox needs an STA thread -- some crash paths (background threads) won't
                    have one; the log write above is what matters most and already happened. */ }
    }
}
