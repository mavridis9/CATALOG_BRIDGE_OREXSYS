using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace CatalogBridge
{
    // The full diff between the Orexsys product master and the PointToOrder catalog, with
    // bulk activate/deactivate and CSV export. Extras (ITMC.ISEXTRA=1) never participate -
    // see CatalogDiff for the matching rules.
    public partial class CompareView : UserControl
    {
        private class FilterOption
        {
            public string Label { get; init; } = "";

            // Null means "everything"; otherwise only rows with this status are shown.
            public DiffStatus? Status { get; init; }

            public override string ToString() => Label;
        }

        private DiffResult? diff;
        private OrexsysCatalogSnapshot? orexsysSnapshot;
        private CatalogSnapshot? platformSnapshot;

        public CompareView()
        {
            InitializeComponent();
            BuildFilterOptions();
        }

        private void BuildFilterOptions()
        {
            FilterCombo.ItemsSource = new List<FilterOption>
            {
                new() { Label = "Όλα" },
                new() { Label = "Διαφορά κατάστασης", Status = DiffStatus.ActiveMismatch },
                new() { Label = "Κρυφά online", Status = DiffStatus.HiddenOnline },
                new() { Label = "Μόνο στο Orexsys", Status = DiffStatus.OrexsysOnly },
                new() { Label = "Μόνο στο PointToOrder", Status = DiffStatus.PlatformOnly },
                new() { Label = "Συμφωνούν", Status = DiffStatus.Identical },
            };
            FilterCombo.SelectedIndex = 0;
        }

        private async void CompareButton_Click(object sender, RoutedEventArgs e) => await CompareAsync();

        private async Task CompareAsync()
        {
            Settings settings;
            try
            {
                settings = Settings.Load();
            }
            catch (Exception ex)
            {
                ShowStatus("Δεν ήταν δυνατή η ανάγνωση των ρυθμίσεων:\n" + ex.Message, Bad);
                return;
            }

            if (!settings.IsDatabaseConfigured || !settings.IsApiConfigured)
            {
                ShowStatus("Συμπληρώστε πρώτα τις Ρυθμίσεις (κάτω αριστερά).", Warn);
                return;
            }

            SetBusy(true);
            ShowStatus("Ανάγνωση προϊόντων από το Orexsys...", Normal);

            try
            {
                orexsysSnapshot = await OrexsysCatalog.LoadAsync(settings);

                ShowStatus($"Διαβάστηκαν {orexsysSnapshot.Products.Count} προϊόντα. " +
                           "Ανάγνωση καταλόγου PointToOrder...", Normal);

                using var api = new PointToOrderApiClient(settings);
                platformSnapshot = await api.GetCatalogAsync();

                diff = CatalogDiff.Build(orexsysSnapshot, platformSnapshot, settings);

                ApplyFilter();
                UpdateCounters();

                ExportButton.IsEnabled = true;
                ActivateButton.IsEnabled = true;
                DeactivateButton.IsEnabled = true;

                ReportMatchQuality(settings);
            }
            catch (Exception ex)
            {
                ShowStatus("Η σύγκριση απέτυχε:\n" + ex.Message, Bad);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // The whole comparison rests on EFIC.CODE lining up with the platform's offerIds, so
        // the match rate is reported every run: a run that matched nothing by code was compared
        // on names alone, and every number on screen should be read as a hint, not a fact.
        private void ReportMatchQuality(Settings settings)
        {
            if (diff == null || orexsysSnapshot == null || platformSnapshot == null) return;

            var message = new StringBuilder();
            message.Append($"{orexsysSnapshot.Products.Count} προϊόντα Orexsys · ");
            message.Append($"{platformSnapshot.Offers.Count} προϊόντα PointToOrder · ");
            message.Append($"ταίριασμα: {diff.MatchedByCode} με κωδικό, {diff.MatchedByName} με όνομα. ");

            Brush colour = Normal;

            if (diff.MatchedByCode == 0 && diff.MatchedByName > 0)
            {
                message.Append("\n⚠ Κανένα προϊόν δεν ταίριαξε με κωδικό — η σύγκριση έγινε μόνο με " +
                               "ονόματα, οπότε ελέγξτε κάθε διαφορά πριν αλλάξετε τιμή.");
                colour = Warn;
            }
            else if (diff.MatchedByCode == 0)
            {
                message.Append("\n⚠ Κανένα προϊόν δεν ταίριαξε. Ελέγξτε ότι το κατάστημα στις " +
                               "Ρυθμίσεις είναι αυτό που αντιστοιχεί στη βάση Orexsys.");
                colour = Bad;
            }

            ShowStatus(message.ToString(), colour);
        }

        private void UpdateCounters()
        {
            if (diff == null) return;

            OkPill.Text = $"Συμφωνούν: {diff.CountOf(DiffStatus.Identical)}";
            PricePill.Text = $"Κρυφά online: {diff.CountOf(DiffStatus.HiddenOnline)}";
            MissingPill.Text =
                $"Μόνο POS: {diff.CountOf(DiffStatus.OrexsysOnly)} · " +
                $"Μόνο online: {diff.CountOf(DiffStatus.PlatformOnly)}";
        }

        private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            if (diff == null || SearchBox == null) return;

            var option = FilterCombo.SelectedItem as FilterOption;
            string search = CatalogDiff.Normalise(SearchBox.Text);

            IEnumerable<DiffRow> rows = diff.Rows;

            if (option?.Status is DiffStatus status)
                rows = rows.Where(r => r.Status == status);

            if (search.Length > 0)
                rows = rows.Where(r =>
                    CatalogDiff.Normalise(r.OrexsysName).Contains(search) ||
                    CatalogDiff.Normalise(r.PlatformName).Contains(search) ||
                    CatalogDiff.Normalise(r.OrexsysCode).Contains(search));

            var list = rows.ToList();
            ResultsGrid.ItemsSource = list;
            CountText.Text = $"{list.Count} από {diff.Rows.Count} γραμμές";
        }

        private async void ActivateButton_Click(object sender, RoutedEventArgs e) =>
            await ApplyStatusAsync(OfferItemType.Orderable, sellable: true, "ενεργοποιήθηκαν");

        private async void DeactivateButton_Click(object sender, RoutedEventArgs e) =>
            await ApplyStatusAsync(OfferItemType.Inactive, sellable: false, "απενεργοποιήθηκαν");

        // Activate/deactivate move BOTH settings together: on its own, Inactive would leave the
        // product still listed in the menu (just unselectable), which is not what staff mean by
        // "switch it off".
        private async Task ApplyStatusAsync(string itemType, bool sellable, string verbPastPlural)
        {
            if (diff == null) return;

            var selected = ResultsGrid.SelectedItems.OfType<DiffRow>()
                .Where(r => r.Offer != null)
                .ToList();

            if (selected.Count == 0)
            {
                ShowStatus("Επιλέξτε πρώτα γραμμές που υπάρχουν στο PointToOrder.", Warn);
                return;
            }

            // Anything already in the requested state is left out of the call entirely.
            var toChange = selected
                .Where(r => !string.Equals(r.Offer!.ItemType, itemType, StringComparison.OrdinalIgnoreCase)
                            || r.Offer.Sellable != sellable)
                .ToList();

            if (toChange.Count == 0)
            {
                ShowStatus("Τα επιλεγμένα προϊόντα είναι ήδη σε αυτή την κατάσταση.", Warn);
                return;
            }

            var confirm = MessageBox.Show(
                $"Θα αλλάξει η κατάσταση {toChange.Count} προϊόντων στο PointToOrder.\n\n" +
                "Η αλλαγή είναι άμεση και ορατή στους πελάτες. Να συνεχίσω;",
                "Επιβεβαίωση", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes) return;

            SetBusy(true);
            ShowStatus($"Αποστολή αλλαγών για {toChange.Count} προϊόν(τα)...", Normal);

            try
            {
                var settings = Settings.Load();
                using var api = new PointToOrderApiClient(settings);

                var offerIds = toChange.Select(r => r.Offer!.OfferId).ToList();
                await api.SetOfferStatusAsync(diff.CatalogId, offerIds, itemType, sellable);

                // Update the local copy of the changed offers, then rebuild the diff from the
                // refreshed snapshots so the grid and counters cannot drift apart.
                foreach (var row in toChange)
                {
                    row.Offer!.ItemType = itemType;
                    row.Offer.Sellable = sellable;
                }

                diff = CatalogDiff.Build(orexsysSnapshot!, platformSnapshot!, settings);

                ApplyFilter();
                UpdateCounters();
                ShowStatus($"{toChange.Count} προϊόν(τα) {verbPastPlural}.", Good);
            }
            catch (Exception ex)
            {
                ShowStatus("Η αλλαγή απέτυχε:\n" + ex.Message, Bad);
            }
            finally
            {
                SetBusy(false);
            }
        }


        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (diff == null) return;

            var dialog = new SaveFileDialog
            {
                Title = "Εξαγωγή σύγκρισης",
                Filter = "Αρχείο CSV (*.csv)|*.csv",
                FileName = $"catalog-diff-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                var builder = new StringBuilder();
                builder.AppendLine("Κατάσταση;Κατηγορία;Κωδικός;Orexsys;PointToOrder;" +
                                   "ΤιμήPOS;ΤιμήOnline;POS;Online;Ταίριασμα");

                foreach (var row in ResultsGrid.ItemsSource.OfType<DiffRow>())
                {
                    builder.AppendLine(string.Join(";", new[]
                    {
                        row.StatusText, row.Category, row.OrexsysCode, row.OrexsysName,
                        row.PlatformName, row.OrexsysPriceText, row.PlatformPriceText,
                        row.OrexsysStateText, row.PlatformStateText,
                        row.MatchedByText,
                    }.Select(Escape)));
                }

                // UTF-8 with BOM so Excel opens the Greek text correctly instead of as mojibake.
                File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
                ShowStatus("Η εξαγωγή ολοκληρώθηκε: " + dialog.FileName, Good);
            }
            catch (Exception ex)
            {
                ShowStatus("Η εξαγωγή απέτυχε:\n" + ex.Message, Bad);
            }

            static string Escape(string value) =>
                value.Contains(';') || value.Contains('"')
                    ? "\"" + value.Replace("\"", "\"\"") + "\""
                    : value;
        }

        private void SetBusy(bool busy)
        {
            CompareButton.IsEnabled = !busy;
            ResultsGrid.IsEnabled = !busy;
            Cursor = busy ? System.Windows.Input.Cursors.Wait : null;

            bool loaded = diff != null;
            ExportButton.IsEnabled = !busy && loaded;
            ActivateButton.IsEnabled = !busy && loaded;
            DeactivateButton.IsEnabled = !busy && loaded;
        }

        private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x16, 0x80, 0x3C));
        private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
        private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));

        private void ShowStatus(string message, Brush colour)
        {
            StatusText.Text = message;
            StatusText.Foreground = colour;
        }
    }
}
