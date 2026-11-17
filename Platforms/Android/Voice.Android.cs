using Android.Media;

namespace Mi_nU.Services
{
    public sealed partial class VoiceRecorder
    {
        MediaRecorder? _recorder;

        private partial Task StartPlatformAsync(string path)
        {
#pragma warning disable CA1422 // The constructor without a context is all there is before Android 12.
            var recorder = OperatingSystem.IsAndroidVersionAtLeast(31) ? new MediaRecorder(Platform.AppContext) : new MediaRecorder();
#pragma warning restore CA1422
            try
            {
                recorder.SetAudioSource(AudioSource.Mic);
                recorder.SetOutputFormat(OutputFormat.Mpeg4);
                recorder.SetAudioEncoder(AudioEncoder.Aac);
                // Mono at 64 kbit/s: clear speech at under half a megabyte a minute.
                recorder.SetAudioChannels(1);
                recorder.SetAudioSamplingRate(44100);
                recorder.SetAudioEncodingBitRate(64000);
                recorder.SetOutputFile(path);
                recorder.Prepare();
                recorder.Start();
            }
            catch
            {
                recorder.Release();
                throw;
            }
            _recorder = recorder;
            return Task.CompletedTask;
        }

        // The loudest moment since it was last asked, out of the 16-bit range.
        private partial float LevelPlatform() => (_recorder?.MaxAmplitude ?? 0) / 32767f;

        private partial Task StopPlatformAsync()
        {
            var recorder = _recorder;
            _recorder = null;
            try
            {
                recorder?.Stop();
            }
            finally
            {
                recorder?.Release();
            }
            return Task.CompletedTask;
        }
    }

    public sealed partial class VoicePlayer
    {
        MediaPlayer? _player;

        private partial Task PlayPlatformAsync(string path)
        {
            var player = new MediaPlayer();
            try
            {
                player.SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Media)!
                    .SetContentType(AudioContentType.Speech)!
                    .Build()!);
                player.SetDataSource(path);
                player.Completion += (_, _) => OnEnded();
                player.Error += (_, e) =>
                {
                    e.Handled = true;
                    OnEnded();
                };
                // A local file: preparing takes a moment, not a download.
                player.Prepare();
                player.Start();
            }
            catch
            {
                player.Release();
                throw;
            }
            _player = player;
            return Task.CompletedTask;
        }

        private partial void PausePlatform() => _player?.Pause();

        private partial void ResumePlatform() => _player?.Start();

        private partial void StopPlatform()
        {
            var player = _player;
            _player = null;
            player?.Release();
        }

        private partial TimeSpan PositionPlatform() => TimeSpan.FromMilliseconds(_player?.CurrentPosition ?? 0);
    }
}
