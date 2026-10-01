using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace CatalogBridge
{
    // The app's motion in one place, so every screen moves the same way:
    //   * entering things fade + rise a few pixels, 200 ms, a strong ease-out (starts fast -
    //     the user is watching the first frame, not the last);
    //   * state changes (a dot, a pill) crossfade in 200 ms; nothing moves;
    //   * a pressed button shrinks to 0.97 in 100 ms and springs back in 160 ms;
    //   * nothing that happens every few seconds animates (a refreshed counter just changes);
    //   * when Windows says «no animations» (Settings → Accessibility → Visual effects), it all
    //     collapses to zero duration - the UI simply changes state.
    // Durations are published as resources (App.xaml) so XAML storyboards share them.
    public static class Motion
    {
        public static bool Enabled { get; private set; } = true;

        public static Duration Enter => Enabled ? new Duration(TimeSpan.FromMilliseconds(200)) : new Duration(TimeSpan.Zero);
        public static Duration Fade => Enabled ? new Duration(TimeSpan.FromMilliseconds(200)) : new Duration(TimeSpan.Zero);
        public static Duration Press => Enabled ? new Duration(TimeSpan.FromMilliseconds(100)) : new Duration(TimeSpan.Zero);
        public static Duration Release => Enabled ? new Duration(TimeSpan.FromMilliseconds(160)) : new Duration(TimeSpan.Zero);

        // cubic-bezier(0.23, 1, 0.32, 1): the «strong ease-out» used for UI entrances.
        public static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Called once at start-up; writes the durations into the application resources.
        public static void Apply(ResourceDictionary resources)
        {
            Enabled = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
            resources["EnterDuration"] = Enter;
            resources["FadeDuration"] = Fade;
            resources["PressDuration"] = Press;
            resources["ReleaseDuration"] = Release;
        }
    }
}
