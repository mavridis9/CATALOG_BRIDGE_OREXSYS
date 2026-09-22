using System;
using System.Globalization;
using System.Windows;

namespace CatalogBridge
{
    // Double-click on a customer in «Πελάτες»: every real order of theirs in Orexsys — online
    // and at the till — with the count, the total and the average per order. Read-only.
    public partial class CustomerOrdersWindow : Window
    {
        private static readonly CultureInfo Greek = new("el-GR");

        public CustomerOrdersWindow(Settings settings, OrexsysCustomer customer)
        {
            InitializeComponent();
            TitleText.Text = customer.Name.Length > 0 ? customer.Name : $"Πελάτης {customer.CustId}";
            SubText.Text = $"Κάρτα {(customer.Card.Length > 0 ? customer.Card : "—")} · {customer.Email} · CUSTID {customer.CustId}";
            StatusText.Text = "Ανάγνωση παραγγελιών...";
            Loaded += async (s, e) =>
            {
                try
                {
                    var orders = await CustomerDirectory.LoadOrdersAsync(settings, customer.CustId);
                    OrdersGrid.ItemsSource = orders.Orders;
                    CountText.Text = orders.Count.ToString();
                    TotalText.Text = orders.Total.ToString("0.00", Greek) + " €";
                    AverageText.Text = orders.Average.ToString("0.00", Greek) + " €";
                    StatusText.Text = orders.Count == 0
                        ? "Καμία παραγγελία." + (orders.Cancelled > 0 ? $" ({orders.Cancelled} ακυρωμένες δεν μετράνε.)" : "")
                        : $"Από {orders.First:dd/MM/yyyy} έως {orders.Last:dd/MM/yyyy}. Οι ακυρωμένες " +
                          $"({orders.Cancelled}) δεν μετράνε στα σύνολα.";
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Η ανάγνωση απέτυχε: " + ex.Message;
                }
            };
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
