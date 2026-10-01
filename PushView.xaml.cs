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
    // The "new installation" tab: Orexsys products that have no EFIC correlation yet, pushed to
    // the PointToOrder catalog. For each selected product this creates the platform category if
    // needed, creates the offer, and writes the EFIC row linking the two - the same chain the
    // stock Orexsys integration expects, so external orders for the new products correlate
    // automatically from then on.
    public partial class PushView : UserControl
    {
        public class PushItem
        {
            public OrexsysProduct Product { get; init; } = null!;
            public string CategoryName => Product.CategoryName;
            public string Code => Product.Code;
            public string Name => Product.Name;
            public string PriceText => Product.Price is decimal p ? p.ToString("0.00") : "—";
            public string StateText => Product.IsActive ? "Ενεργό" : "Ανενεργό";

            // What is still to be sent for this product (PushRules): with sizes as separate
            // items each pending size with its price; with an option group the choices.
            public List<PushUnit> Units { get; init; } = new();
            public string SizesText =>
                !Product.HasSizes ? "" :
                Units.Count == 1 && Units[0].Options.Count > 0
                    ? "επιλογές: " + string.Join(" · ", Units[0].Options.Select(o => $"{o.Name} +{o.Surcharge:0.00}"))
                    : string.Join(" · ", Units.Select(u => $"{u.SizeName} {u.Price:0.00}"));
        }

        private List<PushItem> unmapped = new();
        private CatalogSnapshot? platform;
        private string storeCode = "";
        private string feedMismatch = "";   // OrderFeedCheck message; non-empty blocks sending

        public PushView()
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
            ShowStatus("Ανάγνωση Orexsys και καταλόγου PointToOrder...", Normal);

            try
            {
                var orexsys = await OrexsysCatalog.LoadAsync(settings);

                using var api = new PointToOrderApiClient(settings);
                platform = await api.GetCatalogAsync();

                // Validated: empty if the configured code belongs to Wolt / efood.
                storeCode = await EficRepository.LoadStoreCodeAsync(settings);

                // Orders arriving under another code than ours can never match (OrderFeedCheck).
                feedMismatch = (await EficRepository.CheckOrderFeedAsync(settings)).Message;

                // Links to offers deleted on PointToOrder count as no link (PushRules).
                int orphaned = PushRules.DropOrphanLinks(orexsys, platform);

                // Extras (ITMC.ISEXTRA=1) are excluded outright: they are modifiers riding on
                // another item's line, not products, and must never become platform offers.
                int extrasExcluded = orexsys.Products
                    .Count(p => p.ExternalCodes.Count == 0 && p.IsExtra);

                // Everything with something still to create on PointToOrder (PushRules): a
                // product with no link, or a size of a product that has no link yet.
                unmapped = orexsys.Products
                    .Select(p => new PushItem { Product = p, Units = PushRules.PendingUnits(p, settings) })
                    .Where(i => i.Units.Count > 0)
                    .ToList();

                ApplyFilter();

                var message = new StringBuilder();
                message.Append($"{unmapped.Count} προϊόντα Orexsys χωρίς αντιστοίχιση EFIC " +
                               $"({extrasExcluded} extras εξαιρέθηκαν). ");
                message.Append($"Κατάλογος PointToOrder: {platform.Offers.Count} προϊόντα, " +
                               $"{platform.Categories.Count} κατηγορίες. ");
                if (orphaned > 0)
                    message.Append($"♻ {orphaned} προϊόντα είχαν διαγραφεί από το PointToOrder — " +
                                   "εμφανίζονται ξανά· η παλιά αντιστοίχιση καθαρίζεται στην αποστολή. ");
                message.Append(storeCode.Length > 0
                    ? $"Κωδικός EFIC: {storeCode}."
                    : "⚠ Δεν βρέθηκε κωδικός EFIC (κενό EFST) — ορίστε το «EficStoreCode» στις Ρυθμίσεις πριν την αποστολή.");

                if (feedMismatch.Length > 0)
                    ShowStatus(feedMismatch + "\n" + message, Bad);
                else
                    ShowStatus(message.ToString(), storeCode.Length > 0 ? Normal : Warn);

                PushButton.IsEnabled = true;
                SelectAllButton.IsEnabled = true;
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

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            // The checkboxes raise Checked while the XAML is still being parsed, before their
            // sibling elements exist - crashed the whole window at startup until guarded.
            if (SearchBox == null || OnlyPricedCheck == null || OnlyActiveCheck == null ||
                ProductsGrid == null)
                return;

            string search = CatalogDiff.Normalise(SearchBox.Text);
            bool onlyPriced = OnlyPricedCheck.IsChecked == true;
            bool onlyActive = OnlyActiveCheck.IsChecked == true;

            IEnumerable<PushItem> items = unmapped;

            if (onlyPriced) items = items.Where(i => i.Product.HasPrice);
            if (onlyActive) items = items.Where(i => i.Product.IsActive);
            if (search.Length > 0)
                items = items.Where(i =>
                    CatalogDiff.Normalise(i.Name).Contains(search) ||
                    CatalogDiff.Normalise(i.Code).Contains(search) ||
                    CatalogDiff.Normalise(i.CategoryName).Contains(search));

            ProductsGrid.ItemsSource = items.ToList();
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            ProductsGrid.SelectAll();
            ProductsGrid.Focus();
        }

        private async void PushButton_Click(object sender, RoutedEventArgs e)
        {
            if (platform == null) return;
            if (License.Blocked(Module.Catalogue, out string why)) { ShowStatus(why, Bad); return; }

            var selectedItems = ProductsGrid.SelectedItems.OfType<PushItem>()
                .Where(i => i.Units.Count > 0).ToList();
            var selected = selectedItems.Select(i => i.Product).ToList();

            if (selected.Count == 0)
            {
                ShowStatus("Επιλέξτε πρώτα ένα ή περισσότερα προϊόντα.", Warn);
                return;
            }

            if (feedMismatch.Length > 0)
            {
                ShowStatus(feedMismatch, Bad);
                return;
            }

            if (storeCode.Length == 0)
            {
                ShowStatus("Δεν υπάρχει κωδικός EFIC. Ορίστε το «EficStoreCode» στις Ρυθμίσεις " +
                           "(π.χ. PO11521) — σε νέα εγκατάσταση δεν υπάρχει πουθενά στη βάση.", Warn);
                return;
            }

            // Platform categories the push can reuse, keyed by normalised name; anything not
            // found here gets created on the fly.
            var categoriesByName = new Dictionary<string, long>();
            foreach (var category in platform.Categories)
            {
                string key = CatalogDiff.Normalise(category.Name);
                if (key.Length > 0 && !categoriesByName.ContainsKey(key))
                    categoriesByName[key] = category.CategoryId;
            }

            bool makeVisible = MakeVisibleCheck.IsChecked == true;

            // The review step: PointToOrder is what the customer reads, so every product name
            // is editable before it is sent, and every category about to be created asks for
            // its PointToOrder name (e.g. «OREXSYS»). Nothing is created until «Αποστολή» there.
            // One plan item per unit: the product, or each of its sizes as a separate item.
            var plans = selectedItems
                .SelectMany(i => i.Units)
                .Select(PushPlanItem.From)
                .ToList();

            var categoryPlans = selected
                .Select(p => p.CategoryName.Length > 0 ? p.CategoryName : "ΔΙΑΦΟΡΑ")
                .Distinct()
                .Where(c => !categoriesByName.ContainsKey(CatalogDiff.Normalise(c)))
                .Select(c => new CategoryPlan { OrexsysName = c, TargetName = c })
                .ToList();

            var preview = new PushPreviewWindow(plans, categoryPlans, makeVisible)
            {
                Owner = Window.GetWindow(this),
            };
            if (preview.ShowDialog() != true) return;

            // The chosen target name per Orexsys category, for the loop below.
            var targetCategoryNames = categoryPlans.ToDictionary(
                c => CatalogDiff.Normalise(c.OrexsysName), c => c.FinalName);

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

            SetBusy(true);

            int created = 0;
            var failures = new List<string>();
            var eficFailures = new List<string>();
            var createdOfferIds = new List<long>();
            var orphansCleaned = new HashSet<long>();

            try
            {
                using var api = new PointToOrderApiClient(settings);

                foreach (var plan in plans)
                {
                    var product = plan.Product;
                    string displayName = plan.FinalName;

                    ShowStatus($"Αποστολή {created + failures.Count + 1}/{plans.Count}: " +
                               $"{displayName}...", Normal);

                    try
                    {
                        // Dead EFIC rows of this product go first, once per product.
                        if (product.OrphanCodes.Count > 0 && orphansCleaned.Add(product.ItmsId))
                            await EficRepository.DeleteOrphanLinksAsync(
                                settings, product.ItmsId, product.OrphanCodes);

                        // The category goes to PointToOrder under the name chosen in the
                        // review step, not necessarily the Orexsys one.
                        string orexsysCategory =
                            product.CategoryName.Length > 0 ? product.CategoryName : "ΔΙΑΦΟΡΑ";
                        string targetCategory = targetCategoryNames.TryGetValue(
                            CatalogDiff.Normalise(orexsysCategory), out var chosen)
                            ? chosen : orexsysCategory;

                        long categoryId = await EnsureCategoryAsync(
                            api, categoriesByName, targetCategory);

                        var offer = await api.CreateOfferAsync(
                            platform.CatalogId, categoryId, displayName, plan.Price);

                        if (offer.Id == 0)
                            throw new PointToOrderException(
                                "Η απάντηση δεν περιείχε offerId — ελέγξτε το κατάστημα με το χέρι.");

                        createdOfferIds.Add(offer.Id);

                        // Size option group («ΕΠΙΛΕΞΤΕ ΜΕΓΕΘΟΣ») under the offer; its options
                        // are offers too and are made visible along with the parent.
                        if (plan.Options.Count > 0)
                        {
                            try
                            {
                                createdOfferIds.AddRange(await api.CreateOptionGroupAsync(
                                    platform.CatalogId, offer.Id, PushRules.SizeGroupName,
                                    plan.Options.Select(o => (o.Name, o.Surcharge)).ToList()));
                            }
                            catch (Exception ex)
                            {
                                failures.Add($"{displayName}: το προϊόν δημιουργήθηκε αλλά όχι οι " +
                                             $"επιλογές μεγέθους — {ex.Message}");
                            }
                        }

                        // The platform product now exists; if the EFIC insert fails the two
                        // systems are out of step, which is worse than a failed create - so it
                        // is tracked separately and reported loudly. EFIC.NAME records the
                        // PLATFORM name, matching how the live rows are kept.
                        try
                        {
                            await EficRepository.InsertAsync(
                                settings, storeCode, offer.Id, displayName, product.ItmsId,
                                plan.ItmvId);
                        }
                        catch (Exception ex)
                        {
                            eficFailures.Add($"{displayName} (offerId {offer.Id}): {ex.Message}");
                        }

                        created++;
                        unmapped.RemoveAll(i => i.Product.ItmsId == product.ItmsId);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{displayName}: {ex.Message}");
                    }
                }

                if (makeVisible && createdOfferIds.Count > 0)
                {
                    ShowStatus("Τα νέα προϊόντα γίνονται ορατά...", Normal);
                    await api.SetOfferStatusAsync(platform.CatalogId, createdOfferIds, sellable: true);
                }
            }
            finally
            {
                SetBusy(false);
            }

            ApplyFilter();

            var report = new StringBuilder();
            report.Append($"Δημιουργήθηκαν {created} από {plans.Count} προϊόντα" +
                          (makeVisible ? " (ορατά)." : " (κρυφά)."));

            if (eficFailures.Count > 0)
            {
                report.Append($"\n⚠ ΠΡΟΣΟΧΗ: {eficFailures.Count} προϊόντα δημιουργήθηκαν στο " +
                              "PointToOrder αλλά ΔΕΝ γράφτηκε η αντιστοίχιση EFIC — φτιάξτε τη " +
                              "χειροκίνητα στην καρτέλα «Αντιστοιχίσεις»:\n   " +
                              string.Join("\n   ", eficFailures.Take(5)));
            }

            if (failures.Count > 0)
            {
                report.Append($"\nΑπέτυχαν {failures.Count}:\n   " +
                              string.Join("\n   ", failures.Take(5)) +
                              (failures.Count > 5 ? "\n   ..." : ""));
            }

            ShowStatus(report.ToString(),
                eficFailures.Count > 0 ? Bad : failures.Count > 0 ? Warn : Good);
        }

        private async Task<long> EnsureCategoryAsync(
            PointToOrderApiClient api, Dictionary<string, long> categoriesByName, string categoryName)
        {
            string name = categoryName.Length > 0 ? categoryName : "ΔΙΑΦΟΡΑ";
            string key = CatalogDiff.Normalise(name);

            if (categoriesByName.TryGetValue(key, out long existing))
                return existing;

            var category = await api.CreateCategoryAsync(platform!.CatalogId, name);
            if (category.Id == 0)
                throw new PointToOrderException(
                    $"Η κατηγορία «{name}» δημιουργήθηκε αλλά δεν επέστρεψε id.");

            categoriesByName[key] = category.Id;
            return category.Id;
        }

        private void SetBusy(bool busy)
        {
            LoadButton.IsEnabled = !busy;
            PushButton.IsEnabled = !busy && platform != null;
            SelectAllButton.IsEnabled = !busy && platform != null;
            ProductsGrid.IsEnabled = !busy;
            Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        }

        private static readonly Brush Normal = Brushes.Black;
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
