using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace CatalogBridge
{
    // Technician dialog (opened from Ρυθμίσεις) that chooses which Orexsys categories the sync
    // may send. Reads the categories with the settings currently typed in the form, so a
    // changed price list shows its effect on the counts before anything is saved.
    public partial class CategoryPickerWindow : Window
    {
        public class Choice : INotifyPropertyChanged
        {
            private bool isSelected;

            public OrexsysCategory Category { get; init; } = null!;
            public string Name => Category.Name;

            public bool IsSelected
            {
                get => isSelected;
                set
                {
                    if (isSelected == value) return;
                    isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public string CountsText => Category.Priced > 0
                ? $"{Category.Priced} με τιμή · {Category.Total} σύνολο"
                : $"κανένα με τιμή σε αυτόν τον τιμοκατάλογο · {Category.Total} σύνολο";

            public Brush CountsBrush => Category.Priced > 0 ? MutedBrush : WarnBrush;

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));

        private readonly Settings settings;
        private readonly bool initialAll;
        private readonly HashSet<long> initialSelected;
        private List<Choice> choices = new();

        public bool AllSelected { get; private set; }
        public HashSet<long> SelectedIds { get; private set; } = new();

        public CategoryPickerWindow(Settings settings, bool allSelected, HashSet<long> selected)
        {
            InitializeComponent();

            this.settings = settings;
            initialAll = allSelected;
            initialSelected = selected;

            Loaded += async (s, e) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            try
            {
                var categories = await OrexsysCategories.LoadAsync(settings);

                choices = categories
                    .Select(c => new Choice
                    {
                        Category = c,
                        IsSelected = initialAll || initialSelected.Contains(c.Id),
                    })
                    .ToList();

                CategoryList.ItemsSource = choices;
                OkButton.IsEnabled = true;

                int unpriced = categories.Count(c => c.Priced == 0);
                StatusText.Text =
                    $"{categories.Count} κατηγορίες · τιμοκατάλογος #{settings.PriceListId}, " +
                    $"τιμή {settings.PriceSourceLabel}" +
                    (unpriced > 0
                        ? $" · {unpriced} χωρίς τιμές εδώ — ίσως θέλουν άλλο τιμοκατάλογο."
                        : ".");
            }
            catch (Exception ex)
            {
                StatusText.Text = "Δεν ήταν δυνατή η ανάγνωση των κατηγοριών:\n" + ex.Message;
            }
        }

        private void AllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var choice in choices) choice.IsSelected = true;
        }

        private void NoneButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var choice in choices) choice.IsSelected = false;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            AllSelected = choices.All(c => c.IsSelected);
            SelectedIds = choices.Where(c => c.IsSelected).Select(c => c.Category.Id).ToHashSet();

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
