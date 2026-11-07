using System;
using System.Collections.Generic;
using System.Linq;

namespace Tide.Core;

public enum AlertKind { NewApp, Spike }

public sealed class AlertRecord
{
    public AlertKind Kind { get; set; }
    public DateTime Time { get; set; }
    public string AppKey { get; set; } = "";
    public string AppName { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>
/// Raises alerts for apps that touch the network for the first time and for apps that suddenly move
/// a lot of data (more than the threshold within a rolling one-minute window).
/// </summary>
public sealed class AlertEngine
{
    private const int WindowSeconds = 60;
    private static readonly TimeSpan SpikeCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LearningPeriod = TimeSpan.FromMinutes(2);

    private readonly AppSettings _settings;
    private readonly HashSet<string> _known;
    private readonly Dictionary<string, Queue<(DateTime T, long Bytes)>> _windows = new();
    private readonly Dictionary<string, long> _windowSums = new();
    private readonly Dictionary<string, DateTime> _lastSpike = new();
    private readonly DateTime _learnUntil;
    private bool _knownDirty;

    public AlertEngine(AppSettings settings)
    {
        _settings = settings;
        var stored = Json.Load<List<string>>(AppPaths.KnownAppsFile);
        _known = new HashSet<string>(stored ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        // On the very first run every app is "new"; learn quietly for a couple of minutes instead of
        // flooding the user with notifications.
        _learnUntil = stored == null ? DateTime.Now + LearningPeriod : DateTime.MinValue;
    }

    public bool IsLearning => DateTime.Now < _learnUntil;

    public event Action<AlertRecord>? Raised;

    public void Process(IReadOnlyList<AppDelta> deltas)
    {
        var now = DateTime.Now;

        foreach (var d in deltas)
        {
            var app = d.App;

            // ---- first connection --------------------------------------------------------
            if (_known.Add(app.Key))
            {
                _knownDirty = true;
                if (_settings.AlertNewApps && !IsLearning && app.CanBlock)
                {
                    Raise(new AlertRecord
                    {
                        Kind = AlertKind.NewApp,
                        Time = now,
                        AppKey = app.Key,
                        AppName = app.Name,
                        AppPath = app.Path,
                        Title = $"{app.Name} connected to the internet",
                        Message = $"First time Tide has seen this app use the network.\n{app.Path}",
                    });
                }
            }

            // ---- spikes ------------------------------------------------------------------
            long bytes = d.Down + d.Up;
            if (bytes <= 0) continue;
            if (!_windows.TryGetValue(app.Key, out var q))
            {
                q = new Queue<(DateTime, long)>();
                _windows[app.Key] = q;
                _windowSums[app.Key] = 0;
            }
            q.Enqueue((now, bytes));
            _windowSums[app.Key] += bytes;
        }

        // Expire old samples and check thresholds.
        long threshold = (long)(_settings.SpikeThresholdMB * 1024 * 1024);
        foreach (var key in _windows.Keys.ToList())
        {
            var q = _windows[key];
            while (q.Count > 0 && (now - q.Peek().T).TotalSeconds > WindowSeconds)
                _windowSums[key] -= q.Dequeue().Bytes;
            if (q.Count == 0)
            {
                _windows.Remove(key);
                _windowSums.Remove(key);
                continue;
            }

            if (!_settings.AlertSpikes || threshold <= 0 || _windowSums[key] < threshold) continue;
            if (_lastSpike.TryGetValue(key, out var last) && now - last < SpikeCooldown) continue;

            var app = deltas.FirstOrDefault(x => x.App.Key == key).App;
            if (app == null) continue;
            _lastSpike[key] = now;
            Raise(new AlertRecord
            {
                Kind = AlertKind.Spike,
                Time = now,
                AppKey = key,
                AppName = app.Name,
                AppPath = app.Path,
                Title = $"{app.Name} is using a lot of data",
                Message = $"{Format.Bytes(_windowSums[key])} in the last minute.",
            });
        }
    }

    private void Raise(AlertRecord alert) => Raised?.Invoke(alert);

    public void Save()
    {
        if (!_knownDirty) return;
        Json.Save(AppPaths.KnownAppsFile, _known.OrderBy(x => x).ToList());
        _knownDirty = false;
    }
}
