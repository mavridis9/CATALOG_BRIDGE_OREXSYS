using System.Windows;

namespace CatalogBridge
{
    // Asks for the card of a customer that has none.
    public partial class CardWindow : Window
    {
        public string Card { get; private set; } = "";

        public CardWindow(string customerName, bool createsOrexsysCustomer)
        {
            InitializeComponent();
            TitleText.Text = $"Κάρτα για «{customerName}»";
            HintText.Text = createsOrexsysCustomer
                ? "Ο πελάτης υπάρχει μόνο στο PointToOrder. Με την κάρτα δημιουργείται και στο Orexsys, με το όνομα, " +
                  "το e-mail και το κινητό του λογαριασμού του, ώστε οι παραγγελίες του να ταιριάζουν από εδώ και πέρα."
                : "Καταχωρείται στο Orexsys (CARDNO) και, αν γίνεται, στον λογαριασμό του στο PointToOrder.";
            Loaded += (s, e) => CardBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            string? problem = CustomerDirectory.ValidateCard(CardBox.Text);
            if (problem != null) { ErrorText.Text = problem; return; }
            Card = CardBox.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
