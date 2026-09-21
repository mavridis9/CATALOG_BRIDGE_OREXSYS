using System.Windows;

namespace CatalogBridge
{
    // Collects and validates the new customer's details; creating happens in CustomersView so
    // progress and the outcome of both systems are reported in one place.
    public partial class NewCustomerWindow : Window
    {
        public CustomerInput? Result { get; private set; }

        public NewCustomerWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => NameBox.Focus();
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            var input = new CustomerInput(NameBox.Text, CardBox.Text, EmailBox.Text, MobileBox.Text).Trimmed();
            string? problem = CustomerDirectory.Validate(input);
            if (problem != null)
            {
                ErrorText.Text = problem;
                return;
            }

            Result = input;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
