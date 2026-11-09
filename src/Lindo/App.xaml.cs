using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Lindo.Services;

namespace Lindo
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log.Write("Errore fatale: " + a.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (s, a) => { Log.Write("Errore attività: " + a.Exception); a.SetObserved(); };

            var args = e.Args ?? new string[0];
            var selftest = Array.FindIndex(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
            if (selftest >= 0)
            {
                var output = selftest + 1 < args.Length ? args[selftest + 1] : "lindo-selftest.txt";
                var code = SelfTest.Run(output);
                Shutdown(code);
                return;
            }

            string Arg(string name)
            {
                var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            string[] List(string name) => (Arg(name) ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            if (Arg("--safety-test") != null)
            {
                Shutdown(SelfTest.SafetyTest(Arg("--safety-test"), List("--expect"), int.TryParse(Arg("--recent-days"), out var d) ? d : 90));
                return;
            }
            if (Arg("--test-uninstall") != null)
            {
                Shutdown(SelfTest.UninstallTest(Arg("--test-uninstall"), List("--apps"), List("--must-survive")));
                return;
            }

            var window = new MainWindow();
            var shot = Array.FindIndex(args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
            if (shot >= 0) window.ScreenshotFolder = shot + 1 < args.Length ? args[shot + 1] : ".";
            MainWindow = window;
            window.Show();
        }

        private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Write("Errore interfaccia: " + e.Exception);
            MessageBox.Show("Si è verificato un errore imprevisto:\n\n" + e.Exception.Message +
                            "\n\nDettagli nel file:\n" + Log.FilePath, "Lindo", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
