using System.Diagnostics;

namespace Mi_nU.Services
{
    /// <summary>
    /// Records a voice message from the microphone into an AAC file in .m4a, which both Android and Windows play.
    /// One recording at a time; the file is left for the caller, who sends it and then deletes it.
    /// </summary>
    public sealed partial class VoiceRecorder
    {
        /// <summary>A recording stops and is sent by itself at this length.</summary>
        public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(10);

        /// <summary>Anything shorter is a slip of the finger rather than a message.</summary>
        static readonly TimeSpan MinLength = TimeSpan.FromMilliseconds(700);

        readonly Stopwatch _clock = new();
        string? _path;

        public bool IsRecording { get; private set; }

        public TimeSpan Elapsed => _clock.Elapsed;

        /// <summary>False when the microphone isn't allowed or doesn't start.</summary>
        public async Task<bool> StartAsync()
        {
            if (IsRecording)
                return true;
            try
            {
                if (await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
                    return false;
            }
            catch (PermissionException)
            {
                return false;
            }

            var folder = Path.Combine(FileSystem.CacheDirectory, "outgoing");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".m4a");
            try
            {
                await StartPlatformAsync(path);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // No microphone, or another app holds it.
                Debug.WriteLine($"Voice recording didn't start: {e}");
                TryDelete(path);
                return false;
            }
            _path = path;
            _clock.Restart();
            IsRecording = true;
            return true;
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
                // Android refuses to stop a recording that has no sound in it yet.
                Debug.WriteLine($"Voice recording didn't finish: {e}");
                TryDelete(path);
                return null;
            }
            if (_clock.Elapsed < MinLength || !File.Exists(path) || new FileInfo(path).Length == 0)
            {
                TryDelete(path);
                return null;
            }
            return (path, Math.Max(1, (int)Math.Round(_clock.Elapsed.TotalSeconds)));
        }

        /// <summary>Stops and throws the recording away.</summary>
        public async Task CancelAsync()
        {
            if (await StopAsync() is { } recording)
                TryDelete(recording.Path);
        }

        public static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Still open somewhere; the system clears the cache folder in time.
            }
        }

        private partial Task StartPlatformAsync(string path);
        private partial Task StopPlatformAsync();
    }

    /// <summary>
    /// Plays voice messages, one at a time: starting another stops the one before.
    /// <see cref="Changed"/> fires on the main thread while playing, a few times a second, for the progress.
    /// </summary>
    public sealed partial class VoicePlayer
    {
        IDispatcherTimer? _ticker;

        /// <summary>What is playing or paused: the file's id, or its path before it was sent.</summary>
        public string? Current { get; private set; }

        public bool IsPlaying { get; private set; }

        public TimeSpan Position => Current is null ? TimeSpan.Zero : PositionPlatform();

        public event Action? Changed;

        /// <summary>Plays <paramref name="key"/> from the start, or pauses or resumes it when it is the current one.</summary>
        public async Task ToggleAsync(string key, string path)
        {
            if (Current == key)
            {
                if (IsPlaying)
                    PausePlatform();
                else
                    ResumePlatform();
                IsPlaying = !IsPlaying;
            }
            else
            {
                Stop();
                Current = key;
                try
                {
                    await PlayPlatformAsync(path);
                }
                catch
                {
                    Current = null;
                    Changed?.Invoke();
                    throw;
                }
                IsPlaying = true;
            }
            Tick(IsPlaying);
            Changed?.Invoke();
        }

        public void Stop()
        {
            if (Current is null)
                return;
            StopPlatform();
            Current = null;
            IsPlaying = false;
            Tick(false);
            Changed?.Invoke();
        }

        /// <summary>The platform player reached the end, or gave up, on whatever thread it uses.</summary>
        void OnEnded() => MainThread.BeginInvokeOnMainThread(Stop);

        void Tick(bool on)
        {
            if (!on)
            {
                _ticker?.Stop();
                return;
            }
            if (_ticker is null)
            {
                _ticker = Application.Current!.Dispatcher.CreateTimer();
                _ticker.Interval = TimeSpan.FromMilliseconds(200);
                _ticker.Tick += (_, _) => Changed?.Invoke();
            }
            _ticker.Start();
        }

        private partial Task PlayPlatformAsync(string path);
        private partial void PausePlatform();
        private partial void ResumePlatform();
        private partial void StopPlatform();
        private partial TimeSpan PositionPlatform();
    }
}
