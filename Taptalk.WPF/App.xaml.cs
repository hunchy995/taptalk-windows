using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Taptalk.WPF;

public partial class App : Application
{
    private static readonly string LogFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Taptalk", "Logs");
    private static readonly string CrashLogPath = Path.Combine(LogFolder, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);

            // (1) UI-thread exceptions: dispatcher callbacks, event handlers, DispatcherTimer,
            //     and async-void continuations resumed on the UI thread.
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            // (2) Every unhandled managed exception on ANY other thread
            //     (threadpool/Task.Run, NAudio wave thread, finalizer).
            //     Process WILL terminate after this returns — log only.
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // (3) Diagnostic only: does NOT crash the process (since .NET 4.5).
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            // Hook may not be live yet — guard startup itself
            LogFatal("OnStartup", ex);
            Shutdown(-1);
        }
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Process is ALWAYS terminated after this returns. Log synchronously.
        LogFatal("AppDomain.UnhandledException (IsTerminating=" + e.IsTerminating + ")",
            e.ExceptionObject as Exception);
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogFatal("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Recoverable: clipboard busy (CLIPBRD_E_CANT_OPEN 0x800401D0) — retry later, don't die
        if (e.Exception is System.Runtime.InteropServices.COMException comEx &&
            (uint)comEx.ErrorCode == 0x800401D0)
        {
            LogFatal("Recoverable COMException (clipboard blocked)", e.Exception);
            e.Handled = true;
            return;
        }

        // Everything else: log and let it die cleanly rather than limp on in a corrupt state
        LogFatal("DispatcherUnhandledException", e.Exception);
        e.Handled = false;
    }

    private static void LogFatal(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
        }
        catch
        {
            // The crash handler itself must never throw
        }
    }
}
