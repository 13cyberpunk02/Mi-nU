namespace Mi_nU.Controls
{
    /// <summary>
    /// A tap on something inside a message — a photo, a voice message, a reaction, a quote — also selects the message
    /// itself; marked here, that selection opens no menu on top of what the tap did.
    /// </summary>
    public static class TapGuard
    {
        static long _at;

        public static void Mark() => _at = Environment.TickCount64;

        public static bool Recent => Environment.TickCount64 - _at < 600;
    }

    /// <summary>
    /// Over a message: "Переслано от …" for a forwarded one, and for a reply the line it answers, with a bar down its side.
    /// A tap on that line asks the chat, through <see cref="ReplyTapped"/>, to show the answered message.
    /// </summary>
    public class MessageHeader : ContentView
    {
        public static readonly BindableProperty RowProperty =
            BindableProperty.Create(nameof(Row), typeof(MessageRow), typeof(MessageHeader), propertyChanged: (view, _, _) => ((MessageHeader)view).Show());

        public static event Action<MessageRow>? ReplyTapped;

        readonly Label _forward = new() { FontSize = 13, FontFamily = "InterSemiBold", AutomationId = "MessageForwarded" };
        readonly Label _replyName = new() { FontSize = 13, FontFamily = "InterSemiBold", LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 };
        readonly Label _replyText = new() { FontSize = 13, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 };
        readonly Border _reply;

        public MessageHeader()
        {
            _forward.SetDynamicResource(Label.TextColorProperty, "AccentText");
            _replyName.SetDynamicResource(Label.TextColorProperty, "AccentText");
            _replyText.SetDynamicResource(Label.TextColorProperty, "TextMuted");
            var bar = new BoxView { WidthRequest = 3, CornerRadius = 1.5 };
            bar.SetDynamicResource(BoxView.ColorProperty, "Accent");
            _reply = new Border
            {
                StrokeThickness = 0,
                Padding = new Thickness(0, 2, 8, 2),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                AutomationId = "MessageReply",
                Content = new Grid
                {
                    ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
                    ColumnSpacing = 8,
                    Children = { bar, new VerticalStackLayout { Children = { _replyName, _replyText } }.Column(1) },
                },
            };
            _reply.SetDynamicResource(BackgroundColorProperty, "ReplyBackground");
            _reply.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => { TapGuard.Mark(); if (Row is { } row) ReplyTapped?.Invoke(row); }) });
            Content = new VerticalStackLayout { Spacing = 4, Children = { _forward, _reply } };
            IsVisible = false;
        }

        public MessageRow? Row
        {
            get => (MessageRow?)GetValue(RowProperty);
            set => SetValue(RowProperty, value);
        }

        void Show()
        {
            var row = Row;
            IsVisible = row is { IsForwarded: true } or { HasReply: true };
            if (row is null || !IsVisible)
                return;
            _forward.IsVisible = row.IsForwarded;
            _forward.Text = row.ForwardLine;
            _reply.IsVisible = row.HasReply;
            _replyName.IsVisible = row.ReplyName.Length > 0;
            _replyName.Text = row.ReplyName;
            _replyText.Text = row.ReplyText;
            SemanticProperties.SetDescription(_reply, row.ReplyDescription);
            // Photos fill their bubble to its edges; the lines over them keep the inset text has.
            Margin = row.IsMedia ? new Thickness(12, row.ShowSenderName ? 0 : 8, 12, 6) : new Thickness(0, 0, 0, 4);
        }
    }

    /// <summary>The reactions under a message, one chip per emoji; a tap on one, through <see cref="ChipTapped"/>, sets or takes back the user's own.</summary>
    public class ReactionBar : ContentView
    {
        public static readonly BindableProperty RowProperty =
            BindableProperty.Create(nameof(Row), typeof(MessageRow), typeof(ReactionBar), propertyChanged: (view, _, _) => ((ReactionBar)view).Show());

        public static event Action<ReactionChip>? ChipTapped;

        readonly FlexLayout _chips = new() { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };

        public ReactionBar()
        {
            Content = _chips;
            IsVisible = false;
            Margin = new Thickness(0, 4, 0, 0);
        }

        public MessageRow? Row
        {
            get => (MessageRow?)GetValue(RowProperty);
            set => SetValue(RowProperty, value);
        }

        void Show()
        {
            _chips.Children.Clear();
            IsVisible = Row is { HasReactions: true };
            if (Row?.Reactions is not { } reactions)
                return;
            foreach (var chip in reactions)
            {
                var label = new Label { Text = chip.Text, FontSize = 14, VerticalTextAlignment = TextAlignment.Center };
                var pill = new Border
                {
                    StrokeThickness = chip.Mine ? 1.5 : 0,
                    Padding = new Thickness(8, 2),
                    Margin = new Thickness(0, 0, 4, 4),
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                    AutomationId = "Reaction",
                    Content = label,
                };
                pill.SetDynamicResource(BackgroundColorProperty, chip.Mine ? "AccentSoft" : "Pill");
                pill.SetDynamicResource(Border.StrokeProperty, "Accent");
                label.SetDynamicResource(Label.TextColorProperty, chip.Mine ? "AccentInk" : "Text");
                SemanticProperties.SetDescription(pill, chip.Description);
                pill.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => { TapGuard.Mark(); ChipTapped?.Invoke(chip); }) });
                _chips.Children.Add(pill);
            }
            if (Row.IsMedia)
                Margin = new Thickness(0, 4, 0, 0);
        }
    }
}
