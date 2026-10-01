using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CatalogBridge
{
    // Αρχική: what the till needs during the day.
    //   1. Online orders - every waiting order, and the ticket that pops up when one arrives
    //      (OrderTicketWindow). The decision is applied to Orexsys first and PointToOrder second.
    //   2. The catalogue button - one press makes PointToOrder match Orexsys:
    //      reads both catalogs and the EFIC correlations; shows anything new in Orexsys (real
    //      products, active, priced) in an editable preview first and sends NOTHING until the
    //      user confirms; never touches prices; switches off online what is off at the POS;
    //      reports in plain language. Only ID-matched (EFIC) pairs are ever auto-fixed.
    // The e-mail correlation, till orders → PointToOrder and the mismatch check live on
    // «Αντιστοίχιση παραγγελιών» (OrdersView) since 01/10/2026.
    public partial class SyncView : UserControl
    {
        public SyncView()
        {
            InitializeComponent();
            var greek = new System.Globalization.CultureInfo("el-GR");
            string day = DateTime.Today.ToString("dddd d MMMM", greek);
            TodayTitle.Text = char.ToUpper(day[0], greek) + day[1..];
        }

        private async void SyncButton_Click(object sender, RoutedEventArgs e) => await SyncAsync();

        private Brush Res(string key) => (Brush)FindResource(key);

        // ---- licence -------------------------------------------------------------------------

        // Called by MainWindow at start and after Ρυθμίσεις. Nothing to say while licensed.
        public void ShowLicence()
        {
            var worst = License.Worst();
            bool show = worst != LicenseState.Licensed;
            LicenceBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            bool amber = worst == LicenseState.Grace;
            LicenceBanner.Background = Res(amber ? "WarnSoft" : "BadSoft");
            LicenceBannerText.Foreground = Res(amber ? "WarnBrush" : "BadBrush");
            LicenceBannerText.Text = worst switch
            {
                LicenseState.Grace => $"{License.Summary()}. Το πρόγραμμα λειτουργεί κανονικά για λίγες μέρες ακόμα. Επικοινωνήστε για ανανέωση.",
                LicenseState.Expired => $"{License.Summary()}. Το πρόγραμμα μόνο δείχνει: δεν συγχρονίζει, δεν στέλνει, δεν αλλάζει τίποτα. Επικοινωνήστε για ανανέωση.",
                _ => License.Current == null
                    ? "Χωρίς άδεια χρήσης. Ανοίξτε τις Ρυθμίσεις, ενότητα «Άδεια χρήσης», διαβάστε το αποτύπωμα του υπολογιστή και ζητήστε άδεια." +
                      (License.Problem.Length > 0 && License.Problem != "Δεν υπάρχει άδεια χρήσης." ? $" ({License.Problem})" : "")
                    : License.Summary(),
            };
        }

        // ---- online orders (owner, 01/10/2026) ------------------------------------------------

        // Wired by MainWindow, which owns the service so it keeps watching on every screen.
        public OrderMirrorService? Mirror { get; set; }

        // Rows are updated IN PLACE (one view-model per order, keyed by uuid) so a refresh every
        // 5 s never re-creates them - the enter motion plays once, when the order really arrives.
        private readonly System.Collections.ObjectModel.ObservableCollection<WaitingRow> waitingRows = new();
        private readonly Dictionary<string, WaitingRow> rowsByUuid = new();

        // The ticket: one open at a time, the rest queued oldest first. «Αργότερα» on a ticket
        // means it does not pop up again by itself; its row on Αρχική still opens it.
        private OrderTicketWindow? ticket;
        private readonly List<string> ticketQueue = new();
        private readonly HashSet<string> dismissed = new();
        private bool switchOn;

        public void ShowMirrorState(Settings settings)
        {
            switchOn = settings.OrderAcceptanceEnabled;
            WaitingRow.DefaultMinutes = settings.AcceptMinutes;
            MirrorButton.Tag = switchOn ? "True" : "False";
            MirrorStateText.Text = switchOn
                ? "Κάθε online παραγγελία περιμένει την απάντησή σας και ανοίγει μπροστά σας μόλις έρθει. Μόνο όσες δεχτείτε " +
                  "γίνονται παραγγελίες στο ταμείο. Ό,τι απαντήσετε από την οθόνη online παραγγελιών του Orexsys περνάει κι αυτό στο PointToOrder."
                : "Το PointToOrder δέχεται κάθε παραγγελία μόνο του. Ενεργοποιήστε για να εγκρίνετε εσείς κάθε παραγγελία.";
            WaitingList.ItemsSource ??= waitingRows;
            if (!switchOn)
            {
                waitingRows.Clear();
                rowsByUuid.Clear();
                ticketQueue.Clear();
                ticket?.Close();
                MirrorStatusText.Text = "";
                MirrorTodayText.Text = "";
                MirrorCount.Visibility = Visibility.Collapsed;
                WaitingEmpty.Visibility = Visibility.Collapsed;
                SetDot(MirrorDotColor, DotOffColor);
            }
            else SetDot(MirrorDotColor, License.CanWrite(Module.Orders) ? DotOnColor : DotOffColor);
        }

        public void ShowMirror(MirrorState state)
        {
            WaitingList.ItemsSource ??= waitingRows;

            // Update / add the ones still waiting; a new one goes to the ticket queue.
            var seen = new HashSet<string>();
            bool arrived = false;
            foreach (var w in state.Waiting)
            {
                seen.Add(w.Order.Uuid);
                if (rowsByUuid.TryGetValue(w.Order.Uuid, out var row)) row.Update(w);
                else
                {
                    row = new WaitingRow(w);
                    rowsByUuid[w.Order.Uuid] = row;
                    waitingRows.Add(row);
                    if (!dismissed.Contains(row.Uuid) && !ticketQueue.Contains(row.Uuid) && ticket?.Row.Uuid != row.Uuid)
                    {
                        ticketQueue.Add(row.Uuid);
                        arrived = true;
                    }
                }
            }
            // The ones that left: show the verdict for a moment, then let the row go.
            foreach (var row in waitingRows.Where(r => !seen.Contains(r.Uuid) && !r.Leaving).ToList())
            {
                var verdict = state.Today.Where(e => e.Token == row.Token).OrderByDescending(e => e.When).FirstOrDefault();
                row.Leave(verdict);
                ticketQueue.Remove(row.Uuid);
                if (ticket != null && ticket.Row == row) ticket.ShowAnsweredElsewhere(verdict);
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(verdict == null ? 0.2 : 2.4) };
                t.Tick += (s, e) => { t.Stop(); waitingRows.Remove(row); rowsByUuid.Remove(row.Uuid); };
                t.Start();
            }

            int count = state.Waiting.Count;
            MirrorCountText.Text = count.ToString();
            MirrorCount.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            WaitingEmpty.Visibility = switchOn && count == 0 && !state.Error ? Visibility.Visible : Visibility.Collapsed;
            MirrorStatusText.Text = state.Status;
            MirrorStatusText.Foreground = state.Error ? Bad : Normal;
            MirrorTodayText.Text = state.TodayText;
            int worst = state.Waiting.Count == 0 ? 0 : state.Waiting.Max(w => w.Level);
            SetDot(MirrorDotColor, state.Error || worst == 2 ? DotBadColor : count > 0 ? DotWaitColor
                : Mirror?.IsRunning == true ? DotOnColor : DotOffColor);

            if (arrived)
            {
                // The till must hear it and see it: a new order is money waiting for an answer.
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                if (ticket == null) ShowNextTicket();
            }
            UpdateQueueText();
        }

        private void ShowNextTicket()
        {
            while (ticketQueue.Count > 0)
            {
                string uuid = ticketQueue[0];
                ticketQueue.RemoveAt(0);
                if (rowsByUuid.TryGetValue(uuid, out var row) && !row.Leaving) { OpenTicket(row); return; }
            }
        }

        private void OpenTicket(WaitingRow row)
        {
            if (ticket != null)
            {
                if (ticket.Busy || ticket.Row == row) { ticket.Activate(); return; }
                // Switch the open ticket to the clicked order; the one it showed goes back in line.
                if (!ticket.Row.Leaving && !ticketQueue.Contains(ticket.Row.Uuid)) ticketQueue.Insert(0, ticket.Row.Uuid);
                ticketQueue.Remove(row.Uuid);
                ticket.Load(row);
                ticket.Activate();
                UpdateQueueText();
                return;
            }
            var owner = Window.GetWindow(this);
            ticket = new OrderTicketWindow(row, AcceptOrderAsync, RejectOrderAsync) { Owner = owner };
            ticket.Closed += (s, e) =>
            {
                var closed = (OrderTicketWindow)s!;
                if (!closed.Row.Leaving) dismissed.Add(closed.Row.Uuid);   // «Αργότερα»: no more pop-ups for it
                ticket = null;
                ShowNextTicket();
            };
            if (owner != null)
            {
                if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
                owner.Activate();
            }
            UpdateQueueText();
            ticket.Show();
        }

        private void UpdateQueueText()
        {
            if (ticket == null) return;
            int queued = ticketQueue.Count(u => rowsByUuid.TryGetValue(u, out var r) && !r.Leaving);
            ticket.SetQueue(1, 1 + queued);
        }

        private void OpenTicket_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WaitingRow row || row.Leaving) return;
            dismissed.Remove(row.Uuid);
            OpenTicket(row);
        }

        // ---- the decision (called by the ticket) ----------------------------------------------
        // Αποδοχή: the till first (the connector's row 1 → 2 with the minutes - the vendor trigger
        // raises the till order), then the platform (CLOSED + ready time). Απόρριψη: the till
        // (1 → -1, so nobody raises it; skipped when the row is not there yet - the service voids
        // it on arrival), then the platform (REJECTED + the reason the customer reads). If the
        // platform call fails after the till changed, the mirror tells the platform on a later
        // tick - the row is 2 / -1 by then - so the customer is never left wrong for long.

        public async Task<(bool Ok, string Message)> AcceptOrderAsync(WaitingRow row, int minutes)
        {
            if (!row.CanAccept) return (false, row.AcceptBlock);
            if (License.Blocked(Module.Orders, out string why)) return (false, why);
            Settings settings;
            try { settings = Settings.Load(); }
            catch (Exception ex) { return (false, "Δεν ήταν δυνατή η ανάγνωση των ρυθμίσεων: " + ex.Message); }

            if (minutes != settings.AcceptMinutes)
            {
                settings.AcceptMinutes = minutes;
                WaitingRow.DefaultMinutes = minutes;
                try { Settings.Save(settings); } catch { }
            }

            row.Busy = true;
            long? ordmId;
            try { ordmId = await OrderMirror.AcceptAtTillAsync(settings, row.EfomId, minutes); }
            catch (Exception ex)
            {
                row.Busy = false;
                CustomerDirectory.Log($"accept: {row.Token} at the till FAILED - {ex.Message}");
                if (Mirror != null) await Mirror.RefreshAsync();
                return (false, ex.Message);
            }
            CustomerDirectory.Log($"accept: {row.Token} accepted at the till from here ({minutes}′) → EFOM {row.EfomId} status 2, ORDM {ordmId?.ToString() ?? "(not yet)"}");
            OrderMirror.RememberOnce(OrderMirror.RegisterFile, new MirrorEvent(DateTime.UtcNow, row.Token, MirrorAction.Accept, minutes));

            string message = ordmId is long o ? $"Παραγγελία #{o} στο ταμείο. Ο πελάτης ενημερώθηκε." : "Πέρασε στο ταμείο. Ο πελάτης ενημερώθηκε.";
            try
            {
                using var api = new PointToOrderApiClient(settings);
                await OrderAcceptance.AcceptAsync(api, row.Uuid, minutes > 0 ? DateTime.UtcNow.AddMinutes(minutes) : null);
                CustomerDirectory.Log($"accept: {row.Token} → CLOSED on the platform, ready in {minutes}′");
            }
            catch (Exception ex)
            {
                CustomerDirectory.Log($"accept: {row.Token} platform FAILED - {ex.Message} (the mirror will retry)");
                message = (ordmId is long o2 ? $"Παραγγελία #{o2} στο ταμείο. " : "Πέρασε στο ταμείο. ") +
                          "Το PointToOrder δεν ενημερώθηκε ακόμα· θα ξαναπροσπαθήσει μόνο του.";
            }
            if (Mirror != null) await Mirror.RefreshAsync();
            return (true, message);
        }

        public async Task<(bool Ok, string Message)> RejectOrderAsync(WaitingRow row, string reason)
        {
            if (row.Leaving || !row.CanReject) return (false, row.RejectBlock.Length > 0 ? row.RejectBlock : "Η παραγγελία έχει ήδη απαντηθεί.");
            if (License.Blocked(Module.Orders, out string why)) return (false, why);
            Settings settings;
            try { settings = Settings.Load(); }
            catch (Exception ex) { return (false, "Δεν ήταν δυνατή η ανάγνωση των ρυθμίσεων: " + ex.Message); }

            row.Busy = true;
            bool atTill = row.CanAccept;   // the row is at the till and still waits: void it there first
            if (atTill)
            {
                try
                {
                    await OrderMirror.RejectAtTillAsync(settings, row.EfomId);
                    CustomerDirectory.Log($"reject: {row.Token} voided at the till from here (EFOM {row.EfomId} → -1)");
                    OrderMirror.RememberOnce(OrderMirror.RegisterFile, new MirrorEvent(DateTime.UtcNow, row.Token, MirrorAction.Reject, 0));
                }
                catch (Exception ex)
                {
                    row.Busy = false;
                    CustomerDirectory.Log($"reject: {row.Token} at the till FAILED - {ex.Message}");
                    if (Mirror != null) await Mirror.RefreshAsync();
                    return (false, ex.Message);
                }
            }
            try
            {
                using var api = new PointToOrderApiClient(settings);
                await OrderAcceptance.RejectAsync(api, row.Uuid, reason);
                CustomerDirectory.Log($"reject: {row.Token} → REJECTED on the platform - {reason}");
                if (!atTill)
                    OrderMirror.RememberOnce(OrderMirror.RegisterFile, new MirrorEvent(DateTime.UtcNow, row.Token, MirrorAction.Reject, 0));
            }
            catch (Exception ex)
            {
                CustomerDirectory.Log($"reject: {row.Token} platform FAILED - {ex.Message}");
                row.Busy = false;
                if (Mirror != null) await Mirror.RefreshAsync();
                return atTill
                    ? (true, "Δεν θα φτάσει στο ταμείο. Το PointToOrder δεν ενημερώθηκε ακόμα· θα ξαναπροσπαθήσει μόνο του.")
                    : (false, "Το PointToOrder δεν ενημερώθηκε: " + ex.Message);
            }
            if (Mirror != null) await Mirror.RefreshAsync();
            return (true, "Δεν θα φτάσει στο ταμείο. Ο πελάτης βλέπει τον λόγο.");
        }

        private static readonly Color DotOffColor = Color.FromRgb(0xC7, 0xD2, 0xD4);
        private static readonly Color DotOnColor = Color.FromRgb(0x1F, 0x7A, 0x4D);
        private static readonly Color DotWaitColor = Color.FromRgb(0xA8, 0x64, 0x00);
        private static readonly Color DotBadColor = Color.FromRgb(0xB9, 0x3A, 0x2E);

        // A colour change on a status dot is a state change: it crossfades (200 ms, ease-out)
        // instead of snapping, unless the user asked Windows for no animations.
        private static void SetDot(SolidColorBrush brush, Color to)
        {
            if (brush.Color == to) return;
            if (!Motion.Enabled) { brush.Color = to; return; }
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(to, Motion.Fade) { EasingFunction = Motion.EaseOut });
        }

        private async void MirrorButton_Click(object sender, RoutedEventArgs e)
        {
            Settings settings;
            try { settings = Settings.Load(); }
            catch (Exception ex) { MirrorStatusText.Text = "Δεν ήταν δυνατή η ανάγνωση των ρυθμίσεων: " + ex.Message; MirrorStatusText.Foreground = Bad; return; }

            bool turningOn = !settings.OrderAcceptanceEnabled;
            if (turningOn)
            {
                if (License.Blocked(Module.Orders, out string why)) { MirrorStatusText.Text = why; MirrorStatusText.Foreground = Bad; return; }
                if (!settings.IsApiConfigured || !settings.IsDatabaseConfigured)
                {
                    MirrorStatusText.Text = "Το πρόγραμμα δεν έχει ρυθμιστεί ακόμα. Ανοίξτε τις Ρυθμίσεις, κάτω αριστερά.";
                    MirrorStatusText.Foreground = Warn;
                    return;
                }
                var sure = MessageBox.Show(
                    "Να περιμένει κάθε online παραγγελία την έγκρισή σας;\n\n" +
                    "Το PointToOrder θα σταματήσει να δέχεται παραγγελίες μόνο του. Κάθε νέα παραγγελία ανοίγει μπροστά σας: " +
                    "με «Αποδοχή» (και τα λεπτά της) γίνεται παραγγελία στο ταμείο και ο πελάτης βλέπει πότε θα είναι έτοιμη. " +
                    "Με «Απόρριψη» δεν φτάνει ποτέ στο ταμείο και ο πελάτης βλέπει τον λόγο. Το ίδιο ισχύει αν απαντήσετε " +
                    "από την οθόνη online παραγγελιών του Orexsys.\n\n" +
                    "Όσο αυτό το πρόγραμμα είναι κλειστό, οι παραγγελίες περιμένουν στο ταμείο (και ο πελάτης βλέπει «σε αναμονή») " +
                    "μέχρι να ξανανοίξει ή να απαντήσετε από το Orexsys.\n\nΣυνέχεια;",
                    "Online παραγγελίες", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (sure != MessageBoxResult.Yes) return;
            }

            // The platform first: if its setting does not change, nothing changes here either.
            MirrorButton.IsEnabled = false;
            try
            {
                using var api = new PointToOrderApiClient(settings);
                await OrderAcceptance.SetPlatformAutoAcceptAsync(api, turningOn);
            }
            catch (Exception ex)
            {
                MirrorStatusText.Text = ex.Message;
                MirrorStatusText.Foreground = Bad;
                MirrorButton.IsEnabled = true;
                return;
            }
            MirrorButton.IsEnabled = true;

            settings.OrderAcceptanceEnabled = turningOn;
            try { Settings.Save(settings); }
            catch (Exception ex) { MirrorStatusText.Text = "Η αποθήκευση απέτυχε: " + ex.Message; MirrorStatusText.Foreground = Bad; return; }
            CustomerDirectory.Log($"mirror: switched {(turningOn ? "ON (platform AUTO_ACCEPT_ORDERS=false)" : "OFF (platform AUTO_ACCEPT_ORDERS=true)")}");

            ShowMirrorState(settings);
            Mirror?.Apply(settings);
        }

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

            if (License.Blocked(Module.Catalogue, out string why)) { ShowStatus(why, Bad); return; }
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
                // Orders arriving under a different code than the one links are written with
                // can never match (21/09, Stavros): stop before anything is created.
                var feed = await EficRepository.CheckOrderFeedAsync(settings);
                if (feed.Mismatch)
                {
                    ShowStatus(feed.Message, Bad);
                    return;
                }

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

                // Active, not yet online, in a chosen category - but with no price in the
                // configured list/column, so nothing can be sent. Never skipped in silence:
                // seen 18/09, four products priced for restaurant/take-away with Delivery 0,00
                // and the sync said «όλα συγχρονισμένα».
                var unpriced = orexsys.Products
                    .Where(p => PushRules.IsUnpriced(p, settings))
                    .ToList();
                string unpricedNote = unpriced.Count == 0 ? "" :
                    $"⚠ {unpriced.Count} ενεργά προϊόντα στις επιλεγμένες κατηγορίες ΔΕΝ στάλθηκαν γιατί " +
                    $"δεν έχουν τιμή «{settings.PriceSourceLabel}» στον τιμοκατάλογο «{orexsys.PriceListName}»: " +
                    string.Join(", ", unpriced.Take(6).Select(p => p.Name)) + (unpriced.Count > 6 ? ", ..." : "") +
                    ". Βάλτε τιμή στο Orexsys ή αλλάξτε «Τιμή» στις Ρυθμίσεις.";
                if (unpricedNote.Length > 0) results.Add(unpricedNote);


                // Prices are deliberately not compared or touched: the PointToOrder prices
                // travel into Orexsys with each order anyway, so the platform's price IS the
                // online price (user decision).
                var deactivations = diff.Rows
                    .Where(r => r.Status == DiffStatus.ActiveMismatch && r.MatchedBy == MatchKind.ByCode)
                    .ToList();

                if (newProducts.Count == 0 && deactivations.Count == 0)
                {
                    ResultsList.ItemsSource = results;
                    ResultsCard.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (unpriced.Count > 0)
                    {
                        ShowStatus($"Τίποτα δεν στάλθηκε: {unpriced.Count} προϊόντα δεν έχουν τιμή " +
                                   $"«{settings.PriceSourceLabel}» (βλ. παρακάτω). ({DateTime.Now:HH:mm})", Warn);
                        return;
                    }
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

        private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0x5E, 0x6E, 0x72));
        private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x4D));
        private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xA8, 0x64, 0x00));
        private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xB9, 0x3A, 0x2E));

        private void ShowStatus(string message, Brush colour)
        {
            StatusText.Text = message;
            StatusText.Foreground = colour;
            StatusText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
