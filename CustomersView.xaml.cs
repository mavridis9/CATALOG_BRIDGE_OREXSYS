using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CatalogBridge
{
    // «Πελάτες» (owner decision 21/09/2026): only the shop creates customers - once, here, in
    // Orexsys and on PointToOrder, with the card and the e-mail - so the customer's first online
    // order already finds them (orders match by e-mail only). The list shows the Orexsys
    // customers that carry an e-mail next to the PointToOrder accounts, matched on e-mail
    // exactly as the order trigger matches them. The invitation is a separate, deliberate button.
    public partial class CustomersView : UserControl
    {
        private List<CustomerRow> rows = new();

        public CustomersView()
        {
            InitializeComponent();
        }

        private async void LoadButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private static Settings? TryLoadSettings(Action<string> complain)
        {
            Settings settings;
            try { settings = Settings.Load(); }
            catch (Exception ex) { complain("Δεν ήταν δυνατή η ανάγνωση των ρυθμίσεων: " + ex.Message); return null; }

            if (!settings.IsDatabaseConfigured || !settings.IsApiConfigured)
            {
                complain("Συμπληρώστε πρώτα τις Ρυθμίσεις (βάση Orexsys και PointToOrder).");
                return null;
            }
            return settings;
        }

        private async Task LoadAsync(string? thenSay = null, Brush? colour = null)
        {
            var settings = TryLoadSettings(m => ShowStatus(m, Warn));
            if (settings == null) return;

            SetBusy(true);
            ShowStatus("Ανάγνωση πελατών από το Orexsys και το PointToOrder...", Normal);
            try
            {
                var orexsys = await CustomerDirectory.LoadOrexsysAsync(settings);
                using var api = new PointToOrderApiClient(settings);
                var platform = await api.GetCustomersAsync();

                rows = CustomerDirectory.Merge(orexsys, platform);
                var invites = CustomerDirectory.LoadInvites();
                foreach (var row in rows)
                    if (invites.TryGetValue(CustomerDirectory.InviteKey(row.Email), out string? when))
                        row.InvitedOn = when;

                ApplyFilter();

                int duplicates = rows.Count(r => r.Link == CustomerLink.DuplicateEmail);
                int platformOnly = rows.Count(r => r.Link == CustomerLink.PlatformOnly);
                string summary =
                    $"{rows.Count(r => r.Link == CustomerLink.Linked)} και στα δύο · " +
                    $"{rows.Count(r => r.Link == CustomerLink.OrexsysOnly)} μόνο στο Orexsys · " +
                    $"{platformOnly} μόνο στο PointToOrder" +
                    (duplicates > 0 ? $" · ⛔ {duplicates} με ίδιο e-mail — η παραγγελία θα πάει σε έναν από αυτούς στην τύχη" : "") +
                    ". Εμφανίζονται οι πελάτες του Orexsys που έχουν e-mail.";

                ShowStatus(thenSay != null ? thenSay + "\n" + summary : summary,
                    colour ?? (duplicates > 0 ? Bad : platformOnly > 0 ? Warn : Normal));
            }
            catch (Exception ex)
            {
                ShowStatus((thenSay != null ? thenSay + "\n" : "") + "Η φόρτωση απέτυχε: " + ex.Message, Bad);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            if (CustomersGrid == null || SearchBox == null) return;
            string search = CatalogDiff.Normalise(SearchBox.Text);
            CustomersGrid.ItemsSource = search.Length == 0 ? rows : rows.Where(r =>
                CatalogDiff.Normalise(r.Name).Contains(search) ||
                CatalogDiff.Normalise(r.Card).Contains(search) ||
                CatalogDiff.Normalise(r.Email).Contains(search)).ToList();
        }

        private CustomerRow? Selected => CustomersGrid.SelectedItem as CustomerRow;

        private void CustomersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = Selected;
            SendButton.IsEnabled = row is { Link: CustomerLink.OrexsysOnly };
            InviteButton.IsEnabled = row?.Platform is { Registered: false };
        }

        // ---- new customer ------------------------------------------------------------------

        private async void NewButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = TryLoadSettings(m => ShowStatus(m, Warn));
            if (settings == null) return;

            var window = new NewCustomerWindow { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() != true || window.Result == null) return;
            var input = window.Result;

            SetBusy(true);
            string message;
            Brush colour = Good;
            try
            {
                ShowStatus($"Δημιουργία «{input.Name}» στο Orexsys...", Normal);
                long custId = await CustomerDirectory.CreateInOrexsysAsync(settings, input);
                CustomerDirectory.Log($"Orexsys: created CUSTID {custId} card {input.Card} <{input.Email}>");
                message = $"✓ Ο «{input.Name}» δημιουργήθηκε στο Orexsys (κάρτα {input.Card}).";

                // The Orexsys customer is saved and is the important half: a platform failure
                // never undoes it - it is reported, and «Αποστολή στο PointToOrder» retries.
                try
                {
                    ShowStatus(message + " Δημιουργία στο PointToOrder...", Normal);
                    using var api = new PointToOrderApiClient(settings);
                    var outcome = await CustomerDirectory.CreateOnPlatformAsync(api, custId, input);
                    CustomerDirectory.Log($"PointToOrder: {outcome} card {input.Card} <{input.Email}>");
                    (string text, Brush brush) = Describe(outcome, input);
                    message += " " + text;
                    colour = brush;
                }
                catch (Exception ex)
                {
                    CustomerDirectory.Log($"PointToOrder: FAILED card {input.Card} <{input.Email}> - {ex.Message}");
                    message += $" ⚠ ΔΕΝ δημιουργήθηκε στο PointToOrder: {ex.Message} — επιλέξτε τον στη λίστα " +
                               "και πατήστε «Αποστολή στο PointToOrder».";
                    colour = Warn;
                }
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ShowStatus(ex.Message, ex is CustomerDirectory.DuplicateCustomerException ? Warn : Bad);
                return;
            }

            await LoadAsync(message, colour);
        }

        private static (string, Brush) Describe(CustomerDirectory.PlatformOutcome outcome, CustomerInput input) => outcome switch
        {
            CustomerDirectory.PlatformOutcome.Created =>
                ("✓ Δημιουργήθηκε και στο PointToOrder. Για να συνδεθεί, στείλτε του πρόσκληση από το κουμπί «Αποστολή πρόσκλησης».", Good),
            CustomerDirectory.PlatformOutcome.AlreadyThere =>
                ("Υπάρχει ήδη στο PointToOrder με αυτό το e-mail ή την κάρτα — δεν δημιουργήθηκε δεύτερος.", Warn),
            _ => ($"⚠ ΔΕΝ δημιουργήθηκε στο PointToOrder: το e-mail «{input.Email}» ή η κάρτα «{input.Card}» χρησιμοποιείται " +
                  "ήδη στην πλατφόρμα (πιθανόν σε άλλο κατάστημα).", Warn),
        };

        // ---- send an Orexsys-only customer to the platform -----------------------------------

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var row = Selected;
            if (row?.Orexsys == null || row.Link != CustomerLink.OrexsysOnly) return;
            var settings = TryLoadSettings(m => ShowStatus(m, Warn));
            if (settings == null) return;

            var input = new CustomerInput(row.Orexsys.Name, row.Orexsys.Card, row.Orexsys.Email, row.Orexsys.Phone);
            if (input.Card.Trim().Length == 0)
            {
                ShowStatus($"Ο «{input.Name}» δεν έχει κάρτα στο Orexsys — το PointToOrder τη χρειάζεται. Συμπληρώστε την πρώτα στο Orexsys.", Warn);
                return;
            }

            SetBusy(true);
            string message;
            Brush colour;
            try
            {
                using var api = new PointToOrderApiClient(settings);
                var outcome = await CustomerDirectory.CreateOnPlatformAsync(api, row.Orexsys.CustId, input);
                CustomerDirectory.Log($"PointToOrder: {outcome} card {input.Card} <{input.Email}> (from list)");
                (message, colour) = Describe(outcome, input);
                message = $"«{input.Name}»: " + message;
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ShowStatus("Η αποστολή απέτυχε: " + ex.Message, Bad);
                return;
            }
            await LoadAsync(message, colour);
        }

        // ---- invitation: a real e-mail, never automatic ---------------------------------------

        private async void InviteButton_Click(object sender, RoutedEventArgs e)
        {
            var row = Selected;
            if (row?.Platform == null || row.Platform.Registered) return;
            var settings = TryLoadSettings(m => ShowStatus(m, Warn));
            if (settings == null) return;

            var sure = MessageBox.Show(
                $"Να σταλεί πρόσκληση εγγραφής στο PointToOrder;\n\n{row.Name}\n{row.Email}\n\n" +
                (row.InvitedOn.Length > 0 ? $"Έχει ήδη σταλεί πρόσκληση στις {row.InvitedOn}.\n\n" : "") +
                "Είναι πραγματικό e-mail προς τον πελάτη και δεν ανακαλείται.",
                "Αποστολή πρόσκλησης", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (sure != MessageBoxResult.Yes) return;

            SetBusy(true);
            try
            {
                using var api = new PointToOrderApiClient(settings);
                await api.InviteCustomerAsync(row.Platform.Id);
                CustomerDirectory.RecordInvite(row.Email);
                CustomerDirectory.Log($"PointToOrder: invitation sent to <{row.Email}> (id {row.Platform.Id})");
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ShowStatus("Η πρόσκληση δεν στάλθηκε: " + ex.Message, Bad);
                return;
            }
            await LoadAsync($"✓ Στάλθηκε πρόσκληση στον «{row.Name}» ({row.Email}).", Good);
        }

        private void SetBusy(bool busy)
        {
            LoadButton.IsEnabled = !busy;
            NewButton.IsEnabled = !busy;
            CustomersGrid.IsEnabled = !busy;
            if (busy) { SendButton.IsEnabled = false; InviteButton.IsEnabled = false; }
            Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        }

        private static readonly Brush Normal = Brushes.Black;
        private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x2F, 0x6C, 0x43));
        private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0x8A, 0x5A, 0x1B));
        private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xA6, 0x3A, 0x22));

        private void ShowStatus(string message, Brush colour)
        {
            StatusText.Text = message;
            StatusText.Foreground = colour;
        }
    }
}
