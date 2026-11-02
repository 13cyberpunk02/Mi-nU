using Mi_nU.Services;

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
            CallerLabel.Text = _calls.Invite?.Caller.DisplayName;
            KindLabel.Text = _calls.Invite?.Video == true ? "Входящий видеозвонок" : "Входящий звонок";
        }

        protected override bool OnBackButtonPressed()
        {
            OnDeclineClicked(this, EventArgs.Empty);
            return true;
        }

        async void OnAcceptClicked(object? sender, EventArgs e)
        {
            AcceptButton.IsEnabled = DeclineButton.IsEnabled = false;
            try
            {
                await _calls.AcceptAsync();
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
