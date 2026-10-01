using System;
using System.Windows;
using System.Windows.Media;

namespace CatalogBridge
{
    // Thin shell: the petrol rail on the left, one view on the right. Staff use «Αρχική» (online
    // orders + the catalogue button), «Αντιστοίχιση παραγγελιών» (everything that rides on the
    // e-mail correlation: the trigger switch, till orders → PointToOrder, the mismatch check)
    // and «Πελάτες»; the technician screens appear when enabled in Ρυθμίσεις. Views are created
    // once and kept alive, so switching screens never loses loaded data or selections.
    public partial class MainWindow : Window
    {
        private readonly SyncView sync = new();
        private readonly OrdersView orders = new();
        private readonly DashboardView dashboard = new();
        private readonly CompareView compare = new();
        private readonly PushView push = new();
        private readonly EficView efic = new();
        private readonly CustomersView customers = new();
        private readonly StockSyncService stockSync = new();
        private readonly TillOrderSyncService tillSync = new();
        private readonly OrderMismatchService mismatches = new();
        private readonly OrderMirrorService mirror = new();

        public MainWindow()
        {
            InitializeComponent();

            // The released version, from <Version> in the csproj.
            var version = typeof(MainWindow).Assembly.GetName().Version;
            if (version != null)
                VersionText.Text = $"Orexsys ⇄ PointToOrder  v{version.Major}.{version.Minor}.{version.Build}";

            dashboard.NavigateRequested += NavigateTo;
            ContentHost.Content = sync;

            stockSync.StatusChanged += message => Dispatcher.Invoke(() =>
            {
                StockSyncText.Text = message;
                StockSyncText.Visibility = message.Length > 0
                    ? Visibility.Visible : Visibility.Collapsed;
            });

            orders.TillSync = tillSync;
            tillSync.StatusChanged += message => Dispatcher.Invoke(() => orders.ShowTillSyncStatus(message));
            orders.Mismatches = mismatches;
            mismatches.Checked += (report, status) => Dispatcher.Invoke(() =>
            {
                orders.ShowMismatches(report, status);
                // The mismatch card no longer sits on Αρχική: the rail carries the count.
                OrdersBadgeText.Text = report.Count.ToString();
                OrdersBadge.Visibility = report.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            });
            orders.SettingsChanged += ApplySettings;
            sync.Mirror = mirror;
            mirror.Changed += state => Dispatcher.Invoke(() => sync.ShowMirror(state));

            ApplySettings();
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            // Fires during InitializeComponent for the default-checked item, before the
            // content host exists.
            if (ContentHost == null) return;

            ContentHost.Content =
                ReferenceEquals(sender, OrdersNav) ? orders :
                ReferenceEquals(sender, DashNav) ? dashboard :
                ReferenceEquals(sender, CompareNav) ? compare :
                ReferenceEquals(sender, PushNav) ? push :
                ReferenceEquals(sender, EficNav) ? efic :
                ReferenceEquals(sender, CustomersNav) ? (object)customers :
                sync;
        }

        private void NavigateTo(string target)
        {
            switch (target)
            {
                case "compare": CompareNav.IsChecked = true; break;
                case "push": PushNav.IsChecked = true; break;
                case "efic": EficNav.IsChecked = true; break;
                default: SyncNav.IsChecked = true; break;
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // Settings are technician-only: the PIN gate keeps the store staff on «Αρχική».
            var pin = new PinPromptWindow { Owner = this };
            if (pin.ShowDialog() != true) return;

            var window = new SettingsWindow { Owner = this };
            window.ShowDialog();
            ApplySettings();
        }

        // Reflects the settings in the shell: which screens exist in the rail, and what the
        // foot of the rail says. If the screen currently shown was just hidden, the user lands
        // back on «Αρχική».
        private void ApplySettings()
        {
            Settings settings;
            try
            {
                settings = Settings.Load();
            }
            catch (Exception)
            {
                settings = new Settings();
            }

            // Licence (docs/LICENSING_AND_UPDATES.md §4.1): a module that is not in the token
            // has its screens hidden; an expired one keeps its screens (read-only - each write
            // path refuses by itself) and loses its automatic services. Never removes anything.
            License.Load();
            bool ordersKnown = License.StateOf(Module.Orders) != LicenseState.NotLicensed;
            bool showCustomers = settings.ShowCustomers && ordersKnown;

            OrdersNav.Visibility = ordersKnown ? Visibility.Visible : Visibility.Collapsed;
            DashNav.Visibility = settings.ShowDashboard ? Visibility.Visible : Visibility.Collapsed;
            CompareNav.Visibility = settings.ShowCompare ? Visibility.Visible : Visibility.Collapsed;
            PushNav.Visibility = settings.ShowPush ? Visibility.Visible : Visibility.Collapsed;
            EficNav.Visibility = settings.ShowEfic ? Visibility.Visible : Visibility.Collapsed;
            CustomersNav.Visibility = showCustomers ? Visibility.Visible : Visibility.Collapsed;
            TechnicianLabel.Visibility = settings.ShowDashboard || settings.ShowCompare || settings.ShowPush || settings.ShowEfic
                ? Visibility.Visible : Visibility.Collapsed;

            bool currentHidden =
                (OrdersNav.IsChecked == true && !ordersKnown) ||
                (DashNav.IsChecked == true && !settings.ShowDashboard) ||
                (CompareNav.IsChecked == true && !settings.ShowCompare) ||
                (PushNav.IsChecked == true && !settings.ShowPush) ||
                (EficNav.IsChecked == true && !settings.ShowEfic) ||
                (CustomersNav.IsChecked == true && !showCustomers);
            if (currentHidden) SyncNav.IsChecked = true;

            string database = System.IO.Path.GetFileName(settings.Database);
            StoreInfoText.Text = settings.IsApiConfigured
                ? $"Κατάστημα PointToOrder {settings.ApiStoreId}\nΒάση {database}"
                : "Δεν έχει ρυθμιστεί ακόμα";
            LicenceText.Text = License.Summary();
            LicenceText.Foreground = License.Worst() switch
            {
                LicenseState.Licensed => (Brush)FindResource("RailFaint"),
                LicenseState.Grace => new SolidColorBrush(Color.FromRgb(0xF0, 0xB6, 0x5A)),
                _ => new SolidColorBrush(Color.FromRgb(0xF0, 0x8A, 0x7C)),
            };
            sync.ShowLicence();

            stockSync.Apply(settings);
            tillSync.Apply(settings);
            mismatches.Apply(settings);
            orders.ShowState(settings);
            sync.ShowMirrorState(settings);
            mirror.Apply(settings);
            EnsureEmailCorrelation(settings);
        }

        // At every start (and after Ρυθμίσεις): if the e-mail correlation is on, make sure the
        // patch is still in Orexsys's trigger - an Orexsys upgrade silently restores the vendor
        // version. A no-op when it is there; «off» never touches the trigger.
        private void EnsureEmailCorrelation(Settings settings)
        {
            if (!settings.EmailOrderCorrelation || !settings.IsDatabaseConfigured) return;
            // Without an «orders» licence the patch is neither re-applied nor removed: ΔΙΑΧΕΙΡΙΣΗ
            // may depend on it at the same shop (§4.1, trigger ownership).
            if (!License.CanWrite(Module.Orders)) return;

            string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "backups");
            System.Threading.Tasks.Task.Run(() => EfomTriggerGuard.Run(settings, folder)).ContinueWith(t =>
            {
                if (t.IsFaulted) return;
                var result = t.Result;
                Dispatcher.Invoke(() =>
                {
                    if (result.Outcome == EfomTriggerGuard.Outcome.Patched)
                        MessageBox.Show("Η αντιστοίχιση παραγγελιών με e-mail είχε αφαιρεθεί από το Orexsys " +
                                        "(πιθανόν από αναβάθμιση) και εφαρμόστηκε ξανά.",
                                        "Γέφυρα Καταλόγου", MessageBoxButton.OK, MessageBoxImage.Information);
                    else if (result.IsProblem)
                        MessageBox.Show("Η αντιστοίχιση παραγγελιών με e-mail ΔΕΝ είναι ενεργή στο Orexsys:\n\n" +
                                        result.Message, "Γέφυρα Καταλόγου", MessageBoxButton.OK, MessageBoxImage.Warning);
                    orders.RefreshEmailState();
                });
            });
        }
    }
}
