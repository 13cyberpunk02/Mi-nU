namespace Mi_nU.Controls
{
    /// <summary>
    /// A voice message's loudness as a row of rounded bars, as many as fit; the part already played is in
    /// <see cref="PlayedColor"/>. Levels run from 0 to 31, see <see cref="MinU.Contracts.FileDto.Waveform"/>.
    /// </summary>
    public class WaveformView : GraphicsView, IDrawable
    {
        public static readonly BindableProperty PlayedColorProperty =
            BindableProperty.Create(nameof(PlayedColor), typeof(Color), typeof(WaveformView), Colors.Black, propertyChanged: Redraw);
        public static readonly BindableProperty RestColorProperty =
            BindableProperty.Create(nameof(RestColor), typeof(Color), typeof(WaveformView), Colors.Gray, propertyChanged: Redraw);

        const float Bar = 3;
        const float Gap = 2;
        /// <summary>A message without a measured waveform still looks like sound: low, even bars.</summary>
        static readonly byte[] Flat = [.. Enumerable.Repeat((byte)5, 40)];

        byte[] _levels = Flat;
        double _progress;

        public WaveformView()
        {
            Drawable = this;
            HeightRequest = 28;
            InputTransparent = true;
        }

        public Color PlayedColor
        {
            get => (Color)GetValue(PlayedColorProperty);
            set => SetValue(PlayedColorProperty, value);
        }

        public Color RestColor
        {
            get => (Color)GetValue(RestColorProperty);
            set => SetValue(RestColorProperty, value);
        }

        /// <summary>Levels 0 to 31; null or empty draws the even bars of a message without a waveform.</summary>
        public byte[]? Levels
        {
            get => _levels;
            set
            {
                _levels = value is { Length: > 0 } ? value : Flat;
                Invalidate();
            }
        }

        /// <summary>How much has played, 0 to 1.</summary>
        public double Progress
        {
            get => _progress;
            set
            {
                value = Math.Clamp(value, 0, 1);
                if (Math.Abs(value - _progress) < 0.001)
                    return;
                _progress = value;
                Invalidate();
            }
        }

        /// <summary>Base64 from the server, or null when it is missing or damaged.</summary>
        public static byte[]? Decode(string? waveform)
        {
            if (string.IsNullOrEmpty(waveform))
                return null;
            try
            {
                var levels = Convert.FromBase64String(waveform);
                return levels.Length is > 0 and <= 64 ? levels : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        static void Redraw(BindableObject view, object old, object value) => ((WaveformView)view).Invalidate();

        public void Draw(ICanvas canvas, RectF rect)
        {
            var bars = Math.Max(1, (int)((rect.Width + Gap) / (Bar + Gap)));
            var levels = _levels;
            var played = (int)Math.Round(_progress * bars);
            canvas.StrokeSize = Bar;
            canvas.StrokeLineCap = LineCap.Round;
            var middle = rect.Center.Y;
            for (var i = 0; i < bars; i++)
            {
                // Each bar takes the loudest level in its share of the recording.
                var from = i * levels.Length / bars;
                var to = Math.Max(from + 1, (i + 1) * levels.Length / bars);
                var level = 0;
                for (var j = from; j < to && j < levels.Length; j++)
                    level = Math.Max(level, levels[j]);
                // Half a bar's width at least, so silence still shows as a dot.
                var half = Math.Max(Bar / 2, (float)(level / 31.0 * (rect.Height - Bar) / 2));
                var x = rect.Left + Bar / 2 + i * (Bar + Gap);
                canvas.StrokeColor = i < played ? PlayedColor : RestColor;
                canvas.DrawLine(x, middle - half + Bar / 2, x, middle + half - Bar / 2);
            }
        }
    }
}
