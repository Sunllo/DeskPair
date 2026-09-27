using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using DeskPair.Platform.Abstractions.Audio;

namespace DeskPair.Platform.Windows.Audio;

/// <summary>Lists the sound devices the settings screen offers, and resolves a saved choice back to a device.</summary>
[SupportedOSPlatform("windows")]
public static class AudioDevices
{
    /// <summary>Speakers and headphones. The host captures "what you hear" from one of these.</summary>
    public static IReadOnlyList<AudioDeviceInfo> Playback() => List(DataFlow.Render);

    /// <summary>Microphones and line inputs.</summary>
    public static IReadOnlyList<AudioDeviceInfo> Capture() => List(DataFlow.Capture);

    /// <summary>The device with this id, or null for "whatever Windows is using" and for a device that has gone away.</summary>
    public static MMDevice? Resolve(string? id, DataFlow flow)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                if (string.Equals(device.ID, id, StringComparison.Ordinal))
                {
                    return device;
                }

                device.Dispose();
            }
        }
        catch (Exception)
        {
            // A missing or busy audio service must not stop a session from starting.
        }

        return null;
    }

    private static IReadOnlyList<AudioDeviceInfo> List(DataFlow flow)
    {
        var devices = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string defaultId = string.Empty;
            if (enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            {
                using MMDevice fallback = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = fallback.ID;
            }

            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, string.Equals(device.ID, defaultId, StringComparison.Ordinal)));
                }
            }
        }
        catch (Exception)
        {
            // Report nothing rather than failing the settings screen.
        }

        return devices;
    }
}
