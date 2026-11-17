namespace Mi_nU.Controls
{
    /// <summary>
    /// What the camera sees while a round video message is being recorded; <see cref="Services.VideoNoteRecorder"/>
    /// feeds it. The platform view fills the control, cropped rather than stretched.
    /// </summary>
    public class CameraPreview : View
    {
    }

    /// <summary>
    /// Plays a video file from the device, cropped to fill the control rather than stretched, so a circle around it
    /// shows a round video. Shows the first frame once <see cref="Path"/> is set, until <see cref="Play"/>.
    /// </summary>
    public class VideoSurface : View
    {
        public static readonly BindableProperty PathProperty =
            BindableProperty.Create(nameof(Path), typeof(string), typeof(VideoSurface));

        public string? Path
        {
            get => (string?)GetValue(PathProperty);
            set => SetValue(PathProperty, value);
        }

        /// <summary>Played to the end, or couldn't play; back at the start.</summary>
        public event EventHandler? Ended;

        public bool IsPlaying { get; private set; }

        /// <summary>How far it has played; zero before it starts.</summary>
        public TimeSpan Position => (Handler as IVideoSurfaceHandler)?.Position ?? TimeSpan.Zero;

        public void Play()
        {
            IsPlaying = true;
            Handler?.Invoke(nameof(Play));
        }

        public void Pause()
        {
            IsPlaying = false;
            Handler?.Invoke(nameof(Pause));
        }

        /// <summary>Back to the first frame, not playing.</summary>
        public void Stop()
        {
            IsPlaying = false;
            Handler?.Invoke(nameof(Stop));
        }

        internal void OnEnded()
        {
            IsPlaying = false;
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    public interface IVideoSurfaceHandler
    {
        TimeSpan Position { get; }
    }

    /// <summary>A ring drawn around a circle, filled clockwise from the top as <see cref="Progress"/> goes from 0 to 1.</summary>
    public class RingProgress : GraphicsView, IDrawable
    {
        public static readonly BindableProperty ColorProperty =
            BindableProperty.Create(nameof(Color), typeof(Color), typeof(RingProgress), Colors.White,
                propertyChanged: (view, _, _) => ((RingProgress)view).Invalidate());

        double _progress;

        public RingProgress()
        {
            Drawable = this;
            InputTransparent = true;
        }

        public Color Color
        {
            get => (Color)GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        public float Thickness { get; set; } = 4;

        public double Progress
        {
            get => _progress;
            set
            {
                value = Math.Clamp(value, 0, 1);
                if (Math.Abs(value - _progress) < 0.002)
                    return;
                _progress = value;
                Invalidate();
            }
        }

        public void Draw(ICanvas canvas, RectF rect)
        {
            if (_progress <= 0)
                return;
            var inset = Thickness / 2;
            var box = new RectF(rect.X + inset, rect.Y + inset, rect.Width - Thickness, rect.Height - Thickness);
            canvas.StrokeColor = Color;
            canvas.StrokeSize = Thickness;
            canvas.StrokeLineCap = LineCap.Round;
            if (_progress >= 0.999)
            {
                canvas.DrawEllipse(box);
                return;
            }
            // From twelve o'clock, clockwise.
            canvas.DrawArc(box, 90, 90 - (float)(360 * _progress), true, false);
        }
    }

    /// <summary>
    /// One thing plays at a time: a voice message or a video message starting stops whatever else was playing.
    /// </summary>
    public static class MediaFocus
    {
        public static event Action<object>? Taken;

        public static void Take(object owner) => Taken?.Invoke(owner);
    }
}
