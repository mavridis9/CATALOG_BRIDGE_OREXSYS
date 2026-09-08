using System;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace CatalogBridge
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Orexsys databases are WIN1253. .NET 6 doesn't ship the legacy codepages, and
            // without this FbConnection.Open() fails outright with "Invalid character set
            // specified." rather than merely returning mangled Greek.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            base.OnStartup(e);
        }

        // A crash inside an async handler would otherwise take the window down with no message
        // at all, which on a till PC means "the app just disappeared". The full detail goes to
        // crash.log next to the exe - "Exception has been thrown by the target of an
        // invocation" on its own has no diagnostic value.
        private void Application_DispatcherUnhandledException(
            object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Exception root = e.Exception;
            while (root.InnerException != null) root = root.InnerException;

            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{e.Exception}\n\n");
            }
            catch (Exception)
            {
                // Nowhere to log to - the dialog below still carries the root message.
            }

            MessageBox.Show(
                "Παρουσιάστηκε σφάλμα:\n\n" + root.Message +
                "\n\n(Λεπτομέρειες στο crash.log δίπλα στο πρόγραμμα.)",
                "Γέφυρα Καταλόγου", MessageBoxButton.OK, MessageBoxImage.Error);

            e.Handled = true;
        }
    }
}
