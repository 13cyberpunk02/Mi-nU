using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;

namespace Mi_nU.Services
{
    public sealed partial class VoiceRecorder
    {
        MediaCapture? _capture;

        private async partial Task StartPlatformAsync(string path)
        {
            var capture = new MediaCapture();
            try
            {
                await capture.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio });
                await File.WriteAllBytesAsync(path, []);
                var file = await StorageFile.GetFileFromPathAsync(path);
                var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium);
                // Mono at 64 kbit/s, as on Android: clear speech at under half a megabyte a minute.
                profile.Audio = AudioEncodingProperties.CreateAac(44100, 1, 64000);
                await capture.StartRecordToStorageFileAsync(profile, file);
            }
            catch
            {
                capture.Dispose();
                throw;
            }
            _capture = capture;
        }

        private async partial Task StopPlatformAsync()
        {
            var capture = _capture;
            _capture = null;
            if (capture is null)
                return;
            try
            {
                await capture.StopRecordAsync();
            }
            finally
            {
                capture.Dispose();
            }
        }
    }

    public sealed partial class VoicePlayer
    {
        MediaPlayer? _player;

        private async partial Task PlayPlatformAsync(string path)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var player = new MediaPlayer { AutoPlay = false, AudioCategory = MediaPlayerAudioCategory.Speech };
            player.MediaEnded += (_, _) => OnEnded();
            player.MediaFailed += (_, _) => OnEnded();
            player.Source = MediaSource.CreateFromStorageFile(file);
            player.Play();
            _player = player;
        }

        private partial void PausePlatform() => _player?.Pause();

        private partial void ResumePlatform() => _player?.Play();

        private partial void StopPlatform()
        {
            var player = _player;
            _player = null;
            player?.Dispose();
        }

        private partial TimeSpan PositionPlatform() => _player?.PlaybackSession.Position ?? TimeSpan.Zero;
    }
}
