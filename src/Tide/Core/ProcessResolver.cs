using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tide.Core;

/// <summary>Identifies an application: one entry per executable path.</summary>
public sealed record AppIdentity(string Key, string Path, string Name)
{
    public bool IsBlockable => Path.Length > 0 && System.IO.Path.IsPathRooted(Path);
}

public static class ProcessResolver
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static readonly ConcurrentDictionary<string, AppIdentity> ByPath = new(StringComparer.OrdinalIgnoreCase);

    public static readonly AppIdentity SystemApp = new("system", "", "System");
    public static readonly AppIdentity UnknownApp = new("unknown", "", "Protected processes");

    /// <summary>Rebuilds an identity from a history key and stored name.</summary>
    public static AppIdentity FromStored(string key, string? path, string? name)
    {
        if (key == SystemApp.Key) return SystemApp;
        if (key == UnknownApp.Key) return UnknownApp;
        var p = string.IsNullOrEmpty(path) ? key : path;
        return ByPath.GetOrAdd(p, x => new AppIdentity(x.ToLowerInvariant(), x, string.IsNullOrEmpty(name) ? FileName(x) : name));
    }

    public static AppIdentity Resolve(uint pid)
    {
        if (pid == 0 || pid == 4) return SystemApp;

        string? path = QueryImagePath(pid);
        if (string.IsNullOrEmpty(path))
            return UnknownApp;

        return ForPath(path);
    }

    public static AppIdentity ForPath(string path)
    {
        return ByPath.GetOrAdd(path, p => new AppIdentity(p.ToLowerInvariant(), p, FriendlyName(p)));
    }

    private static string FriendlyName(string path)
    {
        string file = FileName(path);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var desc = info.FileDescription?.Trim();
            if (!string.IsNullOrEmpty(desc) && desc.Length <= 48 &&
                !desc.Equals("Application", StringComparison.OrdinalIgnoreCase))
                return desc;
            var product = info.ProductName?.Trim();
            if (!string.IsNullOrEmpty(product) && product.Length <= 48)
                return product;
        }
        catch
        {
        }
        return file;
    }

    private static string FileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static string? QueryImagePath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}

/// <summary>Caches small app icons as frozen WPF images.</summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return Cache.GetOrAdd(path, Load);
    }

    private static ImageSource? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
    }
}
