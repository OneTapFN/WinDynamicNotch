using System;
using System.IO;
using System.Threading;
using System.Windows;
using WinDynamicNotch.Tray;

namespace WinDynamicNotch;

public partial class App : Application
{
    private const string MutexName = @"Local\WinDynamicNotch.SingleInstance";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private TrayIconManager? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);

        _window = new MainWindow();
        MainWindow = _window;
        _window.Show();

        _tray = new TrayIconManager(ToggleWindow, Shutdown);
    }

    private void ToggleWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsVisible)
        {
            _window.Hide();
        }
        else
        {
            _window.Show();
            _window.Activate();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _tray = null;

        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch
            {
                // ignored
            }
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void Log(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinDynamicNotch");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // ignored
        }
    }
}
