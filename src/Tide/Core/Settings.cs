using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tide.Core;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tide");

    public static string HistoryDir => Path.Combine(DataDir, "History");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string KnownAppsFile => Path.Combine(DataDir, "known-apps.json");
    public static string BlockedFile => Path.Combine(DataDir, "blocked.json");
    public static string AlertsFile => Path.Combine(DataDir, "alerts.json");
    public static string LogFile => Path.Combine(DataDir, "tide.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(HistoryDir);
    }
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Tide";
    public bool AlertNewApps { get; set; } = true;
    public bool AlertSpikes { get; set; } = true;
    /// <summary>An app moving more than this much data within one minute triggers a spike alert.</summary>
    public double SpikeThresholdMB { get; set; } = 250;
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    public bool IgnoreLoopback { get; set; } = true;
    public bool HideIdleApps { get; set; }
    public int HistoryRetentionDays { get; set; } = 365;
    public string? SkippedUpdateVersion { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(fs, Options);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes to a temp file and swaps it in, so a crash never leaves a half-written file.</summary>
    public static void Save<T>(string path, T value)
    {
        try
        {
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
                JsonSerializer.Serialize(fs, value, Options);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not write {path}: {ex.Message}");
        }
    }
}

public static class SettingsStore
{
    public static AppSettings Load() => Json.Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
    public static void Save(AppSettings s) => Json.Save(AppPaths.SettingsFile, s);
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 512 * 1024)
                    File.Move(AppPaths.LogFile, AppPaths.LogFile + ".1", overwrite: true);
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
