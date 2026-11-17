using Android.Graphics;
using Android.Media;
using Android.Views;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.Video;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using Mi_nU.Controls;
using Microsoft.Maui.Handlers;

namespace Mi_nU.Services
{
    /// <summary>CameraX: the front camera into the preview and, with the microphone, into an .mp4.</summary>
    public sealed partial class VideoNoteRecorder
    {
        ProcessCameraProvider? _provider;
        Recording? _recording;
        TaskCompletionSource<bool>? _finished;

        private async partial Task StartPlatformAsync(string path, CameraPreview preview)
        {
            var context = Platform.AppContext;
            var owner = Platform.CurrentActivity as AndroidX.Lifecycle.ILifecycleOwner
                ?? throw new InvalidOperationException("No activity to tie the camera to.");
            var view = preview.Handler?.PlatformView as PreviewView ?? throw new InvalidOperationException("The preview isn't shown yet.");
            var future = ProcessCameraProvider.GetInstance(context);
            // Waiting for CameraX to start is blocking; off the main thread.
            var provider = (ProcessCameraProvider)(await Task.Run(() => future.Get()))!;
            _provider = provider;

            var mainThread = ContextCompat.GetMainExecutor(context);
            var shown = new Preview.Builder().Build()!;
            shown.SetSurfaceProvider(mainThread!, view.SurfaceProvider);
            // Standard definition is plenty for a circle a few centimetres across, and keeps the file small.
            // About a megabit a second: sharp enough in the circle, under eight megabytes for a full minute.
            var recorder = new Recorder.Builder()
                .SetTargetVideoEncodingBitRate(1_000_000)!
                .SetQualitySelector(QualitySelector.From(Quality.Sd!, FallbackStrategy.LowerQualityOrHigherThan(Quality.Sd!)!))!
                .Build()!;
            var capture = VideoCapture.WithOutput(recorder)!;
            var camera = provider.HasCamera(CameraSelector.DefaultFrontCamera!) ? CameraSelector.DefaultFrontCamera! : CameraSelector.DefaultBackCamera!;
            provider.UnbindAll();
            provider.BindToLifecycle(owner, camera, shown, capture);

            var finished = _finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new FileOutputOptions.Builder(new Java.IO.File(path)).Build()!;
            _recording = recorder.PrepareRecording(context, output)!
                .WithAudioEnabled()!
                .Start(mainThread!, new RecordingEvents(e =>
                {
                    if (e is VideoRecordEvent.Finalize done)
                        finished.TrySetResult(!done.HasError);
                }));
        }

        private async partial Task StopPlatformAsync()
        {
            var recording = _recording;
            var finished = _finished;
            _recording = null;
            _finished = null;
            try
            {
                if (recording is not null && finished is not null)
                {
                    recording.Stop();
                    // The file is complete only once CameraX says it is finalized.
                    if (await finished.Task.WaitAsync(TimeSpan.FromSeconds(10)) is false)
                        throw new InvalidOperationException("The recording finished with an error.");
                }
            }
            finally
            {
                _provider?.UnbindAll();
                _provider = null;
            }
        }

        sealed class RecordingEvents(Action<VideoRecordEvent> handle) : Java.Lang.Object, AndroidX.Core.Util.IConsumer
        {
            public void Accept(Java.Lang.Object? value)
            {
                if (value is VideoRecordEvent e)
                    handle(e);
            }
        }
    }
}

namespace Mi_nU.Controls
{
    public class CameraPreviewHandler() : ViewHandler<CameraPreview, PreviewView>(ViewMapper)
    {
        protected override PreviewView CreatePlatformView()
        {
            var view = new PreviewView(Context);
            // A TextureView, which is drawn with the rest of the screen and so can be cut to a circle;
            // the default SurfaceView would ignore the circle.
            view.SetImplementationMode(PreviewView.ImplementationMode.Compatible!);
            view.SetScaleType(PreviewView.ScaleType.FillCenter!);
            return view;
        }
    }

    /// <summary>A MediaPlayer drawing into a TextureView, which a circle can clip; scaled to fill it, cropping the rest.</summary>
    public class VideoSurfaceHandler : ViewHandler<VideoSurface, TextureView>, IVideoSurfaceHandler
    {
        public static readonly IPropertyMapper<VideoSurface, VideoSurfaceHandler> Mapper =
            new PropertyMapper<VideoSurface, VideoSurfaceHandler>(ViewMapper)
            {
                [nameof(VideoSurface.Path)] = (handler, _) => handler.Open(),
            };

        public static readonly CommandMapper<VideoSurface, VideoSurfaceHandler> Commands = new(ViewCommandMapper)
        {
            [nameof(VideoSurface.Play)] = (handler, _, _) => handler.Play(),
            [nameof(VideoSurface.Pause)] = (handler, _, _) => handler._player?.Pause(),
            [nameof(VideoSurface.Stop)] = (handler, _, _) => handler.Rewind(),
        };

        MediaPlayer? _player;
        Surface? _surface;
        bool _prepared;
        bool _playWhenReady;

        public VideoSurfaceHandler() : base(Mapper, Commands)
        {
        }

        public TimeSpan Position => _prepared && _player is { } player ? TimeSpan.FromMilliseconds(player.CurrentPosition) : TimeSpan.Zero;

        protected override TextureView CreatePlatformView()
        {
            var view = new TextureView(Context);
            view.SurfaceTextureListener = new SurfaceEvents(this);
            return view;
        }

        protected override void DisconnectHandler(TextureView platformView)
        {
            Release();
            _surface?.Release();
            _surface = null;
            base.DisconnectHandler(platformView);
        }

        void OnSurface(SurfaceTexture? texture)
        {
            _surface?.Release();
            _surface = texture is null ? null : new Surface(texture);
            if (texture is not null)
                Open();
            else
                Release();
        }

        void Open()
        {
            Release();
            if (_surface is null || VirtualView?.Path is not { Length: > 0 } path)
                return;
            var player = _player = new MediaPlayer();
            player.SetSurface(_surface);
            player.Prepared += (_, _) =>
            {
                _prepared = true;
                Fill(player.VideoWidth, player.VideoHeight);
                if (_playWhenReady)
                    player.Start();
                else
                    // Draws the first frame, so the circle shows the person rather than black.
                    ToFirstFrame(player);
            };
            player.Completion += (_, _) => Ended();
            player.Error += (_, e) =>
            {
                e.Handled = true;
                Ended();
            };
            try
            {
                player.SetDataSource(path);
                player.PrepareAsync();
            }
            catch (Exception e) when (e is Java.IO.IOException or Java.Lang.IllegalStateException)
            {
                System.Diagnostics.Debug.WriteLine($"Video message didn't open: {e.Message}");
                Ended();
            }
        }

        void Play()
        {
            _playWhenReady = true;
            if (_prepared)
                _player?.Start();
        }

        void Rewind()
        {
            _playWhenReady = false;
            if (_prepared && _player is { } player)
            {
                player.Pause();
                ToFirstFrame(player);
            }
        }

        void Ended()
        {
            _playWhenReady = false;
            if (_prepared)
                ToFirstFrame(_player);
            VirtualView?.OnEnded();
        }

        /// <summary>Shows the first picture: an exact seek where Android has one, the start of the file otherwise.</summary>
        static void ToFirstFrame(MediaPlayer? player)
        {
            if (player is null)
                return;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                player.SeekTo(1, MediaPlayerSeekMode.ClosestSync);
            else
                player.SeekTo(0);
        }

        /// <summary>Scales the picture to cover the view, the way a photo is cropped into a circle.</summary>
        void Fill(int videoWidth, int videoHeight)
        {
            var view = PlatformView;
            if (view is null || videoWidth <= 0 || videoHeight <= 0 || view.Width <= 0 || view.Height <= 0)
                return;
            float width = view.Width, height = view.Height;
            var scale = Math.Max(width / videoWidth, height / videoHeight);
            var matrix = new Matrix();
            matrix.SetScale(videoWidth * scale / width, videoHeight * scale / height, width / 2, height / 2);
            view.SetTransform(matrix);
        }

        void Release()
        {
            _prepared = false;
            _player?.Release();
            _player = null;
        }

        sealed class SurfaceEvents(VideoSurfaceHandler handler) : Java.Lang.Object, TextureView.ISurfaceTextureListener
        {
            public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) => handler.OnSurface(surface);

            public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
            {
                handler.OnSurface(null);
                return true;
            }

            public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
            {
                if (handler._player is { } player && handler._prepared)
                    handler.Fill(player.VideoWidth, player.VideoHeight);
            }

            public void OnSurfaceTextureUpdated(SurfaceTexture surface)
            {
            }
        }
    }
}
