using System.Collections.Generic;
using System.Windows;

namespace CatalogBridge
{
    // One product about to be pushed, with the name it will carry on PointToOrder. The POS
    // name is an abbreviated ALL-CAPS code («ΚΟΤ ΦΙΛΕΤΟ ΠΟΥΡΕ»); PointToOrder is what the
    // customer reads, so the display name is editable and pre-filled with the POS name.
    public class PushPlanItem
    {
        public OrexsysProduct Product { get; init; } = null!;
        public string DisplayName { get; set; } = "";

        public string Code => Product.Code;
        public string OrexsysName => Product.Name;
        public string PriceText => Product.Price is decimal p ? p.ToString("0.00") : "0.00";

        // What actually gets sent: the edited name, falling back to the POS name if the
        // field was emptied.
        public string FinalName =>
            string.IsNullOrWhiteSpace(DisplayName) ? Product.Name : DisplayName.Trim();
    }

    // A category that does not exist on PointToOrder yet. The user chooses what it will be
    // called there — e.g. «OREXSYS» — before anything is created.
    public class CategoryPlan
    {
        public string OrexsysName { get; init; } = "";
        public string TargetName { get; set; } = "";

        public string FinalName =>
            string.IsNullOrWhiteSpace(TargetName) ? OrexsysName : TargetName.Trim();
    }

    // The review step before a push: nothing reaches PointToOrder until «Αποστολή» here.
    public partial class PushPreviewWindow : Window
    {
        public PushPreviewWindow(
            List<PushPlanItem> items, List<CategoryPlan> newCategories, bool makeVisible,
            string extraSummary = "")
        {
            InitializeComponent();

            ProductsGrid.ItemsSource = items;

            if (newCategories.Count > 0)
                CategoriesList.ItemsSource = newCategories;
            else
                CategoriesCard.Visibility = Visibility.Collapsed;

            SummaryText.Text =
                $"{items.Count} προϊόν(τα)" +
                (newCategories.Count > 0 ? $", {newCategories.Count} νέα κατηγορία(ες)" : "") +
                (makeVisible ? " — θα γίνουν ΑΜΕΣΩΣ ορατά στους πελάτες."
                             : " — θα δημιουργηθούν κρυφά.") +
                extraSummary;
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            // A cell still in edit mode would otherwise lose its value on close.
            ProductsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            ProductsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
