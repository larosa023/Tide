using System;
using System.Windows.Media;
using Tide.Core;

namespace Tide.ViewModels;

public sealed class HistoryAppRow
{
    public HistoryAppRow(AppTotal t, long max)
    {
        App = t.App;
        Down = t.Down;
        Up = t.Up;
        Share = max > 0 ? (double)t.Total / max : 0;
    }

    public AppIdentity App { get; }
    public string Name => App.Name;
    public string Path => App.Path;
    public ImageSource? Icon => IconCache.Get(App.Path);
    public long Down { get; }
    public long Up { get; }
    public long Total => Down + Up;
    public double Share { get; }
}

public sealed class BlockedAppRow
{
    public BlockedAppRow(string path)
    {
        App = ProcessResolver.ForPath(path);
    }

    public AppIdentity App { get; }
    public string Name => App.Name;
    public string Path => App.Path;
    public ImageSource? Icon => IconCache.Get(App.Path);
    public bool Exists => System.IO.File.Exists(App.Path);
}

public sealed class AlertItem : ObservableObject
{
    private bool _isBlocked;

    public AlertItem(AlertRecord record, bool blocked)
    {
        Record = record;
        _isBlocked = blocked;
    }

    public AlertRecord Record { get; }
    public AlertKind Kind => Record.Kind;
    public DateTime Time => Record.Time;
    public string Title => Record.Title;
    public string Message => Record.Message;
    public string Path => Record.AppPath;
    public ImageSource? Icon => IconCache.Get(Record.AppPath);
    public string Glyph => Kind == AlertKind.Spike ? "" : "";
    public bool CanBlock => !string.IsNullOrEmpty(Record.AppPath);
    public bool IsBlocked { get => _isBlocked; set => Set(ref _isBlocked, value); }

    public void NotifyBlocked() => OnPropertyChanged(nameof(IsBlocked));
}

public sealed record ThemeOption(string Name, string Bg, string Surface, string Accent, string Up);
