using Android.App;
using Android.Content;
using Android.Media;
using AndroidX.Core.App;
using Firebase.Messaging;
using Mi_nU.Services;
using MinU.Contracts;

namespace Mi_nU.Droid
{
    /// <summary>Receives Firebase Cloud Messaging pushes, also while the app is closed.</summary>
    [Service(Exported = false)]
    [IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
    public class PushService : FirebaseMessagingService
    {
        /// <summary>Null without google-services.json, when Firebase isn't set up: the app then runs without push.</summary>
        public static async Task<string?> GetTokenAsync()
        {
            try
            {
                var result = new TaskCompletionSource<string?>();
#pragma warning disable CS0618 // Firebase Messaging 25 deprecates registration tokens for installation ids (Fid); tokens still work.
                FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new TokenListener(result));
#pragma warning restore CS0618
                return await result.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (Exception e) when (e is Java.Lang.IllegalStateException or TimeoutException)
            {
                return null;
            }
        }

#pragma warning disable CS0672 // See GetTokenAsync: deprecated along with registration tokens.
        public override void OnNewToken(string token) =>
            IPlatformApplication.Current?.Services.GetService<PushNotifications>()?.OnNewToken(token);
#pragma warning restore CS0672

        public override void OnMessageReceived(RemoteMessage message) =>
            PushNotifier.Show(this, message.Data);

        sealed class TokenListener(TaskCompletionSource<string?> result) : Java.Lang.Object, Android.Gms.Tasks.IOnCompleteListener
        {
            public void OnComplete(Android.Gms.Tasks.Task task) =>
                result.TrySetResult(task.IsSuccessful ? task.Result?.ToString() : null);
        }
    }

    /// <summary>"Decline" on a ringing notification: answers the server without opening the app.</summary>
    [BroadcastReceiver(Exported = false)]
    public class DeclineCallReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context is null || !Guid.TryParse(intent?.GetStringExtra(PushNotifier.ExtraCallId), out var callId))
                return;

            NotificationManagerCompat.From(context)!.Cancel(PushNotifier.CallNotificationId(callId));
            var pending = GoAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    var services = IPlatformApplication.Current?.Services;
                    var auth = services?.GetService<AuthService>();
                    if (auth is null || services is null)
                        return;
                    // Started by the tap alone, the app has no session loaded yet.
                    if (auth.Session is null && !await auth.RestoreAsync())
                        return;
                    await services.GetRequiredService<ChatApi>().DeclineCallAsync(callId);
                }
                catch (ApiException)
                {
                    // The caller's phone stops ringing on its own when nobody answers.
                }
                finally
                {
                    pending?.Finish();
                }
            });
        }
    }

    public static class PushNotifier
    {
        const string MessagesChannel = "messages";
        const string CallsChannel = "calls";
        static readonly TimeSpan RingTime = TimeSpan.FromSeconds(45);

        public const string ExtraAction = "minu_action";
        public const string ExtraChatId = "minu_chat";
        public const string ExtraCallId = "minu_call";
        public const string ExtraCaller = "minu_caller";
        public const string ExtraVideo = "minu_video";
        public const string ActionOpenChat = "open_chat";
        public const string ActionShowCall = "show_call";
        public const string ActionAnswer = "answer";

        /// <summary>While the app is on screen it shows everything itself, through SignalR.</summary>
        public static bool AppVisible { get; set; }

        public static int CallNotificationId(Guid callId) => callId.GetHashCode();

        public static void Show(Context context, IDictionary<string, string> received)
        {
            IReadOnlyDictionary<string, string> data = new Dictionary<string, string>(received);
            if (!data.TryGetValue(PushKeys.Type, out var type))
                return;

            EnsureChannels(context);
            switch (type)
            {
                case PushKeys.TypeMessage when !AppVisible:
                    ShowMessage(context, data);
                    break;
                case PushKeys.TypeCall when !AppVisible:
                    ShowCall(context, data);
                    break;
                case PushKeys.TypeCallEnded when Guid.TryParse(data.GetValueOrDefault(PushKeys.CallId), out var callId):
                    NotificationManagerCompat.From(context)!.Cancel(CallNotificationId(callId));
                    break;
            }
        }

        /// <summary>Turns a tap on a notification into an action for the app; called by MainActivity.</summary>
        public static void HandleIntent(Context context, Intent? intent)
        {
            switch (intent?.GetStringExtra(ExtraAction))
            {
                case ActionOpenChat when Guid.TryParse(intent.GetStringExtra(ExtraChatId), out var chatId):
                    PushNotifications.Post(new PushAction.OpenChat(chatId));
                    break;
                case ActionShowCall or ActionAnswer when Guid.TryParse(intent.GetStringExtra(ExtraCallId), out var callId)
                                                      && Guid.TryParse(intent.GetStringExtra(ExtraChatId), out var chatId):
                    NotificationManagerCompat.From(context)!.Cancel(CallNotificationId(callId));
                    var caller = new UserDto(Guid.Empty, "", intent.GetStringExtra(ExtraCaller) ?? "");
                    var invite = new CallInvite(callId, chatId, caller, intent.GetBooleanExtra(ExtraVideo, false));
                    PushNotifications.Post(new PushAction.IncomingCall(invite, intent.GetStringExtra(ExtraAction) == ActionAnswer));
                    break;
                default:
                    return;
            }
            // Handled once: rotating the screen recreates the activity with the same intent.
            intent!.RemoveExtra(ExtraAction);
        }

        static void ShowMessage(Context context, IReadOnlyDictionary<string, string> data)
        {
            if (!Guid.TryParse(data.GetValueOrDefault(PushKeys.ChatId), out var chatId))
                return;

            var sender = data.GetValueOrDefault(PushKeys.Sender) ?? "";
            var group = data.GetValueOrDefault(PushKeys.ChatTitle);
            var text = data.GetValueOrDefault(PushKeys.Text) ?? "";
            var open = OpenApp(context, chatId.GetHashCode(), intent => intent
                .PutExtra(ExtraAction, ActionOpenChat)
                .PutExtra(ExtraChatId, chatId.ToString()));

            var notification = new NotificationCompat.Builder(context, MessagesChannel)
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetContentTitle(string.IsNullOrEmpty(group) ? sender : $"{sender} · {group}")
                .SetContentText(text)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(text))
                .SetCategory(NotificationCompat.CategoryMessage)
                .SetAutoCancel(true)
                .SetContentIntent(open)!
                .Build()!;
            Notify(context, chatId.GetHashCode(), notification);
        }

        static void ShowCall(Context context, IReadOnlyDictionary<string, string> data)
        {
            if (!Guid.TryParse(data.GetValueOrDefault(PushKeys.CallId), out var callId)
                || !Guid.TryParse(data.GetValueOrDefault(PushKeys.ChatId), out var chatId))
                return;

            var caller = data.GetValueOrDefault(PushKeys.Sender) ?? "";
            var video = data.GetValueOrDefault(PushKeys.Video) == "1";
            var id = CallNotificationId(callId);
            Intent CallExtras(Intent intent, string action) => intent
                .PutExtra(ExtraAction, action)
                .PutExtra(ExtraCallId, callId.ToString())
                .PutExtra(ExtraChatId, chatId.ToString())
                .PutExtra(ExtraCaller, caller)
                .PutExtra(ExtraVideo, video);

            var show = OpenApp(context, id, intent => CallExtras(intent, ActionShowCall));
            var answer = OpenApp(context, id + 1, intent => CallExtras(intent, ActionAnswer));
            var decline = PendingIntent.GetBroadcast(context, id + 2,
                new Intent(context, typeof(DeclineCallReceiver)).PutExtra(ExtraCallId, callId.ToString()),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            var notification = new NotificationCompat.Builder(context, CallsChannel)
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetContentTitle(caller)
                .SetContentText(video ? "Входящий видеозвонок" : "Входящий звонок")
                .SetCategory(NotificationCompat.CategoryCall)
                .SetPriority(NotificationCompat.PriorityMax)
                .SetOngoing(true)
                .SetAutoCancel(true)
                // Opens the incoming call screen over the lock screen where Android allows it.
                .SetFullScreenIntent(show, true)
                .SetContentIntent(show)
                .AddAction(0, "Отклонить", decline)
                .AddAction(0, "Ответить", answer)
                .SetTimeoutAfter((long)RingTime.TotalMilliseconds)!
                .Build()!;
            // Rings until answered, declined or timed out, not just once.
            notification.Flags |= NotificationFlags.Insistent;
            Notify(context, id, notification);
        }

        static PendingIntent OpenApp(Context context, int requestCode, Func<Intent, Intent> extras)
        {
            var intent = extras(new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop));
            return PendingIntent.GetActivity(context, requestCode, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        }

        static void Notify(Context context, int id, Notification notification)
        {
            // Without the permission (Android 13+) the user has turned notifications off; there is nothing to show.
            if (OperatingSystem.IsAndroidVersionAtLeast(33)
                && context.CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Android.Content.PM.Permission.Granted)
                return;
            NotificationManagerCompat.From(context)!.Notify(id, notification);
        }

        static void EnsureChannels(Context context)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26) || context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
                return;

            manager.CreateNotificationChannel(new NotificationChannel(MessagesChannel, "Сообщения", NotificationImportance.High));

            var calls = new NotificationChannel(CallsChannel, "Звонки", NotificationImportance.High);
            calls.SetSound(RingtoneManager.GetDefaultUri(RingtoneType.Ringtone),
                new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.NotificationRingtone)!.SetContentType(AudioContentType.Sonification)!.Build());
            calls.EnableVibration(true);
            calls.LockscreenVisibility = NotificationVisibility.Public;
            manager.CreateNotificationChannel(calls);
        }
    }
}
