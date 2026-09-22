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
    // and at the till — with the count, the total and the average per order; and «Αποστολή στο
    // PointToOrder» for a till order, so the history matches on both sides (TillOrderSender).
    public partial class CustomerOrdersWindow : Window
    {
        private static readonly CultureInfo Greek = new("el-GR");
        private readonly Settings settings;
        private readonly OrexsysCustomer customer;
        private long? platformCustomerId;
        private Dictionary<string, string> sent = new();

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

                // What the platform already holds for this customer: their account, and the
                // till orders sent before (externalOrderId OREXSYS-<ORDMID>).
                if (settings.IsApiConfigured && customer.Email.Length > 0)
                {
                    try
                    {
                        using var api = new PointToOrderApiClient(settings);
                        platformCustomerId = await api.FindCustomerIdAsync(customer.Email, null);
                        sent = platformCustomerId is long id ? await TillOrderSender.LoadSentAsync(api, id) : new();
                    }
                    catch (Exception ex)
                    {
                        thenSay = (thenSay == null ? "" : thenSay + " ") + "(PointToOrder: " + ex.Message + ")";
                    }
                }
                foreach (var o in orders.Orders)
                    o.PlatformText = o.FromPlatform ? "από P2O"
                        : sent.TryGetValue(TillOrderSender.Plan.ExternalIdFor(o.OrdmId), out string? token) ? $"✓ {token}" : "";

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

        private void UpdateButton()
        {
            var o = Selected;
            SendButton.IsEnabled = o != null && !o.FromPlatform && o.PlatformText.Length == 0 && platformCustomerId != null;
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
            catch (Exception ex)
            {
                CustomerDirectory.Log($"PointToOrder: till order {o.OrdmId} FAILED - {ex.Message}");
                StatusText.Text = "Η αποστολή απέτυχε: " + ex.Message;
                UpdateButton();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
