using System;
using NAudio.CoreAudioApi;

namespace IdmClone.Engine;

/// <summary>
/// Last resort for a player that does not stop: Windows' own volume mixer entry of THIS program (the one you see in Volume mixer) is muted, so nothing is heard even when the playback
/// engine is stuck. It is switched off again the next time a player opens.
/// </summary>
public static class ProcessAudio
{
    private static bool _silenced;

    public static void Silence(bool mute)
    {
        // (unmuting also looks at the sound devices when nothing was muted by us: a mute left on this program in Windows' mixer by an earlier run is lifted too)
        try
        {
            using var devices = new MMDeviceEnumerator();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.GetProcessID == (uint)Environment.ProcessId) session.SimpleAudioVolume.Mute = mute;
                }
            }
            _silenced = mute;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or System.Runtime.InteropServices.ExternalException) { /* no sound device to look at */ }
    }
}
