using Maui.WebRTC;
using Mi_nU.Controls;
using Mi_nU.Services;
using Mi_nU.Themes;

namespace Mi_nU.Pages
{
    public partial class CallPage : ContentPage
    {
        readonly CallService _calls;
        readonly (Border Tile, Avatar Avatar, Label Name, VideoView Video)[] _tiles;
        readonly IDispatcherTimer _timer;
        DateTime? _connectedAt;

        public CallPage(CallService calls)
        {
            InitializeComponent();
            _calls = calls;
            _tiles = [(Tile0, Avatar0, Name0, Video0), (Tile1, Avatar1, Name1, Video1), (Tile2, Avatar2, Name2, Video2), (Tile3, Avatar3, Name3, Video3)];
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += OnTick;
            SizeChanged += (_, _) => Refresh();
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
            _calls.SpeakingChanged += Refresh;
            _timer.Start();
            // Asked once rather than on every refresh: listing devices goes to the system. The list itself is fresh on each tap.
            AudioOutputButton.IsVisible = _calls.AudioOutputs.Count > 1;
            Refresh();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _timer.Stop();
            _calls.Changed -= Refresh;
            _calls.SpeakingChanged -= Refresh;
            foreach (var tile in _tiles)
                tile.Video.Track = null;
            LocalVideo.Track = null;
        }

        protected override bool OnBackButtonPressed()
        {
            OnHangUpClicked(this, EventArgs.Empty);
            return true;
        }

        void OnLocalVideoHandlerChanged(object? sender, EventArgs e)
        {
#if ANDROID
            // Two SurfaceViews in the same place draw in no fixed order; this puts the preview above the other video.
            (LocalVideo.Handler?.PlatformView as Android.Views.SurfaceView)?.SetZOrderMediaOverlay(true);
#endif
        }

        void Refresh()
        {
            var peers = _calls.Peers.ToList();
            // One other person fills the screen with you in the corner; with more, everyone gets a tile.
            var preview = peers.Count <= 1 && _calls.LocalVideo is not null;
            var shown = peers.Select(peer => (Name: peer.Name, Video: peer.Video, Speaking: _calls.IsSpeaking(peer), Picture: peer.AvatarId)).ToList();
            if (!preview)
                shown.Add(("Вы", _calls.LocalVideo, _calls.IsSelfSpeaking, _calls.SelfAvatarId));
            if (shown.Count == 0)
                shown.Add((_calls.Title, null, false, null));
            // Only worth marking among several tiles: one person filling the screen is obviously the one talking.
            var markSpeaker = shown.Count > 1;

            for (var i = 0; i < _tiles.Length; i++)
            {
                var (tile, avatar, name, video) = _tiles[i];
                var visible = i < shown.Count;
                tile.IsVisible = visible;
                if (!visible)
                {
                    video.Track = null;
                    continue;
                }

                var (who, track, speaking, picture) = shown[i];
                name.Text = who;
                avatar.Name = avatar.Seed = who;
                avatar.PictureId = picture;
                video.Track = track;
                video.IsVisible = track is not null;
                var marked = markSpeaker && speaking;
                var style = StyleNamed(marked ? "CallTileSpeaking" : "CallTile");
                if (tile.Style != style)
                    tile.Style = style;
                // The frame can't be seen by a screen reader, so the name says it too.
                SemanticProperties.SetDescription(name, marked ? $"{who}, говорит" : who);
            }
            Arrange(Math.Min(shown.Count, _tiles.Length));

            LocalPreview.IsVisible = preview;
            LocalVideo.Track = preview ? _calls.LocalVideo : null;

            TitleLabel.Text = _calls.Title;
            MuteButton.Text = _calls.IsMuted ? Icons.MicOff : Icons.Mic;
            MuteButton.Style = StyleNamed(_calls.IsMuted ? "CallButtonOff" : "CallButton");
            SemanticProperties.SetDescription(MuteButton, _calls.IsMuted ? "Включить микрофон" : "Выключить микрофон");
            CameraButton.IsVisible = _calls.LocalVideo is not null;
            CameraButton.Text = _calls.IsCameraOff ? Icons.VideoOff : Icons.Video;
            CameraButton.Style = StyleNamed(_calls.IsCameraOff ? "CallButtonOff" : "CallButton");
            SemanticProperties.SetDescription(CameraButton, _calls.IsCameraOff ? "Включить камеру" : "Выключить камеру");
            // With the camera off there is nothing to switch.
            SwitchCameraButton.IsVisible = _calls.CanSwitchCamera && !_calls.IsCameraOff;
            UpdateStatus();
        }

        static Style StyleNamed(string key) =>
            Application.Current!.Resources.TryGetValue(key, out var style) ? (Style)style : throw new KeyNotFoundException(key);

        // A wide window puts tiles side by side; a phone stacks them, and a third tile spans the top row.
        void Arrange(int count)
        {
            var wide = Width > Height;
            var columns = count switch
            {
                <= 1 => 1,
                2 => wide ? 2 : 1,
                3 => wide ? 3 : 2,
                _ => 2,
            };
            var rows = (count + columns - 1) / columns;
            if (count == 3 && !wide)
                rows = 2;

            Tiles.ColumnDefinitions.Clear();
            Tiles.RowDefinitions.Clear();
            for (var c = 0; c < columns; c++)
                Tiles.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (var r = 0; r < rows; r++)
                Tiles.RowDefinitions.Add(new RowDefinition(GridLength.Star));

            for (var i = 0; i < count; i++)
            {
                var tile = _tiles[i].Tile;
                if (count == 3 && !wide)
                {
                    Grid.SetRow(tile, i == 0 ? 0 : 1);
                    Grid.SetColumn(tile, i == 0 ? 0 : i - 1);
                    Grid.SetColumnSpan(tile, i == 0 ? 2 : 1);
                }
                else
                {
                    Grid.SetRow(tile, i / columns);
                    Grid.SetColumn(tile, i % columns);
                    Grid.SetColumnSpan(tile, 1);
                }
            }
        }

        void UpdateStatus()
        {
            var connected = _calls.Peers.Any(peer => peer.State == PeerConnectionState.Connected);
            if (connected)
                _connectedAt ??= DateTime.UtcNow;
            else
                _connectedAt = null;

            // Only while connecting: once connected the warning no longer matters.
            RelayWarning.IsVisible = !_calls.HasRelay && _connectedAt is null && _calls.Phase != CallPhase.Idle;
            StatusLabel.Text = _connectedAt is { } start
                ? (DateTime.UtcNow - start).ToString(@"mm\:ss")
                : _calls.Phase == CallPhase.Connected ? "Соединение…" : "Вызов…";
        }

        async void OnTick(object? sender, EventArgs e)
        {
            UpdateStatus();
#if DEBUG
            var frames = string.Join(" ", _tiles.Where(tile => tile.Tile.IsVisible).Select(tile => tile.Video.FramesRendered).Append(LocalVideo.FramesRendered));
            DebugLabel.Text = $"rendered {frames}\n{await _calls.DebugStatsAsync()}";
            // For the device tests: Android's UI dump never finishes while video plays, so they read the state and button positions here.
            Console.WriteLine($"CALLDBG muted={_calls.IsMuted} mute={CenterOf(MuteButton)} hangup={CenterOf(HangUpButton)} {DebugLabel.Text.Replace('\n', ';')}");
#else
            await Task.CompletedTask;
#endif
        }

#if DEBUG
        static string CenterOf(View view)
        {
#if ANDROID
            if (view.Handler?.PlatformView is Android.Views.View native)
            {
                var at = new int[2];
                native.GetLocationOnScreen(at);
                return $"{at[0] + native.Width / 2},{at[1] + native.Height / 2}";
            }
#endif
            return "";
        }
#endif

        void OnMuteClicked(object? sender, EventArgs e) => _calls.SetMuted(!_calls.IsMuted);

        void OnCameraClicked(object? sender, EventArgs e) => _calls.SetCameraOff(!_calls.IsCameraOff);

        // Two cameras, as on a phone, simply swap; more, as on a computer with a webcam and virtual ones, are picked by name.
        async void OnSwitchCameraClicked(object? sender, EventArgs e)
        {
            var cameras = _calls.Cameras;
            Maui.WebRTC.CameraDevice? target = null;
            if (cameras.Count > 2)
            {
                var current = _calls.CurrentCamera;
                var index = await ChooseAsync("Камера", cameras.Select(camera => (camera.Name, camera.Index == current?.Index)).ToList());
                if (index < 0)
                    return;
                target = cameras[index];
            }

            SwitchCameraButton.IsEnabled = false;
            await _calls.SwitchCameraAsync(target);
            SwitchCameraButton.IsEnabled = true;
        }

        async void OnAudioOutputClicked(object? sender, EventArgs e)
        {
            var outputs = _calls.AudioOutputs;
            var current = _calls.CurrentAudioOutputId;
            var index = await ChooseAsync("Звук", outputs.Select(output => (output.Name, output.Id == current)).ToList());
            if (index >= 0 && !_calls.SelectAudioOutput(outputs[index]))
                await DisplayAlertAsync("Звук", $"Не удалось переключиться на «{outputs[index].Name}».", "OK");
        }

        /// <summary>A list to pick from, the current choice marked; the index picked, or -1 if cancelled.</summary>
        async Task<int> ChooseAsync(string title, IReadOnlyList<(string Name, bool Current)> items)
        {
            var labels = items.Select(item => item.Current ? $"{item.Name} (сейчас)" : item.Name).ToArray();
            var choice = await DisplayActionSheetAsync(title, "Отмена", null, labels);
            return Array.IndexOf(labels, choice);
        }

        // AppShell leaves this page when the call finishes.
        async void OnHangUpClicked(object? sender, EventArgs e) => await _calls.HangUpAsync();
    }
}
