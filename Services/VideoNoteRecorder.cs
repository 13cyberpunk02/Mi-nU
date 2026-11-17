using System.Diagnostics;
using Mi_nU.Controls;

namespace Mi_nU.Services
{
    /// <summary>
    /// Records a round video message from the front camera, with sound, into an .mp4, showing the camera in a
    /// <see cref="CameraPreview"/> meanwhile. The file is left for the caller, who sends it and then deletes it.
    /// </summary>
    public sealed partial class VideoNoteRecorder
    {
        /// <summary>A video message stops and is sent by itself at this length.</summary>
        public static readonly TimeSpan MaxLength = TimeSpan.FromSeconds(60);

        /// <summary>Anything shorter is a slip of the finger rather than a message.</summary>
        static readonly TimeSpan MinLength = TimeSpan.FromSeconds(1);

        readonly Stopwatch _clock = new();
        string? _path;

        public bool IsRecording { get; private set; }

        public TimeSpan Elapsed => _clock.Elapsed;

        /// <summary>Null when it started; otherwise what to tell the user.</summary>
        public async Task<string?> StartAsync(CameraPreview preview)
        {
            if (IsRecording)
                return null;
            try
            {
                if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted
                    || await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
                    return "Нет доступа к камере или микрофону. Разрешите его в настройках.";
            }
            catch (PermissionException)
            {
                return "Нет доступа к камере или микрофону.";
            }

            var folder = Path.Combine(FileSystem.CacheDirectory, "outgoing");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                await StartPlatformAsync(path, preview);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // No camera, or another app (a call, say) holds it.
                Debug.WriteLine($"Video message didn't start: {e}");
                await StopQuietlyAsync();
                VoiceRecorder.TryDelete(path);
                return "Не удалось включить камеру.";
            }
            _path = path;
            _clock.Restart();
            IsRecording = true;
            return null;
        }

        /// <summary>The finished recording and its length, or null when it is too short or didn't work out.</summary>
        public async Task<(string Path, int Seconds)?> StopAsync()
        {
            if (!IsRecording || _path is not { } path)
                return null;
            IsRecording = false;
            _path = null;
            _clock.Stop();
            try
            {
                await StopPlatformAsync();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Debug.WriteLine($"Video message didn't finish: {e}");
                VoiceRecorder.TryDelete(path);
                return null;
            }
            if (_clock.Elapsed < MinLength || !File.Exists(path) || new FileInfo(path).Length == 0)
            {
                VoiceRecorder.TryDelete(path);
                return null;
            }
            return (path, Math.Max(1, (int)Math.Round(_clock.Elapsed.TotalSeconds)));
        }

        /// <summary>Stops and throws the recording away.</summary>
        public async Task CancelAsync()
        {
            if (await StopAsync() is { } recording)
                VoiceRecorder.TryDelete(recording.Path);
        }

        async Task StopQuietlyAsync()
        {
            try
            {
                await StopPlatformAsync();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Debug.WriteLine($"Video message cleanup: {e.Message}");
            }
        }

        private partial Task StartPlatformAsync(string path, CameraPreview preview);
        /// <summary>Finishes the file and lets the camera go; safe to call when nothing was started.</summary>
        private partial Task StopPlatformAsync();
    }
}
