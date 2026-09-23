using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CatalogBridge
{
    // Runs OrderMismatch.FindAsync at start and every 10 minutes while the app is open, and
    // hands the result to Αρχική (SyncView). Read-only; the fix is the user's click there.
    // Runs whenever the database and the API are configured and the «orders» module is in
    // the licence (an expired one may still LOOK - only the cancel buttons refuse).
    public class OrderMismatchService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
        private readonly DispatcherTimer timer;
        private bool running;

        // (mismatches, status line)
        public event Action<List<Mismatch>, string>? Checked;

        public OrderMismatchService()
        {
            timer = new DispatcherTimer { Interval = Interval };
            timer.Tick += async (s, e) => await RunOnceAsync();
        }

        public void Apply(Settings settings)
        {
            bool shouldRun = settings.IsDatabaseConfigured && settings.IsApiConfigured &&
                             License.StateOf(Module.Orders) != LicenseState.NotLicensed;
            if (shouldRun && !timer.IsEnabled)
            {
                timer.Start();
                _ = RunOnceAsync();
            }
            else if (!shouldRun && timer.IsEnabled)
            {
                timer.Stop();
                Checked?.Invoke(new List<Mismatch>(), "");
            }
        }

        public Task RunNowAsync() => RunOnceAsync();

        private async Task RunOnceAsync()
        {
            if (running) return;
            running = true;
            try
            {
                Settings settings = Settings.Load();
                if (!settings.IsDatabaseConfigured || !settings.IsApiConfigured) return;
                using var api = new PointToOrderApiClient(settings);
                var found = await OrderMismatch.FindAsync(settings, api);
                Checked?.Invoke(found, found.Count == 0
                    ? $"Παραγγελίες: καμία ασυμφωνία τις τελευταίες 30 μέρες ({DateTime.Now:HH:mm})"
                    : $"Παραγγελίες: {found.Count} ασυμφωνί{(found.Count == 1 ? "α" : "ες")} ({DateTime.Now:HH:mm})");
            }
            catch (Exception ex)
            {
                Checked?.Invoke(new List<Mismatch>(), $"Έλεγχος παραγγελιών: σφάλμα ({DateTime.Now:HH:mm}) — {ex.Message}");
            }
            finally
            {
                running = false;
            }
        }
    }
}
