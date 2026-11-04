using SoftCal.App.Services;
using SoftCal.Windows.Elevation;
using SoftCal.Windows.Safety;

namespace SoftCal.App;

/// <summary>
/// Entry point. Four invocations never show UI: the elevated helper (<c>--elevated</c>, allowlisted tasks only), the
/// guardian (<c>guardian --parent …</c>), which waits for the app to exit and restores anything it left changed, and
/// SoftCal's calibration loader (<c>--load-calibrations</c>) and its stop request (<c>--stop-loader</c>).
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (ElevatedTasks.TryRunFromCommandLine(args) is { } elevatedExitCode)
        {
            return elevatedExitCode;
        }

        if (args.Length > 0 && args[0] == Guardian.Verb)
        {
            Guardian.Run(Guardian.ParseParent(args), AppSafety.Journal, AppSafety.Restorers, AppSafety.GuardianLog);
            return 0;
        }

        if (args.Length > 0 && args[0] == CalibrationLoaderProcess.Verb)
        {
            return CalibrationLoaderProcess.Run();
        }

        if (args.Length > 0 && args[0] == CalibrationLoaderProcess.StopVerb)
        {
            return CalibrationLoaderProcess.Stop(TimeSpan.FromSeconds(10)) ? 0 : 1;
        }

        // Whatever an earlier run left behind (it crashed, or the machine lost power) is put back first.
        AppSafety.RecoverDeadRuns();

        // An installed calibration's loader runs again if a rebuild (or the user) stopped it.
        CalibrationLoaderProcess.EnsureRunning();

        var app = new App();
        app.InitializeComponent();

        // An error that still escapes a screen's own handling is logged with its stack trace and shown, and the app goes
        // on: each screen puts back what it changed in its own finally. A second one while the first is shown closes the
        // app (the guardian then restores anything left changed).
        var showing = false;
        app.DispatcherUnhandledException += (_, e) =>
        {
            var log = AppPaths.WriteErrorLog(e.Exception);
            if (showing)
            {
                return;
            }

            showing = true;
            try
            {
                System.Windows.MessageBox.Show(
                    $"Something went wrong: {e.Exception.Message}{Environment.NewLine}{Environment.NewLine}The details are in {log}.",
                    "Soft Color Calibrator",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                e.Handled = true;
            }
            finally
            {
                showing = false;
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppPaths.WriteErrorLog(ex);
            }
        };
        return app.Run(new MainWindow());
    }
}
