using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Win32;

namespace CatalogBridge
{
    public partial class SettingsWindow : Window
    {
        private class PriceListOption
        {
            public long Id { get; init; }
            public string Label { get; init; } = "";
        }

        private class PriceSourceOption
        {
            public PriceSource Value { get; init; }
            public string Label { get; init; } = "";
            public override string ToString() => Label;
        }

        public SettingsWindow()
        {
            InitializeComponent();
            LoadIntoForm();
        }

        private void LoadIntoForm()
        {
            var settings = Settings.Load();

            ServerBox.Text = settings.Server;
            PortBox.Text = settings.Port;
            DatabaseBox.Text = settings.Database;
            UserBox.Text = settings.User;
            PasswordBox.Password = settings.Password;

            SelectComboText(CharsetCombo, settings.Charset);

            ApiUrlBox.Text = settings.ApiBaseUrl;
            ApiUserBox.Text = settings.ApiUsername;
            ApiPasswordBox.Password = settings.ApiPassword;
            ApiStoreBox.Text = settings.ApiStoreId;

            PriceSourceCombo.ItemsSource = new List<PriceSourceOption>
            {
                new() { Value = PriceSource.Delivery, Label = "Delivery (DELIVERYPRICE)" },
                new() { Value = PriceSource.TakeAway, Label = "Take away (TAKEAWAYPRICE)" },
                new() { Value = PriceSource.Restaurant, Label = "Καταστήματος (RESTPRICE)" },
            };
            PriceSourceCombo.SelectedIndex = settings.PriceSource switch
            {
                PriceSource.TakeAway => 1,
                PriceSource.Restaurant => 2,
                _ => 0,
            };

            // The price lists can only be read once the database details are valid, so the combo
            // starts holding just the saved id and is filled in properly by "Έλεγχος βάσης".
            PriceListCombo.ItemsSource = new List<PriceListOption>
            {
                new() { Id = settings.PriceListId, Label = $"#{settings.PriceListId}" },
            };
            PriceListCombo.SelectedIndex = 0;

            EficStoreBox.Text = settings.EficStoreCode;

            StockSyncCheck.IsChecked = settings.StockSyncEnabled;
            ShowDashboardCheck.IsChecked = settings.ShowDashboard;
            ShowCompareCheck.IsChecked = settings.ShowCompare;
            ShowPushCheck.IsChecked = settings.ShowPush;
            ShowEficCheck.IsChecked = settings.ShowEfic;
        }

        private static void SelectComboText(ComboBox combo, string value)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            // An unusual charset from a hand-edited ini is kept rather than silently replaced.
            combo.Items.Add(new ComboBoxItem { Content = value });
            combo.SelectedIndex = combo.Items.Count - 1;
        }

        private Settings BuildFromForm() => new()
        {
            Server = ServerBox.Text.Trim(),
            Port = PortBox.Text.Trim(),
            Database = DatabaseBox.Text.Trim(),
            User = UserBox.Text.Trim(),
            Password = PasswordBox.Password,
            Charset = (CharsetCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "WIN1253",
            ApiBaseUrl = ApiUrlBox.Text.Trim(),
            ApiUsername = ApiUserBox.Text.Trim(),
            ApiPassword = ApiPasswordBox.Password,
            ApiStoreId = ApiStoreBox.Text.Trim(),
            PriceListId = (PriceListCombo.SelectedItem as PriceListOption)?.Id ?? 1,
            PriceSource = (PriceSourceCombo.SelectedItem as PriceSourceOption)?.Value ?? PriceSource.Delivery,
            EficStoreCode = EficStoreBox.Text.Trim(),
            StockSyncEnabled = StockSyncCheck.IsChecked == true,
            ShowDashboard = ShowDashboardCheck.IsChecked == true,
            ShowCompare = ShowCompareCheck.IsChecked == true,
            ShowPush = ShowPushCheck.IsChecked == true,
            ShowEfic = ShowEficCheck.IsChecked == true,
        };

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Επιλογή βάσης Orexsys",
                Filter = "Βάση Firebird (*.fdb)|*.fdb|Όλα τα αρχεία (*.*)|*.*",
            };

            if (dialog.ShowDialog() == true)
                DatabaseBox.Text = dialog.FileName;
        }

        private async void TestDbButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = BuildFromForm();

            if (!settings.IsDatabaseConfigured)
            {
                ShowStatus("Συμπληρώστε διακομιστή και αρχείο βάσης.", Warn);
                return;
            }

            TestDbButton.IsEnabled = false;
            ShowStatus("Έλεγχος σύνδεσης με τη βάση...", Normal);

            try
            {
                int productCount = await System.Threading.Tasks.Task.Run(() =>
                {
                    using var connection = new FbConnection(settings.ToConnectionString());
                    connection.Open();

                    using var command = new FbCommand("SELECT COUNT(*) FROM ITMS", connection);
                    return Convert.ToInt32(command.ExecuteScalar());
                });

                var lists = await OrexsysCatalog.LoadPriceListsAsync(settings);

                if (lists.Count > 0)
                {
                    long previous = (PriceListCombo.SelectedItem as PriceListOption)?.Id ?? 1;

                    var options = lists
                        .Select(l => new PriceListOption
                        {
                            Id = l.Id,
                            Label = string.IsNullOrWhiteSpace(l.Name) ? $"#{l.Id}" : $"#{l.Id} — {l.Name}",
                        })
                        .ToList();

                    PriceListCombo.ItemsSource = options;
                    PriceListCombo.SelectedItem =
                        options.FirstOrDefault(o => o.Id == previous) ?? options[0];
                }

                ShowStatus($"Επιτυχής σύνδεση. {productCount} προϊόντα, " +
                           $"{lists.Count} τιμοκατάλογος/οι.", Good);
            }
            catch (Exception ex)
            {
                ShowStatus("Η σύνδεση με τη βάση απέτυχε:\n" + ex.Message, Bad);
            }
            finally
            {
                TestDbButton.IsEnabled = true;
            }
        }

        private async void TestApiButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = BuildFromForm();

            if (!settings.IsApiConfigured)
            {
                ShowStatus("Συμπληρώστε διεύθυνση, χρήστη, κωδικό και κωδικό καταστήματος.", Warn);
                return;
            }

            TestApiButton.IsEnabled = false;
            ShowStatus("Έλεγχος σύνδεσης με το PointToOrder...", Normal);

            try
            {
                using var api = new PointToOrderApiClient(settings);
                var user = await api.TestConnectionAsync();

                string stores = user.StoreIds.Count > 0
                    ? string.Join(", ", user.StoreIds)
                    : "(κανένα)";

                // Pointing the app at a store the account cannot see is the single most likely
                // configuration mistake, and it fails later as "nothing matched" rather than as
                // an error, so it is called out here instead.
                bool storeVisible = user.StoreIds.Contains(settings.ApiStoreId);

                if (storeVisible)
                {
                    ShowStatus($"Επιτυχής σύνδεση ως {user.Username}. Καταστήματα: {stores}.", Good);
                }
                else
                {
                    ShowStatus($"Σύνδεση επιτυχής ως {user.Username}, αλλά το κατάστημα " +
                               $"«{settings.ApiStoreId}» δεν είναι στη λίστα: {stores}.", Warn);
                }
            }
            catch (Exception ex)
            {
                ShowStatus("Η σύνδεση με το PointToOrder απέτυχε:\n" + ex.Message, Bad);
            }
            finally
            {
                TestApiButton.IsEnabled = true;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Settings.Save(BuildFromForm());
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowStatus("Η αποθήκευση απέτυχε:\n" + ex.Message, Bad);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

        private static readonly Brush Normal = Brushes.Black;
        private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x2F, 0x6C, 0x43));
        private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0x8A, 0x5A, 0x1B));
        private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xA6, 0x3A, 0x22));

        private void ShowStatus(string message, Brush colour)
        {
            StatusText.Text = message;
            StatusText.Foreground = colour;
        }
    }
}
