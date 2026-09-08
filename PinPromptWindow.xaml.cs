using System.Windows;
using System.Windows.Input;

namespace CatalogBridge
{
    // Keeps the Settings screen out of the hands of the store staff: the technician's PIN is
    // asked before it opens. This is a stop-the-honest gate, not cryptography - the settings
    // file itself is already protected (passwords DPAPI-encrypted).
    public partial class PinPromptWindow : Window
    {
        private const string TechnicianPin = "5757";

        public PinPromptWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => PinBox.Focus();
        }

        private void PinBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) TryAccept();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) => TryAccept();

        private void TryAccept()
        {
            if (PinBox.Password == TechnicianPin)
            {
                DialogResult = true;
                Close();
                return;
            }

            ErrorText.Text = "Λάθος κωδικός.";
            PinBox.Password = "";
            PinBox.Focus();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
