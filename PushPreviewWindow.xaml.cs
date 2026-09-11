using System.Collections.Generic;
using System.Linq;
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

        // The size this item stands for (ITMV; 0 = the product itself) and the price that
        // goes with it - one plan item per size when sizes are sent as separate products.
        public long ItmvId { get; init; }
        public string SizeName { get; init; } = "";
        public decimal Price { get; init; }

        // Size option group to add under the offer (SizeMode.OptionGroup); empty otherwise.
        public List<PushOption> Options { get; init; } = new();

        public static PushPlanItem From(PushUnit unit) => new()
        {
            Product = unit.Product, ItmvId = unit.ItmvId, SizeName = unit.SizeName,
            Price = unit.Price, DisplayName = unit.DefaultName, Options = unit.Options,
        };

        // «Κανονικό +0,00 · XL +3,00» - what the customer will be asked to choose.
        public string OptionsText => Options.Count == 0 ? "" :
            string.Join(" · ", Options.Select(o => $"{o.Name} +{o.Surcharge:0.00}"));

        public string Code => Product.Code;
        public string OrexsysName => SizeName.Length > 0 ? $"{Product.Name} ({SizeName})" : Product.Name;
        public string PriceText => Price.ToString("0.00");

        // What actually gets sent: the edited name, falling back to the POS name (plus size)
        // if the field was emptied.
        public string FinalName =>
            string.IsNullOrWhiteSpace(DisplayName)
                ? (SizeName.Length > 0 && ItmvId != 0 ? $"{Product.Name} {SizeName}" : Product.Name)
                : DisplayName.Trim();
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
