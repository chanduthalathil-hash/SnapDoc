using System;
using System.Collections.Generic;
using Vortice.MediaFoundation;

namespace SnapDoc.Recording;

/// <summary>One selectable webcam, for the toolbar's camera dropdown.</summary>
public sealed record VideoDevice(string SymbolicLink, string Name);

/// <summary>
/// Real webcam enumeration via Media Foundation, so the toolbar's camera dropdown shows actual
/// hardware. Enumeration only -- webcam capture/compositing into the recording isn't wired up yet
/// (see the toolbar's webcam toggle, which is disabled with a "Coming soon" tooltip); the device
/// list is still real so the picker isn't just an empty stub.
/// </summary>
public static class VideoDevices
{
    public static IReadOnlyList<VideoDevice> ListCameras()
    {
        try
        {
            MediaFoundationBootstrap.EnsureStarted();
            var list = new List<VideoDevice>();
            using var collection = MediaFactory.MFEnumVideoDeviceSources();
            foreach (var activate in collection)
            {
                using (activate)
                {
                    var attrs = (IMFAttributes)activate;
                    string name = TryGetString(attrs, CaptureDeviceAttributeKeys.FriendlyName) ?? "Camera";
                    string link = TryGetString(attrs, CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink) ?? name;
                    list.Add(new VideoDevice(link, name));
                }
            }
            return list;
        }
        catch
        {
            return Array.Empty<VideoDevice>();
        }
    }

    private static string? TryGetString(IMFAttributes attrs, Guid key)
    {
        try { return attrs.GetString(key); } catch { return null; }
    }
}
