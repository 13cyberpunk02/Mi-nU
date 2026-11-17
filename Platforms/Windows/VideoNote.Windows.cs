using Mi_nU.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;

namespace Mi_nU.Services
{
    /// <summary>
    /// MediaCapture: the front camera, or the only one, with the microphone, into an .mp4; the same camera's
    /// frames into the preview meanwhile.
    /// </summary>
    public sealed partial class VideoNoteRecorder
    {
        MediaCapture? _capture;
        MediaPlayer? _preview;
        MediaPlayerElement? _previewElement;
        bool _recording;

        private async partial Task StartPlatformAsync(string path, CameraPreview preview)
        {
            var element = preview.Handler?.PlatformView as MediaPlayerElement ?? throw new InvalidOperationException("The preview isn't shown yet.");
            var cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            var camera = cameras.FirstOrDefault(c => c.EnclosureLocation?.Panel == Windows.Devices.Enumeration.Panel.Front) ?? cameras.FirstOrDefault()
                ?? throw new InvalidOperationException("No camera.");

            var capture = _capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = camera.Id,
                StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo,
            });

            // The preview stream if the camera has one, else its recording stream, shared with the recording.
            var source = capture.FrameSources.Values
                .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                .OrderBy(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview ? 0 : 1)
                .FirstOrDefault();
            if (source is not null)
            {
                var player = _preview = new MediaPlayer { RealTimePlayback = true, AutoPlay = true, IsMuted = true };
                player.Source = MediaSource.CreateFromMediaFrameSource(source);
                element.SetMediaPlayer(player);
                _previewElement = element;
            }

            await File.WriteAllBytesAsync(path, []);
            var file = await StorageFile.GetFileFromPathAsync(path);
            // 640 by 480 is plenty for a circle a few centimetres across, and keeps the file small.
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
            // About a megabit a second: sharp enough in the circle, under eight megabytes for a full minute.
            profile.Video.Bitrate = 1_000_000;
            profile.Audio = AudioEncodingProperties.CreateAac(44100, 1, 64000);
            await capture.StartRecordToStorageFileAsync(profile, file);
            _recording = true;
        }

        private async partial Task StopPlatformAsync()
        {
            var capture = _capture;
            var recording = _recording;
            _capture = null;
            _recording = false;
            try
            {
                if (capture is not null && recording)
                    await capture.StopRecordAsync();
            }
            finally
            {
                _previewElement?.SetMediaPlayer(null);
                _previewElement = null;
                _preview?.Dispose();
                _preview = null;
                capture?.Dispose();
            }
        }
    }
}

namespace Mi_nU.Controls
{
    public class CameraPreviewHandler() : ViewHandler<CameraPreview, MediaPlayerElement>(ViewMapper)
    {
        protected override MediaPlayerElement CreatePlatformView() => new()
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            AreTransportControlsEnabled = false,
            IsHitTestVisible = false,
        };
    }

    /// <summary>A MediaPlayerElement, scaled to fill the control and cropped, without any controls of its own.</summary>
    public class VideoSurfaceHandler : ViewHandler<VideoSurface, MediaPlayerElement>, IVideoSurfaceHandler
    {
        public static readonly IPropertyMapper<VideoSurface, VideoSurfaceHandler> Mapper =
            new PropertyMapper<VideoSurface, VideoSurfaceHandler>(ViewMapper)
            {
                [nameof(VideoSurface.Path)] = (handler, _) => handler.Open(),
            };

        public static readonly CommandMapper<VideoSurface, VideoSurfaceHandler> Commands = new(ViewCommandMapper)
        {
            [nameof(VideoSurface.Play)] = (handler, _, _) => handler._player?.Play(),
            [nameof(VideoSurface.Pause)] = (handler, _, _) => handler._player?.Pause(),
            [nameof(VideoSurface.Stop)] = (handler, _, _) => handler.Rewind(),
        };

        MediaPlayer? _player;
        int _opening;

        public VideoSurfaceHandler() : base(Mapper, Commands)
        {
        }

        public TimeSpan Position => _player?.PlaybackSession.Position ?? TimeSpan.Zero;

        protected override MediaPlayerElement CreatePlatformView() => new()
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            AreTransportControlsEnabled = false,
            IsHitTestVisible = false,
        };

        protected override void DisconnectHandler(MediaPlayerElement platformView)
        {
            Release();
            base.DisconnectHandler(platformView);
        }

        async void Open()
        {
            Release();
            var opening = ++_opening;
            if (VirtualView?.Path is not { Length: > 0 } path)
                return;
            StorageFile file;
            try
            {
                file = await StorageFile.GetFileFromPathAsync(path);
            }
            catch (Exception e) when (e is FileNotFoundException or UnauthorizedAccessException)
            {
                return;
            }
            // Another file was set meanwhile.
            if (opening != _opening || PlatformView is null)
                return;
            var player = _player = new MediaPlayer { AutoPlay = false };
            player.MediaEnded += (_, _) => PlatformView?.DispatcherQueue.TryEnqueue(Ended);
            player.MediaFailed += (_, _) => PlatformView?.DispatcherQueue.TryEnqueue(Ended);
            player.Source = MediaSource.CreateFromStorageFile(file);
            PlatformView.SetMediaPlayer(player);
            if (VirtualView.IsPlaying)
                player.Play();
        }

        void Rewind()
        {
            if (_player is not { } player)
                return;
            player.Pause();
            player.PlaybackSession.Position = TimeSpan.Zero;
        }

        void Ended()
        {
            Rewind();
            VirtualView?.OnEnded();
        }

        void Release()
        {
            PlatformView?.SetMediaPlayer(null);
            _player?.Dispose();
            _player = null;
        }
    }
}
