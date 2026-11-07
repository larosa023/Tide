using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Tide.Core;

public sealed class AppDay
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public long[] Down { get; set; } = new long[24];
    public long[] Up { get; set; } = new long[24];

    public long TotalDown => Down.Sum();
    public long TotalUp => Up.Sum();
}

public sealed class DayRecord
{
    public string Date { get; set; } = "";
    public Dictionary<string, AppDay> Apps { get; set; } = new();
}

public sealed record UsageBucket(string Label, string Detail, long Down, long Up);

public sealed record AppTotal(AppIdentity App, long Down, long Up)
{
    public long Total => Down + Up;
}

public sealed record HistoryReport(string Title, IReadOnlyList<UsageBucket> Buckets, IReadOnlyList<AppTotal> Apps, long Down, long Up);

public enum HistoryRange { Day, Week, Month }

/// <summary>
/// Hourly per-app usage, one small JSON file per day under %LocalAppData%\Tide\History.
/// Only today's file is kept in memory and is flushed once a minute.
/// </summary>
public sealed class HistoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DayRecord> _cache = new();
    private DayRecord _today;
    private bool _dirty;

    public HistoryStore()
    {
        _today = LoadDay(DateTime.Today) ?? new DayRecord { Date = Key(DateTime.Today) };
    }

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string FileFor(DateTime d) => System.IO.Path.Combine(AppPaths.HistoryDir, Key(d) + ".json");

    public void Add(AppIdentity app, DateTime when, long down, long up)
    {
        if (down == 0 && up == 0) return;
        lock (_gate)
        {
            if (_today.Date != Key(when.Date))
            {
                SaveLocked();
                _today = LoadDay(when.Date) ?? new DayRecord { Date = Key(when.Date) };
            }

            if (!_today.Apps.TryGetValue(app.Key, out var day))
            {
                day = new AppDay { Name = app.Name, Path = app.Path };
                _today.Apps[app.Key] = day;
            }
            day.Down[when.Hour] += down;
            day.Up[when.Hour] += up;
            _dirty = true;
        }
    }

    /// <summary>Today's per-app totals, used to restore the "today" counters on startup.</summary>
    public Dictionary<string, (AppIdentity App, long Down, long Up)> TodayTotals()
    {
        lock (_gate)
        {
            if (_today.Date != Key(DateTime.Today)) return new();
            return _today.Apps.ToDictionary(
                kv => kv.Key,
                kv => (ProcessResolver.FromStored(kv.Key, kv.Value.Path, kv.Value.Name), kv.Value.TotalDown, kv.Value.TotalUp));
        }
    }

    public void Save()
    {
        lock (_gate) SaveLocked();
    }

    private void SaveLocked()
    {
        if (!_dirty) return;
        Json.Save(FileFor(DateTime.ParseExact(_today.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)), _today);
        _dirty = false;
    }

    private DayRecord? LoadDay(DateTime date) => Json.Load<DayRecord>(FileFor(date));

    private DayRecord? GetDay(DateTime date)
    {
        var key = Key(date);
        if (_today.Date == key) return _today;
        if (_cache.TryGetValue(key, out var rec)) return rec;
        rec = LoadDay(date);
        if (rec != null && date < DateTime.Today) _cache[key] = rec;
        if (_cache.Count > 120) _cache.Clear();
        return rec;
    }

    public HistoryReport Query(HistoryRange range, int offset)
    {
        lock (_gate)
        {
            var today = DateTime.Today;
            var buckets = new List<UsageBucket>();
            var totals = new Dictionary<string, (AppIdentity App, long Down, long Up)>();
            string title;

            void Accumulate(string key, AppDay d, long down, long up)
            {
                if (down == 0 && up == 0) return;
                if (totals.TryGetValue(key, out var t))
                    totals[key] = (t.App, t.Down + down, t.Up + up);
                else
                    totals[key] = (ProcessResolver.FromStored(key, d.Path, d.Name), down, up);
            }

            if (range == HistoryRange.Day)
            {
                var date = today.AddDays(-offset);
                title = offset == 0 ? "Today" : offset == 1 ? "Yesterday" : date.ToString("dddd, MMM d", CultureInfo.CurrentCulture);
                var rec = GetDay(date);
                for (int h = 0; h < 24; h++)
                {
                    long down = 0, up = 0;
                    if (rec != null)
                        foreach (var a in rec.Apps.Values) { down += a.Down[h]; up += a.Up[h]; }
                    var label = DateTime.Today.AddHours(h).ToString("htt", CultureInfo.InvariantCulture).ToLowerInvariant();
                    buckets.Add(new UsageBucket(label, $"{label} – {DateTime.Today.AddHours(h + 1).ToString("htt", CultureInfo.InvariantCulture).ToLowerInvariant()}", down, up));
                }
                if (rec != null)
                    foreach (var (k, a) in rec.Apps) Accumulate(k, a, a.TotalDown, a.TotalUp);
            }
            else
            {
                int days = range == HistoryRange.Week ? 7 : 30;
                var end = today.AddDays(-offset * days);
                var start = end.AddDays(-(days - 1));
                title = offset == 0
                    ? (range == HistoryRange.Week ? "Last 7 days" : "Last 30 days")
                    : $"{start:MMM d} – {end:MMM d}";
                for (var d = start; d <= end; d = d.AddDays(1))
                {
                    long down = 0, up = 0;
                    var rec = GetDay(d);
                    if (rec != null)
                        foreach (var (k, a) in rec.Apps)
                        {
                            long ad = a.TotalDown, au = a.TotalUp;
                            down += ad; up += au;
                            Accumulate(k, a, ad, au);
                        }
                    var label = range == HistoryRange.Week
                        ? d.ToString("ddd", CultureInfo.CurrentCulture)
                        : d.ToString("%d", CultureInfo.CurrentCulture);
                    buckets.Add(new UsageBucket(label, d.ToString("ddd, MMM d", CultureInfo.CurrentCulture), down, up));
                }
            }

            var apps = totals.Values
                .Select(t => new AppTotal(t.App, t.Down, t.Up))
                .OrderByDescending(t => t.Total)
                .ToList();
            return new HistoryReport(title, buckets, apps, apps.Sum(a => a.Down), apps.Sum(a => a.Up));
        }
    }

    public static void Prune(int retentionDays)
    {
        try
        {
            if (retentionDays <= 0) return;
            var cutoff = DateTime.Today.AddDays(-retentionDays);
            foreach (var f in Directory.EnumerateFiles(AppPaths.HistoryDir, "*.json"))
            {
                if (DateTime.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < cutoff)
                    File.Delete(f);
            }
        }
        catch (Exception ex)
        {
            Log.Write("History prune: " + ex.Message);
        }
    }
}
