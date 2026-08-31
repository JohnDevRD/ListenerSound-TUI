using NAudio.CoreAudioApi;

namespace ListenerSound.Common;

/// <summary>
/// Reduce a 0% / silencia el volumen de todas las demás aplicaciones de Windows (Spotify, navegadores,
/// reproductores, etc.) mientras ListenerSound reproduce un audio, y restablece el volumen al terminar.
/// </summary>
public static class SystemAudioDucker
{
    public sealed class DuckingSession : IDisposable
    {
        private readonly List<(AudioSessionControl session, float originalVolume, bool originalMute)> _mutedSessions = [];
        private readonly MMDeviceEnumerator? _enumerator;
        private readonly MMDevice? _device;

        public DuckingSession()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return;

                _enumerator = new MMDeviceEnumerator();
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessionManager = _device.AudioSessionManager;
                var sessions = sessionManager.Sessions;
                var currentPid = (uint)Environment.ProcessId;

                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        if (session.GetProcessID != currentPid && session.SimpleAudioVolume != null)
                        {
                            var vol = session.SimpleAudioVolume.Volume;
                            var mute = session.SimpleAudioVolume.Mute;
                            _mutedSessions.Add((session, vol, mute));

                            session.SimpleAudioVolume.Volume = 0f;
                            session.SimpleAudioVolume.Mute = true;
                        }
                    }
                    catch
                    {
                        // Ignorar errores de sesiones individuales
                    }
                }
            }
            catch
            {
                // Fallback silencioso si el subsistema de audio no admite sesiones
            }
        }

        public void Dispose()
        {
            foreach (var (session, originalVolume, originalMute) in _mutedSessions)
            {
                try
                {
                    session.SimpleAudioVolume.Volume = originalVolume;
                    session.SimpleAudioVolume.Mute = originalMute;
                }
                catch
                {
                    // Ignorar errores al restaurar sesión
                }
            }
            _mutedSessions.Clear();

            try { _device?.Dispose(); } catch { }
            try { _enumerator?.Dispose(); } catch { }
        }
    }

    public static IDisposable DuckOtherApplications()
    {
        return new DuckingSession();
    }
}
