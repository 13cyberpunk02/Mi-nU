using MinU.Contracts;

namespace Mi_nU.Pages
{
    public partial class ChatPage : ContentPage, IQueryAttributable
    {
        public ChatPage()
        {
            InitializeComponent();
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
            View.Activate();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            View.Deactivate();
        }
    }
}
