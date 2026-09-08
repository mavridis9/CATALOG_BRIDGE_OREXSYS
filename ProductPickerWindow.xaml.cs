using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace CatalogBridge
{
    // Modal picker for one Orexsys product, used when creating or repairing an EFIC
    // correlation. Double-click selects, same as pressing Επιλογή.
    public partial class ProductPickerWindow : Window
    {
        public class PickerItem
        {
            public OrexsysProduct Product { get; init; } = null!;
            public string Code => Product.Code;
            public string Name => Product.Name;
            public string CategoryName => Product.CategoryName;
            public string PriceText => Product.Price is decimal p ? p.ToString("0.00") : "—";
        }

        private readonly List<PickerItem> allItems;

        public OrexsysProduct? SelectedProduct { get; private set; }

        public ProductPickerWindow(IEnumerable<OrexsysProduct> products, string prompt)
        {
            InitializeComponent();

            PromptText.Text = prompt;
            allItems = products.Select(p => new PickerItem { Product = p }).ToList();
            ProductsGrid.ItemsSource = allItems;
            SearchBox.Focus();
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string search = CatalogDiff.Normalise(SearchBox.Text);

            ProductsGrid.ItemsSource = search.Length == 0
                ? allItems
                : allItems.Where(i =>
                    CatalogDiff.Normalise(i.Name).Contains(search) ||
                    CatalogDiff.Normalise(i.Code).Contains(search)).ToList();
        }

        private void ProductsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Confirm();

        private void OkButton_Click(object sender, RoutedEventArgs e) => Confirm();

        private void Confirm()
        {
            if (ProductsGrid.SelectedItem is not PickerItem item)
            {
                MessageBox.Show("Επιλέξτε πρώτα ένα προϊόν.", "Επιλογή προϊόντος",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SelectedProduct = item.Product;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
