using System;
using System.Globalization;

namespace Tide.Core;

public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(double bytes)
    {
        if (bytes < 0) bytes = 0;
        int u = 0;
        while (bytes >= 1024 && u < Units.Length - 1)
        {
            bytes /= 1024;
            u++;
        }
        string num = u == 0 ? bytes.ToString("0", CultureInfo.CurrentCulture)
            : bytes >= 100 ? bytes.ToString("0", CultureInfo.CurrentCulture)
            : bytes >= 10 ? bytes.ToString("0.0", CultureInfo.CurrentCulture)
            : bytes.ToString("0.00", CultureInfo.CurrentCulture);
        return $"{num} {Units[u]}";
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 7) return t.ToString("ddd h:mm tt", CultureInfo.CurrentCulture);
        return t.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);
    }
}
