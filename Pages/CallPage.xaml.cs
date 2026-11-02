using Maui.WebRTC;
using Mi_nU.Services;

namespace Mi_nU.Pages
{
    public partial class CallPage : ContentPage
    {
        readonly CallService _calls;
        readonly (Grid Tile, Label Name, VideoView Video)[] _tiles;
        readonly IDispatcherTimer _timer;
        DateTime? _connectedAt;

        public CallPage(CallService calls)
        {
            InitializeComponent();
            _calls = calls;
            _tiles = [(Tile0, Name0, Video0), (Tile1, Name1, Video1), (Tile2, Name2, Video2), (Tile3, Name3, Video3)];
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += OnTick;
#if DEBUG
            DebugLabel.IsVisible = true;
#endif
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            // The call can end while this page is still opening, and then nothing would close it.
            if (_calls.Phase == CallPhase.Idle)
            {
                await Shell.Current.GoToAsync("..");
                return;
            }

            _calls.Changed += Refresh;
            _timer.Start();
            Refresh();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _timer.Stop();
            _calls.Changed -= Refresh;
            foreach (var tile in _tiles)
                tile.Video.Track = null;
        }

        protected override bool OnBackButtonPressed()
        {
            OnHangUpClicked(this, EventArgs.Empty);
            return true;
        }

        void Refresh()
        {
            var shown = new List<(string Name, VideoTrack? Video)> { ("Вы", _calls.LocalVideo) };
            shown.AddRange(_calls.Peers.Select(peer => (peer.Name, peer.Video)));

            for (var i = 0; i < _tiles.Length; i++)
            {
                var (tile, name, video) = _tiles[i];
                var visible = i < shown.Count;
                tile.IsVisible = visible;
                name.Text = visible ? shown[i].Name : "";
                video.Track = visible ? shown[i].Video : null;
                video.IsVisible = visible && shown[i].Video is not null;
            }
            Arrange(shown.Count);

            MuteButton.Text = _calls.IsMuted ? "Включить микрофон" : "Выключить микрофон";
            CameraButton.IsVisible = _calls.LocalVideo is not null;
            CameraButton.Text = _calls.IsCameraOff ? "Включить камеру" : "Выключить камеру";
            UpdateStatus();
        }

        // Two tiles are stacked; a third takes the whole top row; four fill the grid.
        void Arrange(int count)
        {
            for (var i = 0; i < _tiles.Length; i++)
            {
                var (row, column, span) = (count, i) switch
                {
                    (1, _) => (0, 0, 2),
                    (2, _) => (i, 0, 2),
                    (3, 0) => (0, 0, 2),
                    (3, _) => (1, i - 1, 1),
                    _ => (i / 2, i % 2, 1),
                };
                var tile = _tiles[i].Tile;
                Grid.SetRow(tile, row);
                Grid.SetColumn(tile, column);
                Grid.SetColumnSpan(tile, span);
                Grid.SetRowSpan(tile, count == 1 ? 2 : 1);
            }
        }

        void UpdateStatus()
        {
            var connected = _calls.Peers.Any(peer => peer.State == global::Maui.WebRTC.PeerConnectionState.Connected);
            if (connected)
                _connectedAt ??= DateTime.UtcNow;
            else
                _connectedAt = null;

            StatusLabel.Text = _connectedAt is { } start
                ? (DateTime.UtcNow - start).ToString(@"mm\:ss")
                : _calls.Phase == CallPhase.Connected ? "Соединение…" : "Вызов…";
        }

        async void OnTick(object? sender, EventArgs e)
        {
            UpdateStatus();
#if DEBUG
            var frames = string.Join(" ", _tiles.Where(tile => tile.Tile.IsVisible).Select(tile => tile.Video.FramesRendered));
            DebugLabel.Text = $"rendered {frames}\n{await _calls.DebugStatsAsync()}";
#else
            await Task.CompletedTask;
#endif
        }

        void OnMuteClicked(object? sender, EventArgs e) => _calls.SetMuted(!_calls.IsMuted);

        void OnCameraClicked(object? sender, EventArgs e) => _calls.SetCameraOff(!_calls.IsCameraOff);

        // AppShell leaves this page when the call finishes.
        async void OnHangUpClicked(object? sender, EventArgs e) => await _calls.HangUpAsync();
    }
}
