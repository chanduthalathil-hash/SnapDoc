using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace SnapDoc.Services;

/// <summary>
/// Checks GitHub Releases (github.com/chanduthalathil-hash/SnapDoc) for newer versions, downloads
/// them in the background, and applies them on request. Publishing a new version is just: bump
/// SnapDoc.csproj's Version, run the vpk pack + vpk upload github steps (see installer/README), and
/// every installed copy of SnapDoc picks it up the next time it checks.
/// </summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/chanduthalathil-hash/SnapDoc";

    private static UpdateManager? _manager;
    private static UpdateInfo? _pendingUpdate;

    /// <summary>Fires once a new version has finished downloading and is ready to install on restart.</summary>
    public static event Action<string>? UpdateReady;

    public static bool IsUpdateReady => _pendingUpdate != null;

    /// <summary>
    /// Checks for, and downloads, a newer version if one exists. Returns the new version string,
    /// or null if already up to date (or the check couldn't run at all -- e.g. offline, or this is
    /// a dev build that wasn't installed via the Velopack-built setup.exe, in which case there's
    /// nothing to update in place).
    /// </summary>
    public static async Task<string?> CheckForUpdatesAsync()
    {
        try
        {
            _manager ??= new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
            if (!_manager.IsInstalled) return null;

            var info = await _manager.CheckForUpdatesAsync();
            if (info == null) return null;

            await _manager.DownloadUpdatesAsync(info);
            _pendingUpdate = info;

            string version = info.TargetFullRelease.Version.ToString();
            UpdateReady?.Invoke(version);
            return version;
        }
        catch
        {
            // Update checks are opportunistic (network down, GitHub unreachable, rate-limited,
            // running unpackaged during development, ...) -- never let one disrupt normal use.
            return null;
        }
    }

    public static void ApplyUpdateAndRestart()
    {
        if (_manager == null || _pendingUpdate == null) return;
        _manager.ApplyUpdatesAndRestart(_pendingUpdate);
    }
}
