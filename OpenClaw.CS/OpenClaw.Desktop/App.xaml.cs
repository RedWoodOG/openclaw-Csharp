using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace OpenClaw.Desktop;

/// <summary>
/// Application entry point.
/// </summary>
public partial class App : Application
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenClaw", "startup.log");

    public App()
    {
        UnhandledException += (s, e) =>
        {
            Log($"UnhandledException: {e.Exception}");
            e.Handled = true;
        };

        try
        {
            Log("App constructor starting...");
            InitializeComponent();
            Log("InitializeComponent completed.");
        }
        catch (Exception ex)
        {
            Log($"App constructor error: {ex}");
            throw;
        }
    }
    
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Log("OnLaunched starting...");
            var window = new MainWindow();
            Log("MainWindow created.");
            window.Activate();
            Log("Window activated.");
        }
        catch (Exception ex)
        {
            Log($"OnLaunched error: {ex}");
            throw;
        }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFile);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);
            File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
