using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Tide.Core;

namespace Tide.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly TrafficTracker _tracker;
    private readonly HistoryStore _history;
    private readonly FirewallManager _firewall;
    private readonly AlertEngine _alerts;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _updateTimer;
    private int _ticks;
    private bool _alertsDirty;

    public MainViewModel(AppSettings settings, TrafficTracker tracker, HistoryStore history,
        FirewallManager firewall, AlertEngine alerts)
    {
        _settings = settings;
        _tracker = tracker;
        _history = history;
        _firewall = firewall;
        _alerts = alerts;

        _tracker.AppAdded += a => a.IsBlocked = _firewall.IsBlocked(a.Path);
        foreach (var a in _tracker.Apps) a.IsBlocked = _firewall.IsBlocked(a.Path);
        _firewall.Changed += OnFirewallChanged;
        _alerts.Raised += OnAlert;

        foreach (var r in Json.Load<List<AlertRecord>>(AppPaths.AlertsFile) ?? new List<AlertRecord>())
            Alerts.Add(new AlertItem(r, _firewall.IsBlocked(r.AppPath)));

        ToggleBlockCommand = new RelayCommand(p => { if (p is AppUsage a) ToggleBlock(a); });
        SelectAllCommand = new RelayCommand(() => SelectedApp = null);
        OpenLocationCommand = new RelayCommand(p => OpenLocation(p as string ?? (p as AppUsage)?.Path));
        CopyPathCommand = new RelayCommand(p => { var s = p as string ?? (p as AppUsage)?.Path; if (!string.IsNullOrEmpty(s)) Clipboard.SetText(s); });
        PrevPeriodCommand = new RelayCommand(() => HistoryOffset++);
        NextPeriodCommand = new RelayCommand(() => HistoryOffset--, _ => HistoryOffset > 0);
        UnblockCommand = new RelayCommand(p => { if (p is BlockedAppRow r) SetBlocked(r.App, false); });
        BlockFileCommand = new RelayCommand(BlockFile);
        OpenFirewallCommand = new RelayCommand(() => Shell("wf.msc"));
        RefreshFirewallCommand = new RelayCommand(() => _firewall.Refresh());
        DismissAlertCommand = new RelayCommand(p => { if (p is AlertItem a) { Alerts.Remove(a); _alertsDirty = true; } });
        ClearAlertsCommand = new RelayCommand(() => { Alerts.Clear(); _alertsDirty = true; }, _ => Alerts.Count > 0);
        BlockFromAlertCommand = new RelayCommand(p =>
        {
            if (p is not AlertItem a || !a.CanBlock) return;
            SetBlocked(ProcessResolver.ForPath(a.Path), !_firewall.IsBlocked(a.Path));
            a.IsBlocked = _firewall.IsBlocked(a.Path);
            a.NotifyBlocked();
        });
        ShowAppFromAlertCommand = new RelayCommand(p =>
        {
            if (p is not AlertItem a) return;
            var app = _tracker.Apps.FirstOrDefault(x => x.Key == a.Record.AppKey);
            if (app == null) return;
            SearchText = "";
            Page = "Live";
            SelectedApp = app;
        });
        CheckUpdatesCommand = new RelayCommand(async () => await CheckForUpdatesAsync(manual: true), _ => !IsCheckingUpdate && !IsUpdating);
        InstallUpdateCommand = new RelayCommand(async () => await InstallUpdateAsync(), _ => AvailableUpdate != null && !IsUpdating);
        DismissUpdateCommand = new RelayCommand(() => ShowUpdateBanner = false);
        OpenReleasesCommand = new RelayCommand(() => Shell(AvailableUpdate?.PageUrl ?? Updater.ReleasesPage));
        OpenDataFolderCommand = new RelayCommand(() => Shell(AppPaths.DataDir));
        SetThemeCommand = new RelayCommand(p => { if (p is string s) Theme = s; });

        RebuildBlocked();
        Resort();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(12);
            if (_settings.AutoCheckUpdates) await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
    }

    /// <summary>Asks the app shell to show a tray notification.</summary>
    public event Action<string, string>? NotificationRequested;
    public event Action<string>? ThemeRequested;
    public event Action? RestartRequested;

    // =========================================================================================
    // Navigation
    // =========================================================================================

    private string _page = "Live";
    public string Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            if (value == "History") RefreshHistory();
            if (value == "Alerts") UnreadAlerts = 0;
            if (value == "Firewall") _firewall.Refresh();
        }
    }

    // =========================================================================================
    // Live
    // =========================================================================================

    public ObservableCollection<AppUsage> VisibleApps { get; } = new();

    private AppUsage? _selectedApp;
    public AppUsage? SelectedApp
    {
        get => _selectedApp;
        set
        {
            if (!Set(ref _selectedApp, value)) return;
            OnPropertyChanged(nameof(GraphSeries));
            OnPropertyChanged(nameof(GraphTitle));
            OnPropertyChanged(nameof(GraphSubtitle));
            OnPropertyChanged(nameof(IsAppSelected));
        }
    }

    public bool IsAppSelected => _selectedApp != null;
    public RateSeries GraphSeries => _selectedApp?.Series ?? _tracker.Total;
    public string GraphTitle => _selectedApp?.Name ?? "All apps";
    public string GraphSubtitle => _selectedApp?.Path is { Length: > 0 } p ? p : "Last 2 minutes";

    private string _searchText = "";
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) Resort(); } }

    private string _sortMode = "Speed";
    public string SortMode { get => _sortMode; set { if (Set(ref _sortMode, value)) Resort(); } }

    public bool HideIdleApps
    {
        get => _settings.HideIdleApps;
        set { _settings.HideIdleApps = value; OnPropertyChanged(); SaveSettings(); Resort(); }
    }

    private double _rateDown, _rateUp;
    public double RateDown { get => _rateDown; private set => Set(ref _rateDown, value); }
    public double RateUp { get => _rateUp; private set => Set(ref _rateUp, value); }

    private long _todayDown, _todayUp;
    public long TodayDown { get => _todayDown; private set => Set(ref _todayDown, value); }
    public long TodayUp { get => _todayUp; private set => Set(ref _todayUp, value); }

    private int _activeCount;
    public int ActiveCount { get => _activeCount; private set => Set(ref _activeCount, value); }

    private string _monitorError = "";
    public string MonitorError { get => _monitorError; set => Set(ref _monitorError, value); }

    public ICommand ToggleBlockCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand OpenLocationCommand { get; }
    public ICommand CopyPathCommand { get; }

    private void Tick()
    {
        List<AppDelta> deltas;
        try
        {
            deltas = _tracker.Tick();
        }
        catch (Exception ex)
        {
            Log.Write("Tick: " + ex);
            return;
        }

        _alerts.Process(deltas);

        RateDown = _tracker.RateDown;
        RateUp = _tracker.RateUp;
        TodayDown = _tracker.TodayDown;
        TodayUp = _tracker.TodayUp;
        ActiveCount = _tracker.Apps.Count(a => a.IsActive);

        if (_page == "Live") Resort();
        _ticks++;
        if (_page == "History" && HistoryOffset == 0 && _ticks % 30 == 0) RefreshHistory();
        if (_ticks % 60 == 0) Flush();
    }

    private void Resort()
    {
        var q = _tracker.Apps.Where(a => a.TodayTotal > 0 || a.IsActive || a.IsBlocked);
        if (HideIdleApps) q = q.Where(a => a.IsActive || a == _selectedApp);
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var s = _searchText.Trim();
            q = q.Where(a => a.Name.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                             a.Path.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        var items = (_sortMode switch
        {
            "Today" => q.OrderByDescending(a => a.TodayTotal).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            "Name" => q.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => q.OrderByDescending(a => Math.Round(a.RateTotal / 512)).ThenByDescending(a => a.TodayTotal),
        }).ToList();

        // Move items in place rather than resetting, so selection and scroll position survive.
        for (int i = 0; i < items.Count; i++)
        {
            if (i < VisibleApps.Count && ReferenceEquals(VisibleApps[i], items[i])) continue;
            int existing = VisibleApps.IndexOf(items[i]);
            if (existing >= 0) VisibleApps.Move(existing, i);
            else VisibleApps.Insert(i, items[i]);
        }
        while (VisibleApps.Count > items.Count) VisibleApps.RemoveAt(VisibleApps.Count - 1);
    }

    private void ToggleBlock(AppUsage app)
    {
        SetBlocked(app.Identity, !_firewall.IsBlocked(app.Path));
        app.NotifyBlocked();
    }

    private void SetBlocked(AppIdentity app, bool block)
    {
        if (!app.IsBlockable) return;

        if (block)
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            bool system = app.Path.StartsWith(win, StringComparison.OrdinalIgnoreCase);
            bool self = string.Equals(app.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
            string msg = self
                ? "Blocking Tide stops it from checking for updates. Block it anyway?"
                : system
                    ? $"{app.Name} is part of Windows. Blocking it can break Windows Update, DNS, sign-in or other " +
                      $"services that share this program.\n\n{app.Path}\n\nBlock it anyway?"
                    : $"Block {app.Name} from the internet?\n\n{app.Path}\n\nThis adds inbound and outbound Windows Firewall rules. " +
                      "Connections it already has open may keep working until they close.";
            var r = MessageBox.Show(msg, "Tide", MessageBoxButton.YesNo,
                system || self ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
        }

        bool ok = block ? _firewall.Block(app.Path) : _firewall.Unblock(app.Path);
        if (!ok)
            MessageBox.Show($"Windows Firewall refused the change:\n\n{_firewall.LastError}", "Tide",
                MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnFirewallChanged()
    {
        void Apply()
        {
            foreach (var a in _tracker.Apps) a.IsBlocked = _firewall.IsBlocked(a.Path);
            foreach (var al in Alerts) al.IsBlocked = _firewall.IsBlocked(al.Path);
            RebuildBlocked();
            Resort();
        }

        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Apply();
        else d.BeginInvoke(Apply);
    }

    private static void OpenLocation(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Open {target}: {ex.Message}"); }
    }

    // =========================================================================================
    // History
    // =========================================================================================

    private HistoryRange _historyRange = HistoryRange.Day;
    public HistoryRange HistoryRange
    {
        get => _historyRange;
        set { if (Set(ref _historyRange, value)) { _historyOffset = 0; OnPropertyChanged(nameof(HistoryOffset)); RefreshHistory(); } }
    }

    private int _historyOffset;
    public int HistoryOffset
    {
        get => _historyOffset;
        set { if (Set(ref _historyOffset, Math.Max(0, value))) RefreshHistory(); }
    }

    private string _historyTitle = "";
    public string HistoryTitle { get => _historyTitle; private set => Set(ref _historyTitle, value); }

    private IReadOnlyList<UsageBucket>? _historyBuckets;
    public IReadOnlyList<UsageBucket>? HistoryBuckets { get => _historyBuckets; private set => Set(ref _historyBuckets, value); }

    private long _historyDown, _historyUp;
    public long HistoryDown { get => _historyDown; private set => Set(ref _historyDown, value); }
    public long HistoryUp { get => _historyUp; private set => Set(ref _historyUp, value); }

    public ObservableCollection<HistoryAppRow> HistoryApps { get; } = new();

    public ICommand PrevPeriodCommand { get; }
    public ICommand NextPeriodCommand { get; }

    private void RefreshHistory()
    {
        var report = _history.Query(_historyRange, _historyOffset);
        HistoryTitle = report.Title;
        HistoryBuckets = report.Buckets;
        HistoryDown = report.Down;
        HistoryUp = report.Up;
        HistoryApps.Clear();
        long max = report.Apps.Count > 0 ? report.Apps[0].Total : 0;
        foreach (var a in report.Apps.Take(200)) HistoryApps.Add(new HistoryAppRow(a, max));
    }

    // =========================================================================================
    // Firewall
    // =========================================================================================

    public ObservableCollection<BlockedAppRow> BlockedApps { get; } = new();

    public string FirewallError => _firewall.LastError ?? "";

    public ICommand UnblockCommand { get; }
    public ICommand BlockFileCommand { get; }
    public ICommand OpenFirewallCommand { get; }
    public ICommand RefreshFirewallCommand { get; }

    private void RebuildBlocked()
    {
        BlockedApps.Clear();
        foreach (var p in _firewall.BlockedPaths.OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
            BlockedApps.Add(new BlockedAppRow(p));
        OnPropertyChanged(nameof(FirewallError));
    }

    private void BlockFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose an app to block",
            Filter = "Programs (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog() == true)
            SetBlocked(ProcessResolver.ForPath(dlg.FileName), true);
    }

    // =========================================================================================
    // Alerts
    // =========================================================================================

    public ObservableCollection<AlertItem> Alerts { get; } = new();

    private int _unreadAlerts;
    public int UnreadAlerts { get => _unreadAlerts; set => Set(ref _unreadAlerts, value); }

    public bool IsLearning => _alerts.IsLearning;

    public ICommand DismissAlertCommand { get; }
    public ICommand ClearAlertsCommand { get; }
    public ICommand BlockFromAlertCommand { get; }
    public ICommand ShowAppFromAlertCommand { get; }

    private void OnAlert(AlertRecord r)
    {
        Alerts.Insert(0, new AlertItem(r, _firewall.IsBlocked(r.AppPath)));
        while (Alerts.Count > 200) Alerts.RemoveAt(Alerts.Count - 1);
        _alertsDirty = true;
        if (_page != "Alerts") UnreadAlerts++;
        NotificationRequested?.Invoke(r.Title, r.Message);
    }

    // =========================================================================================
    // Settings
    // =========================================================================================

    public IReadOnlyList<ThemeOption> Themes { get; } = new[]
    {
        new ThemeOption("Tide", "#0B1620", "#16293A", "#2EC4B6", "#F4A261"),
        new ThemeOption("Dark", "#111113", "#212125", "#5CC8FF", "#FF8F6B"),
        new ThemeOption("Dusk", "#191724", "#26233A", "#9CCFD8", "#F6C177"),
        new ThemeOption("Light", "#F3F6F8", "#FFFFFF", "#0E8C82", "#D9731F"),
    };

    public ICommand SetThemeCommand { get; }

    public string Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value) return;
            _settings.Theme = value;
            OnPropertyChanged();
            ThemeRequested?.Invoke(value);
            SaveSettings();
        }
    }

    public bool AlertNewApps { get => _settings.AlertNewApps; set { _settings.AlertNewApps = value; OnPropertyChanged(); SaveSettings(); } }
    public bool AlertSpikes { get => _settings.AlertSpikes; set { _settings.AlertSpikes = value; OnPropertyChanged(); SaveSettings(); } }

    public double SpikeThresholdMB
    {
        get => _settings.SpikeThresholdMB;
        set { _settings.SpikeThresholdMB = Math.Clamp(value, 1, 100_000); OnPropertyChanged(); SaveSettings(); }
    }

    public bool CloseToTray { get => _settings.CloseToTray; set { _settings.CloseToTray = value; OnPropertyChanged(); SaveSettings(); } }
    public bool StartMinimized { get => _settings.StartMinimized; set { _settings.StartMinimized = value; OnPropertyChanged(); SaveSettings(); } }
    public bool AutoCheckUpdates { get => _settings.AutoCheckUpdates; set { _settings.AutoCheckUpdates = value; OnPropertyChanged(); SaveSettings(); } }

    public bool IgnoreLoopback
    {
        get => _settings.IgnoreLoopback;
        set { _settings.IgnoreLoopback = value; OnPropertyChanged(); SaveSettings(); LoopbackChanged?.Invoke(value); }
    }

    public event Action<bool>? LoopbackChanged;

    public int HistoryRetentionDays
    {
        get => _settings.HistoryRetentionDays;
        set { _settings.HistoryRetentionDays = Math.Clamp(value, 7, 3650); OnPropertyChanged(); SaveSettings(); }
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (StartupManager.SetEnabled(value, out var err))
            {
                _settings.StartWithWindows = value;
                SaveSettings();
            }
            else
            {
                MessageBox.Show($"Couldn't change the startup task:\n\n{err}", "Tide", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            OnPropertyChanged();
        }
    }

    public string VersionText => $"Version {Updater.CurrentVersionText}";
    public ICommand OpenDataFolderCommand { get; }

    private void SaveSettings() => SettingsStore.Save(_settings);

    // =========================================================================================
    // Updates
    // =========================================================================================

    private UpdateInfo? _availableUpdate;
    public UpdateInfo? AvailableUpdate
    {
        get => _availableUpdate;
        private set { if (Set(ref _availableUpdate, value)) OnPropertyChanged(nameof(UpdateBannerText)); }
    }

    private bool _showUpdateBanner;
    public bool ShowUpdateBanner { get => _showUpdateBanner; set => Set(ref _showUpdateBanner, value); }

    public string UpdateBannerText => _availableUpdate == null ? "" : $"Tide {_availableUpdate.Version.ToString(3)} is available";

    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }

    private bool _isCheckingUpdate, _isUpdating;
    public bool IsCheckingUpdate { get => _isCheckingUpdate; private set => Set(ref _isCheckingUpdate, value); }
    public bool IsUpdating { get => _isUpdating; private set => Set(ref _isUpdating, value); }

    private double _updateProgress;
    public double UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }

    public ICommand CheckUpdatesCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand DismissUpdateCommand { get; }
    public ICommand OpenReleasesCommand { get; }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (IsCheckingUpdate || IsUpdating) return;
        IsCheckingUpdate = true;
        UpdateStatus = "Checking for updates…";
        try
        {
            var update = await Updater.CheckAsync();
            AvailableUpdate = update;
            if (update == null)
            {
                UpdateStatus = $"You're up to date · checked {DateTime.Now:t}";
            }
            else
            {
                UpdateStatus = $"Version {update.Version.ToString(3)} is ready to install.";
                ShowUpdateBanner = true;
                if (!manual && _settings.SkippedUpdateVersion != update.Tag)
                {
                    _settings.SkippedUpdateVersion = update.Tag; // notify once per version
                    SaveSettings();
                    NotificationRequested?.Invoke("Tide update available",
                        $"Version {update.Version.ToString(3)} is ready. Open Tide to install it.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Update check: " + ex.Message);
            UpdateStatus = manual ? $"Couldn't check for updates: {ex.Message}" : "Couldn't reach GitHub to check for updates.";
        }
        finally
        {
            IsCheckingUpdate = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task InstallUpdateAsync()
    {
        var update = AvailableUpdate;
        if (update == null || IsUpdating) return;
        IsUpdating = true;
        UpdateProgress = 0;
        UpdateStatus = $"Downloading Tide {update.Version.ToString(3)}…";
        try
        {
            Flush();
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p;
                UpdateStatus = $"Downloading Tide {update.Version.ToString(3)}… {p:P0}";
            });
            await Updater.InstallAsync(update, progress);
            UpdateStatus = "Restarting…";
            RestartRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write("Update install: " + ex);
            UpdateStatus = $"Update failed: {ex.Message}";
            IsUpdating = false;
            MessageBox.Show($"The update couldn't be installed.\n\n{ex.Message}\n\nYou can download it manually from GitHub.",
                "Tide", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // =========================================================================================

    /// <summary>Writes history, known apps and alerts to disk.</summary>
    public void Flush()
    {
        _history.Save();
        _alerts.Save();
        if (_alertsDirty)
        {
            Json.Save(AppPaths.AlertsFile, Alerts.Select(a => a.Record).ToList());
            _alertsDirty = false;
        }
        if (_ticks % 3600 == 0) HistoryStore.Prune(_settings.HistoryRetentionDays);
    }

    public void Stop()
    {
        _timer.Stop();
        _updateTimer.Stop();
    }
}
