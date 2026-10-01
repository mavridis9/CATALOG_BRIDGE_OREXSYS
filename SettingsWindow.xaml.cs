using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Win32;

namespace CatalogBridge
{
    public partial class SettingsWindow : Window
    {
        private class PriceListOption
        {
            public long Id { get; init; }
            public string Label { get; init; } = "";
        }

        private class PriceSourceOption
        {
            public PriceSource Value { get; init; }
            public string Label { get; init; } = "";
            public override string ToString() => Label;
        }

        private class EficConnectionOption
        {
            public string Code { get; init; } = "";
            public string Label { get; init; } = "";
        }

        public SettingsWindow()
        {
            InitializeComponent();
            LoadIntoForm();

            // The PointToOrder connections live in the database, so they are read as soon as
            // the window is up rather than making the technician press «Έλεγχος βάσης» first.
            Loaded += async (s, e) => await RefreshEficConnectionsAsync(BuildFromForm());
        }

        private void LoadIntoForm()
        {
            var settings = Settings.Load();
            loaded = settings;

            ServerBox.Text = settings.Server;
            PortBox.Text = settings.Port;
            DatabaseBox.Text = settings.Database;
            UserBox.Text = settings.User;
            PasswordBox.Password = settings.Password;

            SelectComboText(CharsetCombo, settings.Charset);

            ApiUrlBox.Text = settings.ApiBaseUrl;
            ApiUserBox.Text = settings.ApiUsername;
            ApiPasswordBox.Password = settings.ApiPassword;
            ApiStoreBox.Text = settings.ApiStoreId;

            PriceSourceCombo.ItemsSource = new List<PriceSourceOption>
            {
                new() { Value = PriceSource.Delivery, Label = "Delivery (DELIVERYPRICE)" },
                new() { Value = PriceSource.TakeAway, Label = "Take away (TAKEAWAYPRICE)" },
                new() { Value = PriceSource.Restaurant, Label = "Καταστήματος (RESTPRICE)" },
            };
            PriceSourceCombo.SelectedIndex = settings.PriceSource switch
            {
                PriceSource.TakeAway => 1,
                PriceSource.Restaurant => 2,
                _ => 0,
            };

            // The price lists can only be read once the database details are valid, so the combo
            // starts holding just the saved id and is filled in properly by "Έλεγχος βάσης".
            PriceListCombo.ItemsSource = new List<PriceListOption>
            {
                new() { Id = settings.PriceListId, Label = $"#{settings.PriceListId}" },
            };
            PriceListCombo.SelectedIndex = 0;

            // Same idea for the PointToOrder connection: the saved code is shown at once and the
            // real EFST lines replace it as soon as the database answers.
            savedEficCode = settings.EficStoreCode;
            EficConnectionCombo.ItemsSource = savedEficCode.Length > 0
                ? new List<EficConnectionOption> { new() { Code = savedEficCode, Label = savedEficCode } }
                : new List<EficConnectionOption>();
            EficConnectionCombo.SelectedIndex = savedEficCode.Length > 0 ? 0 : -1;

            StockSyncCheck.IsChecked = settings.StockSyncEnabled;
            ShowLicence();

            SizeModeCombo.ItemsSource = new List<SizeModeOption>
            {
                new() { Value = SizeMode.SeparateItems, Label = "Ξεχωριστά προϊόντα ανά μέγεθος (προτείνεται)" },
                new() { Value = SizeMode.OptionGroup, Label = "Ένα προϊόν με επιλογές μεγέθους (επιβάρυνση ανά μέγεθος)" },
            };
            SizeModeCombo.SelectedIndex = settings.SizeMode == SizeMode.OptionGroup ? 1 : 0;

            syncAllCategories = settings.SyncAllCategories;
            syncCategoryIds = Settings.ParseIdSet(settings.SyncCategoryIds);
            categoryPriceLists = Settings.ParsePriceListMap(settings.CategoryPriceLists);
            UpdateCategorySummary();
            ShowDashboardCheck.IsChecked = settings.ShowDashboard;
            ShowCompareCheck.IsChecked = settings.ShowCompare;
            ShowPushCheck.IsChecked = settings.ShowPush;
            ShowEficCheck.IsChecked = settings.ShowEfic;
            ShowCustomersCheck.IsChecked = settings.ShowCustomers;
        }

        private static void SelectComboText(ComboBox combo, string value)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            // An unusual charset from a hand-edited ini is kept rather than silently replaced.
            combo.Items.Add(new ComboBoxItem { Content = value });
            combo.SelectedIndex = combo.Items.Count - 1;
        }

        private Settings BuildFromForm() => new()
        {
            Server = ServerBox.Text.Trim(),
            Port = PortBox.Text.Trim(),
            Database = DatabaseBox.Text.Trim(),
            User = UserBox.Text.Trim(),
            Password = PasswordBox.Password,
            Charset = (CharsetCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "WIN1253",
            ApiBaseUrl = ApiUrlBox.Text.Trim(),
            ApiUsername = ApiUserBox.Text.Trim(),
            ApiPassword = ApiPasswordBox.Password,
            ApiStoreId = ApiStoreBox.Text.Trim(),
            PriceListId = (PriceListCombo.SelectedItem as PriceListOption)?.Id ?? 1,
            PriceSource = (PriceSourceCombo.SelectedItem as PriceSourceOption)?.Value ?? PriceSource.Delivery,
            EficStoreCode = (EficConnectionCombo.SelectedItem as EficConnectionOption)?.Code ?? "",
            StockSyncEnabled = StockSyncCheck.IsChecked == true,
            EmailOrderCorrelation = loaded.EmailOrderCorrelation,      // switched on «Αντιστοίχιση παραγγελιών» since 01/10/2026
            TillOrderSyncEnabled = loaded.TillOrderSyncEnabled,      // the home-page switch, kept as it is
            TillOrderSyncSince = loaded.TillOrderSyncSince,
            OrderAcceptanceEnabled = loaded.OrderAcceptanceEnabled,  // the home-page switch: saving here must not turn it off
            AcceptMinutes = loaded.AcceptMinutes,
            SizeMode = (SizeModeCombo.SelectedItem as SizeModeOption)?.Value ?? SizeMode.SeparateItems,
            SyncAllCategories = syncAllCategories,
            SyncCategoryIds = string.Join(",", syncCategoryIds.OrderBy(id => id)),
            CategoryPriceLists = Settings.FormatPriceListMap(categoryPriceLists),
            ShowDashboard = ShowDashboardCheck.IsChecked == true,
            ShowCompare = ShowCompareCheck.IsChecked == true,
            ShowPush = ShowPushCheck.IsChecked == true,
            ShowEfic = ShowEficCheck.IsChecked == true,
            ShowCustomers = ShowCustomersCheck.IsChecked == true,
        };

        private string savedEficCode = "";
        private Settings loaded = new();

        // Uses what is on the form (store, database, «Σύνδεση P2O») - the dialog names the
        // store and every EFIC code before anything is deleted.
        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (License.Blocked(Module.Catalogue, out string why)) { ShowStatus(why, Bad); return; }
            new ResetWindow(BuildFromForm()) { Owner = this }.ShowDialog();
        }

        private class SizeModeOption
        {
            public SizeMode Value { get; init; }
            public string Label { get; init; } = "";
        }

        // Offers ONLY the PointToOrder lines of EFST (one line per platform connection) - Wolt
        // and efood lines are never selectable. A saved code that is not an EFST line (the
        // sandbox stores run on PO1007 / PO1110) stays as its own entry so saving the form does
        // not silently drop it, but it is flagged: real PointToOrder orders will not match it.
        private async Task RefreshEficConnectionsAsync(Settings settings)
        {
            if (!settings.IsDatabaseConfigured) return;

            List<EficRepository.EfstConnection> connections;
            try
            {
                connections = await EficRepository.LoadConnectionsAsync(settings);
            }
            catch (Exception ex)
            {
                EficHintText.Foreground = Warn;
                EficHintText.Text = "Δεν ήταν δυνατή η ανάγνωση των συνδέσεων (EFST): " + ex.Message;
                return;
            }

            string current = (EficConnectionCombo.SelectedItem as EficConnectionOption)?.Code ?? savedEficCode;
            var pointToOrder = connections.Where(c => c.IsPointToOrder).ToList();

            // «Καμία» first: clearing the choice must always be possible, and it is safe -
            // with no code the app writes nothing to EFIC at all.
            var options = new List<EficConnectionOption>
            {
                new() { Code = "", Label = "— καμία (δεν γράφεται τίποτα στο EFIC) —" },
            };
            options.AddRange(pointToOrder.Select(c => new EficConnectionOption
            {
                Code = c.EfstId,
                Label = $"{c.EfstId} — {(c.Name.Length > 0 ? c.Name : "PointToOrder")}" +
                        (c.Remarks.Length > 0 ? $" ({c.Remarks})" : ""),
            }));

            if (current.Length > 0 && !pointToOrder.Any(c => c.EfstId == current))
                options.Add(new EficConnectionOption
                {
                    Code = current,
                    Label = $"{current} — χωρίς γραμμή στο EFST (μόνο για δοκιμές)",
                });

            // The codes P2O orders actually arrived under (last 30 days) - the hint warns when
            // the choice is not one of them. A failure here only loses the warning.
            try
            {
                arrivingCodes = (await EficRepository.LoadOrderFeedAsync(settings)).Arriving
                    .Select(a => a.Code.Trim()).Where(c => c.Length > 0).ToList();
            }
            catch
            {
                arrivingCodes = new List<string>();
            }

            eficConnections = connections;
            EficConnectionCombo.ItemsSource = options;
            EficConnectionCombo.SelectedItem =
                options.FirstOrDefault(o => o.Code == current && current.Length > 0) ??
                (pointToOrder.Count == 1 ? options[1] : options[0]);

            UpdateEficHint();
        }

        private List<EficRepository.EfstConnection>? eficConnections;
        private List<string> arrivingCodes = new();

        private void EficConnectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            UpdateEficHint();

        // Describes what the CURRENT choice will do - so the warning is never out of step with
        // the dropdown.
        private void UpdateEficHint()
        {
            if (eficConnections == null) return;

            string selected = (EficConnectionCombo.SelectedItem as EficConnectionOption)?.Code ?? "";
            var pointToOrder = eficConnections.Where(c => c.IsPointToOrder).ToList();
            string others = string.Join(", ", eficConnections
                .Where(c => !c.IsPointToOrder && c.Name.Length > 0)
                .Select(c => c.Name));
            string noP2O = "Η βάση δεν έχει σύνδεση PointToOrder στο EFST" +
                           (others.Length > 0 ? $" (υπάρχουν μόνο: {others})" : "") + ".";

            // Checked first: the one choice that makes every online order arrive unmatched.
            var foreign = arrivingCodes.Where(c => c != selected).Distinct().ToList();
            if (foreign.Count > 0)
            {
                string codes = string.Join(", ", foreign.Select(c => $"«{c}»"));
                EficHintText.Foreground = Bad;
                EficHintText.Text =
                    $"⛔ Οι παραγγελίες του PointToOrder φτάνουν με κωδικό {codes}" +
                    (selected.Length > 0 ? $", όχι «{selected}»" : "") + ". " +
                    (pointToOrder.Any(c => foreign.Contains(c.EfstId))
                        ? $"Επιλέξτε τη σύνδεση {codes} παραπάνω."
                        : $"Αλλάξτε στο Orexsys τον κωδικό της γραμμής PointToOrder του EFST σε {codes} και πατήστε «Έλεγχος βάσης».") +
                    " Μέχρι τότε οι παραγγελίες δεν ταιριάζουν με τα προϊόντα και ο Συγχρονισμός δεν στέλνει τίποτα.";
                return;
            }

            if (selected.Length == 0)
            {
                EficHintText.Foreground = Warn;
                EficHintText.Text = pointToOrder.Count == 0
                    ? noP2O + " Δημιουργήστε πρώτα τη σύνδεση στο Orexsys — μέχρι τότε η Αποστολή " +
                      "δεν γράφει τίποτα στο EFIC."
                    : "Επιλέξτε τη σύνδεση PointToOrder — χωρίς αυτήν η Αποστολή δεν γράφει τίποτα στο EFIC.";
            }
            else if (!pointToOrder.Any(c => c.EfstId == selected))
            {
                EficHintText.Foreground = Warn;
                EficHintText.Text =
                    $"Ο «{selected}» είναι κωδικός δοκιμών, όχι σύνδεση του EFST: η Αποστολή γράφει κάτω " +
                    "από αυτόν, αλλά οι online παραγγελίες δεν θα ταιριάζουν με τα προϊόντα. " +
                    (pointToOrder.Count == 0
                        ? noP2O + " Για πραγματικό κατάστημα δημιουργήστε πρώτα τη σύνδεση στο Orexsys."
                        : "Για πραγματικό κατάστημα επιλέξτε τη σύνδεση PointToOrder.");
            }
            else
            {
                EficHintText.Foreground = Muted;
                EficHintText.Text =
                    $"Σύνδεση PointToOrder από το EFST." +
                    (others.Length > 0
                        ? $" Οι υπόλοιπες ({others}) δεν εμφανίζονται — το πρόγραμμα δεν τις αγγίζει ποτέ."
                        : "");
            }
        }

        // The category choice lives here until Αποθήκευση, like every other field on the form.
        private bool syncAllCategories = true;
        private HashSet<long> syncCategoryIds = new();
        private Dictionary<long, long> categoryPriceLists = new();   // category → price list, where not the default

        private void ChooseCategoriesButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = BuildFromForm();
            if (!settings.IsDatabaseConfigured)
            {
                ShowStatus("Συμπληρώστε πρώτα τη σύνδεση με τη βάση Orexsys.", Warn);
                return;
            }

            var picker = new CategoryPickerWindow(settings, syncAllCategories, syncCategoryIds, categoryPriceLists)
            {
                Owner = this,
            };
            if (picker.ShowDialog() != true) return;

            syncAllCategories = picker.AllSelected;
            syncCategoryIds = picker.SelectedIds;
            categoryPriceLists = picker.PriceLists;
            UpdateCategorySummary();
        }

        private void UpdateCategorySummary()
        {
            CategorySummaryText.Text =
                syncAllCategories ? "Όλες (και όσες προστεθούν αργότερα)" :
                syncCategoryIds.Count == 0 ? "Καμία — ο Συγχρονισμός δεν στέλνει νέα προϊόντα" :
                $"{syncCategoryIds.Count} επιλεγμένες";
            if (categoryPriceLists.Count > 0)
                CategorySummaryText.Text += $" · {categoryPriceLists.Count} με δικό τους τιμοκατάλογο";
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Επιλογή βάσης Orexsys",
                Filter = "Βάση Firebird (*.fdb)|*.fdb|Όλα τα αρχεία (*.*)|*.*",
            };

            if (dialog.ShowDialog() == true)
                DatabaseBox.Text = dialog.FileName;
        }

        private async void TestDbButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = BuildFromForm();

            if (!settings.IsDatabaseConfigured)
            {
                ShowStatus("Συμπληρώστε διακομιστή και αρχείο βάσης.", Warn);
                return;
            }

            TestDbButton.IsEnabled = false;
            ShowStatus("Έλεγχος σύνδεσης με τη βάση...", Normal);

            try
            {
                int productCount = await Task.Run(() =>
                {
                    using var connection = new FbConnection(settings.ToConnectionString());
                    connection.Open();

                    using var command = new FbCommand("SELECT COUNT(*) FROM ITMS", connection);
                    return Convert.ToInt32(command.ExecuteScalar());
                });

                var lists = await OrexsysCatalog.LoadPriceListsAsync(settings);

                if (lists.Count > 0)
                {
                    long previous = (PriceListCombo.SelectedItem as PriceListOption)?.Id ?? 1;

                    var options = lists
                        .Select(l => new PriceListOption
                        {
                            Id = l.Id,
                            Label = string.IsNullOrWhiteSpace(l.Name) ? $"#{l.Id}" : $"#{l.Id} — {l.Name}",
                        })
                        .ToList();

                    PriceListCombo.ItemsSource = options;
                    PriceListCombo.SelectedItem =
                        options.FirstOrDefault(o => o.Id == previous) ?? options[0];
                }

                await RefreshEficConnectionsAsync(settings);

                ShowStatus($"Επιτυχής σύνδεση. {productCount} προϊόντα, " +
                           $"{lists.Count} τιμοκατάλογος/οι.", Good);
            }
            catch (Exception ex)
            {
                ShowStatus("Η σύνδεση με τη βάση απέτυχε:\n" + ex.Message, Bad);
            }
            finally
            {
                TestDbButton.IsEnabled = true;
            }
        }

        private async void TestApiButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = BuildFromForm();

            if (!settings.IsApiConfigured)
            {
                ShowStatus("Συμπληρώστε διεύθυνση, χρήστη, κωδικό και κωδικό καταστήματος.", Warn);
                return;
            }

            TestApiButton.IsEnabled = false;
            ShowStatus("Έλεγχος σύνδεσης με το PointToOrder...", Normal);

            try
            {
                using var api = new PointToOrderApiClient(settings);
                var user = await api.TestConnectionAsync();

                string stores = user.StoreIds.Count > 0
                    ? string.Join(", ", user.StoreIds)
                    : "(κανένα)";

                // Pointing the app at a store the account cannot see is the single most likely
                // configuration mistake, and it fails later as "nothing matched" rather than as
                // an error, so it is called out here instead.
                bool storeVisible = user.StoreIds.Contains(settings.ApiStoreId);

                if (storeVisible)
                {
                    ShowStatus($"Επιτυχής σύνδεση ως {user.Username}. Καταστήματα: {stores}.", Good);
                }
                else
                {
                    ShowStatus($"Σύνδεση επιτυχής ως {user.Username}, αλλά το κατάστημα " +
                               $"«{settings.ApiStoreId}» δεν είναι στη λίστα: {stores}.", Warn);
                }
            }
            catch (Exception ex)
            {
                ShowStatus("Η σύνδεση με το PointToOrder απέτυχε:\n" + ex.Message, Bad);
            }
            finally
            {
                TestApiButton.IsEnabled = true;
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            Settings saved;
            try
            {
                saved = BuildFromForm();
                Settings.Save(saved);
            }
            catch (Exception ex)
            {
                ShowStatus("Η αποθήκευση απέτυχε:\n" + ex.Message, Bad);
                return;
            }

            // The e-mail correlation is applied now, not left to the next start, so the
            // technician sees at once whether Orexsys accepted the change. It is the on-switch
            // of the «orders» module: without that licence it is saved but NOT applied (and never
            // removed - ΔΙΑΧΕΙΡΙΣΗ may rely on the same patch).
            if (saved.EmailOrderCorrelation && saved.IsDatabaseConfigured && License.Blocked(Module.Orders, out string why))
            {
                MessageBox.Show("Οι ρυθμίσεις αποθηκεύτηκαν, αλλά η αντιστοίχιση με e-mail ΔΕΝ εφαρμόστηκε στο Orexsys:\n\n" + why,
                    "Ρυθμίσεις", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (saved.EmailOrderCorrelation && saved.IsDatabaseConfigured)
            {
                SaveButton.IsEnabled = false;
                ShowStatus("Εφαρμογή της αντιστοίχισης με e-mail στο Orexsys...", Normal);
                var result = await Task.Run(() => EfomTriggerGuard.Run(saved, BackupFolder));
                SaveButton.IsEnabled = true;

                if (result.Outcome == EfomTriggerGuard.Outcome.Patched)
                    MessageBox.Show("Η αντιστοίχιση παραγγελιών με e-mail εφαρμόστηκε στο Orexsys.\n\n" +
                                    $"Αντίγραφο του αρχικού trigger: {result.BackupPath}",
                                    "Ρυθμίσεις", MessageBoxButton.OK, MessageBoxImage.Information);
                else if (result.IsProblem)
                {
                    MessageBox.Show("Οι ρυθμίσεις αποθηκεύτηκαν, αλλά η αντιστοίχιση με e-mail ΔΕΝ εφαρμόστηκε:\n\n" +
                                    result.Message, "Ρυθμίσεις", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;   // stay open so the problem is visible
                }
            }

            DialogResult = true;
            Close();
        }

        private static string BackupFolder => System.IO.Path.Combine(AppContext.BaseDirectory, "backups");


        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

        // ---- «Άδεια χρήσης» (docs/LICENSING_AND_UPDATES.md, phase 1) ---------------------------

        private void ShowLicence()
        {
            FingerprintBox.Text = License.Fingerprint;
            var t = License.Current;
            if (t == null)
            {
                LicenceStateText.Text = "Χωρίς άδεια χρήσης" + (License.Problem.Length > 0 ? " — " + License.Problem : "") +
                                        " Το πρόγραμμα μόνο δείχνει μέχρι να ενεργοποιηθεί άδεια.";
                LicenceStateText.Foreground = Bad;
                return;
            }
            string modules = string.Join(", ", new[] { Module.Catalogue, Module.Orders }.Where(t.Has).Select(License.Label));
            string state = License.Worst() switch
            {
                LicenseState.Licensed => "ενεργή",
                LicenseState.Grace => "έληξε — περίοδος χάριτος " + t.GraceDays + " ημερών",
                _ => "ΕΛΗΞΕ",
            };
            LicenceStateText.Text = $"{t.Customer} · {t.Licence} · {modules} · έως {t.LicenceUntil:dd/MM/yyyy} · {state}" +
                                    (t.Store is long s ? $" · κατάστημα {s}" : "");
            LicenceStateText.Foreground = License.Worst() switch { LicenseState.Licensed => Good, LicenseState.Grace => Warn, _ => Bad };
        }

        private void CopyFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(License.Fingerprint); ShowStatus("Το αποτύπωμα αντιγράφηκε.", Good); }
            catch (Exception ex) { ShowStatus("Η αντιγραφή απέτυχε: " + ex.Message, Bad); }
        }

        private void InstallLicenceButton_Click(object sender, RoutedEventArgs e)
        {
            string token = LicenceBox.Text.Trim();
            if (token.Length == 0) { ShowStatus("Επικολλήστε πρώτα το κείμενο της άδειας.", Warn); return; }
            try
            {
                var result = License.Install(token);
                if (!result.Ok) { ShowStatus("Η άδεια δεν έγινε δεκτή: " + result.Problem, Bad); return; }
                LicenceBox.Clear();
                ShowLicence();
                ShowStatus($"Η άδεια ενεργοποιήθηκε: {result.Token!.Customer}, έως {result.Token.LicenceUntil:dd/MM/yyyy}.", Good);
            }
            catch (Exception ex)
            {
                ShowStatus("Η άδεια δεν αποθηκεύτηκε: " + ex.Message, Bad);
            }
        }

        private static readonly Brush Normal = Brushes.Black;
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x5E, 0x6E, 0x72));
        private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x4D));
        private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xA8, 0x64, 0x00));
        private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xB9, 0x3A, 0x2E));

        private void ShowStatus(string message, Brush colour)
        {
            StatusText.Text = message;
            StatusText.Foreground = colour;
        }
    }
}
