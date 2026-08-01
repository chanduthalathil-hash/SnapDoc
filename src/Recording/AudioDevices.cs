using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;

namespace SnapDoc.Recording;

/// <summary>One selectable microphone, for the toolbar's device dropdown.</summary>
public sealed record AudioDevice(string Id, string Name);

/// <summary>Real microphone enumeration via WASAPI (NAudio) -- the toolbar's mic dropdown shows
/// actual hardware, not placeholders.</summary>
public static class AudioDevices
{
    public static IReadOnlyList<AudioDevice> ListMicrophones()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new AudioDevice(d.ID, d.FriendlyName))
                .ToList();
        }
        catch
        {
            return Array.Empty<AudioDevice>(); // no audio subsystem / no mic -- an empty list, not a crash
        }
    }
}
