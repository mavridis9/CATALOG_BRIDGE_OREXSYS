using System.Windows;

namespace CatalogBridge
{
    // The one question before an order is rejected: the sentence the customer will read.
    public partial class RejectReasonWindow : Window
    {
        public string Reason { get; private set; } = "";

        public RejectReasonWindow(string title, string items)
        {
            InitializeComponent();
            OrderText.Text = title + "\n" + items;
            ReasonBox.SelectedIndex = 0;
            Loaded += (s, e) => ReasonBox.Focus();
        }

        private void Reject_Click(object sender, RoutedEventArgs e)
        {
            Reason = OrderAcceptance.FitRejectReason(ReasonBox.Text);
            DialogResult = true;
        }
    }
}
