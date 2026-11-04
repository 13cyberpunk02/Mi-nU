using Mi_nU.Services;
using Mi_nU.Themes;

namespace Mi_nU.Pages
{
    public partial class IncomingCallPage : ContentPage
    {
        readonly CallService _calls;

        public IncomingCallPage(CallService calls)
        {
            InitializeComponent();
            _calls = calls;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            var caller = _calls.Invite?.Caller.DisplayName ?? "";
            var video = _calls.Invite?.Video == true;
            CallerLabel.Text = caller;
            CallerAvatar.Name = CallerAvatar.Seed = caller;
            KindLabel.Text = video ? "Входящий видеозвонок" : "Входящий звонок";
            AcceptButton.Text = video ? Icons.Video : Icons.Phone;
            AudioOnlyButton.IsVisible = video;
        }

        protected override bool OnBackButtonPressed()
        {
            OnDeclineClicked(this, EventArgs.Empty);
            return true;
        }

        async void OnAcceptClicked(object? sender, EventArgs e) => await AcceptAsync(video: true);

        async void OnAcceptWithoutVideoClicked(object? sender, EventArgs e) => await AcceptAsync(video: false);

        async Task AcceptAsync(bool video)
        {
            AcceptButton.IsEnabled = DeclineButton.IsEnabled = AudioOnlyButton.IsEnabled = false;
            try
            {
                await _calls.AcceptAsync(video);
                await Shell.Current.GoToAsync("../call");
            }
            catch (CallException error)
            {
                ErrorLabel.Text = error.Message;
                ErrorLabel.IsVisible = true;
                DeclineButton.IsEnabled = true;
            }
        }

        async void OnDeclineClicked(object? sender, EventArgs e)
        {
            await _calls.DeclineAsync();
            await Shell.Current.GoToAsync("..");
        }
    }
}
