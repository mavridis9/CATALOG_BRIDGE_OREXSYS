using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CatalogBridge
{
    // The EFIC correlation editor: every platform↔Orexsys mapping in one grid, with the three
    // repair operations - delete a wrong row, point a row at a different Orexsys product, and
    // map a still-unmatched platform product. All three lean on Orexsys's own EFIC_AIUD_TRG,
    // which backfills unmapped EFOD order lines whenever a correlation is inserted or fixed.
    public partial class EficView : UserControl
    {
        private List<EficRow> rows = new();
        private List<CatalogOffer> unmappedOffers = new();
        private List<OrexsysProduct> orexsysProducts = new();
        private string storeCode = "";

        public EficView()
        {
            InitializeComponent();
        }

        private async void LoadButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private async Task LoadAsync()
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
                ShowStatus("Συμπληρώστε πρώτα τις Ρυθμίσεις (βάση Orexsys και PointToOrder).", Warn);
                return;
            }

            SetBusy(true);
            ShowStatus("Φόρτωση αντιστοιχίσεων και καταλόγου...", Normal);

            try
            {
                rows = await EficRepository.LoadAsync(settings);

                // Correlations point at real products only - extras (ITMC.ISEXTRA=1) are
                // modifiers on another item's line, and none of the 435 genuine EFIC rows
                // targets one, so the pickers never offer them.
                var orexsys = await OrexsysCatalog.LoadAsync(settings);

                using var api = new PointToOrderApiClient(settings);
                var platform = await api.GetCatalogAsync();

                // Only the categories chosen in Ρυθμίσεις (CategoryScope): the rows whose
                // product is in them (a row pointing at no product is a fault and stays
                // visible), the pickers' products, and the unlinked PointToOrder products of
                // the matching PointToOrder categories.
                var scopedPlatformCategories = CategoryScope.PlatformCategories(orexsys, platform, settings);
                bool filtered = CategoryScope.IsFiltered(settings);
                if (filtered)
                    rows = rows.Where(r => r.ItmsId == 0 || settings.IncludesCategory(r.CategoryId)).ToList();
                orexsysProducts = orexsys.Products
                    .Where(p => !p.IsExtra && CategoryScope.Includes(p, settings)).ToList();

                // Validated: empty if the configured code belongs to Wolt / efood.
                storeCode = await EficRepository.LoadStoreCodeAsync(settings);

                // Annotate each correlation with what the platform actually holds under that
                // code today - and whether the pairing still looks like the same product.
                var offersById = new Dictionary<string, CatalogOffer>();
                foreach (var offer in platform.Offers)
                    offersById[offer.OfferId.ToString()] = offer;

                foreach (var row in rows)
                {
                    if (offersById.TryGetValue(row.Code, out var offer))
                    {
                        row.FoundOnPlatform = true;
                        row.PlatformName = offer.Name;
                    }
                }

                var mappedCodes = rows.Select(r => r.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
                unmappedOffers = platform.Offers
                    .Where(o => !mappedCodes.Contains(o.OfferId.ToString()))
                    .Where(o => !filtered || scopedPlatformCategories.Contains(o.CategoryId))
                    .OrderBy(o => o.CategoryName).ThenBy(o => o.Name)
                    .ToList();

                ApplyFilter();
                UnmappedGrid.ItemsSource = unmappedOffers;

                int onExtras = rows.Count(r => r.TargetsExtra);
                int orphaned = rows.Count(r => !r.FoundOnPlatform);

                var message = new StringBuilder();
                message.Append($"{rows.Count} αντιστοιχίσεις EFIC · {onExtras} σε extra · " +
                               $"{orphaned} ορφανές (δεν υπάρχουν πλέον στο PointToOrder) · " +
                               $"{unmappedOffers.Count} προϊόντα PointToOrder χωρίς αντιστοίχιση." +
                               (filtered ? " " + CategoryScope.Note(settings) : ""));

                ShowStatus(message.ToString(), onExtras + orphaned > 0 ? Warn : Good);

                RepairButton.IsEnabled = true;
                DeleteButton.IsEnabled = true;
                MapButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                ShowStatus("Η φόρτωση απέτυχε:\n" + ex.Message, Bad);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            if (SearchBox == null || EficGrid == null) return;

            string search = CatalogDiff.Normalise(SearchBox.Text);
            int filter = FilterCombo.SelectedIndex;

            IEnumerable<EficRow> filtered = rows;
            if (filter == 1) filtered = filtered.Where(r => r.TargetsExtra);
            if (filter == 2) filtered = filtered.Where(r => !r.FoundOnPlatform);

            if (search.Length > 0)
                filtered = filtered.Where(r =>
                    CatalogDiff.Normalise(r.OrexsysName).Contains(search) ||
                    CatalogDiff.Normalise(r.PlatformName).Contains(search) ||
                    r.Code.Contains(search, StringComparison.OrdinalIgnoreCase));

            EficGrid.ItemsSource = filtered.ToList();
        }

        private async void RepairButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = EficGrid.SelectedItems.OfType<EficRow>().ToList();
            if (selected.Count != 1)
            {
                ShowStatus("Επιλέξτε ακριβώς μία αντιστοίχιση για διόρθωση.", Warn);
                return;
            }

            var row = selected[0];
            string platformLabel = row.FoundOnPlatform ? row.PlatformName : $"κωδ. {row.Code}";

            var picker = new ProductPickerWindow(orexsysProducts,
                $"Σε ποιο προϊόν Orexsys αντιστοιχεί το «{platformLabel}»;")
            {
                Owner = Window.GetWindow(this),
            };

            if (picker.ShowDialog() != true || picker.SelectedProduct == null) return;

            var product = picker.SelectedProduct;

            SetBusy(true);
            try
            {
                // The size stays as it was: re-pointing fixes the product, not the size.
                await EficRepository.RepairAsync(Settings.Load(), row.EficId, product.ItmsId, row.ItmvId);

                row.ItmsId = product.ItmsId;
                row.OrexsysCode = product.Code;
                row.OrexsysName = product.Name;
                // The picker only offers non-extras, so a repaired row is clean by definition.
                row.TargetsExtra = false;

                ApplyFilter();
                ShowStatus($"Η αντιστοίχιση «{platformLabel}» δείχνει πλέον στο «{product.Name}». " +
                           "Τυχόν εκκρεμείς γραμμές παραγγελιών διορθώθηκαν αυτόματα από το Orexsys.",
                    Good);
            }
            catch (Exception ex)
            {
                ShowStatus("Η διόρθωση απέτυχε:\n" + ex.Message, Bad);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = EficGrid.SelectedItems.OfType<EficRow>().ToList();
            if (selected.Count == 0)
            {
                ShowStatus("Επιλέξτε πρώτα μία ή περισσότερες αντιστοιχίσεις.", Warn);
                return;
            }

            var message = new StringBuilder();
            message.AppendLine(selected.Count == 1
                ? "Θα διαγραφεί η αντιστοίχιση:"
                : $"Θα διαγραφούν {selected.Count} αντιστοιχίσεις:");
            foreach (var row in selected.Take(10))
                message.AppendLine($"   • {row.PlatformText} ↔ {row.OrexsysName}");
            if (selected.Count > 10) message.AppendLine($"   ... και άλλες {selected.Count - 10}");
            int orphanCount = selected.Count(r => !r.FoundOnPlatform);

            message.AppendLine();
            message.AppendLine("Δεν διαγράφεται κανένα προϊόν — ούτε στο Orexsys ούτε στο PointToOrder.");
            if (orphanCount > 0)
                message.AppendLine($"Οι {orphanCount} ορφανές: τα προϊόντα Orexsys θα εμφανιστούν " +
                                   "ξανά στην «Αποστολή» ώστε να ξανασταλούν.");
            if (orphanCount < selected.Count)
                message.AppendLine("Οι υπόλοιπες: μέχρι να ξαναγίνει αντιστοίχιση, οι online " +
                                   "παραγγελίες τους θα φτάνουν χωρίς αυτόματη σύνδεση με το Orexsys.");
            message.Append("Να συνεχίσω;");

            var confirm = MessageBox.Show(message.ToString(), "Διαγραφή αντιστοιχίσεων",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;

            SetBusy(true);
            try
            {
                await EficRepository.DeleteAsync(
                    Settings.Load(), selected.Select(r => r.EficId).ToList());

                var deletedIds = selected.Select(r => r.EficId).ToHashSet();
                rows.RemoveAll(r => deletedIds.Contains(r.EficId));

                ApplyFilter();
                ShowStatus($"Διαγράφηκαν {selected.Count} αντιστοιχίσεις." +
                           (orphanCount > 0
                               ? $" Τα {orphanCount} προϊόντα των ορφανών είναι ξανά διαθέσιμα " +
                                 "στην «Αποστολή» (πατήστε εκεί «Φόρτωση»)."
                               : ""),
                    Good);
            }
            catch (Exception ex)
            {
                ShowStatus("Η διαγραφή απέτυχε:\n" + ex.Message, Bad);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void MapButton_Click(object sender, RoutedEventArgs e)
        {
            if (UnmappedGrid.SelectedItem is not CatalogOffer offer)
            {
                ShowStatus("Επιλέξτε πρώτα ένα προϊόν PointToOrder από την κάτω λίστα.", Warn);
                return;
            }

            if (storeCode.Length == 0)
            {
                ShowStatus("Δεν υπάρχει κωδικός EFIC. Ορίστε το «EficStoreCode» στις Ρυθμίσεις " +
                           "(π.χ. PO11521).", Warn);
                return;
            }

            var picker = new ProductPickerWindow(orexsysProducts,
                $"Σε ποιο προϊόν Orexsys αντιστοιχεί το «{offer.Name}» ({offer.Price:0.00});")
            {
                Owner = Window.GetWindow(this),
            };

            if (picker.ShowDialog() != true || picker.SelectedProduct == null) return;

            var product = picker.SelectedProduct;

            SetBusy(true);
            try
            {
                long eficId = await EficRepository.InsertAsync(
                    Settings.Load(), storeCode, offer.OfferId, offer.Name, product.ItmsId);

                rows.Add(new EficRow
                {
                    EficId = eficId,
                    StoreId = storeCode,
                    Code = offer.OfferId.ToString(),
                    Name = offer.Name,
                    ItmsId = product.ItmsId,
                    OrexsysCode = product.Code,
                    OrexsysName = product.Name,
                    PlatformName = offer.Name,
                    FoundOnPlatform = true,
                });

                unmappedOffers.Remove(offer);
                UnmappedGrid.ItemsSource = null;
                UnmappedGrid.ItemsSource = unmappedOffers;

                ApplyFilter();
                ShowStatus($"Το «{offer.Name}» αντιστοιχίστηκε στο «{product.Name}». " +
                           "Τυχόν εκκρεμείς γραμμές παραγγελιών διορθώθηκαν αυτόματα από το Orexsys.",
                    Good);
            }
            catch (Exception ex)
            {
                ShowStatus("Η αντιστοίχιση απέτυχε:\n" + ex.Message, Bad);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            LoadButton.IsEnabled = !busy;
            bool loaded = rows.Count > 0 || unmappedOffers.Count > 0;
            RepairButton.IsEnabled = !busy && loaded;
            DeleteButton.IsEnabled = !busy && loaded;
            MapButton.IsEnabled = !busy && loaded;
            EficGrid.IsEnabled = !busy;
            UnmappedGrid.IsEnabled = !busy;
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
