namespace Mi_nU.Services
{
    /// <summary>
    /// Whether the user can see the app: its window is in front on Windows, the app is on screen on Android.
    /// A chat counts as read only then, and notifications show only otherwise.
    /// </summary>
    public class AppActivity
    {
        public bool IsActive { get; private set; } = true;

        /// <summary>Raised on the main thread.</summary>
        public event Action? Changed;

        /// <summary>Called from the window's Activated and Deactivated events, which Android raises on resume and pause.</summary>
        public void Set(bool active)
        {
            if (IsActive == active)
                return;

            IsActive = active;
            Changed?.Invoke();
        }
    }
}
