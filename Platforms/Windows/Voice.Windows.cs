using System.Runtime.InteropServices;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Render;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;

namespace Mi_nU.Services
{
    /// <summary>
    /// Records through an audio graph: the microphone feeds both the file and a tap that measures how loud it is,
    /// which the waveform is made of.
    /// </summary>
    public sealed partial class VoiceRecorder
    {
        AudioGraph? _graph;
        AudioFileOutputNode? _file;
        AudioFrameOutputNode? _tap;
        float[] _samples = [];
        float _peak;
        readonly Lock _peakLock = new();

        private async partial Task StartPlatformAsync(string path)
        {
            var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech));
            if (created.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException($"No audio graph: {created.Status}");
            var graph = created.Graph;
            try
            {
                var microphone = await graph.CreateDeviceInputNodeAsync(MediaCategory.Speech);
                if (microphone.Status != AudioDeviceNodeCreationStatus.Success)
                    throw new InvalidOperationException($"No microphone: {microphone.Status}");
                await File.WriteAllBytesAsync(path, []);
                var storage = await StorageFile.GetFileFromPathAsync(path);
                var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium);
                // Mono at 64 kbit/s, as on Android: clear speech at under half a megabyte a minute.
                profile.Audio = AudioEncodingProperties.CreateAac(44100, 1, 64000);
                var output = await graph.CreateFileOutputNodeAsync(storage, profile);
                if (output.Status != AudioFileNodeCreationStatus.Success)
                    throw new InvalidOperationException($"No file to record into: {output.Status}");

                var tap = graph.CreateFrameOutputNode();
                microphone.DeviceInputNode.AddOutgoingConnection(output.FileOutputNode);
                microphone.DeviceInputNode.AddOutgoingConnection(tap);
                graph.QuantumStarted += (_, _) => Measure();
                _graph = graph;
                _file = output.FileOutputNode;
                _tap = tap;
                graph.Start();
            }
            catch
            {
                _graph = null;
                _file = null;
                _tap = null;
                graph.Dispose();
                throw;
            }
        }

        /// <summary>Each few milliseconds of sound, on the graph's own thread: only its loudest sample is kept.</summary>
        void Measure()
        {
            try
            {
                if (_tap is not { } tap)
                    return;
                using var frame = tap.GetFrame();
                using var buffer = frame.LockBuffer(Windows.Media.AudioBufferAccessMode.Read);
                using var reference = buffer.CreateReference();
                var count = (int)(buffer.Length / sizeof(float));
                if (count == 0)
                    return;
                if (_samples.Length < count)
                    _samples = new float[count];
                Marshal.Copy(BufferBytes(reference), _samples, 0, count);
                var peak = 0f;
                for (var i = 0; i < count; i++)
                    peak = Math.Max(peak, Math.Abs(_samples[i]));
                lock (_peakLock)
                    _peak = Math.Max(_peak, peak);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A moment unmeasured only flattens one bar; the recording itself goes on.
                System.Diagnostics.Debug.WriteLine($"Voice level: {e.Message}");
            }
        }

        private partial float LevelPlatform()
        {
            lock (_peakLock)
            {
                var peak = _peak;
                _peak = 0;
                return Math.Min(peak, 1f);
            }
        }

        private async partial Task StopPlatformAsync()
        {
            var graph = _graph;
            var file = _file;
            _graph = null;
            _file = null;
            _tap = null;
            if (graph is null || file is null)
                return;
            try
            {
                graph.Stop();
                // Writes the file's index; without it the recording doesn't play.
                var finished = await file.FinalizeAsync();
                if (finished != Windows.Media.Transcoding.TranscodeFailureReason.None)
                    throw new InvalidOperationException($"Recording not finished: {finished}");
            }
            finally
            {
                graph.Dispose();
            }
        }

        static readonly Guid MemoryBufferByteAccess = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

        /// <summary>
        /// Where a WinRT memory buffer's bytes are, through its COM interface IMemoryBufferByteAccess,
        /// called through its table directly so it works without built-in COM support.
        /// </summary>
        static unsafe IntPtr BufferBytes(Windows.Foundation.IMemoryBufferReference reference)
        {
            var unknown = ((WinRT.IWinRTObject)reference).NativeObject.ThisPtr;
            var iid = MemoryBufferByteAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var access));
            try
            {
                // IUnknown's three methods come first; GetBuffer(byte** value, uint* capacity) is the fourth.
                var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)(*(void***)access)[3];
                byte* bytes;
                uint capacity;
                Marshal.ThrowExceptionForHR(getBuffer(access, &bytes, &capacity));
                return (IntPtr)bytes;
            }
            finally
            {
                Marshal.Release(access);
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
