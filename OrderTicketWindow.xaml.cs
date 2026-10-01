using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CatalogBridge
{
    // The incoming online order, as a kitchen ticket that pops up over everything (owner,
    // 01/10/2026: «accepting the orders should be a pop up when an order comes»). One ticket at a
    // time; SyncView queues the rest and shows «1 από 3». The ticket decides nothing itself: it
    // calls back into SyncView, which applies the decision to Orexsys first and PointToOrder
    // second (OrderMirror). «Αργότερα» / Esc closes it; the order stays on Αρχική.
    public partial class OrderTicketWindow : Window
    {
        private static readonly int[] MinuteChoices = { 10, 15, 20, 30, 45, 60 };

        // What the customer reads, by chip. The last one opens a text box.
        private static readonly (string Chip, string Sentence)[] Reasons =
        {
            ("Έχουμε κλείσει", "Το κατάστημα είναι κλειστό αυτή τη στιγμή."),
            ("Τελείωσε κάποιο προϊόν", "Κάποιο από τα προϊόντα της παραγγελίας έχει εξαντληθεί."),
            ("Μεγάλη αναμονή", "Έχουμε πολύ μεγάλη αναμονή. Δοκιμάστε ξανά λίγο αργότερα."),
            ("Δεν κάνουμε διανομή εκεί", "Δεν κάνουμε διανομή σε αυτή την περιοχή."),
            ("Άλλος λόγος", ""),
        };

        private readonly Func<WaitingRow, int, Task<(bool Ok, string Message)>> accept;
        private readonly Func<WaitingRow, string, Task<(bool Ok, string Message)>> reject;
        private int minutes;
        private string reason = "";

        public WaitingRow Row { get; private set; }
        public bool Busy { get; private set; }

        public OrderTicketWindow(WaitingRow row,
            Func<WaitingRow, int, Task<(bool Ok, string Message)>> accept,
            Func<WaitingRow, string, Task<(bool Ok, string Message)>> reject)
        {
            InitializeComponent();
            this.accept = accept;
            this.reject = reject;
            Row = row;
            BuildChips();
            Load(row);
            Loaded += (s, e) => { DrawTeeth(); Enter(); };
            SizeChanged += (s, e) => DrawTeeth();
        }

        // Shows another order in the same ticket (a row clicked on Αρχική while one is open).
        public void Load(WaitingRow row)
        {
            Row = row;
            DataContext = row;
            minutes = row.Minutes > 0 ? row.Minutes : WaitingRow.DefaultMinutes;
            foreach (var chip in MinutesPanel.Children.OfType<RadioButton>())
                chip.IsChecked = (int)chip.Tag == minutes;
            AcceptButton.Content = OrderMirror.AcceptLabel(minutes);
            ShowPanel(DecidePanel);
            ErrorText.Visibility = Visibility.Collapsed;
            KindText.Text = "Νέα online παραγγελία";
        }

        public void SetQueue(int position, int total) =>
            QueueText.Text = total > 1 ? $"{position} από {total}" : "";

        // The order was answered somewhere else (the till's own screen) while this was open.
        public void ShowAnsweredElsewhere(MirrorEvent? verdict)
        {
            if (Busy) return;
            bool accepted = verdict?.Action == MirrorAction.Accept;
            ShowResult(accepted, accepted ? "Δεκτή από το ταμείο" : verdict == null ? "Απαντήθηκε" : "Απορρίφθηκε από το ταμείο",
                       "Απαντήθηκε από την οθόνη online παραγγελιών του Orexsys.");
            CloseSoon(1.6);
        }

        // ---- building ---------------------------------------------------------------------------

        private void BuildChips()
        {
            var chip = (Style)FindResource("Chip");
            var num = (FontFamily)FindResource("NumFont");
            foreach (int m in MinuteChoices)
            {
                var rb = new RadioButton
                {
                    Content = $"{m}′", Tag = m, Style = chip, GroupName = "Minutes", FontFamily = num, FontSize = 16,
                    Margin = new Thickness(0, 0, m == MinuteChoices[^1] ? 0 : 6, 0), Padding = new Thickness(0, 8, 0, 8),
                };
                rb.Checked += (s, e) => { minutes = (int)((RadioButton)s).Tag; AcceptButton.Content = OrderMirror.AcceptLabel(minutes); };
                MinutesPanel.Children.Add(rb);
            }
            foreach (var (label, sentence) in Reasons)
            {
                var rb = new RadioButton { Content = label, Tag = sentence, Style = chip, GroupName = "Reason" };
                rb.Checked += (s, e) => PickReason((string)((RadioButton)s).Tag);
                ReasonsPanel.Children.Add(rb);
            }
        }

        private void PickReason(string sentence)
        {
            bool own = sentence.Length == 0;
            OwnReasonBox.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
            if (own) { OwnReasonBox.Focus(); reason = OwnReasonBox.Text.Trim(); }
            else reason = sentence;
            UpdatePreview();
        }

        private void OwnReasonBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            reason = OwnReasonBox.Text.Trim();
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            ReasonPreview.Text = reason.Length > 0 ? $"Ο πελάτης θα δει: «{reason}»" : "Διαλέξτε λόγο ή γράψτε τον δικό σας.";
            RejectButton.IsEnabled = reason.Length > 0;
        }

        // The torn edge: a row of small triangles across the top, to the ticket's width.
        private void DrawTeeth()
        {
            double width = Root.ActualWidth;
            if (width <= 0) return;
            const double tooth = 12, height = 8;
            var figure = new PathFigure { StartPoint = new Point(0, height), IsClosed = true };
            for (double x = 0; x < width; x += tooth)
            {
                figure.Segments.Add(new LineSegment(new Point(Math.Min(x + tooth / 2, width), 0), true));
                figure.Segments.Add(new LineSegment(new Point(Math.Min(x + tooth, width), height), true));
            }
            figure.Segments.Add(new LineSegment(new Point(width, height + 2), true));
            figure.Segments.Add(new LineSegment(new Point(0, height + 2), true));
            Teeth.Data = new PathGeometry(new[] { figure });
        }

        // One orchestrated moment: the ticket fades in and rises 12 px (220 ms, ease-out).
        // Leaving is instant - the person already decided.
        private void Enter()
        {
            if (!Motion.Enabled) { Root.Opacity = 1; RootY.Y = 0; return; }
            var d = new Duration(TimeSpan.FromMilliseconds(220));
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = Motion.EaseOut });
            RootY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, d) { EasingFunction = Motion.EaseOut });
        }

        private void ShowPanel(FrameworkElement panel)
        {
            DecidePanel.Visibility = panel == DecidePanel ? Visibility.Visible : Visibility.Collapsed;
            RejectPanel.Visibility = panel == RejectPanel ? Visibility.Visible : Visibility.Collapsed;
            ResultPanel.Visibility = panel == ResultPanel ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowWorking(string text)
        {
            ResultBadge.Background = (Brush)FindResource("HoverBg");
            ResultIcon.Text = "";
            ResultIcon.Foreground = (Brush)FindResource("MutedBrush");
            ResultText.Text = text;
            ResultDetail.Text = "";
            ShowPanel(ResultPanel);
        }

        private void ShowResult(bool good, string text, string detail)
        {
            ResultBadge.Background = (Brush)FindResource(good ? "GoodSoft" : "BadSoft");
            ResultIcon.Text = good ? "" : "";
            ResultIcon.Foreground = (Brush)FindResource(good ? "GoodBrush" : "BadBrush");
            ResultText.Text = text;
            ResultDetail.Text = detail;
            ShowPanel(ResultPanel);
        }

        private void CloseSoon(double seconds)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            t.Tick += (s, e) => { t.Stop(); Close(); };
            t.Start();
        }

        // ---- actions ------------------------------------------------------------------------------

        private async void Accept_Click(object sender, RoutedEventArgs e)
        {
            if (Busy || !Row.CanAccept) return;
            Busy = true;
            Row.Minutes = minutes;
            ShowWorking("Αποδοχή...");
            var (ok, message) = await accept(Row, minutes);
            Busy = false;
            if (!ok)
            {
                ShowPanel(DecidePanel);
                ErrorText.Text = message;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            ShowResult(true, $"Δεκτή, έτοιμη στις {DateTime.Now.AddMinutes(minutes):HH:mm}", message);
            CloseSoon(1.4);
        }

        private void RejectOpen_Click(object sender, RoutedEventArgs e)
        {
            if (Busy || !Row.CanReject) return;
            foreach (var rb in ReasonsPanel.Children.OfType<RadioButton>()) rb.IsChecked = false;
            OwnReasonBox.Text = "";
            OwnReasonBox.Visibility = Visibility.Collapsed;
            reason = "";
            UpdatePreview();
            RejectErrorText.Visibility = Visibility.Collapsed;
            KindText.Text = "Απόρριψη παραγγελίας";
            ShowPanel(RejectPanel);
        }

        private void RejectBack_Click(object sender, RoutedEventArgs e)
        {
            KindText.Text = "Νέα online παραγγελία";
            ShowPanel(DecidePanel);
        }

        private async void Reject_Click(object sender, RoutedEventArgs e)
        {
            if (Busy || reason.Length == 0) return;
            Busy = true;
            ShowWorking("Απόρριψη...");
            var (ok, message) = await reject(Row, OrderAcceptance.FitRejectReason(reason));
            Busy = false;
            if (!ok)
            {
                ShowPanel(RejectPanel);
                RejectErrorText.Text = message;
                RejectErrorText.Visibility = Visibility.Visible;
                return;
            }
            ShowResult(false, "Απορρίφθηκε", message);
            CloseSoon(1.4);
        }

        private void Later_Click(object sender, RoutedEventArgs e)
        {
            if (Busy) return;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !Busy)
            {
                if (RejectPanel.Visibility == Visibility.Visible) RejectBack_Click(sender, e);
                else Close();
                e.Handled = true;
            }
        }

        // Dragging the ticket anywhere moves it (it has no title bar).
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.OriginalSource is not (Button or TextBox) && e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        }
    }
}
