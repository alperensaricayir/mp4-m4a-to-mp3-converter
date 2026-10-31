using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace Mp3Converter;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static string CrashLogPath { get; } =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UI thread exceptions: show and keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(args.Exception.Message, "Unexpected Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Background thread exceptions: log before the process terminates.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrashLog("AppDomain.UnhandledException", args.ExceptionObject as Exception);
            try
            {
                MessageBox.Show(
                    $"The application hit a fatal error and must close.\n\nDetails were written to:\n{CrashLogPath}",
                    "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        };

        // Faulted tasks nobody awaited: log and mark observed so they never escalate.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
