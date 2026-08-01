using System;
using System.IO;
using System.Text.Json;

namespace SnapDoc.Models;

/// <summary>What kind of region the user is capturing.</summary>
public enum CaptureMode
{
    Region,      // drag a rectangle
    FullScreen,  // the monitor under the cursor
    AllScreens,  // the whole virtual desktop
    Window,      // a single top-level window (click to pick)
    Scrolling    // stitched scrolling capture -- see Capture/ScrollingCapture.cs (v2 stub)
}

/// <summary>
/// User preferences, persisted to %AppData%\SnapDoc\settings.json.
/// Kept deliberately small; add fields as features land.
/// </summary>
public sealed class AppSettings
{
    public string WorkspaceFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapDoc");

    // Global hotkeys (parsed by Services/HotkeyService.cs). Human-readable so users can edit the file.
    public string HotkeyRegion { get; set; } = "Ctrl+Shift+1";
    public string HotkeyFullScreen { get; set; } = "Ctrl+Shift+2";
    public string HotkeyWindow { get; set; } = "Ctrl+Shift+3";
    public string HotkeyRecord { get; set; } = "Ctrl+Shift+R";
    public string HotkeyPauseResume { get; set; } = "Ctrl+Shift+P";

    public bool CopyToClipboardOnCapture { get; set; } = true;
    public bool OpenEditorOnCapture { get; set; } = true;
    public bool RunOcrAutomatically { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;

    // ---- persistence ----

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SnapDoc", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* corrupt settings -> fall back to defaults rather than crash on startup */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* non-fatal: user just loses persisted prefs this session */ }
    }
}
