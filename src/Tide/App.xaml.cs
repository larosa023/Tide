using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Tide.Core;
using Tide.ViewModels;
using Forms = System.Windows.Forms;

namespace Tide;

public partial class App : Application
{
    private const string MutexName = "Local\\Tide-SingleInstance-7b1c";
    private const string ShowEventName = "Local\\Tide-Show-7b1c";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private EtwNetworkMonitor? _monitor;
    private AppSettings _settings = new();
    private bool _exiting;
    private bool _trayHintShown;
    private string _balloonTarget = "Alerts";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.Ensure();

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Write("Fatal: " + args.ExceptionObject);
            _monitor?.Dispose();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("Task: " + args.Exception);
            args.SetObserved();
        };

        bool updated = e.Args.Contains("--updated");
        bool autostart = e.Args.Contains("--autostart") || e.Args.Contains("--minimized");

        // ---- single instance ------------------------------------------------------------------
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            if (updated)
            {
                // The previous version is shutting down after installing this one.
                try { _ownsMutex = _mutex.WaitOne(TimeSpan.FromSeconds(20)); }
                catch (AbandonedMutexException) { _ownsMutex = true; }
            }
            if (!_ownsMutex)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (_exiting) return;
                Dispatcher.BeginInvoke(() => ShowMain(null));
            }
        }) { IsBackground = true, Name = "Tide show listener" };
        listener.Start();

        Task.Run(Updater.CleanUp);

        // ---- services --------------------------------------------------------------------------
        _settings = SettingsStore.Load();
        ApplyTheme(_settings.Theme);
        HistoryStore.Prune(_settings.HistoryRetentionDays);

        var history = new HistoryStore();
        var tracker = new TrafficTracker(history);
        var firewall = new FirewallManager();
        var alerts = new AlertEngine(_settings);

        string monitorError = "";
        _monitor = new EtwNetworkMonitor(tracker) { IgnoreLoopback = _settings.IgnoreLoopback };
        try
        {
            _monitor.Start();
        }
        catch (Exception ex)
        {
            Log.Write("Monitor start: " + ex);
            monitorError = $"Network monitoring couldn't start: {ex.Message}";
        }

        _vm = new MainViewModel(_settings, tracker, history, firewall, alerts) { MonitorError = monitorError };
        _monitor.Faulted += msg => Dispatcher.BeginInvoke(() =>
        {
            if (!_exiting && _vm != null) _vm.MonitorError = $"Network monitoring stopped unexpectedly ({msg}). Restart Tide to resume.";
        });
        _vm.ThemeRequested += ApplyTheme;
        _vm.LoopbackChanged += v => { if (_monitor != null) _monitor.IgnoreLoopback = v; };
        _vm.NotificationRequested += (title, msg) => Notify(title, msg, title.Contains("update") ? "Settings" : "Alerts");
        _vm.RestartRequested += () => ExitApp();
        _vm.PropertyChanged += OnVmPropertyChanged;

        Task.Run(firewall.Refresh);

        // Keep the setting honest if the user deleted the scheduled task by hand.
        Task.Run(() =>
        {
            bool enabled = StartupManager.IsEnabled();
            if (enabled != _settings.StartWithWindows)
                Dispatcher.BeginInvoke(() => { _settings.StartWithWindows = enabled; SettingsStore.Save(_settings); });
        });

        CreateTray();

        _window = new MainWindow { DataContext = _vm };
        _window.ShouldHideOnClose = () =>
        {
            if (_exiting || !_settings.CloseToTray) return false;
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                Notify("Tide is still running", "It keeps monitoring from the system tray. Right-click the tray icon to exit.", "Live");
            }
            return true;
        };
        _window.ExitRequested += () => ExitApp();
        MainWindow = _window;

        if (!(autostart && _settings.StartMinimized))
            _window.Show();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("UI: " + e.Exception);
        e.Handled = true;
        if (!_exiting)
            MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to {AppPaths.LogFile}",
                "Tide", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ---- theme ------------------------------------------------------------------------------

    public void ApplyTheme(string name)
    {
        if (name is not ("Tide" or "Dark" or "Dusk" or "Light")) name = "Tide";
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{name}.xaml", UriKind.Absolute) };
        Resources.MergedDictionaries[0] = dict;
    }

    // ---- tray -------------------------------------------------------------------------------

    private void CreateTray()
    {
        System.Drawing.Icon icon;
        var res = GetResourceStream(new Uri("pack://application:,,,/Assets/tide.ico"));
        using (var s = res!.Stream)
            icon = new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);

        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Open Tide", null, (_, _) => ShowMain("Live"));
        menu.Items.Add("History", null, (_, _) => ShowMain("History"));
        menu.Items.Add("Blocked apps", null, (_, _) => ShowMain("Firewall"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Tide",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) ShowMain(null); };
        _tray.BalloonTipClicked += (_, _) => ShowMain(_balloonTarget);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_tray == null || _vm == null) return;
        if (e.PropertyName is nameof(MainViewModel.RateDown) or nameof(MainViewModel.RateUp))
        {
            var text = $"Tide\n↓ {Format.Rate(_vm.RateDown)}   ↑ {Format.Rate(_vm.RateUp)}";
            _tray.Text = text.Length > 63 ? text[..63] : text;
        }
    }

    private void Notify(string title, string message, string target)
    {
        if (_tray == null) return;
        _balloonTarget = target;
        _tray.ShowBalloonTip(6000, title, message, Forms.ToolTipIcon.None);
    }

    private void ShowMain(string? page)
    {
        if (_window == null || _exiting) return;
        if (page != null && _vm != null) _vm.Page = page;
        _window.ShowFromTray();
    }

    // ---- exit -------------------------------------------------------------------------------

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            _vm?.Stop();
            _vm?.Flush();
        }
        catch (Exception ex)
        {
            Log.Write("Flush on exit: " + ex);
        }

        _monitor?.Dispose();

        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        try { _showEvent?.Set(); } catch { }
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            _ownsMutex = false;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _monitor?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        ExitApp();
        base.OnSessionEnding(e);
    }
}
