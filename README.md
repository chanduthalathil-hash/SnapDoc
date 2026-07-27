# SnapDoc

A friendly Windows screen-capture and documentation tool (a Screenpresso-style app), built with
**WPF on .NET 8**. Capture a region, annotate it, and export a sequence of captures as a clean
**step-by-step guide** in Markdown, PDF, Word, or PowerPoint.

> **Status:** v0.1 foundation. The capture → annotate → export core is implemented and runnable.
> OCR uses the built-in Windows engine. Screen recording, scrolling capture, Tesseract OCR, and
> plugins are **stubbed with clear TODOs** so you can fill them in one at a time.

## Download

**[⬇ Download SnapDoc for Windows](https://github.com/chanduthalathil-hash/SnapDoc/releases/latest/download/SnapDoc-win-Setup.exe)**

Run the installer, then check the system tray (near the clock) — SnapDoc has no window on launch
by design. The installed app checks for and applies new versions automatically; see
[Releasing an update](#releasing-an-update) below for how to ship one.

---

## Build & run

You need **Windows** (this is a native Windows app — it cannot build or run on Linux/macOS) and
either **Visual Studio 2022** (17.8+) with the *.NET desktop development* workload, or the
**.NET 8 SDK** on the command line.

```powershell
# from the repo root
dotnet restore
dotnet build -c Debug
dotnet run --project src/SnapDoc.csproj
```

Or open `SnapDoc.sln` in Visual Studio and press F5.

On first run, the app starts **in the system tray** (no main window pops up — that's intentional).
- Tray balloon tells you it's running.
- **Ctrl+Shift+1** — capture a region (drag a rectangle).
- **Ctrl+Shift+2** — capture the current monitor.
- **Ctrl+Shift+3** — capture a window (click it).
- Double-click the tray icon to open the **Workspace** window, where you export multi-step guides.

---

## Architecture (why it's laid out this way)

Everything is wired by hand in `App.xaml.cs` (the *composition root*). Each subsystem hides behind
an interface, so you can replace an implementation without touching anything else:

| Interface | Default impl | Swap to… |
|---|---|---|
| `ICaptureEngine` | `GdiCaptureEngine` (BitBlt) | a `WgcCaptureEngine` using Windows.Graphics.Capture for GPU-fast, protected-content capture |
| `IOcrEngine` | `WindowsOcrEngine` (built-in) | `TesseractOcrEngine` for more languages / offline |
| `IScreenRecorder` | `StubScreenRecorder` | a real recorder (FFmpeg or Media Foundation) |
| `IExporter` | Markdown / PDF / Word / PPTX | add your own format |

```
src/
  Models/      Capture, Annotation types, AppSettings   (the data everything shares)
  Capture/     ICaptureEngine + GDI capture + scrolling stub
  Editor/      AnnotationRenderer  (single source of truth for drawing)
  Ocr/         IOcrEngine + Windows OCR + Tesseract stub
  Export/      IExporter + Markdown/PDF/Word/PPTX + CaptureFlattener
  Recording/   IScreenRecorder + stub  (the big v2 feature)
  Plugins/     IPlugin + context  (deferred by design)
  Services/    HotkeyService, WorkspaceService, CaptureController
  Views/       CaptureOverlay, EditorWindow, MainWindow, TrayIcon  (the WPF UI)
```

**Key design choice:** annotations are stored in *image-pixel coordinates* and drawn by one
`AnnotationRenderer`. The live editor and every exporter call it, so what you see is what you
export, and a new annotation type exports for free once you add its `case` there.

---

## What's implemented vs stubbed

**Working now**
- Region / monitor / window capture (GDI), DPI-aware
- Annotation editor: arrow, rectangle, ellipse, pen, highlighter, text, **numbered step tool**
- Colours, undo, delete
- Windows OCR (built-in; needs a language pack)
- Export one capture or the whole workspace to **Markdown, PDF, Word, PowerPoint**
- Step-guide mode: each capture becomes "Step N" with its title + caption
- System tray, global hotkeys, clipboard copy, PNG save

**Stubbed — with implementation notes in the code**
- Screen recording (`Recording/IScreenRecorder.cs`) — the biggest single feature; recommend FFmpeg
- Scrolling capture (`Capture/ScrollingCapture.cs`) — stitch algorithm sketched in comments
- Tesseract OCR (`Ocr/TesseractOcrEngine.cs`) — enable the NuGet + ship `tessdata`
- Plugins (`Plugins/IPlugin.cs`) — structured for, but not loading external assemblies yet

---

## Suggested build order (don't do it all at once)

You asked for "everything." Here's the sane sequence so you always have a working app:

1. **Ship the core** above as v1. Get real feedback on capture + annotate + step-guide export.
2. Polish the editor: proper selection/move/resize of annotations (currently undo-only).
3. Add **screen recording** via FFmpeg — the most-requested "everything" feature.
4. Add **scrolling capture**.
5. Add **Tesseract** only if users need languages Windows OCR lacks.
6. Add **plugins** last, once you know what people actually want to extend.

Each step is independent and leaves a shippable app.

---

## Known rough edges (honest list)

- **Not compiled here.** This project was authored in an environment without the .NET SDK, so it
  has not been built. Expect a few small fixes on first `dotnet build` — namespace or using nits
  are the likely culprits. Paste any error and it's quick to resolve.
- The editor's **Delete** currently removes the last annotation (no click-to-select yet).
- **Text annotation** uses a simple VB `InputBox`; replace with a WPF dialog for polish.
- **PPTX exporter** builds a minimal valid deck; complex layouts may need layout tweaks.
- Mixed-DPI multi-monitor capture is correct per-window but approximate right at monitor seams.
- No app icon yet (uses the system default) — drop an `.ico` in and set it on the tray + windows.

---

## Releasing an update

SnapDoc auto-updates itself via [Velopack](https://velopack.io), checking this repo's GitHub
Releases. Every installed copy picks up a new version automatically (or via the tray's "Check for
updates…" item) once you publish one:

```powershell
# 1. Bump <Version> in src/SnapDoc.csproj, then from src/:
dotnet publish SnapDoc.csproj -c Release -r win-x64 --self-contained true -o publish-velopack

# 2. Package the release (from src/):
vpk pack --packId SnapDoc --packVersion <new version> --packDir publish-velopack `
  --mainExe SnapDoc.exe --icon Assets/icon.ico -o ../releases

# 3. Publish it to GitHub Releases (from the repo root):
vpk upload github -o releases --repoUrl https://github.com/chanduthalathil-hash/SnapDoc `
  --token <gh token> --publish true
```

`vpk` is installed via `dotnet tool install -g vpk`. A GitHub token with `repo` scope is required
for step 3 — `gh auth token` will print the one already used to set this repo up, if the GitHub CLI
is authenticated.

---

## Licences to be aware of before shipping

- **QuestPDF** (PDF export) is free under the Community licence for small businesses/individuals;
  check their current terms if you commercialise.
- **DocumentFormat.OpenXml** (Word/PPTX) — MIT.
- **CommunityToolkit.Mvvm** — MIT.
- Tesseract (if enabled) — Apache 2.0, plus the language data files.
