using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using SnapDoc.Models;

namespace SnapDoc.Services;

/// <summary>
/// Holds the in-memory list of captures for this session and saves flattened PNGs to the
/// workspace folder. Deliberately simple -- a real persistence layer (DB/index) is a later
/// addition; for now the folder of PNGs plus the in-memory list is enough.
/// </summary>
public sealed class WorkspaceService
{
    private readonly AppSettings _settings;

    public ObservableCollection<Capture> Captures { get; } = new();

    public WorkspaceService(AppSettings settings)
    {
        _settings = settings;
        Directory.CreateDirectory(_settings.WorkspaceFolder);
    }

    public void Add(Capture capture) => Captures.Insert(0, capture); // newest first

    /// <summary>Remove a capture from the workspace and delete its saved PNG from disk.</summary>
    public void Delete(Capture capture)
    {
        Captures.Remove(capture);
        if (!string.IsNullOrEmpty(capture.FilePath))
        {
            try { if (File.Exists(capture.FilePath)) File.Delete(capture.FilePath); }
            catch { /* file may be locked/already gone; the workspace entry is still removed */ }
        }
    }

    /// <summary>Save the flattened capture (image + annotations) as a PNG. Returns the path.</summary>
    public string SavePng(Capture capture)
    {
        string path = Path.Combine(_settings.WorkspaceFolder, capture.SuggestedFileName + ".png");
        var bmp = Export.CaptureFlattener.Flatten(capture);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = new FileStream(path, FileMode.Create))
            encoder.Save(fs);
        capture.FilePath = path;
        return path;
    }
}
