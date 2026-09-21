using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace CatalogBridge
{
    // «Επαναφορά από την αρχή»: empties the PointToOrder store and the app's EFIC rows so the
    // Συγχρονισμός can send everything again. Reached only from Ρυθμίσεις (PIN); shows exactly
    // what goes before anything is touched, and needs «ΔΙΑΓΡΑΦΗ» typed.
    public partial class ResetWindow : Window
    {
        public class CodeOption : INotifyPropertyChanged
        {
            private bool isSelected;
            public string Code { get; init; } = "";
            public string Label { get; init; } = "";
            public bool CanDelete { get; init; }
            public Brush LabelBrush => CanDelete ? Ink : Muted;
            public bool IsSelected
            {
                get => isSelected;
                set { isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
            }
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private readonly Settings settings;
        private ResetPlan? plan;
        private List<CodeOption> codes = new();
        private bool busy;

        public ResetWindow(Settings settings)
        {
            InitializeComponent();
            this.settings = settings;
            Loaded += async (s, e) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            SetBusy(true);
            try
            {
                if (ResetService.IsProtected(settings.ApiStoreId))
                {
                    P2OCheck.IsChecked = false;
                    P2OCheck.IsEnabled = false;
                    P2OText.Text = $"Κατάστημα {settings.ApiStoreId}: ζωντανό κατάστημα πελάτη — δεν επιτρέπεται επαναφορά.";
                }
                else if (settings.IsApiConfigured)
                {
                    using var api = new PointToOrderApiClient(settings);
                    plan = await ResetService.PlanAsync(api, m => StatusText.Text = m);
                    P2OText.Text =
                        $"PointToOrder, κατάστημα {plan.StoreId}: διαγραφή {plan.Offers.Count} προϊόντων, " +
                        $"{plan.Categories.Count} κατηγοριών" +
                        (plan.OptionGroups.Count > 0
                            ? $" και {plan.OptionGroups.Count} ομάδων επιλογών ({plan.ChoiceCount} επιλογές)"
                            : "") + " — όλο το κατάστημα, όχι μόνο όσα έστειλε το πρόγραμμα.";
                }
                else
                {
                    P2OCheck.IsChecked = false;
                    P2OCheck.IsEnabled = false;
                    P2OText.Text = "PointToOrder: δεν έχει ρυθμιστεί.";
                }

                if (settings.IsDatabaseConfigured)
                {
                    var summary = await EficRepository.LoadCodeSummaryAsync(settings);
                    codes = summary.Select(s => new CodeOption
                    {
                        Code = s.Code,
                        CanDelete = s.Kind != EficRepository.EficCodeKind.OtherPlatform,
                        IsSelected = s.Kind == EficRepository.EficCodeKind.Own,
                        Label = s.Kind switch
                        {
                            EficRepository.EficCodeKind.Own =>
                                $"«{s.Code}» — η «Σύνδεση P2O» αυτής της εγκατάστασης: {s.Rows} γραμμές",
                            EficRepository.EficCodeKind.OtherPointToOrder =>
                                $"«{s.Code}» — άλλη σύνδεση PointToOrder ({s.EfstName}): {s.Rows} γραμμές",
                            EficRepository.EficCodeKind.NoEfstLine =>
                                $"«{s.Code}» — χωρίς γραμμή στο EFST (παλιές δοκιμές): {s.Rows} γραμμές",
                            _ => $"«{s.Code}» — {(s.EfstName.Length > 0 ? s.EfstName : "άλλη πλατφόρμα")}: " +
                                 $"{s.Rows} γραμμές — δεν αγγίζεται ποτέ",
                        },
                    }).ToList();
                    CodeList.ItemsSource = codes;
                }

                StatusText.Text = "";
            }
            catch (Exception ex)
            {
                StatusText.Foreground = Bad;
                StatusText.Text = "Η ανάγνωση απέτυχε: " + ex.Message;
                plan = null;
                P2OCheck.IsChecked = false;
                P2OCheck.IsEnabled = false;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void Selection_Changed(object sender, RoutedEventArgs e) => UpdateButton();

        private void ConfirmBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateButton();

        private bool Confirmed =>
            CatalogDiff.Normalise(ConfirmBox.Text) == CatalogDiff.Normalise("ΔΙΑΓΡΑΦΗ");

        private void UpdateButton()
        {
            if (ResetButton == null || ConfirmBox == null) return;
            bool anything = (P2OCheck.IsChecked == true && plan != null) || codes.Any(c => c.IsSelected && c.CanDelete);
            ResetButton.IsEnabled = !busy && anything && Confirmed;
        }

        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedCodes = codes.Where(c => c.IsSelected && c.CanDelete).Select(c => c.Code).ToList();
            bool doP2O = P2OCheck.IsChecked == true && plan != null;

            var sure = MessageBox.Show(
                (doP2O ? $"Θα διαγραφεί ΟΛΟ το κατάστημα PointToOrder {plan!.StoreId}.\n" : "") +
                (selectedCodes.Count > 0 ? $"Θα διαγραφούν οι αντιστοιχίσεις EFIC: {string.Join(", ", selectedCodes)}.\n" : "") +
                "\nΣυνέχεια;", "Επαναφορά", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (sure != MessageBoxResult.Yes) return;

            SetBusy(true);
            var log = new List<string>();
            void Log(string line) { log.Add(line); LogText.Text = string.Join("\n", log); }

            try
            {
                if (doP2O)
                {
                    using var api = new PointToOrderApiClient(settings);
                    string backup = ResetService.SaveBackup(plan!);
                    Log($"✓ Αντίγραφο του καταλόγου: {backup}");

                    var failures = await ResetService.ExecuteAsync(api, plan!, m => StatusText.Text = m);
                    var after = await api.GetCatalogAsync();
                    Log(failures.Count == 0
                        ? $"✓ PointToOrder: διαγράφηκαν {plan!.Offers.Count} προϊόντα και {plan.Categories.Count} κατηγορίες."
                        : $"⚠ PointToOrder: {failures.Count} σφάλματα — {string.Join("; ", failures.Take(4))}");
                    Log($"   Απομένουν στο κατάστημα: {after.Offers.Count} προϊόντα, {after.Categories.Count} κατηγορίες.");
                }

                if (selectedCodes.Count > 0)
                {
                    int removed = await EficRepository.DeleteAllUnderCodesAsync(settings, selectedCodes);
                    Log($"✓ EFIC: διαγράφηκαν {removed} γραμμές ({string.Join(", ", selectedCodes)}). " +
                        "Αντίγραφο του πίνακα στον φάκελο backups.");
                }

                StatusText.Foreground = Good;
                StatusText.Text = "Η επαναφορά ολοκληρώθηκε — πατήστε Συγχρονισμός για να σταλούν όλα ξανά.";
                ConfirmBox.Text = "";
                plan = null;
                P2OCheck.IsChecked = false;
                P2OCheck.IsEnabled = false;
            }
            catch (Exception ex)
            {
                StatusText.Foreground = Bad;
                StatusText.Text = "Η επαναφορά σταμάτησε: " + ex.Message;
                Log("⛔ " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void SetBusy(bool value)
        {
            busy = value;
            CloseButton.IsEnabled = !value;
            Cursor = value ? System.Windows.Input.Cursors.Wait : null;
            UpdateButton();
        }

        private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x2F, 0x6C, 0x43));
        private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xA6, 0x3A, 0x22));
    }
}
