using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CatalogBridge
{
    // Double-click on a customer in «Πελάτες»: every real order of theirs in Orexsys — online
    // and at the till — with the count, the total and the average per order; «Αποστολή στο
    // PointToOrder» for a till order, so the history matches on both sides (TillOrderSender);
    // and «Ακύρωση παραγγελίας» — one click, both platforms (OrderCancel, owner 22/09/2026).
    public partial class CustomerOrdersWindow : Window
    {
        private static readonly CultureInfo Greek = new("el-GR");
        private readonly Settings settings;
        private readonly OrexsysCustomer customer;
        private long? platformCustomerId;
        private Dictionary<long, string> sentTokens = new();   // ORDMID → token, from the local registry
        private Dictionary<string, TillOrderSender.PlatformOrderSummary?> byToken = new(StringComparer.OrdinalIgnoreCase);

        public CustomerOrdersWindow(Settings settings, OrexsysCustomer customer)
        {
            InitializeComponent();
            this.settings = settings;
            this.customer = customer;
            TitleText.Text = customer.Name.Length > 0 ? customer.Name : $"Πελάτης {customer.CustId}";
            SubText.Text = $"Κάρτα {(customer.Card.Length > 0 ? customer.Card : "—")} · {customer.Email} · CUSTID {customer.CustId}";
            Loaded += async (s, e) => await LoadAsync();
        }

        private async Task LoadAsync(string? thenSay = null)
        {
            StatusText.Text = "Ανάγνωση παραγγελιών...";
            try
            {
                var orders = await CustomerDirectory.LoadOrdersAsync(settings, customer.CustId);

                // The PointToOrder side of each order: the till-sent token comes from the local
                // registry (survives the order being cancelled there - the platform's own
                // customer-history endpoint silently drops cancelled/deleted orders, found 22/09
                // while building the cancel button); the platform-native token comes from
                // Orexsys's EFOM.JSON. Each distinct token is then looked up on the platform by
                // itself (orders/search, which DOES still find a cancelled one) for its live status.
                sentTokens = TillOrderSender.LoadSentRegisterWithTokens();
                byToken = new(StringComparer.OrdinalIgnoreCase);
                if (settings.IsApiConfigured && customer.Email.Length > 0)
                {
                    try
                    {
                        using var api = new PointToOrderApiClient(settings);
                        platformCustomerId = await api.FindCustomerIdAsync(customer.Email, null);
                        var tokens = orders.Orders.Select(PlatformTokenFor).Where(t => t != null).Select(t => t!)
                            .Distinct(StringComparer.OrdinalIgnoreCase);
                        foreach (var token in tokens)
                            byToken[token] = await TillOrderSender.FindOrderByTokenAsync(api, token);
                    }
                    catch (Exception ex)
                    {
                        thenSay = (thenSay == null ? "" : thenSay + " ") + "(PointToOrder: " + ex.Message + ")";
                    }
                }
                foreach (var o in orders.Orders)
                {
                    string? token = PlatformTokenFor(o);
                    var plat = token != null && byToken.TryGetValue(token, out var p) ? p : null;
                    string tag = plat != null && plat.Status is "CANCELLED" or "DELETED" ? " (ακυρωμένη)" : "";
                    o.PlatformText = o.FromPlatform ? "από P2O" + tag : token != null ? $"✓ {token}" + tag : "";
                }

                OrdersGrid.ItemsSource = orders.Orders;
                CountText.Text = orders.Count.ToString();
                TotalText.Text = orders.Total.ToString("0.00", Greek) + " €";
                AverageText.Text = orders.Average.ToString("0.00", Greek) + " €";
                string summary = orders.Count == 0
                    ? "Καμία παραγγελία." + (orders.Cancelled > 0 ? $" ({orders.Cancelled} ακυρωμένες δεν μετράνε.)" : "")
                    : $"Από {orders.First:dd/MM/yyyy} έως {orders.Last:dd/MM/yyyy}. Οι ακυρωμένες ({orders.Cancelled}) δεν μετράνε στα σύνολα.";
                if (platformCustomerId == null) summary += " Ο πελάτης δεν έχει λογαριασμό στο PointToOrder με αυτό το e-mail.";
                StatusText.Text = thenSay == null ? summary : thenSay + "\n" + summary;
                UpdateButton();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Η ανάγνωση απέτυχε: " + ex.Message;
            }
        }

        private CustomerDirectory.CustomerOrder? Selected => OrdersGrid.SelectedItem as CustomerDirectory.CustomerOrder;

        private void OrdersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButton();

        // The PointToOrder token for this Orexsys order, however it is known - from EFOM.JSON for
        // one that arrived FROM the platform, from the local registry for a till order pushed
        // there. Null = no PointToOrder order known for it at all.
        private string? PlatformTokenFor(CustomerDirectory.CustomerOrder o) =>
            o.FromPlatform ? (o.PlatformOrderNumber.Length > 0 ? o.PlatformOrderNumber : null)
                            : (sentTokens.TryGetValue(o.OrdmId, out string? t) ? t : null);

        // What OrderCancel.Decide sees: null = no PointToOrder order at all; "" = one is known by
        // token but its live status could not be read (API down at load, or the search found
        // nothing) - still treated as active, so the button offers to act, and the click handler
        // explains itself if it really cannot find it there.
        private CancelSide CancelDecisionFor(CustomerDirectory.CustomerOrder o)
        {
            string? token = PlatformTokenFor(o);
            string? status = token == null ? null : (byToken.TryGetValue(token, out var p) && p != null ? p.Status : "");
            return OrderCancel.Decide(o.CancelledInOrexsys, status);
        }

        private void UpdateButton()
        {
            var o = Selected;
            SendButton.IsEnabled = o != null && !o.FromPlatform && !o.CancelledInOrexsys && o.PlatformText.Length == 0 && platformCustomerId != null;
            CancelOrderButton.IsEnabled = o != null && CancelDecisionFor(o) != CancelSide.None;
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var o = Selected;
            if (o == null || platformCustomerId is not long customerId) return;

            SendButton.IsEnabled = false;
            try
            {
                // The loop guard must be in the trigger, or this order comes straight back as a
                // duplicate till order (seen live 22/09).
                var state = await Task.Run(() => EfomTriggerGuard.ReadState(settings));
                if (state != EfomTriggerGuard.State.Patched)
                {
                    StatusText.Text = "Δεν στέλνεται: η προστασία επιστροφής δεν είναι ενεργή στο Orexsys. Ενεργοποιήστε την " +
                                      "«Αντιστοίχιση παραγγελιών με e-mail» στις Ρυθμίσεις (με το Orexsys κλειστό) και ξαναδοκιμάστε.";
                    return;
                }

                StatusText.Text = "Έλεγχος της παραγγελίας...";
                var (head, lines) = await TillOrderSender.LoadOrderAsync(settings, o.OrdmId);
                if (head.Cancelled || head.FromPlatform) { StatusText.Text = "Αυτή η παραγγελία δεν στέλνεται."; return; }

                var offers = await TillOrderSender.LoadOffersAsync(settings);
                using var api = new PointToOrderApiClient(settings);
                var catalog = await api.GetCatalogAsync();
                var prices = catalog.Offers.GroupBy(x => x.OfferId).ToDictionary(g => g.Key, g => g.First().Price);

                var placeholders = await TillOrderSender.EnsurePlaceholdersAsync(api, catalog);
                string? problem = TillOrderSender.BuildPlatformLines(lines, offers, prices, placeholders, out var platformLines);
                if (problem != null) { StatusText.Text = problem; return; }
                var plan = new TillOrderSender.Plan(head.OrdmId, head.When, head.Total, lines, platformLines,
                    TillOrderSender.Plan.ExternalIdFor(head.OrdmId));

                var sure = MessageBox.Show(
                    $"Να σταλεί η παραγγελία #{head.OrdmId} ({head.When:dd/MM/yyyy HH:mm}, {head.Total:0.00} €) στο PointToOrder, " +
                    $"στο ιστορικό του πελάτη «{customer.Name}»;\n\n" +
                    string.Join("\n", lines.Select(l => $"  {l.Name} ×{l.Qty:0} — {l.Total:0.00} €")) +
                    "\n\nΘα εμφανιστεί ως παραγγελία του PointToOrder, πληρωμένη στο κατάστημα.",
                    "Αποστολή στο PointToOrder", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (sure != MessageBoxResult.Yes) { StatusText.Text = "Ακυρώθηκε."; UpdateButton(); return; }

                var result = await TillOrderSender.SendAsync(api, customerId, customer.Name, plan, m => StatusText.Text = m);
                CustomerDirectory.Log($"PointToOrder: till order {head.OrdmId} ({head.Total:0.00}) sent for CUSTID {customer.CustId} → {result.Token} {result.Status}");
                await LoadAsync($"✓ Η παραγγελία #{head.OrdmId} στάλθηκε στο PointToOrder ως {result.Token} ({result.Status}).");
            }
            catch (TillOrderSender.AlreadySentException ex)
            {
                // Someone (the automatic run, another PC) got there first - show the truth, do not resend.
                CustomerDirectory.Log($"PointToOrder: till order {o.OrdmId} NOT resent - {ex.Message}");
                await LoadAsync(ex.Message);
            }
            catch (Exception ex)
            {
                CustomerDirectory.Log($"PointToOrder: till order {o.OrdmId} FAILED - {ex.Message}");
                StatusText.Text = "Η αποστολή απέτυχε: " + ex.Message;
                UpdateButton();
            }
        }

        private async void CancelOrderButton_Click(object sender, RoutedEventArgs e)
        {
            var o = Selected;
            if (o == null) return;

            string? token = PlatformTokenFor(o);
            TillOrderSender.PlatformOrderSummary? plat = token != null && byToken.TryGetValue(token, out var p) ? p : null;
            var decision = OrderCancel.Decide(o.CancelledInOrexsys, token == null ? null : (plat?.Status ?? ""));

            if (decision == CancelSide.None)
            {
                StatusText.Text = "Είναι ήδη ακυρωμένη και στα δύο συστήματα — δεν χρειάζεται τίποτα.";
                return;
            }
            if (token != null && plat == null)
            {
                StatusText.Text = $"Η παραγγελία «{token}» δεν βρέθηκε στο PointToOrder (ή δεν διαβάστηκε) — " +
                                   "κλείστε και ξανανοίξτε αυτό το παράθυρο και ξαναδοκιμάστε.";
                return;
            }

            string where = decision switch
            {
                CancelSide.Both => "στο Orexsys ΚΑΙ στο PointToOrder",
                CancelSide.OrexsysOnly => "στο Orexsys (στο PointToOrder είναι ήδη ακυρωμένη)",
                CancelSide.PlatformOnly => "στο PointToOrder (στο Orexsys είναι ήδη ακυρωμένη)",
                _ => "",
            };
            var sure = MessageBox.Show(
                $"ΑΚΥΡΩΣΗ παραγγελίας #{o.OrdmId} ({o.When:dd/MM/yyyy HH:mm}, {o.Total:0.00} €)\n{o.Items}\n\n" +
                $"Θα ακυρωθεί {where}.\n\n" +
                (decision != CancelSide.PlatformOnly
                    ? "Στο Orexsys γίνεται όπως στο ταμείο: αντιστρέφονται τα είδη και οι πληρωμές και εκδίδεται " +
                      "ακυρωτικό παραστατικό. ΔΕΝ αναιρείται.\n\n"
                    : "") +
                "Συνέχεια;",
                "Ακύρωση παραγγελίας", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (sure != MessageBoxResult.Yes) { StatusText.Text = "Ακυρώθηκε η ενέργεια."; return; }

            CancelOrderButton.IsEnabled = false;
            bool didOrexsys = false, didPlatform = false;
            try
            {
                if (decision is CancelSide.OrexsysOnly or CancelSide.Both)
                {
                    string remarks = $"Ακύρωση από Γέφυρα Καταλόγου ({Environment.UserName})";
                    await OrderCancel.CancelInOrexsysAsync(settings, o.OrdmId, remarks);
                    didOrexsys = true;
                    CustomerDirectory.Log($"cancel: order {o.OrdmId} cancelled in Orexsys for CUSTID {customer.CustId}");
                }
                if (decision is CancelSide.PlatformOnly or CancelSide.Both)
                {
                    using var api = new PointToOrderApiClient(settings);
                    await TillOrderSender.CancelOnPlatformAsync(api, plat!.Uuid);
                    didPlatform = true;
                    CustomerDirectory.Log($"cancel: order {o.OrdmId} cancelled on PointToOrder ({token}) for CUSTID {customer.CustId}");
                }
                await LoadAsync($"✓ Η παραγγελία #{o.OrdmId} ακυρώθηκε {where}.");
            }
            catch (Exception ex)
            {
                string done = didOrexsys ? " (στο Orexsys ΕΓΙΝΕ ήδη)" : didPlatform ? " (στο PointToOrder ΕΓΙΝΕ ήδη)" : "";
                CustomerDirectory.Log($"cancel: order {o.OrdmId} FAILED{done} - {ex.Message}");
                StatusText.Text = $"Η ακύρωση απέτυχε{done}: {ex.Message}";
                UpdateButton();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
