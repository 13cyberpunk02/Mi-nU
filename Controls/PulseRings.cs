namespace Mi_nU.Controls
{
    /// <summary>
    /// Rings around a round avatar of <see cref="InnerRadius"/> in the middle: a thin one in the person's colour always,
    /// and while they talk (<see cref="IsActive"/>), waves of that colour spreading out from it and fading.
    /// </summary>
    public class PulseRings : GraphicsView, IDrawable
    {
        public static readonly BindableProperty RingColorProperty =
            BindableProperty.Create(nameof(RingColor), typeof(Color), typeof(PulseRings), Colors.White,
                propertyChanged: (view, _, _) => ((PulseRings)view).Invalidate());
        public static readonly BindableProperty IsActiveProperty =
            BindableProperty.Create(nameof(IsActive), typeof(bool), typeof(PulseRings), false,
                propertyChanged: (view, _, _) => ((PulseRings)view).Animate());
        public static readonly BindableProperty InnerRadiusProperty =
            BindableProperty.Create(nameof(InnerRadius), typeof(double), typeof(PulseRings), 48.0,
                propertyChanged: (view, _, _) => ((PulseRings)view).Invalidate());

        /// <summary>One wave takes this long to spread out; three are under way at a time.</summary>
        static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1500);
        const int Waves = 3;

        readonly System.Diagnostics.Stopwatch _clock = new();
        IDispatcherTimer? _ticker;
        /// <summary>The waves under way when talking stopped finish spreading rather than vanish.</summary>
        double _fadeFrom = -1;

        public PulseRings()
        {
            Drawable = this;
            InputTransparent = true;
            Unloaded += (_, _) => _ticker?.Stop();
            Loaded += (_, _) => Animate();
        }

        public Color RingColor
        {
            get => (Color)GetValue(RingColorProperty);
            set => SetValue(RingColorProperty, value);
        }

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public double InnerRadius
        {
            get => (double)GetValue(InnerRadiusProperty);
            set => SetValue(InnerRadiusProperty, value);
        }

        void Animate()
        {
            if (IsActive)
            {
                _fadeFrom = -1;
                if (!_clock.IsRunning)
                    _clock.Restart();
            }
            else if (_clock.IsRunning && _fadeFrom < 0)
                _fadeFrom = Phase;

            if (_clock.IsRunning && IsLoaded)
            {
                if (_ticker is null)
                {
                    _ticker = Dispatcher.CreateTimer();
                    // About 30 frames a second: smooth enough for a soft pulse, light on the battery.
                    _ticker.Interval = TimeSpan.FromMilliseconds(33);
                    _ticker.Tick += (_, _) => Invalidate();
                }
                _ticker.Start();
            }
            Invalidate();
        }

        double Phase => _clock.Elapsed.TotalMilliseconds / Period.TotalMilliseconds;

        public void Draw(ICanvas canvas, RectF rect)
        {
            var center = rect.Center;
            var inner = (float)InnerRadius;
            // The waves spread to about twice the avatar's size, less if the tile is small.
            var outer = Math.Min(Math.Min(rect.Width, rect.Height) / 2 - 2, inner * 1.9f);
            var color = RingColor;

            if (_clock.IsRunning && outer > inner)
            {
                var phase = Phase;
                var stillGoing = false;
                for (var k = 0; k < Waves; k++)
                {
                    // Wave k sets out at k/3 of each period; once talking stopped, only those already out may finish.
                    var started = Math.Floor(phase - (double)k / Waves) + (double)k / Waves;
                    var progress = (float)(phase - started);
                    if (_fadeFrom >= 0 && started + 0.0001 > _fadeFrom)
                        continue;
                    stillGoing = true;
                    var radius = inner + (outer - inner) * progress;
                    canvas.FillColor = color.WithAlpha(0.35f * (1 - progress));
                    canvas.FillCircle(center, radius);
                }
                if (!stillGoing)
                {
                    _clock.Reset();
                    _fadeFrom = -1;
                    _ticker?.Stop();
                }
            }

            // Whose colour this is, talking or not.
            canvas.StrokeColor = color.WithAlpha(IsActive ? 1f : 0.6f);
            canvas.StrokeSize = IsActive ? 3 : 2;
            canvas.DrawCircle(center, inner + 4);
        }
    }
}
