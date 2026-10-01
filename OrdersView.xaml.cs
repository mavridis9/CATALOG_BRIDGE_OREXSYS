using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CatalogBridge
{
    // «Αντιστοίχιση παραγγελιών» (owner, 01/10/2026): the e-mail correlation and everything that
    // rides on it, on one screen of its own. Three cards:
    //   1. the correlation itself - EfomTriggerGuard patches Orexsys's EFOM_BIUD_TRG (moved here
    //      from Ρυθμίσεις; still behind the technician's PIN, since it ALTERs a trigger);
    //   2. till orders → the customer's PointToOrder history (TillOrderSyncService);
    //   3. the mismatch check (OrderMismatchService), with its one-click fixes.
    // MainWindow owns the services and wires them here.
    public partial class OrdersView : UserControl
    {
        public OrdersView()
        {
            InitializeComponent();
        }

        // Raised after this screen changed a setting, so the shell re-applies everything.
        public event Action? SettingsChanged;

        private static string BackupFolder => System.IO.Path.Combine(AppContext.BaseDirectory, "backups");

        private Brush Res(string key) => (Brush)FindResource(key);

        public void ShowState(Settings settings)
        {
            EmailSwitch.Tag = settings.EmailOrderCorrelation ? "True" : "False";
            ShowTillSyncState(settings);
            RefreshEmailState();
        }

        // ---- 1. the correlation ---------------------------------------------------------------

        // What the live trigger holds right now - read-only.
        public async void RefreshEmailState()
        {
            Settings settings;
            try { settings = Settings.Load(); }
            catch { return; }
            if (!settings.IsDatabaseConfigured)
            {
                SetEmailPill("Χωρίς βάση", "HoverBg", "MutedBrush", "Ορίστε τη βάση του Orexsys στις Ρυθμίσεις.");
                return;
            }
            try
            {
                var state = await Task.Run(() => EfomTriggerGuard.ReadState(settings));
                switch (state)
                {
                    case EfomTriggerGuard.State.Patched:
                        SetEmailPill("Ενεργή στο Orexsys", "GoodSoft", "GoodBrush", "Οι παραγγελίες ταιριάζουν μόνο με το e-mail.");
                        break;
                    case EfomTriggerGuard.State.PatchedOlder:
                        SetEmailPill("Παλαιότερη έκδοση", "WarnSoft", "WarnBrush", "Ενεργοποιήστε ξανά για να γίνει «μόνο e-mail».");
                        break;
                    case EfomTriggerGuard.State.NotPatched:
                        SetEmailPill("Δεν έχει εφαρμοστεί", "HoverBg", "MutedBrush", "Οι παραγγελίες ταιριάζουν μόνο με το τηλέφωνο.");
                        break;
                    default:
                        SetEmailPill("Δεν βρέθηκε", "BadSoft", "BadBrush", "Το trigger EFOM_BIUD_TRG δεν υπάρχει σε αυτή τη βάση.");
                        break;
                }
                RemoveEmailButton.Visibility = state is EfomTriggerGuard.State.Patched or EfomTriggerGuard.State.PatchedOlder
                    ? Visibility.Visible : Visibility.Collapsed;
                TillNeedsEmail.Visibility = state == EfomTriggerGuard.State.Patched ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex)
            {
                SetEmailPill("Άγνωστη", "WarnSoft", "WarnBrush", "Δεν ήταν δυνατή η ανάγνωση: " + ex.Message);
            }
        }

        private void SetEmailPill(string pill, string bg, string ink, string detail)
        {
            EmailStatePill.Background = Res(bg);
            EmailStatePillText.Foreground = Res(ink);
            EmailStatePillText.Text = pill;
            EmailStateText.Text = detail;
        }

        private bool TechnicianPin() => new PinPromptWindow { Owner = Window.GetWindow(this) }.ShowDialog() == true;

        private async void EmailSwitch_Click(object sender, RoutedEventArgs e)
        {
            Settings settings;
            try { settings = Settings.Load(); }
            catch (Exception ex) { EmailStateText.Text = "Δεν ήταν δυνατή η ανάγνωση των ρυθμίσεων: " + ex.Message; return; }

            bool turningOn = !settings.EmailOrderCorrelation;
            if (turningOn)
            {
                if (License.Blocked(Module.Orders, out string why)) { EmailStateText.Text = why; return; }
                if (!settings.IsDatabaseConfigured) { EmailStateText.Text = "Ορίστε πρώτα τη βάση του Orexsys στις Ρυθμίσεις."; return; }
                if (!TechnicianPin()) return;
                var sure = MessageBox.Show(
                    "Να ταιριάζουν οι online παραγγελίες με τον πελάτη μόνο από το e-mail του;\n\n" +
                    "Αλλάζει το trigger EFOM_BIUD_TRG του Orexsys (κρατιέται πρώτα αντίγραφο). " +
                    "Το Orexsys πρέπει να είναι κλειστό σε όλους τους σταθμούς αυτή τη στιγμή.\n\nΣυνέχεια;",
                    "Αντιστοίχιση με e-mail", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (sure != MessageBoxResult.Yes) return;

                EmailSwitch.IsEnabled = false;
                SetEmailPill("Εφαρμογή...", "HoverBg", "MutedBrush", "Αλλάζει το trigger στο Orexsys.");
                var result = await Task.Run(() => EfomTriggerGuard.Run(settings, BackupFolder));
                EmailSwitch.IsEnabled = true;
                if (result.IsProblem)
                {
                    MessageBox.Show("Η αντιστοίχιση με e-mail ΔΕΝ εφαρμόστηκε:\n\n" + result.Message,
                        "Αντιστοίχιση με e-mail", MessageBoxButton.OK, MessageBoxImage.Warning);
                    RefreshEmailState();
                    return;
                }
                settings.EmailOrderCorrelation = true;
            }
            else
            {
                if (!TechnicianPin()) return;
                var sure = MessageBox.Show(
                    "Να σταματήσει η αντιστοίχιση με e-mail σε αυτό το πρόγραμμα;\n\n" +
                    "Το trigger του Orexsys μένει όπως είναι (μπορεί να το χρειάζεται η ΔΙΑΧΕΙΡΙΣΗ). " +
                    "Για να αφαιρεθεί και από το Orexsys, πατήστε «Αφαίρεση από το Orexsys...».\n\nΣυνέχεια;",
                    "Αντιστοίχιση με e-mail", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (sure != MessageBoxResult.Yes) return;
                settings.EmailOrderCorrelation = false;
            }

            try { Settings.Save(settings); }
            catch (Exception ex) { EmailStateText.Text = "Η αποθήκευση απέτυχε: " + ex.Message; return; }
            CustomerDirectory.Log($"email correlation: switched {(settings.EmailOrderCorrelation ? "ON" : "OFF")} from «Αντιστοίχιση παραγγελιών»");
            SettingsChanged?.Invoke();
        }

        private async void RemoveEmailButton_Click(object sender, RoutedEventArgs e)
        {
            Settings settings;
            try { settings = Settings.Load(); }
            catch (Exception ex) { EmailStateText.Text = "Δεν ήταν δυνατή η ανάγνωση των ρυθμίσεων: " + ex.Message; return; }
            if (!TechnicianPin()) return;
            var sure = MessageBox.Show(
                "Να αφαιρεθεί η αντιστοίχιση με e-mail από το trigger του Orexsys;\n\n" +
                "Οι παραγγελίες θα ταιριάζουν πάλι μόνο με τηλέφωνο. Αν το ίδιο Orexsys το χρησιμοποιεί " +
                "και η ΔΙΑΧΕΙΡΙΣΗ, εκείνη θα την ξαναβάλει στην επόμενη εκκίνησή της.",
                "Αφαίρεση", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (sure != MessageBoxResult.Yes) return;

            RemoveEmailButton.IsEnabled = false;
            var result = await Task.Run(() => EfomTriggerGuard.Remove(settings, BackupFolder));
            RemoveEmailButton.IsEnabled = true;

            if (result.Outcome == EfomTriggerGuard.Outcome.Removed)
            {
                settings.EmailOrderCorrelation = false;
                try { Settings.Save(settings); } catch { }
                CustomerDirectory.Log("email correlation: REMOVED from the trigger from «Αντιστοίχιση παραγγελιών»");
            }
            MessageBox.Show(result.Outcome == EfomTriggerGuard.Outcome.Removed
                    ? "Η αντιστοίχιση με e-mail αφαιρέθηκε από το Orexsys."
                    : result.Message,
                "Αφαίρεση", MessageBoxButton.OK,
                result.IsProblem ? MessageBoxImage.Warning : MessageBoxImage.Information);
            SettingsChanged?.Invoke();
        }

        // ---- 2. till orders → PointToOrder, automatic ------------------------------------------

        public TillOrderSyncService? TillSync { get; set; }

        public void ShowTillSyncState(Settings settings)
        {
            bool on = settings.TillOrderSyncEnabled;
            TillSyncButton.Tag = on ? "True" : "False";
            TillSyncStateText.Text = on
                ? $"Από {settings.TillOrderSyncSince:dd/MM/yyyy}: κάθε κλειστή παραγγελία του ταμείου για πελάτη με λογαριασμό " +
                  "PointToOrder εμφανίζεται μόνη της και στο ιστορικό του εκεί, αν είναι ολόιδια (ίδια προϊόντα, ίδιες τιμές)."
                : "Όταν είναι ενεργό, κάθε κλειστή παραγγελία του ταμείου για πελάτη με λογαριασμό PointToOrder εμφανίζεται " +
                  "και στο ιστορικό του εκεί, από τη μέρα ενεργοποίησης και μετά.";
            if (!on) TillSyncStatusText.Text = "";
        }

        public void ShowTillSyncStatus(string message) => TillSyncStatusText.Text = message;

        private void ShowTillMessage(string message, string ink)
        {
            TillSyncMessage.Text = message;
            TillSyncMessage.Foreground = Res(ink);
            TillSyncMessage.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TillSyncButton_Click(object sender, RoutedEventArgs e)
        {
            Settings settings;
            try { settings = Settings.Load(); }
            catch (Exception ex) { ShowTillMessage("Δεν ήταν δυνατή η ανάγνωση των ρυθμίσεων: " + ex.Message, "BadBrush"); return; }
            ShowTillMessage("", "MutedBrush");

            if (!settings.TillOrderSyncEnabled)
            {
                if (License.Blocked(Module.Orders, out string why)) { ShowTillMessage(why, "BadBrush"); return; }
                if (!settings.IsDatabaseConfigured || !settings.IsApiConfigured)
                {
                    ShowTillMessage("Το πρόγραμμα δεν έχει ρυθμιστεί ακόμα. Ανοίξτε τις Ρυθμίσεις, κάτω αριστερά.", "WarnBrush");
                    return;
                }
                if (!settings.EmailOrderCorrelation)
                {
                    ShowTillMessage("Ενεργοποιήστε πρώτα την αντιστοίχιση με e-mail, παραπάνω.", "WarnBrush");
                    return;
                }
                var sure = MessageBox.Show(
                    "Να στέλνονται αυτόματα οι παραγγελίες του ταμείου στο PointToOrder;\n\n" +
                    $"Από σήμερα ({DateTime.Today:dd/MM/yyyy}) και μετά, κάθε κλειστή παραγγελία πελάτη που έχει λογαριασμό " +
                    "PointToOrder θα εμφανίζεται και στο ιστορικό του εκεί, ως πληρωμένη στο κατάστημα. Παλιότερες παραγγελίες " +
                    "δεν στέλνονται. Παραγγελία με προϊόν που δεν υπάρχει online, ή με άλλη τιμή, δεν στέλνεται και καταγράφεται.",
                    "Παραγγελίες ταμείου στο PointToOrder", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (sure != MessageBoxResult.Yes) return;

                settings.TillOrderSyncEnabled = true;
                settings.TillOrderSyncSince = DateTime.Today;
            }
            else
            {
                settings.TillOrderSyncEnabled = false;
            }

            try { Settings.Save(settings); }
            catch (Exception ex) { ShowTillMessage("Η αποθήκευση απέτυχε: " + ex.Message, "BadBrush"); return; }

            ShowTillSyncState(settings);
            TillSync?.Apply(settings);
        }

        // ---- 3. mismatches ------------------------------------------------------------------------

        public OrderMismatchService? Mismatches { get; set; }

        private MismatchReport lastReport = new();

        public void ShowMismatches(MismatchReport report, string status)
        {
            lastReport = report;
            MismatchGrid.ItemsSource = report.Orders;
            MismatchGrid.Visibility = report.Orders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CustomerTotalsHeader.Visibility = CustomerTotalsGrid.Visibility =
                report.Customers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CustomerTotalsGrid.ItemsSource = report.Customers;
            MismatchCountText.Text = report.Count.ToString();
            MismatchCount.Visibility = report.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            MismatchStatusText.Text = status;
            bool error = status.Contains("σφάλμα");
            MismatchStatusText.Foreground = error ? Res("BadBrush") : report.Count > 0 ? Res("MutedBrush") : Res("GoodBrush");
        }

        // «Παραγγελίες...» on a customer whose totals differ: the same window as the double-click
        // in Πελάτες, where each order can be sent or cancelled.
        private async void CustomerTotalsOpen_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CustomerTotals c) return;
            Settings settings;
            try { settings = Settings.Load(); }
            catch (Exception ex) { MismatchStatusText.Text = "Δεν ήταν δυνατή η ανάγνωση των ρυθμίσεων: " + ex.Message; return; }
            new CustomerOrdersWindow(settings, c.Customer) { Owner = Window.GetWindow(this) }.ShowDialog();
            if (Mismatches != null) await Mismatches.RunNowAsync();
        }

        private async void MismatchCheckButton_Click(object sender, RoutedEventArgs e)
        {
            if (Mismatches == null) return;
            MismatchCheckButton.IsEnabled = false;
            MismatchStatusText.Text = "Έλεγχος...";
            try { await Mismatches.RunNowAsync(); }
            finally { MismatchCheckButton.IsEnabled = true; }
        }

        // The fix, on the user's word only: the side that is still active gets cancelled the
        // same way the «Ακύρωση παραγγελίας» button does it (OrderCancel / TillOrderSender).
        private async void MismatchFix_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not Mismatch m) return;
            if (License.Blocked(Module.Orders, out string why)) { ShowMismatches(lastReport, why); return; }

            bool onPlatform = m.Kind == MismatchKind.CancelledInOrexsysActiveOnPlatform;
            var sure = MessageBox.Show(
                $"{m.Title}\n{m.Problem}\n\n" +
                (onPlatform
                    ? $"Να ακυρωθεί και στο PointToOrder ({m.Token}); Θα φύγει από το ιστορικό του πελάτη."
                    : "Να ακυρωθεί και στο Orexsys; Γίνεται όπως στο ταμείο: αντιστρέφονται τα είδη και οι πληρωμές και εκδίδεται " +
                      "ακυρωτικό παραστατικό. ΔΕΝ αναιρείται.") +
                "\n\nΣυνέχεια;",
                m.Action, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (sure != MessageBoxResult.Yes) return;

            Settings settings;
            try { settings = Settings.Load(); }
            catch (Exception ex) { MismatchStatusText.Text = "Δεν ήταν δυνατή η ανάγνωση των ρυθμίσεων: " + ex.Message; return; }

            MismatchStatusText.Text = $"Ακύρωση #{m.OrdmId}...";
            try
            {
                if (onPlatform)
                {
                    using var api = new PointToOrderApiClient(settings);
                    await TillOrderSender.CancelOnPlatformAsync(api, m.Uuid);
                    CustomerDirectory.Log($"mismatch: order {m.OrdmId} cancelled on PointToOrder ({m.Token}) - was cancelled in Orexsys");
                }
                else
                {
                    await OrderCancel.CancelInOrexsysAsync(settings, m.OrdmId, $"Γέφυρα: ακυρωμένη στο P2O {m.Token}");
                    CustomerDirectory.Log($"mismatch: order {m.OrdmId} cancelled in Orexsys - was cancelled on PointToOrder ({m.Token})");
                }
            }
            catch (Exception ex)
            {
                CustomerDirectory.Log($"mismatch: order {m.OrdmId} FAILED - {ex.Message}");
                MismatchStatusText.Text = $"Η ακύρωση της #{m.OrdmId} απέτυχε: {ex.Message}";
                MismatchStatusText.Foreground = Res("BadBrush");
                return;
            }
            if (Mismatches != null) await Mismatches.RunNowAsync();
        }
    }
}
