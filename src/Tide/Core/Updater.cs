using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tide.Core;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string AssetUrl, long AssetSize, string? ChecksumUrl);

/// <summary>
/// Checks GitHub Releases for a newer Tide and swaps the running .exe in place.
/// Windows lets a running executable be renamed (not overwritten), so the update is:
/// download Tide.exe.new → rename Tide.exe to Tide.exe.old → move the new file in → restart.
/// The .old file is cleaned up on the next launch.
/// </summary>
public static class Updater
{
    public const string Repo = "larosa023/Tide";
    public static readonly string ReleasesPage = $"https://github.com/{Repo}/releases";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tide-Updater", CurrentVersion.ToString(3)));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static Version CurrentVersion { get; } = ReadVersion();

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary>"full" (self-contained) or "small" (needs the .NET runtime); decides which asset to download.</summary>
    public static string Flavor { get; } =
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "TideFlavor")?.Value ?? "full";

    private static string AssetName => Flavor == "small" ? "Tide-small.exe" : "Tide.exe";

    private static Version ReadVersion()
    {
        var info = typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info != null)
        {
            var core = info.Split('+', '-')[0];
            if (Version.TryParse(core, out var v)) return Normalize(v);
        }
        return Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static bool TryParseTag(string tag, out Version version)
    {
        var t = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (Version.TryParse(t, out var v)) { version = Normalize(v); return true; }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Returns the latest release if it's newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseTag(tag, out var version) || version <= CurrentVersion) return null;

        string? assetUrl = null, checksumUrl = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
            {
                assetUrl = url;
                size = asset.GetProperty("size").GetInt64();
            }
            else if (string.Equals(name, AssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = url;
            }
        }
        if (assetUrl == null) return null;

        return new UpdateInfo(version, tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage,
            assetUrl, size, checksumUrl);
    }

    /// <summary>Downloads and verifies the update, swaps the executable and restarts Tide.</summary>
    public static async Task InstallAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find Tide's own executable.");
        var newFile = exe + ".new";
        var oldFile = exe + ".old";

        // ---- download --------------------------------------------------------------------
        using (var resp = await Http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? update.AssetSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(newFile);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        // ---- verify ----------------------------------------------------------------------
        try
        {
            if (update.ChecksumUrl != null)
            {
                var expected = (await Http.GetStringAsync(update.ChecksumUrl, ct).ConfigureAwait(false))
                    .Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
                string actual;
                await using (var fs = File.OpenRead(newFile))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
                if (actual != expected)
                    throw new InvalidDataException("The downloaded update didn't match its checksum, so it wasn't installed.");
            }

            var fi = new FileInfo(newFile);
            if (fi.Length < 64 * 1024)
                throw new InvalidDataException("The downloaded update looks incomplete.");
        }
        catch
        {
            TryDelete(newFile);
            throw;
        }

        // ---- swap ------------------------------------------------------------------------
        TryDelete(oldFile);
        File.Move(exe, oldFile);
        try
        {
            File.Move(newFile, exe);
        }
        catch
        {
            File.Move(oldFile, exe); // roll back
            throw;
        }

        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    /// <summary>Removes leftovers from a previous update.</summary>
    public static void CleanUp()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        for (int i = 0; i < 10; i++)
        {
            if (TryDelete(exe + ".old")) break;
            Thread.Sleep(300);
        }
        TryDelete(exe + ".new");
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
