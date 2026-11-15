using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU.Pages
{
    public partial class ChatPage : ContentPage, IQueryAttributable
    {
        readonly ChatHubClient _hub;

        public ChatPage(ChatHubClient hub)
        {
            InitializeComponent();
            _hub = hub;
            View.BackRequested += async () => await Shell.Current.GoToAsync("..");
        }

        public void ApplyQueryAttributes(IDictionary<string, object> query)
        {
            var chat = (ChatDto)query["chat"];
            Title = chat.Title;
            View.Show(chat);
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            _hub.ChatRemoved += OnChatRemoved;
            View.Activate();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _hub.ChatRemoved -= OnChatRemoved;
            View.Deactivate();
        }

        // Removed from the group while reading it: nothing here to stay for.
        void OnChatRemoved(Guid chatId) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (chatId == View.ChatId && Shell.Current.CurrentPage == this)
                await Shell.Current.GoToAsync("..");
        });
    }
}
