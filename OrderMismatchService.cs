using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CatalogBridge
{
    // Runs OrderMismatch.FindAsync at start and every 5 minutes while the app is open, and
    // hands the report to Αρχική (SyncView): order-level mismatches (cancelled on one side) and
    // per-linked-customer totals that differ. Read-only; the fix is the user's click there.
    // Runs whenever the database and the API are configured and the «orders» module is in
    // the licence (an expired one may still LOOK - only the buttons refuse).
    public class OrderMismatchService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
        private readonly DispatcherTimer timer;
        private bool running;

        // (report, status line)
        public event Action<MismatchReport, string>? Checked;

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
                Checked?.Invoke(new MismatchReport(), "");
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
                var report = await OrderMismatch.FindAsync(settings, api);
                string when = DateTime.Now.ToString("HH:mm");
                Checked?.Invoke(report, report.Count == 0
                    ? $"Καμία ασυμφωνία τις τελευταίες 30 μέρες ({when})"
                    : $"{(report.Orders.Count > 0 ? $"{report.Orders.Count} παραγγελί{(report.Orders.Count == 1 ? "α" : "ες")}" : "")}" +
                      (report.Orders.Count > 0 && report.Customers.Count > 0 ? " · " : "") +
                      $"{(report.Customers.Count > 0 ? $"{report.Customers.Count} πελάτ{(report.Customers.Count == 1 ? "ης" : "ες")} με διαφορετικά σύνολα" : "")}" +
                      $" ({when})");
            }
            catch (Exception ex)
            {
                Checked?.Invoke(new MismatchReport(), $"Έλεγχος: σφάλμα ({DateTime.Now:HH:mm}) — {ex.Message}");
            }
            finally
            {
                running = false;
            }
        }
    }
}
