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
    // may send AND, per category, which price list its prices come from (owner, 29/09/2026:
    // one database can price different categories in different lists). Reads with the settings
    // currently typed in the form, so nothing has to be saved first.
    public partial class CategoryPickerWindow : Window
    {
        public class PriceListOption
        {
            public long Id { get; init; }
            public string Name { get; init; } = "";
            public int Priced { get; init; }
            public string Label => $"{Name} · {(Priced > 0 ? Priced + " με τιμή" : "καμία τιμή")}";
        }

        public class Choice : INotifyPropertyChanged
        {
            private bool isSelected;
            private PriceListOption? priceList;

            public OrexsysCategory Category { get; init; } = null!;
            public string Name => Category.Name;
            public List<PriceListOption> PriceLists { get; init; } = new();

            public bool IsSelected
            {
                get => isSelected;
                set
                {
                    if (isSelected == value) return;
                    isSelected = value;
                    Raise(nameof(IsSelected));
                }
            }

            public PriceListOption? PriceList
            {
                get => priceList;
                set
                {
                    if (ReferenceEquals(priceList, value)) return;
                    priceList = value;
                    Raise(nameof(PriceList));
                    Raise(nameof(CountsText));
                    Raise(nameof(CountsBrush));
                }
            }

            private int Priced => PriceList?.Priced ?? 0;

            public string CountsText => Priced > 0
                ? $"{Priced} με τιμή · {Category.Total} σύνολο"
                : $"κανένα με τιμή · {Category.Total} σύνολο";

            public Brush CountsBrush => Priced > 0 ? MutedBrush : WarnBrush;

            public event PropertyChangedEventHandler? PropertyChanged;
            private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));

        private readonly Settings settings;
        private readonly bool initialAll;
        private readonly HashSet<long> initialSelected;
        private readonly Dictionary<long, long> initialPriceLists;
        private List<Choice> choices = new();

        public bool AllSelected { get; private set; }
        public HashSet<long> SelectedIds { get; private set; } = new();

        // Category → price list, only for the categories whose list is NOT the default one.
        public Dictionary<long, long> PriceLists { get; private set; } = new();

        public CategoryPickerWindow(Settings settings, bool allSelected, HashSet<long> selected, Dictionary<long, long> priceLists)
        {
            InitializeComponent();

            this.settings = settings;
            initialAll = allSelected;
            initialSelected = selected;
            initialPriceLists = priceLists;

            Loaded += async (s, e) => await LoadAsync();
        }

        // Which list a category starts on: the one saved for it; else the default list if the
        // category has prices there; else the list it IS priced in (so a ΣΤΟΑ category opens on
        // «ΣΤΟΑ» by itself instead of showing «κανένα με τιμή»).
        public static long InitialPriceList(OrexsysCategory category, IReadOnlyDictionary<long, long> saved, long defaultList)
        {
            if (saved.TryGetValue(category.Id, out long chosen)) return chosen;
            if (category.PricedIn(defaultList) > 0) return defaultList;
            long best = category.BestPriceList;
            return best > 0 ? best : defaultList;
        }

        private async Task LoadAsync()
        {
            try
            {
                var categories = await OrexsysCategories.LoadAsync(settings);
                var lists = await OrexsysCatalog.LoadPriceListsAsync(settings);
                if (lists.Count == 0) lists.Add((settings.PriceListId, $"#{settings.PriceListId}"));

                int moved = 0;
                choices = categories.Select(c =>
                {
                    var options = lists.Select(l => new PriceListOption
                    {
                        Id = l.Id,
                        Name = l.Name.Length > 0 ? l.Name : $"#{l.Id}",
                        Priced = c.PricedIn(l.Id),
                    }).ToList();
                    long start = InitialPriceList(c, initialPriceLists, settings.PriceListId);
                    if (start != settings.PriceListId && !initialPriceLists.ContainsKey(c.Id)) moved++;
                    return new Choice
                    {
                        Category = c,
                        PriceLists = options,
                        IsSelected = initialAll || initialSelected.Contains(c.Id),
                        PriceList = options.FirstOrDefault(o => o.Id == start) ?? options[0],
                    };
                }).ToList();

                CategoryList.ItemsSource = choices;
                OkButton.IsEnabled = true;

                int unpriced = choices.Count(c => (c.PriceList?.Priced ?? 0) == 0);
                StatusText.Text =
                    $"{categories.Count} κατηγορίες · {lists.Count} τιμοκατάλογοι · τιμή {settings.PriceSourceLabel}" +
                    (moved > 0 ? $" · σε {moved} προτείνεται ο τιμοκατάλογος που έχει τις τιμές τους" : "") +
                    (unpriced > 0 ? $" · {unpriced} χωρίς τιμή σε κανέναν." : ".");
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
            PriceLists = choices
                .Where(c => c.PriceList != null && c.PriceList.Id != settings.PriceListId)
                .ToDictionary(c => c.Category.Id, c => c.PriceList!.Id);

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
