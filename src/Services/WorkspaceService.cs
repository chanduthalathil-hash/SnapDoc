using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using SnapDoc.Models;

namespace SnapDoc.Services;

/// <summary>
/// Holds the in-memory list of captures for this session and saves flattened PNGs to the
/// workspace folder. Deliberately simple -- a real persistence layer (DB/index) is a later
/// addition; for now the folder of PNGs plus the in-memory list is enough. Because there's no
/// separate index yet, the folder of PNGs *is* the persistence: on construction we scan it and
/// rebuild a <see cref="Capture"/> per file so a fresh launch shows previous sessions' snaps
/// instead of starting empty (see <see cref="LoadExisting"/>).
/// </summary>
public sealed class WorkspaceService
{
    private readonly AppSettings _settings;

    public ObservableCollection<Capture> Captures { get; } = new();
    public ObservableCollection<RecordingClip> Recordings { get; } = new();

    public WorkspaceService(AppSettings settings)
    {
        _settings = settings;
        Directory.CreateDirectory(_settings.WorkspaceFolder);
        LoadExisting();
        LoadExistingRecordings();
    }

    /// <summary>
    /// Rebuild the gallery from PNGs already sitting in the workspace folder (e.g. saved by a
    /// previous run of the app). Title/caption/tags/OCR text aren't persisted alongside the PNG
    /// yet, so reloaded captures get filename/file-time-derived defaults -- good enough until a
    /// real index (see class remarks) lands. A single unreadable/corrupt file is skipped rather
    /// than aborting the whole load.
    /// </summary>
    private void LoadExisting()
    {
        string[] files;
        try { files = Directory.GetFiles(_settings.WorkspaceFolder, "*.png"); }
        catch { return; } // folder missing/inaccessible -- start with an empty workspace instead of crashing

        foreach (var path in files.OrderByDescending(File.GetLastWriteTime))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // read the whole file up front so it isn't left locked
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze(); // usable off the UI thread and safe to share

                Captures.Add(new Capture
                {
                    Image = bmp,
                    FilePath = path,
                    Title = Path.GetFileNameWithoutExtension(path),
                    CreatedAt = File.GetLastWriteTime(path),
                });
            }
            catch { /* skip this file; the rest of the workspace should still load */ }
        }
    }

    /// <summary>Same idea as <see cref="LoadExisting"/> but for recordings: rebuild the list from
    /// MP4s already in the workspace folder. A reloaded recording's <see cref="RecordingClip.Duration"/>
    /// is left null (its length isn't probed from the file) -- only recordings made this session
    /// know their own duration.</summary>
    private void LoadExistingRecordings()
    {
        string[] files;
        try { files = Directory.GetFiles(_settings.WorkspaceFolder, "*.mp4"); }
        catch { return; }

        foreach (var path in files.OrderByDescending(File.GetLastWriteTime))
        {
            Recordings.Add(new RecordingClip
            {
                FilePath = path,
                Title = Path.GetFileNameWithoutExtension(path),
                CreatedAt = File.GetLastWriteTime(path),
            });
        }
    }

    public void Add(Capture capture) => Captures.Insert(0, capture); // newest first

    /// <summary>Register a finished recording (already saved to <paramref name="filePath"/> by the
    /// recorder itself) in the workspace list.</summary>
    public void AddRecording(string filePath, TimeSpan? duration) => Recordings.Insert(0, new RecordingClip
    {
        FilePath = filePath,
        Title = Path.GetFileNameWithoutExtension(filePath),
        Duration = duration,
    });

    /// <summary>Remove a recording from the workspace and delete its MP4 from disk.</summary>
    public void DeleteRecording(RecordingClip recording)
    {
        Recordings.Remove(recording);
        try { if (File.Exists(recording.FilePath)) File.Delete(recording.FilePath); }
        catch { /* file may be locked/already gone; the workspace entry is still removed */ }
    }

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
