using System;
using System.Windows;

namespace CatalogBridge
{
    // Thin shell: sidebar navigation on the left, one view on the right. The end user sees
    // only «Αρχική» (the one-button sync); the technician screens appear when enabled in the
    // Settings screen. Views are created once and kept alive, so switching screens never
    // loses loaded data or selections.
    public partial class MainWindow : Window
    {
        private readonly SyncView sync = new();
        private readonly DashboardView dashboard = new();
        private readonly CompareView compare = new();
        private readonly PushView push = new();
        private readonly EficView efic = new();
        private readonly CustomersView customers = new();
        private readonly StockSyncService stockSync = new();
        private readonly TillOrderSyncService tillSync = new();

        public MainWindow()
        {
            InitializeComponent();

            // The released version, from <Version> in the csproj.
            var version = typeof(MainWindow).Assembly.GetName().Version;
            if (version != null)
                VersionText.Text = $"Γέφυρα PointToOrder  •  v{version.Major}.{version.Minor}.{version.Build}";

            dashboard.NavigateRequested += NavigateTo;
            ContentHost.Content = sync;

            stockSync.StatusChanged += message => Dispatcher.Invoke(() =>
            {
                StockSyncText.Text = message;
                StockSyncText.Visibility = message.Length > 0
                    ? Visibility.Visible : Visibility.Collapsed;
            });

            sync.TillSync = tillSync;
            tillSync.StatusChanged += message => Dispatcher.Invoke(() => sync.ShowTillSyncStatus(message));

            ApplySettings();
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            // Fires during InitializeComponent for the default-checked item, before the
            // content host exists.
            if (ContentHost == null) return;

            ContentHost.Content =
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

        // Reflects the settings in the shell: which technician screens exist in the sidebar,
        // and what the store card says. If the screen currently shown was just hidden, the
        // user lands back on «Αρχική».
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

            DashNav.Visibility = settings.ShowDashboard ? Visibility.Visible : Visibility.Collapsed;
            CompareNav.Visibility = settings.ShowCompare ? Visibility.Visible : Visibility.Collapsed;
            PushNav.Visibility = settings.ShowPush ? Visibility.Visible : Visibility.Collapsed;
            EficNav.Visibility = settings.ShowEfic ? Visibility.Visible : Visibility.Collapsed;
            CustomersNav.Visibility = settings.ShowCustomers ? Visibility.Visible : Visibility.Collapsed;

            bool currentHidden =
                (DashNav.IsChecked == true && !settings.ShowDashboard) ||
                (CompareNav.IsChecked == true && !settings.ShowCompare) ||
                (PushNav.IsChecked == true && !settings.ShowPush) ||
                (EficNav.IsChecked == true && !settings.ShowEfic) ||
                (CustomersNav.IsChecked == true && !settings.ShowCustomers);
            if (currentHidden) SyncNav.IsChecked = true;

            string database = System.IO.Path.GetFileName(settings.Database);
            StoreInfoText.Text = settings.IsApiConfigured
                ? $"PointToOrder {settings.ApiStoreId}\n{database}"
                : "Δεν έχει ρυθμιστεί ακόμα";

            stockSync.Apply(settings);
            tillSync.Apply(settings);
            sync.ShowTillSyncState(settings);
            EnsureEmailCorrelation(settings);
        }

        // At every start (and after Ρυθμίσεις): if the e-mail correlation is on, make sure the
        // patch is still in Orexsys's trigger - an Orexsys upgrade silently restores the vendor
        // version. A no-op when it is there; «off» never touches the trigger.
        private void EnsureEmailCorrelation(Settings settings)
        {
            if (!settings.EmailOrderCorrelation || !settings.IsDatabaseConfigured) return;

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
                });
            });
        }
    }
}
