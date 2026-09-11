using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CatalogBridge
{
    // The landing screen: one button, four numbers, and the price differences that matter.
    // Everything deeper lives one click away behind the quick-action buttons.
    public partial class DashboardView : UserControl
    {
        // Raised with "compare" / "push" / "efic" so the shell can switch screens.
        public event Action<string>? NavigateRequested;

        private bool autoRefreshDone;

        public DashboardView()
        {
            InitializeComponent();

            // "Synced all the time" starts with not having to click: the first time the
            // dashboard shows, the comparison runs by itself. Loaded fires again every time
            // the user navigates back, so it only auto-runs once - after that «Ανανέωση»
            // is the explicit refresh.
            Loaded += async (s, e) =>
            {
                if (autoRefreshDone) return;
                autoRefreshDone = true;
                await RefreshAsync();
            };
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async Task RefreshAsync()
        {
            Settings settings;
            try
            {
                settings = Settings.Load();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Δεν ήταν δυνατή η ανάγνωση των ρυθμίσεων: " + ex.Message;
                return;
            }

            if (!settings.IsDatabaseConfigured || !settings.IsApiConfigured)
            {
                StatusText.Text = "Συμπληρώστε πρώτα τις Ρυθμίσεις (κάτω αριστερά).";
                return;
            }

            RefreshButton.IsEnabled = false;
            StatusText.Text = "Σύγκριση σε εξέλιξη...";

            try
            {
                var orexsys = await OrexsysCatalog.LoadAsync(settings);

                using var api = new PointToOrderApiClient(settings);
                var platform = await api.GetCatalogAsync();

                PushRules.DropOrphanLinks(orexsys, platform);
                var diff = CatalogDiff.Build(orexsys, platform, settings);

                OkCount.Text = diff.CountOf(DiffStatus.Identical).ToString();
                PriceCount.Text = diff.CountOf(DiffStatus.HiddenOnline).ToString();
                UnmappedCount.Text = diff.CountOf(DiffStatus.PlatformOnly).ToString();
                // Same rule as the sync (PushRules), so this number is exactly what one press
                // of «Συγχρονισμός» would create - a product with sizes sent as separate items
                // counts once per size; internal rows (SYSTEM), unpriced/inactive items and
                // held-back products are not counted.
                PushCount.Text = orexsys.Products
                    .Sum(p => PushRules.SendableUnits(p, settings).Count).ToString();

                var hidden = diff.Rows
                    .Where(r => r.Status == DiffStatus.HiddenOnline)
                    .Take(8)
                    .ToList();

                DiffList.ItemsSource = hidden;
                NoDiffText.Text = hidden.Count == 0
                    ? "Ό,τι είναι ενεργό στο ταμείο πουλιέται και online. 🎉" : "";
                NoDiffText.Visibility = hidden.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                HeaderSubText.Text = $"Κατάστημα {settings.ApiStoreId} · " +
                                     $"{platform.Offers.Count} προϊόντα online · " +
                                     $"{orexsys.Products.Count} προϊόντα Orexsys";

                StatusText.Text = $"Τελευταία ανανέωση {DateTime.Now:HH:mm}. " +
                                  $"Ταίριασμα: {diff.MatchedByCode} με κωδικό, {diff.MatchedByName} με όνομα.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Η σύγκριση απέτυχε: " + ex.Message;
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }

        private void OpenCompare_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("compare");
        private void OpenPush_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("push");
        private void OpenEfic_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("efic");
    }
}
