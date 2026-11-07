using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Media;

namespace Tide.Core;

/// <summary>Live and today's usage for one application.</summary>
public sealed class AppUsage : ObservableObject
{
    public const int GraphSeconds = 120;

    private double _rateDown, _rateUp;
    private long _todayDown, _todayUp;
    private bool _isBlocked, _isActive;
    private DateTime _lastActive;
    private ImageSource? _icon;
    private bool _iconLoaded;

    public AppUsage(AppIdentity identity) => Identity = identity;

    public AppIdentity Identity { get; }
    public string Key => Identity.Key;
    public string Name => Identity.Name;
    public string Path => Identity.Path;
    public bool CanBlock => Identity.IsBlockable;
    public RateSeries Series { get; } = new(GraphSeconds);

    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded) { _iconLoaded = true; _icon = IconCache.Get(Path); }
            return _icon;
        }
    }

    public double RateDown { get => _rateDown; set { if (Set(ref _rateDown, value)) OnPropertyChanged(nameof(RateTotal)); } }
    public double RateUp { get => _rateUp; set { if (Set(ref _rateUp, value)) OnPropertyChanged(nameof(RateTotal)); } }
    public double RateTotal => _rateDown + _rateUp;

    public long TodayDown { get => _todayDown; set { if (Set(ref _todayDown, value)) OnPropertyChanged(nameof(TodayTotal)); } }
    public long TodayUp { get => _todayUp; set { if (Set(ref _todayUp, value)) OnPropertyChanged(nameof(TodayTotal)); } }
    public long TodayTotal => _todayDown + _todayUp;

    public DateTime LastActive { get => _lastActive; set => Set(ref _lastActive, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public bool IsBlocked { get => _isBlocked; set => Set(ref _isBlocked, value); }

    /// <summary>Re-pushes IsBlocked to bindings (e.g. after the user cancels a toggle).</summary>
    public void NotifyBlocked() => OnPropertyChanged(nameof(IsBlocked));
}

public readonly record struct AppDelta(AppUsage App, long Down, long Up, bool Connected);

/// <summary>
/// Collects ETW traffic per process on the trace thread and turns it into per-app rates once a second
/// on the UI thread. Counters are lock-free so the hot path stays tiny.
/// </summary>
public sealed class TrafficTracker : INetworkSink
{
    private sealed class PidCounter
    {
        public PidCounter(AppIdentity app) => App = app;
        public readonly AppIdentity App;
        public long Sent;
        public long Recv;
        public int Connects;
        public long IdleTicks;
    }

    private readonly ConcurrentDictionary<uint, PidCounter> _pids = new();
    private readonly Dictionary<string, AppUsage> _apps = new();
    private readonly HistoryStore _history;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick;
    private DateTime _day = DateTime.Today;
    private long _todayDown, _todayUp;

    public TrafficTracker(HistoryStore history)
    {
        _history = history;
        foreach (var (key, t) in history.TodayTotals())
        {
            var app = new AppUsage(t.App) { TodayDown = t.Down, TodayUp = t.Up };
            _apps[key] = app;
            _todayDown += t.Down;
            _todayUp += t.Up;
        }
    }

    public RateSeries Total { get; } = new(AppUsage.GraphSeconds);
    public IEnumerable<AppUsage> Apps => _apps.Values;
    public double RateDown { get; private set; }
    public double RateUp { get; private set; }
    public long TodayDown => _todayDown;
    public long TodayUp => _todayUp;

    public event Action<AppUsage>? AppAdded;
    public event Action? DayChanged;

    // ---- ETW thread -------------------------------------------------------------------------

    public void OnTraffic(uint pid, uint bytes, bool sent)
    {
        var c = _pids.GetOrAdd(pid, static p => new PidCounter(ProcessResolver.Resolve(p)));
        if (sent) Interlocked.Add(ref c.Sent, bytes);
        else Interlocked.Add(ref c.Recv, bytes);
    }

    public void OnConnect(uint pid)
    {
        var c = _pids.GetOrAdd(pid, static p => new PidCounter(ProcessResolver.Resolve(p)));
        Interlocked.Increment(ref c.Connects);
    }

    // ---- UI thread --------------------------------------------------------------------------

    /// <summary>Folds the last interval's counters into per-app rates. Returns the apps that had activity.</summary>
    public List<AppDelta> Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double elapsed = Math.Max(0.05, now - _lastTick);
        _lastTick = now;
        var stamp = DateTime.Now;

        if (stamp.Date != _day)
        {
            _day = stamp.Date;
            _todayDown = _todayUp = 0;
            foreach (var a in _apps.Values) { a.TodayDown = 0; a.TodayUp = 0; }
            DayChanged?.Invoke();
        }

        // 1. Drain per-process counters, grouped by app.
        var perApp = new Dictionary<string, (AppIdentity App, long Down, long Up, bool Conn)>();
        foreach (var (pid, c) in _pids)
        {
            long up = Interlocked.Exchange(ref c.Sent, 0);
            long down = Interlocked.Exchange(ref c.Recv, 0);
            bool conn = Interlocked.Exchange(ref c.Connects, 0) > 0;

            if (up == 0 && down == 0 && !conn)
            {
                // Forget idle PIDs so a recycled PID is resolved again.
                if (++c.IdleTicks > 30) _pids.TryRemove(pid, out _);
                continue;
            }
            c.IdleTicks = 0;

            if (perApp.TryGetValue(c.App.Key, out var e))
                perApp[c.App.Key] = (e.App, e.Down + down, e.Up + up, e.Conn || conn);
            else
                perApp[c.App.Key] = (c.App, down, up, conn);
        }

        // 2. Ensure every active app has an entry.
        foreach (var (key, e) in perApp)
        {
            if (!_apps.ContainsKey(key))
            {
                var app = new AppUsage(e.App);
                _apps[key] = app;
                AppAdded?.Invoke(app);
            }
        }

        // 3. Update rates, graphs, totals and history.
        var deltas = new List<AppDelta>();
        long totalDown = 0, totalUp = 0;
        foreach (var app in _apps.Values)
        {
            if (perApp.TryGetValue(app.Key, out var e))
            {
                app.RateDown = e.Down / elapsed;
                app.RateUp = e.Up / elapsed;
                app.TodayDown += e.Down;
                app.TodayUp += e.Up;
                if (e.Down + e.Up > 0)
                {
                    app.LastActive = stamp;
                    _history.Add(app.Identity, stamp, e.Down, e.Up);
                }
                totalDown += e.Down;
                totalUp += e.Up;
                deltas.Add(new AppDelta(app, e.Down, e.Up, e.Conn));
            }
            else
            {
                app.RateDown = 0;
                app.RateUp = 0;
            }
            app.IsActive = (stamp - app.LastActive).TotalSeconds < 60;
            app.Series.Push(app.RateDown, app.RateUp);
        }

        _todayDown += totalDown;
        _todayUp += totalUp;
        RateDown = totalDown / elapsed;
        RateUp = totalUp / elapsed;
        Total.Push(RateDown, RateUp);
        return deltas;
    }

    public AppUsage GetOrAdd(AppIdentity id)
    {
        if (_apps.TryGetValue(id.Key, out var app)) return app;
        app = new AppUsage(id);
        _apps[id.Key] = app;
        AppAdded?.Invoke(app);
        return app;
    }
}
