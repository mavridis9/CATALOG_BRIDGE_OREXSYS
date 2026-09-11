using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CatalogBridge
{
    // The end user's whole world: one button that makes PointToOrder match Orexsys.
    //
    // What one press does, in order:
    //   1. Reads both catalogs and the EFIC correlations.
    //   2. Anything new in Orexsys (real products - not extras - active, priced) is shown in
    //      the preview/map first: category names and product names are editable there, and
    //      NOTHING is sent until the user confirms. On a fresh installation this preview IS
    //      the whole catalog.
    //   3. Prices are NEVER touched: PointToOrder prices flow into Orexsys with each order.
    //   4. Products switched off in the POS but still selling online are switched off online.
    //   5. Everything is reported in plain language.
    //
    // Only ID-matched (EFIC) pairs are ever auto-fixed; name-guessed pairs are technician
    // territory and never touched from here.
    public partial class SyncView : UserControl
    {
        public SyncView()
        {
            InitializeComponent();
        }

        private async void SyncButton_Click(object sender, RoutedEventArgs e) => await SyncAsync();

        private async Task SyncAsync()
        {
            Settings settings;
            try
            {
                settings = Settings.Load();
            }
            catch (Exception ex)
            {
                ShowStatus("Δεν ήταν δυνατή η ανάγνωση των ρυθμίσεων: " + ex.Message, Bad);
                return;
            }

            if (!settings.IsDatabaseConfigured || !settings.IsApiConfigured)
            {
                ShowStatus("Το πρόγραμμα δεν έχει ρυθμιστεί ακόμα — ανοίξτε τις Ρυθμίσεις (κάτω αριστερά).", Warn);
                return;
            }

            SyncButton.IsEnabled = false;
            ResultsCard.Visibility = Visibility.Collapsed;
            ShowStatus("Ανάγνωση των δύο καταλόγων...", Normal);

            var results = new List<string>();

            try
            {
                var orexsys = await OrexsysCatalog.LoadAsync(settings);

                using var api = new PointToOrderApiClient(settings);
                var platform = await api.GetCatalogAsync();
                // A link to an offer that no longer exists online (deleted on PointToOrder by
                // hand) is no link: the product is sent again and the dead row cleaned up.
                int orphaned = PushRules.DropOrphanLinks(orexsys, platform);

                var diff = CatalogDiff.Build(orexsys, platform, settings);

                // --- what needs doing -------------------------------------------------------
                var newProducts = orexsys.Products
                    .Where(p => PushRules.IsSendable(p, settings))
                    .ToList();


                // Prices are deliberately not compared or touched: the PointToOrder prices
                // travel into Orexsys with each order anyway, so the platform's price IS the
                // online price (user decision).
                var deactivations = diff.Rows
                    .Where(r => r.Status == DiffStatus.ActiveMismatch && r.MatchedBy == MatchKind.ByCode)
                    .ToList();

                if (newProducts.Count == 0 && deactivations.Count == 0)
                {
                    ShowStatus($"✓ Όλα συγχρονισμένα. {platform.Offers.Count} προϊόντα online, " +
                               $"καμία διαφορά. ({DateTime.Now:HH:mm})", Good);
                    return;
                }

                // --- the preview / map ------------------------------------------------------
                // Validated, never read raw from settings: a code that belongs to another
                // platform (Wolt / efood) comes back empty and stops the sync here, before
                // anything is created on PointToOrder.
                string storeCode = await EficRepository.LoadStoreCodeAsync(settings);

                if (newProducts.Count > 0 && storeCode.Length == 0)
                {
                    ShowStatus("Λείπει ο κωδικός PointToOrder («Κωδ. EFIC») ή ανήκει σε άλλη πλατφόρμα " +
                               "(Wolt / efood) — ένας τεχνικός πρέπει να τον ορίσει στις Ρυθμίσεις.", Warn);
                    return;
                }

                var categoriesByName = new Dictionary<string, long>();
                foreach (var category in platform.Categories)
                {
                    string key = CatalogDiff.Normalise(category.Name);
                    if (key.Length > 0 && !categoriesByName.ContainsKey(key))
                        categoriesByName[key] = category.CategoryId;
                }

                // One plan item per unit: a product, or - sent as separate items - each of its
                // sizes («ΤΟΣΤ», «ΤΟΣΤ XL»), with that size's price and ITMVID.
                var plans = newProducts
                    .SelectMany(p => PushRules.SendableUnits(p, settings))
                    .Select(PushPlanItem.From)
                    .ToList();

                var categoryPlans = newProducts
                    .Select(p => p.CategoryName.Length > 0 ? p.CategoryName : "ΔΙΑΦΟΡΑ")
                    .Distinct()
                    .Where(c => !categoriesByName.ContainsKey(CatalogDiff.Normalise(c)))
                    .Select(c => new CategoryPlan { OrexsysName = c, TargetName = c })
                    .ToList();

                string fixesSummary =
                    
                    (deactivations.Count > 0 ? $" · {deactivations.Count} προϊόντα θα κλείσουν online" : "");

                if (plans.Count > 0)
                {
                    var preview = new PushPreviewWindow(plans, categoryPlans, makeVisible: true,
                        extraSummary: fixesSummary)
                    {
                        Owner = Window.GetWindow(this),
                    };
                    if (preview.ShowDialog() != true)
                    {
                        ShowStatus("Ο συγχρονισμός ακυρώθηκε — δεν άλλαξε τίποτα.", Normal);
                        return;
                    }
                }
                else
                {
                    var confirm = MessageBox.Show(
                        $"Θα γίνουν οι εξής διορθώσεις στο PointToOrder:{fixesSummary}\n\nΝα συνεχίσω;",
                        "Συγχρονισμός", MessageBoxButton.YesNo, MessageBoxImage.Question,
                        MessageBoxResult.No);
                    if (confirm != MessageBoxResult.Yes)
                    {
                        ShowStatus("Ο συγχρονισμός ακυρώθηκε — δεν άλλαξε τίποτα.", Normal);
                        return;
                    }
                }

                // --- execute ----------------------------------------------------------------
                var targetCategoryNames = categoryPlans.ToDictionary(
                    c => CatalogDiff.Normalise(c.OrexsysName), c => c.FinalName);

                int pushed = 0, orphanRowsRemoved = 0;
                var pushFailures = new List<string>();
                var newOfferIds = new List<long>();
                var orphansCleaned = new HashSet<long>();

                foreach (var plan in plans)
                {
                    ShowStatus($"Αποστολή {pushed + pushFailures.Count + 1}/{plans.Count}: " +
                               $"{plan.FinalName}...", Normal);
                    try
                    {
                        // Dead EFIC rows of this product go first, once per product, so the
                        // new row is the only one for its size.
                        if (plan.Product.OrphanCodes.Count > 0 && orphansCleaned.Add(plan.Product.ItmsId))
                            orphanRowsRemoved += await EficRepository.DeleteOrphanLinksAsync(
                                settings, plan.Product.ItmsId, plan.Product.OrphanCodes);

                        string orexsysCategory = plan.Product.CategoryName.Length > 0
                            ? plan.Product.CategoryName : "ΔΙΑΦΟΡΑ";
                        string targetCategory = targetCategoryNames.TryGetValue(
                            CatalogDiff.Normalise(orexsysCategory), out var chosen)
                            ? chosen : orexsysCategory;

                        string key = CatalogDiff.Normalise(targetCategory);
                        if (!categoriesByName.TryGetValue(key, out long categoryId))
                        {
                            var created = await api.CreateCategoryAsync(platform.CatalogId, targetCategory);
                            categoryId = created.Id;
                            categoriesByName[key] = categoryId;
                        }

                        var offer = await api.CreateOfferAsync(
                            platform.CatalogId, categoryId, plan.FinalName, plan.Price);
                        if (offer.Id == 0)
                            throw new PointToOrderException("δεν επέστρεψε κωδικό");

                        newOfferIds.Add(offer.Id);
                        await EficRepository.InsertAsync(
                            settings, storeCode, offer.Id, plan.FinalName, plan.Product.ItmsId,
                            plan.ItmvId);

                        // Size option group («ΕΠΙΛΕΞΤΕ ΜΕΓΕΘΟΣ») under the offer; its options
                        // are offers too and must be made visible along with the parent.
                        if (plan.Options.Count > 0)
                            newOfferIds.AddRange(await api.CreateOptionGroupAsync(
                                platform.CatalogId, offer.Id, PushRules.SizeGroupName,
                                plan.Options.Select(o => (o.Name, o.Surcharge)).ToList()));
                        pushed++;
                    }
                    catch (Exception ex)
                    {
                        pushFailures.Add($"{plan.FinalName}: {ex.Message}");
                    }
                }

                // The simple flow means "synced" from the customer's point of view: what was
                // sent is immediately visible and orderable online.
                if (newOfferIds.Count > 0)
                {
                    ShowStatus("Τα νέα προϊόντα γίνονται ορατά...", Normal);
                    await api.SetOfferStatusAsync(platform.CatalogId, newOfferIds, sellable: true);
                    results.Add($"✓ Στάλθηκαν {pushed} νέα προϊόντα (ορατά στους πελάτες)");
                }

                if (orphanRowsRemoved > 0)
                    results.Add($"♻ {orphansCleaned.Count} προϊόντα είχαν διαγραφεί από το PointToOrder " +
                                $"και στάλθηκαν ξανά — {orphanRowsRemoved} παλιές αντιστοιχίσεις καθαρίστηκαν");
                else if (orphaned > 0)
                    results.Add($"⚠ {orphaned} προϊόντα έχουν αντιστοίχιση σε προϊόν που δεν υπάρχει πια " +
                                "στο PointToOrder (βλ. Αντιστοιχίσεις)");

                if (deactivations.Count > 0)
                {
                    ShowStatus("Απενεργοποίηση προϊόντων που έκλεισαν στο ταμείο...", Normal);
                    await api.SetOfferStatusAsync(diff.CatalogId,
                        deactivations.Select(r => r.Offer!.OfferId).ToList(),
                        OfferItemType.Inactive, sellable: false);
                    results.Add($"✓ Έκλεισαν online {deactivations.Count} προϊόντα που είναι " +
                                "ανενεργά στο ταμείο");
                }

                if (pushFailures.Count > 0)
                    results.Add($"⚠ Απέτυχαν {pushFailures.Count}: " +
                                string.Join("; ", pushFailures.Take(4)));

                ResultsList.ItemsSource = results;
                ResultsCard.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                ShowStatus(pushFailures.Count == 0
                    ? $"✓ Ο συγχρονισμός ολοκληρώθηκε ({DateTime.Now:HH:mm})."
                    : $"Ο συγχρονισμός ολοκληρώθηκε με σφάλματα ({DateTime.Now:HH:mm}).",
                    pushFailures.Count == 0 ? Good : Warn);
            }
            catch (Exception ex)
            {
                ShowStatus("Ο συγχρονισμός απέτυχε: " + ex.Message, Bad);
            }
            finally
            {
                SyncButton.IsEnabled = true;
            }
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
